using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
#if UNITY_EDITOR_WIN
using Microsoft.Win32;
#endif

namespace Glasspage.UnitySync
{
    internal sealed class UnitySyncWindow : EditorWindow
    {
        private const string DisplayNamePreference = "Glasspage.UnitySync.DisplayName";
        private const string ColorPreference = "Glasspage.UnitySync.Color";
        private const string HostAddressPreference = "Glasspage.UnitySync.HostAddress";
        private const string PortPreference = "Glasspage.UnitySync.Port";
        // Keep this in sync with package.json when releasing a new UnitySync version.
        private const string Version = "0.7.0";
        private const string HeaderTitle = "UnitySync v" + Version;
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
            window.titleContent = new GUIContent("UnitySync");
            window.minSize = new Vector2(360f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("UnitySync");

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
            EditorGUILayout.LabelField(HeaderTitle, EditorStyles.boldLabel);
            EditorGUILayout.Space(8f);

            DrawStatus();
            DrawPackageChecklist();
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
                    status = UnitySyncFileSynchronizer.IsWaitingForPackageChanges
                        ? "Package changes required"
                        : UnitySyncSession.IsAwaitingFileDownloadConfirmation
                        ? "Review host file download"
                        : UnitySyncSession.IsFileSyncing
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

        private void DrawPackageChecklist()
        {
            if (!UnitySyncFileSynchronizer.IsWaitingForPackageChanges)
            {
                return;
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Match the host's packages", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                UnitySyncFileSynchronizer.PackageChecklistOrderHint,
                MessageType.Info);
            UnitySyncFileSynchronizer.PackageChecklistItem[] items =
                UnitySyncFileSynchronizer.RequiredPackageChecklistItems;
            foreach (UnitySyncFileSynchronizer.PackageChecklistItem item in items)
            {
                EditorGUILayout.HelpBox(item.Text, MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(GetPackageActionLabel(item.Action)))
                    {
                        OpenPackageHelper(item.Action, item.PackageName);
                    }
                    if (item.ShowAdditionalWebSearch && GUILayout.Button("Search Google"))
                    {
                        OpenPackageHelper(
                            UnitySyncFileSynchronizer.PackageChecklistAction.WebSearch,
                            item.PackageName);
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Copy checklist"))
                {
                    string[] changes = UnitySyncFileSynchronizer.RequiredPackageChanges;
                    GUIUtility.systemCopyBuffer =
                        UnitySyncFileSynchronizer.PackageChecklistOrderHint +
                        Environment.NewLine + Environment.NewLine +
                        string.Join(Environment.NewLine + Environment.NewLine, changes);
                }
                using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || EditorApplication.isUpdating))
                {
                    if (GUILayout.Button("Recheck packages"))
                    {
                        UnitySyncSession.RecheckPackageVersions();
                    }
                }
            }
        }

        private static string GetPackageActionLabel(
            UnitySyncFileSynchronizer.PackageChecklistAction action)
        {
            switch (action)
            {
                case UnitySyncFileSynchronizer.PackageChecklistAction.CreatorCompanion:
                    return "Open Creator Companion";
                case UnitySyncFileSynchronizer.PackageChecklistAction.UnityPackageManager:
                    return "Open Package Manager";
                default:
                    return "Search Google";
            }
        }

        private void OpenPackageHelper(
            UnitySyncFileSynchronizer.PackageChecklistAction action,
            string packageName)
        {
            _error = string.Empty;
            switch (action)
            {
                case UnitySyncFileSynchronizer.PackageChecklistAction.CreatorCompanion:
                    if (!OpenCreatorCompanion())
                    {
                        _error = "Creator Companion could not be found. Open it manually or reinstall it, then try again.";
                    }
                    break;
                case UnitySyncFileSynchronizer.PackageChecklistAction.UnityPackageManager:
                    if (!EditorApplication.ExecuteMenuItem("Window/Package Manager"))
                    {
                        _error = "Unity Package Manager could not be opened.";
                    }
                    break;
                default:
                    Application.OpenURL(
                        "https://www.google.com/search?q=" +
                        Uri.EscapeDataString("\"" + packageName + "\""));
                    break;
            }
        }

        private static bool OpenCreatorCompanion()
        {
#if UNITY_EDITOR_WIN
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if ((process.ProcessName.IndexOf("CreatorCompanion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         process.ProcessName.Equals("VCC", StringComparison.OrdinalIgnoreCase)) &&
                        process.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(process.MainWindowHandle, 9);
                        SetForegroundWindow(process.MainWindowHandle);
                        return true;
                    }
                }
                catch (InvalidOperationException) { }
                finally
                {
                    process.Dispose();
                }
            }

            string executable = FindCreatorCompanionExecutable();
            if (string.IsNullOrEmpty(executable))
            {
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = true
                });
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            return false;
#else
            return false;
#endif
        }

#if UNITY_EDITOR_WIN
        private static string FindCreatorCompanionExecutable()
        {
            string registryLocation = GetCreatorCompanionRegistryLocation();
            string registryExecutable = FindCreatorCompanionExecutableAt(registryLocation);
            if (!string.IsNullOrEmpty(registryExecutable))
            {
                return registryExecutable;
            }

            string programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs");
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates =
            {
                Path.Combine(programs, "VRChat Creator Companion", "CreatorCompanion.exe"),
                Path.Combine(programs, "VRChat Creator Companion", "CreatorCompanionBeta.exe"),
                Path.Combine(programs, "CreatorCompanion", "CreatorCompanion.exe"),
                Path.Combine(programs, "VRChat Creator Companion", "VCC.exe"),
                Path.Combine(programFiles, "VRChat Creator Companion", "CreatorCompanion.exe"),
                Path.Combine(programFilesX86, "VRChat Creator Companion", "CreatorCompanion.exe")
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            try
            {
                string[] executableNames =
                {
                    "CreatorCompanion.exe",
                    "CreatorCompanionBeta.exe"
                };
                foreach (string executableName in executableNames)
                {
                    string[] matches = Directory.GetFiles(
                        programs, executableName, SearchOption.AllDirectories);
                    if (matches.Length > 0)
                    {
                        return matches[0];
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return string.Empty;
        }

        private static string GetCreatorCompanionRegistryLocation()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\VCC"))
                {
                    return key != null
                        ? key.GetValue("InstallPath", string.Empty) as string
                        : string.Empty;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            return string.Empty;
        }

        private static string FindCreatorCompanionExecutableAt(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return string.Empty;
            }

            string path = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
            if (File.Exists(path) &&
                path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            string[] executableNames =
            {
                "CreatorCompanion.exe",
                "CreatorCompanionBeta.exe",
                "VCC.exe"
            };
            foreach (string executableName in executableNames)
            {
                string candidate = Path.Combine(path, executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return string.Empty;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr windowHandle, int command);
#endif

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
            if (!isHosting && UnitySyncSession.IsAwaitingFileDownloadConfirmation)
            {
                int fileCount = UnitySyncSession.PendingFileDownloadCount;
                long downloadBytes = UnitySyncSession.PendingFileDownloadBytes;
                string fileLabel = fileCount == 1 ? "file" : "files";
                EditorGUILayout.HelpBox(
                    "The host has " +
                    fileCount +
                    " " +
                    fileLabel +
                    " you need to download (" +
                    FormatBytes(downloadBytes) +
                    "). " +
                    UnitySyncFileSynchronizer.GuestPendingDeletionCount +
                    " guest-only files will be deleted. Review the file list in the Activity Log before continuing.",
                    MessageType.Warning);

                bool disconnected = false;
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Disconnect"))
                    {
                        _error = string.Empty;
                        UnitySyncSession.Stop();
                        disconnected = true;
                    }

                    if (GUILayout.Button("Continue"))
                    {
                        _error = string.Empty;
                        if (!UnitySyncSession.ContinueFileSync(out _error))
                        {
                            Repaint();
                        }
                    }
                }

                EditorGUI.indentLevel--;
                if (disconnected)
                {
                    Repaint();
                }

                return;
            }

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

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            string[] units = { "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unitIndex = -1;
            do
            {
                value /= 1024d;
                unitIndex++;
            }
            while (value >= 1024d && unitIndex < units.Length - 1);

            string format = value >= 100d
                ? "0"
                : value >= 10d
                    ? "0.0"
                    : "0.00";
            return value.ToString(format) + " " + units[unitIndex];
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

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Restore replaces Assets and ProjectSettings with the host's files, " +
                "including files that already match. Guest-only files and unsaved scene edits are removed. " +
                "Packages must be managed manually. The host must accept the request in Debug. Downloading and importing may take a long time.",
                MessageType.Warning);
            bool connectedGuest = UnitySyncSession.State == UnitySyncSessionState.Connected;
            using (new EditorGUI.DisabledScope(!connectedGuest || UnitySyncSession.IsFileSyncing))
            {
                if (GUILayout.Button("Delete and restore project from host"))
                {
                    UnitySyncSession.RequestProjectRestore();
                }
            }

            if (UnitySyncFileSynchronizer.IsWaitingForRestoreApproval)
            {
                EditorGUILayout.LabelField("Waiting for the host to accept in Debug.");
            }
            else if (UnitySyncSession.State == UnitySyncSessionState.Hosting)
            {
                EditorGUILayout.LabelField(
                    "Guests request a restore from their Debug panel. Accept their requests below.",
                    EditorStyles.wordWrappedMiniLabel);
            }
            else if (!connectedGuest)
            {
                EditorGUILayout.LabelField(
                    "Join a host session to request a project restore.",
                    EditorStyles.wordWrappedMiniLabel);
            }
            else if (UnitySyncSession.IsFileSyncing)
            {
                EditorGUILayout.LabelField(
                    "Wait for the current file synchronization to finish.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            if (UnitySyncSession.State == UnitySyncSessionState.Hosting)
            {
                Guid[] restoreRequests = UnitySyncFileSynchronizer.PendingProjectRestores;
                if (restoreRequests.Length == 0)
                {
                    EditorGUILayout.LabelField("No project restore requests pending.");
                }

                foreach (Guid playerId in restoreRequests)
                {
                    string requester = UnitySyncPresenceRoot.TryGetViewport(playerId, out UnitySyncViewportState viewport)
                        ? viewport.DisplayName : playerId.ToString("N");
                    EditorGUILayout.LabelField("Restore request: " + requester);
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Accept project restore"))
                    {
                        UnitySyncSession.RespondToProjectRestore(playerId, true);
                    }
                    if (GUILayout.Button("Decline"))
                    {
                        UnitySyncSession.RespondToProjectRestore(playerId, false);
                    }
                    EditorGUILayout.EndHorizontal();
                }
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
