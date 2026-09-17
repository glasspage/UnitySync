using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal sealed class UnitySyncVisualOptionsWindow : EditorWindow
    {
        private static readonly string[] LineLengthLabels =
        {
            "Off",
            "Short",
            "Long"
        };

        private static readonly string[] StatusLogVisibilityLabels =
        {
            "Full",
            "Log only",
            "Pill only",
            "Disabled"
        };

        private static readonly string[] StatusLogPositionLabels =
        {
            "Left",
            "Right"
        };

        [MenuItem("UnitySync/Visual Options", false, 20)]
        internal static void Open()
        {
            UnitySyncVisualOptionsWindow window = GetWindow<UnitySyncVisualOptionsWindow>();
            window.titleContent = new GUIContent("UnitySync Visuals");
            window.minSize = new Vector2(320f, 280f);
            window.Show();
        }

        private void OnEnable()
        {
            UnitySyncVisualSettings.Changed += OnSettingsChanged;
        }

        private void OnDisable()
        {
            UnitySyncVisualSettings.Changed -= OnSettingsChanged;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Viewport Indicators", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            UnitySyncViewportLineLength lineLength = UnitySyncVisualSettings.LineLength;
            int selectedLineLength = EditorGUILayout.Popup(
                new GUIContent("Direction Line", "Controls the line pointing forward from remote viewports."),
                (int)lineLength,
                LineLengthLabels);
            if (selectedLineLength != (int)lineLength)
            {
                UnitySyncVisualSettings.LineLength = (UnitySyncViewportLineLength)selectedLineLength;
            }

            float opacity = EditorGUILayout.Slider(
                new GUIContent("Viewport Opacity", "Controls the opacity of remote viewport indicators and names."),
                UnitySyncVisualSettings.ViewportOpacity,
                0f,
                1f);
            UnitySyncVisualSettings.ViewportOpacity = opacity;

            float contrastIntensity = EditorGUILayout.Slider(
                new GUIContent("Contrast Intensity", "Multiplies the opacity of the adaptive viewport and username outlines."),
                UnitySyncVisualSettings.ContrastIntensity,
                0f,
                1f);
            UnitySyncVisualSettings.ContrastIntensity = contrastIntensity;

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Status Log", EditorStyles.boldLabel);
            EditorGUILayout.Space(2f);

            UnitySyncStatusLogVisibility statusLogVisibility =
                UnitySyncVisualSettings.StatusLogVisibility;
            int selectedStatusLogVisibility = EditorGUILayout.Popup(
                new GUIContent(
                    "Status Log Visibility",
                    "Controls whether the Scene view shows the activity log, UnitySync session pill, both, or neither."),
                (int)statusLogVisibility,
                StatusLogVisibilityLabels);
            if (selectedStatusLogVisibility != (int)statusLogVisibility)
            {
                UnitySyncVisualSettings.StatusLogVisibility =
                    (UnitySyncStatusLogVisibility)selectedStatusLogVisibility;
            }

            UnitySyncStatusLogPosition statusLogPosition =
                UnitySyncVisualSettings.StatusLogPosition;
            int selectedStatusLogPosition = EditorGUILayout.Popup(
                new GUIContent(
                    "Status Log Position",
                    "Aligns the enabled status log elements to the bottom-left or bottom-right of the Scene view."),
                (int)statusLogPosition,
                StatusLogPositionLabels);
            if (selectedStatusLogPosition != (int)statusLogPosition)
            {
                UnitySyncVisualSettings.StatusLogPosition =
                    (UnitySyncStatusLogPosition)selectedStatusLogPosition;
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Scene Selection", EditorStyles.boldLabel);
            EditorGUILayout.Space(2f);

            bool selectionOutlines = EditorGUILayout.Toggle(
                new GUIContent(
                    "Selection Outlines",
                    "Shows colored outlines around GameObjects selected by remote collaborators."),
                UnitySyncVisualSettings.SelectionOutlines);
            UnitySyncVisualSettings.SelectionOutlines = selectionOutlines;

            EditorGUILayout.Space(12f);
            if (GUILayout.Button("Reset to Defaults"))
            {
                UnitySyncVisualSettings.Reset();
            }
        }

        private void OnSettingsChanged()
        {
            Repaint();
            SceneView.RepaintAll();
        }
    }
}
