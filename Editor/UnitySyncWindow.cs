using System;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal sealed class UnitySyncWindow : EditorWindow
    {
        private const string DisplayNamePreference = "Glasspage.UnitySync.DisplayName";
        private const string ColorPreference = "Glasspage.UnitySync.Color";
        private const string HostAddressPreference = "Glasspage.UnitySync.HostAddress";
        private const string PortPreference = "Glasspage.UnitySync.Port";
        private const int DefaultPort = 47832;

        private static readonly Color ActiveSessionColor = new Color(1f, 0.55f, 0.15f);

        private string _displayName;
        private Color _color;
        private string _hostAddress;
        private int _port;
        private string _joinCodeInput = string.Empty;
        private string _error = string.Empty;
        private Vector2 _scroll;
        private bool _showHostControls;
        private bool _showJoinControls;
        private bool _showHostingControls = true;
        private bool _showJoinedControls = true;
        private bool _showActivityLog;
        private bool _showDebug;
        private string _debugDisplayName = "Debug User";
        private Color _debugColor;
        private UnitySyncSessionState _previousSessionState;

        private static GUIStyle _activeSessionFoldoutStyle;

        [MenuItem("UnitySync/Session", false, 0)]
        private static void Open()
        {
            UnitySyncWindow window = GetWindow<UnitySyncWindow>();
            window.titleContent = new GUIContent("UnitySync v0.5.0");
            window.minSize = new Vector2(360f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("UnitySync v0.5.0");

            _displayName = EditorPrefs.GetString(DisplayNamePreference, Environment.UserName);
            if (string.IsNullOrWhiteSpace(_displayName))
            {
                _displayName = "Collaborator";
            }

            _color = LoadColor();
            _debugColor = _color;

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
            UpdateActiveFoldoutState();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("UnitySync", EditorStyles.boldLabel);
            EditorGUILayout.Space(8f);

            DrawStatus();
            EditorGUILayout.Space(8f);

            DrawIdentity();
            EditorGUILayout.Space(10f);

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
            DrawActivityLog();
            EditorGUILayout.Space(12f);
            DrawDebug();
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
                    status = UnitySyncSession.IsFileSyncing
                        ? "Syncing host files..."
                        : "Connected";
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
            using (new EditorGUI.DisabledScope(UnitySyncSession.IsActive))
            {
                EditorGUI.BeginChangeCheck();
                _displayName = EditorGUILayout.TextField(new GUIContent("Username", "Shown above your viewport gizmo."), _displayName);
                if (EditorGUI.EndChangeCheck())
                {
                    if (_displayName.Length > 32)
                    {
                        _displayName = _displayName.Substring(0, 32);
                    }

                    EditorPrefs.SetString(DisplayNamePreference, _displayName);
                }
            }

            EditorGUI.BeginChangeCheck();
            _color = EditorGUILayout.ColorField(
                new GUIContent("Color", "Used for your viewport marker on other collaborators' screens."),
                _color,
                true,
                false,
                false);
            if (EditorGUI.EndChangeCheck())
            {
                _color = NormalizeColor(_color);
                EditorPrefs.SetString(ColorPreference, "#" + ColorUtility.ToHtmlStringRGB(_color));
                UnitySyncSession.SetLocalColor(_color);
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
                        if (!UnitySyncSession.StartHost(_hostAddress, _port, _displayName, _color, out _error))
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
                        if (!UnitySyncSession.Connect(_joinCodeInput, _displayName, _color, out _error))
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
            bool isHosting = UnitySyncSession.State == UnitySyncSessionState.Hosting;
            string categoryName = isHosting
                ? "Hosting a session"
                : UnitySyncSession.State == UnitySyncSessionState.Connecting
                    ? "Joining a session"
                    : "In a session";
            bool expanded = isHosting ? _showHostingControls : _showJoinedControls;
            expanded = EditorGUILayout.Foldout(
                expanded,
                categoryName,
                true,
                ActiveSessionFoldoutStyle);

            if (isHosting)
            {
                _showHostingControls = expanded;
            }
            else
            {
                _showJoinedControls = expanded;
            }

            if (!expanded)
            {
                return;
            }

            EditorGUI.indentLevel++;
            if (isHosting)
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

            EditorGUI.indentLevel--;
        }

        private void UpdateActiveFoldoutState()
        {
            UnitySyncSessionState state = UnitySyncSession.State;
            if (state == _previousSessionState)
            {
                return;
            }

            if (state == UnitySyncSessionState.Hosting)
            {
                _showHostingControls = true;
            }
            else if (state == UnitySyncSessionState.Connecting ||
                     (state == UnitySyncSessionState.Connected &&
                      _previousSessionState != UnitySyncSessionState.Connecting))
            {
                _showJoinedControls = true;
            }

            _previousSessionState = state;
        }

        private static Color LoadColor()
        {
            string stored = EditorPrefs.GetString(ColorPreference, string.Empty);
            if (!string.IsNullOrEmpty(stored) && ColorUtility.TryParseHtmlString(stored, out Color color))
            {
                return NormalizeColor(color);
            }

            return UnitySyncSession.DefaultColor;
        }

        private static Color NormalizeColor(Color color)
        {
            return new Color(
                Mathf.Clamp01(color.r),
                Mathf.Clamp01(color.g),
                Mathf.Clamp01(color.b),
                1f);
        }

        private static GUIStyle ActiveSessionFoldoutStyle
        {
            get
            {
                if (_activeSessionFoldoutStyle == null)
                {
                    _activeSessionFoldoutStyle = new GUIStyle(EditorStyles.foldout);
                    SetTextColor(_activeSessionFoldoutStyle.normal, ActiveSessionColor);
                    SetTextColor(_activeSessionFoldoutStyle.hover, ActiveSessionColor);
                    SetTextColor(_activeSessionFoldoutStyle.active, ActiveSessionColor);
                    SetTextColor(_activeSessionFoldoutStyle.focused, ActiveSessionColor);
                    SetTextColor(_activeSessionFoldoutStyle.onNormal, ActiveSessionColor);
                    SetTextColor(_activeSessionFoldoutStyle.onHover, ActiveSessionColor);
                    SetTextColor(_activeSessionFoldoutStyle.onActive, ActiveSessionColor);
                    SetTextColor(_activeSessionFoldoutStyle.onFocused, ActiveSessionColor);
                }

                return _activeSessionFoldoutStyle;
            }
        }

        private static void SetTextColor(GUIStyleState state, Color color)
        {
            state.textColor = color;
        }

        private void DrawParticipants()
        {
            EditorGUILayout.LabelField("Collaborators", EditorStyles.boldLabel);
            DrawParticipantLabel(_displayName + " (you)", _color);

            UnitySyncRemoteParticipant[] participants = UnitySyncSession.GetRemoteParticipants();
            foreach (UnitySyncRemoteParticipant participant in participants)
            {
                DrawRemoteParticipant(participant);
            }
        }

        private static void DrawRemoteParticipant(UnitySyncRemoteParticipant participant)
        {
            bool isSpectating =
                UnitySyncSession.SpectatingPlayerId == participant.PlayerId;
            bool canSpectate = UnitySyncSession.CanSpectate(participant.PlayerId);

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawParticipantLabel(participant.DisplayName, participant.Color);

                using (new EditorGUI.DisabledScope(!isSpectating && !canSpectate))
                {
                    if (GUILayout.Button(
                            isSpectating ? "Stop Spectating" : "Spectate",
                            GUILayout.Width(110f)))
                    {
                        if (isSpectating)
                        {
                            UnitySyncSession.StopSpectating();
                        }
                        else
                        {
                            UnitySyncSession.StartSpectating(participant.PlayerId);
                        }
                    }
                }
            }
        }

        private static void DrawParticipantLabel(string displayName, Color color)
        {
            GUIStyle style = new GUIStyle(EditorStyles.label);
            SetTextColor(style.normal, color);
            SetTextColor(style.hover, color);
            SetTextColor(style.active, color);
            SetTextColor(style.focused, color);
            SetTextColor(style.onNormal, color);
            SetTextColor(style.onHover, color);
            SetTextColor(style.onActive, color);
            SetTextColor(style.onFocused, color);
            EditorGUILayout.LabelField("• " + displayName, style);
        }

        private void DrawActivityLog()
        {
            _showActivityLog = EditorGUILayout.Foldout(_showActivityLog, "Activity Log", true);
            if (!_showActivityLog)
            {
                return;
            }

            EditorGUI.indentLevel++;
            string[] logs = UnitySyncSession.GetLogs();
            if (logs.Length == 0)
            {
                EditorGUILayout.LabelField("No activity yet", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
                return;
            }

            int first = Mathf.Max(0, logs.Length - 8);
            for (int i = first; i < logs.Length; i++)
            {
                EditorGUILayout.LabelField(logs[i], EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUI.indentLevel--;
        }

        private void DrawDebug()
        {
            _showDebug = EditorGUILayout.Foldout(_showDebug, "Debug", true);
            if (!_showDebug)
            {
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            _debugDisplayName = EditorGUILayout.TextField(
                new GUIContent("Username", "Username shown above the test viewport gizmo."),
                _debugDisplayName);
            _debugColor = EditorGUILayout.ColorField(
                new GUIContent("Color", "Color used by the test viewport gizmo."),
                _debugColor,
                true,
                false,
                false);
            if (EditorGUI.EndChangeCheck())
            {
                _debugDisplayName = NormalizeDebugDisplayName(_debugDisplayName);
                _debugColor = NormalizeColor(_debugColor);
                UnitySyncPresenceRoot.UpdateDebugMarkerAppearance(_debugDisplayName, _debugColor);
            }

            bool debugMarkerVisible = UnitySyncPresenceRoot.DebugMarkerVisible;
            if (GUILayout.Button(debugMarkerVisible ? "Destroy Viewport Gizmo" : "Summon Viewport Gizmo"))
            {
                if (debugMarkerVisible)
                {
                    UnitySyncPresenceRoot.DestroyDebugMarker();
                }
                else
                {
                    _debugDisplayName = NormalizeDebugDisplayName(_debugDisplayName);
                    _debugColor = NormalizeColor(_debugColor);
                    UnitySyncPresenceRoot.SummonDebugMarker(_debugDisplayName, _debugColor);
                }

                Repaint();
            }

            EditorGUILayout.LabelField(
                "The test gizmo is placed at the current Scene view pivot.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUI.indentLevel--;
        }

        private static string NormalizeDebugDisplayName(string displayName)
        {
            string value = string.IsNullOrWhiteSpace(displayName) ? "Debug User" : displayName.Trim();
            return value.Length <= 32 ? value : value.Substring(0, 32);
        }
    }
}
