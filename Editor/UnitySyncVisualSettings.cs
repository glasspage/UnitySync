using System;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal enum UnitySyncViewportLineLength
    {
        Off,
        Short,
        Long
    }

    internal static class UnitySyncVisualSettings
    {
        private const string LineLengthPreference = "Glasspage.UnitySync.Visuals.ViewportLineLength";
        private const string ViewportOpacityPreference = "Glasspage.UnitySync.Visuals.ViewportOpacity";
        private const string ContrastIntensityPreference = "Glasspage.UnitySync.Visuals.ContrastIntensity";
        private const string SelectionOutlinesPreference = "Glasspage.UnitySync.Visuals.SelectionOutlines";

        internal const UnitySyncViewportLineLength DefaultLineLength = UnitySyncViewportLineLength.Short;
        internal const float DefaultViewportOpacity = 1f;
        internal const float DefaultContrastIntensity = 0.2f;
        internal const bool DefaultSelectionOutlines = true;
        internal const float ShortLineDistance = 1.25f;
        internal const float LongLineDistance = 3f;

        internal static event Action Changed;

        internal static UnitySyncViewportLineLength LineLength
        {
            get
            {
                int stored = EditorPrefs.GetInt(LineLengthPreference, (int)DefaultLineLength);
                return Enum.IsDefined(typeof(UnitySyncViewportLineLength), stored)
                    ? (UnitySyncViewportLineLength)stored
                    : DefaultLineLength;
            }
            set
            {
                if (LineLength == value)
                {
                    return;
                }

                EditorPrefs.SetInt(LineLengthPreference, (int)value);
                Changed?.Invoke();
            }
        }

        internal static float ViewportOpacity
        {
            get => Mathf.Clamp01(EditorPrefs.GetFloat(ViewportOpacityPreference, DefaultViewportOpacity));
            set
            {
                float clamped = Mathf.Clamp01(value);
                if (Mathf.Approximately(ViewportOpacity, clamped))
                {
                    return;
                }

                EditorPrefs.SetFloat(ViewportOpacityPreference, clamped);
                Changed?.Invoke();
            }
        }

        internal static float ContrastIntensity
        {
            get => Mathf.Clamp01(EditorPrefs.GetFloat(ContrastIntensityPreference, DefaultContrastIntensity));
            set
            {
                float clamped = Mathf.Clamp01(value);
                if (Mathf.Approximately(ContrastIntensity, clamped))
                {
                    return;
                }

                EditorPrefs.SetFloat(ContrastIntensityPreference, clamped);
                Changed?.Invoke();
            }
        }

        internal static bool SelectionOutlines
        {
            get => EditorPrefs.GetBool(SelectionOutlinesPreference, DefaultSelectionOutlines);
            set
            {
                if (SelectionOutlines == value)
                {
                    return;
                }

                EditorPrefs.SetBool(SelectionOutlinesPreference, value);
                Changed?.Invoke();
            }
        }

        internal static float LineDistance
        {
            get
            {
                switch (LineLength)
                {
                    case UnitySyncViewportLineLength.Short:
                        return ShortLineDistance;

                    case UnitySyncViewportLineLength.Long:
                        return LongLineDistance;

                    default:
                        return 0f;
                }
            }
        }

        internal static void Reset()
        {
            EditorPrefs.DeleteKey(LineLengthPreference);
            EditorPrefs.DeleteKey(ViewportOpacityPreference);
            EditorPrefs.DeleteKey(ContrastIntensityPreference);
            EditorPrefs.DeleteKey(SelectionOutlinesPreference);
            Changed?.Invoke();
        }
    }
}
