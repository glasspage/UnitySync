using System;
using System.Collections.Generic;
using UnityEngine;

namespace Glasspage.UnitySync
{
    // Object identities deliberately live only for the current collaboration session. Persisting
    // package-specific IDs into every scene object would make UnitySync edits visible in projects.
    internal static class UnitySyncSceneObjectRegistry
    {
        private static readonly Dictionary<int, string> IdByInstanceId =
            new Dictionary<int, string>();
        private static readonly Dictionary<string, GameObject> ObjectById =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ParentIdById =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void Clear()
        {
            IdByInstanceId.Clear();
            ObjectById.Clear();
            ParentIdById.Clear();
        }

        internal static string GetOrCreateId(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return string.Empty;
            }

            int instanceId = gameObject.GetInstanceID();
            if (IdByInstanceId.TryGetValue(instanceId, out string existingId))
            {
                ObjectById[existingId] = gameObject;
                return existingId;
            }

            string id = Guid.NewGuid().ToString("N");
            Assign(gameObject, id);
            return id;
        }

        internal static bool TryGetId(GameObject gameObject, out string id)
        {
            id = string.Empty;
            return gameObject != null && IdByInstanceId.TryGetValue(gameObject.GetInstanceID(), out id);
        }

        internal static bool TryGetId(int instanceId, out string id)
        {
            return IdByInstanceId.TryGetValue(instanceId, out id);
        }

        internal static void Assign(GameObject gameObject, string id)
        {
            if (gameObject == null || !Guid.TryParse(id, out _))
            {
                return;
            }

            int instanceId = gameObject.GetInstanceID();
            if (IdByInstanceId.TryGetValue(instanceId, out string previousId) && previousId != id)
            {
                ObjectById.Remove(previousId);
                ParentIdById.Remove(previousId);
            }

            if (ObjectById.TryGetValue(id, out GameObject previousObject) &&
                previousObject != null &&
                previousObject != gameObject)
            {
                IdByInstanceId.Remove(previousObject.GetInstanceID());
            }

            IdByInstanceId[instanceId] = id;
            ObjectById[id] = gameObject;
        }

        internal static bool TryResolve(string id, out GameObject gameObject)
        {
            gameObject = null;
            if (string.IsNullOrEmpty(id) || !ObjectById.TryGetValue(id, out GameObject result))
            {
                return false;
            }

            if (result == null)
            {
                ObjectById.Remove(id);
                ParentIdById.Remove(id);
                return false;
            }

            gameObject = result;
            return true;
        }

        internal static void SetParent(string id, string parentId)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            ParentIdById[id] = parentId ?? string.Empty;
        }

        internal static void ForgetHierarchy(string rootId)
        {
            if (string.IsNullOrEmpty(rootId))
            {
                return;
            }

            HashSet<string> idsToRemove = new HashSet<string>(StringComparer.Ordinal)
            {
                rootId
            };
            bool foundMore;
            do
            {
                foundMore = false;
                foreach (KeyValuePair<string, string> pair in ParentIdById)
                {
                    if (idsToRemove.Contains(pair.Value) && idsToRemove.Add(pair.Key))
                    {
                        foundMore = true;
                    }
                }
            }
            while (foundMore);

            foreach (string id in idsToRemove)
            {
                if (ObjectById.TryGetValue(id, out GameObject gameObject) && gameObject != null)
                {
                    IdByInstanceId.Remove(gameObject.GetInstanceID());
                }

                ObjectById.Remove(id);
                ParentIdById.Remove(id);
            }
        }
    }
}
