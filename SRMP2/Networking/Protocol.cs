using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SRMP2.Networking;

internal static class Protocol
{
    internal const int Version = 1;
    internal const int DefaultPort = 6996;
    internal const int MaxPlayers = 8;
    internal const int MaxFrameBytes = 64 * 1024;
    internal const int MaxUsernameLength = 32;
    internal const int MaxChatLength = 300;
    internal const int MaxSceneLength = 160;

    // ASCII "SRM2" when read as little-endian uint.
    internal const uint UdpMagic = 0x324D5253;

    internal enum MessageKind : byte
    {
        Hello = 1,
        Welcome = 2,
        Reject = 3,
        PeerJoined = 4,
        PeerLeft = 5,
        Chat = 6,
        SceneChanged = 7,
        Disconnect = 8,
        Ping = 9,
        Pong = 10
    }

    internal enum UdpKind : byte
    {
        Hello = 1,
        PlayerSnapshot = 2
    }

    internal static string CleanUsername(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Rancher";

        var builder = new StringBuilder(Math.Min(value.Length, MaxUsernameLength));
        foreach (var c in value.Trim())
        {
            if (builder.Length >= MaxUsernameLength)
                break;

            if (!char.IsControl(c))
                builder.Append(c);
        }

        return builder.Length == 0 ? "Rancher" : builder.ToString();
    }

    internal static string CleanChat(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim();
        return value.Length <= MaxChatLength ? value : value[..MaxChatLength];
    }

    internal static string ReadBoundedString(BinaryReader reader, int maxLength)
    {
        var value = reader.ReadString();
        if (value.Length > maxLength)
            throw new InvalidDataException($"String exceeded {maxLength} characters.");
        return value;
    }

    internal static byte[] BuildTcpFrame(MessageKind kind, Action<BinaryWriter> writePayload = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)kind);
        writePayload?.Invoke(writer);
        writer.Flush();
        return stream.ToArray();
    }

    internal static byte[] BuildUdpHello(Guid sessionId, int senderId)
    {
        using var stream = new MemoryStream(32);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteUdpHeader(writer, UdpKind.Hello, sessionId, senderId, 0);
        writer.Flush();
        return stream.ToArray();
    }

    internal static byte[] BuildSnapshot(Guid sessionId, PlayerSnapshot snapshot)
    {
        using var stream = new MemoryStream(80);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteUdpHeader(writer, UdpKind.PlayerSnapshot, sessionId, snapshot.PlayerId, snapshot.Sequence);
        writer.Write(snapshot.Position.x);
        writer.Write(snapshot.Position.y);
        writer.Write(snapshot.Position.z);
        writer.Write(snapshot.Rotation.x);
        writer.Write(snapshot.Rotation.y);
        writer.Write(snapshot.Rotation.z);
        writer.Write(snapshot.Rotation.w);
        writer.Flush();
        return stream.ToArray();
    }

    internal static bool TryReadUdp(byte[] bytes, out UdpPacket packet)
    {
        packet = default;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (reader.ReadUInt32() != UdpMagic)
                return false;
            if (reader.ReadUInt16() != Version)
                return false;

            var kind = (UdpKind)reader.ReadByte();
            var sessionId = new Guid(reader.ReadBytes(16));
            var senderId = reader.ReadInt32();
            var sequence = reader.ReadUInt16();

            if (kind == UdpKind.Hello)
            {
                packet = new UdpPacket(kind, sessionId, senderId, sequence, default);
                return true;
            }

            if (kind != UdpKind.PlayerSnapshot || stream.Length - stream.Position < 28)
                return false;

            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var snapshot = new PlayerSnapshot(senderId, sequence, position, rotation);
            packet = new UdpPacket(kind, sessionId, senderId, sequence, snapshot);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteUdpHeader(BinaryWriter writer, UdpKind kind, Guid sessionId, int senderId, ushort sequence)
    {
        writer.Write(UdpMagic);
        writer.Write((ushort)Version);
        writer.Write((byte)kind);
        writer.Write(sessionId.ToByteArray());
        writer.Write(senderId);
        writer.Write(sequence);
    }
}

internal readonly struct UdpPacket
{
    internal UdpPacket(Protocol.UdpKind kind, Guid sessionId, int senderId, ushort sequence, PlayerSnapshot snapshot)
    {
        Kind = kind;
        SessionId = sessionId;
        SenderId = senderId;
        Sequence = sequence;
        Snapshot = snapshot;
    }

    internal Protocol.UdpKind Kind { get; }
    internal Guid SessionId { get; }
    internal int SenderId { get; }
    internal ushort Sequence { get; }
    internal PlayerSnapshot Snapshot { get; }
}

public readonly struct PlayerSnapshot
{
    public PlayerSnapshot(int playerId, ushort sequence, Vector3 position, Quaternion rotation)
    {
        PlayerId = playerId;
        Sequence = sequence;
        Position = position;
        Rotation = rotation;
    }

    public int PlayerId { get; }
    public ushort Sequence { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
}
