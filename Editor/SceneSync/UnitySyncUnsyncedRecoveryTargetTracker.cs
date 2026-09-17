using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    [InitializeOnLoad]
    internal static class UnitySyncUnsyncedRecoveryTargetTracker
    {
        private sealed class TrackedTarget
        {
            internal GameObject GameObject;
            internal readonly HashSet<int> TargetAndAncestorInstanceIds =
                new HashSet<int>();
        }

        private static readonly FieldInfo FailedRemoteChangesField =
            typeof(UnitySyncSceneSynchronizer).GetField(
                "FailedRemoteChanges",
                BindingFlags.Static | BindingFlags.NonPublic);

        private static readonly Dictionary<string, TrackedTarget> TrackedTargets =
            new Dictionary<string, TrackedTarget>(StringComparer.Ordinal);

        private static Type _failedRemoteChangeType;
        private static FieldInfo _changeField;
        private static FieldInfo _errorField;

        static UnitySyncUnsyncedRecoveryTargetTracker()
        {
            EditorApplication.update += Update;
            ObjectChangeEvents.changesPublished += OnChangesPublished;
        }

        private static void Update()
        {
            if (!UnitySyncSession.IsActive ||
                !UnitySyncSession.HasUnsyncedSceneObjects)
            {
                TrackedTargets.Clear();
                return;
            }

            IDictionary failedChanges = GetFailedChanges();
            if (failedChanges == null || failedChanges.Count == 0)
            {
                TrackedTargets.Clear();
                return;
            }

            HashSet<string> liveKeys = new HashSet<string>(StringComparer.Ordinal);
            List<string> staleKeys = null;

            foreach (DictionaryEntry entry in failedChanges)
            {
                string stateKey = entry.Key as string;
                if (string.IsNullOrEmpty(stateKey))
                {
                    continue;
                }

                liveKeys.Add(stateKey);
                UnitySyncSceneObjectChange change = GetChange(entry.Value);
                if (change == null || change.Address == null)
                {
                    continue;
                }

                GameObject currentTarget =
                    UnitySyncSceneSerializer.ResolveAddress(change.Address);
                if (currentTarget != null)
                {
                    TrackedTargets[stateKey] = CreateTrackedTarget(currentTarget);
                    continue;
                }

                bool targetWasDeleted =
                    TrackedTargets.TryGetValue(
                        stateKey,
                        out TrackedTarget trackedTarget) &&
                    trackedTarget.GameObject == null;
                bool targetNeverExistedLocally =
                    !TrackedTargets.ContainsKey(stateKey) &&
                    IsMissingTargetFailure(GetLastError(entry.Value));

                // A failed property/component packet cannot reconstruct an object that does not
                // exist locally. Keeping that packet as a recovery item only makes Force Sync
                // replay an update that is guaranteed to fail with the same missing-object error.
                // Drop it just like a target that existed and was subsequently deleted.
                if (targetWasDeleted || targetNeverExistedLocally)
                {
                    if (staleKeys == null)
                    {
                        staleKeys = new List<string>();
                    }

                    staleKeys.Add(stateKey);
                }
            }

            List<string> obsoleteTrackedKeys = null;
            foreach (string trackedKey in TrackedTargets.Keys)
            {
                if (liveKeys.Contains(trackedKey))
                {
                    continue;
                }

                if (obsoleteTrackedKeys == null)
                {
                    obsoleteTrackedKeys = new List<string>();
                }

                obsoleteTrackedKeys.Add(trackedKey);
            }

            if (obsoleteTrackedKeys != null)
            {
                foreach (string obsoleteKey in obsoleteTrackedKeys)
                {
                    TrackedTargets.Remove(obsoleteKey);
                }
            }

            RemoveFailedEntries(failedChanges, staleKeys);
        }

        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (!UnitySyncSession.IsActive ||
                !UnitySyncSession.HasUnsyncedSceneObjects ||
                TrackedTargets.Count == 0)
            {
                return;
            }

            HashSet<int> destroyedHierarchyRoots = null;
            for (int eventIndex = 0; eventIndex < stream.length; eventIndex++)
            {
                if (stream.GetEventType(eventIndex) !=
                    ObjectChangeKind.DestroyGameObjectHierarchy)
                {
                    continue;
                }

                stream.GetDestroyGameObjectHierarchyEvent(
                    eventIndex,
                    out DestroyGameObjectHierarchyEventArgs destroyEvent);
                if (destroyedHierarchyRoots == null)
                {
                    destroyedHierarchyRoots = new HashSet<int>();
                }

                destroyedHierarchyRoots.Add(destroyEvent.instanceId);
            }

            if (destroyedHierarchyRoots == null || destroyedHierarchyRoots.Count == 0)
            {
                return;
            }

            List<string> staleKeys = null;
            foreach (KeyValuePair<string, TrackedTarget> pair in TrackedTargets)
            {
                TrackedTarget trackedTarget = pair.Value;
                if (trackedTarget == null)
                {
                    continue;
                }

                bool hierarchyDeleted = false;
                foreach (int destroyedRoot in destroyedHierarchyRoots)
                {
                    if (trackedTarget.TargetAndAncestorInstanceIds.Contains(destroyedRoot))
                    {
                        hierarchyDeleted = true;
                        break;
                    }
                }

                if (!hierarchyDeleted)
                {
                    continue;
                }

                if (staleKeys == null)
                {
                    staleKeys = new List<string>();
                }

                staleKeys.Add(pair.Key);
            }

            RemoveFailedEntries(GetFailedChanges(), staleKeys);
        }

        private static TrackedTarget CreateTrackedTarget(GameObject target)
        {
            TrackedTarget tracked = new TrackedTarget
            {
                GameObject = target
            };

            Transform current = target != null ? target.transform : null;
            while (current != null)
            {
                tracked.TargetAndAncestorInstanceIds.Add(current.gameObject.GetInstanceID());
                current = current.parent;
            }

            return tracked;
        }

        private static IDictionary GetFailedChanges()
        {
            return FailedRemoteChangesField?.GetValue(null) as IDictionary;
        }

        private static void RemoveFailedEntries(
            IDictionary failedChanges,
            List<string> staleKeys)
        {
            if (failedChanges == null || staleKeys == null || staleKeys.Count == 0)
            {
                return;
            }

            bool removedAny = false;
            foreach (string staleKey in staleKeys)
            {
                if (failedChanges.Contains(staleKey))
                {
                    failedChanges.Remove(staleKey);
                    removedAny = true;
                }

                TrackedTargets.Remove(staleKey);
            }

            if (removedAny)
            {
                UnitySyncSession.NotifyUnsyncedSceneStateChanged();
            }
        }

        private static UnitySyncSceneObjectChange GetChange(object failedRemoteChange)
        {
            EnsureFailedRemoteChangeFields(failedRemoteChange);
            return _changeField?.GetValue(failedRemoteChange) as UnitySyncSceneObjectChange;
        }

        private static string GetLastError(object failedRemoteChange)
        {
            EnsureFailedRemoteChangeFields(failedRemoteChange);
            return _errorField?.GetValue(failedRemoteChange) as string ?? string.Empty;
        }

        private static void EnsureFailedRemoteChangeFields(object failedRemoteChange)
        {
            if (failedRemoteChange == null)
            {
                return;
            }

            Type failedType = failedRemoteChange.GetType();
            if (_failedRemoteChangeType == failedType)
            {
                return;
            }

            _failedRemoteChangeType = failedType;
            _changeField = failedType.GetField(
                "Change",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _errorField = failedType.GetField(
                "LastError",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static bool IsMissingTargetFailure(string error)
        {
            return !string.IsNullOrEmpty(error) &&
                   error.IndexOf(
                       "No matching scene object exists",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
