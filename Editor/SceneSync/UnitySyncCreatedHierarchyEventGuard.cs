using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    [InitializeOnLoad]
    internal static class UnitySyncCreatedHierarchyEventGuard
    {
        private static readonly MethodInfo RemovePendingForInstanceIdMethod =
            typeof(UnitySyncSceneSynchronizer).GetMethod(
                "RemovePendingForInstanceId",
                BindingFlags.Static | BindingFlags.NonPublic);

        static UnitySyncCreatedHierarchyEventGuard()
        {
            // Force the main synchronizer to register its ObjectChangeEvents callback first.
            // This guard then runs after that callback and can discard redundant construction-
            // time changes that were queued later in the same event stream.
            _ = UnitySyncSceneSynchronizer.IsApplyingRemoteSnapshot;
            ObjectChangeEvents.changesPublished += OnChangesPublished;
        }

        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (RemovePendingForInstanceIdMethod == null)
            {
                return;
            }

            for (int eventIndex = 0; eventIndex < stream.length; eventIndex++)
            {
                if (stream.GetEventType(eventIndex) != ObjectChangeKind.CreateGameObjectHierarchy)
                {
                    continue;
                }

                stream.GetCreateGameObjectHierarchyEvent(
                    eventIndex,
                    out CreateGameObjectHierarchyEventArgs createEvent);
                GameObject root = EditorUtility.InstanceIDToObject(createEvent.instanceId) as GameObject;
                if (root != null)
                {
                    ClearPendingForHierarchy(root);
                }
            }
        }

        private static void ClearPendingForHierarchy(GameObject gameObject)
        {
            RemovePendingForInstanceIdMethod.Invoke(
                null,
                new object[] { gameObject.GetInstanceID() });

            for (int childIndex = 0; childIndex < gameObject.transform.childCount; childIndex++)
            {
                ClearPendingForHierarchy(gameObject.transform.GetChild(childIndex).gameObject);
            }
        }
    }
}
