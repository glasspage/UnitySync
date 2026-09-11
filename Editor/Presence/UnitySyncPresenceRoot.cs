using System;
using System.Collections.Generic;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal static class UnitySyncPresenceRoot
    {
        private const string RootName = "[UnitySync] Collaborators";

        private static readonly Dictionary<Guid, UnitySyncViewportMarker> Markers =
            new Dictionary<Guid, UnitySyncViewportMarker>();

        private static GameObject _root;

        internal static void Apply(UnitySyncViewportState viewport, Guid localPlayerId)
        {
            if (viewport.PlayerId == localPlayerId)
            {
                return;
            }

            if (!Markers.TryGetValue(viewport.PlayerId, out UnitySyncViewportMarker marker) || marker == null)
            {
                EnsureRoot();
                GameObject markerObject = new GameObject(viewport.DisplayName + " (Viewport)");
                markerObject.hideFlags = HideFlags.DontSaveInEditor | HideFlags.NotEditable;
                SetEditorOnlyTag(markerObject);
                markerObject.transform.SetParent(_root.transform, false);

                marker = markerObject.AddComponent<UnitySyncViewportMarker>();
                marker.Initialize(viewport.PlayerId, viewport.DisplayName, ColorFor(viewport.PlayerId));
                Markers[viewport.PlayerId] = marker;
            }

            marker.Apply(viewport);
        }

        internal static void Remove(Guid playerId)
        {
            if (!Markers.TryGetValue(playerId, out UnitySyncViewportMarker marker))
            {
                return;
            }

            Markers.Remove(playerId);
            if (marker != null)
            {
                UnityEngine.Object.DestroyImmediate(marker.gameObject);
            }
        }

        internal static string[] GetParticipantNames()
        {
            List<string> names = new List<string>();
            List<Guid> staleIds = new List<Guid>();
            foreach (KeyValuePair<Guid, UnitySyncViewportMarker> pair in Markers)
            {
                if (pair.Value == null)
                {
                    staleIds.Add(pair.Key);
                }
                else
                {
                    names.Add(pair.Value.DisplayName);
                }
            }

            foreach (Guid staleId in staleIds)
            {
                Markers.Remove(staleId);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        internal static void Clear()
        {
            Markers.Clear();
            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root);
            }

            _root = null;
        }

        private static void EnsureRoot()
        {
            if (_root != null)
            {
                return;
            }

            _root = new GameObject(RootName)
            {
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.NotEditable
            };
            SetEditorOnlyTag(_root);
        }

        private static void SetEditorOnlyTag(GameObject gameObject)
        {
            try
            {
                gameObject.tag = "EditorOnly";
            }
            catch (UnityException)
            {
                // EditorOnly is built in, but the hide flags still prevent persistence if a project has altered its tags.
            }
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
    }
}
