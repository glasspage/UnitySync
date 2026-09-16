using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.Profiling;
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
            internal readonly HashSet<int> FullStateSceneHandles = new HashSet<int>();
            internal readonly HashSet<int> FullStateComponentInstanceIds = new HashSet<int>();
            internal readonly HashSet<string> SentHierarchyObjectIds =
                new HashSet<string>(StringComparer.Ordinal);
            internal int Index;
            internal int ComponentIndex;
            internal HierarchyBatchPhase Phase;
            internal bool HasCaptureFailure;
        }

        private sealed class RemoteSnapshot
        {
            internal UnitySyncSceneSnapshotBoundary Boundary;
            internal bool HasApplyFailure;
            internal int AppliedChangeCount;
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

        private sealed class CachedSnapshotComponentState
        {
            internal Component Component;
            internal UnitySyncComponentState State;
        }

        private sealed class RemoteInitializationState
        {
            internal int GameObjectInstanceId;
            internal double LastUpdateTime;
            internal readonly HashSet<int> PendingComponentIndices = new HashSet<int>();
        }

        private sealed class DeferredRemoteChange
        {
            internal UnitySyncSceneObjectChange Change;
            internal string StateHash = string.Empty;
            internal string LastError = string.Empty;
            internal double FirstDeferredTime;
            internal double NextRetryTime;
        }

        private const double FlushIntervalSeconds = 0.05;
        private const double TransformSyncIntervalSeconds = 0.1;
        private const double OtherSyncIntervalSeconds = 1.0;
        private const double TransformInterpolationSeconds = 0.1;
        private const double SceneSettingsCheckIntervalSeconds = 0.1;
        private const double SceneSettingsSyncDelaySeconds = 1.0;
        private const double RemoteInitializationTimeoutSeconds = 2.0;
        private const double DeferredRemoteAssetRetryDelaySeconds = 2.0;
        private const int MaximumDeferredRemoteRetriesPerUpdate = 8;
        private const int MaximumChangesPerUpdate = 64;
        private const int MaximumPendingKeysExaminedPerUpdate = 128;
        private const int SnapshotObjectsPerUpdate = 16;
        private const double CaptureBudgetSeconds = 0.008;

        private static readonly ProfilerMarker SceneSettingsFlushMarker =
            new ProfilerMarker("UnitySync.SceneSync.SceneSettings");
        private static readonly ProfilerMarker HierarchyFlushMarker =
            new ProfilerMarker("UnitySync.SceneSync.Hierarchy");
        private static readonly ProfilerMarker PendingFlushMarker =
            new ProfilerMarker("UnitySync.SceneSync.PendingChanges");
        private static readonly ProfilerMarker RemoteSerializerApplyMarker =
            new ProfilerMarker("US.Scene.SerializerApply");
        private static readonly ProfilerMarker RemoteRememberStateMarker =
            new ProfilerMarker("US.Scene.RememberState");
        private static readonly ProfilerMarker SnapshotEndSettingsMarker =
            new ProfilerMarker("US.SnapEnd.Settings");
        private static readonly ProfilerMarker SnapshotEndFingerprintMarker =
            new ProfilerMarker("US.SnapEnd.Fingerprint");
        private static readonly ProfilerMarker SnapshotEndPruneMarker =
            new ProfilerMarker("US.SnapEnd.Prune");
        private static readonly ProfilerMarker SnapshotComponentCompareMarker =
            new ProfilerMarker("US.Snapshot.Compare");
        private static readonly Dictionary<string, PendingChange> Pending =
            new Dictionary<string, PendingChange>();
        private static readonly Queue<string> PendingOrder = new Queue<string>();
        private static readonly Dictionary<int, HashSet<string>> PendingKeysByInstanceId =
            new Dictionary<int, HashSet<string>>();
        private static readonly Dictionary<string, string> KnownHashes =
            new Dictionary<string, string>();
        private static readonly Dictionary<string, string> LastIncomingLiveHashes =
            new Dictionary<string, string>();
        private static readonly Dictionary<string, double> NextAllowedSendTimes =
            new Dictionary<string, double>();
        private static readonly Dictionary<string, RemoteTransformInterpolation> RemoteTransformInterpolations =
            new Dictionary<string, RemoteTransformInterpolation>();
        private static readonly Dictionary<string, RemoteInitializationState> RemoteInitializations =
            new Dictionary<string, RemoteInitializationState>(StringComparer.Ordinal);
        private static readonly HashSet<string> CompletedRemoteInitializations =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DeferredRemoteChange> DeferredRemoteChanges =
            new Dictionary<string, DeferredRemoteChange>(StringComparer.Ordinal);
        private static readonly Queue<HierarchyBatch> HierarchyBatches =
            new Queue<HierarchyBatch>();
        private static readonly HashSet<int> BatchedObjectInstanceIds =
            new HashSet<int>();

        private static readonly Dictionary<int, CachedSnapshotComponentState>
            SnapshotComponentStates =
                new Dictionary<int, CachedSnapshotComponentState>();

        private static readonly List<Component> SnapshotComponentBuffer =
            new List<Component>(16);

        private static bool _active;
        private static bool _applyingRemoteChange;
        private static bool _suppressPublishedSnapshotChanges;
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
            EditorApplication.update += UpdateDeferredRemoteChanges;
        }

        internal static string DebugBackgroundWork
        {
            get
            {
                if (_remoteSnapshot != null)
                {
                    return "applying scene snapshot";
                }

                if (HierarchyBatches.Count > 0)
                {
                    return "sending scene snapshot";
                }

                if (DeferredRemoteChanges.Count > 0)
                {
                    return "waiting for synchronized asset";
                }

                if (Pending.Count > 0)
                {
                    return "syncing " + Pending.Count + " scene " +
                           (Pending.Count == 1 ? "change" : "changes");
                }

                return string.Empty;
            }
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
            PendingOrder.Clear();
            PendingKeysByInstanceId.Clear();
            KnownHashes.Clear();
            LastIncomingLiveHashes.Clear();
            NextAllowedSendTimes.Clear();
            RemoteTransformInterpolations.Clear();
            RemoteInitializations.Clear();
            CompletedRemoteInitializations.Clear();
            DeferredRemoteChanges.Clear();
            EditorApplication.delayCall -= ReleaseCompletedRemoteInitializations;
            HierarchyBatches.Clear();
            BatchedObjectInstanceIds.Clear();
            SnapshotComponentStates.Clear();
            _remoteSnapshot = null;
            _suppressPublishedSnapshotChanges = false;
            EditorApplication.delayCall -= ReleaseSnapshotChangeSuppression;
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
            PendingOrder.Clear();
            PendingKeysByInstanceId.Clear();
            KnownHashes.Clear();
            LastIncomingLiveHashes.Clear();
            NextAllowedSendTimes.Clear();
            RemoteTransformInterpolations.Clear();
            RemoteInitializations.Clear();
            CompletedRemoteInitializations.Clear();
            DeferredRemoteChanges.Clear();
            EditorApplication.delayCall -= ReleaseCompletedRemoteInitializations;
            HierarchyBatches.Clear();
            BatchedObjectInstanceIds.Clear();
            SnapshotComponentStates.Clear();
            _remoteSnapshot = null;
            _suppressPublishedSnapshotChanges = false;
            EditorApplication.delayCall -= ReleaseSnapshotChangeSuppression;
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

            // A reconnecting guest keeps the same player identity. If an earlier connection
            // dropped while its snapshot was still being emitted, that unfinished batch must
            // not run ahead of the new connection's snapshot request.
            CancelSnapshotsForPlayer(targetPlayerId);

            HierarchyBatch batch = new HierarchyBatch
            {
                TargetPlayerId = targetPlayerId,
                Snapshot = new UnitySyncSceneSnapshotBoundary
                {
                    SnapshotId = Guid.NewGuid(),
                    Scenes = UnitySyncSceneSerializer.GetLoadedSceneDescriptors()
                },
                Objects = UnitySyncSceneSerializer.GetAllSceneObjects(),
                Phase = HierarchyBatchPhase.BeginSnapshot
            };

            // File/asset synchronization completes before the guest requests this snapshot,
            // and PrepareScenesForSnapshot opens those synchronized scene assets on the guest.
            // Saved scene files therefore provide the shared component baseline. For a dirty
            // saved scene, replay only components Unity actually marks dirty; otherwise a tiny
            // unsaved edit makes the guest deeply compare every component in the scene. Unsaved
            // scenes have no shared file baseline, so they still require every component.
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() ||
                    !scene.isLoaded ||
                    EditorSceneManager.IsPreviewScene(scene))
                {
                    continue;
                }

                if (scene.isDirty || string.IsNullOrEmpty(scene.path))
                {
                    batch.FullStateSceneHandles.Add(scene.handle);
                }
            }

            foreach (GameObject gameObject in batch.Objects ?? new List<GameObject>())
            {
                if (gameObject == null ||
                    !batch.FullStateSceneHandles.Contains(gameObject.scene.handle))
                {
                    continue;
                }

                bool sendEveryComponent = string.IsNullOrEmpty(gameObject.scene.path);
                Component[] components = gameObject.GetComponents<Component>();
                foreach (Component component in components)
                {
                    if (component != null &&
                        (sendEveryComponent || EditorUtility.IsDirty(component)))
                    {
                        batch.FullStateComponentInstanceIds.Add(component.GetInstanceID());
                    }
                }
            }

            batch.Snapshot.TotalChangeCount =
                (batch.Objects != null ? batch.Objects.Count : 0) +
                batch.FullStateComponentInstanceIds.Count;
            HierarchyBatches.Enqueue(batch);
        }

        internal static void CancelSnapshotsForPlayer(Guid targetPlayerId)
        {
            if (targetPlayerId == Guid.Empty || HierarchyBatches.Count == 0)
            {
                return;
            }

            int batchCount = HierarchyBatches.Count;
            for (int index = 0; index < batchCount; index++)
            {
                HierarchyBatch batch = HierarchyBatches.Dequeue();
                if (batch.Snapshot != null && batch.TargetPlayerId == targetPlayerId)
                {
                    continue;
                }

                HierarchyBatches.Enqueue(batch);
            }
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

            _suppressPublishedSnapshotChanges = true;
            EditorApplication.delayCall -= ReleaseSnapshotChangeSuppression;
            _applyingRemoteChange = true;
            try
            {
                if (!UnitySyncSceneSerializer.PrepareScenesForSnapshot(
                        snapshot,
                        out error))
                {
                    ScheduleSnapshotChangeSuppressionRelease();
                    return false;
                }

                Pending.Clear();
                PendingOrder.Clear();
                PendingKeysByInstanceId.Clear();
                LastIncomingLiveHashes.Clear();
                RemoteInitializations.Clear();
                CompletedRemoteInitializations.Clear();
                EditorApplication.delayCall -= ReleaseCompletedRemoteInitializations;
                UnitySyncSceneObjectRegistry.Clear();
                PruneSnapshotComponentStates();
            }
            finally
            {
                _applyingRemoteChange = false;
            }

            _remoteSnapshot = new RemoteSnapshot
            {
                Boundary = snapshot
            };
            UnitySyncPresenceRoot.RequestSceneRepaint();
            return true;
        }

        internal static bool TryGetRemoteSnapshotProgress(out float progress01)
        {
            if (_remoteSnapshot == null)
            {
                progress01 = 0f;
                return false;
            }

            int total = _remoteSnapshot.Boundary != null
                ? _remoteSnapshot.Boundary.TotalChangeCount
                : 0;
            progress01 = total > 0
                ? Mathf.Clamp01(_remoteSnapshot.AppliedChangeCount / (float)total)
                : 0f;
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
            if (!hostStateComplete)
            {
                _remoteSnapshot = null;
                UnitySyncPresenceRoot.RequestSceneRepaint();
                ScheduleSnapshotChangeSuppressionRelease();
                error = "The host could not serialize one or more objects, so unmatched local " +
                        "objects were kept instead of being removed.";
                return false;
            }

            if (completedSnapshot.HasApplyFailure)
            {
                _remoteSnapshot = null;
                UnitySyncPresenceRoot.RequestSceneRepaint();
                ScheduleSnapshotChangeSuppressionRelease();
                error = "One or more host objects could not be applied, so unmatched local " +
                        "objects were kept instead of being removed.";
                return false;
            }

            _applyingRemoteChange = true;
            try
            {
                using (SnapshotEndSettingsMarker.Auto())
                {
                    if (!UnitySyncSceneSerializer.ApplySceneSettings(
                            completedSnapshot.Boundary,
                            out error))
                    {
                        return false;
                    }
                }

                using (SnapshotEndFingerprintMarker.Auto())
                {
                    _knownSceneSettingsSignature =
                        UnitySyncSceneSerializer.GetRenderSettingsFingerprint();
                }
                _pendingSceneSettingsSignature = string.Empty;
                _sceneSettingsSendAfterTime = 0d;

                using (SnapshotEndPruneMarker.Auto())
                {
                    return UnitySyncSceneSerializer.PruneSnapshot(
                        completedSnapshot.Boundary,
                        completedSnapshot.RepresentedObjectIds,
                        out error);
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                _applyingRemoteChange = false;
                _remoteSnapshot = null;
                UnitySyncPresenceRoot.RequestSceneRepaint();
                Pending.Clear();
                PendingOrder.Clear();
                PendingKeysByInstanceId.Clear();
                ScheduleSnapshotChangeSuppressionRelease();
            }
        }

        private static void ScheduleSnapshotChangeSuppressionRelease()
        {
            EditorApplication.delayCall -= ReleaseSnapshotChangeSuppression;
            EditorApplication.delayCall += ReleaseSnapshotChangeSuppression;
        }

        private static void ReleaseSnapshotChangeSuppression()
        {
            EditorApplication.delayCall -= ReleaseSnapshotChangeSuppression;
            Pending.Clear();
            PendingOrder.Clear();
            PendingKeysByInstanceId.Clear();
            _suppressPublishedSnapshotChanges = false;
        }

        internal static void Flush(UnitySyncTransport transport, Guid localPlayerId)
        {
            if (!_active || transport == null || _remoteSnapshot != null ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now < _nextFlushTime)
            {
                return;
            }

            _nextFlushTime = now + FlushIntervalSeconds;
            using (SceneSettingsFlushMarker.Auto())
            {
                FlushSceneSettingsIfNeeded(transport, localPlayerId, now);
            }

            HierarchyBatch activeSnapshotBatch =
                HierarchyBatches.Count > 0 && HierarchyBatches.Peek().Snapshot != null
                    ? HierarchyBatches.Peek()
                    : null;

            if (activeSnapshotBatch != null)
            {
                // Live edits should not sit behind minutes of initial snapshot traffic. If the
                // edited object (or a new/current parent) has not had its synchronized identity
                // established on the guest yet, move that hierarchy chain to the front first.
                PromotePendingSnapshotHierarchy(activeSnapshotBatch);
                using (PendingFlushMarker.Auto())
                {
                    if (FlushPendingChanges(
                            transport,
                            localPlayerId,
                            now,
                            activeSnapshotBatch))
                    {
                        return;
                    }
                }
            }

            using (HierarchyFlushMarker.Auto())
            {
                if (FlushHierarchyBatch(transport, localPlayerId))
                {
                    return;
                }
            }

            using (PendingFlushMarker.Auto())
            {
                FlushPendingChanges(transport, localPlayerId, now, null);
            }
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
            return ApplyRemoteChangeInternal(change, true, out error);
        }

        private static bool ApplyRemoteChangeInternal(
            UnitySyncSceneObjectChange change,
            bool allowAssetReferenceDeferral,
            out string error)
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

            if (change.SnapshotId != Guid.Empty && _remoteSnapshot != null)
            {
                _remoteSnapshot.AppliedChangeCount++;
                UnitySyncPresenceRoot.RequestSceneRepaint();
            }

            if (change.SnapshotId == Guid.Empty)
            {
                string incomingStateKey = GetStateKey(change);
                if (TryGetHash(change, out string incomingHash) &&
                    LastIncomingLiveHashes.TryGetValue(
                        incomingStateKey,
                        out string previousIncomingHash) &&
                    previousIncomingHash == incomingHash)
                {
                    return true;
                }
            }
            else if (IsSnapshotComponentState(change))
            {
                using (SnapshotComponentCompareMarker.Auto())
                {
                    if (SnapshotComponentAlreadyMatches(change))
                    {
                        return true;
                    }
                }
            }

            bool beginsRemoteInitialization =
                change.SnapshotId == Guid.Empty &&
                change.Kind == UnitySyncSceneChangeKind.Upsert &&
                change.HierarchyOnly &&
                change.Address != null &&
                !string.IsNullOrEmpty(change.Address.ObjectId) &&
                UnitySyncSceneSerializer.ResolveAddress(change.Address) == null;
            bool suppressTransformInterpolation =
                change.Address != null &&
                IsRemoteInitializationSuppressed(change.Address.ObjectId);
            Transform transform = null;
            bool interpolateTransform =
                !suppressTransformInterpolation &&
                TryGetLiveTransform(change, out transform);
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
                using (RemoteSerializerApplyMarker.Auto())
                {
                    if (!UnitySyncSceneSerializer.Apply(change, out error))
                    {
                        if (allowAssetReferenceDeferral &&
                            IsRetryableProjectAssetResolutionFailure(change, error))
                        {
                            QueueDeferredRemoteChange(change, error);
                            error = string.Empty;
                            return true;
                        }

                        if (change.SnapshotId != Guid.Empty && _remoteSnapshot != null)
                        {
                            _remoteSnapshot.HasApplyFailure = true;
                        }

                        return false;
                    }
                }

                if (change.SnapshotId == Guid.Empty)
                {
                    DeferredRemoteChanges.Remove(GetStateKey(change));
                }

                UpdateSnapshotComponentCacheAfterApply(change);

                if (beginsRemoteInitialization)
                {
                    BeginRemoteInitialization(change);
                }
                else if (change.SnapshotId == Guid.Empty)
                {
                    AdvanceRemoteInitialization(change);
                }

                if (change.Kind == UnitySyncSceneChangeKind.Destroy &&
                    change.Address != null &&
                    !string.IsNullOrEmpty(change.Address.ObjectId))
                {
                    CancelRemoteInitialization(change.Address.ObjectId);
                }

                if (change.SnapshotId != Guid.Empty &&
                    change.HierarchyOnly &&
                    change.Address != null &&
                    !string.IsNullOrEmpty(change.Address.ObjectId))
                {
                    _remoteSnapshot.RepresentedObjectIds.Add(change.Address.ObjectId);
                }

                // Snapshot application already suppresses Unity object-change notifications
                // through the end-of-snapshot delay call, so re-capturing and hashing every
                // snapshot object here is redundant and can be very expensive in large scenes.
                // Live edits still need the applied-state hash for delayed echo suppression.
                if (change.SnapshotId == Guid.Empty)
                {
                    if (TryGetHash(change, out string appliedIncomingHash))
                    {
                        LastIncomingLiveHashes[GetStateKey(change)] = appliedIncomingHash;
                    }

                    using (RemoteRememberStateMarker.Auto())
                    {
                        RememberAppliedState(change);
                    }
                }

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

        private static void QueueDeferredRemoteChange(
            UnitySyncSceneObjectChange change,
            string error)
        {
            string stateKey = GetStateKey(change);
            string stateHash = TryGetHash(change, out string capturedHash)
                ? capturedHash
                : string.Empty;
            double now = EditorApplication.timeSinceStartup;

            if (DeferredRemoteChanges.TryGetValue(
                    stateKey,
                    out DeferredRemoteChange existing) &&
                !string.IsNullOrEmpty(stateHash) &&
                string.Equals(existing.StateHash, stateHash, StringComparison.Ordinal))
            {
                existing.Change = change;
                existing.LastError = error;
                existing.NextRetryTime = now + DeferredRemoteAssetRetryDelaySeconds;
                return;
            }

            DeferredRemoteChanges[stateKey] = new DeferredRemoteChange
            {
                Change = change,
                StateHash = stateHash,
                LastError = error,
                FirstDeferredTime = now,
                NextRetryTime = now + DeferredRemoteAssetRetryDelaySeconds
            };
        }

        private static void UpdateDeferredRemoteChanges()
        {
            if (!_active ||
                _applyingRemoteChange ||
                DeferredRemoteChanges.Count == 0 ||
                EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            List<string> readyKeys = null;
            foreach (KeyValuePair<string, DeferredRemoteChange> pair in DeferredRemoteChanges)
            {
                if (pair.Value != null && pair.Value.NextRetryTime <= now)
                {
                    if (readyKeys == null)
                    {
                        readyKeys = new List<string>();
                    }

                    readyKeys.Add(pair.Key);
                    if (readyKeys.Count >= MaximumDeferredRemoteRetriesPerUpdate)
                    {
                        break;
                    }
                }
            }

            if (readyKeys == null)
            {
                return;
            }

            foreach (string stateKey in readyKeys)
            {
                if (!DeferredRemoteChanges.TryGetValue(
                        stateKey,
                        out DeferredRemoteChange deferred) ||
                    deferred == null ||
                    deferred.Change == null)
                {
                    DeferredRemoteChanges.Remove(stateKey);
                    continue;
                }

                if (ApplyRemoteChangeInternal(
                        deferred.Change,
                        false,
                        out string retryError))
                {
                    DeferredRemoteChanges.Remove(stateKey);
                    continue;
                }

                DeferredRemoteChanges.Remove(stateKey);
                UnitySyncSession.ReportDeferredSceneSyncFailure(
                    string.IsNullOrEmpty(retryError) ? deferred.LastError : retryError);
            }
        }

        private static bool IsRetryableProjectAssetResolutionFailure(
            UnitySyncSceneObjectChange change,
            string error)
        {
            if (change == null ||
                change.SnapshotId != Guid.Empty ||
                string.IsNullOrEmpty(error))
            {
                return false;
            }

            foreach (UnitySyncComponentState component in
                     change.Components ?? new UnitySyncComponentState[0])
            {
                if (component == null)
                {
                    continue;
                }

                foreach (UnitySyncSerializedPropertyState property in
                         component.Properties ?? new UnitySyncSerializedPropertyState[0])
                {
                    UnitySyncObjectReferenceState reference =
                        property != null ? property.ObjectReference : null;
                    if (reference == null ||
                        reference.Kind != UnitySyncObjectReferenceKind.Asset ||
                        string.IsNullOrEmpty(reference.AssetPath) ||
                        !reference.AssetPath.StartsWith(
                            "Assets/",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string expectedErrorPrefix =
                        "Object reference " + property.Path +
                        " could not be matched safely for local field type ";
                    if (error.StartsWith(expectedErrorPrefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsSnapshotComponentState(UnitySyncSceneObjectChange change)
        {
            return change != null &&
                   change.SnapshotId != Guid.Empty &&
                   change.Kind == UnitySyncSceneChangeKind.Upsert &&
                   !change.HierarchyOnly &&
                   !change.ReconcileComponents &&
                   change.GameObject == null &&
                   change.Components != null &&
                   change.Components.Length == 1 &&
                   change.Components[0] != null;
        }

        private static bool SnapshotComponentAlreadyMatches(UnitySyncSceneObjectChange incoming)
        {
            if (!TryResolveSingleComponentState(incoming, out Component component))
            {
                return false;
            }

            UnitySyncComponentState incomingState = incoming.Components[0];
            int componentInstanceId = component.GetInstanceID();
            if (SnapshotComponentStates.TryGetValue(
                    componentInstanceId,
                    out CachedSnapshotComponentState cached) &&
                ReferenceEquals(cached.Component, component))
            {
                // Local property changes and live remote component updates invalidate this
                // cache. If the host state differs from the previous authoritative snapshot,
                // the local component cannot already match it.
                return UnitySyncSceneSerializer.ComponentStatesEqual(
                    cached.State,
                    incomingState);
            }

            if (!UnitySyncSceneSerializer.ComponentMatchesState(component, incomingState))
            {
                return false;
            }

            CacheSnapshotComponentState(component, incomingState);
            return true;
        }

        private static bool TryResolveSingleComponentState(
            UnitySyncSceneObjectChange change,
            out Component component)
        {
            component = null;
            if (change == null ||
                change.Address == null ||
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
            SnapshotComponentBuffer.Clear();
            gameObject.GetComponents(SnapshotComponentBuffer);
            if (componentIndex < 0 ||
                componentIndex >= SnapshotComponentBuffer.Count ||
                SnapshotComponentBuffer[componentIndex] == null)
            {
                SnapshotComponentBuffer.Clear();
                return false;
            }

            component = SnapshotComponentBuffer[componentIndex];
            SnapshotComponentBuffer.Clear();
            return true;
        }

        private static void CacheSnapshotComponentState(
            Component component,
            UnitySyncComponentState state)
        {
            if (component == null || state == null)
            {
                return;
            }

            SnapshotComponentStates[component.GetInstanceID()] =
                new CachedSnapshotComponentState
                {
                    Component = component,
                    State = state
                };
        }

        private static void InvalidateSnapshotComponentState(Component component)
        {
            if (component != null)
            {
                SnapshotComponentStates.Remove(component.GetInstanceID());
            }
        }

        private static void InvalidateSnapshotComponentStates(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            Component[] components = gameObject.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component != null)
                {
                    InvalidateSnapshotComponentState(component);
                }
            }
        }

        private static void PruneSnapshotComponentStates()
        {
            if (SnapshotComponentStates.Count == 0)
            {
                return;
            }

            List<int> staleInstanceIds = null;
            foreach (KeyValuePair<int, CachedSnapshotComponentState> pair in SnapshotComponentStates)
            {
                if (pair.Value != null && pair.Value.Component != null)
                {
                    continue;
                }

                if (staleInstanceIds == null)
                {
                    staleInstanceIds = new List<int>();
                }

                staleInstanceIds.Add(pair.Key);
            }

            if (staleInstanceIds == null)
            {
                return;
            }

            foreach (int instanceId in staleInstanceIds)
            {
                SnapshotComponentStates.Remove(instanceId);
            }
        }

        private static void UpdateSnapshotComponentCacheAfterApply(
            UnitySyncSceneObjectChange change)
        {
            if (change == null ||
                change.Kind != UnitySyncSceneChangeKind.Upsert ||
                change.HierarchyOnly)
            {
                return;
            }

            if (change.ReconcileComponents)
            {
                InvalidateSnapshotComponentStates(
                    UnitySyncSceneSerializer.ResolveAddress(change.Address));
                return;
            }

            if (!TryResolveSingleComponentState(change, out Component component))
            {
                return;
            }

            if (change.SnapshotId != Guid.Empty)
            {
                CacheSnapshotComponentState(component, change.Components[0]);
            }
            else
            {
                // Live remote edits are applied while ObjectChangeEvents are suppressed.
                // Explicit invalidation prevents a later snapshot from trusting stale state.
                InvalidateSnapshotComponentState(component);
            }
        }

        private static void BeginRemoteInitialization(UnitySyncSceneObjectChange change)
        {
            if (change == null ||
                change.Address == null ||
                string.IsNullOrEmpty(change.Address.ObjectId))
            {
                return;
            }

            GameObject gameObject = UnitySyncSceneSerializer.ResolveAddress(change.Address);
            if (gameObject == null)
            {
                return;
            }

            RemoteInitializationState state = new RemoteInitializationState
            {
                GameObjectInstanceId = gameObject.GetInstanceID(),
                LastUpdateTime = EditorApplication.timeSinceStartup
            };

            foreach (UnitySyncComponentState componentState in
                     change.Components ?? new UnitySyncComponentState[0])
            {
                // Missing-script slots have no component object to serialize in the live
                // full-state phase, so they must not keep initialization suppressed forever.
                if (componentState != null &&
                    !string.IsNullOrEmpty(componentState.TypeName))
                {
                    state.PendingComponentIndices.Add(componentState.ComponentIndex);
                }
            }

            if (state.PendingComponentIndices.Count == 0)
            {
                return;
            }

            RemoteInitializations[change.Address.ObjectId] = state;
        }

        private static void AdvanceRemoteInitialization(UnitySyncSceneObjectChange change)
        {
            if (change == null ||
                change.Address == null ||
                string.IsNullOrEmpty(change.Address.ObjectId) ||
                !RemoteInitializations.TryGetValue(
                    change.Address.ObjectId,
                    out RemoteInitializationState state))
            {
                return;
            }

            state.LastUpdateTime = EditorApplication.timeSinceStartup;
            foreach (UnitySyncComponentState componentState in
                     change.Components ?? new UnitySyncComponentState[0])
            {
                if (componentState != null)
                {
                    state.PendingComponentIndices.Remove(componentState.ComponentIndex);
                }
            }

            if (state.PendingComponentIndices.Count == 0)
            {
                CompletedRemoteInitializations.Add(change.Address.ObjectId);
                EditorApplication.delayCall -= ReleaseCompletedRemoteInitializations;
                EditorApplication.delayCall += ReleaseCompletedRemoteInitializations;
            }
        }

        private static bool IsRemoteInitializationSuppressed(string objectId)
        {
            if (string.IsNullOrEmpty(objectId) ||
                !RemoteInitializations.TryGetValue(
                    objectId,
                    out RemoteInitializationState state))
            {
                return false;
            }

            if (EditorApplication.timeSinceStartup - state.LastUpdateTime <=
                RemoteInitializationTimeoutSeconds)
            {
                return true;
            }

            CancelRemoteInitialization(objectId);
            return false;
        }

        private static bool IsRemoteInitializationSuppressed(GameObject gameObject)
        {
            return gameObject != null &&
                   UnitySyncSceneObjectRegistry.TryGetId(gameObject, out string objectId) &&
                   IsRemoteInitializationSuppressed(objectId);
        }

        private static void CancelRemoteInitialization(string objectId)
        {
            if (string.IsNullOrEmpty(objectId))
            {
                return;
            }

            RemoteInitializations.Remove(objectId);
            CompletedRemoteInitializations.Remove(objectId);
        }

        private static void ReleaseCompletedRemoteInitializations()
        {
            EditorApplication.delayCall -= ReleaseCompletedRemoteInitializations;
            if (CompletedRemoteInitializations.Count == 0)
            {
                return;
            }

            foreach (string objectId in CompletedRemoteInitializations)
            {
                if (!RemoteInitializations.TryGetValue(
                        objectId,
                        out RemoteInitializationState state) ||
                    state.PendingComponentIndices.Count != 0)
                {
                    continue;
                }

                // Drop delayed Unity notifications produced by constructing/applying the
                // remote object before allowing genuine local edits to publish again.
                RemovePendingForInstanceId(state.GameObjectInstanceId);
                RemoteInitializations.Remove(objectId);
            }

            CompletedRemoteInitializations.Clear();
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
                UnitySyncPresenceRoot.RequestSceneRepaint();
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
            if (!_active || _applyingRemoteChange || _remoteSnapshot != null ||
                _suppressPublishedSnapshotChanges ||
                EditorApplication.isPlayingOrWillChangePlaymode)
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
                if (!IsRemoteInitializationSuppressed(gameObject))
                {
                    AddPending(gameObject, PendingKind.GameObject, -1);
                }
                return;
            }

            if (!(changedObject is Component component) || component == null)
            {
                return;
            }

            if (IsRemoteInitializationSuppressed(component.gameObject))
            {
                return;
            }

            InvalidateSnapshotComponentState(component);

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
            if (!IsRemoteInitializationSuppressed(gameObject))
            {
                AddPending(gameObject, PendingKind.Structure, -1);
            }
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
            if (IsRemoteInitializationSuppressed(root))
            {
                return;
            }

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
            SetPending(
                "d:" + objectId,
                new PendingChange
                {
                    ObjectId = objectId,
                    Kind = PendingKind.Destroy
                });
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
            SetPending(
                pendingKey,
                new PendingChange
                {
                    GameObjectInstanceId = instanceId,
                    ComponentIndex = componentIndex,
                    ObjectId = address.ObjectId,
                    Kind = kind
                });
        }

        private static void SetPending(string key, PendingChange change)
        {
            if (!Pending.ContainsKey(key))
            {
                PendingOrder.Enqueue(key);
                if (change.GameObjectInstanceId != 0)
                {
                    if (!PendingKeysByInstanceId.TryGetValue(
                            change.GameObjectInstanceId,
                            out HashSet<string> instanceKeys))
                    {
                        instanceKeys = new HashSet<string>(StringComparer.Ordinal);
                        PendingKeysByInstanceId.Add(change.GameObjectInstanceId, instanceKeys);
                    }

                    instanceKeys.Add(key);
                }
            }

            Pending[key] = change;
        }

        private static void RemovePending(string key, PendingChange change)
        {
            Pending.Remove(key);
            if (change == null || change.GameObjectInstanceId == 0 ||
                !PendingKeysByInstanceId.TryGetValue(
                    change.GameObjectInstanceId,
                    out HashSet<string> instanceKeys))
            {
                return;
            }

            instanceKeys.Remove(key);
            if (instanceKeys.Count == 0)
            {
                PendingKeysByInstanceId.Remove(change.GameObjectInstanceId);
            }
        }

        private static void RemovePendingForInstanceId(int instanceId)
        {
            if (!PendingKeysByInstanceId.TryGetValue(instanceId, out HashSet<string> keys))
            {
                return;
            }

            foreach (string key in keys)
            {
                Pending.Remove(key);
            }

            PendingKeysByInstanceId.Remove(instanceId);
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

            int attempted = 0;
            long captureStart = System.Diagnostics.Stopwatch.GetTimestamp();

            if (batch.Phase == HierarchyBatchPhase.Hierarchy)
            {
                while (batch.Index < batch.Objects.Count &&
                       attempted < SnapshotObjectsPerUpdate &&
                       (attempted == 0 || HasCaptureTimeRemaining(captureStart)))
                {
                    attempted++;
                    GameObject gameObject = batch.Objects[batch.Index++];
                    if (!UnitySyncSceneSerializer.TryCaptureHierarchy(
                            gameObject,
                            out UnitySyncSceneObjectChange change))
                    {
                        batch.HasCaptureFailure = true;
                        continue;
                    }

                    // This hierarchy packet already contains the current GameObject settings.
                    // Seed that baseline before adding the snapshot ID so a follow-up Unity
                    // property event for the same value is not sent again as a live update.
                    RememberHierarchyGameObject(change);

                    if (batch.Snapshot != null)
                    {
                        change.SnapshotId = batch.Snapshot.SnapshotId;
                    }

                    transport.SendSceneObjectChange(localPlayerId, change, batch.TargetPlayerId);
                    if (change.Address != null &&
                        !string.IsNullOrEmpty(change.Address.ObjectId))
                    {
                        batch.SentHierarchyObjectIds.Add(change.Address.ObjectId);
                    }
                }

                if (batch.Index >= batch.Objects.Count)
                {
                    batch.Index = 0;
                    batch.ComponentIndex = 0;
                    batch.Phase = HierarchyBatchPhase.FullState;
                }

                return true;
            }

            // Hierarchy packets already established the GameObject settings and exact component
            // layout. Send serialized state one component at a time so one incoming event can
            // never apply every component on a large GameObject in one Editor update.
            while (batch.Index < batch.Objects.Count &&
                   attempted < SnapshotObjectsPerUpdate &&
                   (attempted == 0 || HasCaptureTimeRemaining(captureStart)))
            {
                GameObject gameObject = batch.Objects[batch.Index];
                if (gameObject == null)
                {
                    batch.HasCaptureFailure = true;
                    batch.Index++;
                    batch.ComponentIndex = 0;
                    continue;
                }

                // Snapshot batches can rely on the synchronized saved-scene baseline and
                // selectively replay only dirty component state. A live-created hierarchy has
                // no receiver-side baseline, so every current component must follow its
                // hierarchy shell.
                if (batch.Snapshot != null &&
                    !batch.FullStateSceneHandles.Contains(gameObject.scene.handle))
                {
                    batch.Index++;
                    batch.ComponentIndex = 0;
                    continue;
                }

                Component[] components = gameObject.GetComponents<Component>();
                while (batch.ComponentIndex < components.Length)
                {
                    Component candidate = components[batch.ComponentIndex];
                    if (candidate != null &&
                        (batch.Snapshot == null ||
                         batch.FullStateComponentInstanceIds.Contains(candidate.GetInstanceID())))
                    {
                        break;
                    }

                    // Missing-script slots and unchanged components need no serialized state
                    // beyond the hierarchy packet / synchronized saved scene baseline.
                    batch.ComponentIndex++;
                }

                if (batch.ComponentIndex >= components.Length)
                {
                    batch.Index++;
                    batch.ComponentIndex = 0;
                    continue;
                }

                Component component = components[batch.ComponentIndex++];
                attempted++;
                if (!UnitySyncSceneSerializer.TryCaptureComponent(
                        component,
                        out UnitySyncSceneObjectChange change))
                {
                    batch.HasCaptureFailure = true;
                    continue;
                }

                // The component state is now authoritative locally as well. Remember it before
                // adding the snapshot ID, because live hashes intentionally do not include a
                // snapshot boundary. This suppresses delayed/no-op Unity change notifications
                // that would otherwise resend the exact same component state.
                Remember(change);

                if (batch.Snapshot != null)
                {
                    change.SnapshotId = batch.Snapshot.SnapshotId;
                }

                transport.SendSceneObjectChange(localPlayerId, change, batch.TargetPlayerId);
            }

            if (batch.Index >= batch.Objects.Count)
            {
                batch.Index = 0;
                batch.ComponentIndex = 0;
                batch.Phase = batch.Snapshot != null
                    ? HierarchyBatchPhase.EndSnapshot
                    : HierarchyBatchPhase.FullState;
                if (batch.Snapshot == null)
                {
                    HierarchyBatches.Dequeue();
                    RemoveBatchTracking(batch);
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

        private static bool FlushPendingChanges(
            UnitySyncTransport transport,
            Guid localPlayerId,
            double now,
            HierarchyBatch activeSnapshotBatch)
        {
            if (Pending.Count == 0)
            {
                // Removal can leave stale queue entries. Drop them without revisiting
                // every former key in later idle updates.
                PendingOrder.Clear();
                return false;
            }

            bool sentAny = false;
            int attempted = 0;
            int examined = 0;
            int keysAvailableAtStart = PendingOrder.Count;
            long captureStart = System.Diagnostics.Stopwatch.GetTimestamp();
            while (PendingOrder.Count > 0 &&
                   examined < keysAvailableAtStart &&
                   examined < MaximumPendingKeysExaminedPerUpdate &&
                   attempted < MaximumChangesPerUpdate &&
                   HasCaptureTimeRemaining(captureStart))
            {
                string pendingKey = PendingOrder.Dequeue();
                examined++;
                if (!Pending.TryGetValue(pendingKey, out PendingChange pending))
                {
                    continue;
                }

                if (activeSnapshotBatch != null &&
                    !CanSendPendingDuringSnapshot(activeSnapshotBatch, pending))
                {
                    PendingOrder.Enqueue(pendingKey);
                    continue;
                }

                bool isTransform = IsTransformPending(pending);
                double interval = isTransform
                    ? TransformSyncIntervalSeconds
                    : OtherSyncIntervalSeconds;
                if (NextAllowedSendTimes.TryGetValue(pendingKey, out double nextAllowedSendTime) &&
                    now < nextAllowedSendTime)
                {
                    PendingOrder.Enqueue(pendingKey);
                    continue;
                }

                // Count failed and unchanged captures too, not only transmitted updates.
                attempted++;
                NextAllowedSendTimes[pendingKey] = now + interval;
                RemovePending(pendingKey, pending);
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

                // Asset references are a cross-pipeline dependency: the referenced project
                // files must be queued before the scene packet that points at them. If the
                // asset is not ready on disk yet, keep this scene change pending locally
                // instead of sending an update the receiver cannot possibly resolve.
                if (!UnitySyncProjectSynchronizer.EnsureSceneAssetReferencesQueued(
                        transport,
                        localPlayerId,
                        change))
                {
                    SetPending(pendingKey, pending);
                    continue;
                }

                if (hasHash)
                {
                    // Full structural packets contain GameObject and component state together.
                    // Remember all of those sub-states, not only the aggregate structure hash,
                    // so follow-up Unity events for values already included in this packet are
                    // suppressed instead of being serialized and sent again.
                    Remember(change, stateKey, hash);
                }

                // A real local edit means a later packet equal to an older remote state must be
                // allowed through; only suppress consecutive identical incoming live states.
                LastIncomingLiveHashes.Remove(stateKey);
                transport.SendSceneObjectChange(localPlayerId, change);
                sentAny = true;
            }

            return sentAny;
        }

        private static bool CanSendPendingDuringSnapshot(
            HierarchyBatch batch,
            PendingChange pending)
        {
            if (batch == null || batch.Snapshot == null)
            {
                return true;
            }

            if (batch.Phase == HierarchyBatchPhase.BeginSnapshot)
            {
                return false;
            }

            if (batch.Phase != HierarchyBatchPhase.Hierarchy)
            {
                return true;
            }

            if (pending == null ||
                string.IsNullOrEmpty(pending.ObjectId) ||
                !batch.SentHierarchyObjectIds.Contains(pending.ObjectId))
            {
                return false;
            }

            if (pending.Kind == PendingKind.Destroy || pending.GameObjectInstanceId == 0)
            {
                return true;
            }

            GameObject gameObject =
                EditorUtility.InstanceIDToObject(pending.GameObjectInstanceId) as GameObject;
            if (gameObject == null)
            {
                return false;
            }

            Transform parent = gameObject.transform.parent;
            while (parent != null)
            {
                if (!UnitySyncSceneObjectRegistry.TryGetId(
                        parent.gameObject,
                        out string parentObjectId) ||
                    !batch.SentHierarchyObjectIds.Contains(parentObjectId))
                {
                    return false;
                }

                parent = parent.parent;
            }

            return true;
        }

        private static void PromotePendingSnapshotHierarchy(HierarchyBatch batch)
        {
            if (batch == null ||
                batch.Snapshot == null ||
                batch.Phase != HierarchyBatchPhase.Hierarchy ||
                batch.Objects == null ||
                batch.Index >= batch.Objects.Count ||
                Pending.Count == 0)
            {
                return;
            }

            bool needsPromotion = false;
            foreach (PendingChange pending in Pending.Values)
            {
                if (pending != null &&
                    pending.GameObjectInstanceId != 0 &&
                    !CanSendPendingDuringSnapshot(batch, pending))
                {
                    needsPromotion = true;
                    break;
                }
            }

            if (!needsPromotion)
            {
                return;
            }

            HashSet<int> remainingInstanceIds = new HashSet<int>();
            for (int index = batch.Index; index < batch.Objects.Count; index++)
            {
                GameObject remainingObject = batch.Objects[index];
                if (remainingObject != null)
                {
                    remainingInstanceIds.Add(remainingObject.GetInstanceID());
                }
            }

            List<GameObject> promoted = new List<GameObject>();
            HashSet<int> promotedInstanceIds = new HashSet<int>();
            foreach (PendingChange pending in Pending.Values)
            {
                if (pending == null || pending.GameObjectInstanceId == 0)
                {
                    continue;
                }

                GameObject gameObject =
                    EditorUtility.InstanceIDToObject(pending.GameObjectInstanceId) as GameObject;
                if (gameObject == null)
                {
                    continue;
                }

                List<GameObject> hierarchyChain = new List<GameObject>();
                Transform current = gameObject.transform;
                while (current != null)
                {
                    hierarchyChain.Add(current.gameObject);
                    current = current.parent;
                }

                for (int chainIndex = hierarchyChain.Count - 1; chainIndex >= 0; chainIndex--)
                {
                    GameObject hierarchyObject = hierarchyChain[chainIndex];
                    int instanceId = hierarchyObject.GetInstanceID();
                    if (remainingInstanceIds.Contains(instanceId) &&
                        promotedInstanceIds.Add(instanceId))
                    {
                        promoted.Add(hierarchyObject);
                    }
                }
            }

            if (promoted.Count == 0)
            {
                return;
            }

            List<GameObject> reordered = new List<GameObject>(batch.Objects.Count - batch.Index);
            reordered.AddRange(promoted);
            for (int index = batch.Index; index < batch.Objects.Count; index++)
            {
                GameObject remainingObject = batch.Objects[index];
                if (remainingObject == null ||
                    !promotedInstanceIds.Contains(remainingObject.GetInstanceID()))
                {
                    reordered.Add(remainingObject);
                }
            }

            for (int offset = 0; offset < reordered.Count; offset++)
            {
                batch.Objects[batch.Index + offset] = reordered[offset];
            }
        }

        private static bool HasCaptureTimeRemaining(long startTimestamp)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) /
                   (double)System.Diagnostics.Stopwatch.Frequency < CaptureBudgetSeconds;
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

            Remember(change, GetStateKey(change), GetHash(change));
        }

        private static void Remember(
            UnitySyncSceneObjectChange change,
            string stateKey,
            string hash)
        {
            if (change == null || change.Address == null ||
                change.Kind == UnitySyncSceneChangeKind.Destroy || change.HierarchyOnly)
            {
                return;
            }

            KnownHashes[stateKey] = hash;

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

                foreach (UnitySyncComponentState component in
                         change.Components ?? new UnitySyncComponentState[0])
                {
                    if (component == null)
                    {
                        continue;
                    }

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

        private static void RememberHierarchyGameObject(UnitySyncSceneObjectChange change)
        {
            if (change == null ||
                change.Address == null ||
                !change.HierarchyOnly ||
                change.GameObject == null)
            {
                return;
            }

            UnitySyncSceneObjectChange gameObjectOnly = new UnitySyncSceneObjectChange
            {
                Kind = UnitySyncSceneChangeKind.Upsert,
                Address = change.Address,
                GameObject = change.GameObject
            };
            KnownHashes[GetStateKey(gameObjectOnly)] = GetHash(gameObjectOnly);
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
