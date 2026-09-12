using System;
using System.Collections.Generic;
using System.Reflection;
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
        private static readonly PropertyInfo GradientValueProperty = typeof(SerializedProperty).GetProperty(
            "gradientValue",
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
                Address = address,
                GameObject = CaptureGameObjectSettings(gameObject)
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

            change = new UnitySyncSceneObjectChange
            {
                Address = address,
                Components = new[] { CaptureComponent(component, componentIndex) }
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
                componentStates[index] = CaptureComponent(components[index], index);
            }

            change = new UnitySyncSceneObjectChange
            {
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

        internal static bool Apply(UnitySyncSceneObjectChange change, out string error)
        {
            error = string.Empty;
            if (change == null || change.Address == null)
            {
                error = "The scene update did not contain a valid object address.";
                return false;
            }

            GameObject gameObject = ResolveAddress(change.Address);
            if (gameObject == null)
            {
                error = "No matching object exists at " + Describe(change.Address) + ".";
                return false;
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

            address = new UnitySyncSceneObjectAddress
            {
                ScenePath = gameObject.scene.path ?? string.Empty,
                SceneName = gameObject.scene.name ?? string.Empty,
                SceneIndex = sceneIndex,
                SiblingPath = path.ToArray()
            };
            return true;
        }

        internal static GameObject ResolveAddress(UnitySyncSceneObjectAddress address)
        {
            if (address == null || address.SiblingPath == null || address.SiblingPath.Length == 0)
            {
                return null;
            }

            Scene scene = default;
            if (!string.IsNullOrEmpty(address.ScenePath))
            {
                scene = SceneManager.GetSceneByPath(address.ScenePath);
            }

            if (!scene.IsValid() &&
                address.SceneIndex >= 0 &&
                address.SceneIndex < SceneManager.sceneCount)
            {
                Scene indexedScene = SceneManager.GetSceneAt(address.SceneIndex);
                if (string.IsNullOrEmpty(address.SceneName) || indexedScene.name == address.SceneName)
                {
                    scene = indexedScene;
                }
            }

            if (!scene.IsValid() && !string.IsNullOrEmpty(address.SceneName))
            {
                for (int index = 0; index < SceneManager.sceneCount; index++)
                {
                    Scene candidate = SceneManager.GetSceneAt(index);
                    if (candidate.name == address.SceneName)
                    {
                        scene = candidate;
                        break;
                    }
                }
            }

            if (!scene.IsValid() || !scene.isLoaded)
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

        private static UnitySyncComponentState CaptureComponent(Component component, int componentIndex)
        {
            UnitySyncComponentState state = new UnitySyncComponentState
            {
                ComponentIndex = componentIndex,
                TypeName = component != null ? GetStableTypeName(component.GetType()) : string.Empty
            };

            if (component == null)
            {
                return state;
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
                }
            }

            state.Properties = properties.ToArray();
            return state;
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
                    return TryCaptureObjectReference(property.objectReferenceValue, out state.ObjectReference);

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
                    return TryCaptureObjectReference(property.exposedReferenceValue, out state.ObjectReference);

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
                reference.AssetTypeName = GetStableTypeName(value.GetType());
                reference.AssetName = value.name ?? string.Empty;
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

            SerializedObject serializedObject = new SerializedObject(component);
            serializedObject.UpdateIfRequiredOrScript();
            Undo.RecordObject(component, "Apply UnitySync component settings");

            foreach (UnitySyncSerializedPropertyState propertyState in state.Properties)
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

            foreach (UnitySyncSerializedPropertyState propertyState in state.Properties)
            {
                if (propertyState == null || IsIgnoredPropertyPath(propertyState.Path))
                {
                    continue;
                }

                if (propertyState.Kind == UnitySyncSerializedValueKind.ArraySize ||
                    propertyState.Kind == UnitySyncSerializedValueKind.ManagedReference)
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
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            EditorUtility.SetDirty(component);
            return true;
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
                        if (!TryResolveObjectReference(
                                state.ObjectReference,
                                property.objectReferenceValue,
                                out Object objectReference)) return false;
                        property.objectReferenceValue = objectReference;
                        return true;

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
                        if (!TryResolveObjectReference(
                                state.ObjectReference,
                                property.exposedReferenceValue,
                                out Object exposedReference)) return false;
                        property.exposedReferenceValue = exposedReference;
                        return true;

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
            Object currentValue,
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
                    return true;
                }

                Component[] components = gameObject.GetComponents<Component>();
                if (reference.ComponentIndex >= components.Length || components[reference.ComponentIndex] == null)
                {
                    return false;
                }

                value = components[reference.ComponentIndex];
                return true;
            }

            if (reference.Kind == UnitySyncObjectReferenceKind.Asset)
            {
                if (AssetReferenceMatches(currentValue, reference))
                {
                    value = currentValue;
                    return true;
                }

                string assetPath = AssetDatabase.GUIDToAssetPath(reference.AssetGuid);
                if (string.IsNullOrEmpty(assetPath))
                {
                    assetPath = reference.AssetPath;
                }

                if (!string.IsNullOrEmpty(assetPath))
                {
                    foreach (Object candidate in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                    {
                        if (AssetReferenceMatches(candidate, reference))
                        {
                            value = candidate;
                            return true;
                        }
                    }
                }

                if (IsBuiltinAssetReference(reference))
                {
                    Type assetType = ResolveType(reference.AssetTypeName);
                    if (assetType == null || !typeof(Object).IsAssignableFrom(assetType))
                    {
                        return false;
                    }

                    foreach (Object candidate in Resources.FindObjectsOfTypeAll(assetType))
                    {
                        if (AssetReferenceMatches(candidate, reference))
                        {
                            value = candidate;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool AssetReferenceMatches(
            Object candidate,
            UnitySyncObjectReferenceState reference)
        {
            if (candidate == null ||
                !TypeMatches(candidate.GetType(), reference.AssetTypeName) ||
                candidate.name != reference.AssetName)
            {
                return false;
            }

            if (IsBuiltinAssetReference(reference))
            {
                string candidatePath = AssetDatabase.GetAssetPath(candidate) ?? string.Empty;
                return EditorUtility.IsPersistent(candidate) &&
                       (string.IsNullOrEmpty(candidatePath) || IsBuiltinAssetPath(candidatePath));
            }

            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                       candidate,
                       out string candidateGuid,
                       out long candidateFileId) &&
                   candidateGuid == reference.AssetGuid &&
                   candidateFileId == reference.LocalFileId;
        }

        private static bool IsBuiltinAssetReference(UnitySyncObjectReferenceState reference)
        {
            return reference != null &&
                   (IsBuiltinAssetPath(reference.AssetPath) ||
                    reference.AssetGuid == "0000000000000000e000000000000000" ||
                    reference.AssetGuid == "0000000000000000f000000000000000");
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

        private static string Describe(UnitySyncSceneObjectAddress address)
        {
            string scene = string.IsNullOrEmpty(address.ScenePath) ? address.SceneName : address.ScenePath;
            return scene + " [" + string.Join(",", address.SiblingPath) + "]";
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
