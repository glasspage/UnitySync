using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Glasspage.UnitySync
{
    internal static class UnitySyncSceneSerializer
    {
        private sealed class ResolvedObjectReferenceAssignment
        {
            internal string Path = string.Empty;
            internal UnitySyncSerializedValueKind Kind;
            internal UnitySyncObjectReferenceState Reference;
            internal Object Value;
        }

        private sealed class AssetFileHashCacheEntry
        {
            internal long Length;
            internal long LastWriteTicks;
            internal string Hash = string.Empty;
        }

        private static readonly Dictionary<string, AssetFileHashCacheEntry> AssetFileHashCache =
            new Dictionary<string, AssetFileHashCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly PropertyInfo GradientValueProperty = typeof(SerializedProperty).GetProperty(
            "gradientValue",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly PropertyInfo TransformConstrainProportionsProperty = typeof(Transform).GetProperty(
            "constrainProportionsScale",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly HashSet<string> IgnoredPropertyPaths = new HashSet<string>
        {
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject",
            "m_Script",
            "m_Children",
            "m_Father",
            "m_RootOrder"
        };

        internal static bool TryCaptureGameObject(GameObject gameObject, out UnitySyncSceneObjectChange change)
        {
            change = null;
            if (!TryCreateAddress(gameObject, out UnitySyncSceneObjectAddress address))
            {
                return false;
            }

            change = new UnitySyncSceneObjectChange
            {
                Kind = UnitySyncSceneChangeKind.Upsert,
                Address = address,
                GameObject = CaptureGameObjectSettings(gameObject)
            };
            return true;
        }

        internal static bool TryCaptureHierarchy(GameObject gameObject, out UnitySyncSceneObjectChange change)
        {
            change = null;
            if (!TryCreateAddress(gameObject, out UnitySyncSceneObjectAddress address))
            {
                return false;
            }

            Component[] components = gameObject.GetComponents<Component>();
            UnitySyncComponentState[] componentStates = new UnitySyncComponentState[components.Length];
            for (int index = 0; index < components.Length; index++)
            {
                componentStates[index] = new UnitySyncComponentState
                {
                    ComponentIndex = index,
                    TypeName = components[index] != null
                        ? GetStableTypeName(components[index].GetType())
                        : string.Empty
                };
            }

            change = new UnitySyncSceneObjectChange
            {
                Kind = UnitySyncSceneChangeKind.Upsert,
                Address = address,
                HierarchyOnly = true,
                ReconcileComponents = true,
                GameObject = CaptureGameObjectSettings(gameObject),
                Components = componentStates
            };
            return true;
        }

        internal static bool TryCaptureDestroyedObject(string objectId, out UnitySyncSceneObjectChange change)
        {
            change = null;
            if (!Guid.TryParse(objectId, out _))
            {
                return false;
            }

            change = new UnitySyncSceneObjectChange
            {
                Kind = UnitySyncSceneChangeKind.Destroy,
                Address = new UnitySyncSceneObjectAddress
                {
                    ObjectId = objectId
                }
            };
            return true;
        }

        internal static bool TryCaptureComponent(Component component, out UnitySyncSceneObjectChange change)
        {
            change = null;
            if (component == null ||
                !TryCreateAddress(component.gameObject, out UnitySyncSceneObjectAddress address))
            {
                return false;
            }

            int componentIndex = GetComponentIndex(component.gameObject, component);
            if (componentIndex < 0)
            {
                return false;
            }

            if (!TryCaptureComponentState(component, componentIndex, out UnitySyncComponentState componentState))
            {
                return false;
            }

            change = new UnitySyncSceneObjectChange
            {
                Kind = UnitySyncSceneChangeKind.Upsert,
                Address = address,
                Components = new[] { componentState }
            };
            return true;
        }

        internal static bool TryCaptureFullObject(GameObject gameObject, out UnitySyncSceneObjectChange change)
        {
            change = null;
            if (!TryCreateAddress(gameObject, out UnitySyncSceneObjectAddress address))
            {
                return false;
            }

            Component[] components = gameObject.GetComponents<Component>();
            UnitySyncComponentState[] componentStates = new UnitySyncComponentState[components.Length];
            for (int index = 0; index < components.Length; index++)
            {
                if (!TryCaptureComponentState(components[index], index, out componentStates[index]))
                {
                    return false;
                }
            }

            change = new UnitySyncSceneObjectChange
            {
                Kind = UnitySyncSceneChangeKind.Upsert,
                Address = address,
                ReconcileComponents = true,
                GameObject = CaptureGameObjectSettings(gameObject),
                Components = componentStates
            };
            return true;
        }

        internal static List<GameObject> GetAllSceneObjects()
        {
            List<GameObject> result = new List<GameObject>();
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                {
                    continue;
                }

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    AddHierarchy(root, result);
                }
            }

            return result;
        }

        internal static List<GameObject> GetHierarchyObjects(GameObject root)
        {
            List<GameObject> result = new List<GameObject>();
            AddHierarchy(root, result);
            return result;
        }

        internal static UnitySyncSceneDescriptor[] GetLoadedSceneDescriptors()
        {
            List<UnitySyncSceneDescriptor> scenes = new List<UnitySyncSceneDescriptor>();
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                {
                    continue;
                }

                scenes.Add(new UnitySyncSceneDescriptor
                {
                    ScenePath = scene.path ?? string.Empty,
                    SceneName = scene.name ?? string.Empty,
                    SceneIndex = sceneIndex
                });
            }

            return scenes.ToArray();
        }

        internal static bool Apply(UnitySyncSceneObjectChange change, out string error)
        {
            error = string.Empty;
            if (change == null || change.Address == null)
            {
                error = "The scene update did not contain a valid object address.";
                return false;
            }

            if (change.Kind == UnitySyncSceneChangeKind.Destroy)
            {
                return DestroySceneObject(change.Address, out error);
            }

            if (change.Kind != UnitySyncSceneChangeKind.Upsert)
            {
                error = "The scene update has an unsupported operation.";
                return false;
            }

            Type expectedTransformType = GetExpectedTransformType(change);
            bool allowSnapshotAdoption = change.SnapshotId != Guid.Empty && change.HierarchyOnly;
            if (!TryCreateOrUpdateHierarchy(
                    change.Address,
                    expectedTransformType,
                    change.GameObject != null ? change.GameObject.Name : string.Empty,
                    allowSnapshotAdoption,
                    out GameObject gameObject,
                    out error))
            {
                return false;
            }

            if (change.HierarchyOnly)
            {
                if (change.ReconcileComponents &&
                    !ReconcileComponents(gameObject, change.Components, out error))
                {
                    return false;
                }

                if (change.GameObject != null)
                {
                    ApplyGameObjectSettings(gameObject, change.GameObject);
                }

                EditorSceneManager.MarkSceneDirty(gameObject.scene);
                return true;
            }

            if (change.ReconcileComponents && !ReconcileComponents(gameObject, change.Components, out error))
            {
                return false;
            }

            foreach (UnitySyncComponentState componentState in change.Components ?? new UnitySyncComponentState[0])
            {
                if (!ApplyComponent(gameObject, componentState, out error))
                {
                    return false;
                }
            }

            if (change.GameObject != null)
            {
                ApplyGameObjectSettings(gameObject, change.GameObject);
            }

            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return true;
        }

        internal static bool TryCreateAddress(GameObject gameObject, out UnitySyncSceneObjectAddress address)
        {
            address = null;
            if (!IsEligibleSceneObject(gameObject))
            {
                return false;
            }

            List<int> path = new List<int>();
            Transform current = gameObject.transform;
            while (current != null)
            {
                path.Add(GetFilteredSiblingIndex(current));
                current = current.parent;
            }
            path.Reverse();

            int sceneIndex = -1;
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                if (SceneManager.GetSceneAt(index).handle == gameObject.scene.handle)
                {
                    sceneIndex = index;
                    break;
                }
            }

            Transform parent = gameObject.transform.parent;
            string parentObjectId = string.Empty;
            if (parent != null && IsEligibleSceneObject(parent.gameObject))
            {
                parentObjectId = UnitySyncSceneObjectRegistry.GetOrCreateId(parent.gameObject);
            }

            string objectId = UnitySyncSceneObjectRegistry.GetOrCreateId(gameObject);
            UnitySyncSceneObjectRegistry.SetParent(objectId, parentObjectId);
            int siblingIndex = GetFilteredSiblingIndex(gameObject.transform);
            if (siblingIndex < 0)
            {
                return false;
            }

            address = new UnitySyncSceneObjectAddress
            {
                ObjectId = objectId,
                ParentObjectId = parentObjectId,
                SiblingIndex = siblingIndex,
                ScenePath = gameObject.scene.path ?? string.Empty,
                SceneName = gameObject.scene.name ?? string.Empty,
                SceneIndex = sceneIndex,
                SiblingPath = path.ToArray()
            };
            return true;
        }

        internal static GameObject ResolveAddress(UnitySyncSceneObjectAddress address)
        {
            if (address == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(address.ObjectId))
            {
                return UnitySyncSceneObjectRegistry.TryResolve(address.ObjectId, out GameObject resolved) &&
                       IsEligibleSceneObject(resolved)
                    ? resolved
                    : null;
            }

            if (address.SiblingPath == null || address.SiblingPath.Length == 0)
            {
                return null;
            }

            if (!TryResolveScene(address, out Scene scene))
            {
                return null;
            }

            GameObject currentObject = GetFilteredRoot(scene, address.SiblingPath[0]);
            for (int depth = 1; currentObject != null && depth < address.SiblingPath.Length; depth++)
            {
                Transform child = GetFilteredChild(currentObject.transform, address.SiblingPath[depth]);
                currentObject = child != null ? child.gameObject : null;
            }

            return IsEligibleSceneObject(currentObject) ? currentObject : null;
        }

        internal static bool PruneSnapshot(
            UnitySyncSceneSnapshotBoundary snapshot,
            ISet<string> representedObjectIds,
            out string error)
        {
            error = string.Empty;
            if (snapshot == null || snapshot.SnapshotId == Guid.Empty)
            {
                error = "The scene snapshot did not contain a valid ID.";
                return false;
            }

            ISet<string> represented = representedObjectIds ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (UnitySyncSceneDescriptor descriptor in snapshot.Scenes ?? new UnitySyncSceneDescriptor[0])
            {
                if (descriptor == null ||
                    !TryResolveScene(descriptor.ScenePath, descriptor.SceneName, descriptor.SceneIndex, out Scene scene))
                {
                    // A collaborator may not have one of the host's additive scenes open. Never
                    // delete an unmatched local scene merely because it could not be identified.
                    continue;
                }

                GameObject[] roots = scene.GetRootGameObjects();
                for (int rootIndex = roots.Length - 1; rootIndex >= 0; rootIndex--)
                {
                    PruneUnrepresentedObject(roots[rootIndex], represented);
                }

                EditorSceneManager.MarkSceneDirty(scene);
            }

            return true;
        }

        private static bool TryCreateOrUpdateHierarchy(
            UnitySyncSceneObjectAddress address,
            Type expectedTransformType,
            string expectedName,
            bool allowSnapshotAdoption,
            out GameObject gameObject,
            out string error)
        {
            gameObject = ResolveAddress(address);
            error = string.Empty;
            if (!TryResolveScene(address, out Scene targetScene))
            {
                error = "No matching loaded scene exists for " + Describe(address) + ".";
                return false;
            }

            GameObject parentObject = null;
            if (!string.IsNullOrEmpty(address.ParentObjectId))
            {
                if (!UnitySyncSceneObjectRegistry.TryResolve(address.ParentObjectId, out parentObject) ||
                    !IsEligibleSceneObject(parentObject))
                {
                    error = "Parent " + address.ParentObjectId + " is not available for " +
                            Describe(address) + ".";
                    return false;
                }
            }

            if (gameObject == null && allowSnapshotAdoption)
            {
                gameObject = TryAdoptSnapshotObject(
                    address,
                    targetScene,
                    parentObject,
                    expectedTransformType,
                    expectedName);
            }

            if (gameObject == null)
            {
                gameObject = expectedTransformType == typeof(RectTransform)
                    ? new GameObject("[UnitySync New Object]", typeof(RectTransform))
                    : new GameObject("[UnitySync New Object]");
                Undo.RegisterCreatedObjectUndo(gameObject, "Create UnitySync scene object");
                Undo.RegisterCompleteObjectUndo(gameObject, "Sync UnitySync hierarchy");
                if (gameObject.scene.handle != targetScene.handle)
                {
                    SceneManager.MoveGameObjectToScene(gameObject, targetScene);
                }

                UnitySyncSceneObjectRegistry.Assign(gameObject, address.ObjectId);
            }
            else
            {
                UnitySyncSceneObjectRegistry.Assign(gameObject, address.ObjectId);
            }

            if (parentObject == null)
            {
                if (gameObject.transform.parent != null)
                {
                    Undo.SetTransformParent(gameObject.transform, null, "Sync UnitySync hierarchy");
                }

                if (gameObject.scene.handle != targetScene.handle)
                {
                    SceneManager.MoveGameObjectToScene(gameObject, targetScene);
                }
            }
            else
            {
                if (gameObject.scene.handle != parentObject.scene.handle)
                {
                    if (gameObject.transform.parent != null)
                    {
                        Undo.SetTransformParent(gameObject.transform, null, "Sync UnitySync hierarchy");
                    }

                    SceneManager.MoveGameObjectToScene(gameObject, parentObject.scene);
                }

                if (gameObject.transform.parent != parentObject.transform)
                {
                    Undo.SetTransformParent(
                        gameObject.transform,
                        parentObject.transform,
                        "Sync UnitySync hierarchy");
                }
            }

            SetFilteredSiblingIndex(gameObject.transform, address.SiblingIndex);
            UnitySyncSceneObjectRegistry.SetParent(address.ObjectId, address.ParentObjectId);
            return true;
        }

        private static GameObject TryAdoptSnapshotObject(
            UnitySyncSceneObjectAddress address,
            Scene targetScene,
            GameObject parentObject,
            Type expectedTransformType,
            string expectedName)
        {
            if (address == null ||
                string.IsNullOrEmpty(address.ObjectId) ||
                address.SiblingIndex < 0)
            {
                return null;
            }

            GameObject candidate;
            if (parentObject == null)
            {
                candidate = GetFilteredRoot(targetScene, address.SiblingIndex);
            }
            else
            {
                Transform child = GetFilteredChild(parentObject.transform, address.SiblingIndex);
                candidate = child != null ? child.gameObject : null;
            }

            if (!IsEligibleSceneObject(candidate) ||
                candidate.transform.GetType() != expectedTransformType ||
                (!string.IsNullOrEmpty(expectedName) &&
                 !string.Equals(candidate.name, expectedName, StringComparison.Ordinal)))
            {
                return null;
            }

            if (UnitySyncSceneObjectRegistry.TryGetId(candidate, out string existingId) &&
                !string.Equals(existingId, address.ObjectId, StringComparison.Ordinal))
            {
                return null;
            }

            UnitySyncSceneObjectRegistry.Assign(candidate, address.ObjectId);
            return candidate;
        }

        private static Type GetExpectedTransformType(UnitySyncSceneObjectChange change)
        {
            if (change == null ||
                !change.ReconcileComponents ||
                change.Components == null ||
                change.Components.Length == 0 ||
                change.Components[0] == null ||
                change.Components[0].ComponentIndex != 0)
            {
                return typeof(Transform);
            }

            Type type = ResolveType(change.Components[0].TypeName);
            return type == typeof(RectTransform) ? typeof(RectTransform) : typeof(Transform);
        }

        private static bool DestroySceneObject(UnitySyncSceneObjectAddress address, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(address.ObjectId))
            {
                error = "The deleted scene object did not contain an ID.";
                return false;
            }

            if (!UnitySyncSceneObjectRegistry.TryResolve(address.ObjectId, out GameObject gameObject) ||
                !IsEligibleSceneObject(gameObject))
            {
                UnitySyncSceneObjectRegistry.ForgetHierarchy(address.ObjectId);
                return true;
            }

            UnitySyncSceneObjectRegistry.ForgetHierarchy(address.ObjectId);
            Undo.DestroyObjectImmediate(gameObject);
            return true;
        }

        private static void PruneUnrepresentedObject(GameObject gameObject, ISet<string> representedObjectIds)
        {
            if (!IsEligibleSceneObject(gameObject))
            {
                return;
            }

            if (!UnitySyncSceneObjectRegistry.TryGetId(gameObject, out string objectId) ||
                !representedObjectIds.Contains(objectId))
            {
                UnitySyncSceneObjectRegistry.ForgetHierarchy(objectId);
                Undo.DestroyObjectImmediate(gameObject);
                return;
            }

            for (int childIndex = gameObject.transform.childCount - 1; childIndex >= 0; childIndex--)
            {
                PruneUnrepresentedObject(
                    gameObject.transform.GetChild(childIndex).gameObject,
                    representedObjectIds);
            }
        }

        private static bool TryResolveScene(UnitySyncSceneObjectAddress address, out Scene scene)
        {
            return TryResolveScene(address.ScenePath, address.SceneName, address.SceneIndex, out scene);
        }

        private static bool TryResolveScene(
            string scenePath,
            string sceneName,
            int sceneIndex,
            out Scene scene)
        {
            scene = default;
            if (!string.IsNullOrEmpty(scenePath))
            {
                scene = SceneManager.GetSceneByPath(scenePath);
            }

            if (!scene.IsValid() &&
                sceneIndex >= 0 &&
                sceneIndex < SceneManager.sceneCount)
            {
                Scene indexedScene = SceneManager.GetSceneAt(sceneIndex);
                if (string.IsNullOrEmpty(sceneName) || indexedScene.name == sceneName)
                {
                    scene = indexedScene;
                }
            }

            if (!scene.IsValid() && !string.IsNullOrEmpty(sceneName))
            {
                for (int index = 0; index < SceneManager.sceneCount; index++)
                {
                    Scene candidate = SceneManager.GetSceneAt(index);
                    if (candidate.name == sceneName)
                    {
                        scene = candidate;
                        break;
                    }
                }
            }

            return scene.IsValid() && scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene);
        }

        private static UnitySyncGameObjectState CaptureGameObjectSettings(GameObject gameObject)
        {
            return new UnitySyncGameObjectState
            {
                Name = gameObject.name,
                ActiveSelf = gameObject.activeSelf,
                Layer = gameObject.layer,
                Tag = gameObject.tag,
                StaticEditorFlags = (int)GameObjectUtility.GetStaticEditorFlags(gameObject)
            };
        }

        private static bool TryCaptureComponentState(
            Component component,
            int componentIndex,
            out UnitySyncComponentState state)
        {
            state = new UnitySyncComponentState
            {
                ComponentIndex = componentIndex,
                TypeName = component != null ? GetStableTypeName(component.GetType()) : string.Empty
            };

            if (component == null)
            {
                return true;
            }

            SerializedObject serializedObject = new SerializedObject(component);
            serializedObject.UpdateIfRequiredOrScript();
            SerializedProperty iterator = serializedObject.GetIterator();
            List<UnitySyncSerializedPropertyState> properties = new List<UnitySyncSerializedPropertyState>();
            bool enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                bool ignored = IsIgnoredPropertyPath(iterator.propertyPath);
                enterChildren = !ignored;
                if (ignored || !iterator.editable)
                {
                    continue;
                }

                if (TryCaptureProperty(iterator, out UnitySyncSerializedPropertyState propertyState))
                {
                    properties.Add(propertyState);
                    continue;
                }

                if (iterator.propertyType == SerializedPropertyType.ObjectReference ||
                    iterator.propertyType == SerializedPropertyType.ExposedReference)
                {
                    string captureError = "Scene sync skipped a local " + component.GetType().Name +
                                          " update: object reference " + iterator.propertyPath +
                                          " has no stable cross-editor identity.";
                    UnitySyncSession.ReportSceneSyncIssue(captureError);
                    Debug.LogWarning(
                        "[UnitySync] " + captureError);
                    return false;
                }
            }

            state.Properties = properties.ToArray();
            return true;
        }

        private static bool TryCaptureProperty(
            SerializedProperty property,
            out UnitySyncSerializedPropertyState state)
        {
            state = new UnitySyncSerializedPropertyState { Path = property.propertyPath };
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    state.Kind = UnitySyncSerializedValueKind.Integer;
                    state.IntegerValue = property.longValue;
                    return true;

                case SerializedPropertyType.Boolean:
                    state.Kind = UnitySyncSerializedValueKind.Boolean;
                    state.IntegerValue = property.boolValue ? 1 : 0;
                    return true;

                case SerializedPropertyType.Float:
                    state.Kind = UnitySyncSerializedValueKind.Float;
                    state.NumberValue = property.doubleValue;
                    return true;

                case SerializedPropertyType.String:
                    state.Kind = UnitySyncSerializedValueKind.String;
                    state.StringValue = property.stringValue;
                    return true;

                case SerializedPropertyType.Color:
                    state.Kind = UnitySyncSerializedValueKind.Color;
                    state.FloatValues = ToArray(property.colorValue);
                    return true;

                case SerializedPropertyType.ObjectReference:
                    state.Kind = UnitySyncSerializedValueKind.ObjectReference;
                    Object objectReference = property.objectReferenceValue;
                    if (!IsSerializedReferenceTypeCompatible(property.type, objectReference) ||
                        !TryCaptureObjectReference(objectReference, out state.ObjectReference))
                    {
                        return false;
                    }

                    state.ObjectReference.SerializedPropertyTypeName = property.type;
                    return true;

                case SerializedPropertyType.LayerMask:
                    state.Kind = UnitySyncSerializedValueKind.LayerMask;
                    state.IntegerValue = property.intValue;
                    return true;

                case SerializedPropertyType.Enum:
                    state.Kind = UnitySyncSerializedValueKind.Enum;
                    state.IntegerValue = property.intValue;
                    return true;

                case SerializedPropertyType.Vector2:
                    state.Kind = UnitySyncSerializedValueKind.Vector2;
                    state.FloatValues = ToArray(property.vector2Value);
                    return true;

                case SerializedPropertyType.Vector3:
                    state.Kind = UnitySyncSerializedValueKind.Vector3;
                    state.FloatValues = ToArray(property.vector3Value);
                    return true;

                case SerializedPropertyType.Vector4:
                    state.Kind = UnitySyncSerializedValueKind.Vector4;
                    state.FloatValues = ToArray(property.vector4Value);
                    return true;

                case SerializedPropertyType.Rect:
                    state.Kind = UnitySyncSerializedValueKind.Rect;
                    Rect rect = property.rectValue;
                    state.FloatValues = new[] { rect.x, rect.y, rect.width, rect.height };
                    return true;

                case SerializedPropertyType.ArraySize:
                    state.Kind = UnitySyncSerializedValueKind.ArraySize;
                    state.IntegerValue = property.intValue;
                    return true;

                case SerializedPropertyType.Character:
                    state.Kind = UnitySyncSerializedValueKind.Character;
                    state.IntegerValue = property.intValue;
                    return true;

                case SerializedPropertyType.AnimationCurve:
                    state.Kind = UnitySyncSerializedValueKind.AnimationCurve;
                    AnimationCurve curve = property.animationCurveValue;
                    state.AnimationCurve = new UnitySyncAnimationCurveState
                    {
                        PreWrapMode = curve != null ? curve.preWrapMode : WrapMode.Default,
                        PostWrapMode = curve != null ? curve.postWrapMode : WrapMode.Default,
                        Keys = curve != null ? curve.keys : new Keyframe[0]
                    };
                    return true;

                case SerializedPropertyType.Bounds:
                    state.Kind = UnitySyncSerializedValueKind.Bounds;
                    Bounds bounds = property.boundsValue;
                    state.FloatValues = new[]
                    {
                        bounds.center.x, bounds.center.y, bounds.center.z,
                        bounds.size.x, bounds.size.y, bounds.size.z
                    };
                    return true;

                case SerializedPropertyType.Gradient:
                    return TryCaptureGradient(property, state);

                case SerializedPropertyType.Quaternion:
                    state.Kind = UnitySyncSerializedValueKind.Quaternion;
                    state.FloatValues = ToArray(property.quaternionValue);
                    return true;

                case SerializedPropertyType.ExposedReference:
                    state.Kind = UnitySyncSerializedValueKind.ExposedReference;
                    if (!TryCaptureObjectReference(property.exposedReferenceValue, out state.ObjectReference))
                    {
                        return false;
                    }

                    state.ObjectReference.SerializedPropertyTypeName = property.type;
                    return true;

                case SerializedPropertyType.Vector2Int:
                    state.Kind = UnitySyncSerializedValueKind.Vector2Int;
                    Vector2Int vector2Int = property.vector2IntValue;
                    state.IntegerValues = new[] { vector2Int.x, vector2Int.y };
                    return true;

                case SerializedPropertyType.Vector3Int:
                    state.Kind = UnitySyncSerializedValueKind.Vector3Int;
                    Vector3Int vector3Int = property.vector3IntValue;
                    state.IntegerValues = new[] { vector3Int.x, vector3Int.y, vector3Int.z };
                    return true;

                case SerializedPropertyType.RectInt:
                    state.Kind = UnitySyncSerializedValueKind.RectInt;
                    RectInt rectInt = property.rectIntValue;
                    state.IntegerValues = new[] { rectInt.x, rectInt.y, rectInt.width, rectInt.height };
                    return true;

                case SerializedPropertyType.BoundsInt:
                    state.Kind = UnitySyncSerializedValueKind.BoundsInt;
                    BoundsInt boundsInt = property.boundsIntValue;
                    state.IntegerValues = new[]
                    {
                        boundsInt.position.x, boundsInt.position.y, boundsInt.position.z,
                        boundsInt.size.x, boundsInt.size.y, boundsInt.size.z
                    };
                    return true;

                case SerializedPropertyType.ManagedReference:
                    state.Kind = UnitySyncSerializedValueKind.ManagedReference;
                    state.StringValue = property.managedReferenceFullTypename ?? string.Empty;
                    return true;

                case SerializedPropertyType.Hash128:
                    state.Kind = UnitySyncSerializedValueKind.Hash128;
                    state.StringValue = property.hash128Value.ToString();
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryCaptureGradient(
            SerializedProperty property,
            UnitySyncSerializedPropertyState state)
        {
            if (GradientValueProperty == null)
            {
                return false;
            }

            Gradient gradient;
            try
            {
                gradient = GradientValueProperty.GetValue(property, null) as Gradient;
            }
            catch (TargetInvocationException)
            {
                return false;
            }

            if (gradient == null)
            {
                return false;
            }

            state.Kind = UnitySyncSerializedValueKind.Gradient;
            state.Gradient = new UnitySyncGradientState
            {
                Mode = gradient.mode,
                ColorKeys = gradient.colorKeys,
                AlphaKeys = gradient.alphaKeys
            };
            return true;
        }

        private static bool TryCaptureObjectReference(
            Object value,
            out UnitySyncObjectReferenceState reference)
        {
            reference = new UnitySyncObjectReferenceState();
            if (value == null)
            {
                reference.Kind = UnitySyncObjectReferenceKind.Null;
                return true;
            }

            reference.ObjectTypeName = GetStableTypeName(value.GetType());

            if (value is GameObject gameObject)
            {
                if (!TryCreateAddress(gameObject, out reference.SceneObject))
                {
                    return false;
                }

                reference.Kind = UnitySyncObjectReferenceKind.SceneObject;
                reference.ComponentIndex = -1;
                return true;
            }

            if (value is Component component)
            {
                if (!TryCreateAddress(component.gameObject, out reference.SceneObject))
                {
                    return false;
                }

                reference.Kind = UnitySyncObjectReferenceKind.SceneObject;
                reference.ComponentIndex = GetComponentIndex(component.gameObject, component);
                return reference.ComponentIndex >= 0;
            }

            if (EditorUtility.IsPersistent(value))
            {
                string assetPath = AssetDatabase.GetAssetPath(value) ?? string.Empty;
                bool hasFileIdentifier = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    value,
                    out string guid,
                    out long localFileId);
                if ((!hasFileIdentifier || string.IsNullOrEmpty(guid)) && !IsBuiltinAssetPath(assetPath))
                {
                    return false;
                }

                reference.Kind = UnitySyncObjectReferenceKind.Asset;
                reference.AssetGuid = guid ?? string.Empty;
                reference.AssetPath = assetPath;
                reference.AssetName = value.name ?? string.Empty;
                reference.AssetContentHash = GetAssetContentHash(assetPath);
                reference.LocalFileId = localFileId;
                return true;
            }

            return false;
        }

        private static void ApplyGameObjectSettings(GameObject gameObject, UnitySyncGameObjectState state)
        {
            Undo.RecordObject(gameObject, "Apply UnitySync object settings");
            gameObject.name = state.Name;
            gameObject.layer = Mathf.Clamp(state.Layer, 0, 31);
            try
            {
                gameObject.tag = state.Tag;
            }
            catch (UnityException)
            {
                // Keep the local tag when that tag is not defined in this project.
            }

            GameObjectUtility.SetStaticEditorFlags(gameObject, (StaticEditorFlags)state.StaticEditorFlags);
            gameObject.SetActive(state.ActiveSelf);
            PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
            EditorUtility.SetDirty(gameObject);
        }

        private static bool ApplyComponent(
            GameObject gameObject,
            UnitySyncComponentState state,
            out string error)
        {
            error = string.Empty;
            if (state == null)
            {
                error = "The scene update contained an invalid component.";
                return false;
            }

            Component[] components = gameObject.GetComponents<Component>();
            if (state.ComponentIndex < 0 || state.ComponentIndex >= components.Length)
            {
                error = "Component " + state.ComponentIndex + " is missing on " + gameObject.name + ".";
                return false;
            }

            Component component = components[state.ComponentIndex];
            if (component == null)
            {
                if (string.IsNullOrEmpty(state.TypeName))
                {
                    return true;
                }

                error = "A script is missing on " + gameObject.name + ".";
                return false;
            }

            if (!TypeMatches(component.GetType(), state.TypeName))
            {
                error = "Component order differs on " + gameObject.name + ".";
                return false;
            }

            return ApplyComponentThroughStaging(component, state, out error);
        }

        private static bool ApplyComponentThroughStaging(
            Component component,
            UnitySyncComponentState state,
            out string error)
        {
            error = string.Empty;
            GameObject stagingObject = null;
            try
            {
                if (!TryCreateStagingComponent(component.GetType(), out stagingObject, out Component stagingComponent))
                {
                    error = "Could not create a staging " + component.GetType().Name + ".";
                    return false;
                }

                // A valid target is the best source for hidden/non-editable defaults. A target
                // containing a broken PPtr is deliberately not copied into the staging object;
                // the complete incoming component state can repair it from clean defaults.
                if (TryValidateObjectReferencesWithoutDereferencing(component, out _))
                {
                    EditorUtility.CopySerialized(component, stagingComponent);
                }

                List<ResolvedObjectReferenceAssignment> referenceAssignments;
                if (RequiresAtomicSerializedApply(stagingComponent.GetType()))
                {
                    if (!ApplySerializedPropertiesAtomicallyWithReferences(
                            stagingComponent,
                            state,
                            out referenceAssignments,
                            out error))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!ApplySerializedProperties(stagingComponent, state, out error))
                    {
                        return false;
                    }

                    if (!TryResolveObjectReferenceAssignments(
                            stagingComponent,
                            state,
                            out referenceAssignments,
                            out error))
                    {
                        return false;
                    }
                }

                Undo.RecordObject(component, "Apply UnitySync component settings");
                if (component is Transform targetTransform && stagingComponent is Transform stagingTransform)
                {
                    CopyTransformSettings(stagingTransform, targetTransform);
                }
                else
                {
                    EditorUtility.CopySerialized(stagingComponent, component);
                }

                if (!ApplyResolvedObjectReferences(component, referenceAssignments, out error))
                {
                    return false;
                }

                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                EditorUtility.SetDirty(component);
                return true;
            }
            catch (Exception exception)
            {
                error = "Could not stage " + component.GetType().Name + ": " + exception.Message;
                return false;
            }
            finally
            {
                if (stagingObject != null)
                {
                    Object.DestroyImmediate(stagingObject);
                }
            }
        }

        private static bool RequiresAtomicSerializedApply(Type componentType)
        {
            // UdonBehaviour reconstructs its public-variable table from a serialized byte string
            // and a parallel UnityEngine.Object list in OnAfterDeserialize. Applying either half
            // by itself makes the callback deserialize an inconsistent table.
            return componentType != null &&
                   componentType.FullName == "VRC.Udon.UdonBehaviour";
        }

        private static bool ApplySerializedPropertiesAtomicallyWithReferences(
            Component component,
            UnitySyncComponentState state,
            out List<ResolvedObjectReferenceAssignment> assignments,
            out string error)
        {
            assignments = new List<ResolvedObjectReferenceAssignment>();
            error = string.Empty;
            SerializedObject serializedObject = new SerializedObject(component);
            serializedObject.UpdateIfRequiredOrScript();

            foreach (UnitySyncSerializedPropertyState propertyState in
                     state.Properties ?? new UnitySyncSerializedPropertyState[0])
            {
                if (propertyState == null || IsIgnoredPropertyPath(propertyState.Path))
                {
                    continue;
                }

                if (propertyState.Kind != UnitySyncSerializedValueKind.ArraySize &&
                    propertyState.Kind != UnitySyncSerializedValueKind.ManagedReference)
                {
                    continue;
                }

                SerializedProperty property = serializedObject.FindProperty(propertyState.Path);
                if (property != null)
                {
                    ApplyProperty(property, propertyState);
                }
            }

            foreach (UnitySyncSerializedPropertyState propertyState in
                     state.Properties ?? new UnitySyncSerializedPropertyState[0])
            {
                if (propertyState == null || IsIgnoredPropertyPath(propertyState.Path))
                {
                    continue;
                }

                if (propertyState.Kind == UnitySyncSerializedValueKind.ArraySize ||
                    propertyState.Kind == UnitySyncSerializedValueKind.ManagedReference ||
                    IsObjectReferenceKind(propertyState.Kind))
                {
                    continue;
                }

                SerializedProperty property = serializedObject.FindProperty(propertyState.Path);
                if (property != null)
                {
                    ApplyProperty(property, propertyState);
                }
            }

            foreach (UnitySyncSerializedPropertyState propertyState in
                     state.Properties ?? new UnitySyncSerializedPropertyState[0])
            {
                if (propertyState == null ||
                    IsIgnoredPropertyPath(propertyState.Path) ||
                    !IsObjectReferenceKind(propertyState.Kind))
                {
                    continue;
                }

                SerializedProperty property = serializedObject.FindProperty(propertyState.Path);
                if (property == null ||
                    !IsSerializedPropertyKindCompatible(property, propertyState.Kind) ||
                    !CanApplyObjectReference(property, propertyState.ObjectReference))
                {
                    error = "Object reference " + propertyState.Path +
                            " does not match the local serialized layout on " +
                            component.GetType().Name + ".";
                    return false;
                }

                if (!TryResolveObjectReference(propertyState.ObjectReference, out Object value) ||
                    !IsSerializedReferenceTypeCompatible(property.type, value))
                {
                    error = "Object reference " + propertyState.Path +
                            " could not be matched safely for local field type " +
                            property.type + " on " + component.GetType().Name + ".";
                    return false;
                }

                if (propertyState.Kind == UnitySyncSerializedValueKind.ExposedReference)
                {
                    property.exposedReferenceValue = value;
                }
                else
                {
                    property.objectReferenceValue = value;
                }

                assignments.Add(new ResolvedObjectReferenceAssignment
                {
                    Path = propertyState.Path,
                    Kind = propertyState.Kind,
                    Reference = propertyState.ObjectReference,
                    Value = value
                });
            }

            // Commit the whole serialized state once. This is required for components such as
            // VRC.Udon.UdonBehaviour whose deserialization callback expects multiple serialized
            // fields to change as one coherent unit.
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            serializedObject.UpdateIfRequiredOrScript();

            foreach (ResolvedObjectReferenceAssignment assignment in assignments)
            {
                SerializedProperty property = serializedObject.FindProperty(assignment.Path);
                if (property == null)
                {
                    error = "Object reference " + assignment.Path +
                            " disappeared from the staged serialized layout on " +
                            component.GetType().Name + ".";
                    return false;
                }

                Object appliedValue = assignment.Kind == UnitySyncSerializedValueKind.ExposedReference
                    ? property.exposedReferenceValue
                    : property.objectReferenceValue;
                if (appliedValue != assignment.Value)
                {
                    error = "Object reference " + assignment.Path + " on staged " +
                            component.GetType().Name +
                            " did not retain the resolved local object.";
                    return false;
                }
            }

            return true;
        }

        private static bool ApplySerializedProperties(
            Component component,
            UnitySyncComponentState state,
            out string error)
        {
            error = string.Empty;
            SerializedObject serializedObject = new SerializedObject(component);
            serializedObject.UpdateIfRequiredOrScript();

            foreach (UnitySyncSerializedPropertyState propertyState in
                     state.Properties ?? new UnitySyncSerializedPropertyState[0])
            {
                if (propertyState == null || IsIgnoredPropertyPath(propertyState.Path))
                {
                    continue;
                }

                if (propertyState.Kind != UnitySyncSerializedValueKind.ArraySize &&
                    propertyState.Kind != UnitySyncSerializedValueKind.ManagedReference)
                {
                    continue;
                }

                SerializedProperty property = serializedObject.FindProperty(propertyState.Path);
                if (property != null && ApplyProperty(property, propertyState))
                {
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                    serializedObject.UpdateIfRequiredOrScript();
                }
            }

            serializedObject.UpdateIfRequiredOrScript();

            foreach (UnitySyncSerializedPropertyState propertyState in
                     state.Properties ?? new UnitySyncSerializedPropertyState[0])
            {
                if (propertyState == null || IsIgnoredPropertyPath(propertyState.Path))
                {
                    continue;
                }

                if (propertyState.Kind == UnitySyncSerializedValueKind.ArraySize ||
                    propertyState.Kind == UnitySyncSerializedValueKind.ManagedReference ||
                    IsObjectReferenceKind(propertyState.Kind))
                {
                    continue;
                }

                SerializedProperty property = serializedObject.FindProperty(propertyState.Path);
                if (property != null)
                {
                    ApplyProperty(property, propertyState);
                }
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        private static bool TryCreateStagingComponent(
            Type componentType,
            out GameObject stagingObject,
            out Component stagingComponent)
        {
            stagingObject = null;
            stagingComponent = null;

            if (componentType == typeof(RectTransform))
            {
                stagingObject = new GameObject("[UnitySync Component Staging]", typeof(RectTransform));
                stagingObject.hideFlags = HideFlags.HideAndDontSave;
                stagingObject.SetActive(false);
                stagingComponent = stagingObject.transform;
                return true;
            }

            stagingObject = new GameObject("[UnitySync Component Staging]");
            stagingObject.hideFlags = HideFlags.HideAndDontSave;
            stagingObject.SetActive(false);
            if (componentType == typeof(Transform))
            {
                stagingComponent = stagingObject.transform;
                return true;
            }

            stagingComponent = stagingObject.AddComponent(componentType);
            return stagingComponent != null;
        }

        private static bool TryValidateObjectReferencesWithoutDereferencing(
            Component component,
            out string invalidProperty)
        {
            invalidProperty = string.Empty;
            SerializedObject serializedObject = new SerializedObject(component);
            serializedObject.UpdateIfRequiredOrScript();
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                int instanceId = iterator.objectReferenceInstanceIDValue;
                if (instanceId == 0)
                {
                    continue;
                }

                Object value = EditorUtility.InstanceIDToObject(instanceId);
                if (value == null || !IsSerializedReferenceTypeCompatible(iterator.type, value))
                {
                    invalidProperty = iterator.propertyPath;
                    return false;
                }
            }

            return true;
        }

        private static bool TryResolveObjectReferenceAssignments(
            Component layoutComponent,
            UnitySyncComponentState state,
            out List<ResolvedObjectReferenceAssignment> assignments,
            out string error)
        {
            assignments = new List<ResolvedObjectReferenceAssignment>();
            error = string.Empty;
            SerializedObject serializedObject = new SerializedObject(layoutComponent);
            serializedObject.UpdateIfRequiredOrScript();

            foreach (UnitySyncSerializedPropertyState propertyState in
                     state.Properties ?? new UnitySyncSerializedPropertyState[0])
            {
                if (propertyState == null ||
                    IsIgnoredPropertyPath(propertyState.Path) ||
                    !IsObjectReferenceKind(propertyState.Kind))
                {
                    continue;
                }

                SerializedProperty property = serializedObject.FindProperty(propertyState.Path);
                if (property == null ||
                    !IsSerializedPropertyKindCompatible(property, propertyState.Kind) ||
                    !CanApplyObjectReference(property, propertyState.ObjectReference))
                {
                    error = "Object reference " + propertyState.Path +
                            " does not match the local serialized layout on " +
                            layoutComponent.GetType().Name + ".";
                    return false;
                }

                if (!TryResolveObjectReference(propertyState.ObjectReference, out Object value) ||
                    !IsSerializedReferenceTypeCompatible(property.type, value))
                {
                    error = "Object reference " + propertyState.Path +
                            " could not be matched safely for local field type " +
                            property.type + " on " + layoutComponent.GetType().Name + ".";
                    return false;
                }

                assignments.Add(new ResolvedObjectReferenceAssignment
                {
                    Path = propertyState.Path,
                    Kind = propertyState.Kind,
                    Reference = propertyState.ObjectReference,
                    Value = value
                });
            }

            return true;
        }

        private static bool ApplyResolvedObjectReferences(
            Component component,
            List<ResolvedObjectReferenceAssignment> assignments,
            out string error)
        {
            error = string.Empty;
            if (assignments == null || assignments.Count == 0)
            {
                return true;
            }

            SerializedObject serializedObject = new SerializedObject(component);
            serializedObject.UpdateIfRequiredOrScript();
            foreach (ResolvedObjectReferenceAssignment assignment in assignments)
            {
                SerializedProperty property = serializedObject.FindProperty(assignment.Path);
                if (property == null ||
                    !IsSerializedPropertyKindCompatible(property, assignment.Kind) ||
                    !CanApplyObjectReference(property, assignment.Reference))
                {
                    error = "Object reference " + assignment.Path +
                            " no longer matches the live serialized layout on " +
                            component.GetType().Name + ".";
                    return false;
                }

                if (assignment.Kind == UnitySyncSerializedValueKind.ExposedReference)
                {
                    property.exposedReferenceValue = assignment.Value;
                }
                else
                {
                    property.objectReferenceValue = assignment.Value;
                }
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            serializedObject.UpdateIfRequiredOrScript();
            foreach (ResolvedObjectReferenceAssignment assignment in assignments)
            {
                SerializedProperty property = serializedObject.FindProperty(assignment.Path);
                if (property == null)
                {
                    error = "Object reference " + assignment.Path +
                            " disappeared from the live serialized layout on " +
                            component.GetType().Name + ".";
                    return false;
                }

                Object appliedValue = assignment.Kind == UnitySyncSerializedValueKind.ExposedReference
                    ? property.exposedReferenceValue
                    : property.objectReferenceValue;
                if (appliedValue != assignment.Value)
                {
                    error = "Object reference " + assignment.Path + " on " +
                            component.GetType().Name +
                            " did not retain the resolved local object.";
                    return false;
                }
            }

            return true;
        }

        private static bool IsObjectReferenceKind(UnitySyncSerializedValueKind kind)
        {
            return kind == UnitySyncSerializedValueKind.ObjectReference ||
                   kind == UnitySyncSerializedValueKind.ExposedReference;
        }

        private static bool IsSerializedPropertyKindCompatible(
            SerializedProperty property,
            UnitySyncSerializedValueKind kind)
        {
            return (kind == UnitySyncSerializedValueKind.ObjectReference &&
                    property.propertyType == SerializedPropertyType.ObjectReference) ||
                   (kind == UnitySyncSerializedValueKind.ExposedReference &&
                    property.propertyType == SerializedPropertyType.ExposedReference);
        }

        private static void CopyTransformSettings(Transform source, Transform destination)
        {
            if (source is RectTransform sourceRect && destination is RectTransform destinationRect)
            {
                destinationRect.anchorMin = sourceRect.anchorMin;
                destinationRect.anchorMax = sourceRect.anchorMax;
                destinationRect.pivot = sourceRect.pivot;
                destinationRect.sizeDelta = sourceRect.sizeDelta;
                destinationRect.anchoredPosition3D = sourceRect.anchoredPosition3D;
            }
            else
            {
                destination.localPosition = source.localPosition;
            }

            destination.localRotation = source.localRotation;
            destination.localScale = source.localScale;
            if (TransformConstrainProportionsProperty != null)
            {
                object constrained = TransformConstrainProportionsProperty.GetValue(source, null);
                TransformConstrainProportionsProperty.SetValue(destination, constrained, null);
            }
        }

        private static bool ApplyProperty(
            SerializedProperty property,
            UnitySyncSerializedPropertyState state)
        {
            try
            {
                switch (state.Kind)
                {
                    case UnitySyncSerializedValueKind.Integer:
                        property.longValue = state.IntegerValue;
                        return true;

                    case UnitySyncSerializedValueKind.Boolean:
                        property.boolValue = state.IntegerValue != 0;
                        return true;

                    case UnitySyncSerializedValueKind.Float:
                        property.doubleValue = state.NumberValue;
                        return true;

                    case UnitySyncSerializedValueKind.String:
                        property.stringValue = state.StringValue;
                        return true;

                    case UnitySyncSerializedValueKind.Color:
                        if (!HasFloats(state, 4)) return false;
                        property.colorValue = new Color(
                            state.FloatValues[0], state.FloatValues[1],
                            state.FloatValues[2], state.FloatValues[3]);
                        return true;

                    case UnitySyncSerializedValueKind.ObjectReference:
                        return false;

                    case UnitySyncSerializedValueKind.LayerMask:
                        property.intValue = (int)state.IntegerValue;
                        return true;

                    case UnitySyncSerializedValueKind.Enum:
                        property.intValue = (int)state.IntegerValue;
                        return true;

                    case UnitySyncSerializedValueKind.Vector2:
                        if (!HasFloats(state, 2)) return false;
                        property.vector2Value = new Vector2(state.FloatValues[0], state.FloatValues[1]);
                        return true;

                    case UnitySyncSerializedValueKind.Vector3:
                        if (!HasFloats(state, 3)) return false;
                        property.vector3Value = new Vector3(
                            state.FloatValues[0], state.FloatValues[1], state.FloatValues[2]);
                        return true;

                    case UnitySyncSerializedValueKind.Vector4:
                        if (!HasFloats(state, 4)) return false;
                        property.vector4Value = new Vector4(
                            state.FloatValues[0], state.FloatValues[1],
                            state.FloatValues[2], state.FloatValues[3]);
                        return true;

                    case UnitySyncSerializedValueKind.Rect:
                        if (!HasFloats(state, 4)) return false;
                        property.rectValue = new Rect(
                            state.FloatValues[0], state.FloatValues[1],
                            state.FloatValues[2], state.FloatValues[3]);
                        return true;

                    case UnitySyncSerializedValueKind.ArraySize:
                        property.intValue = Mathf.Max(0, (int)state.IntegerValue);
                        return true;

                    case UnitySyncSerializedValueKind.Character:
                        property.intValue = (int)state.IntegerValue;
                        return true;

                    case UnitySyncSerializedValueKind.AnimationCurve:
                        property.animationCurveValue = CreateAnimationCurve(state.AnimationCurve);
                        return true;

                    case UnitySyncSerializedValueKind.Bounds:
                        if (!HasFloats(state, 6)) return false;
                        property.boundsValue = new Bounds(
                            new Vector3(state.FloatValues[0], state.FloatValues[1], state.FloatValues[2]),
                            new Vector3(state.FloatValues[3], state.FloatValues[4], state.FloatValues[5]));
                        return true;

                    case UnitySyncSerializedValueKind.Gradient:
                        return ApplyGradient(property, state.Gradient);

                    case UnitySyncSerializedValueKind.Quaternion:
                        if (!HasFloats(state, 4)) return false;
                        property.quaternionValue = new Quaternion(
                            state.FloatValues[0], state.FloatValues[1],
                            state.FloatValues[2], state.FloatValues[3]);
                        return true;

                    case UnitySyncSerializedValueKind.ExposedReference:
                        return false;

                    case UnitySyncSerializedValueKind.Vector2Int:
                        if (!HasIntegers(state, 2)) return false;
                        property.vector2IntValue = new Vector2Int(state.IntegerValues[0], state.IntegerValues[1]);
                        return true;

                    case UnitySyncSerializedValueKind.Vector3Int:
                        if (!HasIntegers(state, 3)) return false;
                        property.vector3IntValue = new Vector3Int(
                            state.IntegerValues[0], state.IntegerValues[1], state.IntegerValues[2]);
                        return true;

                    case UnitySyncSerializedValueKind.RectInt:
                        if (!HasIntegers(state, 4)) return false;
                        property.rectIntValue = new RectInt(
                            state.IntegerValues[0], state.IntegerValues[1],
                            state.IntegerValues[2], state.IntegerValues[3]);
                        return true;

                    case UnitySyncSerializedValueKind.BoundsInt:
                        if (!HasIntegers(state, 6)) return false;
                        property.boundsIntValue = new BoundsInt(
                            new Vector3Int(state.IntegerValues[0], state.IntegerValues[1], state.IntegerValues[2]),
                            new Vector3Int(state.IntegerValues[3], state.IntegerValues[4], state.IntegerValues[5]));
                        return true;

                    case UnitySyncSerializedValueKind.ManagedReference:
                        return ApplyManagedReference(property, state.StringValue);

                    case UnitySyncSerializedValueKind.Hash128:
                        property.hash128Value = Hash128.Parse(state.StringValue);
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is InvalidOperationException ||
                exception is TargetInvocationException)
            {
                return false;
            }
        }

        private static bool ReconcileComponents(
            GameObject gameObject,
            UnitySyncComponentState[] desiredStates,
            out string error)
        {
            error = string.Empty;
            desiredStates = desiredStates ?? new UnitySyncComponentState[0];
            if (desiredStates.Length == 0 ||
                desiredStates[0] == null ||
                desiredStates[0].ComponentIndex != 0 ||
                !TypeMatches(gameObject.transform.GetType(), desiredStates[0].TypeName))
            {
                error = "The Transform type differs on " + gameObject.name + ".";
                return false;
            }

            Type[] desiredTypes = new Type[desiredStates.Length];
            desiredTypes[0] = gameObject.transform.GetType();
            for (int index = 1; index < desiredStates.Length; index++)
            {
                UnitySyncComponentState state = desiredStates[index];
                if (state == null || state.ComponentIndex != index)
                {
                    error = "Component order differs on " + gameObject.name + ".";
                    return false;
                }

                if (string.IsNullOrEmpty(state.TypeName))
                {
                    Component[] currentWithMissingScript = gameObject.GetComponents<Component>();
                    if (index >= currentWithMissingScript.Length || currentWithMissingScript[index] != null)
                    {
                        error = "A missing-script component differs on " + gameObject.name + ".";
                        return false;
                    }

                    continue;
                }

                desiredTypes[index] = ResolveType(state.TypeName);
                if (desiredTypes[index] == null || !typeof(Component).IsAssignableFrom(desiredTypes[index]))
                {
                    error = "Component type " + state.TypeName + " is not installed locally.";
                    return false;
                }
            }

            for (int desiredIndex = 1; desiredIndex < desiredTypes.Length; desiredIndex++)
            {
                Type desiredType = desiredTypes[desiredIndex];
                if (desiredType == null)
                {
                    continue;
                }

                Component[] current = gameObject.GetComponents<Component>();
                if (desiredIndex < current.Length &&
                    current[desiredIndex] != null &&
                    current[desiredIndex].GetType() == desiredType)
                {
                    continue;
                }

                Component match = null;
                for (int searchIndex = desiredIndex + 1; searchIndex < current.Length; searchIndex++)
                {
                    if (current[searchIndex] != null && current[searchIndex].GetType() == desiredType)
                    {
                        match = current[searchIndex];
                        break;
                    }
                }

                if (match == null)
                {
                    match = Undo.AddComponent(gameObject, desiredType);
                    if (match == null)
                    {
                        error = "Could not add " + desiredType.Name + " to " + gameObject.name + ".";
                        return false;
                    }
                }

                while (GetComponentIndex(gameObject, match) > desiredIndex)
                {
                    if (!ComponentUtility.MoveComponentUp(match))
                    {
                        error = "Could not match component order on " + gameObject.name + ".";
                        return false;
                    }
                }
            }

            Component[] finalComponents = gameObject.GetComponents<Component>();
            for (int index = finalComponents.Length - 1; index >= desiredTypes.Length; index--)
            {
                Component extra = finalComponents[index];
                if (extra == null)
                {
                    error = "A missing-script component differs on " + gameObject.name + ".";
                    return false;
                }

                Undo.DestroyObjectImmediate(extra);
            }

            finalComponents = gameObject.GetComponents<Component>();
            if (finalComponents.Length != desiredTypes.Length)
            {
                error = "Could not reconcile components on " + gameObject.name + ".";
                return false;
            }

            return true;
        }

        private static bool TryResolveObjectReference(
            UnitySyncObjectReferenceState reference,
            out Object value)
        {
            value = null;
            if (reference == null || reference.Kind == UnitySyncObjectReferenceKind.Null)
            {
                return true;
            }

            if (reference.Kind == UnitySyncObjectReferenceKind.SceneObject)
            {
                GameObject gameObject = ResolveAddress(reference.SceneObject);
                if (gameObject == null)
                {
                    return false;
                }

                if (reference.ComponentIndex < 0)
                {
                    value = gameObject;
                    return TypeMatches(value.GetType(), reference.ObjectTypeName);
                }

                Component[] components = gameObject.GetComponents<Component>();
                if (reference.ComponentIndex >= components.Length || components[reference.ComponentIndex] == null)
                {
                    return false;
                }

                value = components[reference.ComponentIndex];
                return TypeMatches(value.GetType(), reference.ObjectTypeName);
            }

            if (reference.Kind == UnitySyncObjectReferenceKind.Asset)
            {
                if (IsBuiltinAssetReference(reference))
                {
                    return TryResolveBuiltinAssetReference(reference, out value);
                }

                if (TryResolveAssetAtPath(
                        reference.AssetPath,
                        reference,
                        true,
                        out value))
                {
                    return true;
                }

                string guidPath = AssetDatabase.GUIDToAssetPath(reference.AssetGuid);
                if (!string.Equals(guidPath, reference.AssetPath, StringComparison.OrdinalIgnoreCase) &&
                    TryResolveAssetAtPath(
                        guidPath,
                        reference,
                        false,
                        out value))
                {
                    return true;
                }

                return TryFindAssetDeterministically(reference, out value);
            }

            return false;
        }

        private static bool TryResolveAssetAtPath(
            string assetPath,
            UnitySyncObjectReferenceState reference,
            bool allowPathEquivalent,
            out Object value)
        {
            value = null;
            if (!IsProjectAssetPath(assetPath))
            {
                return false;
            }

            Object uniqueLocalIdCandidate = null;
            int localIdCandidateCount = 0;
            Object[] candidates = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            foreach (Object candidate in candidates)
            {
                if (candidate == null ||
                    !TypeMatches(candidate.GetType(), reference.ObjectTypeName))
                {
                    continue;
                }

                if (ExactAssetIdentityMatches(candidate, reference))
                {
                    value = candidate;
                    return true;
                }
            }

            if (allowPathEquivalent)
            {
                Object uniqueLocalIdCandidateAtPath = null;
                int localIdCandidateCountAtPath = 0;
                Object uniqueNamedCandidateAtPath = null;
                int namedCandidateCountAtPath = 0;

                foreach (Object candidate in candidates)
                {
                    if (candidate == null ||
                        !TypeMatches(candidate.GetType(), reference.ObjectTypeName))
                    {
                        continue;
                    }

                    if (reference.LocalFileId != 0 &&
                        LocalFileIdMatches(candidate, reference.LocalFileId))
                    {
                        uniqueLocalIdCandidateAtPath = candidate;
                        localIdCandidateCountAtPath++;
                    }

                    if (candidate.name == reference.AssetName)
                    {
                        uniqueNamedCandidateAtPath = candidate;
                        namedCandidateCountAtPath++;
                    }
                }

                if (localIdCandidateCountAtPath == 1)
                {
                    value = uniqueLocalIdCandidateAtPath;
                    return true;
                }

                if (namedCandidateCountAtPath == 1)
                {
                    value = uniqueNamedCandidateAtPath;
                    return true;
                }
            }

            if (!AssetContentHashMatches(assetPath, reference.AssetContentHash))
            {
                return false;
            }

            foreach (Object candidate in candidates)
            {
                if (!BasicAssetCandidateMatches(candidate, reference))
                {
                    continue;
                }

                if (LocalFileIdMatches(candidate, reference.LocalFileId))
                {
                    uniqueLocalIdCandidate = candidate;
                    localIdCandidateCount++;
                }
            }

            if (localIdCandidateCount == 1)
            {
                value = uniqueLocalIdCandidate;
                return true;
            }

            return false;
        }

        private static bool TryFindAssetDeterministically(
            UnitySyncObjectReferenceState reference,
            out Object value)
        {
            value = null;
            Type assetType = ResolveType(reference.ObjectTypeName);
            if (assetType == null ||
                !typeof(Object).IsAssignableFrom(assetType) ||
                string.IsNullOrEmpty(reference.AssetName) ||
                string.IsNullOrEmpty(reference.AssetContentHash))
            {
                return false;
            }

            Object uniqueLocalIdCandidate = null;
            int localIdCandidateCount = 0;
            Object uniqueContentCandidate = null;
            int contentCandidateCount = 0;
            HashSet<int> visitedCandidates = new HashSet<int>();

            string[] candidateGuids = AssetDatabase.FindAssets("t:" + assetType.Name);
            Array.Sort(candidateGuids, StringComparer.Ordinal);
            foreach (string candidateGuid in candidateGuids)
            {
                string candidatePath = AssetDatabase.GUIDToAssetPath(candidateGuid);
                if (!IsProjectAssetPath(candidatePath))
                {
                    continue;
                }

                if (!AssetContentHashMatches(candidatePath, reference.AssetContentHash))
                {
                    continue;
                }

                foreach (Object candidate in AssetDatabase.LoadAllAssetsAtPath(candidatePath))
                {
                    if (!BasicAssetCandidateMatches(candidate, reference) ||
                        !visitedCandidates.Add(candidate.GetInstanceID()))
                    {
                        continue;
                    }

                    uniqueContentCandidate = candidate;
                    contentCandidateCount++;
                    if (LocalFileIdMatches(candidate, reference.LocalFileId))
                    {
                        uniqueLocalIdCandidate = candidate;
                        localIdCandidateCount++;
                    }
                }
            }

            if (localIdCandidateCount == 1)
            {
                value = uniqueLocalIdCandidate;
                return true;
            }

            if (contentCandidateCount == 1)
            {
                value = uniqueContentCandidate;
                return true;
            }

            return false;
        }

        private static bool TryResolveBuiltinAssetReference(
            UnitySyncObjectReferenceState reference,
            out Object value)
        {
            value = null;
            Type assetType = ResolveType(reference.ObjectTypeName);
            if (assetType == null || !typeof(Object).IsAssignableFrom(assetType))
            {
                return false;
            }

            if (assetType == typeof(Mesh) &&
                TryResolveBuiltinPrimitiveMesh(reference, out Mesh mesh))
            {
                value = mesh;
                return true;
            }

            if (assetType == typeof(Material) &&
                TryResolveBuiltinMaterialReference(reference, out Material material))
            {
                value = material;
                return true;
            }

            if (assetType == typeof(Shader))
            {
                Shader shader = Shader.Find(reference.AssetName);
                if (AssetReferenceMatches(shader, reference))
                {
                    value = shader;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveBuiltinPrimitiveMesh(
            UnitySyncObjectReferenceState reference,
            out Mesh mesh)
        {
            mesh = null;
            if (!TryGetPrimitiveType(reference.AssetName, out PrimitiveType primitiveType))
            {
                return false;
            }

            GameObject primitive = null;
            try
            {
                primitive = GameObject.CreatePrimitive(primitiveType);
                primitive.hideFlags = HideFlags.HideAndDontSave;
                MeshFilter meshFilter = primitive.GetComponent<MeshFilter>();
                Mesh candidate = meshFilter != null ? meshFilter.sharedMesh : null;
                if (!BuiltinAssetCandidateMatches(candidate, reference))
                {
                    return false;
                }

                mesh = candidate;
                return true;
            }
            finally
            {
                if (primitive != null)
                {
                    Object.DestroyImmediate(primitive);
                }
            }
        }

        private static bool TryResolveBuiltinMaterialReference(
            UnitySyncObjectReferenceState reference,
            out Material material)
        {
            material = null;
            if (reference == null || string.IsNullOrEmpty(reference.AssetName))
            {
                return false;
            }

            string resourceName = reference.AssetName.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                ? reference.AssetName
                : reference.AssetName + ".mat";

            Material candidate = AssetDatabase.GetBuiltinExtraResource<Material>(resourceName);
            if (BuiltinAssetCandidateMatches(candidate, reference))
            {
                material = candidate;
                return true;
            }

            // Keep the primitive-derived material as a fallback for Unity versions where the
            // default renderer material is exposed differently from the named extra resource.
            GameObject primitive = null;
            try
            {
                primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                primitive.hideFlags = HideFlags.HideAndDontSave;
                MeshRenderer meshRenderer = primitive.GetComponent<MeshRenderer>();
                candidate = meshRenderer != null ? meshRenderer.sharedMaterial : null;
                if (!BuiltinAssetCandidateMatches(candidate, reference))
                {
                    return false;
                }

                material = candidate;
                return true;
            }
            finally
            {
                if (primitive != null)
                {
                    Object.DestroyImmediate(primitive);
                }
            }
        }

        private static bool TryGetPrimitiveType(string assetName, out PrimitiveType primitiveType)
        {
            switch (assetName)
            {
                case "Sphere":
                    primitiveType = PrimitiveType.Sphere;
                    return true;
                case "Capsule":
                    primitiveType = PrimitiveType.Capsule;
                    return true;
                case "Cylinder":
                    primitiveType = PrimitiveType.Cylinder;
                    return true;
                case "Cube":
                    primitiveType = PrimitiveType.Cube;
                    return true;
                case "Plane":
                    primitiveType = PrimitiveType.Plane;
                    return true;
                case "Quad":
                    primitiveType = PrimitiveType.Quad;
                    return true;
                default:
                    primitiveType = default;
                    return false;
            }
        }

        private static bool AssetReferenceMatches(
            Object candidate,
            UnitySyncObjectReferenceState reference)
        {
            if (!BasicAssetCandidateMatches(candidate, reference))
            {
                return false;
            }

            if (IsBuiltinAssetReference(reference))
            {
                return BuiltinAssetCandidateMatches(candidate, reference);
            }

            if (ExactAssetIdentityMatches(candidate, reference))
            {
                return true;
            }

            string candidatePath = AssetDatabase.GetAssetPath(candidate) ?? string.Empty;
            return IsProjectAssetPath(candidatePath) &&
                   string.Equals(
                       NormalizeAssetPath(candidatePath),
                       NormalizeAssetPath(reference.AssetPath),
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool BasicAssetCandidateMatches(
            Object candidate,
            UnitySyncObjectReferenceState reference)
        {
            return candidate != null &&
                   TypeMatches(candidate.GetType(), reference.ObjectTypeName) &&
                   candidate.name == reference.AssetName;
        }

        private static bool ExactAssetIdentityMatches(
            Object candidate,
            UnitySyncObjectReferenceState reference)
        {
            return candidate != null &&
                   AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                       candidate,
                       out string candidateGuid,
                       out long candidateFileId) &&
                   candidateGuid == reference.AssetGuid &&
                   candidateFileId == reference.LocalFileId;
        }

        private static bool LocalFileIdMatches(Object candidate, long expectedLocalFileId)
        {
            return candidate != null &&
                   AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                       candidate,
                       out string _,
                       out long candidateFileId) &&
                   candidateFileId == expectedLocalFileId;
        }

        private static bool BuiltinAssetCandidateMatches(
            Object candidate,
            UnitySyncObjectReferenceState reference)
        {
            if (!BasicAssetCandidateMatches(candidate, reference) ||
                !EditorUtility.IsPersistent(candidate))
            {
                return false;
            }

            string candidatePath = AssetDatabase.GetAssetPath(candidate) ?? string.Empty;
            if (IsBuiltinAssetPath(candidatePath))
            {
                return true;
            }

            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                       candidate,
                       out string candidateGuid,
                       out long _) &&
                   IsBuiltinAssetGuid(candidateGuid);
        }

        private static bool IsBuiltinAssetReference(UnitySyncObjectReferenceState reference)
        {
            return reference != null &&
                   (IsBuiltinAssetPath(reference.AssetPath) ||
                    IsBuiltinAssetGuid(reference.AssetGuid));
        }

        private static bool IsBuiltinAssetGuid(string guid)
        {
            return guid == "0000000000000000e000000000000000" ||
                   guid == "0000000000000000f000000000000000";
        }

        private static bool IsProjectAssetPath(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            return normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAssetContentHash(string assetPath)
        {
            try
            {
                if (!TryGetAssetFileSystemPath(assetPath, out string fileSystemPath))
                {
                    return string.Empty;
                }

                FileInfo fileInfo = new FileInfo(fileSystemPath);
                long length = fileInfo.Length;
                long lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
                if (AssetFileHashCache.TryGetValue(fileSystemPath, out AssetFileHashCacheEntry cached) &&
                    cached.Length == length &&
                    cached.LastWriteTicks == lastWriteTicks)
                {
                    return cached.Hash;
                }

                string hash;
                using (FileStream stream = new FileStream(
                           fileSystemPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.ReadWrite))
                using (SHA256 sha256 = SHA256.Create())
                {
                    hash = Convert.ToBase64String(sha256.ComputeHash(stream));
                }

                AssetFileHashCache[fileSystemPath] = new AssetFileHashCacheEntry
                {
                    Length = length,
                    LastWriteTicks = lastWriteTicks,
                    Hash = hash
                };
                return hash;
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is IOException ||
                exception is NotSupportedException ||
                exception is UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static bool TryGetAssetFileSystemPath(string assetPath, out string fileSystemPath)
        {
            fileSystemPath = string.Empty;
            string normalized = NormalizeAssetPath(assetPath);
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                if (string.IsNullOrEmpty(projectRoot))
                {
                    return false;
                }

                fileSystemPath = Path.GetFullPath(Path.Combine(
                    projectRoot,
                    normalized.Replace('/', Path.DirectorySeparatorChar)));
                return File.Exists(fileSystemPath);
            }

            if (!normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(normalized);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath))
            {
                return false;
            }

            string packagePrefix = "Packages/" + package.name;
            if (!normalized.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string relativePath = normalized.Substring(packagePrefix.Length).TrimStart('/');
            fileSystemPath = Path.GetFullPath(Path.Combine(
                package.resolvedPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(fileSystemPath);
        }

        private static bool AssetContentHashMatches(string assetPath, string expectedHash)
        {
            if (string.IsNullOrEmpty(expectedHash))
            {
                return true;
            }

            string localHash = GetAssetContentHash(assetPath);
            return !string.IsNullOrEmpty(localHash) &&
                   string.Equals(localHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            return (assetPath ?? string.Empty).Replace('\\', '/').Trim();
        }

        private static bool IsBuiltinAssetPath(string assetPath)
        {
            return string.Equals(
                       assetPath,
                       "Resources/unity_builtin_extra",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       assetPath,
                       "Library/unity default resources",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       assetPath,
                       "Library/unity editor resources",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static AnimationCurve CreateAnimationCurve(UnitySyncAnimationCurveState state)
        {
            state = state ?? new UnitySyncAnimationCurveState();
            AnimationCurve curve = new AnimationCurve(state.Keys ?? new Keyframe[0])
            {
                preWrapMode = state.PreWrapMode,
                postWrapMode = state.PostWrapMode
            };
            return curve;
        }

        private static bool ApplyGradient(SerializedProperty property, UnitySyncGradientState state)
        {
            if (GradientValueProperty == null || state == null)
            {
                return false;
            }

            Gradient gradient = new Gradient
            {
                mode = state.Mode
            };
            gradient.SetKeys(
                state.ColorKeys ?? new GradientColorKey[0],
                state.AlphaKeys ?? new GradientAlphaKey[0]);
            GradientValueProperty.SetValue(property, gradient, null);
            return true;
        }

        private static bool ApplyManagedReference(SerializedProperty property, string fullTypeName)
        {
            if (string.IsNullOrEmpty(fullTypeName))
            {
                property.managedReferenceValue = null;
                return true;
            }

            Type desiredType = ResolveManagedReferenceType(fullTypeName);
            if (desiredType == null)
            {
                return false;
            }

            object current = property.managedReferenceValue;
            if (current == null || current.GetType() != desiredType)
            {
                property.managedReferenceValue = Activator.CreateInstance(desiredType);
            }

            return true;
        }

        private static Type ResolveManagedReferenceType(string fullTypeName)
        {
            int separator = fullTypeName.IndexOf(' ');
            if (separator <= 0 || separator >= fullTypeName.Length - 1)
            {
                return null;
            }

            string assemblyName = fullTypeName.Substring(0, separator);
            string typeName = fullTypeName.Substring(separator + 1);
            return ResolveType(typeName + ", " + assemblyName);
        }

        private static string GetStableTypeName(Type type)
        {
            return type.FullName + ", " + type.Assembly.GetName().Name;
        }

        private static bool TypeMatches(Type type, string stableTypeName)
        {
            return type != null && GetStableTypeName(type) == stableTypeName;
        }

        private static Type ResolveType(string stableTypeName)
        {
            Type type = Type.GetType(stableTypeName, false);
            if (type != null)
            {
                return type;
            }

            int separator = stableTypeName.IndexOf(',');
            string fullName = separator >= 0
                ? stableTypeName.Substring(0, separator).Trim()
                : stableTypeName.Trim();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static int GetComponentIndex(GameObject gameObject, Component component)
        {
            Component[] components = gameObject.GetComponents<Component>();
            for (int index = 0; index < components.Length; index++)
            {
                if (components[index] == component)
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool IsEligibleSceneObject(GameObject gameObject)
        {
            return gameObject != null &&
                   gameObject.scene.IsValid() &&
                   gameObject.scene.isLoaded &&
                   !EditorUtility.IsPersistent(gameObject) &&
                   !UnitySyncHierarchy.IsUnitySyncObject(gameObject);
        }

        private static void AddHierarchy(GameObject gameObject, List<GameObject> result)
        {
            if (!IsEligibleSceneObject(gameObject))
            {
                return;
            }

            result.Add(gameObject);
            for (int childIndex = 0; childIndex < gameObject.transform.childCount; childIndex++)
            {
                AddHierarchy(gameObject.transform.GetChild(childIndex).gameObject, result);
            }
        }

        private static int GetFilteredSiblingIndex(Transform transform)
        {
            int filteredIndex = 0;
            if (transform.parent == null)
            {
                foreach (GameObject root in transform.gameObject.scene.GetRootGameObjects())
                {
                    if (UnitySyncHierarchy.IsUnitySyncObject(root))
                    {
                        continue;
                    }

                    if (root.transform == transform)
                    {
                        return filteredIndex;
                    }

                    filteredIndex++;
                }
            }
            else
            {
                for (int index = 0; index < transform.parent.childCount; index++)
                {
                    Transform sibling = transform.parent.GetChild(index);
                    if (UnitySyncHierarchy.IsUnitySyncObject(sibling.gameObject))
                    {
                        continue;
                    }

                    if (sibling == transform)
                    {
                        return filteredIndex;
                    }

                    filteredIndex++;
                }
            }

            return -1;
        }

        private static GameObject GetFilteredRoot(Scene scene, int filteredIndex)
        {
            int currentIndex = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (UnitySyncHierarchy.IsUnitySyncObject(root))
                {
                    continue;
                }

                if (currentIndex == filteredIndex)
                {
                    return root;
                }

                currentIndex++;
            }

            return null;
        }

        private static Transform GetFilteredChild(Transform parent, int filteredIndex)
        {
            int currentIndex = 0;
            for (int index = 0; index < parent.childCount; index++)
            {
                Transform child = parent.GetChild(index);
                if (UnitySyncHierarchy.IsUnitySyncObject(child.gameObject))
                {
                    continue;
                }

                if (currentIndex == filteredIndex)
                {
                    return child;
                }

                currentIndex++;
            }

            return null;
        }

        private static void SetFilteredSiblingIndex(Transform transform, int filteredIndex)
        {
            if (transform == null || filteredIndex < 0)
            {
                return;
            }

            List<Transform> siblings = new List<Transform>();
            if (transform.parent == null)
            {
                foreach (GameObject root in transform.gameObject.scene.GetRootGameObjects())
                {
                    if (root.transform != transform && !UnitySyncHierarchy.IsUnitySyncObject(root))
                    {
                        siblings.Add(root.transform);
                    }
                }
            }
            else
            {
                for (int index = 0; index < transform.parent.childCount; index++)
                {
                    Transform sibling = transform.parent.GetChild(index);
                    if (sibling != transform && !UnitySyncHierarchy.IsUnitySyncObject(sibling.gameObject))
                    {
                        siblings.Add(sibling);
                    }
                }
            }

            int currentIndex = transform.GetSiblingIndex();
            if (filteredIndex < siblings.Count)
            {
                int targetIndex = siblings[filteredIndex].GetSiblingIndex();
                transform.SetSiblingIndex(currentIndex < targetIndex ? targetIndex - 1 : targetIndex);
            }
            else if (siblings.Count > 0)
            {
                int lastIndex = siblings[siblings.Count - 1].GetSiblingIndex();
                transform.SetSiblingIndex(currentIndex < lastIndex ? lastIndex : lastIndex + 1);
            }
        }

        private static string Describe(UnitySyncSceneObjectAddress address)
        {
            string scene = string.IsNullOrEmpty(address.ScenePath) ? address.SceneName : address.ScenePath;
            return string.IsNullOrEmpty(address.ObjectId)
                ? scene + " [" + string.Join(",", address.SiblingPath) + "]"
                : scene + " (" + address.ObjectId + ")";
        }

        private static bool HasFloats(UnitySyncSerializedPropertyState state, int count)
        {
            return state.FloatValues != null && state.FloatValues.Length == count;
        }

        private static bool HasIntegers(UnitySyncSerializedPropertyState state, int count)
        {
            return state.IntegerValues != null && state.IntegerValues.Length == count;
        }

        private static bool IsIgnoredPropertyPath(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath))
            {
                return true;
            }

            foreach (string ignoredPath in IgnoredPropertyPaths)
            {
                if (propertyPath == ignoredPath ||
                    propertyPath.StartsWith(ignoredPath + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanApplyObjectReference(
            SerializedProperty property,
            UnitySyncObjectReferenceState reference)
        {
            return reference != null &&
                   !string.IsNullOrEmpty(reference.SerializedPropertyTypeName) &&
                   property.type == reference.SerializedPropertyTypeName;
        }

        private static bool IsSerializedReferenceTypeCompatible(
            string serializedPropertyTypeName,
            Object value)
        {
            if (value == null || string.IsNullOrEmpty(serializedPropertyTypeName))
            {
                return true;
            }

            const string prefix = "PPtr<";
            if (!serializedPropertyTypeName.StartsWith(prefix, StringComparison.Ordinal) ||
                !serializedPropertyTypeName.EndsWith(">", StringComparison.Ordinal))
            {
                return true;
            }

            string expectedTypeName = serializedPropertyTypeName.Substring(
                prefix.Length,
                serializedPropertyTypeName.Length - prefix.Length - 1);
            if (expectedTypeName.StartsWith("$", StringComparison.Ordinal))
            {
                expectedTypeName = expectedTypeName.Substring(1);
            }

            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                if (type.Name == expectedTypeName)
                {
                    return true;
                }
            }

            return false;
        }

        private static float[] ToArray(Color value)
        {
            return new[] { value.r, value.g, value.b, value.a };
        }

        private static float[] ToArray(Vector2 value)
        {
            return new[] { value.x, value.y };
        }

        private static float[] ToArray(Vector3 value)
        {
            return new[] { value.x, value.y, value.z };
        }

        private static float[] ToArray(Vector4 value)
        {
            return new[] { value.x, value.y, value.z, value.w };
        }

        private static float[] ToArray(Quaternion value)
        {
            return new[] { value.x, value.y, value.z, value.w };
        }
    }
}
