using System;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal enum UnitySyncSerializedValueKind : byte
    {
        Integer = 1,
        Boolean = 2,
        Float = 3,
        String = 4,
        Color = 5,
        ObjectReference = 6,
        LayerMask = 7,
        Enum = 8,
        Vector2 = 9,
        Vector3 = 10,
        Vector4 = 11,
        Rect = 12,
        ArraySize = 13,
        Character = 14,
        AnimationCurve = 15,
        Bounds = 16,
        Gradient = 17,
        Quaternion = 18,
        ExposedReference = 19,
        Vector2Int = 20,
        Vector3Int = 21,
        RectInt = 22,
        BoundsInt = 23,
        ManagedReference = 24,
        Hash128 = 25
    }

    internal enum UnitySyncObjectReferenceKind : byte
    {
        Null = 0,
        Asset = 1,
        SceneObject = 2
    }

    internal enum UnitySyncSceneChangeKind : byte
    {
        Upsert = 0,
        Destroy = 1
    }

    internal sealed class UnitySyncSceneObjectAddress
    {
        // This is a per-session identity, not a Unity instance ID or a value saved to a scene.
        // Sibling paths are retained only as useful diagnostics and initial layout data.
        internal string ObjectId = string.Empty;
        internal string ParentObjectId = string.Empty;
        internal int SiblingIndex = -1;
        internal string ScenePath = string.Empty;
        internal string SceneName = string.Empty;
        internal int SceneIndex;
        internal int[] SiblingPath = new int[0];

        internal string Key
        {
            get
            {
                if (!string.IsNullOrEmpty(ObjectId))
                {
                    return ObjectId;
                }

                string sceneKey = string.IsNullOrEmpty(ScenePath)
                    ? SceneIndex + ":" + SceneName
                    : ScenePath;
                return sceneKey + "|" + string.Join(".", SiblingPath);
            }
        }
    }

    internal sealed class UnitySyncSceneDescriptor
    {
        internal string ScenePath = string.Empty;
        internal string SceneName = string.Empty;
        internal int SceneIndex;
        internal UnitySyncObjectReferenceState SkyboxMaterial;
    }

    internal sealed class UnitySyncSceneSnapshotBoundary
    {
        internal Guid SnapshotId;
        // Set on the end boundary. An incomplete snapshot may update objects, but it must not
        // remove unmatched local objects because the host did not provide a complete state.
        internal bool IsComplete = true;
        internal UnitySyncSceneDescriptor[] Scenes = new UnitySyncSceneDescriptor[0];
    }

    internal sealed class UnitySyncObjectReferenceState
    {
        internal UnitySyncObjectReferenceKind Kind;
        internal string SerializedPropertyTypeName = string.Empty;
        internal string ObjectTypeName = string.Empty;
        internal string AssetGuid = string.Empty;
        internal string AssetPath = string.Empty;
        internal string AssetName = string.Empty;
        internal string AssetContentHash = string.Empty;
        internal long LocalFileId;
        internal UnitySyncSceneObjectAddress SceneObject;
        internal int ComponentIndex = -1;
    }

    internal sealed class UnitySyncAnimationCurveState
    {
        internal WrapMode PreWrapMode;
        internal WrapMode PostWrapMode;
        internal Keyframe[] Keys = new Keyframe[0];
    }

    internal sealed class UnitySyncGradientState
    {
        internal GradientMode Mode;
        internal GradientColorKey[] ColorKeys = new GradientColorKey[0];
        internal GradientAlphaKey[] AlphaKeys = new GradientAlphaKey[0];
    }

    internal sealed class UnitySyncSerializedPropertyState
    {
        internal string Path = string.Empty;
        internal UnitySyncSerializedValueKind Kind;
        internal long IntegerValue;
        internal double NumberValue;
        internal string StringValue = string.Empty;
        internal float[] FloatValues = new float[0];
        internal int[] IntegerValues = new int[0];
        internal UnitySyncObjectReferenceState ObjectReference;
        internal UnitySyncAnimationCurveState AnimationCurve;
        internal UnitySyncGradientState Gradient;
    }

    internal sealed class UnitySyncComponentState
    {
        internal int ComponentIndex;
        internal string TypeName = string.Empty;
        internal UnitySyncSerializedPropertyState[] Properties = new UnitySyncSerializedPropertyState[0];
    }

    internal sealed class UnitySyncGameObjectState
    {
        internal string Name = string.Empty;
        internal bool ActiveSelf;
        internal int Layer;
        internal string Tag = string.Empty;
        internal int StaticEditorFlags;
    }

    internal sealed class UnitySyncSceneObjectChange
    {
        internal UnitySyncSceneChangeKind Kind;
        internal Guid SnapshotId;
        internal UnitySyncSceneObjectAddress Address;
        internal bool HierarchyOnly;
        internal bool ReconcileComponents;
        internal UnitySyncGameObjectState GameObject;
        internal UnitySyncComponentState[] Components = new UnitySyncComponentState[0];
    }
}
