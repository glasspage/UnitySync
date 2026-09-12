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
            internal readonly bool IsDebug;
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
            private Vector3 _interpolationStartPivot;
            private Vector3 _interpolationStartPosition;
            private Quaternion _interpolationStartRotation;
            private double _interpolationStartTime;

            internal ViewportMarker(GameObject gameObject, string displayName, Color color, bool isDebug)
            {
                GameObject = gameObject;
                IsDebug = isDebug;
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
                Transform transform = GameObject.transform;
                if (!_hasTransform)
                {
                    Pivot = TargetPivot;
                    transform.SetPositionAndRotation(TargetPosition, TargetRotation);
                    _interpolationStartPivot = TargetPivot;
                    _interpolationStartPosition = TargetPosition;
                    _interpolationStartRotation = TargetRotation;
                    _interpolationStartTime = EditorApplication.timeSinceStartup;
                    _hasTransform = true;
                    return;
                }

                _interpolationStartPivot = Pivot;
                _interpolationStartPosition = transform.position;
                _interpolationStartRotation = transform.rotation;
                _interpolationStartTime = EditorApplication.timeSinceStartup;
            }

            internal bool Interpolate(double currentTime)
            {
                Transform transform = GameObject.transform;
                float amount = Mathf.Clamp01(
                    (float)((currentTime - _interpolationStartTime) / TransformInterpolationDuration));
                Vector3 position = Vector3.Lerp(
                    _interpolationStartPosition,
                    TargetPosition,
                    amount);
                Quaternion rotation = Quaternion.Slerp(
                    _interpolationStartRotation,
                    TargetRotation,
                    amount);
                Vector3 pivot = Vector3.Lerp(
                    _interpolationStartPivot,
                    TargetPivot,
                    amount);

                bool positionChanged = (transform.position - position).sqrMagnitude > 0.00000001f;
                bool rotationChanged = Quaternion.Angle(transform.rotation, rotation) > 0.01f;
                bool pivotChanged = (Pivot - pivot).sqrMagnitude > 0.00000001f;
                if (!positionChanged && !rotationChanged && !pivotChanged)
                {
                    return false;
                }

                Pivot = pivot;
                transform.SetPositionAndRotation(position, rotation);
                return true;
            }
        }

        private const string CollaboratorsContainerName = "Collaborators";
        private const double TransformInterpolationDuration = 0.1d;
        private const float DirectionLineOpacityMultiplier = 0.6f;
        private const float ForegroundStrokeWidth = 1f;
        private const float OutlineStrokeWidth = 3f;
        private const float NeutralOutlineSwitchValue = 0.35f;
        private const float SaturatedOutlineSwitchValue = 0.65f;
        private const float MinimumOutlineOpacity = 0.35f;
        private const int DiscSegmentCount = 48;

        private static readonly Guid DebugMarkerId = new Guid("f47f5129-96d0-40ac-a62c-6db83ea543fa");
        private static readonly Vector2[] LabelOutlineOffsets =
        {
            new Vector2(-1f, -1f),
            new Vector2(0f, -1f),
            new Vector2(1f, -1f),
            new Vector2(-1f, 0f),
            new Vector2(1f, 0f),
            new Vector2(-1f, 1f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f)
        };
        private static readonly Vector3[] DiscPoints = new Vector3[DiscSegmentCount + 1];

        private static readonly Dictionary<Guid, ViewportMarker> Markers =
            new Dictionary<Guid, ViewportMarker>();

        private static GameObject _collaboratorsRoot;
        private static GUIStyle _labelStyle;
        private static GUIStyle _labelOutlineStyle;

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

            ApplyMarker(viewport, false);
        }

        internal static bool DebugMarkerVisible
        {
            get
            {
                if (!Markers.TryGetValue(DebugMarkerId, out ViewportMarker marker))
                {
                    return false;
                }

                if (marker.GameObject != null)
                {
                    return true;
                }

                Markers.Remove(DebugMarkerId);
                DestroyCollaboratorsContainerIfEmpty();
                return false;
            }
        }

        internal static void SummonDebugMarker(string displayName, Color color)
        {
            SceneView sceneView = SceneView.lastActiveSceneView;
            Camera camera = sceneView != null ? sceneView.camera : null;

            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            Vector3 pivot = Vector3.forward * 2f;
            float fieldOfView = 60f;
            float aspect = 1.777f;
            bool orthographic = false;
            float orthographicSize = 5f;

            if (camera != null)
            {
                position = sceneView.pivot;
                rotation = camera.transform.rotation;
                pivot = position + rotation * Vector3.forward * 2f;
                fieldOfView = camera.fieldOfView;
                aspect = camera.aspect;
                orthographic = camera.orthographic;
                orthographicSize = camera.orthographicSize;
            }

            UnitySyncViewportState viewport = new UnitySyncViewportState(
                DebugMarkerId,
                displayName,
                color,
                position,
                rotation,
                pivot,
                fieldOfView,
                aspect,
                orthographic,
                orthographicSize);
            ApplyMarker(viewport, true);
            SceneView.RepaintAll();
        }

        internal static void UpdateDebugMarkerAppearance(string displayName, Color color)
        {
            if (!Markers.TryGetValue(DebugMarkerId, out ViewportMarker marker) || marker.GameObject == null)
            {
                return;
            }

            marker.DisplayName = displayName;
            marker.Color = color;
            marker.GameObject.name = displayName + " (Viewport)";
            SceneView.RepaintAll();
        }

        internal static void DestroyDebugMarker()
        {
            Remove(DebugMarkerId);
            SceneView.RepaintAll();
        }

        private static void ApplyMarker(UnitySyncViewportState viewport, bool isDebug)
        {
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
                    viewport.Color,
                    isDebug);
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
                else if (pair.Value.IsDebug)
                {
                    continue;
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
            List<Guid> remoteMarkerIds = new List<Guid>();
            foreach (KeyValuePair<Guid, ViewportMarker> pair in Markers)
            {
                if (!pair.Value.IsDebug)
                {
                    remoteMarkerIds.Add(pair.Key);
                }
            }

            foreach (Guid playerId in remoteMarkerIds)
            {
                Remove(playerId);
            }
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
            if (Markers.Count == 0)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
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

                changed |= marker.Interpolate(now);
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
                Color foregroundColor = WithAlpha(marker.Color, opacity);
                Color outlineColor = CalculateOutlineColor(marker.Color, opacity);
                Handles.matrix = Matrix4x4.TRS(
                    markerTransform.position,
                    markerTransform.rotation,
                    Vector3.one);

                if (marker.Orthographic)
                {
                    float height = Mathf.Clamp(marker.OrthographicSize * 0.12f, 0.15f, 1.5f);
                    float width = height * marker.Aspect;
                    DrawWireCube(
                        Vector3.forward * 0.08f,
                        new Vector3(width, height, 0.16f),
                        outlineColor,
                        foregroundColor);
                }
                else
                {
                    DrawPerspectiveFrustum(
                        marker.FieldOfView,
                        marker.Aspect,
                        outlineColor,
                        foregroundColor);
                }

                if (directionLineDistance > 0f)
                {
                    float directionLineOpacity = opacity * DirectionLineOpacityMultiplier;
                    Color directionLineColor = WithAlpha(marker.Color, directionLineOpacity);
                    Color directionLineOutlineColor = CalculateOutlineColor(
                        marker.Color,
                        directionLineOpacity);
                    DrawOutlinedLine(
                        Vector3.zero,
                        Vector3.forward * directionLineDistance,
                        directionLineOutlineColor,
                        directionLineColor);
                    DrawOutlinedDisc(
                        Vector3.forward * directionLineDistance,
                        0.04f,
                        directionLineOutlineColor,
                        directionLineColor);
                }

                Handles.matrix = previousMatrix;
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
            if (Vector3.Dot(camera.transform.forward, labelPosition - camera.transform.position) <= 0f)
            {
                return;
            }

            GUIContent content = new GUIContent(marker.DisplayName);
            Vector2 labelSize = _labelStyle.CalcSize(content);
            Vector2 guiPosition = HandleUtility.WorldToGUIPoint(labelPosition);
            Rect labelRect = new Rect(
                guiPosition.x - labelSize.x * 0.5f,
                guiPosition.y - labelSize.y * 0.5f,
                labelSize.x,
                labelSize.y);

            _labelOutlineStyle.normal.textColor = CalculateOutlineColor(marker.Color, opacity);
            _labelStyle.normal.textColor = WithAlpha(marker.Color, opacity);

            Handles.BeginGUI();
            foreach (Vector2 offset in LabelOutlineOffsets)
            {
                Rect outlineRect = new Rect(
                    labelRect.x + offset.x,
                    labelRect.y + offset.y,
                    labelRect.width,
                    labelRect.height);
                GUI.Label(outlineRect, content, _labelOutlineStyle);
            }

            GUI.Label(labelRect, content, _labelStyle);
            Handles.EndGUI();
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
            _labelOutlineStyle = new GUIStyle(_labelStyle);
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }

        private static Color CalculateOutlineColor(Color color, float viewportOpacity)
        {
            Color.RGBToHSV(color, out _, out float saturation, out float value);
            float switchValue = Mathf.Lerp(
                NeutralOutlineSwitchValue,
                SaturatedOutlineSwitchValue,
                saturation);
            bool useBlack = value >= switchValue;
            float distanceFromSwitch = useBlack
                ? Mathf.InverseLerp(switchValue, 1f, value)
                : 1f - Mathf.InverseLerp(0f, switchValue, value);
            float adaptiveOpacity = Mathf.Lerp(
                MinimumOutlineOpacity,
                1f,
                distanceFromSwitch);
            float alpha = adaptiveOpacity *
                          UnitySyncVisualSettings.ContrastIntensity *
                          viewportOpacity;

            return useBlack
                ? new Color(0f, 0f, 0f, alpha)
                : new Color(1f, 1f, 1f, alpha);
        }

        private static void DrawPerspectiveFrustum(
            float fieldOfView,
            float aspect,
            Color outlineColor,
            Color foregroundColor)
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

            DrawPerspectiveFrustumStroke(
                nearBottomLeft,
                nearBottomRight,
                nearTopRight,
                nearTopLeft,
                farBottomLeft,
                farBottomRight,
                farTopRight,
                farTopLeft,
                outlineColor,
                OutlineStrokeWidth);
            DrawPerspectiveFrustumStroke(
                nearBottomLeft,
                nearBottomRight,
                nearTopRight,
                nearTopLeft,
                farBottomLeft,
                farBottomRight,
                farTopRight,
                farTopLeft,
                foregroundColor,
                ForegroundStrokeWidth);
        }

        private static void DrawPerspectiveFrustumStroke(
            Vector3 nearBottomLeft,
            Vector3 nearBottomRight,
            Vector3 nearTopRight,
            Vector3 nearTopLeft,
            Vector3 farBottomLeft,
            Vector3 farBottomRight,
            Vector3 farTopRight,
            Vector3 farTopLeft,
            Color color,
            float strokeWidth)
        {
            Handles.color = color;
            DrawRectangleStroke(
                nearBottomLeft,
                nearBottomRight,
                nearTopRight,
                nearTopLeft,
                strokeWidth);
            DrawRectangleStroke(
                farBottomLeft,
                farBottomRight,
                farTopRight,
                farTopLeft,
                strokeWidth);
            Handles.DrawAAPolyLine(strokeWidth, nearBottomLeft, farBottomLeft);
            Handles.DrawAAPolyLine(strokeWidth, nearBottomRight, farBottomRight);
            Handles.DrawAAPolyLine(strokeWidth, nearTopRight, farTopRight);
            Handles.DrawAAPolyLine(strokeWidth, nearTopLeft, farTopLeft);
        }

        private static void DrawWireCube(
            Vector3 center,
            Vector3 size,
            Color outlineColor,
            Color foregroundColor)
        {
            Vector3 halfSize = size * 0.5f;
            Vector3 nearBottomLeft = center + new Vector3(-halfSize.x, -halfSize.y, -halfSize.z);
            Vector3 nearBottomRight = center + new Vector3(halfSize.x, -halfSize.y, -halfSize.z);
            Vector3 nearTopRight = center + new Vector3(halfSize.x, halfSize.y, -halfSize.z);
            Vector3 nearTopLeft = center + new Vector3(-halfSize.x, halfSize.y, -halfSize.z);
            Vector3 farBottomLeft = center + new Vector3(-halfSize.x, -halfSize.y, halfSize.z);
            Vector3 farBottomRight = center + new Vector3(halfSize.x, -halfSize.y, halfSize.z);
            Vector3 farTopRight = center + new Vector3(halfSize.x, halfSize.y, halfSize.z);
            Vector3 farTopLeft = center + new Vector3(-halfSize.x, halfSize.y, halfSize.z);

            DrawPerspectiveFrustumStroke(
                nearBottomLeft,
                nearBottomRight,
                nearTopRight,
                nearTopLeft,
                farBottomLeft,
                farBottomRight,
                farTopRight,
                farTopLeft,
                outlineColor,
                OutlineStrokeWidth);
            DrawPerspectiveFrustumStroke(
                nearBottomLeft,
                nearBottomRight,
                nearTopRight,
                nearTopLeft,
                farBottomLeft,
                farBottomRight,
                farTopRight,
                farTopLeft,
                foregroundColor,
                ForegroundStrokeWidth);
        }

        private static void DrawRectangleStroke(
            Vector3 bottomLeft,
            Vector3 bottomRight,
            Vector3 topRight,
            Vector3 topLeft,
            float strokeWidth)
        {
            Handles.DrawAAPolyLine(
                strokeWidth,
                bottomLeft,
                bottomRight,
                topRight,
                topLeft,
                bottomLeft);
        }

        private static void DrawOutlinedLine(
            Vector3 start,
            Vector3 end,
            Color outlineColor,
            Color foregroundColor)
        {
            Handles.color = outlineColor;
            Handles.DrawAAPolyLine(OutlineStrokeWidth, start, end);
            Handles.color = foregroundColor;
            Handles.DrawAAPolyLine(ForegroundStrokeWidth, start, end);
        }

        private static void DrawOutlinedDisc(
            Vector3 center,
            float radius,
            Color outlineColor,
            Color foregroundColor)
        {
            for (int i = 0; i <= DiscSegmentCount; i++)
            {
                float angle = i * Mathf.PI * 2f / DiscSegmentCount;
                DiscPoints[i] = center + new Vector3(
                    Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius,
                    0f);
            }

            Handles.color = outlineColor;
            Handles.DrawAAPolyLine(OutlineStrokeWidth, DiscPoints);
            Handles.color = foregroundColor;
            Handles.DrawAAPolyLine(ForegroundStrokeWidth, DiscPoints);
        }
    }
}
