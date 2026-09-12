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

        [MenuItem("UnitySync/Visual Options", false, 20)]
        internal static void Open()
        {
            UnitySyncVisualOptionsWindow window = GetWindow<UnitySyncVisualOptionsWindow>();
            window.titleContent = new GUIContent("UnitySync Visuals");
            window.minSize = new Vector2(320f, 220f);
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
