using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    [InitializeOnLoad]
    internal static class UnitySyncHierarchy
    {
        private const string RootName = "[UnitySync]";
        private const string LegacyCollaboratorsRootName = "[UnitySync] Collaborators";

        private static GameObject _root;

        static UnitySyncHierarchy()
        {
            EditorApplication.delayCall += CleanupOrphanedRoots;
        }

        internal static GameObject GetOrCreateContainer(string name)
        {
            EnsureRoot();

            Transform existing = _root.transform.Find(name);
            if (existing != null)
            {
                Configure(existing.gameObject);
                return existing.gameObject;
            }

            GameObject container = new GameObject(name);
            Configure(container);
            container.transform.SetParent(_root.transform, false);
            return container;
        }

        internal static void DestroyContainer(GameObject container)
        {
            if (container != null)
            {
                Object.DestroyImmediate(container);
            }

            if (_root != null && _root.transform.childCount == 0)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        internal static void Configure(GameObject gameObject)
        {
            gameObject.hideFlags = HideFlags.DontSaveInEditor | HideFlags.NotEditable;
            SetEditorOnlyTag(gameObject);
        }

        internal static bool IsUnitySyncObject(GameObject gameObject)
        {
            Transform current = gameObject != null ? gameObject.transform : null;
            while (current != null)
            {
                GameObject currentObject = current.gameObject;
                if (currentObject.name == RootName &&
                    (currentObject.hideFlags & HideFlags.DontSaveInEditor) != 0)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static void EnsureRoot()
        {
            if (_root != null)
            {
                return;
            }

            CleanupOrphanedRoots();
            _root = new GameObject(RootName);
            Configure(_root);
        }

        private static void CleanupOrphanedRoots()
        {
            GameObject[] gameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            foreach (GameObject gameObject in gameObjects)
            {
                if (gameObject == null ||
                    gameObject == _root ||
                    (gameObject.name != RootName && gameObject.name != LegacyCollaboratorsRootName) ||
                    EditorUtility.IsPersistent(gameObject) ||
                    (gameObject.hideFlags & HideFlags.DontSaveInEditor) == 0)
                {
                    continue;
                }

                Object.DestroyImmediate(gameObject);
            }
        }

        private static void SetEditorOnlyTag(GameObject gameObject)
        {
            try
            {
                gameObject.tag = "EditorOnly";
            }
            catch (UnityException)
            {
                // DontSaveInEditor still prevents persistence when project tags are unavailable.
            }
        }
    }
}
