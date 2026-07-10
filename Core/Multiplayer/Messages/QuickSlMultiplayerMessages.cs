using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using System.Text;

namespace QuickSL.Core;

internal interface IQuickSlNetworkMessage : INetMessage
{
}

internal enum QuickSlCancelReason : byte
{
    Rejected = 0,
    Timeout = 1,
    InvalidState = 2,
    Disabled = 3
}

internal struct QuickSlInitiateMessage : IQuickSlNetworkMessage
{
    public uint ClientRequestId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(ClientRequestId);
    }

    public void Deserialize(PacketReader reader)
    {
        ClientRequestId = reader.ReadUInt();
    }
}

internal struct QuickSlInitiatePendingMessage : IQuickSlNetworkMessage
{
    public uint ClientRequestId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(ClientRequestId);
    }

    public void Deserialize(PacketReader reader)
    {
        ClientRequestId = reader.ReadUInt();
    }
}

internal struct QuickSlInitiateResponseMessage : IQuickSlNetworkMessage
{
    public uint ClientRequestId;
    public uint HostRequestId;
    public bool Approved;
    public QuickSlCancelReason Reason;
    public bool WaitingForOtherPlayers;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(ClientRequestId);
        writer.WriteUInt(HostRequestId);
        writer.WriteBool(Approved);
        writer.WriteByte((byte)Reason);
        writer.WriteBool(WaitingForOtherPlayers);
    }

    public void Deserialize(PacketReader reader)
    {
        ClientRequestId = reader.ReadUInt();
        HostRequestId = reader.ReadUInt();
        Approved = reader.ReadBool();
        Reason = (QuickSlCancelReason)reader.ReadByte();
        WaitingForOtherPlayers = reader.ReadBool();
    }
}

internal struct QuickSlRequestMessage : IQuickSlNetworkMessage
{
    public uint RequestId;
    public bool RequiresClientConfirmation;
    public ulong InitiatorPlayerId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(RequestId);
        writer.WriteBool(RequiresClientConfirmation);
        writer.WriteULong(InitiatorPlayerId);
    }

    public void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadUInt();
        RequiresClientConfirmation = reader.ReadBool();
        InitiatorPlayerId = reader.ReadULong();
    }
}

internal struct QuickSlVoteMessage : IQuickSlNetworkMessage
{
    public uint RequestId;
    public bool Approved;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(RequestId);
        writer.WriteBool(Approved);
    }

    public void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadUInt();
        Approved = reader.ReadBool();
    }
}

internal struct QuickSlExecuteMessage : IQuickSlNetworkMessage
{
    private const int MaxParticipantCount = 32;
    public const int MaxRunSaveJsonBytes = 1024 * 1024;

    public uint RequestId;
    public ulong[] ConnectedPlayerIds;
    public string RunSaveJson;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(RequestId);
        WriteBoundedString(writer, RunSaveJson ?? string.Empty);
        ulong[] connectedPlayerIds = ConnectedPlayerIds ?? [];
        writer.WriteInt(connectedPlayerIds.Length);
        foreach (ulong playerId in connectedPlayerIds)
        {
            writer.WriteULong(playerId);
        }
    }

    public void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadUInt();
        RunSaveJson = ReadBoundedString(reader);
        int participantCount = reader.ReadInt();
        if (participantCount is < 0 or > MaxParticipantCount)
        {
            throw new InvalidDataException($"Invalid QuickSL participant count: {participantCount}");
        }

        ConnectedPlayerIds = new ulong[participantCount];
        for (int i = 0; i < ConnectedPlayerIds.Length; i++)
        {
            ConnectedPlayerIds[i] = reader.ReadULong();
        }
    }

    private static void WriteBoundedString(PacketWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxRunSaveJsonBytes)
        {
            throw new InvalidDataException(
                $"QuickSL run save payload is too large: {bytes.Length} bytes");
        }

        writer.WriteInt(bytes.Length);
        writer.WriteBytes(bytes, bytes.Length);
    }

    private static string ReadBoundedString(PacketReader reader)
    {
        int byteCount = reader.ReadInt();
        if (byteCount is < 0 or > MaxRunSaveJsonBytes)
        {
            throw new InvalidDataException(
                $"Invalid QuickSL run save payload size: {byteCount} bytes");
        }

        byte[] bytes = new byte[byteCount];
        reader.ReadBytes(bytes, byteCount);
        return Encoding.UTF8.GetString(bytes);
    }
}

internal struct QuickSlLoadReadyMessage : IQuickSlNetworkMessage
{
    public uint RequestId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(RequestId);
    }

    public void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadUInt();
    }
}

internal struct QuickSlLoadBeginMessage : IQuickSlNetworkMessage
{
    public uint RequestId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(RequestId);
    }

    public void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadUInt();
    }
}

internal struct QuickSlSetupReadyMessage : IQuickSlNetworkMessage
{
    public uint RequestId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(RequestId);
    }

    public void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadUInt();
    }
}

internal struct QuickSlRunBeginMessage : IQuickSlNetworkMessage
{
    public uint RequestId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(RequestId);
    }

    public void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadUInt();
    }
}

internal struct QuickSlCancelMessage : IQuickSlNetworkMessage
{
    public uint RequestId;
    public QuickSlCancelReason Reason;
    public ulong RelatedPlayerId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(RequestId);
        writer.WriteByte((byte)Reason);
        writer.WriteULong(RelatedPlayerId);
    }

    public void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadUInt();
        Reason = (QuickSlCancelReason)reader.ReadByte();
        RelatedPlayerId = reader.ReadULong();
    }
}
