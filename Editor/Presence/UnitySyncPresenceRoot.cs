using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    [InitializeOnLoad]
    internal static class UnitySyncPresenceRoot
    {
        private sealed class ViewportMarker
        {
            internal readonly GameObject GameObject;
            internal string DisplayName;
            internal Color Color;
            internal Vector3 Pivot;
            internal float FieldOfView;
            internal float Aspect;
            internal bool Orthographic;
            internal float OrthographicSize;

            internal ViewportMarker(GameObject gameObject, string displayName, Color color)
            {
                GameObject = gameObject;
                DisplayName = displayName;
                Color = color;
                FieldOfView = 60f;
                Aspect = 1.777f;
                OrthographicSize = 5f;
            }

            internal void Apply(UnitySyncViewportState viewport)
            {
                DisplayName = viewport.DisplayName;
                Pivot = viewport.Pivot;
                FieldOfView = viewport.FieldOfView;
                Aspect = Mathf.Clamp(viewport.Aspect, 0.1f, 10f);
                Orthographic = viewport.Orthographic;
                OrthographicSize = viewport.OrthographicSize;

                GameObject.name = DisplayName + " (Viewport)";
                GameObject.transform.SetPositionAndRotation(viewport.Position, viewport.Rotation.normalized);
            }
        }

        private const string RootName = "[UnitySync] Collaborators";

        private static readonly Dictionary<Guid, ViewportMarker> Markers =
            new Dictionary<Guid, ViewportMarker>();

        private static GameObject _root;

        static UnitySyncPresenceRoot()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.delayCall += CleanupOrphanedRoots;
        }

        internal static void Apply(UnitySyncViewportState viewport, Guid localPlayerId)
        {
            if (viewport.PlayerId == localPlayerId)
            {
                return;
            }

            if (!Markers.TryGetValue(viewport.PlayerId, out ViewportMarker marker) ||
                marker.GameObject == null)
            {
                EnsureRoot();
                GameObject markerObject = new GameObject(viewport.DisplayName + " (Viewport)")
                {
                    hideFlags = HideFlags.DontSaveInEditor | HideFlags.NotEditable
                };
                SetEditorOnlyTag(markerObject);
                markerObject.transform.SetParent(_root.transform, false);

                marker = new ViewportMarker(
                    markerObject,
                    viewport.DisplayName,
                    ColorFor(viewport.PlayerId));
                Markers[viewport.PlayerId] = marker;
            }

            marker.Apply(viewport);
        }

        internal static void Remove(Guid playerId)
        {
            if (!Markers.TryGetValue(playerId, out ViewportMarker marker))
            {
                return;
            }

            Markers.Remove(playerId);
            if (marker.GameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(marker.GameObject);
            }
        }

        internal static string[] GetParticipantNames()
        {
            List<string> names = new List<string>();
            List<Guid> staleIds = new List<Guid>();
            foreach (KeyValuePair<Guid, ViewportMarker> pair in Markers)
            {
                if (pair.Value.GameObject == null)
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
            CleanupOrphanedRoots();
        }

        private static void EnsureRoot()
        {
            if (_root != null)
            {
                return;
            }

            CleanupOrphanedRoots();
            _root = new GameObject(RootName)
            {
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.NotEditable
            };
            SetEditorOnlyTag(_root);
        }

        private static void CleanupOrphanedRoots()
        {
            GameObject[] gameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            foreach (GameObject gameObject in gameObjects)
            {
                if (gameObject == null ||
                    gameObject == _root ||
                    gameObject.name != RootName ||
                    EditorUtility.IsPersistent(gameObject) ||
                    (gameObject.hideFlags & HideFlags.DontSaveInEditor) == 0)
                {
                    continue;
                }

                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            Matrix4x4 previousMatrix = Handles.matrix;
            Color previousColor = Handles.color;

            foreach (ViewportMarker marker in Markers.Values)
            {
                if (marker.GameObject == null)
                {
                    continue;
                }

                Transform markerTransform = marker.GameObject.transform;
                Handles.color = marker.Color;
                Handles.matrix = Matrix4x4.TRS(
                    markerTransform.position,
                    markerTransform.rotation,
                    Vector3.one);

                if (marker.Orthographic)
                {
                    float height = Mathf.Clamp(marker.OrthographicSize * 0.12f, 0.15f, 1.5f);
                    float width = height * marker.Aspect;
                    Handles.DrawWireCube(
                        Vector3.forward * 0.08f,
                        new Vector3(width, height, 0.16f));
                }
                else
                {
                    DrawPerspectiveFrustum(marker.FieldOfView, marker.Aspect);
                }

                Handles.DrawLine(Vector3.zero, Vector3.forward * 1.15f);
                Handles.DrawWireDisc(Vector3.forward * 1.15f, Vector3.forward, 0.04f);

                Handles.matrix = previousMatrix;
                Handles.color = new Color(
                    marker.Color.r,
                    marker.Color.g,
                    marker.Color.b,
                    0.55f);
                Handles.DrawDottedLine(markerTransform.position, marker.Pivot, 4f);
                Handles.color = marker.Color;
                Handles.Label(
                    markerTransform.position + markerTransform.up * 0.2f,
                    marker.DisplayName);
            }

            Handles.matrix = previousMatrix;
            Handles.color = previousColor;
        }

        private static void DrawPerspectiveFrustum(float fieldOfView, float aspect)
        {
            const float nearDistance = 0.05f;
            const float farDistance = 0.8f;

            float tangent = Mathf.Tan(Mathf.Clamp(fieldOfView, 5f, 170f) * 0.5f * Mathf.Deg2Rad);
            float nearHalfHeight = tangent * nearDistance;
            float nearHalfWidth = nearHalfHeight * aspect;
            float farHalfHeight = tangent * farDistance;
            float farHalfWidth = farHalfHeight * aspect;

            Vector3 nearBottomLeft = new Vector3(-nearHalfWidth, -nearHalfHeight, nearDistance);
            Vector3 nearBottomRight = new Vector3(nearHalfWidth, -nearHalfHeight, nearDistance);
            Vector3 nearTopRight = new Vector3(nearHalfWidth, nearHalfHeight, nearDistance);
            Vector3 nearTopLeft = new Vector3(-nearHalfWidth, nearHalfHeight, nearDistance);

            Vector3 farBottomLeft = new Vector3(-farHalfWidth, -farHalfHeight, farDistance);
            Vector3 farBottomRight = new Vector3(farHalfWidth, -farHalfHeight, farDistance);
            Vector3 farTopRight = new Vector3(farHalfWidth, farHalfHeight, farDistance);
            Vector3 farTopLeft = new Vector3(-farHalfWidth, farHalfHeight, farDistance);

            DrawRectangle(nearBottomLeft, nearBottomRight, nearTopRight, nearTopLeft);
            DrawRectangle(farBottomLeft, farBottomRight, farTopRight, farTopLeft);
            Handles.DrawLine(nearBottomLeft, farBottomLeft);
            Handles.DrawLine(nearBottomRight, farBottomRight);
            Handles.DrawLine(nearTopRight, farTopRight);
            Handles.DrawLine(nearTopLeft, farTopLeft);
        }

        private static void DrawRectangle(
            Vector3 bottomLeft,
            Vector3 bottomRight,
            Vector3 topRight,
            Vector3 topLeft)
        {
            Handles.DrawLine(bottomLeft, bottomRight);
            Handles.DrawLine(bottomRight, topRight);
            Handles.DrawLine(topRight, topLeft);
            Handles.DrawLine(topLeft, bottomLeft);
        }

        private static void SetEditorOnlyTag(GameObject gameObject)
        {
            try
            {
                gameObject.tag = "EditorOnly";
            }
            catch (UnityException)
            {
                // The transient hide flag still prevents persistence if project tags are unavailable.
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
