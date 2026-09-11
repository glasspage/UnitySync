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
            window.minSize = new Vector2(320f, 150f);
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
