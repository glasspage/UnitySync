using System;
using System.Collections.Generic;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal enum UnitySyncSessionState
    {
        Idle,
        Hosting,
        Connecting,
        Connected
    }

    [InitializeOnLoad]
    internal static class UnitySyncSession
    {
        private const double SendIntervalSeconds = 0.1;
        private const string PlayerIdSessionKey = "Glasspage.UnitySync.PlayerId";
        private const string FileSyncResumePendingKey = "Glasspage.UnitySync.FileSyncResume.Pending";
        private const string FileSyncResumeJoinCodeKey = "Glasspage.UnitySync.FileSyncResume.JoinCode";
        private const string FileSyncResumeDisplayNameKey = "Glasspage.UnitySync.FileSyncResume.DisplayName";
        private const string FileSyncResumeColorKey = "Glasspage.UnitySync.FileSyncResume.Color";
        private const string FileSyncResumeApprovedKey = "Glasspage.UnitySync.FileSyncResume.Approved";
        private const int MaximumFileSyncResumeAttempts = 8;
        private const double FileSyncResumeRetrySeconds = 0.5d;

        private sealed class PendingGuestViewport
        {
            internal UnitySyncViewportState Viewport;
            internal double ReceivedAtSeconds;
        }

        private static readonly Guid LocalPlayerId;
        private static readonly List<string> Logs = new List<string>();
        private static readonly Dictionary<Guid, PendingGuestViewport> PendingGuestViewports =
            new Dictionary<Guid, PendingGuestViewport>();

        private static UnitySyncTransport _transport;
        private static UnitySyncSessionState _state;
        private static string _displayName = "Collaborator";
        private static Color _color = Color.white;
        private static string _joinCode = string.Empty;
        private static string _guestJoinCode = string.Empty;
        private static double _nextSendTime;
        private static bool _hasLastViewportState;
        private static UnitySyncViewportState _lastViewportState;
        private static bool _hasLastSelectionState;
        private static string _lastSelectionSignature = string.Empty;
        private static Color _lastSelectionColor = Color.white;
        private static int _fileSyncResumeAttempts;
        private static double _nextFileSyncResumeAttemptTime;
        private static bool _fileSyncResumeConnectionPending;
        private static bool _guestSyncApproved;
        private static Guid _spectatingPlayerId = Guid.Empty;
        private static SceneView _spectatedSceneView;
        private static bool _hasSavedSceneViewState;
        private static Vector3 _savedSceneViewPivot;
        private static Quaternion _savedSceneViewRotation = Quaternion.identity;
        private static float _savedSceneViewSize;
        private static bool _savedSceneViewOrthographic;
        private static float _savedSceneViewFieldOfView;

        internal static event Action Changed;

        internal static UnitySyncSessionState State => _state;
        internal static string JoinCode => _joinCode;
        internal static bool IsActive => _transport != null;
        internal static bool IsFileSyncing => UnitySyncFileSynchronizer.IsGuestSyncing;
        internal static bool IsAwaitingFileDownloadConfirmation =>
            UnitySyncFileSynchronizer.IsGuestAwaitingDownloadConfirmation;
        internal static int PendingFileDownloadCount =>
            UnitySyncFileSynchronizer.GuestPendingDownloadFileCount;
        internal static long PendingFileDownloadBytes =>
            UnitySyncFileSynchronizer.GuestPendingDownloadBytes;
        internal static Color DefaultColor => ColorFor(LocalPlayerId);
        internal static Guid CurrentPlayerId => LocalPlayerId;
        internal static Guid SpectatingPlayerId => _spectatingPlayerId;

        static UnitySyncSession()
        {
            LocalPlayerId = LoadOrCreatePlayerId();
            EditorApplication.update += Update;
            EditorApplication.quitting += Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.delayCall += ScheduleFileSyncResume;
        }

        internal static bool StartHost(
            string advertisedAddress,
            int port,
            string displayName,
            Color color,
            out string error)
        {
            error = string.Empty;
            if (_transport != null)
            {
                error = "Stop the current UnitySync session first.";
                return false;
            }

            if (!UnitySyncJoinCode.TryCreate(advertisedAddress, port, out string code, out byte[] secret, out error))
            {
                return false;
            }

            try
            {
                _displayName = NormalizeDisplayName(displayName);
                _color = NormalizeColor(color);
                _transport = new UnitySyncTransport(LocalPlayerId, _displayName, secret);
                _transport.StartHost(port);
                _joinCode = code;
                _guestJoinCode = string.Empty;
                _guestSyncApproved = true;
                PendingGuestViewports.Clear();
                ClearFileSyncReloadReconnect();
                _state = UnitySyncSessionState.Hosting;
                _nextSendTime = 0d;
                _hasLastViewportState = false;
                _hasLastSelectionState = false;
                _lastSelectionSignature = string.Empty;
                UnitySyncSceneSynchronizer.BeginSession();
                UnitySyncProjectSynchronizer.BeginSession();
                AddLog("Hosting on " + advertisedAddress + ":" + port + ".");
                Changed?.Invoke();
                return true;
            }
            catch (Exception exception) when (exception is SocketException || exception is ArgumentException)
            {
                _transport?.Dispose();
                _transport = null;
                _joinCode = string.Empty;
                _state = UnitySyncSessionState.Idle;
                error = "Could not start the host: " + exception.Message;
                return false;
            }
            finally
            {
                if (secret != null)
                {
                    Array.Clear(secret, 0, secret.Length);
                }
            }
        }

        internal static bool Connect(string joinCode, string displayName, Color color, out string error)
        {
            error = string.Empty;
            if (_transport != null)
            {
                error = "Stop the current UnitySync session first.";
                return false;
            }

            if (!UnitySyncJoinCode.TryParse(joinCode, out UnitySyncJoinCodeData data, out error))
            {
                return false;
            }

            try
            {
                _displayName = NormalizeDisplayName(displayName);
                _color = NormalizeColor(color);
                _transport = new UnitySyncTransport(LocalPlayerId, _displayName, data.Secret);
                _transport.StartClient(data.Address, data.Port);
                _guestJoinCode = joinCode;
                _guestSyncApproved = false;
                PendingGuestViewports.Clear();
                _state = UnitySyncSessionState.Connecting;
                _nextSendTime = 0d;
                _hasLastViewportState = false;
                _hasLastSelectionState = false;
                _lastSelectionSignature = string.Empty;
                AddLog("Connecting to " + data.Address + ":" + data.Port + "...");
                Changed?.Invoke();
                return true;
            }
            catch (Exception exception) when (exception is SocketException || exception is ArgumentException)
            {
                _transport?.Dispose();
                _transport = null;
                _state = UnitySyncSessionState.Idle;
                error = "Could not connect: " + exception.Message;
                return false;
            }
            finally
            {
                if (data.Secret != null)
                {
                    Array.Clear(data.Secret, 0, data.Secret.Length);
                }
            }
        }

        internal static void Stop()
        {
            StopInternal(true);
        }

        internal static bool ContinueFileSync(out string error)
        {
            error = string.Empty;
            if (_transport == null || _state != UnitySyncSessionState.Connected)
            {
                error = "UnitySync is not connected to a host.";
                return false;
            }

            int fileCount = PendingFileDownloadCount;
            long downloadBytes = PendingFileDownloadBytes;
            if (!UnitySyncFileSynchronizer.ContinueGuestSync(_transport, out error))
            {
                return false;
            }

            _guestSyncApproved = true;
            ApplyPendingGuestPresence();

            AddLog(
                "Continuing host file download: " +
                fileCount +
                " file(s), " +
                downloadBytes +
                " byte(s).");
            Changed?.Invoke();
            return true;
        }

        internal static void SetLocalColor(Color color)
        {
            Color normalizedColor = NormalizeColor(color);
            if (ColorsEqual(_color, normalizedColor))
            {
                return;
            }

            _color = normalizedColor;
            _nextSendTime = 0d;
            _hasLastViewportState = false;
            _hasLastSelectionState = false;
            _lastSelectionSignature = string.Empty;
        }

        internal static string[] GetLogs()
        {
            return Logs.ToArray();
        }

        internal static void ReportSceneSyncIssue(string message)
        {
            AddLog(message);
        }

        internal static void ReportPackageVersionReplacement(
            string packageName,
            string localVersion,
            string hostVersion)
        {
            AddLog(
                "Package version differs: " +
                packageName +
                " (" +
                (string.IsNullOrEmpty(localVersion) ? "local version unknown" : localVersion) +
                " -> " +
                hostVersion +
                "). The local package folder will be replaced after Continue.");
            Changed?.Invoke();
        }

        internal static void ReportFileSyncDownloadRequired(
            string[] neededPaths,
            long totalBytes)
        {
            if (neededPaths == null)
            {
                return;
            }

            AddLog(
                "Host file comparison found " +
                neededPaths.Length +
                " file(s) to download (" +
                totalBytes +
                " byte(s)).");
            if (neededPaths.Length > 0)
            {
                AddLog(
                    "Files needed from host:\n" +
                    string.Join("\n", neededPaths));
            }

            Changed?.Invoke();
        }

        internal static UnitySyncRemoteParticipant[] GetRemoteParticipants()
        {
            return IsGuestSyncDeferred
                ? new UnitySyncRemoteParticipant[0]
                : UnitySyncPresenceRoot.GetParticipants();
        }

        internal static bool CanSpectate(Guid playerId)
        {
            if (_transport == null ||
                playerId == Guid.Empty ||
                playerId == LocalPlayerId ||
                !UnitySyncPresenceRoot.TryGetViewport(playerId, out UnitySyncViewportState viewport))
            {
                return false;
            }

            return viewport.SpectatingPlayerId != LocalPlayerId;
        }

        internal static bool StartSpectating(Guid playerId)
        {
            if (!CanSpectate(playerId))
            {
                return false;
            }

            if (_spectatingPlayerId == playerId)
            {
                return true;
            }

            if (_spectatingPlayerId == Guid.Empty ||
                !_hasSavedSceneViewState ||
                _spectatedSceneView == null)
            {
                SceneView sceneView = SceneView.lastActiveSceneView;
                Camera camera = sceneView != null ? sceneView.camera : null;
                if (camera == null)
                {
                    return false;
                }

                _spectatedSceneView = sceneView;
                _savedSceneViewPivot = sceneView.pivot;
                _savedSceneViewRotation = sceneView.rotation;
                _savedSceneViewSize = sceneView.size;
                _savedSceneViewOrthographic = sceneView.orthographic;
                _savedSceneViewFieldOfView = sceneView.cameraSettings.fieldOfView;
                _hasSavedSceneViewState = true;
            }

            _spectatingPlayerId = playerId;
            ForceViewportSend();
            ApplySpectatedSceneView();
            Changed?.Invoke();
            return true;
        }

        internal static void RequestProjectRestore()
        {
            if (_state != UnitySyncSessionState.Connected || UnitySyncFileSynchronizer.IsGuestSyncing)
            {
                return;
            }

            UnitySyncSceneSynchronizer.EndSession();
            UnitySyncProjectSynchronizer.EndSession();
            UnitySyncFileSynchronizer.RequestProjectRestore(_transport);
            AddLog("Requested a full project restore. The host must accept it in Debug.");
            Changed?.Invoke();
        }

        internal static void RespondToProjectRestore(Guid playerId, bool accept)
        {
            if (_state != UnitySyncSessionState.Hosting)
            {
                return;
            }

            UnitySyncFileSynchronizer.RespondToProjectRestore(_transport, LocalPlayerId, playerId, accept);
            Changed?.Invoke();
        }

        internal static void StopSpectating()
        {
            StopSpectatingInternal(true, true);
        }

        private static bool _updatingSession;

        private static void Update()
        {
            // Scene loading and asset import can pump editor callbacks before they return.
            // Keep later snapshot packets queued until the current packet has finished applying.
            if (_updatingSession)
            {
                return;
            }

            _updatingSession = true;
            try
            {
                UpdateSession();
            }
            finally
            {
                _updatingSession = false;
            }
        }

        private static void UpdateSession()
        {
            UpdateSpectatedSceneView();

            UnitySyncTransport transport = _transport;
            if (transport == null)
            {
                return;
            }

            bool disconnected = false;
            while (!EditorApplication.isCompiling &&
                   !EditorApplication.isUpdating &&
                   transport.TryDequeue(out UnitySyncTransportEvent transportEvent))
            {
                switch (transportEvent.Kind)
                {
                    case UnitySyncTransportEventKind.Connected:
                        _state = UnitySyncSessionState.Connected;
                        UnitySyncFileSynchronizer.BeginGuestSync(
                            transport,
                            _guestSyncApproved);
                        AddLog(transportEvent.Message);
                        if (_fileSyncResumeConnectionPending)
                        {
                            _fileSyncResumeConnectionPending = false;
                            ClearFileSyncReloadReconnect();
                            EditorApplication.update -= TryResumeAfterFileSyncReload;
                            AddLog("Resumed UnitySync after synchronized files reloaded.");
                        }

                        AddLog("Comparing host Packages before Assets and scene synchronization.");
                        Changed?.Invoke();
                        break;

                    case UnitySyncTransportEventKind.Disconnected:
                        AddLog(transportEvent.Message);
                        disconnected = true;
                        break;

                    case UnitySyncTransportEventKind.Viewport:
                        if (IsGuestSyncDeferred)
                        {
                            PendingGuestViewports[transportEvent.Viewport.PlayerId] =
                                new PendingGuestViewport
                                {
                                    Viewport = transportEvent.Viewport,
                                    ReceivedAtSeconds = transportEvent.ReceivedAtSeconds
                                };
                            break;
                        }

                        ApplyRemoteViewport(
                            transportEvent.Viewport,
                            transportEvent.ReceivedAtSeconds);
                        break;

                    case UnitySyncTransportEventKind.Selection:
                        if (IsGuestSyncDeferred)
                        {
                            break;
                        }

                        UnitySyncSelectionPresence.Apply(transportEvent.Selection, LocalPlayerId);
                        SceneView.RepaintAll();
                        break;

                    case UnitySyncTransportEventKind.PeerLeft:
                        if (IsGuestSyncDeferred)
                        {
                            PendingGuestViewports.Remove(transportEvent.PlayerId);
                            UnitySyncFileSynchronizer.RemoveHostPlayer(transportEvent.PlayerId);
                            break;
                        }

                        if (_spectatingPlayerId == transportEvent.PlayerId)
                        {
                            StopSpectatingInternal(true, false);
                        }

                        UnitySyncPresenceRoot.Remove(transportEvent.PlayerId);
                        UnitySyncSelectionPresence.Remove(transportEvent.PlayerId);
                        UnitySyncFileSynchronizer.RemoveHostPlayer(transportEvent.PlayerId);
                        SceneView.RepaintAll();
                        Changed?.Invoke();
                        break;

                    case UnitySyncTransportEventKind.FileSync:
                        bool isProjectUpdate =
                            transportEvent.MessageType == UnitySyncMessageType.ProjectFileBegin ||
                            transportEvent.MessageType == UnitySyncMessageType.ProjectFileChunk ||
                            transportEvent.MessageType == UnitySyncMessageType.ProjectFileDelete;
                        if (isProjectUpdate &&
                            (IsGuestSyncDeferred ||
                             UnitySyncFileSynchronizer.IsGuestReconcilingPackages))
                        {
                            break;
                        }

                        string fileSyncError;
                        bool fileHandled = isProjectUpdate
                            ? UnitySyncProjectSynchronizer.HandleMessage(
                                transportEvent.MessageType,
                                transportEvent.PlayerId,
                                transportEvent.FileSync,
                                out fileSyncError)
                            : UnitySyncFileSynchronizer.HandleMessage(
                                transport,
                                LocalPlayerId,
                                transportEvent.MessageType,
                                transportEvent.PlayerId,
                                transportEvent.FileSync,
                                out fileSyncError);
                        if (!fileHandled)
                        {
                            AddLog(
                                (isProjectUpdate ? "Project sync failed: " : "File sync failed: ") +
                                fileSyncError);
                            if (!isProjectUpdate)
                            {
                                disconnected = true;
                            }
                        }
                        else if (!string.IsNullOrEmpty(fileSyncError))
                        {
                            AddLog(fileSyncError);
                            Changed?.Invoke();
                        }
                        break;

                    case UnitySyncTransportEventKind.SceneObjectChange:
                        if (IsGuestSyncDeferred ||
                            UnitySyncFileSynchronizer.IsGuestSyncing)
                        {
                            // The post-file-sync host snapshot supersedes live edits received while
                            // the guest is still reconciling Assets.
                            break;
                        }

                        if (!UnitySyncSceneSynchronizer.ApplyRemoteChange(
                                transportEvent.SceneChange,
                                out string sceneError))
                        {
                            AddLog("Scene sync skipped an update: " + sceneError);
                            Changed?.Invoke();
                        }
                        break;

                    case UnitySyncTransportEventKind.SceneSnapshotBegin:
                        if (IsGuestSyncDeferred || UnitySyncFileSynchronizer.IsGuestSyncing)
                        {
                            break;
                        }

                        if (!UnitySyncSceneSynchronizer.BeginRemoteSnapshot(
                                transportEvent.SceneSnapshot,
                                out string snapshotBeginError))
                        {
                            AddLog("Scene sync could not start a snapshot: " + snapshotBeginError);
                            // No valid boundary exists for the queued body/end packets.
                            // Stop here instead of applying a partial snapshot or flooding the log.
                            StopInternal(false);
                            return;
                        }
                        break;

                    case UnitySyncTransportEventKind.SceneSnapshotEnd:
                        if (IsGuestSyncDeferred || UnitySyncFileSynchronizer.IsGuestSyncing)
                        {
                            break;
                        }

                        if (!UnitySyncSceneSynchronizer.CompleteRemoteSnapshot(
                                transportEvent.SceneSnapshot.SnapshotId,
                                transportEvent.SceneSnapshot.IsComplete,
                                out string snapshotEndError))
                        {
                            AddLog("Scene sync could not finish a snapshot: " + snapshotEndError);
                            Changed?.Invoke();
                        }
                        break;

                    case UnitySyncTransportEventKind.SceneSnapshotRequest:
                        if (IsGuestSyncDeferred)
                        {
                            break;
                        }

                        UnitySyncSceneSynchronizer.QueueFullSceneSnapshot(transportEvent.PlayerId);
                        AddLog("Sending the current scene state to a collaborator.");
                        Changed?.Invoke();
                        break;

                    case UnitySyncTransportEventKind.SceneSettingsChange:
                        if (IsGuestSyncDeferred)
                        {
                            break;
                        }

                        if (!UnitySyncSceneSynchronizer.ApplyRemoteSceneSettings(
                                transportEvent.SceneSnapshot,
                                out string sceneSettingsError))
                        {
                            AddLog("Scene settings sync skipped an update: " + sceneSettingsError);
                            Changed?.Invoke();
                        }
                        else
                        {
                            AddLog("Applied scene environment settings update.");
                            Changed?.Invoke();
                        }
                        break;

                    case UnitySyncTransportEventKind.Log:
                        AddLog(transportEvent.Message);
                        Changed?.Invoke();
                        break;
                }
            }

            if (disconnected)
            {
                bool retryFileSyncResume =
                    _fileSyncResumeConnectionPending &&
                    SessionState.GetBool(FileSyncResumePendingKey, false);
                StopInternal(false);
                if (retryFileSyncResume)
                {
                    _fileSyncResumeConnectionPending = false;
                    _nextFileSyncResumeAttemptTime =
                        EditorApplication.timeSinceStartup + FileSyncResumeRetrySeconds;
                }

                return;
            }

            UnitySyncFileSynchronizer.Update(transport, LocalPlayerId);
            if (UnitySyncFileSynchronizer.ConsumeGuestFailure(out string fileSyncFailure))
            {
                AddLog("File sync failed: " + fileSyncFailure);
                StopInternal(false);
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            if (UnitySyncFileSynchronizer.ConsumeGuestReadyForSceneSnapshot())
            {
                if (!_guestSyncApproved)
                {
                    _guestSyncApproved = true;
                    ApplyPendingGuestPresence();
                }

                UnitySyncSceneSynchronizer.BeginSession();
                UnitySyncProjectSynchronizer.BeginSession();
                transport.RequestSceneSnapshot();
                AddLog("Host Packages and Assets synchronized. Live project sync enabled.");
                Changed?.Invoke();
            }

            bool guestFileSyncing = UnitySyncFileSynchronizer.IsGuestSyncing;
            if (!guestFileSyncing &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                (_state == UnitySyncSessionState.Hosting || _state == UnitySyncSessionState.Connected))
            {
                UnitySyncProjectSynchronizer.Update(transport, LocalPlayerId);
                SendSelectionIfNeeded(transport);
                UnitySyncSceneSynchronizer.Flush(transport, LocalPlayerId);
            }

            if (guestFileSyncing ||
                EditorApplication.timeSinceStartup < _nextSendTime ||
                EditorApplication.isPlayingOrWillChangePlaymode ||
                (_state != UnitySyncSessionState.Hosting && _state != UnitySyncSessionState.Connected))
            {
                return;
            }

            SceneView sceneView =
                _spectatingPlayerId != Guid.Empty && _spectatedSceneView != null
                    ? _spectatedSceneView
                    : SceneView.lastActiveSceneView;
            Camera camera = sceneView != null ? sceneView.camera : null;
            if (camera == null)
            {
                return;
            }

            _nextSendTime = EditorApplication.timeSinceStartup + SendIntervalSeconds;
            UnitySyncViewportState viewport = new UnitySyncViewportState(
                LocalPlayerId,
                _displayName,
                _color,
                camera.transform.position,
                camera.transform.rotation,
                sceneView.pivot,
                sceneView.cameraSettings.fieldOfView,
                camera.aspect,
                camera.orthographic,
                camera.orthographicSize,
                sceneView.size,
                _spectatingPlayerId);
            if (_hasLastViewportState && ViewportStatesEqual(viewport, _lastViewportState))
            {
                return;
            }

            _lastViewportState = viewport;
            _hasLastViewportState = true;
            transport.SendLocalViewport(viewport);
        }

        private static void SendSelectionIfNeeded(UnitySyncTransport transport)
        {
            List<string> objectIds = new List<string>();
            foreach (GameObject selectedObject in Selection.gameObjects)
            {
                if (selectedObject != null &&
                    UnitySyncSceneObjectRegistry.TryGetId(selectedObject, out string objectId) &&
                    !string.IsNullOrEmpty(objectId))
                {
                    objectIds.Add(objectId);
                }
            }

            objectIds.Sort(StringComparer.Ordinal);
            string signature = string.Join("|", objectIds);
            if (_hasLastSelectionState &&
                signature == _lastSelectionSignature &&
                ColorsEqual(_color, _lastSelectionColor))
            {
                return;
            }

            _hasLastSelectionState = true;
            _lastSelectionSignature = signature;
            _lastSelectionColor = _color;
            transport.SendLocalSelection(new UnitySyncSelectionState(
                LocalPlayerId,
                _color,
                objectIds.ToArray()));
        }

        private static bool ViewportStatesEqual(
            UnitySyncViewportState left,
            UnitySyncViewportState right)
        {
            return string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
                   ColorsEqual(left.Color, right.Color) &&
                   (left.Position - right.Position).sqrMagnitude <= 0.0000000001f &&
                   Quaternion.Angle(left.Rotation, right.Rotation) <= 0.0001f &&
                   (left.Pivot - right.Pivot).sqrMagnitude <= 0.0000000001f &&
                   Mathf.Approximately(left.FieldOfView, right.FieldOfView) &&
                   Mathf.Approximately(left.Aspect, right.Aspect) &&
                   left.Orthographic == right.Orthographic &&
                   Mathf.Approximately(left.OrthographicSize, right.OrthographicSize) &&
                   Mathf.Approximately(left.SceneViewSize, right.SceneViewSize) &&
                   left.SpectatingPlayerId == right.SpectatingPlayerId;
        }

        private static void UpdateSpectatedSceneView()
        {
            if (_spectatingPlayerId == Guid.Empty)
            {
                return;
            }

            if (_spectatedSceneView == null ||
                !_hasSavedSceneViewState ||
                !UnitySyncPresenceRoot.TryGetViewport(
                    _spectatingPlayerId,
                    out UnitySyncViewportState viewport))
            {
                StopSpectatingInternal(true, true);
                return;
            }

            if (viewport.SpectatingPlayerId == LocalPlayerId)
            {
                StopSpectatingInternal(true, true);
                return;
            }

            ApplyViewportToSceneView(_spectatedSceneView, viewport);
        }

        private static void ApplySpectatedSceneView()
        {
            if (_spectatingPlayerId == Guid.Empty ||
                _spectatedSceneView == null ||
                !UnitySyncPresenceRoot.TryGetViewport(
                    _spectatingPlayerId,
                    out UnitySyncViewportState viewport))
            {
                return;
            }

            ApplyViewportToSceneView(_spectatedSceneView, viewport);
        }

        private static void ApplyViewportToSceneView(
            SceneView sceneView,
            UnitySyncViewportState viewport)
        {
            Camera camera = sceneView != null ? sceneView.camera : null;
            if (camera == null)
            {
                return;
            }

            float size = Mathf.Max(0.0001f, viewport.SceneViewSize);
            float fieldOfView = Mathf.Clamp(viewport.FieldOfView, 1f, 179f);
            bool matches =
                (sceneView.pivot - viewport.Pivot).sqrMagnitude <= 0.00000001f &&
                Quaternion.Angle(sceneView.rotation, viewport.Rotation) <= 0.001f &&
                Mathf.Approximately(sceneView.size, size) &&
                sceneView.orthographic == viewport.Orthographic &&
                Mathf.Approximately(
                    sceneView.cameraSettings.fieldOfView,
                    fieldOfView);
            if (matches)
            {
                return;
            }

            SceneView.CameraSettings cameraSettings = sceneView.cameraSettings;
            cameraSettings.fieldOfView = fieldOfView;
            sceneView.cameraSettings = cameraSettings;
            sceneView.LookAt(
                viewport.Pivot,
                viewport.Rotation,
                size,
                viewport.Orthographic,
                true);
            sceneView.Repaint();
        }

        private static void StopSpectatingInternal(
            bool forceViewportSend,
            bool notifyChanged)
        {
            bool wasSpectating = _spectatingPlayerId != Guid.Empty;
            _spectatingPlayerId = Guid.Empty;

            if (_hasSavedSceneViewState && _spectatedSceneView != null)
            {
                Camera camera = _spectatedSceneView.camera;
                if (camera != null)
                {
                    SceneView.CameraSettings cameraSettings =
                        _spectatedSceneView.cameraSettings;
                    cameraSettings.fieldOfView = _savedSceneViewFieldOfView;
                    _spectatedSceneView.cameraSettings = cameraSettings;
                    _spectatedSceneView.LookAt(
                        _savedSceneViewPivot,
                        _savedSceneViewRotation,
                        Mathf.Max(0.0001f, _savedSceneViewSize),
                        _savedSceneViewOrthographic,
                        true);
                    _spectatedSceneView.Repaint();
                }
            }

            _spectatedSceneView = null;
            _hasSavedSceneViewState = false;

            if (wasSpectating && forceViewportSend)
            {
                ForceViewportSend();
            }

            if (wasSpectating && notifyChanged)
            {
                Changed?.Invoke();
            }
        }

        private static void ForceViewportSend()
        {
            _nextSendTime = 0d;
            _hasLastViewportState = false;
        }

        private static bool IsGuestSyncDeferred =>
            !string.IsNullOrEmpty(_guestJoinCode) && !_guestSyncApproved;

        private static void ApplyPendingGuestPresence()
        {
            if (PendingGuestViewports.Count == 0)
            {
                return;
            }

            foreach (PendingGuestViewport pending in PendingGuestViewports.Values)
            {
                ApplyRemoteViewport(pending.Viewport, pending.ReceivedAtSeconds);
            }

            PendingGuestViewports.Clear();
        }

        private static void ApplyRemoteViewport(
            UnitySyncViewportState viewport,
            double receivedAtSeconds)
        {
            UnitySyncPresenceRoot.Apply(
                viewport,
                LocalPlayerId,
                receivedAtSeconds);
            if (_spectatingPlayerId == viewport.PlayerId &&
                viewport.SpectatingPlayerId == LocalPlayerId)
            {
                StopSpectatingInternal(true, false);
            }

            SceneView.RepaintAll();
            Changed?.Invoke();
        }

        private static bool ColorsEqual(Color left, Color right)
        {
            return Mathf.Approximately(left.r, right.r) &&
                   Mathf.Approximately(left.g, right.g) &&
                   Mathf.Approximately(left.b, right.b) &&
                   Mathf.Approximately(left.a, right.a);
        }

        private static void StopInternal(bool addLog)
        {
            StopSpectatingInternal(false, false);

            UnitySyncTransport transport = _transport;
            _transport = null;
            transport?.Dispose();

            _state = UnitySyncSessionState.Idle;
            _joinCode = string.Empty;
            _guestJoinCode = string.Empty;
            _guestSyncApproved = false;
            _fileSyncResumeConnectionPending = false;
            PendingGuestViewports.Clear();
            UnitySyncFileSynchronizer.EndSession();
            UnitySyncProjectSynchronizer.EndSession();
            UnitySyncSceneSynchronizer.EndSession();
            UnitySyncPresenceRoot.Clear();
            UnitySyncSelectionPresence.Clear();
            _hasLastViewportState = false;
            _hasLastSelectionState = false;
            _lastSelectionSignature = string.Empty;
            SceneView.RepaintAll();

            if (addLog && transport != null)
            {
                AddLog("Session stopped.");
            }

            Changed?.Invoke();
        }

        private static void Shutdown()
        {
            StopInternal(false);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                StopInternal(true);
            }
        }

        internal static void PrepareFileSyncReloadReconnect()
        {
            if (_state != UnitySyncSessionState.Connected ||
                string.IsNullOrWhiteSpace(_guestJoinCode))
            {
                return;
            }

            SessionState.SetBool(FileSyncResumePendingKey, true);
            SessionState.SetString(FileSyncResumeJoinCodeKey, _guestJoinCode);
            SessionState.SetString(FileSyncResumeDisplayNameKey, _displayName);
            SessionState.SetString(
                FileSyncResumeColorKey,
                "#" + ColorUtility.ToHtmlStringRGB(_color));
            SessionState.SetBool(FileSyncResumeApprovedKey, _guestSyncApproved);
        }

        internal static void ClearFileSyncReloadReconnect()
        {
            SessionState.SetBool(FileSyncResumePendingKey, false);
            SessionState.SetString(FileSyncResumeJoinCodeKey, string.Empty);
            SessionState.SetString(FileSyncResumeDisplayNameKey, string.Empty);
            SessionState.SetString(FileSyncResumeColorKey, string.Empty);
            SessionState.SetBool(FileSyncResumeApprovedKey, false);
        }

        private static void ScheduleFileSyncResume()
        {
            if (!SessionState.GetBool(FileSyncResumePendingKey, false))
            {
                return;
            }

            _fileSyncResumeAttempts = 0;
            _fileSyncResumeConnectionPending = false;
            _nextFileSyncResumeAttemptTime = EditorApplication.timeSinceStartup + 0.25d;
            EditorApplication.update -= TryResumeAfterFileSyncReload;
            EditorApplication.update += TryResumeAfterFileSyncReload;
        }

        private static void TryResumeAfterFileSyncReload()
        {
            if (!SessionState.GetBool(FileSyncResumePendingKey, false))
            {
                EditorApplication.update -= TryResumeAfterFileSyncReload;
                return;
            }

            if (_transport != null ||
                _fileSyncResumeConnectionPending ||
                EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.timeSinceStartup < _nextFileSyncResumeAttemptTime)
            {
                return;
            }

            if (_fileSyncResumeAttempts >= MaximumFileSyncResumeAttempts)
            {
                ClearFileSyncReloadReconnect();
                EditorApplication.update -= TryResumeAfterFileSyncReload;
                AddLog("Could not resume UnitySync after synchronized files reloaded.");
                Changed?.Invoke();
                return;
            }

            string joinCode = SessionState.GetString(FileSyncResumeJoinCodeKey, string.Empty);
            string displayName = SessionState.GetString(
                FileSyncResumeDisplayNameKey,
                "Collaborator");
            string colorText = SessionState.GetString(
                FileSyncResumeColorKey,
                "#FFFFFF");
            Color color = Color.white;
            ColorUtility.TryParseHtmlString(colorText, out color);
            bool resumeApproved = SessionState.GetBool(
                FileSyncResumeApprovedKey,
                false);

            if (string.IsNullOrWhiteSpace(joinCode))
            {
                ClearFileSyncReloadReconnect();
                EditorApplication.update -= TryResumeAfterFileSyncReload;
                return;
            }

            _fileSyncResumeAttempts++;
            if (Connect(joinCode, displayName, color, out string error))
            {
                _guestSyncApproved = resumeApproved;
                _fileSyncResumeConnectionPending = true;
                return;
            }

            if (_fileSyncResumeAttempts >= MaximumFileSyncResumeAttempts)
            {
                ClearFileSyncReloadReconnect();
                EditorApplication.update -= TryResumeAfterFileSyncReload;
                AddLog("Could not resume UnitySync after synchronized files reloaded: " + error);
                Changed?.Invoke();
                return;
            }

            _nextFileSyncResumeAttemptTime =
                EditorApplication.timeSinceStartup + FileSyncResumeRetrySeconds;
        }

        private static Guid LoadOrCreatePlayerId()
        {
            string stored = SessionState.GetString(PlayerIdSessionKey, string.Empty);
            if (Guid.TryParse(stored, out Guid playerId) && playerId != Guid.Empty)
            {
                return playerId;
            }

            playerId = Guid.NewGuid();
            SessionState.SetString(PlayerIdSessionKey, playerId.ToString("D"));
            return playerId;
        }

        private static string NormalizeDisplayName(string displayName)
        {
            string value = string.IsNullOrWhiteSpace(displayName) ? Environment.UserName : displayName.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                value = "Collaborator";
            }

            return value.Length <= 32 ? value : value.Substring(0, 32);
        }

        private static Color NormalizeColor(Color color)
        {
            return new Color(
                Mathf.Clamp01(color.r),
                Mathf.Clamp01(color.g),
                Mathf.Clamp01(color.b),
                1f);
        }

        private static Color ColorFor(Guid playerId)
        {
            byte[] bytes = playerId.ToByteArray();
            int hash = 17;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash = unchecked(hash * 31 + bytes[i]);
            }

            float hue = (uint)hash % 360u / 360f;
            return Color.HSVToRGB(hue, 0.72f, 1f);
        }

        private static void AddLog(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            Logs.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            if (Logs.Count > 20)
            {
                Logs.RemoveAt(0);
            }
        }
    }
}
