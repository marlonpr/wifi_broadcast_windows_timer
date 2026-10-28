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
    Busy,
    Invalid,
}

public enum RtcDisciplineState
{
    Unknown,
    Disabled,
    Uninitialized,
    Acquiring,
    Locked,
    Holdover,
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
    RtcDiscipline,
    RtcFitPoints,
    RtcFitRms,
    RtcQueueDrops,
    RtcTemperatureValid,
    RtcRate,
    RtcFitOutliers,
    RtcTemperature,
    RtcSqwCore,
    HealthFlags,
    SyncSourceOffset,
    SyncEpochLocal,
    SyncEpochDisciplined,
    SyncMasterMinusDisciplined,
    StartError,
    SchedulerLateness,
    StartPublishLateness,
    WorstPublishLateness,
    FrameNotReady,
    RtcAcceptedEdges,
    RtcInferredMissingEdges,
    RtcHoldoverEntries,
    Cpu0MonitorValid,
    Cpu0MonitorSamples,
    Cpu0MonitorEventCount,
    Cpu0MonitorWorst,
    Cpu0MonitorWorstTask,
    Cpu0CommitLateCount,
    Cpu0CommitWorst,
    Cpu0CommitOverlap,
    Cpu0WrongCoreCallbacks,
    Cpu0MonitorOverflow,
    Cpu0InterruptLevelMatch,
    Cpu0MonitorMissedPeriods,
    FirmwareBuildId,
    DiagnosticFlags,
    WifiDisconnectCount,
    WifiReconnectCount,
    WifiLastReconnectUptimeMs,
    Brightness,
}

public sealed record CommandPacket(
    CommandType CommandType,
    ulong CommandId,
    uint DurationSeconds,
    uint StartDelayMilliseconds,
    long StartAtMasterMicroseconds = 0,
    byte BrightnessPercent = 0,
    ulong PlanId = 0,
    uint SegmentIndex = 0,
    uint RepeatCount = 1,
    uint FirstRepeat = 1,
    uint TotalRepeats = 1, ulong ExpectedBootId = 0, ulong ExpectedPreviousCommandId = 0, uint LogoSeconds = 0)
{
    public uint SegmentDurationSeconds => FactoryProtocol.SegmentDuration(DurationSeconds, RepeatCount, LogoSeconds);
}

public sealed record SyncRequestPacket(
    ulong SyncId,
    long MasterT1Microseconds,
    uint ArtificialReplyDelayMicroseconds = 0);

public sealed record SyncSetPacket(
    ulong SyncId,
    long MasterMinusLocalOffsetMicroseconds,
    long BestRttMicroseconds,
    long OffsetEpochLocalMicroseconds = 0);

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
    string? Bssid = null,
    RtcDisciplineState RtcState = RtcDisciplineState.Unknown,
    ushort? RtcFitPoints = null,
    double? RtcFitRmsMicroseconds = null,
    uint? RtcQueueDrops = null,
    bool? RtcTemperatureValid = null,
    double? RtcRatePpmVsRtc = null,
    ulong? RtcFitOutliers = null,
    double? RtcTemperatureC = null,
    int? RtcSqwCore = null,
    byte? HealthFlags = null,
    long? SyncSourceOffsetMicroseconds = null,
    long? SyncEpochLocalMicroseconds = null,
    long? SyncEpochDisciplinedMicroseconds = null,
    long? SyncMasterMinusDisciplinedMicroseconds = null,
    long? StartErrorMicroseconds = null,
    long? SchedulerLatenessMicroseconds = null,
    long? StartPublishLatenessMicroseconds = null,
    long? WorstPublishLatenessMicroseconds = null,
    uint? FrameNotReadyCount = null,
    ulong? RtcAcceptedEdges = null,
    ulong? RtcInferredMissingEdges = null,
    ulong? RtcHoldoverEntries = null,
    bool? Cpu0MonitorValid = null,
    uint? Cpu0MonitorSamples = null,
    uint? Cpu0MonitorEventCount = null,
    uint? Cpu0MonitorWorstMicroseconds = null,
    string? Cpu0MonitorWorstTask = null,
    uint? Cpu0CommitLateCount = null,
    uint? Cpu0CommitWorstMicroseconds = null,
    bool? Cpu0CommitOverlap = null,
    uint? Cpu0WrongCoreCallbacks = null,
    uint? Cpu0MonitorOverflow = null,
    bool? Cpu0InterruptLevelMatch = null,
    uint? Cpu0MonitorMissedPeriods = null,
    string? FirmwareBuildId = null,
    int StatusFieldCount = 0,
    byte? DiagnosticFlags = null,
    uint? WifiDisconnectCount = null,
    uint? WifiReconnectCount = null,
    ulong? WifiLastReconnectUptimeMs = null) : InboundPacket(DeviceId);

// Separate post-run diagnostics keep monitor counters out of production STATUS.
public sealed record RunDiagnosticPacket(
    string DeviceId, ulong CommandId, bool Valid, uint PeriodMicroseconds,
    uint ThresholdMicroseconds, long MonitorStartMicroseconds, long TStarLocalMicroseconds,
    long MonitorEndMicroseconds, uint FirstAlarmOffsetMicroseconds, ulong ExpectedPeriods,
    uint SampleCallbacks, uint MissedPeriods, uint Cpu0EventsGe50, uint RtcEventsGe50,
    uint WifiEventsGe50, uint UdpEventsGe50, uint CommitLateEvents, uint CommitGe300,
    uint RearmFailures) : InboundPacket(DeviceId)
{
    public long MonitorElapsedMicroseconds => MonitorEndMicroseconds - MonitorStartMicroseconds;
    public long MonitorStartToTStarMicroseconds => MonitorStartMicroseconds - TStarLocalMicroseconds;
}

public sealed record SyncReplyPacket(
    string DeviceId,
    ulong SyncId,
    long MasterT1Microseconds,
    long LocalT2Microseconds,
    long LocalT3Microseconds,
    uint ActualArtificialReplyDelayMicroseconds = 0,
    long IngressLocalMicroseconds = 0, string? Bssid = null) : InboundPacket(DeviceId);

public sealed record ExecutionStatusPacket(
    string DeviceId, ulong ResponseId, ulong BootId, ulong PlanId, uint SegmentIndex,
    ulong CommandId, TimerState State, uint DurationSeconds, uint RepeatCount,
    uint FirstRepeat, uint TotalRepeats, uint CurrentRepeat, uint ElapsedSegmentSeconds,
    long TargetMasterMicroseconds, long TStarLocalMicroseconds, ushort RtcRecoveredEdges, long ElapsedSegmentMicroseconds = 0, uint? LogoSeconds = null)
    : InboundPacket(DeviceId)
{
    public uint SegmentDurationSeconds => FactoryProtocol.SegmentDuration(DurationSeconds, RepeatCount, LogoSeconds.GetValueOrDefault());
    public bool SupportsLogoOutput => LogoSeconds.HasValue;
}

public sealed record SyncAppliedPacket(
    string DeviceId,
    ulong SyncId,
    long MasterMinusLocalOffsetMicroseconds,
    long BestRttMicroseconds,
    long OffsetEpochLocalMicroseconds = 0) : InboundPacket(DeviceId);
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
    public const int MaximumPacketLength = 511;
    public const uint MinimumDurationSeconds = 1;
    public const uint MaximumRepeatDurationSeconds = 5999;
    public const uint MaximumSegmentDurationSeconds = 6000;
    public const uint MaximumDurationSeconds = 86_400;
    public const uint MinimumStartDelayMilliseconds = 100;
    public const uint MaximumStartDelayMilliseconds = 10_000;
    public const uint MaximumArtificialSyncReplyDelayMicroseconds = 1_500_000;
    public const byte MaximumBrightnessPercent = 100;

    public static string SerializeCommand(CommandPacket packet)
    {
        ValidateCommand(packet);
        if (packet.PlanId != 0)
        {
            string basis = FormattableString.Invariant(
                $"{Version2}|CMD|START_AT|{packet.CommandId:X16}|{packet.DurationSeconds}|{packet.StartAtMasterMicroseconds}|{packet.PlanId:X16}|{packet.SegmentIndex}|{packet.RepeatCount}|{packet.FirstRepeat}|{packet.TotalRepeats}|{packet.ExpectedBootId:X16}|{packet.ExpectedPreviousCommandId:X16}");
            return packet.LogoSeconds == 0 ? basis : basis + FormattableString.Invariant($"|{packet.LogoSeconds}");
        }
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
        if (packet.OffsetEpochLocalMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(packet));
        return packet.OffsetEpochLocalMicroseconds > 0
            ? FormattableString.Invariant(
                $"{Version2}|SYNC_SET|{packet.SyncId:X16}|{packet.MasterMinusLocalOffsetMicroseconds}|{packet.BestRttMicroseconds}|{packet.OffsetEpochLocalMicroseconds}")
            : FormattableString.Invariant(
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
        if (fields.Length is 13 or 14)
        {
            if (fields[0] != Version2 || fields[1] != "CMD" || fields[2] != "START_AT" ||
                !TryParseCommand(string.Join("|", fields.Take(6)), out CommandPacket? basis, out error) || basis is null)
                return false;
            uint logo = 0;
            if ((fields.Length == 14 && (!TryUInt(fields[13], RepetitionLogoSeconds, RepetitionLogoSeconds, out logo))) ||
                !TryCommandId(fields[6], false, out ulong planId) ||
                !TryUInt(fields[7], 1, uint.MaxValue, out uint segmentIndex) ||
                !TryUInt(fields[8], 1, uint.MaxValue, out uint repeats) ||
                !TryUInt(fields[9], 1, uint.MaxValue, out uint first) ||
                !TryUInt(fields[10], 1, uint.MaxValue, out uint total) ||
                !TryCommandId(fields[11], false, out ulong boot) ||
                !TryCommandId(fields[12], true, out ulong previous) ||
                !ValidSegment(basis.DurationSeconds, repeats, first, total, segmentIndex, logo))
            {
                error = ProtocolParseError.Duration;
                return false;
            }
            packet = basis with { PlanId = planId, SegmentIndex = segmentIndex,
                RepeatCount = repeats, FirstRepeat = first, TotalRepeats = total,
                ExpectedBootId = boot, ExpectedPreviousCommandId = previous, LogoSeconds = logo };
            return true;
        }
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

        if (fields.Length >= 2 && fields[0] == Version2 && fields[1] == "EXEC_STATUS")
            return TryParseExecutionStatus(fields, out packet, out error);

        if (fields.Length >= 2 && fields[0] == Version2 && fields[1] == "RUN_DIAG")
        {
            return TryParseRunDiagnostic(fields, out packet, out error);
        }

        if (fields.Length >= 2 && fields[0] == Version2 && fields[1] == "SYNC_REPLY")
        {
            // Legacy forms have 7 fields (no reverse-hold diagnostic) or 8 fields
            // (reverse-hold only). v6.22 appends the earliest matched device ingress
            // timestamp as field 9; an optional S3 die-temperature field may follow.
            if (fields.Length is not (7 or 8 or 9 or 10 or 11))
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
            if (fields.Length >= 8 &&
                !uint.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out actualReplyDelayUs))
            {
                error = ProtocolParseError.Timestamp;
                return false;
            }

            long ingressLocalUs = 0;
            if (fields.Length >= 9 &&
                !TryLong(fields[8], nonnegative: true, out ingressLocalUs))
            {
                error = ProtocolParseError.Timestamp;
                return false;
            }

            string? bssid = fields.Length == 11 ? fields[10] : null;
            if (bssid is not null && !ValidBssid(bssid))
            {
                error = ProtocolParseError.Bssid;
                return false;
            }
            packet = new SyncReplyPacket(
                fields[2], syncId, t1, t2, t3, actualReplyDelayUs, ingressLocalUs, bssid);
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
            if (fields.Length is not (6 or 7))
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
            long offsetEpochLocalUs = 0;
            if (fields.Length == 7 &&
                !TryLong(fields[6], nonnegative: true, out offsetEpochLocalUs))
            {
                error = ProtocolParseError.Timestamp;
                return false;
            }
            packet = new SyncAppliedPacket(
                fields[2], syncId, offset, rtt, offsetEpochLocalUs);
            return true;
        }

        // Extended FCT2 STATUS adds Wi-Fi diagnostics, RTC qualification, and
        // (v6.23.3+) compact fleet-health telemetry. Legacy 9/10/14-field
        // forms and the v6.23.3 28-field health form remain parseable;
        // v6.23.4 appends three RTC continuity counters (31 fields), and
        // v6.23.11 appends eleven compact CPU0-monitor fields (42 fields);
        // v6.23.15 appends missed-period count and ELF-SHA8 build identity (44 fields).
        // v6.23.19 appends a diagnostic-build byte (45 fields); queue drops use hex only in that form.
        if ((fields.Length == 9 || fields.Length == 10 || fields.Length == 14 ||
             fields.Length == 28 || fields.Length == 31 || fields.Length == 42 || fields.Length == 44 || fields.Length == 45 || fields.Length == 48) &&
            fields[0] == Version2 && fields[1] == "STATUS")
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

            RtcDisciplineState rtcState = RtcDisciplineState.Unknown;
            ushort? rtcFitPoints = null;
            double? rtcFitRmsUs = null;
            uint? rtcQueueDrops = null;
            bool? rtcTemperatureValid = null;
            double? rtcRatePpmVsRtc = null;
            ulong? rtcFitOutliers = null;
            double? rtcTemperatureC = null;
            int? rtcSqwCore = null;
            byte? healthFlags = null;
            long? syncSourceOffsetUs = null;
            long? syncEpochLocalUs = null;
            long? syncEpochDisciplinedUs = null;
            long? syncMasterMinusDisciplinedUs = null;
            long? startErrorUs = null;
            long? schedulerLatenessUs = null;
            long? startPublishLatenessUs = null;
            long? worstPublishLatenessUs = null;
            uint? frameNotReadyCount = null;
            ulong? rtcAcceptedEdges = null;
            ulong? rtcInferredMissingEdges = null;
            ulong? rtcHoldoverEntries = null;
            bool? cpu0MonitorValid = null;
            uint? cpu0MonitorSamples = null;
            uint? cpu0MonitorEventCount = null;
            uint? cpu0MonitorWorstUs = null;
            string? cpu0MonitorWorstTask = null;
            uint? cpu0CommitLateCount = null;
            uint? cpu0CommitWorstUs = null;
            bool? cpu0CommitOverlap = null;
            uint? cpu0WrongCoreCallbacks = null;
            uint? cpu0MonitorOverflow = null;
            bool? cpu0InterruptLevelMatch = null;
            uint? cpu0MonitorMissedPeriods = null;
            string? firmwareBuildId = null;
            byte? diagnosticFlags = null;

            if (fields.Length >= 10 && !TryRtcDisciplineState(fields[9], out rtcState))
            {
                error = ProtocolParseError.RtcDiscipline;
                return false;
            }
            if (fields.Length >= 14)
            {
                if (!ushort.TryParse(fields[10], NumberStyles.None, CultureInfo.InvariantCulture, out ushort points))
                {
                    error = ProtocolParseError.RtcFitPoints;
                    return false;
                }
                if (!double.TryParse(fields[11], NumberStyles.Float, CultureInfo.InvariantCulture, out double rmsUs) ||
                    !double.IsFinite(rmsUs) || rmsUs < 0d || rmsUs > 1_000_000d)
                {
                    error = ProtocolParseError.RtcFitRms;
                    return false;
                }
                if (!uint.TryParse(fields[12], (fields.Length == 45 || fields.Length == 48) ? NumberStyles.HexNumber : NumberStyles.None,
                    CultureInfo.InvariantCulture, out uint queueDrops))
                {
                    error = ProtocolParseError.RtcQueueDrops;
                    return false;
                }
                if (fields[13] != "0" && fields[13] != "1")
                {
                    error = ProtocolParseError.RtcTemperatureValid;
                    return false;
                }
                rtcFitPoints = points;
                rtcFitRmsUs = rmsUs;
                rtcQueueDrops = queueDrops;
                rtcTemperatureValid = fields[13] == "1";
            }

            if (fields.Length == 28 || fields.Length == 31 || fields.Length == 42 || fields.Length == 44 || fields.Length == 45 || fields.Length == 48)
            {
                if (!double.TryParse(fields[14], NumberStyles.Float, CultureInfo.InvariantCulture, out double ratePpm) ||
                    !double.IsFinite(ratePpm) || ratePpm is < -1_000_000d or > 1_000_000d)
                {
                    error = ProtocolParseError.RtcRate;
                    return false;
                }
                NumberStyles healthCounterStyle = fields.Length >= 44 ? NumberStyles.HexNumber : NumberStyles.None;
                if (!ulong.TryParse(fields[15], healthCounterStyle, CultureInfo.InvariantCulture, out ulong fitOutliers))
                {
                    error = ProtocolParseError.RtcFitOutliers;
                    return false;
                }
                if (!double.TryParse(fields[16], NumberStyles.Float, CultureInfo.InvariantCulture, out double temperatureC) ||
                    !double.IsFinite(temperatureC) || temperatureC is < -1000d or > 1000d)
                {
                    error = ProtocolParseError.RtcTemperature;
                    return false;
                }
                if (!int.TryParse(fields[17], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int sqwCore) ||
                    sqwCore is < -1 or > 1)
                {
                    error = ProtocolParseError.RtcSqwCore;
                    return false;
                }
                if (!byte.TryParse(fields[18], NumberStyles.None, CultureInfo.InvariantCulture, out byte flags) || flags > 7)
                {
                    error = ProtocolParseError.HealthFlags;
                    return false;
                }

                if (!TryLong(fields[19], nonnegative: false, out long sourceOffset))
                {
                    error = ProtocolParseError.SyncSourceOffset;
                    return false;
                }
                if (!TryLong(fields[20], nonnegative: true, out long epochLocal))
                {
                    error = ProtocolParseError.SyncEpochLocal;
                    return false;
                }
                if (!TryLong(fields[21], nonnegative: true, out long epochDisciplined))
                {
                    error = ProtocolParseError.SyncEpochDisciplined;
                    return false;
                }
                if (!TryLong(fields[22], nonnegative: false, out long masterMinusDisciplined))
                {
                    error = ProtocolParseError.SyncMasterMinusDisciplined;
                    return false;
                }
                if (!TryLong(fields[23], nonnegative: false, out long startError))
                {
                    error = ProtocolParseError.StartError;
                    return false;
                }
                if (!TryLong(fields[24], nonnegative: false, out long schedulerLateness))
                {
                    error = ProtocolParseError.SchedulerLateness;
                    return false;
                }
                if (!TryLong(fields[25], nonnegative: false, out long startPublishLateness))
                {
                    error = ProtocolParseError.StartPublishLateness;
                    return false;
                }
                if (!TryLong(fields[26], nonnegative: false, out long worstPublishLateness))
                {
                    error = ProtocolParseError.WorstPublishLateness;
                    return false;
                }
                if (!uint.TryParse(fields[27], NumberStyles.None, CultureInfo.InvariantCulture, out uint frameNotReady))
                {
                    error = ProtocolParseError.FrameNotReady;
                    return false;
                }

                if (fields.Length == 31 || fields.Length == 42 || fields.Length == 44 || fields.Length == 45 || fields.Length == 48)
                {
                    NumberStyles continuityCounterStyle = fields.Length >= 44 ? NumberStyles.HexNumber : NumberStyles.None;
                    if (!ulong.TryParse(fields[28], continuityCounterStyle, CultureInfo.InvariantCulture, out ulong acceptedEdges))
                    {
                        error = ProtocolParseError.RtcAcceptedEdges;
                        return false;
                    }
                    if (!ulong.TryParse(fields[29], continuityCounterStyle, CultureInfo.InvariantCulture, out ulong inferredMissingEdges))
                    {
                        error = ProtocolParseError.RtcInferredMissingEdges;
                        return false;
                    }
                    if (!ulong.TryParse(fields[30], continuityCounterStyle, CultureInfo.InvariantCulture, out ulong holdoverEntries))
                    {
                        error = ProtocolParseError.RtcHoldoverEntries;
                        return false;
                    }
                    rtcAcceptedEdges = acceptedEdges;
                    rtcInferredMissingEdges = inferredMissingEdges;
                    rtcHoldoverEntries = holdoverEntries;
                }

                if (fields.Length == 42 || fields.Length == 44 || fields.Length == 45 || fields.Length == 48)
                {
                    if (fields[31] != "0" && fields[31] != "1")
                    {
                        error = ProtocolParseError.Cpu0MonitorValid;
                        return false;
                    }
                    if (!uint.TryParse(fields[32], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint monitorSamples))
                    {
                        error = ProtocolParseError.Cpu0MonitorSamples;
                        return false;
                    }
                    if (!uint.TryParse(fields[33], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint monitorEvents))
                    {
                        error = ProtocolParseError.Cpu0MonitorEventCount;
                        return false;
                    }
                    if (!uint.TryParse(fields[34], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint monitorWorst))
                    {
                        error = ProtocolParseError.Cpu0MonitorWorst;
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(fields[35]) || fields[35].Length > 15)
                    {
                        error = ProtocolParseError.Cpu0MonitorWorstTask;
                        return false;
                    }
                    if (!uint.TryParse(fields[36], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint commitLateCount))
                    {
                        error = ProtocolParseError.Cpu0CommitLateCount;
                        return false;
                    }
                    if (!uint.TryParse(fields[37], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint commitWorst))
                    {
                        error = ProtocolParseError.Cpu0CommitWorst;
                        return false;
                    }
                    if (fields[38] != "0" && fields[38] != "1")
                    {
                        error = ProtocolParseError.Cpu0CommitOverlap;
                        return false;
                    }
                    if (!uint.TryParse(fields[39], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint wrongCore))
                    {
                        error = ProtocolParseError.Cpu0WrongCoreCallbacks;
                        return false;
                    }
                    if (!uint.TryParse(fields[40], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint monitorOverflow))
                    {
                        error = ProtocolParseError.Cpu0MonitorOverflow;
                        return false;
                    }
                    if (fields[41] != "0" && fields[41] != "1")
                    {
                        error = ProtocolParseError.Cpu0InterruptLevelMatch;
                        return false;
                    }

                    cpu0MonitorValid = fields[31] == "1";
                    cpu0MonitorSamples = monitorSamples;
                    cpu0MonitorEventCount = monitorEvents;
                    cpu0MonitorWorstUs = monitorWorst;
                    cpu0MonitorWorstTask = fields[35];
                    cpu0CommitLateCount = commitLateCount;
                    cpu0CommitWorstUs = commitWorst;
                    cpu0CommitOverlap = fields[38] == "1";
                    cpu0WrongCoreCallbacks = wrongCore;
                    cpu0MonitorOverflow = monitorOverflow;
                    cpu0InterruptLevelMatch = fields[41] == "1";

                    if (fields.Length == 44 || fields.Length == 45 || fields.Length == 48)
                    {
                        if (!uint.TryParse(fields[42], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint missedPeriods))
                        {
                            error = ProtocolParseError.Cpu0MonitorMissedPeriods;
                            return false;
                        }
                        string buildId = fields[43];
                        if (buildId.Length != 8 || !buildId.All(Uri.IsHexDigit))
                        {
                            error = ProtocolParseError.FirmwareBuildId;
                            return false;
                        }
                        cpu0MonitorMissedPeriods = missedPeriods;
                        firmwareBuildId = buildId.ToLowerInvariant();
                    }
                }

                rtcRatePpmVsRtc = ratePpm;
                rtcFitOutliers = fitOutliers;
                rtcTemperatureC = temperatureC;
                rtcSqwCore = sqwCore;
                healthFlags = flags;
                if ((flags & 0x01) != 0)
                {
                    syncSourceOffsetUs = sourceOffset;
                    syncEpochLocalUs = epochLocal;
                    syncEpochDisciplinedUs = epochDisciplined;
                    syncMasterMinusDisciplinedUs = masterMinusDisciplined;
                }
                if ((flags & 0x02) != 0)
                {
                    startErrorUs = startError;
                    schedulerLatenessUs = schedulerLateness;
                }
                if ((flags & 0x04) != 0)
                {
                    startPublishLatenessUs = startPublishLateness;
                    worstPublishLatenessUs = worstPublishLateness;
                    frameNotReadyCount = frameNotReady;
                }
            }

            if (fields.Length == 45 || fields.Length == 48)
            {
                string buildFlags = fields[44];
                if (buildFlags.Length != 2 || !buildFlags.All(Uri.IsHexDigit) ||
                    !byte.TryParse(buildFlags, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsedFlags))
                {
                    error = ProtocolParseError.DiagnosticFlags;
                    return false;
                }
                diagnosticFlags = parsedFlags;
            }

            uint? wifiDisconnectCount = null;
            uint? wifiReconnectCount = null;
            ulong? wifiLastReconnectUptimeMs = null;
            if (fields.Length == 48)
            {
                if (!uint.TryParse(fields[45], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint dc))
                {
                    error = ProtocolParseError.WifiDisconnectCount;
                    return false;
                }
                if (!uint.TryParse(fields[46], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rc))
                {
                    error = ProtocolParseError.WifiReconnectCount;
                    return false;
                }
                if (!ulong.TryParse(fields[47], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong last))
                {
                    error = ProtocolParseError.WifiLastReconnectUptimeMs;
                    return false;
                }
                wifiDisconnectCount = dc;
                wifiReconnectCount = rc;
                wifiLastReconnectUptimeMs = last;
            }
            packet = new StatusPacket(
                fields[2], commandId, state, remaining, rssi, channel, fields[8], rtcState,
                rtcFitPoints, rtcFitRmsUs, rtcQueueDrops, rtcTemperatureValid,
                rtcRatePpmVsRtc, rtcFitOutliers, rtcTemperatureC, rtcSqwCore, healthFlags,
                syncSourceOffsetUs, syncEpochLocalUs, syncEpochDisciplinedUs,
                syncMasterMinusDisciplinedUs, startErrorUs, schedulerLatenessUs,
                startPublishLatenessUs, worstPublishLatenessUs, frameNotReadyCount,
                rtcAcceptedEdges, rtcInferredMissingEdges, rtcHoldoverEntries,
                cpu0MonitorValid, cpu0MonitorSamples, cpu0MonitorEventCount,
                cpu0MonitorWorstUs, cpu0MonitorWorstTask, cpu0CommitLateCount,
                cpu0CommitWorstUs, cpu0CommitOverlap, cpu0WrongCoreCallbacks,
                cpu0MonitorOverflow, cpu0InterruptLevelMatch, cpu0MonitorMissedPeriods,
                firmwareBuildId, fields.Length, diagnosticFlags,
                wifiDisconnectCount, wifiReconnectCount, wifiLastReconnectUptimeMs);
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
            packet = new StatusPacket(fields[2], legacyCommandId, state, remaining, StatusFieldCount: fields.Length);
            return true;
        }

        error = ProtocolParseError.PacketType;
        return false;
    }

    public static string FormatCommandId(ulong commandId) =>
        commandId.ToString("X16", CultureInfo.InvariantCulture);

    private static void ValidateCommand(CommandPacket packet)
    {
        if (packet.PlanId != 0 && (packet.CommandType != CommandType.StartAt || packet.ExpectedBootId == 0 ||
            !ValidSegment(packet.DurationSeconds, packet.RepeatCount, packet.FirstRepeat,
                          packet.TotalRepeats, packet.SegmentIndex, packet.LogoSeconds)))
            throw new ArgumentOutOfRangeException(nameof(packet), "Invalid segment geometry.");
        if (packet.PlanId == 0 && (packet.RepeatCount != 1 || packet.FirstRepeat != 1 ||
            packet.TotalRepeats != 1 || packet.SegmentIndex != 0 || packet.ExpectedBootId != 0 || packet.ExpectedPreviousCommandId != 0 || packet.LogoSeconds != 0))
            throw new ArgumentOutOfRangeException(nameof(packet), "Segment fields require PlanId.");

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

    public const uint RepetitionLogoSeconds = 4;
    public static ulong SegmentSeconds(uint duration, uint repeats, uint logo = 0) => ((ulong)duration + logo) * repeats;
    public static uint SegmentDuration(uint duration, uint repeats, uint logo = 0) => checked((uint)SegmentSeconds(duration, repeats, logo));
    public static uint RepeatOffset(uint duration, uint repeats, ulong elapsed, uint logo = 0) =>
        duration == 0 || repeats == 0 ? 0 : (uint)Math.Min(elapsed / ((ulong)duration + logo), repeats - 1UL);
    public static uint RepeatRemaining(uint duration, uint repeats, ulong elapsed, uint logo = 0)
    {
        if (duration == 0 || elapsed >= SegmentSeconds(duration, repeats, logo)) return 0;
        uint phase = (uint)(elapsed % ((ulong)duration + logo));
        return phase < duration ? duration - phase : 0;
    }
    public static bool ValidSegment(uint duration, uint repeats, uint first, uint total, uint segment, uint logo = 0) =>
        duration is >= 1 and <= MaximumRepeatDurationSeconds && repeats >= 1 && first >= 1 &&
        (logo == 0 || logo == RepetitionLogoSeconds) && total >= 1 && segment >= 1 &&
        SegmentSeconds(duration, repeats, logo) - logo <= MaximumSegmentDurationSeconds && (ulong)first + repeats - 1 <= total;

    private static bool TryParseExecutionStatus(string[] f, out InboundPacket? packet,
                                               out ProtocolParseError error)
    {
        packet = null;
        error = ProtocolParseError.FieldCount;
        if (f.Length is not (19 or 20)) return false;
        error = ProtocolParseError.PacketType;
        if (!ValidDeviceId(f[2]) || !TryCommandId(f[3], true, out ulong response) ||
            !TryCommandId(f[4], false, out ulong boot) || !TryCommandId(f[5], true, out ulong plan) ||
            !TryUInt(f[6], 0, uint.MaxValue, out uint segment) ||
            !TryCommandId(f[7], true, out ulong command) || !TryState(f[8], out TimerState state) ||
            !TryUInt(f[9], 1, MaximumDurationSeconds, out uint duration) ||
            !TryUInt(f[10], 1, uint.MaxValue, out uint repeats) ||
            !TryUInt(f[11], 1, uint.MaxValue, out uint first) ||
            !TryUInt(f[12], 1, uint.MaxValue, out uint total) ||
            !TryUInt(f[13], 1, uint.MaxValue, out uint current) ||
            !TryUInt(f[14], 0, MaximumDurationSeconds, out uint elapsed) ||
            !TryLong(f[15], true, out long target) || !TryLong(f[16], true, out long local) ||
            !TryUInt(f[17], 0, 129, out uint recovery) ||
            !TryLong(f[18], false, out long phase)) return false;
        uint logo = 0;
        if (f.Length == 20 && (!TryUInt(f[19], 0, RepetitionLogoSeconds, out logo) || (logo != 0 && logo != RepetitionLogoSeconds))) return false;
        if (plan != 0 && (!ValidSegment(duration, repeats, first, total, segment, logo) ||
            command == 0 || target == 0 || local == 0)) return false;
        if (plan == 0 && (segment != 0 || repeats != 1 || first != 1 || total != 1 || logo != 0)) return false;
        ulong length = SegmentSeconds(duration, repeats, logo);
        if ((state == TimerState.Ready && elapsed != 0) ||
            (state == TimerState.Armed && phase > 0) ||
            (state != TimerState.Armed && (phase < 0 || phase / 1_000_000 != elapsed)) ||
            phase > (long)length * 1_000_000 ||
            elapsed > length || current != first + RepeatOffset(duration, repeats, elapsed, logo) ||
            (state == TimerState.Armed && elapsed != 0) ||
            (state == TimerState.Running && elapsed >= length) ||
            (state == TimerState.Finished && elapsed != length)) return false;
        packet = new ExecutionStatusPacket(f[2], response, boot, plan, segment, command, state,
            duration, repeats, first, total, current, elapsed, target, local, (ushort)recovery, phase, f.Length == 20 ? logo : null);
        error = ProtocolParseError.None;
        return true;
    }

    private static bool TryParseRunDiagnostic(
        string[] f, out InboundPacket? packet, out ProtocolParseError error)
    {
        packet = null;
        error = ProtocolParseError.FieldCount;
        if (f.Length != 21) return false;
        error = ProtocolParseError.DeviceId;
        if (!ValidDeviceId(f[2])) return false;
        error = ProtocolParseError.CommandId;
        if (!TryCommandId(f[3], allowZero: false, out ulong commandId)) return false;
        error = ProtocolParseError.Cpu0MonitorValid;
        if (f[4] is not ("0" or "1")) return false;
        error = ProtocolParseError.Timestamp;
        if (!TryUInt(f[5], 100, 10000, out uint period) ||
            !TryUInt(f[6], 1, 10000, out uint threshold) ||
            !TryLong(f[7], nonnegative: true, out long start) ||
            !TryLong(f[8], nonnegative: true, out long tstar) ||
            !TryLong(f[9], nonnegative: true, out long end) || end < start ||
            !TryUInt(f[10], 1, period, out uint offset) ||
            !ulong.TryParse(f[11], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong expected)) return false;
        long elapsed = end - start;
        ulong gridPeriods = elapsed < offset ? 0UL : 1UL + (ulong)((elapsed - offset) / period);
        if (f[4] == "1" && expected != gridPeriods) return false;
        uint[] counters = new uint[9];
        error = ProtocolParseError.Cpu0MonitorEventCount;
        for (int i = 0; i < counters.Length; i++)
        {
            if (!uint.TryParse(f[12 + i], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out counters[i])) return false;
        }
        // Per-task and >=300 us counts must be subsets of their full counters.
        if ((ulong)counters[3] + counters[4] + counters[5] > counters[2] ||
            counters[7] > counters[6]) return false;
        packet = new RunDiagnosticPacket(f[2], commandId, f[4] == "1", period, threshold,
            start, tstar, end, offset, expected, counters[0], counters[1], counters[2],
            counters[3], counters[4], counters[5], counters[6], counters[7], counters[8]);
        error = ProtocolParseError.None;
        return true;
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

    private static bool TryRtcDisciplineState(string value, out RtcDisciplineState state)
    {
        state = value switch
        {
            "DISABLED" => RtcDisciplineState.Disabled,
            "UNINITIALIZED" => RtcDisciplineState.Uninitialized,
            "ACQUIRING" => RtcDisciplineState.Acquiring,
            "LOCKED" => RtcDisciplineState.Locked,
            "HOLDOVER" => RtcDisciplineState.Holdover,
            _ => RtcDisciplineState.Unknown,
        };
        return state != RtcDisciplineState.Unknown;
    }

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
            "BUSY" => AckResult.Busy,
            "INVALID" => AckResult.Invalid,
            _ => default,
        };
        return value is "ACCEPTED" or "DUPLICATE" or "NOT_SYNCED" or "LATE" or "BUSY" or "INVALID";
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
