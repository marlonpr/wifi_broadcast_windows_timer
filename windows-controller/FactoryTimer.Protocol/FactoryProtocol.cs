using System.Globalization;
using System.Text;

namespace FactoryTimer.Protocol;

public enum CommandType
{
    Start,
    StartAt,
    Reset,
    StatusRequest,
    Brightness,
}

public enum TimerState
{
    Ready,
    Armed,
    Running,
    Finished,
}

public enum AckResult
{
    Accepted,
    Duplicate,
    NotSynced,
    Late,
}

public enum ProtocolParseError
{
    None,
    Empty,
    TooLong,
    NonPrintable,
    FieldCount,
    Version,
    PacketType,
    CommandType,
    CommandId,
    Duration,
    StartDelay,
    StartAt,
    DeviceId,
    State,
    Remaining,
    AckResult,
    SyncId,
    Timestamp,
    Offset,
    Rtt,
    Rssi,
    Channel,
    Bssid,
    Brightness,
}

public sealed record CommandPacket(
    CommandType CommandType,
    ulong CommandId,
    uint DurationSeconds,
    uint StartDelayMilliseconds,
    long StartAtMasterMicroseconds = 0,
    byte BrightnessPercent = 0);

public sealed record SyncRequestPacket(
    ulong SyncId,
    long MasterT1Microseconds,
    uint ArtificialReplyDelayMicroseconds = 0);

public sealed record SyncSetPacket(
    ulong SyncId,
    long MasterMinusLocalOffsetMicroseconds,
    long BestRttMicroseconds);

public abstract record InboundPacket(string DeviceId);

public sealed record AckPacket(
    string DeviceId,
    ulong CommandId,
    CommandType CommandType,
    AckResult Result) : InboundPacket(DeviceId);

public sealed record StatusPacket(
    string DeviceId,
    ulong CommandId,
    TimerState State,
    uint RemainingSeconds,
    int? RssiDbm = null,
    int? WifiChannel = null,
    string? Bssid = null) : InboundPacket(DeviceId);

public sealed record SyncReplyPacket(
    string DeviceId,
    ulong SyncId,
    long MasterT1Microseconds,
    long LocalT2Microseconds,
    long LocalT3Microseconds,
    uint ActualArtificialReplyDelayMicroseconds = 0) : InboundPacket(DeviceId);

public sealed record SyncAppliedPacket(
    string DeviceId,
    ulong SyncId,
    long MasterMinusLocalOffsetMicroseconds,
    long BestRttMicroseconds) : InboundPacket(DeviceId);
public sealed record StartedPacket(
    string DeviceId,
    ulong CommandId,
    long LocalStartMicroseconds,
    long EstimatedMasterStartMicroseconds,
    long TargetMasterStartMicroseconds) : InboundPacket(DeviceId);


public static class FactoryProtocol
{
    public const string Version1 = "FCT1";
    public const string Version2 = "FCT2";
    public const int MaximumPacketLength = 191;
    public const uint MinimumDurationSeconds = 1;
    public const uint MaximumDurationSeconds = 86_400;
    public const uint MinimumStartDelayMilliseconds = 100;
    public const uint MaximumStartDelayMilliseconds = 10_000;
    public const uint MaximumArtificialSyncReplyDelayMicroseconds = 1_500_000;
    public const byte MaximumBrightnessPercent = 100;

    public static string SerializeCommand(CommandPacket packet)
    {
        ValidateCommand(packet);
        return packet.CommandType switch
        {
            CommandType.Start => FormattableString.Invariant(
                $"{Version1}|CMD|START|{packet.CommandId:X16}|{packet.DurationSeconds}|{packet.StartDelayMilliseconds}"),
            CommandType.StartAt => FormattableString.Invariant(
                $"{Version2}|CMD|START_AT|{packet.CommandId:X16}|{packet.DurationSeconds}|{packet.StartAtMasterMicroseconds}"),
            CommandType.Reset => FormattableString.Invariant(
                $"{Version1}|CMD|RESET|{packet.CommandId:X16}|{packet.DurationSeconds}|0"),
            CommandType.StatusRequest => FormattableString.Invariant(
                $"{Version1}|CMD|STATUS_REQUEST|{packet.CommandId:X16}|0|0"),
            CommandType.Brightness => FormattableString.Invariant(
                $"{Version2}|CMD|BRIGHTNESS|{packet.CommandId:X16}|{packet.BrightnessPercent}|0"),
            _ => throw new ArgumentOutOfRangeException(nameof(packet)),
        };
    }

    public static byte[] SerializeCommandBytes(CommandPacket packet) =>
        Encoding.ASCII.GetBytes(SerializeCommand(packet));

    public static string SerializeSyncRequest(SyncRequestPacket packet)
    {
        if (packet.SyncId == 0) throw new ArgumentOutOfRangeException(nameof(packet));
        if (packet.MasterT1Microseconds < 0) throw new ArgumentOutOfRangeException(nameof(packet));
        if (packet.ArtificialReplyDelayMicroseconds > MaximumArtificialSyncReplyDelayMicroseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(packet));
        }

        // Preserve the original four-field packet when no reverse-path delay is
        // requested. The new firmware accepts both four- and five-field forms.
        return packet.ArtificialReplyDelayMicroseconds == 0
            ? FormattableString.Invariant(
                $"{Version2}|SYNC|{packet.SyncId:X16}|{packet.MasterT1Microseconds}")
            : FormattableString.Invariant(
                $"{Version2}|SYNC|{packet.SyncId:X16}|{packet.MasterT1Microseconds}|{packet.ArtificialReplyDelayMicroseconds}");
    }

    public static byte[] SerializeSyncRequestBytes(SyncRequestPacket packet) =>
        Encoding.ASCII.GetBytes(SerializeSyncRequest(packet));

    public static string SerializeSyncSet(SyncSetPacket packet)
    {
        if (packet.SyncId == 0) throw new ArgumentOutOfRangeException(nameof(packet));
        if (packet.BestRttMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(packet));
        return FormattableString.Invariant(
            $"{Version2}|SYNC_SET|{packet.SyncId:X16}|{packet.MasterMinusLocalOffsetMicroseconds}|{packet.BestRttMicroseconds}");
    }

    public static byte[] SerializeSyncSetBytes(SyncSetPacket packet) =>
        Encoding.ASCII.GetBytes(SerializeSyncSet(packet));

    public static bool TryParseCommand(
        string text,
        out CommandPacket? packet,
        out ProtocolParseError error)
    {
        packet = null;
        if (!ValidateEnvelope(text, out error)) return false;
        string[] fields = text.Split('|', StringSplitOptions.None);
        if (fields.Length != 6)
        {
            error = ProtocolParseError.FieldCount;
            return false;
        }
        if (fields[1] != "CMD")
        {
            error = ProtocolParseError.PacketType;
            return false;
        }
        if (!TryCommandType(fields[2], out CommandType commandType))
        {
            error = ProtocolParseError.CommandType;
            return false;
        }
        bool validVersion = commandType switch
        {
            CommandType.StartAt or CommandType.Brightness => fields[0] == Version2,
            _ => fields[0] == Version1,
        };
        if (!validVersion)
        {
            error = ProtocolParseError.Version;
            return false;
        }
        if (!TryCommandId(fields[3], allowZero: false, out ulong commandId))
        {
            error = ProtocolParseError.CommandId;
            return false;
        }

        if (commandType == CommandType.Brightness)
        {
            if (!TryUInt(fields[4], 0, MaximumBrightnessPercent, out uint brightness) ||
                !TryUInt(fields[5], 0, 0, out _))
            {
                error = ProtocolParseError.Brightness;
                return false;
            }
            packet = new CommandPacket(
                commandType, commandId, 0, 0, 0, checked((byte)brightness));
            return true;
        }

        uint minimumDuration = commandType == CommandType.StatusRequest ? 0 : MinimumDurationSeconds;
        uint maximumDuration = commandType == CommandType.StatusRequest ? 0 : MaximumDurationSeconds;
        if (!TryUInt(fields[4], minimumDuration, maximumDuration, out uint duration))
        {
            error = ProtocolParseError.Duration;
            return false;
        }

        if (commandType == CommandType.StartAt)
        {
            if (!long.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out long startAt) ||
                startAt <= 0)
            {
                error = ProtocolParseError.StartAt;
                return false;
            }
            packet = new CommandPacket(commandType, commandId, duration, 0, startAt);
            return true;
        }

        if (!TryUInt(fields[5], 0, MaximumStartDelayMilliseconds, out uint delay) ||
            (commandType == CommandType.Start && delay < MinimumStartDelayMilliseconds) ||
            ((commandType is CommandType.Reset or CommandType.StatusRequest) && delay != 0))
        {
            error = ProtocolParseError.StartDelay;
            return false;
        }

        packet = new CommandPacket(commandType, commandId, duration, delay);
        return true;
    }

    public static bool TryParseInbound(
        ReadOnlySpan<byte> bytes,
        out InboundPacket? packet,
        out ProtocolParseError error)
    {
        if (bytes.IsEmpty)
        {
            packet = null;
            error = ProtocolParseError.Empty;
            return false;
        }
        if (bytes.Length > MaximumPacketLength)
        {
            packet = null;
            error = ProtocolParseError.TooLong;
            return false;
        }
        foreach (byte value in bytes)
        {
            if (value is < 0x20 or > 0x7e)
            {
                packet = null;
                error = ProtocolParseError.NonPrintable;
                return false;
            }
        }
        return TryParseInbound(Encoding.ASCII.GetString(bytes), out packet, out error);
    }

    public static bool TryParseInbound(
        string text,
        out InboundPacket? packet,
        out ProtocolParseError error)
    {
        packet = null;
        if (!ValidateEnvelope(text, out error)) return false;
        string[] fields = text.Split('|', StringSplitOptions.None);

        if (fields.Length >= 2 && fields[0] == Version2 && fields[1] == "SYNC_REPLY")
        {
            // Seven fields are the original v2 reply. The optional eighth field
            // reports the reverse-path diagnostic delay actually achieved by
            // the device, measured with esp_timer_get_time().
            if (fields.Length is not (7 or 8))
            {
                error = ProtocolParseError.FieldCount;
                return false;
            }
            if (!ValidDeviceId(fields[2]))
            {
                error = ProtocolParseError.DeviceId;
                return false;
            }
            if (!TryCommandId(fields[3], allowZero: false, out ulong syncId))
            {
                error = ProtocolParseError.SyncId;
                return false;
            }
            if (!TryLong(fields[4], nonnegative: true, out long t1) ||
                !TryLong(fields[5], nonnegative: true, out long t2) ||
                !TryLong(fields[6], nonnegative: true, out long t3))
            {
                error = ProtocolParseError.Timestamp;
                return false;
            }

            uint actualReplyDelayUs = 0;
            if (fields.Length == 8 &&
                !uint.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out actualReplyDelayUs))
            {
                error = ProtocolParseError.Timestamp;
                return false;
            }

            packet = new SyncReplyPacket(fields[2], syncId, t1, t2, t3, actualReplyDelayUs);
            return true;
        }

        if (fields.Length >= 2 && fields[0] == Version2 && fields[1] == "STARTED")
        {
            if (fields.Length != 7)
            {
                error = ProtocolParseError.FieldCount;
                return false;
            }
            if (!ValidDeviceId(fields[2]))
            {
                error = ProtocolParseError.DeviceId;
                return false;
            }
            if (!TryCommandId(fields[3], allowZero: false, out ulong startedCommandId))
            {
                error = ProtocolParseError.CommandId;
                return false;
            }
            if (!TryLong(fields[4], nonnegative: true, out long localStart) ||
                !TryLong(fields[5], nonnegative: true, out long estimatedMasterStart) ||
                !TryLong(fields[6], nonnegative: true, out long targetMasterStart))
            {
                error = ProtocolParseError.Timestamp;
                return false;
            }
            packet = new StartedPacket(
                fields[2], startedCommandId, localStart, estimatedMasterStart, targetMasterStart);
            return true;
        }

        if (fields.Length >= 2 && fields[0] == Version2 && fields[1] == "SYNC_APPLIED")
        {
            if (fields.Length != 6)
            {
                error = ProtocolParseError.FieldCount;
                return false;
            }
            if (!ValidDeviceId(fields[2]))
            {
                error = ProtocolParseError.DeviceId;
                return false;
            }
            if (!TryCommandId(fields[3], allowZero: false, out ulong syncId))
            {
                error = ProtocolParseError.SyncId;
                return false;
            }
            if (!TryLong(fields[4], nonnegative: false, out long offset))
            {
                error = ProtocolParseError.Offset;
                return false;
            }
            if (!TryLong(fields[5], nonnegative: true, out long rtt))
            {
                error = ProtocolParseError.Rtt;
                return false;
            }
            packet = new SyncAppliedPacket(fields[2], syncId, offset, rtt);
            return true;
        }

        // Extended FCT2 STATUS adds Wi-Fi diagnostics while the legacy FCT1
        // six-field packet remains accepted for backward compatibility.
        if (fields.Length == 9 && fields[0] == Version2 && fields[1] == "STATUS")
        {
            if (!ValidDeviceId(fields[2]))
            {
                error = ProtocolParseError.DeviceId;
                return false;
            }
            if (!TryCommandId(fields[3], allowZero: true, out ulong commandId))
            {
                error = ProtocolParseError.CommandId;
                return false;
            }
            if (!TryState(fields[4], out TimerState state))
            {
                error = ProtocolParseError.State;
                return false;
            }
            if (!TryUInt(fields[5], 0, MaximumDurationSeconds, out uint remaining))
            {
                error = ProtocolParseError.Remaining;
                return false;
            }
            if (!int.TryParse(fields[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int rssi) ||
                rssi is < -127 or > 0)
            {
                error = ProtocolParseError.Rssi;
                return false;
            }
            if (!int.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out int channel) ||
                channel is < 0 or > 255)
            {
                error = ProtocolParseError.Channel;
                return false;
            }
            if (!ValidBssid(fields[8]))
            {
                error = ProtocolParseError.Bssid;
                return false;
            }
            packet = new StatusPacket(fields[2], commandId, state, remaining, rssi, channel, fields[8]);
            return true;
        }

        if (fields.Length != 6)
        {
            error = ProtocolParseError.FieldCount;
            return false;
        }
        if (fields[0] != Version1)
        {
            error = ProtocolParseError.Version;
            return false;
        }
        if (!ValidDeviceId(fields[2]))
        {
            error = ProtocolParseError.DeviceId;
            return false;
        }
        if (!TryCommandId(fields[3], allowZero: fields[1] == "STATUS", out ulong legacyCommandId))
        {
            error = ProtocolParseError.CommandId;
            return false;
        }

        if (fields[1] == "ACK")
        {
            if (!TryCommandType(fields[4], out CommandType commandType))
            {
                error = ProtocolParseError.CommandType;
                return false;
            }
            if (!TryAckResult(fields[5], out AckResult result))
            {
                error = ProtocolParseError.AckResult;
                return false;
            }
            packet = new AckPacket(fields[2], legacyCommandId, commandType, result);
            return true;
        }
        if (fields[1] == "STATUS")
        {
            if (!TryState(fields[4], out TimerState state))
            {
                error = ProtocolParseError.State;
                return false;
            }
            if (!TryUInt(fields[5], 0, MaximumDurationSeconds, out uint remaining))
            {
                error = ProtocolParseError.Remaining;
                return false;
            }
            packet = new StatusPacket(fields[2], legacyCommandId, state, remaining);
            return true;
        }

        error = ProtocolParseError.PacketType;
        return false;
    }

    public static string FormatCommandId(ulong commandId) =>
        commandId.ToString("X16", CultureInfo.InvariantCulture);

    private static void ValidateCommand(CommandPacket packet)
    {
        if (packet.CommandId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packet), "Command ID cannot be zero.");
        }
        if (packet.CommandType == CommandType.Brightness)
        {
            if (packet.DurationSeconds != 0 || packet.StartDelayMilliseconds != 0 ||
                packet.StartAtMasterMicroseconds != 0 ||
                packet.BrightnessPercent > MaximumBrightnessPercent)
            {
                throw new ArgumentOutOfRangeException(nameof(packet),
                    "BRIGHTNESS requires only a 0..100 percent value.");
            }
            return;
        }
        if (packet.CommandType == CommandType.StatusRequest)
        {
            if (packet.DurationSeconds != 0 || packet.StartDelayMilliseconds != 0 ||
                packet.StartAtMasterMicroseconds != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(packet),
                    "STATUS_REQUEST timing and duration fields must be zero.");
            }
            return;
        }
        if (packet.BrightnessPercent != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packet),
                "BrightnessPercent must be zero for non-BRIGHTNESS commands.");
        }
        if (packet.DurationSeconds is < MinimumDurationSeconds or > MaximumDurationSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(packet), "Duration is outside the protocol range.");
        }
        if (packet.CommandType == CommandType.Start)
        {
            if (packet.StartDelayMilliseconds is < MinimumStartDelayMilliseconds or > MaximumStartDelayMilliseconds ||
                packet.StartAtMasterMicroseconds != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(packet), "Legacy START timing is invalid.");
            }
            return;
        }
        if (packet.CommandType == CommandType.StartAt)
        {
            if (packet.StartDelayMilliseconds != 0 || packet.StartAtMasterMicroseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(packet), "START_AT requires a positive absolute timestamp.");
            }
            return;
        }
        if (packet.CommandType == CommandType.Reset &&
            (packet.StartDelayMilliseconds != 0 || packet.StartAtMasterMicroseconds != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(packet), "RESET timing fields must be zero.");
        }
    }

    private static bool ValidateEnvelope(string text, out ProtocolParseError error)
    {
        error = ProtocolParseError.None;
        if (string.IsNullOrEmpty(text))
        {
            error = ProtocolParseError.Empty;
            return false;
        }
        if (Encoding.UTF8.GetByteCount(text) > MaximumPacketLength)
        {
            error = ProtocolParseError.TooLong;
            return false;
        }
        if (text.Any(character => character is < (char)0x20 or > (char)0x7e))
        {
            error = ProtocolParseError.NonPrintable;
            return false;
        }
        if (!text.StartsWith(Version1 + "|", StringComparison.Ordinal) &&
            !text.StartsWith(Version2 + "|", StringComparison.Ordinal))
        {
            error = ProtocolParseError.Version;
            return false;
        }
        return true;
    }

    private static bool TryCommandId(string value, bool allowZero, out ulong commandId)
    {
        commandId = 0;
        return value.Length == 16 &&
            ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out commandId) &&
            (allowZero || commandId != 0);
    }

    private static bool TryUInt(string value, uint minimum, uint maximum, out uint result) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) &&
        result >= minimum && result <= maximum;

    private static bool TryLong(string value, bool nonnegative, out long result) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result) &&
        (!nonnegative || result >= 0);

    private static bool ValidDeviceId(string value) =>
        value is { Length: >= 1 and <= 16 } &&
        value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    private static bool ValidBssid(string value)
    {
        if (value.Length != 17) return false;
        for (int index = 0; index < value.Length; index++)
        {
            if (index is 2 or 5 or 8 or 11 or 14)
            {
                if (value[index] != ':') return false;
            }
            else if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryCommandType(string value, out CommandType type)
    {
        type = value switch
        {
            "START" => CommandType.Start,
            "START_AT" => CommandType.StartAt,
            "RESET" => CommandType.Reset,
            "STATUS_REQUEST" => CommandType.StatusRequest,
            "BRIGHTNESS" => CommandType.Brightness,
            _ => default,
        };
        return value is "START" or "START_AT" or "RESET" or "STATUS_REQUEST" or "BRIGHTNESS";
    }

    private static bool TryAckResult(string value, out AckResult result)
    {
        result = value switch
        {
            "ACCEPTED" => AckResult.Accepted,
            "DUPLICATE" => AckResult.Duplicate,
            "NOT_SYNCED" => AckResult.NotSynced,
            "LATE" => AckResult.Late,
            _ => default,
        };
        return value is "ACCEPTED" or "DUPLICATE" or "NOT_SYNCED" or "LATE";
    }

    private static bool TryState(string value, out TimerState state)
    {
        state = value switch
        {
            "READY" => TimerState.Ready,
            "ARMED" => TimerState.Armed,
            "RUNNING" => TimerState.Running,
            "FINISHED" => TimerState.Finished,
            _ => default,
        };
        return value is "READY" or "ARMED" or "RUNNING" or "FINISHED";
    }
}
