using System.Globalization;
using System.Text;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core;

public enum RtcWarmupSampleKind { Status, DiscoveryPoll, PostSync }

public sealed record RtcWarmupSample(
    long MasterMicroseconds, long SessionGeneration, StatusPacket Status,
    RtcWarmupSampleKind Kind = RtcWarmupSampleKind.Status);

public sealed record RtcWarmupReadiness(
    bool Ready, double HistorySeconds, double? RateChangePpm, int GoodFitPolls,
    RtcWarmupSample? Latest, IReadOnlyList<string> Reasons)
{
    public string Display(string deviceId)
    {
        string delta = RateChangePpm?.ToString("F3", CultureInfo.InvariantCulture) ?? "--";
        string rms = Latest?.Status.RtcFitRmsMicroseconds?.ToString("F2", CultureInfo.InvariantCulture) ?? "--";
        string temperature = Latest?.Status.RtcTemperatureC?.ToString("F2", CultureInfo.InvariantCulture) ?? "--";
        return FormattableString.Invariant(
            $"{deviceId}  {(Ready ? "READY" : "WAIT")}  history={HistorySeconds / 60:F1} min  rateΔ={delta} ppm  RMS={rms} us  good={GoodFitPolls}/3  temp={temperature} C  fit={Latest?.Status.RtcFitPoints}  accepted={Latest?.Status.RtcAcceptedEdges}") +
            (Reasons.Count == 0 ? "" : "  " + string.Join(",", Reasons));
    }
}

// Called on the UI dispatcher. Discovery and explicit post-SYNC polls enter the
// history. Only discovery polls can advance the consecutive-good-fit streak.
public sealed class RtcWarmupHistory
{
    public const long WindowMicroseconds = 600_000_000;
    public const long ResponseWindowMicroseconds = 2_000_000;
    public const long MaximumStatusAgeMicroseconds = 24_000_000;
    public const long MaximumGapMicroseconds = 48_000_000;
    public const double MaximumRateChangePpm = 0.25;
    public const ushort RequiredFitPoints = 129;
    public const double MaximumFitRmsMicroseconds = 1.0;
    public const int RequiredGoodFitPolls = 3;
    private readonly List<RtcWarmupSample> samples = [];
    private long pollSequence = -1;
    private long pollStartMicroseconds;
    private bool pollConsumed = true;
    private long postSyncPollStartMicroseconds;
    private bool postSyncPollConsumed = true;
    private int goodFitPolls;
    private RtcWarmupSample? latest;

    public RtcWarmupSample? Latest => latest;
    public IReadOnlyList<RtcWarmupSample> Samples => samples.ToArray();

    public void Reset()
    {
        samples.Clear();
        latest = null;
        goodFitPolls = 0;
        pollConsumed = true;
        postSyncPollConsumed = true;
    }

    public void BeginPoll(long sequence, long masterMicroseconds)
    {
        if (sequence <= pollSequence) return;
        if (!pollConsumed) goodFitPolls = 0;
        pollSequence = sequence;
        pollStartMicroseconds = masterMicroseconds;
        pollConsumed = false;
    }

    public void BeginPostSyncPoll(long masterMicroseconds)
    {
        // Keep this marker independent of discovery's sequence/window: a
        // restarted discovery poll and this foreground request may overlap.
        postSyncPollStartMicroseconds = masterMicroseconds;
        postSyncPollConsumed = false;
    }

    public void Observe(StatusPacket status, long sessionGeneration, long masterMicroseconds)
    {
        if (latest is not null && masterMicroseconds < latest.MasterMicroseconds) return;
        bool reset = latest is not null && (
            sessionGeneration != latest.SessionGeneration ||
            status.FirmwareBuildId != latest.Status.FirmwareBuildId ||
            masterMicroseconds - latest.MasterMicroseconds >= MaximumGapMicroseconds ||
            status.RtcAcceptedEdges < latest.Status.RtcAcceptedEdges ||
            status.RtcInferredMissingEdges != latest.Status.RtcInferredMissingEdges ||
            status.RtcHoldoverEntries != latest.Status.RtcHoldoverEntries);
        bool healthy = status.RtcState == RtcDisciplineState.Locked && status.DiagnosticFlags == 0 &&
            status.RtcFitPoints == RequiredFitPoints &&
            Finite(status.RtcRatePpmVsRtc) && Finite(status.RtcFitRmsMicroseconds);
        if (reset || !healthy) { samples.Clear(); goodFitPolls = 0; }
        bool discoveryReply = !pollConsumed &&
            InResponseWindow(masterMicroseconds, pollStartMicroseconds);
        bool postSyncReply = !postSyncPollConsumed &&
            InResponseWindow(masterMicroseconds, postSyncPollStartMicroseconds);
        var sample = new RtcWarmupSample(masterMicroseconds, sessionGeneration, status,
            discoveryReply ? RtcWarmupSampleKind.DiscoveryPoll :
            postSyncReply ? RtcWarmupSampleKind.PostSync : RtcWarmupSampleKind.Status);
        latest = sample;
        bool goodFit = healthy && status.RtcFitRmsMicroseconds is >= 0 and <= MaximumFitRmsMicroseconds;
        if (!goodFit) goodFitPolls = 0; // A bad latest STATUS blocks immediately.
        if (discoveryReply) pollConsumed = true;
        if (postSyncReply) postSyncPollConsumed = true;
        if (!discoveryReply && !postSyncReply) return;
        if (!healthy) return;
        samples.Add(sample);
        if (discoveryReply)
            goodFitPolls = goodFit ? Math.Min(RequiredGoodFitPolls, goodFitPolls + 1) : 0;
        // Keep twenty minutes, including the last sample before the cutoff.
        while (samples.Count > 1 && samples[1].MasterMicroseconds < masterMicroseconds - 2 * WindowMicroseconds)
            samples.RemoveAt(0);
    }

    public RtcWarmupReadiness Evaluate(long masterNowMicroseconds)
    {
        var reasons = new List<string>();
        RtcWarmupSample? current = latest;
        if (current is null) return new(false, 0, null, 0, null, ["NO_STATUS"]);
        StatusPacket status = current.Status;
        if (masterNowMicroseconds < current.MasterMicroseconds ||
            masterNowMicroseconds - current.MasterMicroseconds >= MaximumStatusAgeMicroseconds) reasons.Add("STATUS_STALE");
        if (status.RtcState != RtcDisciplineState.Locked) reasons.Add("NOT_LOCKED");
        if (!status.DiagnosticFlags.HasValue) reasons.Add("DIAGNOSTIC_FLAGS_UNAVAILABLE");
        else if (status.DiagnosticFlags != 0) reasons.Add("DIAGNOSTIC_FIRMWARE");
        if (status.RtcFitPoints != RequiredFitPoints) reasons.Add("FIT_NOT_FULL");
        if (status.RtcQueueDrops != 0) reasons.Add("RTC_QUEUE_DROPS");
        if (status.RtcTemperatureValid != true) reasons.Add("TEMPERATURE_UNAVAILABLE");
        if (!Finite(status.RtcRatePpmVsRtc)) reasons.Add("RATE_UNAVAILABLE");
        if (string.IsNullOrWhiteSpace(status.FirmwareBuildId) || !status.RtcAcceptedEdges.HasValue ||
            !status.RtcInferredMissingEdges.HasValue || !status.RtcHoldoverEntries.HasValue) reasons.Add("METRICS_UNAVAILABLE");
        if (!Finite(status.RtcFitRmsMicroseconds) || status.RtcFitRmsMicroseconds is < 0 or > MaximumFitRmsMicroseconds)
            reasons.Add("FIT_UNSTABLE");
        if (goodFitPolls < RequiredGoodFitPolls) reasons.Add("FIT_POLLS_PENDING");
        double span = samples.Count == 0 ? 0 : Math.Max(0, (current.MasterMicroseconds - samples[0].MasterMicroseconds) / 1_000_000d);
        RtcWarmupSample? anchor = samples.LastOrDefault(sample =>
            sample.MasterMicroseconds <= current.MasterMicroseconds - WindowMicroseconds);
        double? delta = anchor is not null && Finite(status.RtcRatePpmVsRtc)
            ? status.RtcRatePpmVsRtc - anchor.Status.RtcRatePpmVsRtc : null;
        if (anchor is null || samples.Count == 0 ||
            current.MasterMicroseconds - samples[^1].MasterMicroseconds >= MaximumStatusAgeMicroseconds)
            reasons.Add("HISTORY_PENDING");
        if (delta.HasValue && Math.Abs(delta.Value) > MaximumRateChangePpm + 1e-12) reasons.Add("RATE_UNSTABLE");
        return new(reasons.Count == 0, span, delta, goodFitPolls, current, reasons);
    }

    private static bool Finite(double? value) => value.HasValue && double.IsFinite(value.Value);
    private static bool InResponseWindow(long received, long started) =>
        received >= started && received - started <= ResponseWindowMicroseconds;
}

public sealed record QualificationWarmupDeviceProof(
    string DeviceId, RtcWarmupReadiness Readiness, IReadOnlyList<RtcWarmupSample> Samples);

public sealed record QualificationStartRateChange(string DeviceId, double? Start1RatePpm, double? CurrentRatePpm)
{
    public double? ChangePpm => Start1RatePpm.HasValue && CurrentRatePpm.HasValue &&
        double.IsFinite(Start1RatePpm.Value) && double.IsFinite(CurrentRatePpm.Value)
        ? CurrentRatePpm.Value - Start1RatePpm.Value : null;

    public string Display()
    {
        double? delta = ChangePpm;
        if (!delta.HasValue) return $"{DeviceId}  Δrate since START #1=UNAVAILABLE";
        return FormattableString.Invariant($"{DeviceId}  START1={Start1RatePpm:F6}  current={CurrentRatePpm:F6}  Δrate={delta.Value:+0.000000;-0.000000;0.000000} ppm") +
            "  (informational only)";
    }
}

// The proof survives the intentional no-poll countdown. START #2 checks live
// health/session/counters against this frozen START #1 evidence, without polling
// during the long train or demanding another ten-minute warm-up afterward.
public sealed record QualificationWarmupProof(
    ulong FirstCommandId, long FirstTargetMasterMicroseconds, uint FirstDurationSeconds,
    IReadOnlyList<QualificationWarmupDeviceProof> Devices)
{
    public string ControllerSessionId { get; init; } = "";

    public IReadOnlyList<QualificationStartRateChange> RateChangesSinceFirstStart(
        IReadOnlyDictionary<string, double> start1Rates, IReadOnlyList<RtcWarmupSample> current) =>
        Devices.Select(proof => new QualificationStartRateChange(proof.DeviceId,
            start1Rates.TryGetValue(proof.DeviceId, out double first) ? first : null,
            current.FirstOrDefault(sample => sample.Status.DeviceId == proof.DeviceId)?.Status.RtcRatePpmVsRtc)).ToArray();

    public IReadOnlyList<StartBlock> ValidateSecondStart(
        IReadOnlyList<RtcWarmupSample> current, long masterNowMicroseconds)
    {
        var blocks = new List<StartBlock>();
        long finishedAt = FirstTargetMasterMicroseconds + (long)FirstDurationSeconds * 1_000_000;
        if (FirstDurationSeconds < 2400 || masterNowMicroseconds < finishedAt ||
            masterNowMicroseconds - finishedAt > 120_000_000)
            blocks.Add(new(StartBlockReason.WarmupNotReady, "fleet", "START #2 requires a completed >=40-minute START #1, followed within 120 s."));
        if (!current.Select(s => s.Status.DeviceId).Order(StringComparer.Ordinal).SequenceEqual(
                Devices.Select(d => d.DeviceId).Order(StringComparer.Ordinal)))
            blocks.Add(new(StartBlockReason.WarmupNotReady, "fleet", "START #2 participant set differs from the frozen START #1 fleet."));
        foreach (QualificationWarmupDeviceProof proof in Devices)
        {
            RtcWarmupSample? sample = current.FirstOrDefault(s => s.Status.DeviceId == proof.DeviceId);
            RtcWarmupSample? baseline = proof.Readiness.Latest;
            if (!proof.Readiness.Ready || baseline is null || sample is null ||
                !HealthyContinuation(baseline, sample, masterNowMicroseconds) ||
                sample.Status.State != TimerState.Finished || sample.Status.CommandId != FirstCommandId)
                blocks.Add(new(StartBlockReason.WarmupNotReady, proof.DeviceId,
                    "START #2 rejected: missing warm-up proof, changed session/build, unfinished first run, stale STATUS, or unhealthy RTC. Start a fresh warm-up."));
        }
        return blocks;
    }

    public static bool HealthyContinuation(RtcWarmupSample baseline, RtcWarmupSample current, long now) =>
        current.SessionGeneration == baseline.SessionGeneration &&
        !string.IsNullOrWhiteSpace(baseline.Status.FirmwareBuildId) &&
        current.Status.FirmwareBuildId == baseline.Status.FirmwareBuildId &&
        now >= current.MasterMicroseconds && now - current.MasterMicroseconds < RtcWarmupHistory.MaximumStatusAgeMicroseconds &&
        current.Status.RtcState == RtcDisciplineState.Locked && current.Status.DiagnosticFlags == 0 &&
        current.Status.RtcFitPoints == RtcWarmupHistory.RequiredFitPoints &&
        current.Status.RtcFitRmsMicroseconds is >= 0 and <= RtcWarmupHistory.MaximumFitRmsMicroseconds &&
        current.Status.RtcRatePpmVsRtc.HasValue && double.IsFinite(current.Status.RtcRatePpmVsRtc.Value) &&
        current.Status.RtcQueueDrops == 0 && current.Status.RtcTemperatureValid == true &&
        current.Status.RtcAcceptedEdges.HasValue && current.Status.RtcAcceptedEdges >= baseline.Status.RtcAcceptedEdges &&
        current.Status.RtcInferredMissingEdges == baseline.Status.RtcInferredMissingEdges &&
        current.Status.RtcHoldoverEntries == baseline.Status.RtcHoldoverEntries;

    public string ToCsv()
    {
        var text = new StringBuilder("FirstRunCommandId,DeviceId,Kind,MasterUs,SessionGeneration,FirmwareBuildId,RtcState,DiagnosticFlags,RtcRatePpmVsRtc,RtcFitRmsUs,RtcTemperatureValid,RtcTemperatureC,HistorySeconds,RateChangePpm,GoodFitPolls,Ready,RtcFitPoints,WarmupMaxRateChangePpm,WarmupRequiredFitPoints,ControllerSessionId\n");
        foreach (QualificationWarmupDeviceProof proof in Devices)
        {
            foreach (RtcWarmupSample sample in proof.Samples)
                Append(proof, sample, sample.Kind == RtcWarmupSampleKind.PostSync ? "POST_SYNC" : "POLL");
            if (proof.Readiness.Latest is RtcWarmupSample latest) Append(proof, latest, "GATE");
        }
        return text.ToString();

        void Append(QualificationWarmupDeviceProof proof, RtcWarmupSample sample, string kind)
        {
            StatusPacket s = sample.Status;
            static string Number(double? n) => n?.ToString("R", CultureInfo.InvariantCulture) ?? "";
            static string Cell(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            text.AppendLine(string.Join(",", FirstCommandId.ToString("X16"), s.DeviceId, kind,
                sample.MasterMicroseconds.ToString(CultureInfo.InvariantCulture), sample.SessionGeneration.ToString(CultureInfo.InvariantCulture),
                Cell(s.FirmwareBuildId), s.RtcState.ToString().ToUpperInvariant(), s.DiagnosticFlags.HasValue ? $"0x{s.DiagnosticFlags:X2}" : "",
                Number(s.RtcRatePpmVsRtc), Number(s.RtcFitRmsMicroseconds), s.RtcTemperatureValid == true ? "1" : "0", Number(s.RtcTemperatureC),
                Number(proof.Readiness.HistorySeconds), Number(proof.Readiness.RateChangePpm),
                proof.Readiness.GoodFitPolls.ToString(CultureInfo.InvariantCulture), proof.Readiness.Ready ? "1" : "0",
                s.RtcFitPoints?.ToString(CultureInfo.InvariantCulture) ?? "",
                RtcWarmupHistory.MaximumRateChangePpm.ToString("R", CultureInfo.InvariantCulture),
                RtcWarmupHistory.RequiredFitPoints.ToString(CultureInfo.InvariantCulture), ControllerSessionId));
        }
    }
}
