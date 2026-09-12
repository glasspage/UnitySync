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
        private const string PlayerIdPreference = "Glasspage.UnitySync.PlayerId";

        private static readonly Guid LocalPlayerId;
        private static readonly List<string> Logs = new List<string>();

        private static UnitySyncTransport _transport;
        private static UnitySyncSessionState _state;
        private static string _displayName = "Collaborator";
        private static Color _color = Color.white;
        private static string _joinCode = string.Empty;
        private static double _nextSendTime;

        internal static event Action Changed;

        internal static UnitySyncSessionState State => _state;
        internal static string JoinCode => _joinCode;
        internal static bool IsActive => _transport != null;
        internal static Color DefaultColor => ColorFor(LocalPlayerId);

        static UnitySyncSession()
        {
            LocalPlayerId = LoadOrCreatePlayerId();
            EditorApplication.update += Update;
            EditorApplication.quitting += Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
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
                _state = UnitySyncSessionState.Hosting;
                _nextSendTime = 0d;
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
                _state = UnitySyncSessionState.Connecting;
                _nextSendTime = 0d;
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
        }

        internal static string[] GetLogs()
        {
            return Logs.ToArray();
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
                        transport.RequestSceneSnapshot();
                        AddLog(transportEvent.Message);
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

                    case UnitySyncTransportEventKind.PeerLeft:
                        UnitySyncPresenceRoot.Remove(transportEvent.PlayerId);
                        SceneView.RepaintAll();
                        Changed?.Invoke();
                        break;

                    case UnitySyncTransportEventKind.SceneObjectChange:
                        if (!UnitySyncSceneSynchronizer.ApplyRemoteChange(
                                transportEvent.SceneChange,
                                out string sceneError))
                        {
                            AddLog("Scene sync skipped an update: " + sceneError);
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

            if (!EditorApplication.isPlayingOrWillChangePlaymode &&
                (_state == UnitySyncSessionState.Hosting || _state == UnitySyncSessionState.Connected))
            {
                UnitySyncSceneSynchronizer.Flush(transport, LocalPlayerId);
            }

            if (EditorApplication.timeSinceStartup < _nextSendTime ||
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

        private static void StopInternal(bool addLog)
        {
            UnitySyncTransport transport = _transport;
            _transport = null;
            transport?.Dispose();

            _state = UnitySyncSessionState.Idle;
            _joinCode = string.Empty;
            UnitySyncSceneSynchronizer.EndSession();
            UnitySyncPresenceRoot.Clear();
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

        private static Guid LoadOrCreatePlayerId()
        {
            string stored = EditorPrefs.GetString(PlayerIdPreference, string.Empty);
            if (Guid.TryParse(stored, out Guid playerId) && playerId != Guid.Empty)
            {
                return playerId;
            }

            playerId = Guid.NewGuid();
            EditorPrefs.SetString(PlayerIdPreference, playerId.ToString("D"));
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
