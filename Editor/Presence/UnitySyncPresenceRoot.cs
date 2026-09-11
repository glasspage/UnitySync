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
            internal Vector3 TargetPivot;
            internal Vector3 TargetPosition;
            internal Quaternion TargetRotation;
            internal float FieldOfView;
            internal float Aspect;
            internal bool Orthographic;
            internal float OrthographicSize;

            private bool _hasTransform;

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
                Color = viewport.Color;
                TargetPivot = viewport.Pivot;
                TargetPosition = viewport.Position;
                TargetRotation = viewport.Rotation.normalized;
                FieldOfView = viewport.FieldOfView;
                Aspect = Mathf.Clamp(viewport.Aspect, 0.1f, 10f);
                Orthographic = viewport.Orthographic;
                OrthographicSize = viewport.OrthographicSize;

                GameObject.name = DisplayName + " (Viewport)";
                if (!_hasTransform)
                {
                    Pivot = TargetPivot;
                    GameObject.transform.SetPositionAndRotation(TargetPosition, TargetRotation);
                    _hasTransform = true;
                }
            }

            internal bool Interpolate(float amount)
            {
                Transform transform = GameObject.transform;
                Vector3 position = transform.position;
                Quaternion rotation = transform.rotation;

                bool positionChanged = (position - TargetPosition).sqrMagnitude > 0.00000001f;
                bool rotationChanged = Quaternion.Angle(rotation, TargetRotation) > 0.01f;
                bool pivotChanged = (Pivot - TargetPivot).sqrMagnitude > 0.00000001f;
                if (!positionChanged && !rotationChanged && !pivotChanged)
                {
                    return false;
                }

                position = positionChanged
                    ? Vector3.Lerp(position, TargetPosition, amount)
                    : TargetPosition;
                rotation = rotationChanged
                    ? Quaternion.Slerp(rotation, TargetRotation, amount)
                    : TargetRotation;
                Pivot = pivotChanged
                    ? Vector3.Lerp(Pivot, TargetPivot, amount)
                    : TargetPivot;

                if ((position - TargetPosition).sqrMagnitude <= 0.00000001f)
                {
                    position = TargetPosition;
                }

                if (Quaternion.Angle(rotation, TargetRotation) <= 0.01f)
                {
                    rotation = TargetRotation;
                }

                if ((Pivot - TargetPivot).sqrMagnitude <= 0.00000001f)
                {
                    Pivot = TargetPivot;
                }

                transform.SetPositionAndRotation(position, rotation);
                return true;
            }
        }

        private const string CollaboratorsContainerName = "Collaborators";
        private const float TransformInterpolationSpeed = 18f;

        private static readonly Dictionary<Guid, ViewportMarker> Markers =
            new Dictionary<Guid, ViewportMarker>();

        private static GameObject _collaboratorsRoot;
        private static GUIStyle _labelStyle;
        private static GUIStyle _labelShadowStyle;
        private static double _lastInterpolationTime;

        static UnitySyncPresenceRoot()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += UpdateInterpolatedTransforms;
            UnitySyncVisualSettings.Changed += SceneView.RepaintAll;
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
                UnitySyncHierarchy.Configure(markerObject);
                markerObject.transform.SetParent(_collaboratorsRoot.transform, false);

                marker = new ViewportMarker(
                    markerObject,
                    viewport.DisplayName,
                    viewport.Color);
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

            DestroyCollaboratorsContainerIfEmpty();
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

            DestroyCollaboratorsContainerIfEmpty();

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        internal static void Clear()
        {
            Markers.Clear();
            UnitySyncHierarchy.DestroyContainer(_collaboratorsRoot);
            _collaboratorsRoot = null;
        }

        private static void EnsureRoot()
        {
            if (_collaboratorsRoot != null)
            {
                return;
            }

            _collaboratorsRoot = UnitySyncHierarchy.GetOrCreateContainer(CollaboratorsContainerName);
        }

        private static void DestroyCollaboratorsContainerIfEmpty()
        {
            if (Markers.Count != 0)
            {
                return;
            }

            UnitySyncHierarchy.DestroyContainer(_collaboratorsRoot);
            _collaboratorsRoot = null;
        }

        private static void UpdateInterpolatedTransforms()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_lastInterpolationTime <= 0d)
            {
                _lastInterpolationTime = now;
                return;
            }

            float deltaTime = (float)Math.Min(now - _lastInterpolationTime, 0.1d);
            _lastInterpolationTime = now;
            if (deltaTime <= 0f || Markers.Count == 0)
            {
                return;
            }

            float amount = 1f - Mathf.Exp(-TransformInterpolationSpeed * deltaTime);
            bool changed = false;
            List<Guid> staleIds = null;
            foreach (KeyValuePair<Guid, ViewportMarker> pair in Markers)
            {
                ViewportMarker marker = pair.Value;
                if (marker.GameObject == null)
                {
                    if (staleIds == null)
                    {
                        staleIds = new List<Guid>();
                    }

                    staleIds.Add(pair.Key);
                    continue;
                }

                changed |= marker.Interpolate(amount);
            }

            if (staleIds != null)
            {
                foreach (Guid staleId in staleIds)
                {
                    Markers.Remove(staleId);
                }

                DestroyCollaboratorsContainerIfEmpty();
            }

            if (changed)
            {
                SceneView.RepaintAll();
            }
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            float opacity = UnitySyncVisualSettings.ViewportOpacity;
            if (opacity <= 0f)
            {
                return;
            }

            float directionLineDistance = UnitySyncVisualSettings.LineDistance;

            Matrix4x4 previousMatrix = Handles.matrix;
            Color previousColor = Handles.color;

            foreach (ViewportMarker marker in Markers.Values)
            {
                if (marker.GameObject == null)
                {
                    continue;
                }

                Transform markerTransform = marker.GameObject.transform;
                Handles.color = WithAlpha(marker.Color, opacity);
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

                if (directionLineDistance > 0f)
                {
                    Handles.DrawLine(Vector3.zero, Vector3.forward * directionLineDistance);
                    Handles.DrawWireDisc(
                        Vector3.forward * directionLineDistance,
                        Vector3.forward,
                        0.04f);
                }

                Handles.matrix = previousMatrix;
                Handles.color = new Color(
                    marker.Color.r,
                    marker.Color.g,
                    marker.Color.b,
                    0.55f * opacity);
                Handles.DrawDottedLine(markerTransform.position, marker.Pivot, 4f);
                DrawDisplayName(sceneView, marker, markerTransform, opacity);
            }

            Handles.matrix = previousMatrix;
            Handles.color = previousColor;
        }

        private static void DrawDisplayName(
            SceneView sceneView,
            ViewportMarker marker,
            Transform markerTransform,
            float opacity)
        {
            Camera camera = sceneView.camera;
            if (camera == null)
            {
                return;
            }

            EnsureLabelStyles();

            float handleSize = HandleUtility.GetHandleSize(markerTransform.position);
            Vector3 labelPosition = markerTransform.position + camera.transform.up * handleSize * 0.12f;
            float shadowOffset = handleSize * 0.012f;
            Vector3 shadowPosition = labelPosition +
                                     camera.transform.right * shadowOffset -
                                     camera.transform.up * shadowOffset;

            _labelShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.9f * opacity);
            _labelStyle.normal.textColor = WithAlpha(marker.Color, opacity);
            Handles.Label(shadowPosition, marker.DisplayName, _labelShadowStyle);
            Handles.Label(labelPosition, marker.DisplayName, _labelStyle);
        }

        private static void EnsureLabelStyles()
        {
            if (_labelStyle != null)
            {
                return;
            }

            _labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };
            _labelShadowStyle = new GUIStyle(_labelStyle);
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
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

    }
}
