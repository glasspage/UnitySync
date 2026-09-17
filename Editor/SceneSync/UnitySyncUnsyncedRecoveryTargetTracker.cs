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
        private static readonly FieldInfo FailedRemoteChangesField =
            typeof(UnitySyncSceneSynchronizer).GetField(
                "FailedRemoteChanges",
                BindingFlags.Static | BindingFlags.NonPublic);

        private static readonly Dictionary<string, GameObject> TrackedTargets =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);

        private static Type _failedRemoteChangeType;
        private static FieldInfo _changeField;
        private static FieldInfo _errorField;

        static UnitySyncUnsyncedRecoveryTargetTracker()
        {
            EditorApplication.update += Update;
        }

        private static void Update()
        {
            if (!UnitySyncSession.IsActive ||
                !UnitySyncSession.HasUnsyncedSceneObjects)
            {
                TrackedTargets.Clear();
                return;
            }

            IDictionary failedChanges =
                FailedRemoteChangesField?.GetValue(null) as IDictionary;
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
                    // Hold the actual Unity object reference, not only its instance ID. Unity's
                    // destroyed-object null semantics then let us distinguish a target that was
                    // genuinely deleted from a temporarily unresolved address.
                    TrackedTargets[stateKey] = currentTarget;
                    continue;
                }

                bool targetWasDeleted =
                    TrackedTargets.TryGetValue(
                        stateKey,
                        out GameObject trackedTarget) &&
                    trackedTarget == null;
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

            if (staleKeys == null)
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
