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

        internal const UnitySyncViewportLineLength DefaultLineLength = UnitySyncViewportLineLength.Long;
        internal const float DefaultViewportOpacity = 1f;
        internal const float ShortLineDistance = 0.45f;
        internal const float LongLineDistance = 1.15f;

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
            Changed?.Invoke();
        }
    }
}
