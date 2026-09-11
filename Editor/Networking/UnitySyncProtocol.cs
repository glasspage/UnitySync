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
        PeerLeft = 4
    }

    internal readonly struct UnitySyncViewportState
    {
        internal readonly Guid PlayerId;
        internal readonly string DisplayName;
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

        internal UnitySyncMessage(UnitySyncMessageType type, Guid playerId, string displayName, UnitySyncViewportState viewport)
        {
            Type = type;
            PlayerId = playerId;
            DisplayName = displayName;
            Viewport = viewport;
        }
    }

    internal static class UnitySyncProtocol
    {
        internal const int Version = 1;
        internal const int MaximumFrameSize = 64 * 1024;
        internal const int MaximumDisplayNameBytes = 128;

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
