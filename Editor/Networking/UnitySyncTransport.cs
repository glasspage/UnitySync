using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Glasspage.UnitySync
{
    internal enum UnitySyncTransportEventKind
    {
        Connected,
        Disconnected,
        Viewport,
        Selection,
        AssetImportState,
        BuildTarget,
        FileSync,
        PeerLeft,
        SceneObjectChange,
        SceneSnapshotRequest,
        SceneSnapshotBegin,
        SceneSnapshotEnd,
        SceneSettingsChange,
        Log,
        Error
    }

    internal readonly struct UnitySyncTransportEvent
    {
        internal readonly UnitySyncTransportEventKind Kind;
        internal readonly UnitySyncViewportState Viewport;
        internal readonly UnitySyncSelectionState Selection;
        internal readonly bool IsImportingAssets;
        internal readonly string BuildTargetName;
        internal readonly UnitySyncMessageType MessageType;
        internal readonly UnitySyncFileSyncMessage FileSync;
        internal readonly UnitySyncSceneObjectChange SceneChange;
        internal readonly UnitySyncSceneSnapshotBoundary SceneSnapshot;
        internal readonly Guid PlayerId;
        internal readonly string Message;
        internal readonly double ReceivedAtSeconds;

        internal UnitySyncTransportEvent(
            UnitySyncTransportEventKind kind,
            UnitySyncViewportState viewport,
            UnitySyncSceneObjectChange sceneChange,
            Guid playerId,
            string message,
            UnitySyncSceneSnapshotBoundary sceneSnapshot = null,
            UnitySyncSelectionState selection = default,
            UnitySyncMessageType messageType = default(UnitySyncMessageType),
            UnitySyncFileSyncMessage fileSync = null,
            double receivedAtSeconds = 0d,
            bool isImportingAssets = false,
            string buildTargetName = "")
        {
            Kind = kind;
            Viewport = viewport;
            Selection = selection;
            IsImportingAssets = isImportingAssets;
            BuildTargetName = buildTargetName ?? string.Empty;
            MessageType = messageType;
            FileSync = fileSync;
            SceneChange = sceneChange;
            SceneSnapshot = sceneSnapshot;
            PlayerId = playerId;
            Message = message;
            ReceivedAtSeconds = receivedAtSeconds;
        }
    }

    internal sealed class UnitySyncTransport : IDisposable
    {
        private sealed class OutboundMessage
        {
            internal byte[] Payload;
            internal Guid TargetPlayerId;
            internal string CoalesceKey = string.Empty;
        }

        private sealed class Peer
        {
            internal readonly TcpClient Client;
            internal readonly object SendLock = new object();
            internal NetworkStream Stream;
            internal Guid PlayerId;
            internal string DisplayName;
            internal bool RequestedSceneSnapshot;
            internal bool RequestedFileSync;
            internal bool Superseded;

            internal Peer(TcpClient client)
            {
                Client = client;
                PlayerId = Guid.Empty;
                DisplayName = string.Empty;
            }

            internal void Close()
            {
                try
                {
                    Stream?.Close();
                }
                catch
                {
                    // Best-effort shutdown.
                }

                try
                {
                    Client.Close();
                }
                catch
                {
                    // Best-effort shutdown.
                }
            }
        }

        private readonly Guid _localPlayerId;
        private readonly string _localDisplayName;
        private readonly string _localUnityVersion;
        private readonly UnitySyncCrypto _crypto;
        private readonly object _cryptoLock = new object();
        private readonly object _peersLock = new object();
        private readonly List<Peer> _peers = new List<Peer>();
        private readonly object _eventsLock = new object();
        private readonly LinkedList<UnitySyncTransportEvent> _events =
            new LinkedList<UnitySyncTransportEvent>();
        private readonly Dictionary<string, LinkedListNode<UnitySyncTransportEvent>>
            _pendingIncomingLiveSceneEvents =
                new Dictionary<string, LinkedListNode<UnitySyncTransportEvent>>(StringComparer.Ordinal);
        private readonly object _outboundLock = new object();
        private readonly AutoResetEvent _outboundSignal = new AutoResetEvent(false);
        private readonly LinkedList<OutboundMessage> _pendingMessages =
            new LinkedList<OutboundMessage>();
        private readonly Dictionary<string, LinkedListNode<OutboundMessage>>
            _pendingOutgoingLiveSceneMessages =
                new Dictionary<string, LinkedListNode<OutboundMessage>>(StringComparer.Ordinal);
        private long _pendingMessageBytes;
        private long _totalBytesSent;
        private long _totalBytesReceived;

        private volatile bool _running;
        private volatile bool _clientReady;
        private bool _isHost;
        private TcpListener _listener;
        private Peer _serverPeer;
        private byte[] _pendingLocalViewport;
        private byte[] _pendingLocalSelection;
        private string _localBuildTargetName = string.Empty;

        internal UnitySyncTransport(Guid localPlayerId, string localDisplayName, byte[] secret)
        {
            _localPlayerId = localPlayerId;
            _localDisplayName = NormalizeDisplayName(localDisplayName);
            _localUnityVersion = UnityEngine.Application.unityVersion ?? string.Empty;
            _crypto = new UnitySyncCrypto(secret);
        }

        internal bool IsRunning => _running;
        internal bool IsClientReady => _clientReady;
        internal long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
        internal long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
        internal int PendingIncomingEventCount
        {
            get
            {
                lock (_eventsLock)
                {
                    return _events.Count;
                }
            }
        }
        internal long PendingOutboundBytes
        {
            get
            {
                lock (_outboundLock)
                {
                    return _pendingMessageBytes;
                }
            }
        }

        internal void StartHost(int port)
        {
            if (_running)
            {
                throw new InvalidOperationException("UnitySync transport is already running.");
            }

            _isHost = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;

            Thread acceptThread = new Thread(HostAcceptLoop)
            {
                IsBackground = true,
                Name = "UnitySync Host"
            };
            acceptThread.Start();
            StartOutboundThread();
        }

        internal void StartClient(IPAddress address, int port)
        {
            if (_running)
            {
                throw new InvalidOperationException("UnitySync transport is already running.");
            }

            _isHost = false;
            _running = true;

            Thread clientThread = new Thread(() => ClientLoop(address, port))
            {
                IsBackground = true,
                Name = "UnitySync Client"
            };
            clientThread.Start();
            StartOutboundThread();
        }

        internal void SendLocalViewport(UnitySyncViewportState viewport)
        {
            if (!_running)
            {
                return;
            }

            byte[] payload = UnitySyncProtocol.CreateViewport(viewport);
            lock (_outboundLock)
            {
                _pendingLocalViewport = payload;
            }

            _outboundSignal.Set();
        }

        internal void SendLocalSelection(UnitySyncSelectionState selection)
        {
            if (!_running)
            {
                return;
            }

            byte[] payload = UnitySyncProtocol.CreateSelection(selection);
            lock (_outboundLock)
            {
                _pendingLocalSelection = payload;
            }

            _outboundSignal.Set();
        }

        internal void SendAssetImportState(bool isImportingAssets)
        {
            QueueMessage(
                UnitySyncProtocol.CreateAssetImportState(
                    _localPlayerId,
                    isImportingAssets),
                Guid.Empty);
        }

        internal void SetLocalBuildTarget(string buildTargetName)
        {
            _localBuildTargetName = buildTargetName ?? string.Empty;
        }

        internal void SendBuildTarget(string buildTargetName)
        {
            if (!_running || string.IsNullOrEmpty(buildTargetName))
            {
                return;
            }

            _localBuildTargetName = buildTargetName;
            byte[] payload =
                UnitySyncProtocol.CreateBuildTarget(_localPlayerId, buildTargetName);

            // A platform change can reload editor assemblies immediately after the
            // active-target callback returns. Send this tiny control packet directly so
            // it cannot be stranded in the normal asynchronous outbound queue.
            if (_isHost)
            {
                Broadcast(payload, null);
                return;
            }

            Peer server = _serverPeer;
            if (_clientReady && server != null)
            {
                TrySend(server, payload);
                return;
            }

            QueueMessage(payload, Guid.Empty);
        }

        internal void SendRestoreProjectControl(UnitySyncMessageType type, Guid targetPlayerId)
        {
            QueueMessage(UnitySyncProtocol.CreateRestoreProjectControl(_localPlayerId, type), targetPlayerId);
        }

        internal void RequestFileSync(UnitySyncFileSyncScope scope)
        {
            if (scope != UnitySyncFileSyncScope.Packages &&
                scope != UnitySyncFileSyncScope.Assets)
            {
                throw new ArgumentOutOfRangeException(nameof(scope));
            }

            QueueMessage(
                UnitySyncProtocol.CreateFileSyncRequest(_localPlayerId, scope),
                Guid.Empty);
        }

        internal void RequestFile(Guid syncId, string path)
        {
            QueueMessage(
                UnitySyncProtocol.CreateFileRequest(_localPlayerId, syncId, path),
                Guid.Empty);
        }

        internal void SendFileManifestBegin(
            Guid playerId,
            UnitySyncFileSyncMessage state,
            Guid targetPlayerId)
        {
            QueueMessage(
                UnitySyncProtocol.CreateFileManifestBegin(playerId, state),
                targetPlayerId);
        }

        internal void SendFileManifestEntry(
            Guid playerId,
            UnitySyncFileSyncMessage state,
            Guid targetPlayerId)
        {
            QueueMessage(
                UnitySyncProtocol.CreateFileManifestEntry(playerId, state),
                targetPlayerId);
        }

        internal void SendPackageVersionEntry(
            Guid playerId,
            UnitySyncFileSyncMessage state,
            Guid targetPlayerId)
        {
            QueueMessage(
                UnitySyncProtocol.CreatePackageVersionEntry(playerId, state),
                targetPlayerId);
        }

        internal void SendFileManifestEnd(
            Guid playerId,
            Guid syncId,
            Guid targetPlayerId)
        {
            QueueMessage(
                UnitySyncProtocol.CreateFileManifestEnd(playerId, syncId),
                targetPlayerId);
        }

        internal void SendFileChunk(
            Guid playerId,
            UnitySyncFileSyncMessage state,
            Guid targetPlayerId)
        {
            QueueMessage(
                UnitySyncProtocol.CreateFileChunk(playerId, state),
                targetPlayerId);
        }

        internal void SendFileDownloadProgress(
            Guid syncId,
            UnitySyncFileSyncScope scope,
            long receivedBytes,
            long totalBytes)
        {
            QueueMessage(
                UnitySyncProtocol.CreateFileDownloadProgress(
                    _localPlayerId,
                    new UnitySyncFileSyncMessage
                    {
                        SyncId = syncId,
                        Scope = scope,
                        Offset = receivedBytes,
                        TotalBytes = totalBytes
                    }),
                Guid.Empty);
        }

        internal void SendFileSyncAbort(
            Guid playerId,
            Guid syncId,
            string error,
            Guid targetPlayerId)
        {
            QueueMessage(
                UnitySyncProtocol.CreateFileSyncAbort(playerId, syncId, error),
                targetPlayerId);
        }

        internal void SendProjectFileBegin(
            Guid playerId,
            UnitySyncFileSyncMessage state,
            bool sendImmediately = false)
        {
            SendProjectFilePacket(
                UnitySyncProtocol.CreateProjectFileBegin(playerId, state),
                sendImmediately);
        }

        internal void SendProjectFileChunk(
            Guid playerId,
            UnitySyncFileSyncMessage state,
            bool sendImmediately = false)
        {
            SendProjectFilePacket(
                UnitySyncProtocol.CreateProjectFileChunk(playerId, state),
                sendImmediately);
        }

        internal void SendProjectFileDelete(
            Guid playerId,
            string path,
            bool sendImmediately = false)
        {
            SendProjectFilePacket(
                UnitySyncProtocol.CreateProjectFileDelete(playerId, path),
                sendImmediately);
        }

        private void SendProjectFilePacket(byte[] payload, bool sendImmediately)
        {
            if (sendImmediately && TrySendImmediate(payload))
            {
                return;
            }

            QueueMessage(payload, Guid.Empty);
        }

        private bool TrySendImmediate(byte[] payload)
        {
            if (!_running)
            {
                return false;
            }

            if (_isHost)
            {
                Broadcast(payload, null);
                return true;
            }

            Peer server = _serverPeer;
            if (_clientReady && server != null)
            {
                TrySend(server, payload);
                return true;
            }

            return false;
        }

        internal void SendSceneObjectChange(
            Guid playerId,
            UnitySyncSceneObjectChange change,
            Guid targetPlayerId = default)
        {
            try
            {
                byte[] payload = UnitySyncProtocol.CreateSceneObjectChange(playerId, change);
                if (payload.Length > UnitySyncProtocol.MaximumFrameSize - 128)
                {
                    Enqueue(UnitySyncTransportEventKind.Error, "A scene update was too large to send.");
                    return;
                }

                if (TryGetLiveSceneStateKey(change, out string liveStateKey))
                {
                    QueueMessage(
                        payload,
                        targetPlayerId,
                        targetPlayerId.ToString("N") + "|" + liveStateKey);
                }
                else
                {
                    // Snapshot, hierarchy, structure, and destroy packets are ordering barriers.
                    // Never let a later property packet move across one of these operations.
                    QueueMessage(payload, targetPlayerId);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is InvalidDataException ||
                exception is OverflowException)
            {
                Enqueue(UnitySyncTransportEventKind.Error, "Could not serialize a scene update: " + exception.Message);
            }
        }

        internal void RequestSceneSnapshot()
        {
            QueueMessage(UnitySyncProtocol.CreateSceneSnapshotRequest(_localPlayerId), Guid.Empty);
        }

        internal void SendSceneSnapshotBegin(
            Guid playerId,
            UnitySyncSceneSnapshotBoundary snapshot,
            Guid targetPlayerId)
        {
            QueueMessage(
                UnitySyncProtocol.CreateSceneSnapshotBegin(playerId, snapshot),
                targetPlayerId);
        }

        internal void SendSceneSnapshotEnd(
            Guid playerId,
            Guid snapshotId,
            bool isComplete,
            Guid targetPlayerId)
        {
            QueueMessage(
                UnitySyncProtocol.CreateSceneSnapshotEnd(playerId, snapshotId, isComplete),
                targetPlayerId);
        }

        internal void SendSceneSettingsChange(
            Guid playerId,
            UnitySyncSceneSnapshotBoundary snapshot)
        {
            QueueMessage(
                UnitySyncProtocol.CreateSceneSettingsChange(playerId, snapshot),
                Guid.Empty);
        }

        private void QueueMessage(byte[] payload, Guid targetPlayerId)
        {
            QueueMessage(payload, targetPlayerId, string.Empty);
        }

        private void QueueMessage(
            byte[] payload,
            Guid targetPlayerId,
            string coalesceKey)
        {
            if (!_running)
            {
                return;
            }

            lock (_outboundLock)
            {
                if (!string.IsNullOrEmpty(coalesceKey))
                {
                    if (_pendingOutgoingLiveSceneMessages.TryGetValue(
                            coalesceKey,
                            out LinkedListNode<OutboundMessage> existingNode) &&
                        existingNode != null &&
                        existingNode.List == _pendingMessages)
                    {
                        long previousLength = existingNode.Value.Payload != null
                            ? existingNode.Value.Payload.LongLength
                            : 0L;
                        long replacementLength = payload != null ? payload.LongLength : 0L;
                        existingNode.Value.Payload = payload;
                        existingNode.Value.TargetPlayerId = targetPlayerId;
                        _pendingMessageBytes += replacementLength - previousLength;
                        _outboundSignal.Set();
                        return;
                    }

                    OutboundMessage coalescedMessage = new OutboundMessage
                    {
                        Payload = payload,
                        TargetPlayerId = targetPlayerId,
                        CoalesceKey = coalesceKey
                    };
                    LinkedListNode<OutboundMessage> coalescedNode =
                        _pendingMessages.AddLast(coalescedMessage);
                    _pendingOutgoingLiveSceneMessages[coalesceKey] = coalescedNode;
                    _pendingMessageBytes += payload != null ? payload.LongLength : 0L;
                }
                else
                {
                    // Any non-coalescable message is an ordering barrier. Existing queued live
                    // states stay in place, but later updates may not replace them across it.
                    _pendingOutgoingLiveSceneMessages.Clear();
                    _pendingMessages.AddLast(new OutboundMessage
                    {
                        Payload = payload,
                        TargetPlayerId = targetPlayerId
                    });
                    _pendingMessageBytes += payload != null ? payload.LongLength : 0L;
                }
            }

            _outboundSignal.Set();
        }

        private void ReleasePendingMessageBytes(byte[] payload)
        {
            long length = payload != null ? payload.LongLength : 0L;
            if (length <= 0)
            {
                return;
            }

            lock (_outboundLock)
            {
                _pendingMessageBytes = Math.Max(0L, _pendingMessageBytes - length);
            }
        }

        internal void LogLocal(string message)
        {
            Enqueue(UnitySyncTransportEventKind.Log, message ?? string.Empty);
        }

        internal void LogLocalError(string message)
        {
            Enqueue(UnitySyncTransportEventKind.Error, message ?? string.Empty);
        }

        internal bool TryDequeue(out UnitySyncTransportEvent transportEvent)
        {
            lock (_eventsLock)
            {
                if (_events.Count == 0)
                {
                    transportEvent = default;
                    return false;
                }

                LinkedListNode<UnitySyncTransportEvent> firstNode = _events.First;
                transportEvent = firstNode.Value;
                if (TryGetIncomingLiveSceneCoalesceKey(
                        transportEvent,
                        out string coalesceKey) &&
                    _pendingIncomingLiveSceneEvents.TryGetValue(
                        coalesceKey,
                        out LinkedListNode<UnitySyncTransportEvent> mappedNode) &&
                    ReferenceEquals(firstNode, mappedNode))
                {
                    _pendingIncomingLiveSceneEvents.Remove(coalesceKey);
                }

                _events.RemoveFirst();
                return true;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        internal void Stop()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _clientReady = false;
            _outboundSignal.Set();

            try
            {
                _listener?.Stop();
            }
            catch
            {
                // Best-effort shutdown.
            }

            Peer server = _serverPeer;
            server?.Close();

            Peer[] peers;
            lock (_peersLock)
            {
                peers = _peers.ToArray();
                _peers.Clear();
            }

            foreach (Peer peer in peers)
            {
                peer.Close();
            }
        }

        private void HostAcceptLoop()
        {
            Enqueue(UnitySyncTransportEventKind.Log, "Listening for collaborators.");

            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ConfigureClient(client);
                    Peer peer = new Peer(client);

                    lock (_peersLock)
                    {
                        _peers.Add(peer);
                    }

                    Thread peerThread = new Thread(() => HostPeerLoop(peer))
                    {
                        IsBackground = true,
                        Name = "UnitySync Peer"
                    };
                    peerThread.Start();
                }
                catch (SocketException)
                {
                    if (_running)
                    {
                        Enqueue(UnitySyncTransportEventKind.Error, "The host listener stopped unexpectedly.");
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Expected during shutdown.
                }
                catch (Exception exception)
                {
                    if (_running)
                    {
                        Enqueue(UnitySyncTransportEventKind.Error, "Could not accept a collaborator: " + exception.Message);
                    }
                }
            }
        }

        private void StartOutboundThread()
        {
            Thread outboundThread = new Thread(OutboundLoop)
            {
                IsBackground = true,
                Name = "UnitySync Outbound"
            };
            outboundThread.Start();
        }

        private void OutboundLoop()
        {
            while (_running)
            {
                _outboundSignal.WaitOne(250);

                byte[] viewportPayload;
                byte[] selectionPayload;
                OutboundMessage[] messages;
                lock (_outboundLock)
                {
                    viewportPayload = _pendingLocalViewport;
                    _pendingLocalViewport = null;
                    selectionPayload = _pendingLocalSelection;
                    _pendingLocalSelection = null;
                    messages = new OutboundMessage[_pendingMessages.Count];
                    _pendingMessages.CopyTo(messages, 0);
                    _pendingMessages.Clear();
                    _pendingOutgoingLiveSceneMessages.Clear();
                }

                if (!_running)
                {
                    continue;
                }

                if (_isHost)
                {
                    if (viewportPayload != null)
                    {
                        Broadcast(viewportPayload, null);
                    }

                    if (selectionPayload != null)
                    {
                        Broadcast(selectionPayload, null);
                    }

                    foreach (OutboundMessage message in messages)
                    {
                        try
                        {
                            if (message.TargetPlayerId == Guid.Empty)
                            {
                                Broadcast(message.Payload, null);
                            }
                            else
                            {
                                SendToPlayer(message.TargetPlayerId, message.Payload);
                            }
                        }
                        finally
                        {
                            ReleasePendingMessageBytes(message.Payload);
                        }
                    }
                    continue;
                }

                Peer server = _serverPeer;
                if (_clientReady && server != null)
                {
                    if (viewportPayload != null)
                    {
                        TrySend(server, viewportPayload);
                    }

                    if (selectionPayload != null)
                    {
                        TrySend(server, selectionPayload);
                    }

                    foreach (OutboundMessage message in messages)
                    {
                        try
                        {
                            TrySend(server, message.Payload);
                        }
                        finally
                        {
                            ReleasePendingMessageBytes(message.Payload);
                        }
                    }
                }
                else if (messages.Length > 0)
                {
                    lock (_outboundLock)
                    {
                        foreach (OutboundMessage message in messages)
                        {
                            _pendingMessages.AddLast(message);
                        }
                    }
                }
            }
        }

        private void HostPeerLoop(Peer peer)
        {
            bool authenticated = false;

            try
            {
                peer.Stream = peer.Client.GetStream();
                peer.Client.ReceiveTimeout = 10000;

                UnitySyncMessage hello = ReadMessage(peer);
                if (hello.Type != UnitySyncMessageType.Hello || hello.PlayerId == Guid.Empty)
                {
                    throw new InvalidDataException("The collaborator did not send a valid hello message.");
                }

                if (!string.Equals(
                        hello.UnityVersion,
                        _localUnityVersion,
                        StringComparison.Ordinal))
                {
                    string versionError =
                        "Unity version mismatch. Host is using " + _localUnityVersion +
                        ", but the connecting editor is using " + hello.UnityVersion + ".";
                    Send(
                        peer,
                        UnitySyncProtocol.CreateHandshakeRejected(
                            _localPlayerId,
                            versionError));
                    throw new InvalidDataException(versionError);
                }

                Peer supersededPeer = null;
                lock (_peersLock)
                {
                    if (hello.PlayerId == _localPlayerId)
                    {
                        throw new InvalidDataException(
                            "The collaborator is using the host's player identity.");
                    }

                    supersededPeer = _peers.Find(
                        other => other != peer && other.PlayerId == hello.PlayerId);
                    if (supersededPeer != null)
                    {
                        supersededPeer.Superseded = true;
                        _peers.Remove(supersededPeer);
                    }

                    peer.PlayerId = hello.PlayerId;
                    peer.DisplayName = NormalizeDisplayName(hello.DisplayName);
                }

                supersededPeer?.Close();
                peer.Client.ReceiveTimeout = 0;
                Send(
                    peer,
                    UnitySyncProtocol.CreateWelcome(
                        _localPlayerId,
                        _localDisplayName,
                        _localBuildTargetName,
                        _localUnityVersion));
                authenticated = true;
                Enqueue(UnitySyncTransportEventKind.Log, peer.DisplayName + " joined the session.");

                while (_running)
                {
                    UnitySyncMessage message = ReadMessage(peer);
                    if (message.PlayerId != peer.PlayerId)
                    {
                        throw new InvalidDataException("A collaborator sent an unexpected message.");
                    }

                    switch (message.Type)
                    {
                        case UnitySyncMessageType.Viewport:
                            UnitySyncViewportState viewport = WithDisplayName(message.Viewport, peer.DisplayName);
                            if (!IsValid(viewport))
                            {
                                throw new InvalidDataException("A collaborator sent an invalid viewport.");
                            }

                            EnqueueViewport(viewport);
                            Broadcast(UnitySyncProtocol.CreateViewport(viewport), peer);
                            break;

                        case UnitySyncMessageType.Selection:
                            if (!IsValid(message.Selection))
                            {
                                throw new InvalidDataException("A collaborator sent an invalid selection.");
                            }

                            EnqueueSelection(message.Selection);
                            Broadcast(UnitySyncProtocol.CreateSelection(message.Selection), peer);
                            break;

                        case UnitySyncMessageType.AssetImportState:
                            if (message.PlayerId != peer.PlayerId)
                            {
                                throw new InvalidDataException(
                                    "A collaborator sent an asset import state for another player.");
                            }

                            EnqueueAssetImportState(
                                message.PlayerId,
                                message.IsImportingAssets);
                            Broadcast(
                                UnitySyncProtocol.CreateAssetImportState(
                                    message.PlayerId,
                                    message.IsImportingAssets),
                                peer);
                            break;

                        case UnitySyncMessageType.BuildTarget:
                            if (message.PlayerId != peer.PlayerId ||
                                string.IsNullOrEmpty(message.BuildTargetName))
                            {
                                throw new InvalidDataException(
                                    "A collaborator sent an invalid build target update.");
                            }

                            // Guest build-target changes are applied by the host first. The
                            // session layer re-broadcasts the host-authored target only after
                            // Unity successfully switches, keeping one authoritative platform.
                            EnqueueBuildTarget(
                                message.PlayerId,
                                message.BuildTargetName);
                            break;

                        case UnitySyncMessageType.RestoreProjectRequest:
                            peer.RequestedFileSync = true;
                            peer.RequestedSceneSnapshot = false;
                            EnqueueFileSync(message.Type, message.PlayerId, message.FileSync);
                            break;

                        case UnitySyncMessageType.RestoreProjectAccepted:
                        case UnitySyncMessageType.RestoreProjectDeclined:
                            throw new InvalidDataException("A collaborator sent a host-only restore response.");

                        case UnitySyncMessageType.FileSyncRequest:
                            peer.RequestedFileSync = true;
                            UnitySyncFileSyncMessage scopedRequest =
                                message.FileSync ?? new UnitySyncFileSyncMessage();
                            EnqueueFileSync(
                                message.Type,
                                message.PlayerId,
                                scopedRequest);
                            break;

                        case UnitySyncMessageType.FileRequest:
                        case UnitySyncMessageType.FileDownloadProgress:
                            if (!peer.RequestedFileSync)
                            {
                                throw new InvalidDataException(
                                    "A collaborator sent file sync data before starting file sync.");
                            }

                            EnqueueFileSync(message.Type, message.PlayerId, message.FileSync);
                            break;

                        case UnitySyncMessageType.FileManifestBegin:
                        case UnitySyncMessageType.FileManifestEntry:
                        case UnitySyncMessageType.PackageVersionEntry:
                        case UnitySyncMessageType.FileManifestEnd:
                        case UnitySyncMessageType.FileChunk:
                        case UnitySyncMessageType.FileSyncAbort:
                            throw new InvalidDataException(
                                "A collaborator sent a host-only file sync message.");

                        case UnitySyncMessageType.ProjectFileBegin:
                        case UnitySyncMessageType.ProjectFileChunk:
                        case UnitySyncMessageType.ProjectFileDelete:
                            if (message.FileSync == null)
                            {
                                throw new InvalidDataException(
                                    "A collaborator sent an invalid project file update.");
                            }

                            // Project-file transfers can arrive concurrently from guests.
                            // Establish one host-observed packet order for both local application and
                            // relay, then send that exact order back through the host outbound queue
                            // to every guest, including the originator.
                            lock (_eventsLock)
                            {
                                EnqueueFileSync(message.Type, message.PlayerId, message.FileSync);
                                switch (message.Type)
                                {
                                    case UnitySyncMessageType.ProjectFileBegin:
                                        SendProjectFileBegin(
                                            message.PlayerId,
                                            message.FileSync);
                                        break;

                                    case UnitySyncMessageType.ProjectFileChunk:
                                        SendProjectFileChunk(
                                            message.PlayerId,
                                            message.FileSync);
                                        break;

                                    case UnitySyncMessageType.ProjectFileDelete:
                                        SendProjectFileDelete(
                                            message.PlayerId,
                                            message.FileSync.Path);
                                        break;
                                }
                            }
                            break;

                        case UnitySyncMessageType.SceneObjectChange:
                            // Guest receive loops can race here. Serialize host ingestion with the
                            // event queue so the host's apply order and the authoritative relay order
                            // use the same last-write-wins sequence. Echo the host-observed packet to
                            // every guest, including the originator, even in a two-user session.
                            lock (_eventsLock)
                            {
                                EnqueueSceneChange(message.PlayerId, message.SceneChange);
                                SendSceneObjectChange(message.PlayerId, message.SceneChange);
                            }
                            break;

                        case UnitySyncMessageType.SceneSettingsChange:
                            if (message.SceneSnapshot == null)
                            {
                                throw new InvalidDataException(
                                    "A collaborator sent invalid scene settings.");
                            }

                            // Use the same host-authoritative ordering as live scene objects.
                            // Serialize ingestion and relay through the host queue and include the
                            // originator in the echo, even when there is only one guest.
                            lock (_eventsLock)
                            {
                                EnqueueSceneSettingsChange(
                                    message.PlayerId,
                                    message.SceneSnapshot);
                                SendSceneSettingsChange(
                                    message.PlayerId,
                                    message.SceneSnapshot);
                            }
                            break;

                        case UnitySyncMessageType.SceneSnapshotBegin:
                        case UnitySyncMessageType.SceneSnapshotEnd:
                            throw new InvalidDataException(
                                "A collaborator sent a host-only scene snapshot message.");

                        case UnitySyncMessageType.SceneSnapshotRequest:
                            if (!peer.RequestedSceneSnapshot)
                            {
                                peer.RequestedSceneSnapshot = true;
                                EnqueueSceneSnapshotRequest(peer.PlayerId);
                            }
                            break;

                        default:
                            throw new InvalidDataException("A collaborator sent an unexpected message.");
                    }
                }
            }
            catch (EndOfStreamException)
            {
                if (_running && authenticated && !peer.Superseded)
                {
                    Enqueue(
                        UnitySyncTransportEventKind.Log,
                        peer.DisplayName + " disconnected.");
                }
                else if (_running && !authenticated)
                {
                    Enqueue(
                        UnitySyncTransportEventKind.Log,
                        "Collaborator connection closed during the handshake.");
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is SocketException ||
                exception is ObjectDisposedException ||
                exception is CryptographicException ||
                exception is InvalidDataException)
            {
                if (_running && authenticated && !peer.Superseded)
                {
                    Enqueue(
                        UnitySyncTransportEventKind.Error,
                        peer.DisplayName + " disconnected: " + exception.Message);
                }
                else if (_running && !authenticated)
                {
                    Enqueue(
                        UnitySyncTransportEventKind.Error,
                        "Rejected collaborator connection: " + exception.Message);
                }
            }
            finally
            {
                peer.Close();
                lock (_peersLock)
                {
                    _peers.Remove(peer);
                }

                if (_running && authenticated && !peer.Superseded)
                {
                    EnqueuePeerLeft(peer.PlayerId);
                    Broadcast(UnitySyncProtocol.CreatePeerLeft(peer.PlayerId), peer);
                }
            }
        }

        private void ClientLoop(IPAddress address, int port)
        {
            string disconnectReason = "Disconnected from the host.";

            try
            {
                TcpClient client = new TcpClient(AddressFamily.InterNetwork);
                ConfigureClient(client);
                Peer server = new Peer(client);
                _serverPeer = server;

                client.Connect(address, port);
                server.Stream = client.GetStream();
                Send(
                    server,
                    UnitySyncProtocol.CreateHello(
                        _localPlayerId,
                        _localDisplayName,
                        _localUnityVersion));

                UnitySyncMessage welcome;
                try
                {
                    welcome = ReadMessage(server);
                }
                catch (EndOfStreamException exception)
                {
                    throw new IOException(
                        "The host closed the connection during the UnitySync handshake. " +
                        "Check the host Activity Log for the rejection reason.",
                        exception);
                }

                if (welcome.Type == UnitySyncMessageType.HandshakeRejected)
                {
                    throw new InvalidDataException(welcome.HandshakeError);
                }

                if (welcome.Type != UnitySyncMessageType.Welcome || welcome.PlayerId == Guid.Empty)
                {
                    throw new InvalidDataException("The host did not complete the UnitySync handshake.");
                }

                if (!string.Equals(
                        welcome.UnityVersion,
                        _localUnityVersion,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Unity version mismatch. Host is using " + welcome.UnityVersion +
                        ", but this editor is using " + _localUnityVersion + ".");
                }

                server.PlayerId = welcome.PlayerId;
                server.DisplayName = NormalizeDisplayName(welcome.DisplayName);

                // Apply the host platform before the Connected event starts package/file sync.
                // If switching platforms triggers a script reload, Connected remains queued
                // until UnitySync resumes in the new editor domain.
                EnqueueBuildTarget(server.PlayerId, welcome.BuildTargetName);

                _clientReady = true;
                Enqueue(UnitySyncTransportEventKind.Connected, "Connected to " + server.DisplayName +
                    ". Local protocol " + UnitySyncProtocol.Version + " (packet diagnostics 2).");

                while (_running)
                {
                    UnitySyncMessage message = ReadMessage(server);
                    switch (message.Type)
                    {
                        case UnitySyncMessageType.Viewport:
                            if (message.PlayerId == Guid.Empty || !IsValid(message.Viewport))
                            {
                                throw new InvalidDataException("The host sent an invalid viewport.");
                            }

                            EnqueueViewport(message.Viewport);
                            break;

                        case UnitySyncMessageType.Selection:
                            if (message.PlayerId == Guid.Empty || !IsValid(message.Selection))
                            {
                                throw new InvalidDataException("The host sent an invalid selection.");
                            }

                            EnqueueSelection(message.Selection);
                            break;

                        case UnitySyncMessageType.AssetImportState:
                            if (message.PlayerId == Guid.Empty)
                            {
                                throw new InvalidDataException(
                                    "The host sent an invalid asset import state.");
                            }

                            EnqueueAssetImportState(
                                message.PlayerId,
                                message.IsImportingAssets);
                            break;

                        case UnitySyncMessageType.BuildTarget:
                            if (message.PlayerId != server.PlayerId ||
                                string.IsNullOrEmpty(message.BuildTargetName))
                            {
                                throw new InvalidDataException(
                                    "The host sent an invalid build target update.");
                            }

                            EnqueueBuildTarget(
                                message.PlayerId,
                                message.BuildTargetName);
                            break;

                        case UnitySyncMessageType.RestoreProjectAccepted:
                        case UnitySyncMessageType.RestoreProjectDeclined:
                            if (message.PlayerId != server.PlayerId)
                            {
                                throw new InvalidDataException("Invalid restore response sender.");
                            }
                            EnqueueFileSync(message.Type, message.PlayerId, message.FileSync);
                            break;

                        case UnitySyncMessageType.FileManifestBegin:
                        case UnitySyncMessageType.FileManifestEntry:
                        case UnitySyncMessageType.PackageVersionEntry:
                        case UnitySyncMessageType.FileManifestEnd:
                        case UnitySyncMessageType.FileChunk:
                        case UnitySyncMessageType.FileSyncAbort:
                            if (message.PlayerId == Guid.Empty || message.FileSync == null)
                            {
                                throw new InvalidDataException(
                                    "The host sent an invalid file sync message.");
                            }

                            EnqueueFileSync(message.Type, message.PlayerId, message.FileSync);
                            break;

                        case UnitySyncMessageType.ProjectFileBegin:
                        case UnitySyncMessageType.ProjectFileChunk:
                        case UnitySyncMessageType.ProjectFileDelete:
                            if (message.PlayerId == Guid.Empty || message.FileSync == null)
                            {
                                throw new InvalidDataException(
                                    "The host sent an invalid project file update.");
                            }

                            EnqueueFileSync(message.Type, message.PlayerId, message.FileSync);
                            break;

                        case UnitySyncMessageType.FileSyncRequest:
                        case UnitySyncMessageType.FileRequest:
                        case UnitySyncMessageType.FileDownloadProgress:
                            throw new InvalidDataException(
                                "The host sent a guest-only file sync message.");

                        case UnitySyncMessageType.PeerLeft:
                            EnqueuePeerLeft(message.PlayerId);
                            break;

                        case UnitySyncMessageType.SceneObjectChange:
                            if (message.PlayerId == Guid.Empty || message.SceneChange == null)
                            {
                                throw new InvalidDataException("The host sent an invalid scene update.");
                            }

                            EnqueueSceneChange(message.PlayerId, message.SceneChange);
                            break;

                        case UnitySyncMessageType.SceneSettingsChange:
                            if (message.PlayerId == Guid.Empty || message.SceneSnapshot == null)
                            {
                                throw new InvalidDataException(
                                    "The host sent invalid scene settings.");
                            }

                            EnqueueSceneSettingsChange(message.PlayerId, message.SceneSnapshot);
                            break;

                        case UnitySyncMessageType.SceneSnapshotBegin:
                            if (message.PlayerId == Guid.Empty || message.SceneSnapshot == null)
                            {
                                throw new InvalidDataException("The host sent an invalid scene snapshot.");
                            }

                            EnqueueSceneSnapshotBegin(message.PlayerId, message.SceneSnapshot);
                            break;

                        case UnitySyncMessageType.SceneSnapshotEnd:
                            if (message.PlayerId == Guid.Empty ||
                                message.SceneSnapshot == null ||
                                message.SceneSnapshot.SnapshotId == Guid.Empty)
                            {
                                throw new InvalidDataException("The host ended an invalid scene snapshot.");
                            }

                            EnqueueSceneSnapshotEnd(message.PlayerId, message.SceneSnapshot);
                            break;

                        default:
                            throw new InvalidDataException("The host sent an unexpected message.");
                    }
                }
            }
            catch (EndOfStreamException)
            {
                // EOF after the welcome handshake is the normal result when the host ends
                // the session and closes its TCP stream. Keep the default disconnect reason
                // so the session layer exits cleanly instead of reporting a connection failure.
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is SocketException ||
                exception is ObjectDisposedException ||
                exception is CryptographicException ||
                exception is InvalidDataException)
            {
                if (_running)
                {
                    disconnectReason = "Connection failed: " + exception.Message;
                }
            }
            finally
            {
                _clientReady = false;
                bool wasRunning = _running;
                _running = false;
                Guid disconnectedPlayerId =
                    _serverPeer != null ? _serverPeer.PlayerId : Guid.Empty;
                _serverPeer?.Close();
                if (wasRunning)
                {
                    EnqueueDisconnected(disconnectedPlayerId, disconnectReason);
                }
            }
        }

        private UnitySyncMessage ReadMessage(Peer peer)
        {
            byte[] lengthBytes = ReadExact(peer.Stream, 4);
            int frameLength = (lengthBytes[0] << 24) |
                              (lengthBytes[1] << 16) |
                              (lengthBytes[2] << 8) |
                              lengthBytes[3];

            if (frameLength <= 0 || frameLength > UnitySyncProtocol.MaximumFrameSize)
            {
                throw new InvalidDataException("Invalid UnitySync frame length.");
            }

            byte[] envelope = ReadExact(peer.Stream, frameLength);
            Interlocked.Add(ref _totalBytesReceived, 4L + frameLength);
            byte[] plaintext;
            lock (_cryptoLock)
            {
                plaintext = _crypto.Decrypt(envelope);
            }

            if (!UnitySyncProtocol.TryRead(
                plaintext, out UnitySyncMessage message, out string error))
            {
                throw new InvalidDataException("Invalid UnitySync message: " + error);
            }

            return message;
        }

        private void Send(Peer peer, byte[] plaintext)
        {
            byte[] envelope;
            lock (_cryptoLock)
            {
                envelope = _crypto.Encrypt(plaintext);
            }

            if (envelope.Length > UnitySyncProtocol.MaximumFrameSize)
            {
                throw new InvalidDataException("UnitySync message is too large.");
            }

            byte[] lengthBytes =
            {
                (byte)(envelope.Length >> 24),
                (byte)(envelope.Length >> 16),
                (byte)(envelope.Length >> 8),
                (byte)envelope.Length
            };

            lock (peer.SendLock)
            {
                peer.Stream.Write(lengthBytes, 0, lengthBytes.Length);
                peer.Stream.Write(envelope, 0, envelope.Length);
                peer.Stream.Flush();
                Interlocked.Add(
                    ref _totalBytesSent,
                    lengthBytes.LongLength + envelope.LongLength);
            }
        }

        private void Broadcast(byte[] payload, Peer except)
        {
            Peer[] peers;
            lock (_peersLock)
            {
                peers = _peers.ToArray();
            }

            foreach (Peer peer in peers)
            {
                if (peer == except || peer.PlayerId == Guid.Empty)
                {
                    continue;
                }

                TrySend(peer, payload);
            }
        }

        private void SendToPlayer(Guid playerId, byte[] payload)
        {
            Peer target = null;
            lock (_peersLock)
            {
                target = _peers.Find(peer => peer.PlayerId == playerId);
            }

            if (target != null)
            {
                TrySend(target, payload);
            }
        }

        private void TrySend(Peer peer, byte[] payload)
        {
            try
            {
                Send(peer, payload);
            }
            catch (IOException)
            {
                peer.Close();
            }
            catch (SocketException)
            {
                peer.Close();
            }
            catch (ObjectDisposedException)
            {
                peer.Close();
            }
            catch (InvalidDataException exception)
            {
                Enqueue(UnitySyncTransportEventKind.Error, "Could not send an update: " + exception.Message);
            }
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
            }

            return buffer;
        }

        private static void ConfigureClient(TcpClient client)
        {
            client.NoDelay = true;
            client.SendTimeout = 0;
            // Leave TCP send/receive buffer sizing to the OS. UnitySync file chunks can be
            // hundreds of kilobytes, and forcing a 16 KB socket buffer severely constrains
            // throughput once latency is non-trivial (VPN/WAN connections in particular).
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }

        private static string NormalizeDisplayName(string displayName)
        {
            string value = string.IsNullOrWhiteSpace(displayName) ? "Collaborator" : displayName.Trim();
            StringBuilder builder = new StringBuilder(Math.Min(value.Length, 32));
            foreach (char character in value)
            {
                if (!char.IsControl(character))
                {
                    builder.Append(character);
                    if (builder.Length == 32)
                    {
                        break;
                    }
                }
            }

            return builder.Length > 0 ? builder.ToString() : "Collaborator";
        }

        private static UnitySyncViewportState WithDisplayName(UnitySyncViewportState viewport, string displayName)
        {
            return new UnitySyncViewportState(
                viewport.PlayerId,
                displayName,
                viewport.Color,
                viewport.Position,
                viewport.Rotation,
                viewport.Pivot,
                viewport.FieldOfView,
                viewport.Aspect,
                viewport.Orthographic,
                viewport.OrthographicSize,
                viewport.SceneViewSize,
                viewport.ScenePath,
                viewport.SpectatingPlayerId);
        }

        private static bool IsValid(UnitySyncSelectionState selection)
        {
            if (selection.PlayerId == Guid.Empty ||
                !IsValidColorComponent(selection.Color.r) ||
                !IsValidColorComponent(selection.Color.g) ||
                !IsValidColorComponent(selection.Color.b) ||
                selection.ObjectIds == null ||
                selection.ObjectIds.Length > 4096)
            {
                return false;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string objectId in selection.ObjectIds)
            {
                if (!Guid.TryParse(objectId, out Guid parsedId) ||
                    parsedId == Guid.Empty ||
                    !seen.Add(parsedId.ToString("N")))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValid(UnitySyncViewportState viewport)
        {
            return IsFinite(viewport.Position.x) &&
                   IsFinite(viewport.Position.y) &&
                   IsFinite(viewport.Position.z) &&
                   IsFinite(viewport.Rotation.x) &&
                   IsFinite(viewport.Rotation.y) &&
                   IsFinite(viewport.Rotation.z) &&
                   IsFinite(viewport.Rotation.w) &&
                   IsFinite(viewport.Pivot.x) &&
                   IsFinite(viewport.Pivot.y) &&
                   IsFinite(viewport.Pivot.z) &&
                   IsValidColorComponent(viewport.Color.r) &&
                   IsValidColorComponent(viewport.Color.g) &&
                   IsValidColorComponent(viewport.Color.b) &&
                   IsFinite(viewport.FieldOfView) &&
                   viewport.FieldOfView > 0f &&
                   viewport.FieldOfView < 180f &&
                   IsFinite(viewport.Aspect) &&
                   viewport.Aspect > 0f &&
                   IsFinite(viewport.OrthographicSize) &&
                   viewport.OrthographicSize > 0f &&
                   IsFinite(viewport.SceneViewSize) &&
                   viewport.SceneViewSize > 0f &&
                   viewport.ScenePath != null &&
                   viewport.ScenePath.Length <= 4096 &&
                   viewport.SpectatingPlayerId != viewport.PlayerId;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsValidColorComponent(float value)
        {
            return IsFinite(value) && value >= 0f && value <= 1f;
        }

        private static double GetMonotonicSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        private void Enqueue(UnitySyncTransportEventKind kind, string message)
        {
            lock (_eventsLock)
            {
                _events.AddLast(new UnitySyncTransportEvent(kind, default, null, Guid.Empty, message));
            }
        }

        private void EnqueueDisconnected(Guid playerId, string message)
        {
            lock (_eventsLock)
            {
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.Disconnected,
                    default,
                    null,
                    playerId,
                    message));
            }
        }

        private void EnqueueViewport(UnitySyncViewportState viewport)
        {
            lock (_eventsLock)
            {
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.Viewport,
                    viewport,
                    null,
                    viewport.PlayerId,
                    string.Empty,
                    receivedAtSeconds: GetMonotonicSeconds()));
            }
        }

        private void EnqueueSelection(UnitySyncSelectionState selection)
        {
            lock (_eventsLock)
            {
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.Selection,
                    default,
                    null,
                    selection.PlayerId,
                    string.Empty,
                    null,
                    selection));
            }
        }

        private void EnqueueAssetImportState(Guid playerId, bool isImportingAssets)
        {
            lock (_eventsLock)
            {
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.AssetImportState,
                    default,
                    null,
                    playerId,
                    string.Empty,
                    isImportingAssets: isImportingAssets));
            }
        }

        private void EnqueueBuildTarget(Guid playerId, string buildTargetName)
        {
            lock (_eventsLock)
            {
                BreakIncomingLiveSceneCoalescingLocked();
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.BuildTarget,
                    default,
                    null,
                    playerId,
                    string.Empty,
                    buildTargetName: buildTargetName));
            }
        }

        private void EnqueueFileSync(
            UnitySyncMessageType messageType,
            Guid playerId,
            UnitySyncFileSyncMessage fileSync)
        {
            lock (_eventsLock)
            {
                BreakIncomingLiveSceneCoalescingLocked();
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.FileSync,
                    default,
                    null,
                    playerId,
                    string.Empty,
                    null,
                    default,
                    messageType,
                    fileSync));
            }
        }

        private void EnqueuePeerLeft(Guid playerId)
        {
            lock (_eventsLock)
            {
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.PeerLeft,
                    default,
                    null,
                    playerId,
                    string.Empty));
            }
        }

        private void EnqueueSceneChange(Guid playerId, UnitySyncSceneObjectChange change)
        {
            UnitySyncTransportEvent transportEvent = new UnitySyncTransportEvent(
                UnitySyncTransportEventKind.SceneObjectChange,
                default,
                change,
                playerId,
                string.Empty);

            lock (_eventsLock)
            {
                if (TryGetIncomingLiveSceneCoalesceKey(
                        transportEvent,
                        out string coalesceKey))
                {
                    if (_pendingIncomingLiveSceneEvents.TryGetValue(
                            coalesceKey,
                            out LinkedListNode<UnitySyncTransportEvent> existingNode) &&
                        existingNode != null &&
                        existingNode.List == _events)
                    {
                        // Keep the queue position but replace stale intermediate state with the
                        // newest value. This is the same latest-state principle used by viewport
                        // and selection transport, without crossing structural ordering barriers.
                        existingNode.Value = transportEvent;
                        return;
                    }

                    LinkedListNode<UnitySyncTransportEvent> node =
                        _events.AddLast(transportEvent);
                    _pendingIncomingLiveSceneEvents[coalesceKey] = node;
                    return;
                }

                BreakIncomingLiveSceneCoalescingLocked();
                _events.AddLast(transportEvent);
            }
        }

        private void EnqueueSceneSnapshotRequest(Guid playerId)
        {
            lock (_eventsLock)
            {
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.SceneSnapshotRequest,
                    default,
                    null,
                    playerId,
                    string.Empty));
            }
        }

        private void EnqueueSceneSnapshotBegin(
            Guid playerId,
            UnitySyncSceneSnapshotBoundary snapshot)
        {
            lock (_eventsLock)
            {
                BreakIncomingLiveSceneCoalescingLocked();
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.SceneSnapshotBegin,
                    default,
                    null,
                    playerId,
                    string.Empty,
                    snapshot));
            }
        }

        private void EnqueueSceneSnapshotEnd(
            Guid playerId,
            UnitySyncSceneSnapshotBoundary snapshot)
        {
            lock (_eventsLock)
            {
                BreakIncomingLiveSceneCoalescingLocked();
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.SceneSnapshotEnd,
                    default,
                    null,
                    playerId,
                    string.Empty,
                    snapshot));
            }
        }

        private void EnqueueSceneSettingsChange(
            Guid playerId,
            UnitySyncSceneSnapshotBoundary snapshot)
        {
            lock (_eventsLock)
            {
                BreakIncomingLiveSceneCoalescingLocked();
                _events.AddLast(new UnitySyncTransportEvent(
                    UnitySyncTransportEventKind.SceneSettingsChange,
                    default,
                    null,
                    playerId,
                    string.Empty,
                    snapshot));
            }
        }

        internal static bool TryGetLiveSceneStateKey(
            UnitySyncSceneObjectChange change,
            out string stateKey)
        {
            stateKey = string.Empty;
            if (change == null ||
                change.Address == null ||
                change.SnapshotId != Guid.Empty ||
                change.Kind != UnitySyncSceneChangeKind.Upsert ||
                change.HierarchyOnly ||
                change.ReconcileComponents)
            {
                return false;
            }

            if (change.GameObject != null &&
                (change.Components == null || change.Components.Length == 0))
            {
                stateKey = change.Address.Key + "|g";
                return true;
            }

            if (change.GameObject == null &&
                change.Components != null &&
                change.Components.Length == 1 &&
                change.Components[0] != null)
            {
                stateKey = change.Address.Key + "|c:" + change.Components[0].ComponentIndex;
                return true;
            }

            return false;
        }

        private static bool TryGetIncomingLiveSceneCoalesceKey(
            UnitySyncTransportEvent transportEvent,
            out string coalesceKey)
        {
            coalesceKey = string.Empty;
            if (transportEvent.Kind != UnitySyncTransportEventKind.SceneObjectChange ||
                !TryGetLiveSceneStateKey(transportEvent.SceneChange, out string stateKey))
            {
                return false;
            }

            // The transport stream defines last-write-wins order for shared live state.
            // Do not split the same path by originating player, otherwise an A/B/A burst
            // can leave a stale B event behind after A's newer packet replaced A's old node.
            coalesceKey = stateKey;
            return true;
        }

        private void BreakIncomingLiveSceneCoalescingLocked()
        {
            _pendingIncomingLiveSceneEvents.Clear();
        }
    }
}
