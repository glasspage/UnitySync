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
        private const double SelectionHeartbeatSeconds = 1.0;
        private const string PlayerIdSessionKey = "Glasspage.UnitySync.PlayerId";
        private const string FileSyncResumePendingKey = "Glasspage.UnitySync.FileSyncResume.Pending";
        private const string FileSyncResumeJoinCodeKey = "Glasspage.UnitySync.FileSyncResume.JoinCode";
        private const string FileSyncResumeDisplayNameKey = "Glasspage.UnitySync.FileSyncResume.DisplayName";
        private const string FileSyncResumeColorKey = "Glasspage.UnitySync.FileSyncResume.Color";
        private const int MaximumFileSyncResumeAttempts = 8;
        private const double FileSyncResumeRetrySeconds = 0.5d;

        private static readonly Guid LocalPlayerId;
        private static readonly List<string> Logs = new List<string>();

        private static UnitySyncTransport _transport;
        private static UnitySyncSessionState _state;
        private static string _displayName = "Collaborator";
        private static Color _color = Color.white;
        private static string _joinCode = string.Empty;
        private static string _guestJoinCode = string.Empty;
        private static double _nextSendTime;
        private static double _nextSelectionSendTime;
        private static string _lastSelectionSignature = string.Empty;
        private static int _fileSyncResumeAttempts;
        private static double _nextFileSyncResumeAttemptTime;

        internal static event Action Changed;

        internal static UnitySyncSessionState State => _state;
        internal static string JoinCode => _joinCode;
        internal static bool IsActive => _transport != null;
        internal static bool IsFileSyncing => UnitySyncFileSynchronizer.IsGuestSyncing;
        internal static Color DefaultColor => ColorFor(LocalPlayerId);

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
                ClearFileSyncReloadReconnect();
                _state = UnitySyncSessionState.Hosting;
                _nextSendTime = 0d;
                _nextSelectionSendTime = 0d;
                _lastSelectionSignature = string.Empty;
                UnitySyncSceneSynchronizer.BeginSession();
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
                _state = UnitySyncSessionState.Connecting;
                _nextSendTime = 0d;
                _nextSelectionSendTime = 0d;
                _lastSelectionSignature = string.Empty;
                UnitySyncSceneSynchronizer.BeginSession();
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

        internal static void SetLocalColor(Color color)
        {
            _color = NormalizeColor(color);
            _nextSendTime = 0d;
            _nextSelectionSendTime = 0d;
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

        internal static string[] GetParticipantNames()
        {
            return UnitySyncPresenceRoot.GetParticipantNames();
        }

        private static void Update()
        {
            UnitySyncTransport transport = _transport;
            if (transport == null)
            {
                return;
            }

            bool disconnected = false;
            while (transport.TryDequeue(out UnitySyncTransportEvent transportEvent))
            {
                switch (transportEvent.Kind)
                {
                    case UnitySyncTransportEventKind.Connected:
                        _state = UnitySyncSessionState.Connected;
                        UnitySyncFileSynchronizer.BeginGuestSync(transport);
                        AddLog(transportEvent.Message);
                        AddLog("Comparing host Assets before scene synchronization.");
                        Changed?.Invoke();
                        break;

                    case UnitySyncTransportEventKind.Disconnected:
                        AddLog(transportEvent.Message);
                        disconnected = true;
                        break;

                    case UnitySyncTransportEventKind.Viewport:
                        UnitySyncPresenceRoot.Apply(transportEvent.Viewport, LocalPlayerId);
                        SceneView.RepaintAll();
                        Changed?.Invoke();
                        break;

                    case UnitySyncTransportEventKind.Selection:
                        UnitySyncSelectionPresence.Apply(transportEvent.Selection, LocalPlayerId);
                        SceneView.RepaintAll();
                        break;

                    case UnitySyncTransportEventKind.PeerLeft:
                        UnitySyncPresenceRoot.Remove(transportEvent.PlayerId);
                        UnitySyncSelectionPresence.Remove(transportEvent.PlayerId);
                        UnitySyncFileSynchronizer.RemoveHostPlayer(transportEvent.PlayerId);
                        SceneView.RepaintAll();
                        Changed?.Invoke();
                        break;

                    case UnitySyncTransportEventKind.FileSync:
                        if (!UnitySyncFileSynchronizer.HandleMessage(
                                transport,
                                LocalPlayerId,
                                transportEvent.MessageType,
                                transportEvent.PlayerId,
                                transportEvent.FileSync,
                                out string fileSyncError))
                        {
                            AddLog("File sync failed: " + fileSyncError);
                            disconnected = true;
                        }
                        else if (!string.IsNullOrEmpty(fileSyncError))
                        {
                            AddLog(fileSyncError);
                            Changed?.Invoke();
                        }
                        break;

                    case UnitySyncTransportEventKind.SceneObjectChange:
                        if (UnitySyncFileSynchronizer.IsGuestSyncing)
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
                        if (!UnitySyncSceneSynchronizer.BeginRemoteSnapshot(
                                transportEvent.SceneSnapshot,
                                out string snapshotBeginError))
                        {
                            AddLog("Scene sync skipped a snapshot: " + snapshotBeginError);
                            Changed?.Invoke();
                        }
                        break;

                    case UnitySyncTransportEventKind.SceneSnapshotEnd:
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
                        UnitySyncSceneSynchronizer.QueueFullSceneSnapshot(transportEvent.PlayerId);
                        AddLog("Sending the current scene state to a collaborator.");
                        Changed?.Invoke();
                        break;

                    case UnitySyncTransportEventKind.Log:
                        AddLog(transportEvent.Message);
                        Changed?.Invoke();
                        break;
                }
            }

            if (disconnected)
            {
                StopInternal(false);
                return;
            }

            UnitySyncFileSynchronizer.Update(transport, LocalPlayerId);
            if (UnitySyncFileSynchronizer.ConsumeGuestFailure(out string fileSyncFailure))
            {
                AddLog("File sync failed: " + fileSyncFailure);
                StopInternal(false);
                return;
            }

            if (UnitySyncFileSynchronizer.ConsumeGuestReadyForSceneSnapshot())
            {
                UnitySyncSceneSynchronizer.BeginSession();
                transport.RequestSceneSnapshot();
                AddLog("Host Assets synchronized. Requesting the current scene state.");
                Changed?.Invoke();
            }

            bool guestFileSyncing = UnitySyncFileSynchronizer.IsGuestSyncing;
            if (!guestFileSyncing &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                (_state == UnitySyncSessionState.Hosting || _state == UnitySyncSessionState.Connected))
            {
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

            SceneView sceneView = SceneView.lastActiveSceneView;
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
                camera.fieldOfView,
                camera.aspect,
                camera.orthographic,
                camera.orthographicSize);
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
            double now = EditorApplication.timeSinceStartup;
            if (signature == _lastSelectionSignature && now < _nextSelectionSendTime)
            {
                return;
            }

            _lastSelectionSignature = signature;
            _nextSelectionSendTime = now + SelectionHeartbeatSeconds;
            transport.SendLocalSelection(new UnitySyncSelectionState(
                LocalPlayerId,
                _color,
                objectIds.ToArray()));
        }

        private static void StopInternal(bool addLog)
        {
            UnitySyncTransport transport = _transport;
            _transport = null;
            transport?.Dispose();

            _state = UnitySyncSessionState.Idle;
            _joinCode = string.Empty;
            _guestJoinCode = string.Empty;
            UnitySyncFileSynchronizer.EndSession();
            UnitySyncSceneSynchronizer.EndSession();
            UnitySyncPresenceRoot.Clear();
            UnitySyncSelectionPresence.Clear();
            _lastSelectionSignature = string.Empty;
            _nextSelectionSendTime = 0d;
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
        }

        internal static void ClearFileSyncReloadReconnect()
        {
            SessionState.SetBool(FileSyncResumePendingKey, false);
            SessionState.SetString(FileSyncResumeJoinCodeKey, string.Empty);
            SessionState.SetString(FileSyncResumeDisplayNameKey, string.Empty);
            SessionState.SetString(FileSyncResumeColorKey, string.Empty);
        }

        private static void ScheduleFileSyncResume()
        {
            if (!SessionState.GetBool(FileSyncResumePendingKey, false))
            {
                return;
            }

            _fileSyncResumeAttempts = 0;
            _nextFileSyncResumeAttemptTime = EditorApplication.timeSinceStartup + 0.25d;
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
                EditorApplication.timeSinceStartup < _nextFileSyncResumeAttemptTime)
            {
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

            if (string.IsNullOrWhiteSpace(joinCode))
            {
                ClearFileSyncReloadReconnect();
                EditorApplication.update -= TryResumeAfterFileSyncReload;
                return;
            }

            _fileSyncResumeAttempts++;
            if (Connect(joinCode, displayName, color, out string error))
            {
                ClearFileSyncReloadReconnect();
                EditorApplication.update -= TryResumeAfterFileSyncReload;
                AddLog("Resuming UnitySync after synchronized scripts reloaded.");
                Changed?.Invoke();
                return;
            }

            if (_fileSyncResumeAttempts >= MaximumFileSyncResumeAttempts)
            {
                ClearFileSyncReloadReconnect();
                EditorApplication.update -= TryResumeAfterFileSyncReload;
                AddLog("Could not resume UnitySync after synchronized scripts reloaded: " + error);
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
