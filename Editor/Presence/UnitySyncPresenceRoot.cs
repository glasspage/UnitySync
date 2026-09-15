using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal enum UnitySyncSceneStatusPriority
    {
        Ongoing = 0,
        Normal = 100,
        Important = 200
    }

    internal readonly struct UnitySyncRemoteParticipant
    {
        internal readonly Guid PlayerId;
        internal readonly string DisplayName;
        internal readonly Color Color;
        internal readonly Guid SpectatingPlayerId;

        internal UnitySyncRemoteParticipant(
            Guid playerId,
            string displayName,
            Color color,
            Guid spectatingPlayerId)
        {
            PlayerId = playerId;
            DisplayName = displayName;
            Color = color;
            SpectatingPlayerId = spectatingPlayerId;
        }
    }

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
            internal float FieldOfView;
            internal float Aspect;
            internal bool Orthographic;
            internal float OrthographicSize;
            internal float SceneViewSize;
            internal Guid SpectatingPlayerId;

            private readonly struct TransformSample
            {
                internal readonly double Time;
                internal readonly Vector3 Pivot;
                internal readonly Vector3 Position;
                internal readonly Quaternion Rotation;

                internal TransformSample(
                    double time,
                    Vector3 pivot,
                    Vector3 position,
                    Quaternion rotation)
                {
                    Time = time;
                    Pivot = pivot;
                    Position = position;
                    Rotation = rotation;
                }
            }

            private readonly List<TransformSample> _transformSamples =
                new List<TransformSample>(MaximumBufferedTransformSamples);
            private bool _hasTransform;

            internal ViewportMarker(GameObject gameObject, string displayName, Color color, bool isDebug)
            {
                GameObject = gameObject;
                IsDebug = isDebug;
                DisplayName = displayName;
                Color = color;
                FieldOfView = 60f;
                Aspect = 1.777f;
                OrthographicSize = 5f;
                SceneViewSize = 10f;
                SpectatingPlayerId = Guid.Empty;
            }

            internal void Apply(UnitySyncViewportState viewport, double sampleTime)
            {
                DisplayName = viewport.DisplayName;
                Color = viewport.Color;
                FieldOfView = viewport.FieldOfView;
                Aspect = Mathf.Clamp(viewport.Aspect, 0.1f, 10f);
                Orthographic = viewport.Orthographic;
                OrthographicSize = viewport.OrthographicSize;
                SceneViewSize = Mathf.Max(0.0001f, viewport.SceneViewSize);
                SpectatingPlayerId = viewport.SpectatingPlayerId;

                GameObject.name = DisplayName + " (Viewport)";
                if (sampleTime <= 0d)
                {
                    sampleTime = GetMonotonicSeconds();
                }

                TransformSample sample = new TransformSample(
                    sampleTime,
                    viewport.Pivot,
                    viewport.Position,
                    viewport.Rotation.normalized);

                if (!_hasTransform)
                {
                    Pivot = sample.Pivot;
                    GameObject.transform.SetPositionAndRotation(sample.Position, sample.Rotation);
                    _transformSamples.Add(sample);
                    _hasTransform = true;
                    return;
                }

                _transformSamples.Add(sample);
                if (_transformSamples.Count > MaximumBufferedTransformSamples)
                {
                    _transformSamples.RemoveAt(0);
                }
            }

            internal bool Interpolate(double currentTime)
            {
                if (!_hasTransform || _transformSamples.Count == 0)
                {
                    return false;
                }

                double renderTime = currentTime - TransformInterpolationBufferSeconds;
                while (_transformSamples.Count > 1 &&
                       _transformSamples[1].Time <= renderTime)
                {
                    _transformSamples.RemoveAt(0);
                }

                TransformSample from = _transformSamples[0];
                TransformSample to = _transformSamples.Count > 1
                    ? _transformSamples[1]
                    : from;

                float amount = 0f;
                double sampleDuration = to.Time - from.Time;
                if (sampleDuration > 0.000001d)
                {
                    amount = Mathf.Clamp01((float)((renderTime - from.Time) / sampleDuration));
                }

                Vector3 position = Vector3.Lerp(from.Position, to.Position, amount);
                Quaternion rotation = Quaternion.Slerp(from.Rotation, to.Rotation, amount);
                Vector3 pivot = Vector3.Lerp(from.Pivot, to.Pivot, amount);

                Transform transform = GameObject.transform;
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

            internal bool HasPendingInterpolation(double currentTime)
            {
                if (_transformSamples.Count < 2)
                {
                    return false;
                }

                double renderTime = currentTime - TransformInterpolationBufferSeconds;
                return renderTime < _transformSamples[_transformSamples.Count - 1].Time;
            }
        }

        private sealed class TimedStatusEvent
        {
            internal string Text;
            internal Color Color;
            internal UnitySyncSceneStatusPriority Priority;
            internal double CreatedAtSeconds;
            internal double ExpiresAtSeconds;
            internal long Sequence;
        }

        private readonly struct StatusDisplay
        {
            internal readonly string Text;
            internal readonly Color Color;
            internal readonly UnitySyncSceneStatusPriority Priority;
            internal readonly double SortTime;
            internal readonly long Sequence;

            internal StatusDisplay(
                string text,
                Color color,
                UnitySyncSceneStatusPriority priority,
                double sortTime,
                long sequence)
            {
                Text = text;
                Color = color;
                Priority = priority;
                SortTime = sortTime;
                Sequence = sequence;
            }
        }

        private const string CollaboratorsContainerName = "Collaborators";
        private const double TransformInterpolationBufferSeconds = 0.1d;
        private const int MaximumBufferedTransformSamples = 8;
        private const float DirectionLineOpacityMultiplier = 0.6f;
        private const float ForegroundStrokeWidth = 1f;
        private const float OutlineStrokeWidth = 3f;
        private const float NeutralOutlineSwitchValue = 0.35f;
        private const float SaturatedOutlineSwitchValue = 0.65f;
        private const float MinimumOutlineOpacity = 0.35f;
        private const float SpectateStatusLeftMargin = 12f;
        private const float SpectateStatusBottomMargin = 12f;
        private const float SpectateStatusSpacing = 4f;
        private const float BottomRightStatusRightMargin = 12f;
        private const float BottomRightStatusBottomMargin = 12f;
        private const float BottomRightStatusSpacing = 4f;
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
        private static readonly List<TimedStatusEvent> TimedStatusEvents =
            new List<TimedStatusEvent>();

        private static long _nextStatusSequence;
        private static GameObject _collaboratorsRoot;
        private static GUIStyle _labelStyle;
        private static GUIStyle _labelOutlineStyle;

        static UnitySyncPresenceRoot()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += UpdateInterpolatedTransforms;
            UnitySyncVisualSettings.Changed += SceneView.RepaintAll;
        }

        internal static void Apply(
            UnitySyncViewportState viewport,
            Guid localPlayerId,
            double receivedAtSeconds)
        {
            if (viewport.PlayerId == localPlayerId)
            {
                return;
            }

            ApplyMarker(viewport, false, receivedAtSeconds);
        }

        internal static void AddTimedStatus(
            string text,
            Color color,
            UnitySyncSceneStatusPriority priority,
            double durationSeconds)
        {
            if (string.IsNullOrEmpty(text) || durationSeconds <= 0d)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            TimedStatusEvents.Add(new TimedStatusEvent
            {
                Text = text,
                Color = color,
                Priority = priority,
                CreatedAtSeconds = now,
                ExpiresAtSeconds = now + durationSeconds,
                Sequence = ++_nextStatusSequence
            });
            SceneView.RepaintAll();
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
            float sceneViewSize = 10f;

            if (camera != null)
            {
                position = sceneView.pivot;
                rotation = camera.transform.rotation;
                pivot = position + rotation * Vector3.forward * 2f;
                fieldOfView = camera.fieldOfView;
                aspect = camera.aspect;
                orthographic = camera.orthographic;
                orthographicSize = camera.orthographicSize;
                sceneViewSize = sceneView.size;
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
                orthographicSize,
                sceneViewSize,
                Guid.Empty);
            ApplyMarker(viewport, true, GetMonotonicSeconds());
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

        private static void ApplyMarker(
            UnitySyncViewportState viewport,
            bool isDebug,
            double sampleTime)
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

            marker.Apply(viewport, sampleTime);
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

        internal static bool TryGetViewport(
            Guid playerId,
            out UnitySyncViewportState viewport)
        {
            if (!Markers.TryGetValue(playerId, out ViewportMarker marker) ||
                marker.GameObject == null ||
                marker.IsDebug)
            {
                viewport = default;
                return false;
            }

            Transform transform = marker.GameObject.transform;
            viewport = new UnitySyncViewportState(
                playerId,
                marker.DisplayName,
                marker.Color,
                transform.position,
                transform.rotation,
                marker.Pivot,
                marker.FieldOfView,
                marker.Aspect,
                marker.Orthographic,
                marker.OrthographicSize,
                marker.SceneViewSize,
                marker.SpectatingPlayerId);
            return true;
        }

        internal static UnitySyncRemoteParticipant[] GetParticipants()
        {
            List<UnitySyncRemoteParticipant> participants =
                new List<UnitySyncRemoteParticipant>();
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
                    participants.Add(new UnitySyncRemoteParticipant(
                        pair.Key,
                        pair.Value.DisplayName,
                        pair.Value.Color,
                        pair.Value.SpectatingPlayerId));
                }
            }

            foreach (Guid staleId in staleIds)
            {
                Markers.Remove(staleId);
            }

            DestroyCollaboratorsContainerIfEmpty();

            participants.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(
                    left.DisplayName,
                    right.DisplayName));
            return participants.ToArray();
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
            double interpolationNow = GetMonotonicSeconds();
            bool changed = PruneExpiredTimedStatuses(EditorApplication.timeSinceStartup);
            bool needsContinuousUpdate = false;
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

                changed |= marker.Interpolate(interpolationNow);
                needsContinuousUpdate |= marker.HasPendingInterpolation(interpolationNow);
            }

            if (staleIds != null)
            {
                foreach (Guid staleId in staleIds)
                {
                    Markers.Remove(staleId);
                }

                DestroyCollaboratorsContainerIfEmpty();
            }

            if (needsContinuousUpdate)
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }

            if (changed)
            {
                SceneView.RepaintAll();
            }
        }

        private static bool PruneExpiredTimedStatuses(double now)
        {
            bool changed = false;
            for (int index = TimedStatusEvents.Count - 1; index >= 0; index--)
            {
                if (TimedStatusEvents[index].ExpiresAtSeconds > now)
                {
                    continue;
                }

                TimedStatusEvents.RemoveAt(index);
                changed = true;
            }

            return changed;
        }

        private static double GetMonotonicSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
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

            Guid localPlayerId = UnitySyncSession.CurrentPlayerId;
            Guid spectatingPlayerId = UnitySyncSession.SpectatingPlayerId;
            float directionLineDistance = UnitySyncVisualSettings.LineDistance;
            Matrix4x4 previousMatrix = Handles.matrix;
            Color previousColor = Handles.color;

            foreach (KeyValuePair<Guid, ViewportMarker> pair in Markers)
            {
                ViewportMarker marker = pair.Value;
                if (marker.GameObject == null)
                {
                    continue;
                }

                bool isSpectatingRelation =
                    !marker.IsDebug &&
                    (pair.Key == spectatingPlayerId ||
                     marker.SpectatingPlayerId == localPlayerId);
                if (isSpectatingRelation)
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
            DrawSpectatingStatuses(
                sceneView,
                localPlayerId,
                spectatingPlayerId,
                opacity);
            DrawBottomRightStatuses(sceneView, opacity);
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

            Handles.BeginGUI();
            DrawOutlinedGuiLabel(labelRect, content, marker.Color, opacity);
            Handles.EndGUI();
        }

        private static void DrawSpectatingStatuses(
            SceneView sceneView,
            Guid localPlayerId,
            Guid spectatingPlayerId,
            float opacity)
        {
            EnsureLabelStyles();
            int statusIndex = 0;

            Handles.BeginGUI();

            if (spectatingPlayerId != Guid.Empty &&
                Markers.TryGetValue(spectatingPlayerId, out ViewportMarker spectatedMarker) &&
                spectatedMarker.GameObject != null &&
                !spectatedMarker.IsDebug)
            {
                DrawSpectatingStatus(
                    sceneView,
                    "Spectating " + spectatedMarker.DisplayName,
                    spectatedMarker.Color,
                    opacity,
                    statusIndex++);
            }

            foreach (KeyValuePair<Guid, ViewportMarker> pair in Markers)
            {
                ViewportMarker marker = pair.Value;
                if (pair.Key == spectatingPlayerId ||
                    marker.GameObject == null ||
                    marker.IsDebug ||
                    marker.SpectatingPlayerId != localPlayerId)
                {
                    continue;
                }

                DrawSpectatingStatus(
                    sceneView,
                    marker.DisplayName + " is spectating",
                    marker.Color,
                    opacity,
                    statusIndex++);
            }

            Handles.EndGUI();
        }

        private static void DrawBottomRightStatuses(
            SceneView sceneView,
            float opacity)
        {
            PruneExpiredTimedStatuses(EditorApplication.timeSinceStartup);

            List<StatusDisplay> statuses = new List<StatusDisplay>(
                TimedStatusEvents.Count + 4);
            foreach (TimedStatusEvent statusEvent in TimedStatusEvents)
            {
                statuses.Add(new StatusDisplay(
                    statusEvent.Text,
                    statusEvent.Color,
                    statusEvent.Priority,
                    statusEvent.CreatedAtSeconds,
                    statusEvent.Sequence));
            }

            UnitySyncHostDownloadProgress[] progress =
                UnitySyncFileSynchronizer.GetHostDownloadProgresses();
            foreach (UnitySyncHostDownloadProgress item in progress)
            {
                if (!Markers.TryGetValue(item.PlayerId, out ViewportMarker marker) ||
                    marker.GameObject == null ||
                    marker.IsDebug)
                {
                    continue;
                }

                string noun;
                switch (item.Scope)
                {
                    case UnitySyncFileSyncScope.Packages:
                        noun = "packages";
                        break;
                    case UnitySyncFileSyncScope.Project:
                        noun = "project files";
                        break;
                    default:
                        noun = "assets";
                        break;
                }

                int percent = Mathf.Clamp(
                    Mathf.RoundToInt(item.Progress01 * 100f),
                    0,
                    100);
                statuses.Add(new StatusDisplay(
                    marker.DisplayName + " is receiving " + noun + " (" + percent + "%)...",
                    marker.Color,
                    UnitySyncSceneStatusPriority.Ongoing,
                    item.StartedAtSeconds,
                    0));
            }

            if (statuses.Count == 0)
            {
                return;
            }

            statuses.Sort((left, right) =>
            {
                int priorityComparison = ((int)right.Priority).CompareTo((int)left.Priority);
                if (priorityComparison != 0)
                {
                    return priorityComparison;
                }

                int timeComparison = right.SortTime.CompareTo(left.SortTime);
                if (timeComparison != 0)
                {
                    return timeComparison;
                }

                int sequenceComparison = right.Sequence.CompareTo(left.Sequence);
                if (sequenceComparison != 0)
                {
                    return sequenceComparison;
                }

                return StringComparer.OrdinalIgnoreCase.Compare(left.Text, right.Text);
            });

            EnsureLabelStyles();
            float bottom = sceneView.position.height - BottomRightStatusBottomMargin;
            Handles.BeginGUI();

            foreach (StatusDisplay status in statuses)
            {
                GUIContent content = new GUIContent(status.Text);
                Vector2 labelSize = _labelStyle.CalcSize(content);
                float y = bottom - labelSize.y;
                Rect labelRect = new Rect(
                    sceneView.position.width - BottomRightStatusRightMargin - labelSize.x,
                    y,
                    labelSize.x,
                    labelSize.y);
                DrawOutlinedGuiLabel(
                    labelRect,
                    content,
                    status.Color,
                    opacity);
                bottom = y - BottomRightStatusSpacing;
            }

            Handles.EndGUI();
        }

        private static void DrawSpectatingStatus(
            SceneView sceneView,
            string text,
            Color color,
            float opacity,
            int index)
        {
            GUIContent content = new GUIContent(text);
            Vector2 labelSize = _labelStyle.CalcSize(content);
            float y = sceneView.position.height -
                      SpectateStatusBottomMargin -
                      labelSize.y -
                      index * (labelSize.y + SpectateStatusSpacing);
            Rect labelRect = new Rect(
                SpectateStatusLeftMargin,
                y,
                labelSize.x,
                labelSize.y);
            DrawOutlinedGuiLabel(labelRect, content, color, opacity);
        }

        private static void DrawOutlinedGuiLabel(
            Rect labelRect,
            GUIContent content,
            Color color,
            float opacity)
        {
            SetLabelTextColor(
                _labelOutlineStyle,
                CalculateOutlineColor(color, opacity));
            SetLabelTextColor(
                _labelStyle,
                WithAlpha(color, opacity));

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

        private static void SetLabelTextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
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
