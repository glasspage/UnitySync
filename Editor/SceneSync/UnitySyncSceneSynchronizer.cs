using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Glasspage.UnitySync
{
    [InitializeOnLoad]
    internal static class UnitySyncSceneSynchronizer
    {
        private enum PendingKind
        {
            GameObject,
            Component,
            Structure,
            Destroy
        }

        private enum HierarchyBatchPhase
        {
            BeginSnapshot,
            Hierarchy,
            FullState,
            EndSnapshot
        }

        private sealed class PendingChange
        {
            internal int GameObjectInstanceId;
            internal int ComponentIndex;
            internal string ObjectId = string.Empty;
            internal PendingKind Kind;
        }

        private sealed class HierarchyBatch
        {
            internal Guid TargetPlayerId;
            internal UnitySyncSceneSnapshotBoundary Snapshot;
            internal List<GameObject> Objects;
            internal int Index;
            internal HierarchyBatchPhase Phase;
            internal bool HasCaptureFailure;
        }

        private sealed class RemoteSnapshot
        {
            internal UnitySyncSceneSnapshotBoundary Boundary;
            internal bool HasApplyFailure;
            internal readonly HashSet<string> RepresentedObjectIds =
                new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class RemoteTransformInterpolation
        {
            internal Transform Transform;
            internal Vector3 FromPosition;
            internal Quaternion FromRotation;
            internal Vector3 FromScale;
            internal Vector3 ToPosition;
            internal Quaternion ToRotation;
            internal Vector3 ToScale;
            internal double StartTime;
        }

        private const double FlushIntervalSeconds = 0.05;
        private const double TransformSyncIntervalSeconds = 0.1;
        private const double OtherSyncIntervalSeconds = 1.0;
        private const double TransformInterpolationSeconds = 0.1;
        private const double SceneSettingsCheckIntervalSeconds = 0.1;
        private const double SceneSettingsSyncDelaySeconds = 1.0;
        private const int MaximumChangesPerUpdate = 64;
        private const int SnapshotObjectsPerUpdate = 8;

        private static readonly Dictionary<string, PendingChange> Pending =
            new Dictionary<string, PendingChange>();
        private static readonly Dictionary<string, string> KnownHashes =
            new Dictionary<string, string>();
        private static readonly Dictionary<string, double> NextAllowedSendTimes =
            new Dictionary<string, double>();
        private static readonly Dictionary<string, RemoteTransformInterpolation> RemoteTransformInterpolations =
            new Dictionary<string, RemoteTransformInterpolation>();
        private static readonly Queue<HierarchyBatch> HierarchyBatches =
            new Queue<HierarchyBatch>();
        private static readonly HashSet<int> BatchedObjectInstanceIds =
            new HashSet<int>();

        private static bool _active;
        private static bool _applyingRemoteChange;
        private static double _nextFlushTime;
        private static double _nextSceneSettingsCheckTime;
        private static double _sceneSettingsSendAfterTime;
        private static string _knownSceneSettingsSignature = string.Empty;
        private static string _pendingSceneSettingsSignature = string.Empty;
        private static RemoteSnapshot _remoteSnapshot;

        static UnitySyncSceneSynchronizer()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            EditorSceneManager.sceneDirtied += OnSceneDirtied;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.update += UpdateRemoteTransformInterpolations;
        }

        internal static void BeginSession()
        {
            CompleteRemoteTransformInterpolations();
            _active = true;
            _nextFlushTime = 0d;
            _nextSceneSettingsCheckTime = 0d;
            _sceneSettingsSendAfterTime = 0d;
            _knownSceneSettingsSignature = UnitySyncSceneSerializer.GetRenderSettingsFingerprint();
            _pendingSceneSettingsSignature = string.Empty;
            Pending.Clear();
            KnownHashes.Clear();
            NextAllowedSendTimes.Clear();
            RemoteTransformInterpolations.Clear();
            HierarchyBatches.Clear();
            BatchedObjectInstanceIds.Clear();
            _remoteSnapshot = null;
            UnitySyncSceneObjectRegistry.Clear();
        }

        internal static void EndSession()
        {
            _active = false;
            CompleteRemoteTransformInterpolations();
            _knownSceneSettingsSignature = string.Empty;
            _pendingSceneSettingsSignature = string.Empty;
            _nextSceneSettingsCheckTime = 0d;
            _sceneSettingsSendAfterTime = 0d;
            Pending.Clear();
            KnownHashes.Clear();
            NextAllowedSendTimes.Clear();
            RemoteTransformInterpolations.Clear();
            HierarchyBatches.Clear();
            BatchedObjectInstanceIds.Clear();
            _remoteSnapshot = null;
            UnitySyncSceneObjectRegistry.Clear();
        }

        internal static void MarkSceneSettingsChanged()
        {
            if (!_active || _applyingRemoteChange)
            {
                return;
            }

            // RenderSettings Inspector edits can fire repeatedly while dragging controls.
            // Keep checking promptly, but delay transmission until one second after the
            // latest edit so only the settled value is synchronized.
            _nextSceneSettingsCheckTime = 0d;
            _sceneSettingsSendAfterTime =
                EditorApplication.timeSinceStartup + SceneSettingsSyncDelaySeconds;
        }

        private static void OnSceneDirtied(Scene scene)
        {
            if (!_active || _applyingRemoteChange)
            {
                return;
            }

            // Most scene changes are unrelated to lighting, so only accelerate the normal
            // signature check here. A real RenderSettings Undo modification uses the stronger
            // MarkSceneSettingsChanged path above.
            _nextSceneSettingsCheckTime = 0d;
        }

        private static void OnSceneSaved(Scene scene)
        {
            MarkSceneSettingsChanged();
        }

        internal static void QueueFullSceneSnapshot(Guid targetPlayerId)
        {
            if (!_active)
            {
                return;
            }

            HierarchyBatches.Enqueue(new HierarchyBatch
            {
                TargetPlayerId = targetPlayerId,
                Snapshot = new UnitySyncSceneSnapshotBoundary
                {
                    SnapshotId = Guid.NewGuid(),
                    Scenes = UnitySyncSceneSerializer.GetLoadedSceneDescriptors()
                },
                Objects = UnitySyncSceneSerializer.GetAllSceneObjects(),
                Phase = HierarchyBatchPhase.BeginSnapshot
            });
        }

        internal static bool BeginRemoteSnapshot(
            UnitySyncSceneSnapshotBoundary snapshot,
            out string error)
        {
            error = string.Empty;
            if (snapshot == null || snapshot.SnapshotId == Guid.Empty)
            {
                error = "The host sent an invalid scene snapshot.";
                return false;
            }

            if (_remoteSnapshot != null)
            {
                error = "A previous scene snapshot is still being applied.";
                return false;
            }

            _remoteSnapshot = new RemoteSnapshot
            {
                Boundary = snapshot
            };
            return true;
        }

        internal static bool CompleteRemoteSnapshot(
            Guid snapshotId,
            bool hostStateComplete,
            out string error)
        {
            error = string.Empty;
            if (_remoteSnapshot == null || _remoteSnapshot.Boundary.SnapshotId != snapshotId)
            {
                error = "The host ended an unknown scene snapshot.";
                return false;
            }

            RemoteSnapshot completedSnapshot = _remoteSnapshot;
            _remoteSnapshot = null;
            if (!hostStateComplete)
            {
                error = "The host could not serialize one or more objects, so unmatched local " +
                        "objects were kept instead of being removed.";
                return false;
            }

            if (completedSnapshot.HasApplyFailure)
            {
                error = "One or more host objects could not be applied, so unmatched local " +
                        "objects were kept instead of being removed.";
                return false;
            }

            _applyingRemoteChange = true;
            try
            {
                if (!UnitySyncSceneSerializer.ApplySceneSettings(
                        completedSnapshot.Boundary,
                        out error))
                {
                    return false;
                }

                _knownSceneSettingsSignature =
                    UnitySyncSceneSerializer.GetRenderSettingsFingerprint();
                _pendingSceneSettingsSignature = string.Empty;
                _sceneSettingsSendAfterTime = 0d;

                return UnitySyncSceneSerializer.PruneSnapshot(
                    completedSnapshot.Boundary,
                    completedSnapshot.RepresentedObjectIds,
                    out error);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                _applyingRemoteChange = false;
            }
        }

        internal static void Flush(UnitySyncTransport transport, Guid localPlayerId)
        {
            if (!_active || transport == null || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now < _nextFlushTime)
            {
                return;
            }

            _nextFlushTime = now + FlushIntervalSeconds;
            FlushSceneSettingsIfNeeded(transport, localPlayerId, now);
            if (FlushHierarchyBatch(transport, localPlayerId))
            {
                return;
            }

            FlushPendingChanges(transport, localPlayerId, now);
        }

        internal static bool ApplyRemoteSceneSettings(
            UnitySyncSceneSnapshotBoundary settings,
            out string error)
        {
            _applyingRemoteChange = true;
            try
            {
                if (!UnitySyncSceneSerializer.ApplySceneSettings(settings, out error))
                {
                    return false;
                }

                _knownSceneSettingsSignature =
                    UnitySyncSceneSerializer.GetRenderSettingsFingerprint();
                _pendingSceneSettingsSignature = string.Empty;
                _sceneSettingsSendAfterTime = 0d;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                _applyingRemoteChange = false;
            }
        }

        private static void FlushSceneSettingsIfNeeded(
            UnitySyncTransport transport,
            Guid localPlayerId,
            double now)
        {
            if (now < _nextSceneSettingsCheckTime || _applyingRemoteChange)
            {
                return;
            }

            _nextSceneSettingsCheckTime = now + SceneSettingsCheckIntervalSeconds;
            string signature = UnitySyncSceneSerializer.GetRenderSettingsFingerprint();
            if (string.Equals(signature, _knownSceneSettingsSignature, StringComparison.Ordinal))
            {
                _pendingSceneSettingsSignature = string.Empty;
                _sceneSettingsSendAfterTime = 0d;
                return;
            }

            if (!string.Equals(
                    signature,
                    _pendingSceneSettingsSignature,
                    StringComparison.Ordinal))
            {
                _pendingSceneSettingsSignature = signature;
                _sceneSettingsSendAfterTime = now + SceneSettingsSyncDelaySeconds;
                return;
            }

            if (_sceneSettingsSendAfterTime <= 0d)
            {
                _sceneSettingsSendAfterTime = now + SceneSettingsSyncDelaySeconds;
                return;
            }

            if (now < _sceneSettingsSendAfterTime)
            {
                return;
            }

            if (!UnitySyncSceneSerializer.TryGetActiveSceneDescriptor(
                    out UnitySyncSceneDescriptor activeScene))
            {
                // Do not advance the known signature. A transient Editor scene-state issue
                // should be retried rather than turned into an invalid zero-scene packet.
                _nextSceneSettingsCheckTime = now + SceneSettingsCheckIntervalSeconds;
                transport.LogLocal(
                    "Environment settings changed, but the active scene could not be captured; retrying.");
                return;
            }

            UnitySyncSceneSnapshotBoundary settings = new UnitySyncSceneSnapshotBoundary
            {
                SnapshotId = Guid.NewGuid(),
                Scenes = new[] { activeScene }
            };
            _knownSceneSettingsSignature = signature;
            _pendingSceneSettingsSignature = string.Empty;
            _sceneSettingsSendAfterTime = 0d;
            transport.SendSceneSettingsChange(localPlayerId, settings);
            transport.LogLocal(
                "Sent scene environment settings update for active scene " +
                activeScene.SceneName + ".");
        }

        internal static bool ApplyRemoteChange(UnitySyncSceneObjectChange change, out string error)
        {
            error = string.Empty;
            if (change == null)
            {
                error = "The scene update was empty.";
                return false;
            }

            if (change.SnapshotId != Guid.Empty &&
                (_remoteSnapshot == null || _remoteSnapshot.Boundary.SnapshotId != change.SnapshotId))
            {
                error = "The scene update does not belong to the active host snapshot.";
                return false;
            }

            bool interpolateTransform = TryGetLiveTransform(change, out Transform transform);
            Vector3 fromPosition = default;
            Quaternion fromRotation = default;
            Vector3 fromScale = default;
            if (interpolateTransform)
            {
                fromPosition = transform.localPosition;
                fromRotation = transform.localRotation;
                fromScale = transform.localScale;
            }

            _applyingRemoteChange = true;
            try
            {
                if (!UnitySyncSceneSerializer.Apply(change, out error))
                {
                    if (change.SnapshotId != Guid.Empty && _remoteSnapshot != null)
                    {
                        _remoteSnapshot.HasApplyFailure = true;
                    }

                    return false;
                }

                if (change.SnapshotId != Guid.Empty &&
                    change.HierarchyOnly &&
                    change.Address != null &&
                    !string.IsNullOrEmpty(change.Address.ObjectId))
                {
                    _remoteSnapshot.RepresentedObjectIds.Add(change.Address.ObjectId);
                }

                // Remember the final remote state before rewinding a live Transform for
                // interpolation so echo suppression compares against the real target state.
                RememberAppliedState(change);

                if (interpolateTransform && transform != null)
                {
                    BeginRemoteTransformInterpolation(
                        change.Address.ObjectId,
                        transform,
                        fromPosition,
                        fromRotation,
                        fromScale,
                        transform.localPosition,
                        transform.localRotation,
                        transform.localScale);
                }

                return true;
            }
            catch (Exception exception)
            {
                if (change.SnapshotId != Guid.Empty && _remoteSnapshot != null)
                {
                    _remoteSnapshot.HasApplyFailure = true;
                }

                error = exception.Message;
                return false;
            }
            finally
            {
                _applyingRemoteChange = false;
            }
        }

        private static bool TryGetLiveTransform(
            UnitySyncSceneObjectChange change,
            out Transform transform)
        {
            transform = null;
            if (change == null ||
                change.Address == null ||
                change.SnapshotId != Guid.Empty ||
                change.Kind != UnitySyncSceneChangeKind.Upsert ||
                change.HierarchyOnly ||
                change.ReconcileComponents ||
                change.GameObject != null ||
                change.Components == null ||
                change.Components.Length != 1 ||
                change.Components[0] == null)
            {
                return false;
            }

            GameObject gameObject = UnitySyncSceneSerializer.ResolveAddress(change.Address);
            if (gameObject == null)
            {
                return false;
            }

            int componentIndex = change.Components[0].ComponentIndex;
            Component[] components = gameObject.GetComponents<Component>();
            if (componentIndex < 0 || componentIndex >= components.Length)
            {
                return false;
            }

            transform = components[componentIndex] as Transform;
            return transform != null;
        }

        private static void BeginRemoteTransformInterpolation(
            string objectId,
            Transform transform,
            Vector3 fromPosition,
            Quaternion fromRotation,
            Vector3 fromScale,
            Vector3 toPosition,
            Quaternion toRotation,
            Vector3 toScale)
        {
            if (string.IsNullOrEmpty(objectId) || transform == null)
            {
                return;
            }

            bool positionChanged = (fromPosition - toPosition).sqrMagnitude > 0.0000000001f;
            bool rotationChanged = Quaternion.Angle(fromRotation, toRotation) > 0.0001f;
            bool scaleChanged = (fromScale - toScale).sqrMagnitude > 0.0000000001f;
            if (!positionChanged && !rotationChanged && !scaleChanged)
            {
                RemoteTransformInterpolations.Remove(objectId);
                return;
            }

            RemoteTransformInterpolations[objectId] = new RemoteTransformInterpolation
            {
                Transform = transform,
                FromPosition = fromPosition,
                FromRotation = fromRotation,
                FromScale = fromScale,
                ToPosition = toPosition,
                ToRotation = toRotation,
                ToScale = toScale,
                StartTime = EditorApplication.timeSinceStartup
            };

            transform.localPosition = fromPosition;
            transform.localRotation = fromRotation;
            transform.localScale = fromScale;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void UpdateRemoteTransformInterpolations()
        {
            if (!_active || _applyingRemoteChange || RemoteTransformInterpolations.Count == 0)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            List<string> completed = null;
            bool changed = false;
            bool previousApplyingRemoteChange = _applyingRemoteChange;
            _applyingRemoteChange = true;
            try
            {
                foreach (KeyValuePair<string, RemoteTransformInterpolation> pair in
                         RemoteTransformInterpolations)
                {
                    RemoteTransformInterpolation interpolation = pair.Value;
                    Transform target = interpolation.Transform;
                    if (target == null)
                    {
                        if (completed == null)
                        {
                            completed = new List<string>();
                        }

                        completed.Add(pair.Key);
                        continue;
                    }

                    float amount = Mathf.Clamp01(
                        (float)((now - interpolation.StartTime) / TransformInterpolationSeconds));
                    target.localPosition = Vector3.Lerp(
                        interpolation.FromPosition,
                        interpolation.ToPosition,
                        amount);
                    target.localRotation = Quaternion.Lerp(
                        interpolation.FromRotation,
                        interpolation.ToRotation,
                        amount);
                    target.localScale = Vector3.Lerp(
                        interpolation.FromScale,
                        interpolation.ToScale,
                        amount);
                    changed = true;

                    if (amount >= 1f)
                    {
                        if (completed == null)
                        {
                            completed = new List<string>();
                        }

                        completed.Add(pair.Key);
                    }
                }
            }
            finally
            {
                _applyingRemoteChange = previousApplyingRemoteChange;
            }

            if (completed != null)
            {
                foreach (string objectId in completed)
                {
                    RemoteTransformInterpolations.Remove(objectId);
                }
            }

            if (changed)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
        }

        private static void CompleteRemoteTransformInterpolations()
        {
            if (RemoteTransformInterpolations.Count == 0)
            {
                return;
            }

            bool previousApplyingRemoteChange = _applyingRemoteChange;
            _applyingRemoteChange = true;
            try
            {
                foreach (RemoteTransformInterpolation interpolation in
                         RemoteTransformInterpolations.Values)
                {
                    Transform target = interpolation.Transform;
                    if (target == null)
                    {
                        continue;
                    }

                    target.localPosition = interpolation.ToPosition;
                    target.localRotation = interpolation.ToRotation;
                    target.localScale = interpolation.ToScale;
                }
            }
            finally
            {
                _applyingRemoteChange = previousApplyingRemoteChange;
                RemoteTransformInterpolations.Clear();
            }
        }

        private static bool IsRemoteTransformInterpolating(GameObject gameObject)
        {
            return gameObject != null &&
                   UnitySyncSceneObjectRegistry.TryGetId(gameObject, out string objectId) &&
                   !string.IsNullOrEmpty(objectId) &&
                   RemoteTransformInterpolations.ContainsKey(objectId);
        }

        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (!_active || _applyingRemoteChange || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            for (int eventIndex = 0; eventIndex < stream.length; eventIndex++)
            {
                switch (stream.GetEventType(eventIndex))
                {
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(
                            eventIndex,
                            out CreateGameObjectHierarchyEventArgs createEvent);
                        QueueCreatedHierarchy(
                            EditorUtility.InstanceIDToObject(createEvent.instanceId) as GameObject);
                        break;

                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(
                            eventIndex,
                            out DestroyGameObjectHierarchyEventArgs destroyEvent);
                        MarkDestroyedHierarchy(destroyEvent.instanceId);
                        break;

                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(
                            eventIndex,
                            out ChangeGameObjectOrComponentPropertiesEventArgs propertiesEvent);
                        MarkPropertiesChanged(EditorUtility.InstanceIDToObject(propertiesEvent.instanceId));
                        break;

                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(
                            eventIndex,
                            out ChangeGameObjectStructureEventArgs structureEvent);
                        MarkStructureChanged(
                            EditorUtility.InstanceIDToObject(structureEvent.instanceId) as GameObject);
                        break;

                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(
                            eventIndex,
                            out ChangeGameObjectStructureHierarchyEventArgs hierarchyEvent);
                        MarkHierarchyStructureChanged(
                            EditorUtility.InstanceIDToObject(hierarchyEvent.instanceId) as GameObject);
                        break;

                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(
                            eventIndex,
                            out ChangeGameObjectParentEventArgs parentEvent);
                        MarkStructureChanged(
                            EditorUtility.InstanceIDToObject(parentEvent.instanceId) as GameObject);
                        break;
                }
            }
        }

        private static void MarkPropertiesChanged(Object changedObject)
        {
            if (changedObject is GameObject gameObject)
            {
                AddPending(gameObject, PendingKind.GameObject, -1);
                return;
            }

            if (!(changedObject is Component component) || component == null)
            {
                return;
            }

            if (component is Transform && IsRemoteTransformInterpolating(component.gameObject))
            {
                return;
            }

            Component[] components = component.gameObject.GetComponents<Component>();
            for (int index = 0; index < components.Length; index++)
            {
                if (components[index] == component)
                {
                    AddPending(component.gameObject, PendingKind.Component, index);
                    return;
                }
            }
        }

        private static void MarkStructureChanged(GameObject gameObject)
        {
            AddPending(gameObject, PendingKind.Structure, -1);
        }

        private static void MarkHierarchyStructureChanged(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            MarkStructureChanged(gameObject);
            for (int childIndex = 0; childIndex < gameObject.transform.childCount; childIndex++)
            {
                MarkHierarchyStructureChanged(gameObject.transform.GetChild(childIndex).gameObject);
            }
        }

        private static void QueueCreatedHierarchy(GameObject root)
        {
            List<GameObject> objects = UnitySyncSceneSerializer.GetHierarchyObjects(root);
            if (objects.Count == 0)
            {
                return;
            }

            bool alreadyScheduled = true;
            foreach (GameObject gameObject in objects)
            {
                if (!BatchedObjectInstanceIds.Contains(gameObject.GetInstanceID()))
                {
                    alreadyScheduled = false;
                    break;
                }
            }

            if (alreadyScheduled)
            {
                return;
            }

            foreach (GameObject gameObject in objects)
            {
                BatchedObjectInstanceIds.Add(gameObject.GetInstanceID());
                RemovePendingForInstanceId(gameObject.GetInstanceID());
            }

            HierarchyBatches.Enqueue(new HierarchyBatch
            {
                Objects = objects,
                Phase = HierarchyBatchPhase.Hierarchy
            });
        }

        private static void MarkDestroyedHierarchy(int instanceId)
        {
            if (!UnitySyncSceneObjectRegistry.TryGetId(instanceId, out string objectId))
            {
                return;
            }

            RemovePendingForInstanceId(instanceId);
            Pending["d:" + objectId] = new PendingChange
            {
                ObjectId = objectId,
                Kind = PendingKind.Destroy
            };
            UnitySyncSceneObjectRegistry.ForgetHierarchy(objectId);
        }

        private static void AddPending(GameObject gameObject, PendingKind kind, int componentIndex)
        {
            if (gameObject == null ||
                !UnitySyncSceneSerializer.TryCreateAddress(gameObject, out UnitySyncSceneObjectAddress address))
            {
                return;
            }

            int instanceId = gameObject.GetInstanceID();
            string structureKey = instanceId + ":s";
            if (kind != PendingKind.Structure && Pending.ContainsKey(structureKey))
            {
                return;
            }

            if (kind == PendingKind.Structure)
            {
                RemovePendingForInstanceId(instanceId);
            }

            string pendingKey = kind == PendingKind.GameObject
                ? instanceId + ":g"
                : kind == PendingKind.Structure
                    ? structureKey
                    : instanceId + ":c:" + componentIndex;
            Pending[pendingKey] = new PendingChange
            {
                GameObjectInstanceId = instanceId,
                ComponentIndex = componentIndex,
                ObjectId = address.ObjectId,
                Kind = kind
            };
        }

        private static void RemovePendingForInstanceId(int instanceId)
        {
            List<string> obsoleteKeys = new List<string>();
            string prefix = instanceId + ":";
            foreach (string key in Pending.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    obsoleteKeys.Add(key);
                }
            }

            foreach (string key in obsoleteKeys)
            {
                Pending.Remove(key);
            }
        }

        private static bool FlushHierarchyBatch(UnitySyncTransport transport, Guid localPlayerId)
        {
            if (HierarchyBatches.Count == 0)
            {
                return false;
            }

            HierarchyBatch batch = HierarchyBatches.Peek();
            if (batch.Phase == HierarchyBatchPhase.BeginSnapshot)
            {
                transport.SendSceneSnapshotBegin(localPlayerId, batch.Snapshot, batch.TargetPlayerId);
                batch.Phase = HierarchyBatchPhase.Hierarchy;
                return true;
            }

            if (batch.Phase == HierarchyBatchPhase.EndSnapshot)
            {
                transport.SendSceneSnapshotEnd(
                    localPlayerId,
                    batch.Snapshot.SnapshotId,
                    !batch.HasCaptureFailure,
                    batch.TargetPlayerId);
                HierarchyBatches.Dequeue();
                RemoveBatchTracking(batch);
                return true;
            }

            int sent = 0;
            while (batch.Index < batch.Objects.Count && sent < SnapshotObjectsPerUpdate)
            {
                GameObject gameObject = batch.Objects[batch.Index++];
                UnitySyncSceneObjectChange change;
                bool captured = batch.Phase == HierarchyBatchPhase.Hierarchy
                    ? UnitySyncSceneSerializer.TryCaptureHierarchy(gameObject, out change)
                    : UnitySyncSceneSerializer.TryCaptureFullObject(gameObject, out change);
                if (!captured)
                {
                    batch.HasCaptureFailure = true;
                    continue;
                }

                if (batch.Snapshot != null)
                {
                    change.SnapshotId = batch.Snapshot.SnapshotId;
                }

                transport.SendSceneObjectChange(localPlayerId, change, batch.TargetPlayerId);
                sent++;
            }

            if (batch.Index >= batch.Objects.Count)
            {
                batch.Index = 0;
                if (batch.Phase == HierarchyBatchPhase.Hierarchy)
                {
                    batch.Phase = HierarchyBatchPhase.FullState;
                }
                else
                {
                    batch.Phase = batch.Snapshot != null
                        ? HierarchyBatchPhase.EndSnapshot
                        : HierarchyBatchPhase.FullState;
                    if (batch.Snapshot == null)
                    {
                        HierarchyBatches.Dequeue();
                        RemoveBatchTracking(batch);
                    }
                }
            }

            return true;
        }

        private static void RemoveBatchTracking(HierarchyBatch batch)
        {
            if (batch.Snapshot != null || batch.Objects == null)
            {
                return;
            }

            foreach (GameObject gameObject in batch.Objects)
            {
                if (gameObject != null)
                {
                    BatchedObjectInstanceIds.Remove(gameObject.GetInstanceID());
                }
            }
        }

        private static bool IsTransformPending(PendingChange pending)
        {
            if (pending == null || pending.Kind != PendingKind.Component)
            {
                return false;
            }

            GameObject gameObject =
                EditorUtility.InstanceIDToObject(pending.GameObjectInstanceId) as GameObject;
            if (gameObject == null)
            {
                return false;
            }

            Component[] components = gameObject.GetComponents<Component>();
            return pending.ComponentIndex >= 0 &&
                   pending.ComponentIndex < components.Length &&
                   components[pending.ComponentIndex] is Transform;
        }

        private static void FlushPendingChanges(
            UnitySyncTransport transport,
            Guid localPlayerId,
            double now)
        {
            if (Pending.Count == 0)
            {
                return;
            }

            List<string> keys = new List<string>(Pending.Keys);
            int sent = 0;
            for (int index = 0; index < keys.Count && sent < MaximumChangesPerUpdate; index++)
            {
                string pendingKey = keys[index];
                if (!Pending.TryGetValue(pendingKey, out PendingChange pending))
                {
                    continue;
                }

                bool isTransform = IsTransformPending(pending);
                double interval = isTransform
                    ? TransformSyncIntervalSeconds
                    : OtherSyncIntervalSeconds;
                if (NextAllowedSendTimes.TryGetValue(pendingKey, out double nextAllowedSendTime) &&
                    now < nextAllowedSendTime)
                {
                    continue;
                }

                Pending.Remove(pendingKey);
                GameObject gameObject = pending.Kind == PendingKind.Destroy
                    ? null
                    : EditorUtility.InstanceIDToObject(pending.GameObjectInstanceId) as GameObject;
                if (!TryCapture(pending, gameObject, out UnitySyncSceneObjectChange change))
                {
                    continue;
                }

                string stateKey = GetStateKey(change);
                string hash = string.Empty;
                bool hasHash = change.Kind != UnitySyncSceneChangeKind.Destroy &&
                               TryGetHash(change, out hash);
                if (hasHash &&
                    KnownHashes.TryGetValue(stateKey, out string knownHash) &&
                    knownHash == hash)
                {
                    continue;
                }

                if (hasHash)
                {
                    KnownHashes[stateKey] = hash;
                }

                transport.SendSceneObjectChange(localPlayerId, change);
                NextAllowedSendTimes[pendingKey] = now + interval;
                sent++;
            }
        }

        private static bool TryCapture(
            PendingChange pending,
            GameObject gameObject,
            out UnitySyncSceneObjectChange change)
        {
            change = null;
            if (pending.Kind == PendingKind.Destroy)
            {
                return UnitySyncSceneSerializer.TryCaptureDestroyedObject(pending.ObjectId, out change);
            }

            if (gameObject == null)
            {
                return false;
            }

            switch (pending.Kind)
            {
                case PendingKind.GameObject:
                    return UnitySyncSceneSerializer.TryCaptureGameObject(gameObject, out change);

                case PendingKind.Structure:
                    return UnitySyncSceneSerializer.TryCaptureFullObject(gameObject, out change);

                case PendingKind.Component:
                    Component[] components = gameObject.GetComponents<Component>();
                    return pending.ComponentIndex >= 0 &&
                           pending.ComponentIndex < components.Length &&
                           components[pending.ComponentIndex] != null &&
                           UnitySyncSceneSerializer.TryCaptureComponent(
                               components[pending.ComponentIndex],
                               out change);

                default:
                    return false;
            }
        }

        private static void RememberAppliedState(UnitySyncSceneObjectChange receivedChange)
        {
            if (receivedChange == null ||
                receivedChange.Address == null ||
                receivedChange.Kind == UnitySyncSceneChangeKind.Destroy ||
                receivedChange.HierarchyOnly)
            {
                return;
            }

            GameObject gameObject = UnitySyncSceneSerializer.ResolveAddress(receivedChange.Address);
            if (gameObject == null)
            {
                return;
            }

            UnitySyncSceneObjectChange appliedChange = null;
            if (receivedChange.ReconcileComponents)
            {
                UnitySyncSceneSerializer.TryCaptureFullObject(gameObject, out appliedChange);
            }
            else if (receivedChange.GameObject != null)
            {
                UnitySyncSceneSerializer.TryCaptureGameObject(gameObject, out appliedChange);
            }
            else if (receivedChange.Components != null && receivedChange.Components.Length > 0)
            {
                int componentIndex = receivedChange.Components[0].ComponentIndex;
                Component[] components = gameObject.GetComponents<Component>();
                if (componentIndex >= 0 &&
                    componentIndex < components.Length &&
                    components[componentIndex] != null)
                {
                    UnitySyncSceneSerializer.TryCaptureComponent(
                        components[componentIndex],
                        out appliedChange);
                }
            }

            if (appliedChange != null)
            {
                Remember(appliedChange);
            }
        }

        private static void Remember(UnitySyncSceneObjectChange change)
        {
            if (change == null || change.Address == null ||
                change.Kind == UnitySyncSceneChangeKind.Destroy || change.HierarchyOnly)
            {
                return;
            }

            KnownHashes[GetStateKey(change)] = GetHash(change);

            if (change.ReconcileComponents)
            {
                UnitySyncSceneObjectChange gameObjectOnly = new UnitySyncSceneObjectChange
                {
                    Kind = UnitySyncSceneChangeKind.Upsert,
                    Address = change.Address,
                    GameObject = change.GameObject
                };
                if (change.GameObject != null)
                {
                    KnownHashes[GetStateKey(gameObjectOnly)] = GetHash(gameObjectOnly);
                }

                foreach (UnitySyncComponentState component in change.Components)
                {
                    UnitySyncSceneObjectChange componentOnly = new UnitySyncSceneObjectChange
                    {
                        Kind = UnitySyncSceneChangeKind.Upsert,
                        Address = change.Address,
                        Components = new[] { component }
                    };
                    KnownHashes[GetStateKey(componentOnly)] = GetHash(componentOnly);
                }
            }
        }

        private static string GetStateKey(UnitySyncSceneObjectChange change)
        {
            if (change.Kind == UnitySyncSceneChangeKind.Destroy)
            {
                return change.Address.Key + "|d";
            }

            if (change.HierarchyOnly)
            {
                return change.Address.Key + "|h";
            }

            if (change.ReconcileComponents)
            {
                return change.Address.Key + "|s";
            }

            if (change.GameObject != null)
            {
                return change.Address.Key + "|g";
            }

            int componentIndex = change.Components != null && change.Components.Length > 0
                ? change.Components[0].ComponentIndex
                : -1;
            return change.Address.Key + "|c:" + componentIndex;
        }

        private static string GetHash(UnitySyncSceneObjectChange change)
        {
            byte[] payload = UnitySyncProtocol.CreateSceneObjectChange(Guid.Empty, change);
            using (SHA256 sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(payload));
            }
        }

        private static bool TryGetHash(UnitySyncSceneObjectChange change, out string hash)
        {
            try
            {
                hash = GetHash(change);
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is InvalidDataException ||
                exception is OverflowException)
            {
                hash = string.Empty;
                return false;
            }
        }
    }
}
