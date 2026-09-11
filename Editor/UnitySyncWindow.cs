using System;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal sealed class UnitySyncWindow : EditorWindow
    {
        private const string DisplayNamePreference = "Glasspage.UnitySync.DisplayName";
        private const string HostAddressPreference = "Glasspage.UnitySync.HostAddress";
        private const string PortPreference = "Glasspage.UnitySync.Port";
        private const int DefaultPort = 47832;

        private string _displayName;
        private string _hostAddress;
        private int _port;
        private string _joinCodeInput = string.Empty;
        private string _error = string.Empty;
        private Vector2 _scroll;
        private bool _showHostControls;
        private bool _showJoinControls;

        [MenuItem("UnitySync/Session", false, 0)]
        private static void Open()
        {
            UnitySyncWindow window = GetWindow<UnitySyncWindow>();
            window.titleContent = new GUIContent("UnitySync");
            window.minSize = new Vector2(360f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            _displayName = EditorPrefs.GetString(DisplayNamePreference, Environment.UserName);
            if (string.IsNullOrWhiteSpace(_displayName))
            {
                _displayName = "Collaborator";
            }

            _hostAddress = EditorPrefs.GetString(HostAddressPreference, string.Empty);
            if (string.IsNullOrWhiteSpace(_hostAddress))
            {
                _hostAddress = UnitySyncJoinCode.FindSuggestedAddress();
            }

            _port = EditorPrefs.GetInt(PortPreference, DefaultPort);
            UnitySyncSession.Changed += Repaint;
        }

        private void OnDisable()
        {
            UnitySyncSession.Changed -= Repaint;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("UnitySync", EditorStyles.boldLabel);
            EditorGUILayout.Space(8f);

            DrawStatus();
            EditorGUILayout.Space(8f);

            using (new EditorGUI.DisabledScope(UnitySyncSession.IsActive))
            {
                DrawIdentity();
                EditorGUILayout.Space(10f);
            }

            if (UnitySyncSession.State == UnitySyncSessionState.Idle)
            {
                DrawIdleControls();
            }
            else
            {
                DrawActiveControls();
            }

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.Space(8f);
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }

            EditorGUILayout.Space(14f);
            DrawParticipants();
            EditorGUILayout.Space(12f);
            DrawActivity();
            EditorGUILayout.Space(8f);

            EditorGUILayout.EndScrollView();
        }

        private void DrawStatus()
        {
            string status;
            MessageType type;
            switch (UnitySyncSession.State)
            {
                case UnitySyncSessionState.Hosting:
                    status = "Hosting";
                    type = MessageType.Info;
                    break;

                case UnitySyncSessionState.Connecting:
                    status = "Connecting...";
                    type = MessageType.Info;
                    break;

                case UnitySyncSessionState.Connected:
                    status = "Connected";
                    type = MessageType.Info;
                    break;

                default:
                    status = "Not connected";
                    type = MessageType.None;
                    break;
            }

            EditorGUILayout.HelpBox(status, type);
        }

        private void DrawIdentity()
        {
            EditorGUI.BeginChangeCheck();
            _displayName = EditorGUILayout.TextField(new GUIContent("Display name", "Shown above your viewport gizmo."), _displayName);
            if (EditorGUI.EndChangeCheck())
            {
                if (_displayName.Length > 32)
                {
                    _displayName = _displayName.Substring(0, 32);
                }

                EditorPrefs.SetString(DisplayNamePreference, _displayName);
            }
        }

        private void DrawIdleControls()
        {
            _showHostControls = EditorGUILayout.Foldout(_showHostControls, "Host a session", true);
            if (_showHostControls)
            {
                EditorGUI.indentLevel++;

                EditorGUI.BeginChangeCheck();
                _hostAddress = EditorGUILayout.TextField(
                    new GUIContent("Host address", "The reachable IPv4 address placed in the join code. For Radmin VPN this is normally the host's 26.x.x.x address."),
                    _hostAddress);
                _port = EditorGUILayout.IntField(new GUIContent("Port", "TCP port used by UnitySync."), _port);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(HostAddressPreference, _hostAddress);
                    EditorPrefs.SetInt(PortPreference, _port);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Use Suggested Address"))
                    {
                        _hostAddress = UnitySyncJoinCode.FindSuggestedAddress();
                        EditorPrefs.SetString(HostAddressPreference, _hostAddress);
                    }

                    if (GUILayout.Button("Start Hosting"))
                    {
                        _error = string.Empty;
                        if (!UnitySyncSession.StartHost(_hostAddress, _port, _displayName, out _error))
                        {
                            Repaint();
                        }
                    }
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8f);
            _showJoinControls = EditorGUILayout.Foldout(_showJoinControls, "Join a session", true);
            if (_showJoinControls)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("Join code", EditorStyles.miniLabel);
                _joinCodeInput = EditorGUILayout.TextArea(_joinCodeInput, GUILayout.MinHeight(54f));

                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_joinCodeInput)))
                {
                    if (GUILayout.Button("Connect"))
                    {
                        _error = string.Empty;
                        if (!UnitySyncSession.Connect(_joinCodeInput, _displayName, out _error))
                        {
                            Repaint();
                        }
                    }
                }

                EditorGUI.indentLevel--;
            }
        }

        private void DrawActiveControls()
        {
            if (UnitySyncSession.State == UnitySyncSessionState.Hosting)
            {
                EditorGUILayout.LabelField("Share this join code", EditorStyles.boldLabel);
                GUIStyle codeStyle = new GUIStyle(EditorStyles.textArea)
                {
                    wordWrap = true
                };
                EditorGUILayout.SelectableLabel(UnitySyncSession.JoinCode, codeStyle, GUILayout.MinHeight(54f));

                if (GUILayout.Button("Copy Join Code"))
                {
                    GUIUtility.systemCopyBuffer = UnitySyncSession.JoinCode;
                }

                EditorGUILayout.HelpBox(
                    "UnitySync listens on all local interfaces, but collaborators will connect to the address encoded in this code.",
                    MessageType.Info);
            }

            EditorGUILayout.Space(8f);
            if (GUILayout.Button("Stop Session"))
            {
                _error = string.Empty;
                UnitySyncSession.Stop();
            }
        }

        private static void DrawParticipants()
        {
            EditorGUILayout.LabelField("Remote collaborators", EditorStyles.boldLabel);
            string[] names = UnitySyncSession.GetParticipantNames();
            if (names.Length == 0)
            {
                EditorGUILayout.LabelField("None", EditorStyles.miniLabel);
                return;
            }

            foreach (string participantName in names)
            {
                EditorGUILayout.LabelField("• " + participantName);
            }
        }

        private static void DrawActivity()
        {
            EditorGUILayout.LabelField("Activity", EditorStyles.boldLabel);
            string[] logs = UnitySyncSession.GetLogs();
            if (logs.Length == 0)
            {
                EditorGUILayout.LabelField("No activity yet", EditorStyles.miniLabel);
                return;
            }

            int first = Mathf.Max(0, logs.Length - 8);
            for (int i = first; i < logs.Length; i++)
            {
                EditorGUILayout.LabelField(logs[i], EditorStyles.wordWrappedMiniLabel);
            }
        }
    }
}
