using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal enum UnitySyncMessageType : byte
    {
        Hello = 1,
        Welcome = 2,
        Viewport = 3,
        PeerLeft = 4,
        SceneObjectChange = 5,
        SceneSnapshotRequest = 6,
        SceneSnapshotBegin = 7,
        SceneSnapshotEnd = 8
    }

    internal readonly struct UnitySyncViewportState
    {
        internal readonly Guid PlayerId;
        internal readonly string DisplayName;
        internal readonly Color Color;
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;
        internal readonly Vector3 Pivot;
        internal readonly float FieldOfView;
        internal readonly float Aspect;
        internal readonly bool Orthographic;
        internal readonly float OrthographicSize;

        internal UnitySyncViewportState(
            Guid playerId,
            string displayName,
            Color color,
            Vector3 position,
            Quaternion rotation,
            Vector3 pivot,
            float fieldOfView,
            float aspect,
            bool orthographic,
            float orthographicSize)
        {
            PlayerId = playerId;
            DisplayName = displayName;
            Color = color;
            Position = position;
            Rotation = rotation;
            Pivot = pivot;
            FieldOfView = fieldOfView;
            Aspect = aspect;
            Orthographic = orthographic;
            OrthographicSize = orthographicSize;
        }
    }

    internal readonly struct UnitySyncMessage
    {
        internal readonly UnitySyncMessageType Type;
        internal readonly Guid PlayerId;
        internal readonly string DisplayName;
        internal readonly UnitySyncViewportState Viewport;
        internal readonly UnitySyncSceneObjectChange SceneChange;
        internal readonly UnitySyncSceneSnapshotBoundary SceneSnapshot;

        internal UnitySyncMessage(
            UnitySyncMessageType type,
            Guid playerId,
            string displayName,
            UnitySyncViewportState viewport,
            UnitySyncSceneObjectChange sceneChange = null,
            UnitySyncSceneSnapshotBoundary sceneSnapshot = null)
        {
            Type = type;
            PlayerId = playerId;
            DisplayName = displayName;
            Viewport = viewport;
            SceneChange = sceneChange;
            SceneSnapshot = sceneSnapshot;
        }
    }

    internal static class UnitySyncProtocol
    {
        internal const int Version = 8;
        internal const int MaximumFrameSize = 8 * 1024 * 1024;
        internal const int MaximumDisplayNameBytes = 128;
        private const int MaximumStringBytes = 1024 * 1024;
        private const int MaximumHierarchyDepth = 256;
        private const int MaximumScenesPerSnapshot = 256;
        private const int MaximumComponentsPerObject = 1024;
        private const int MaximumPropertiesPerComponent = 65536;
        private const int MaximumArrayElements = 65536;

        internal static byte[] CreateHello(Guid playerId, string displayName)
        {
            return WriteMessage(writer =>
            {
                writer.Write((byte)UnitySyncMessageType.Hello);
                WriteGuid(writer, playerId);
                WriteString(writer, displayName);
            });
        }

        internal static byte[] CreateWelcome(Guid playerId, string displayName)
        {
            return WriteMessage(writer =>
            {
                writer.Write((byte)UnitySyncMessageType.Welcome);
                WriteGuid(writer, playerId);
                WriteString(writer, displayName);
            });
        }

        internal static byte[] CreateViewport(UnitySyncViewportState state)
        {
            return WriteMessage(writer =>
            {
                writer.Write((byte)UnitySyncMessageType.Viewport);
                WriteGuid(writer, state.PlayerId);
                WriteString(writer, state.DisplayName);
                WriteColor(writer, state.Color);
                WriteVector3(writer, state.Position);
                WriteQuaternion(writer, state.Rotation);
                WriteVector3(writer, state.Pivot);
                writer.Write(state.FieldOfView);
                writer.Write(state.Aspect);
                writer.Write(state.Orthographic);
                writer.Write(state.OrthographicSize);
            });
        }

        internal static byte[] CreatePeerLeft(Guid playerId)
        {
            return WriteMessage(writer =>
            {
                writer.Write((byte)UnitySyncMessageType.PeerLeft);
                WriteGuid(writer, playerId);
            });
        }

        internal static byte[] CreateSceneObjectChange(Guid playerId, UnitySyncSceneObjectChange change)
        {
            if (change == null || change.Address == null)
            {
                throw new ArgumentNullException(nameof(change));
            }

            return WriteMessage(writer =>
            {
                writer.Write((byte)UnitySyncMessageType.SceneObjectChange);
                WriteGuid(writer, playerId);
                WriteSceneObjectChange(writer, change);
            });
        }

        internal static byte[] CreateSceneSnapshotRequest(Guid playerId)
        {
            return WriteMessage(writer =>
            {
                writer.Write((byte)UnitySyncMessageType.SceneSnapshotRequest);
                WriteGuid(writer, playerId);
            });
        }

        internal static byte[] CreateSceneSnapshotBegin(
            Guid playerId,
            UnitySyncSceneSnapshotBoundary snapshot)
        {
            if (snapshot == null || snapshot.SnapshotId == Guid.Empty)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            return WriteMessage(writer =>
            {
                writer.Write((byte)UnitySyncMessageType.SceneSnapshotBegin);
                WriteGuid(writer, playerId);
                WriteSceneSnapshotBoundary(writer, snapshot, true);
            });
        }

        internal static byte[] CreateSceneSnapshotEnd(
            Guid playerId,
            Guid snapshotId,
            bool isComplete)
        {
            if (snapshotId == Guid.Empty)
            {
                throw new ArgumentException("A scene snapshot ID is required.", nameof(snapshotId));
            }

            return WriteMessage(writer =>
            {
                writer.Write((byte)UnitySyncMessageType.SceneSnapshotEnd);
                WriteGuid(writer, playerId);
                WriteGuid(writer, snapshotId);
                writer.Write(isComplete);
            });
        }

        internal static bool TryRead(byte[] payload, out UnitySyncMessage message)
        {
            message = default;
            if (payload == null || payload.Length < 2)
            {
                return false;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(payload, false))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadByte() != Version)
                    {
                        return false;
                    }

                    UnitySyncMessageType type = (UnitySyncMessageType)reader.ReadByte();
                    Guid playerId;
                    string displayName;

                    switch (type)
                    {
                        case UnitySyncMessageType.Hello:
                        case UnitySyncMessageType.Welcome:
                            playerId = ReadGuid(reader);
                            displayName = ReadString(reader);
                            message = new UnitySyncMessage(type, playerId, displayName, default);
                            break;

                        case UnitySyncMessageType.Viewport:
                            playerId = ReadGuid(reader);
                            displayName = ReadString(reader);
                            UnitySyncViewportState viewport = new UnitySyncViewportState(
                                playerId,
                                displayName,
                                ReadColor(reader),
                                ReadVector3(reader),
                                ReadQuaternion(reader),
                                ReadVector3(reader),
                                reader.ReadSingle(),
                                reader.ReadSingle(),
                                reader.ReadBoolean(),
                                reader.ReadSingle());
                            message = new UnitySyncMessage(type, playerId, displayName, viewport);
                            break;

                        case UnitySyncMessageType.PeerLeft:
                            playerId = ReadGuid(reader);
                            message = new UnitySyncMessage(type, playerId, string.Empty, default);
                            break;

                        case UnitySyncMessageType.SceneObjectChange:
                            playerId = ReadGuid(reader);
                            UnitySyncSceneObjectChange sceneChange = ReadSceneObjectChange(reader);
                            message = new UnitySyncMessage(type, playerId, string.Empty, default, sceneChange);
                            break;

                        case UnitySyncMessageType.SceneSnapshotRequest:
                            playerId = ReadGuid(reader);
                            message = new UnitySyncMessage(type, playerId, string.Empty, default);
                            break;

                        case UnitySyncMessageType.SceneSnapshotBegin:
                            playerId = ReadGuid(reader);
                            UnitySyncSceneSnapshotBoundary snapshot = ReadSceneSnapshotBoundary(reader);
                            message = new UnitySyncMessage(
                                type,
                                playerId,
                                string.Empty,
                                default,
                                null,
                                snapshot);
                            break;

                        case UnitySyncMessageType.SceneSnapshotEnd:
                            playerId = ReadGuid(reader);
                            message = new UnitySyncMessage(
                                type,
                                playerId,
                                string.Empty,
                                default,
                                null,
                                new UnitySyncSceneSnapshotBoundary
                                {
                                    SnapshotId = ReadGuid(reader),
                                    IsComplete = reader.ReadBoolean()
                                });
                            break;

                        default:
                            return false;
                    }

                    return stream.Position == stream.Length;
                }
            }
            catch (Exception exception) when (
                exception is EndOfStreamException ||
                exception is IOException ||
                exception is ArgumentException ||
                exception is InvalidDataException ||
                exception is OverflowException ||
                exception is DecoderFallbackException)
            {
                return false;
            }
        }

        private static byte[] WriteMessage(Action<BinaryWriter> writeBody)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write((byte)Version);
                writeBody(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteGuid(BinaryWriter writer, Guid guid)
        {
            writer.Write(guid.ToByteArray());
        }

        private static Guid ReadGuid(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(16);
            if (bytes.Length != 16)
            {
                throw new EndOfStreamException();
            }

            return new Guid(bytes);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > MaximumDisplayNameBytes)
            {
                throw new ArgumentException("Display name is too long.", nameof(value));
            }

            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int byteCount = reader.ReadUInt16();
            if (byteCount > MaximumDisplayNameBytes)
            {
                throw new InvalidDataException("Display name is too long.");
            }

            byte[] bytes = reader.ReadBytes(byteCount);
            if (bytes.Length != byteCount)
            {
                throw new EndOfStreamException();
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteSceneObjectChange(BinaryWriter writer, UnitySyncSceneObjectChange change)
        {
            if (change.Kind != UnitySyncSceneChangeKind.Upsert &&
                change.Kind != UnitySyncSceneChangeKind.Destroy)
            {
                throw new InvalidDataException("Unsupported scene change kind.");
            }

            writer.Write((byte)change.Kind);
            WriteGuid(writer, change.SnapshotId);
            WriteSceneAddress(writer, change.Address);
            writer.Write(change.HierarchyOnly);
            writer.Write(change.ReconcileComponents);

            if (change.Kind == UnitySyncSceneChangeKind.Destroy)
            {
                return;
            }

            writer.Write(change.GameObject != null);
            if (change.GameObject != null)
            {
                WriteLimitedString(writer, change.GameObject.Name);
                writer.Write(change.GameObject.ActiveSelf);
                writer.Write(change.GameObject.Layer);
                WriteLimitedString(writer, change.GameObject.Tag);
                writer.Write(change.GameObject.StaticEditorFlags);
            }

            UnitySyncComponentState[] components = change.Components ?? new UnitySyncComponentState[0];
            if (components.Length > MaximumComponentsPerObject)
            {
                throw new InvalidDataException("Too many components in a scene change.");
            }

            writer.Write((ushort)components.Length);
            foreach (UnitySyncComponentState component in components)
            {
                if (component == null)
                {
                    throw new InvalidDataException("A scene change contains an invalid component.");
                }

                writer.Write(component.ComponentIndex);
                WriteLimitedString(writer, component.TypeName);

                UnitySyncSerializedPropertyState[] properties = component.Properties ?? new UnitySyncSerializedPropertyState[0];
                if (properties.Length > MaximumPropertiesPerComponent)
                {
                    throw new InvalidDataException("Too many properties in a component change.");
                }

                writer.Write(properties.Length);
                foreach (UnitySyncSerializedPropertyState property in properties)
                {
                    WriteProperty(writer, property);
                }
            }
        }

        private static UnitySyncSceneObjectChange ReadSceneObjectChange(BinaryReader reader)
        {
            UnitySyncSceneObjectChange change = new UnitySyncSceneObjectChange
            {
                Kind = (UnitySyncSceneChangeKind)reader.ReadByte(),
                SnapshotId = ReadGuid(reader),
                Address = ReadSceneAddress(reader),
                HierarchyOnly = reader.ReadBoolean(),
                ReconcileComponents = reader.ReadBoolean()
            };

            if (change.Kind != UnitySyncSceneChangeKind.Upsert &&
                change.Kind != UnitySyncSceneChangeKind.Destroy)
            {
                throw new InvalidDataException("Unsupported scene change kind.");
            }

            if (change.Kind == UnitySyncSceneChangeKind.Destroy)
            {
                if (change.HierarchyOnly || change.ReconcileComponents)
                {
                    throw new InvalidDataException("Invalid destroyed-object scene change.");
                }

                return change;
            }

            if (reader.ReadBoolean())
            {
                int layer;
                change.GameObject = new UnitySyncGameObjectState
                {
                    Name = ReadLimitedString(reader),
                    ActiveSelf = reader.ReadBoolean(),
                    Layer = layer = reader.ReadInt32(),
                    Tag = ReadLimitedString(reader),
                    StaticEditorFlags = reader.ReadInt32()
                };

                if (layer < 0 || layer > 31)
                {
                    throw new InvalidDataException("Invalid GameObject layer.");
                }
            }

            int componentCount = reader.ReadUInt16();
            if (componentCount > MaximumComponentsPerObject)
            {
                throw new InvalidDataException("Too many components in a scene change.");
            }

            change.Components = new UnitySyncComponentState[componentCount];
            for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                UnitySyncComponentState component = new UnitySyncComponentState
                {
                    ComponentIndex = reader.ReadInt32(),
                    TypeName = ReadLimitedString(reader)
                };

                if (component.ComponentIndex < 0 || component.ComponentIndex >= MaximumComponentsPerObject)
                {
                    throw new InvalidDataException("Invalid component index.");
                }

                int propertyCount = reader.ReadInt32();
                if (propertyCount < 0 || propertyCount > MaximumPropertiesPerComponent)
                {
                    throw new InvalidDataException("Too many properties in a component change.");
                }

                component.Properties = new UnitySyncSerializedPropertyState[propertyCount];
                for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                {
                    component.Properties[propertyIndex] = ReadProperty(reader);
                }

                change.Components[componentIndex] = component;
            }

            return change;
        }

        private static void WriteSceneAddress(BinaryWriter writer, UnitySyncSceneObjectAddress address)
        {
            if (address == null)
            {
                throw new InvalidDataException("A scene object address is required.");
            }

            if (string.IsNullOrEmpty(address.ObjectId) || !Guid.TryParse(address.ObjectId, out _))
            {
                throw new InvalidDataException("A scene object session ID is required.");
            }

            if (!string.IsNullOrEmpty(address.ParentObjectId) &&
                !Guid.TryParse(address.ParentObjectId, out _))
            {
                throw new InvalidDataException("Invalid scene parent session ID.");
            }

            WriteLimitedString(writer, address.ObjectId);
            WriteLimitedString(writer, address.ParentObjectId);
            writer.Write(address.SiblingIndex);
            WriteLimitedString(writer, address.ScenePath);
            WriteLimitedString(writer, address.SceneName);
            writer.Write(address.SceneIndex);

            int[] siblingPath = address.SiblingPath ?? new int[0];
            if (siblingPath.Length > MaximumHierarchyDepth)
            {
                throw new InvalidDataException("Invalid scene hierarchy path.");
            }

            writer.Write((ushort)siblingPath.Length);
            foreach (int siblingIndex in siblingPath)
            {
                if (siblingIndex < 0)
                {
                    throw new InvalidDataException("Invalid scene hierarchy index.");
                }

                writer.Write(siblingIndex);
            }
        }

        private static UnitySyncSceneObjectAddress ReadSceneAddress(BinaryReader reader)
        {
            UnitySyncSceneObjectAddress address = new UnitySyncSceneObjectAddress
            {
                ObjectId = ReadLimitedString(reader),
                ParentObjectId = ReadLimitedString(reader),
                SiblingIndex = reader.ReadInt32(),
                ScenePath = ReadLimitedString(reader),
                SceneName = ReadLimitedString(reader),
                SceneIndex = reader.ReadInt32()
            };

            if (!Guid.TryParse(address.ObjectId, out _) ||
                (!string.IsNullOrEmpty(address.ParentObjectId) &&
                 !Guid.TryParse(address.ParentObjectId, out _)) ||
                address.SiblingIndex < -1)
            {
                throw new InvalidDataException("Invalid scene object address.");
            }

            int depth = reader.ReadUInt16();
            if (depth > MaximumHierarchyDepth)
            {
                throw new InvalidDataException("Invalid scene hierarchy path.");
            }

            address.SiblingPath = new int[depth];
            for (int index = 0; index < depth; index++)
            {
                int siblingIndex = reader.ReadInt32();
                if (siblingIndex < 0)
                {
                    throw new InvalidDataException("Invalid scene hierarchy index.");
                }

                address.SiblingPath[index] = siblingIndex;
            }

            return address;
        }

        private static void WriteSceneSnapshotBoundary(
            BinaryWriter writer,
            UnitySyncSceneSnapshotBoundary snapshot,
            bool includeScenes)
        {
            WriteGuid(writer, snapshot.SnapshotId);
            if (!includeScenes)
            {
                return;
            }

            UnitySyncSceneDescriptor[] scenes = snapshot.Scenes ?? new UnitySyncSceneDescriptor[0];
            if (scenes.Length > MaximumScenesPerSnapshot)
            {
                throw new InvalidDataException("Too many loaded scenes in a snapshot.");
            }

            writer.Write((ushort)scenes.Length);
            foreach (UnitySyncSceneDescriptor scene in scenes)
            {
                if (scene == null)
                {
                    throw new InvalidDataException("A snapshot contains an invalid scene.");
                }

                WriteLimitedString(writer, scene.ScenePath);
                WriteLimitedString(writer, scene.SceneName);
                writer.Write(scene.SceneIndex);
            }
        }

        private static UnitySyncSceneSnapshotBoundary ReadSceneSnapshotBoundary(BinaryReader reader)
        {
            UnitySyncSceneSnapshotBoundary snapshot = new UnitySyncSceneSnapshotBoundary
            {
                SnapshotId = ReadGuid(reader)
            };
            if (snapshot.SnapshotId == Guid.Empty)
            {
                throw new InvalidDataException("A scene snapshot ID is required.");
            }

            int count = reader.ReadUInt16();
            if (count > MaximumScenesPerSnapshot)
            {
                throw new InvalidDataException("Too many loaded scenes in a snapshot.");
            }

            snapshot.Scenes = new UnitySyncSceneDescriptor[count];
            for (int index = 0; index < count; index++)
            {
                snapshot.Scenes[index] = new UnitySyncSceneDescriptor
                {
                    ScenePath = ReadLimitedString(reader),
                    SceneName = ReadLimitedString(reader),
                    SceneIndex = reader.ReadInt32()
                };
            }

            return snapshot;
        }

        private static void WriteProperty(BinaryWriter writer, UnitySyncSerializedPropertyState property)
        {
            if (property == null)
            {
                throw new InvalidDataException("A component contains an invalid property.");
            }

            WriteLimitedString(writer, property.Path);
            writer.Write((byte)property.Kind);

            switch (property.Kind)
            {
                case UnitySyncSerializedValueKind.Integer:
                case UnitySyncSerializedValueKind.LayerMask:
                case UnitySyncSerializedValueKind.Enum:
                case UnitySyncSerializedValueKind.ArraySize:
                case UnitySyncSerializedValueKind.Character:
                    writer.Write(property.IntegerValue);
                    break;

                case UnitySyncSerializedValueKind.Boolean:
                    writer.Write(property.IntegerValue != 0);
                    break;

                case UnitySyncSerializedValueKind.Float:
                    writer.Write(property.NumberValue);
                    break;

                case UnitySyncSerializedValueKind.String:
                case UnitySyncSerializedValueKind.ManagedReference:
                case UnitySyncSerializedValueKind.Hash128:
                    WriteLimitedString(writer, property.StringValue);
                    break;

                case UnitySyncSerializedValueKind.Color:
                case UnitySyncSerializedValueKind.Vector2:
                case UnitySyncSerializedValueKind.Vector3:
                case UnitySyncSerializedValueKind.Vector4:
                case UnitySyncSerializedValueKind.Rect:
                case UnitySyncSerializedValueKind.Bounds:
                case UnitySyncSerializedValueKind.Quaternion:
                    WriteFloatArray(writer, property.FloatValues);
                    break;

                case UnitySyncSerializedValueKind.Vector2Int:
                case UnitySyncSerializedValueKind.Vector3Int:
                case UnitySyncSerializedValueKind.RectInt:
                case UnitySyncSerializedValueKind.BoundsInt:
                    WriteIntArray(writer, property.IntegerValues);
                    break;

                case UnitySyncSerializedValueKind.ObjectReference:
                case UnitySyncSerializedValueKind.ExposedReference:
                    WriteObjectReference(writer, property.ObjectReference);
                    break;

                case UnitySyncSerializedValueKind.AnimationCurve:
                    WriteAnimationCurve(writer, property.AnimationCurve);
                    break;

                case UnitySyncSerializedValueKind.Gradient:
                    WriteGradient(writer, property.Gradient);
                    break;

                default:
                    throw new InvalidDataException("Unsupported serialized property kind.");
            }
        }

        private static UnitySyncSerializedPropertyState ReadProperty(BinaryReader reader)
        {
            UnitySyncSerializedPropertyState property = new UnitySyncSerializedPropertyState
            {
                Path = ReadLimitedString(reader),
                Kind = (UnitySyncSerializedValueKind)reader.ReadByte()
            };

            switch (property.Kind)
            {
                case UnitySyncSerializedValueKind.Integer:
                case UnitySyncSerializedValueKind.LayerMask:
                case UnitySyncSerializedValueKind.Enum:
                case UnitySyncSerializedValueKind.ArraySize:
                case UnitySyncSerializedValueKind.Character:
                    property.IntegerValue = reader.ReadInt64();
                    break;

                case UnitySyncSerializedValueKind.Boolean:
                    property.IntegerValue = reader.ReadBoolean() ? 1 : 0;
                    break;

                case UnitySyncSerializedValueKind.Float:
                    property.NumberValue = reader.ReadDouble();
                    break;

                case UnitySyncSerializedValueKind.String:
                case UnitySyncSerializedValueKind.ManagedReference:
                case UnitySyncSerializedValueKind.Hash128:
                    property.StringValue = ReadLimitedString(reader);
                    break;

                case UnitySyncSerializedValueKind.Color:
                case UnitySyncSerializedValueKind.Vector2:
                case UnitySyncSerializedValueKind.Vector3:
                case UnitySyncSerializedValueKind.Vector4:
                case UnitySyncSerializedValueKind.Rect:
                case UnitySyncSerializedValueKind.Bounds:
                case UnitySyncSerializedValueKind.Quaternion:
                    property.FloatValues = ReadFloatArray(reader);
                    break;

                case UnitySyncSerializedValueKind.Vector2Int:
                case UnitySyncSerializedValueKind.Vector3Int:
                case UnitySyncSerializedValueKind.RectInt:
                case UnitySyncSerializedValueKind.BoundsInt:
                    property.IntegerValues = ReadIntArray(reader);
                    break;

                case UnitySyncSerializedValueKind.ObjectReference:
                case UnitySyncSerializedValueKind.ExposedReference:
                    property.ObjectReference = ReadObjectReference(reader);
                    break;

                case UnitySyncSerializedValueKind.AnimationCurve:
                    property.AnimationCurve = ReadAnimationCurve(reader);
                    break;

                case UnitySyncSerializedValueKind.Gradient:
                    property.Gradient = ReadGradient(reader);
                    break;

                default:
                    throw new InvalidDataException("Unsupported serialized property kind.");
            }

            return property;
        }

        private static void WriteObjectReference(BinaryWriter writer, UnitySyncObjectReferenceState reference)
        {
            reference = reference ?? new UnitySyncObjectReferenceState();
            writer.Write((byte)reference.Kind);
            if (string.IsNullOrEmpty(reference.SerializedPropertyTypeName))
            {
                throw new InvalidDataException("Object reference property type is missing.");
            }

            WriteLimitedString(writer, reference.SerializedPropertyTypeName);
            switch (reference.Kind)
            {
                case UnitySyncObjectReferenceKind.Null:
                    break;

                case UnitySyncObjectReferenceKind.Asset:
                    WriteLimitedString(writer, reference.ObjectTypeName);
                    WriteLimitedString(writer, reference.AssetGuid);
                    WriteLimitedString(writer, reference.AssetPath);
                    WriteLimitedString(writer, reference.AssetName);
                    WriteLimitedString(writer, reference.AssetContentHash);
                    writer.Write(reference.LocalFileId);
                    break;

                case UnitySyncObjectReferenceKind.SceneObject:
                    WriteLimitedString(writer, reference.ObjectTypeName);
                    WriteSceneAddress(writer, reference.SceneObject);
                    writer.Write(reference.ComponentIndex);
                    break;

                default:
                    throw new InvalidDataException("Unsupported object reference kind.");
            }
        }

        private static UnitySyncObjectReferenceState ReadObjectReference(BinaryReader reader)
        {
            UnitySyncObjectReferenceState reference = new UnitySyncObjectReferenceState
            {
                Kind = (UnitySyncObjectReferenceKind)reader.ReadByte(),
                SerializedPropertyTypeName = ReadLimitedString(reader)
            };
            if (string.IsNullOrEmpty(reference.SerializedPropertyTypeName))
            {
                throw new InvalidDataException("Object reference property type is missing.");
            }

            switch (reference.Kind)
            {
                case UnitySyncObjectReferenceKind.Null:
                    break;

                case UnitySyncObjectReferenceKind.Asset:
                    reference.ObjectTypeName = ReadLimitedString(reader);
                    reference.AssetGuid = ReadLimitedString(reader);
                    reference.AssetPath = ReadLimitedString(reader);
                    reference.AssetName = ReadLimitedString(reader);
                    reference.AssetContentHash = ReadLimitedString(reader);
                    reference.LocalFileId = reader.ReadInt64();
                    if (string.IsNullOrEmpty(reference.ObjectTypeName))
                    {
                        throw new InvalidDataException("Asset reference type is missing.");
                    }
                    break;

                case UnitySyncObjectReferenceKind.SceneObject:
                    reference.ObjectTypeName = ReadLimitedString(reader);
                    if (string.IsNullOrEmpty(reference.ObjectTypeName))
                    {
                        throw new InvalidDataException("Scene reference type is missing.");
                    }

                    reference.SceneObject = ReadSceneAddress(reader);
                    reference.ComponentIndex = reader.ReadInt32();
                    if (reference.ComponentIndex < -1 || reference.ComponentIndex >= MaximumComponentsPerObject)
                    {
                        throw new InvalidDataException("Invalid referenced component index.");
                    }
                    break;

                default:
                    throw new InvalidDataException("Unsupported object reference kind.");
            }

            return reference;
        }

        private static void WriteAnimationCurve(BinaryWriter writer, UnitySyncAnimationCurveState curve)
        {
            curve = curve ?? new UnitySyncAnimationCurveState();
            writer.Write((int)curve.PreWrapMode);
            writer.Write((int)curve.PostWrapMode);

            Keyframe[] keys = curve.Keys ?? new Keyframe[0];
            if (keys.Length > MaximumArrayElements)
            {
                throw new InvalidDataException("Animation curve has too many keys.");
            }

            writer.Write(keys.Length);
            foreach (Keyframe key in keys)
            {
                writer.Write(key.time);
                writer.Write(key.value);
                writer.Write(key.inTangent);
                writer.Write(key.outTangent);
                writer.Write(key.inWeight);
                writer.Write(key.outWeight);
                writer.Write((int)key.weightedMode);
            }
        }

        private static UnitySyncAnimationCurveState ReadAnimationCurve(BinaryReader reader)
        {
            UnitySyncAnimationCurveState curve = new UnitySyncAnimationCurveState
            {
                PreWrapMode = (WrapMode)reader.ReadInt32(),
                PostWrapMode = (WrapMode)reader.ReadInt32()
            };

            int keyCount = ReadArrayLength(reader, "animation curve");
            curve.Keys = new Keyframe[keyCount];
            for (int index = 0; index < keyCount; index++)
            {
                Keyframe key = new Keyframe(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle())
                {
                    weightedMode = (WeightedMode)reader.ReadInt32()
                };
                curve.Keys[index] = key;
            }

            return curve;
        }

        private static void WriteGradient(BinaryWriter writer, UnitySyncGradientState gradient)
        {
            gradient = gradient ?? new UnitySyncGradientState();
            writer.Write((int)gradient.Mode);

            GradientColorKey[] colorKeys = gradient.ColorKeys ?? new GradientColorKey[0];
            GradientAlphaKey[] alphaKeys = gradient.AlphaKeys ?? new GradientAlphaKey[0];
            if (colorKeys.Length > MaximumArrayElements || alphaKeys.Length > MaximumArrayElements)
            {
                throw new InvalidDataException("Gradient has too many keys.");
            }

            writer.Write(colorKeys.Length);
            foreach (GradientColorKey key in colorKeys)
            {
                WriteColorWithAlpha(writer, key.color);
                writer.Write(key.time);
            }

            writer.Write(alphaKeys.Length);
            foreach (GradientAlphaKey key in alphaKeys)
            {
                writer.Write(key.alpha);
                writer.Write(key.time);
            }
        }

        private static UnitySyncGradientState ReadGradient(BinaryReader reader)
        {
            UnitySyncGradientState gradient = new UnitySyncGradientState
            {
                Mode = (GradientMode)reader.ReadInt32()
            };

            int colorKeyCount = ReadArrayLength(reader, "gradient");
            gradient.ColorKeys = new GradientColorKey[colorKeyCount];
            for (int index = 0; index < colorKeyCount; index++)
            {
                gradient.ColorKeys[index] = new GradientColorKey(ReadColorWithAlpha(reader), reader.ReadSingle());
            }

            int alphaKeyCount = ReadArrayLength(reader, "gradient");
            gradient.AlphaKeys = new GradientAlphaKey[alphaKeyCount];
            for (int index = 0; index < alphaKeyCount; index++)
            {
                gradient.AlphaKeys[index] = new GradientAlphaKey(reader.ReadSingle(), reader.ReadSingle());
            }

            return gradient;
        }

        private static void WriteFloatArray(BinaryWriter writer, float[] values)
        {
            values = values ?? new float[0];
            if (values.Length > MaximumArrayElements)
            {
                throw new InvalidDataException("Serialized value is too large.");
            }

            writer.Write(values.Length);
            foreach (float value in values)
            {
                writer.Write(value);
            }
        }

        private static float[] ReadFloatArray(BinaryReader reader)
        {
            int count = ReadArrayLength(reader, "serialized value");
            float[] values = new float[count];
            for (int index = 0; index < count; index++)
            {
                values[index] = reader.ReadSingle();
            }

            return values;
        }

        private static void WriteIntArray(BinaryWriter writer, int[] values)
        {
            values = values ?? new int[0];
            if (values.Length > MaximumArrayElements)
            {
                throw new InvalidDataException("Serialized value is too large.");
            }

            writer.Write(values.Length);
            foreach (int value in values)
            {
                writer.Write(value);
            }
        }

        private static int[] ReadIntArray(BinaryReader reader)
        {
            int count = ReadArrayLength(reader, "serialized value");
            int[] values = new int[count];
            for (int index = 0; index < count; index++)
            {
                values[index] = reader.ReadInt32();
            }

            return values;
        }

        private static int ReadArrayLength(BinaryReader reader, string description)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumArrayElements)
            {
                throw new InvalidDataException("Invalid " + description + " length.");
            }

            return count;
        }

        private static void WriteLimitedString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > MaximumStringBytes)
            {
                throw new InvalidDataException("Serialized string is too long.");
            }

            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadLimitedString(BinaryReader reader)
        {
            int byteCount = reader.ReadInt32();
            if (byteCount < 0 || byteCount > MaximumStringBytes)
            {
                throw new InvalidDataException("Serialized string is too long.");
            }

            byte[] bytes = reader.ReadBytes(byteCount);
            if (bytes.Length != byteCount)
            {
                throw new EndOfStreamException();
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static void WriteColor(BinaryWriter writer, Color value)
        {
            writer.Write(value.r);
            writer.Write(value.g);
            writer.Write(value.b);
        }

        private static void WriteColorWithAlpha(BinaryWriter writer, Color value)
        {
            WriteColor(writer, value);
            writer.Write(value.a);
        }

        private static Color ReadColor(BinaryReader reader)
        {
            return new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 1f);
        }

        private static Color ReadColorWithAlpha(BinaryReader reader)
        {
            return new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }

        private static Quaternion ReadQuaternion(BinaryReader reader)
        {
            return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
