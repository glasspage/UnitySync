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
        private static string _joinCode = string.Empty;
        private static double _nextSendTime;

        internal static event Action Changed;

        internal static UnitySyncSessionState State => _state;
        internal static string JoinCode => _joinCode;
        internal static bool IsActive => _transport != null;

        static UnitySyncSession()
        {
            LocalPlayerId = LoadOrCreatePlayerId();
            EditorApplication.update += Update;
            EditorApplication.quitting += Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        internal static bool StartHost(string advertisedAddress, int port, string displayName, out string error)
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
                _transport = new UnitySyncTransport(LocalPlayerId, _displayName, secret);
                _transport.StartHost(port);
                _joinCode = code;
                _state = UnitySyncSessionState.Hosting;
                _nextSendTime = 0d;
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

        internal static bool Connect(string joinCode, string displayName, out string error)
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
                _transport = new UnitySyncTransport(LocalPlayerId, _displayName, data.Secret);
                _transport.StartClient(data.Address, data.Port);
                _state = UnitySyncSessionState.Connecting;
                _nextSendTime = 0d;
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
