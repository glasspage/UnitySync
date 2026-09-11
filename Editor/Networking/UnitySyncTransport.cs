using System;
using System.Collections.Generic;
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
        PeerLeft,
        Log
    }

    internal readonly struct UnitySyncTransportEvent
    {
        internal readonly UnitySyncTransportEventKind Kind;
        internal readonly UnitySyncViewportState Viewport;
        internal readonly Guid PlayerId;
        internal readonly string Message;

        internal UnitySyncTransportEvent(
            UnitySyncTransportEventKind kind,
            UnitySyncViewportState viewport,
            Guid playerId,
            string message)
        {
            Kind = kind;
            Viewport = viewport;
            PlayerId = playerId;
            Message = message;
        }
    }

    internal sealed class UnitySyncTransport : IDisposable
    {
        private sealed class Peer
        {
            internal readonly TcpClient Client;
            internal readonly object SendLock = new object();
            internal NetworkStream Stream;
            internal Guid PlayerId;
            internal string DisplayName;

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
        private readonly UnitySyncCrypto _crypto;
        private readonly object _cryptoLock = new object();
        private readonly object _peersLock = new object();
        private readonly List<Peer> _peers = new List<Peer>();
        private readonly object _eventsLock = new object();
        private readonly Queue<UnitySyncTransportEvent> _events = new Queue<UnitySyncTransportEvent>();
        private readonly object _outboundLock = new object();
        private readonly AutoResetEvent _outboundSignal = new AutoResetEvent(false);

        private volatile bool _running;
        private volatile bool _clientReady;
        private bool _isHost;
        private TcpListener _listener;
        private Peer _serverPeer;
        private byte[] _pendingLocalViewport;

        internal UnitySyncTransport(Guid localPlayerId, string localDisplayName, byte[] secret)
        {
            _localPlayerId = localPlayerId;
            _localDisplayName = NormalizeDisplayName(localDisplayName);
            _crypto = new UnitySyncCrypto(secret);
        }

        internal bool IsRunning => _running;
        internal bool IsClientReady => _clientReady;

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

        internal bool TryDequeue(out UnitySyncTransportEvent transportEvent)
        {
            lock (_eventsLock)
            {
                if (_events.Count == 0)
                {
                    transportEvent = default;
                    return false;
                }

                transportEvent = _events.Dequeue();
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
                        Enqueue(UnitySyncTransportEventKind.Log, "The host listener stopped unexpectedly.");
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
                        Enqueue(UnitySyncTransportEventKind.Log, "Could not accept a collaborator: " + exception.Message);
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

                byte[] payload;
                lock (_outboundLock)
                {
                    payload = _pendingLocalViewport;
                    _pendingLocalViewport = null;
                }

                if (!_running || payload == null)
                {
                    continue;
                }

                if (_isHost)
                {
                    Broadcast(payload, null);
                    continue;
                }

                Peer server = _serverPeer;
                if (_clientReady && server != null)
                {
                    TrySend(server, payload);
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

                lock (_peersLock)
                {
                    if (hello.PlayerId == _localPlayerId ||
                        _peers.Exists(other => other != peer && other.PlayerId == hello.PlayerId))
                    {
                        throw new InvalidDataException("A collaborator with this identity is already connected.");
                    }

                    peer.PlayerId = hello.PlayerId;
                    peer.DisplayName = NormalizeDisplayName(hello.DisplayName);
                }

                peer.Client.ReceiveTimeout = 0;
                Send(peer, UnitySyncProtocol.CreateWelcome(_localPlayerId, _localDisplayName));
                authenticated = true;
                Enqueue(UnitySyncTransportEventKind.Log, peer.DisplayName + " joined the session.");

                while (_running)
                {
                    UnitySyncMessage message = ReadMessage(peer);
                    if (message.Type != UnitySyncMessageType.Viewport || message.PlayerId != peer.PlayerId)
                    {
                        throw new InvalidDataException("A collaborator sent an unexpected message.");
                    }

                    UnitySyncViewportState viewport = WithDisplayName(message.Viewport, peer.DisplayName);
                    if (!IsValid(viewport))
                    {
                        throw new InvalidDataException("A collaborator sent an invalid viewport.");
                    }

                    EnqueueViewport(viewport);
                    Broadcast(UnitySyncProtocol.CreateViewport(viewport), peer);
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is SocketException ||
                exception is ObjectDisposedException ||
                exception is CryptographicException ||
                exception is InvalidDataException)
            {
                if (_running && authenticated)
                {
                    Enqueue(UnitySyncTransportEventKind.Log, peer.DisplayName + " disconnected.");
                }
            }
            finally
            {
                peer.Close();
                lock (_peersLock)
                {
                    _peers.Remove(peer);
                }

                if (_running && authenticated)
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
                Send(server, UnitySyncProtocol.CreateHello(_localPlayerId, _localDisplayName));

                UnitySyncMessage welcome = ReadMessage(server);
                if (welcome.Type != UnitySyncMessageType.Welcome || welcome.PlayerId == Guid.Empty)
                {
                    throw new InvalidDataException("The host did not complete the UnitySync handshake.");
                }

                server.PlayerId = welcome.PlayerId;
                server.DisplayName = NormalizeDisplayName(welcome.DisplayName);
                _clientReady = true;
                Enqueue(UnitySyncTransportEventKind.Connected, "Connected to " + server.DisplayName + ".");

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

                        case UnitySyncMessageType.PeerLeft:
                            EnqueuePeerLeft(message.PlayerId);
                            break;

                        default:
                            throw new InvalidDataException("The host sent an unexpected message.");
                    }
                }
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
                _serverPeer?.Close();
                if (wasRunning)
                {
                    Enqueue(UnitySyncTransportEventKind.Disconnected, disconnectReason);
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
            byte[] plaintext;
            lock (_cryptoLock)
            {
                plaintext = _crypto.Decrypt(envelope);
            }

            if (!UnitySyncProtocol.TryRead(plaintext, out UnitySyncMessage message))
            {
                throw new InvalidDataException("Invalid UnitySync message.");
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
            client.SendTimeout = 5000;
            client.ReceiveBufferSize = 16 * 1024;
            client.SendBufferSize = 16 * 1024;
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
                viewport.OrthographicSize);
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
                   viewport.OrthographicSize > 0f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsValidColorComponent(float value)
        {
            return IsFinite(value) && value >= 0f && value <= 1f;
        }

        private void Enqueue(UnitySyncTransportEventKind kind, string message)
        {
            lock (_eventsLock)
            {
                _events.Enqueue(new UnitySyncTransportEvent(kind, default, Guid.Empty, message));
            }
        }

        private void EnqueueViewport(UnitySyncViewportState viewport)
        {
            lock (_eventsLock)
            {
                _events.Enqueue(new UnitySyncTransportEvent(UnitySyncTransportEventKind.Viewport, viewport, viewport.PlayerId, string.Empty));
            }
        }

        private void EnqueuePeerLeft(Guid playerId)
        {
            lock (_eventsLock)
            {
                _events.Enqueue(new UnitySyncTransportEvent(UnitySyncTransportEventKind.PeerLeft, default, playerId, string.Empty));
            }
        }
    }
}
