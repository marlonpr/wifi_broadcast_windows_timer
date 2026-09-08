using System.Globalization;
using System.Net;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace FactoryTimer.Controller;

internal sealed class DeviceViewModel(string deviceId) : ObservableObject
{
    private static readonly Brush OnlineColor = new SolidColorBrush(Colors.LimeGreen);
    private static readonly Brush OfflineColor = new SolidColorBrush(Colors.Gray);
    private string onlineText = "OFFLINE";
    private Brush onlineBrush = OfflineColor;
    private string ipAddress = "—";
    private string lastCommandId = "—";
    private string remainingSeconds = "—";
    private string state = "UNKNOWN";
    private string ackStatus = "No ACK received";
    private string synchronizationState = "NOT SYNCED";
    private string synchronizationQuality = "—";
    private string clockError = "—";
    private string bestRtt = "—";
    private string verificationRtt = "—";
    private string appliedOffset = "—";
    private string estimatedMasterTime = "—";
    private string actualStartMasterTime = "—";
    private string startTimingError = "—";
    private string rssi = "—";
    private string wifiChannelText = "—";
    private string bssid = "—";
    private readonly DeviceStatusTracker statusTracker = new();
    private ulong pendingCommandId;
    private bool synchronizationAccepted;
    private long? residualErrorMicroseconds;
    private long? verificationOffsetMicroseconds;
    private long? appliedOffsetMicroseconds;
    private long? bestRttMicroseconds;
    private long? verificationRttMicroseconds;
    private long? lastStartErrorMicroseconds;
    private long? lastSchedulerLatenessMicroseconds;
    private long? lastActualStartMasterMicroseconds;
    private ulong? lastStartedCommandId;
    private long? initialResidualErrorMicroseconds;
    private long? expectedSyncBiasMicroseconds;
    private long? syncQualityDeviationMicroseconds;
    private long? syncQualityThresholdMicroseconds;
    private int syncAttemptCount;
    private bool syncQualityAccepted;
    private int? rssiDbm;
    private int? wifiChannel;
    private string? bssidValue;

    public string DeviceId { get; } = deviceId;
    public string OnlineText { get => onlineText; private set => SetProperty(ref onlineText, value); }
    public Brush OnlineBrush { get => onlineBrush; private set => SetProperty(ref onlineBrush, value); }
    public string IpAddress { get => ipAddress; private set => SetProperty(ref ipAddress, value); }
    public string LastCommandId { get => lastCommandId; private set => SetProperty(ref lastCommandId, value); }
    public string RemainingSeconds { get => remainingSeconds; private set => SetProperty(ref remainingSeconds, value); }
    public string State { get => state; private set => SetProperty(ref state, value); }
    public string AckStatus { get => ackStatus; private set => SetProperty(ref ackStatus, value); }
    public string SynchronizationState { get => synchronizationState; private set => SetProperty(ref synchronizationState, value); }
    public string SynchronizationQuality { get => synchronizationQuality; private set => SetProperty(ref synchronizationQuality, value); }
    public string ClockError { get => clockError; private set => SetProperty(ref clockError, value); }
    public string BestRtt { get => bestRtt; private set => SetProperty(ref bestRtt, value); }
    public string VerificationRtt { get => verificationRtt; private set => SetProperty(ref verificationRtt, value); }
    public string AppliedOffset { get => appliedOffset; private set => SetProperty(ref appliedOffset, value); }
    public string EstimatedMasterTime { get => estimatedMasterTime; private set => SetProperty(ref estimatedMasterTime, value); }
    public string ActualStartMasterTime { get => actualStartMasterTime; private set => SetProperty(ref actualStartMasterTime, value); }
    public string StartTimingError { get => startTimingError; private set => SetProperty(ref startTimingError, value); }
    public string Rssi { get => rssi; private set => SetProperty(ref rssi, value); }
    public string WifiChannelText { get => wifiChannelText; private set => SetProperty(ref wifiChannelText, value); }
    public string Bssid { get => bssid; private set => SetProperty(ref bssid, value); }
    public long? ResidualErrorMicroseconds => residualErrorMicroseconds;
    public long? VerificationOffsetMicroseconds => verificationOffsetMicroseconds;
    public long? AppliedOffsetMicroseconds => appliedOffsetMicroseconds;
    public long? BestRttMicroseconds => bestRttMicroseconds;
    public long? VerificationRttMicroseconds => verificationRttMicroseconds;
    public long? LastStartErrorMicroseconds => lastStartErrorMicroseconds;
    public long? LastSchedulerLatenessMicroseconds => lastSchedulerLatenessMicroseconds;
    public long? LastActualStartMasterMicroseconds => lastActualStartMasterMicroseconds;
    public ulong? LastStartedCommandId => lastStartedCommandId;
    public long? InitialResidualErrorMicroseconds => initialResidualErrorMicroseconds;
    public long? ExpectedSyncBiasMicroseconds => expectedSyncBiasMicroseconds;
    public long? SyncQualityDeviationMicroseconds => syncQualityDeviationMicroseconds;
    public long? SyncQualityThresholdMicroseconds => syncQualityThresholdMicroseconds;
    public int SyncAttemptCount => syncAttemptCount;
    public int SyncRetryCount => Math.Max(0, syncAttemptCount - 1);
    public bool SyncQualityAccepted => syncQualityAccepted;
    public int? RssiDbm => rssiDbm;
    public int? WifiChannel => wifiChannel;
    public string? BssidValue => bssidValue;
    public bool IsSynchronized => synchronizationAccepted;

    public bool TryGetIpAddress(out IPAddress? address) =>
        IPAddress.TryParse(IpAddress, out address);

    public void MarkPending(ulong commandId)
    {
        pendingCommandId = commandId;
        LastCommandId = FactoryProtocol.FormatCommandId(commandId);
        AckStatus = "Awaiting ACK";
    }

    public void Apply(AckPacket ack, IPEndPoint endpoint)
    {
        IpAddress = endpoint.Address.ToString();
        LastCommandId = FactoryProtocol.FormatCommandId(ack.CommandId);
        if (ack.CommandId == pendingCommandId)
        {
            AckStatus = ack.Result switch
            {
                AckResult.Accepted => $"ACK accepted ({ack.CommandType.ToString().ToUpperInvariant()})",
                AckResult.Duplicate when AckStatus.StartsWith("ACK accepted", StringComparison.Ordinal) =>
                    "ACK accepted; duplicate copies suppressed",
                AckResult.Duplicate => "ACK duplicate; command already applied",
                AckResult.NotSynced => "START_AT rejected: device is not synchronized",
                AckResult.Late => "START_AT rejected: target time already passed",
                _ => "Unexpected ACK",
            };
        }
        else
        {
            AckStatus = $"ACK for {FactoryProtocol.FormatCommandId(ack.CommandId)}";
        }
    }

    public void ClearStartMeasurement()
    {
        lastStartErrorMicroseconds = null;
        lastSchedulerLatenessMicroseconds = null;
        lastActualStartMasterMicroseconds = null;
        lastStartedCommandId = null;
        ActualStartMasterTime = "—";
        StartTimingError = "—";
    }

    public void Apply(StartedPacket started, IPEndPoint endpoint)
    {
        IpAddress = endpoint.Address.ToString();
        LastCommandId = FactoryProtocol.FormatCommandId(started.CommandId);

        // The firmware's EstimatedMasterStart uses the same applied offset that
        // scheduled START_AT. Under an asymmetric SYNC experiment that can look
        // perfectly self-consistent even when the physical start is biased. Use
        // the delay-free verification offset instead whenever it is available.
        long estimatedActualMasterStart = verificationOffsetMicroseconds.HasValue
            ? started.LocalStartMicroseconds + verificationOffsetMicroseconds.Value
            : started.EstimatedMasterStartMicroseconds;

        lastStartErrorMicroseconds =
            estimatedActualMasterStart - started.TargetMasterStartMicroseconds;
        lastSchedulerLatenessMicroseconds =
            started.EstimatedMasterStartMicroseconds - started.TargetMasterStartMicroseconds;
        lastActualStartMasterMicroseconds = estimatedActualMasterStart;
        lastStartedCommandId = started.CommandId;
        ActualStartMasterTime = $"{estimatedActualMasterStart:N0} µs";
        StartTimingError = FormatSignedMilliseconds(lastStartErrorMicroseconds.Value);
    }

    public void Apply(StatusPacket status, IPEndPoint endpoint)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        statusTracker.RecordValidStatus(now);
        IpAddress = endpoint.Address.ToString();
        LastCommandId = FactoryProtocol.FormatCommandId(status.CommandId);
        RemainingSeconds = status.RemainingSeconds.ToString(CultureInfo.InvariantCulture);
        State = status.State.ToString().ToUpperInvariant();

        if (status.RssiDbm.HasValue)
        {
            rssiDbm = status.RssiDbm;
            Rssi = $"{status.RssiDbm.Value} dBm";
        }
        if (status.WifiChannel.HasValue)
        {
            wifiChannel = status.WifiChannel;
            WifiChannelText = status.WifiChannel.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrWhiteSpace(status.Bssid))
        {
            bssidValue = status.Bssid;
            Bssid = status.Bssid;
        }

        RefreshOnlineStatus(now);
    }

    public void MarkSynchronizing()
    {
        synchronizationAccepted = false;
        syncQualityAccepted = false;
        syncAttemptCount = 0;
        initialResidualErrorMicroseconds = null;
        expectedSyncBiasMicroseconds = null;
        syncQualityDeviationMicroseconds = null;
        syncQualityThresholdMicroseconds = null;
        SynchronizationState = "SYNCHRONIZING";
        SynchronizationQuality = "—";
        verificationOffsetMicroseconds = null;
        ClockError = "—";
        BestRtt = "—";
        VerificationRtt = "—";
        AppliedOffset = "—";
        EstimatedMasterTime = "—";
        residualErrorMicroseconds = null;
        OnPropertyChanged(nameof(IsSynchronized));
    }

    public void MarkSynchronizationAttemptFailed(int attempt, int maxAttempts, string failureKind)
    {
        syncAttemptCount = attempt;
        syncQualityAccepted = false;
        synchronizationAccepted = false;
        SynchronizationState = "SYNC ERROR";
        SynchronizationQuality = $"ERROR {attempt}/{maxAttempts}: {failureKind}";
        OnPropertyChanged(nameof(IsSynchronized));
    }

    public void ApplySynchronizationMeasurement(
        long appliedOffsetMicroseconds,
        long bestRttMicroseconds,
        long residualMicroseconds,
        long verificationRttMicroseconds,
        long verificationOffsetMicroseconds,
        long initialResidualMicroseconds,
        long expectedBiasMicroseconds,
        long qualityDeviationMicroseconds,
        long qualityThresholdMicroseconds,
        int attempt,
        int maxAttempts,
        bool accepted)
    {
        residualErrorMicroseconds = residualMicroseconds;
        this.verificationOffsetMicroseconds = verificationOffsetMicroseconds;
        this.appliedOffsetMicroseconds = appliedOffsetMicroseconds;
        this.bestRttMicroseconds = bestRttMicroseconds;
        this.verificationRttMicroseconds = verificationRttMicroseconds;
        initialResidualErrorMicroseconds = initialResidualMicroseconds;
        expectedSyncBiasMicroseconds = expectedBiasMicroseconds;
        syncQualityDeviationMicroseconds = qualityDeviationMicroseconds;
        syncQualityThresholdMicroseconds = qualityThresholdMicroseconds;
        syncAttemptCount = attempt;
        syncQualityAccepted = accepted;
        synchronizationAccepted = accepted;

        SynchronizationState = accepted ? "SYNCED" : "QUALITY RETRY";
        SynchronizationQuality = accepted
            ? $"PASS {attempt}/{maxAttempts}; deviation {FormatSignedMilliseconds(qualityDeviationMicroseconds)}"
            : $"RETRY {attempt}/{maxAttempts}; deviation {FormatSignedMilliseconds(qualityDeviationMicroseconds)}";
        ClockError = FormatSignedMilliseconds(residualMicroseconds);
        BestRtt = FormatMilliseconds(bestRttMicroseconds);
        VerificationRtt = FormatMilliseconds(verificationRttMicroseconds);
        AppliedOffset = $"{appliedOffsetMicroseconds:+#;-#;0} µs";
        OnPropertyChanged(nameof(IsSynchronized));

        if (accepted)
        {
            UpdateEstimatedMasterTime(MasterClock.NowMicroseconds);
        }
        else
        {
            EstimatedMasterTime = "—";
        }
    }

    public void MarkSynchronizationQualityFailed(int maxAttempts)
    {
        synchronizationAccepted = false;
        syncQualityAccepted = false;
        SynchronizationState = "QUALITY FAILED";
        SynchronizationQuality =
            syncQualityDeviationMicroseconds.HasValue
                ? $"FAIL {syncAttemptCount}/{maxAttempts}; deviation {FormatSignedMilliseconds(syncQualityDeviationMicroseconds.Value)}"
                : $"FAIL {syncAttemptCount}/{maxAttempts}";
        EstimatedMasterTime = "—";
        OnPropertyChanged(nameof(IsSynchronized));
    }

    public void ClearSynchronization()
    {
        synchronizationAccepted = false;
        syncQualityAccepted = false;
        residualErrorMicroseconds = null;
        verificationOffsetMicroseconds = null;
        appliedOffsetMicroseconds = null;
        bestRttMicroseconds = null;
        verificationRttMicroseconds = null;
        initialResidualErrorMicroseconds = null;
        expectedSyncBiasMicroseconds = null;
        syncQualityDeviationMicroseconds = null;
        syncQualityThresholdMicroseconds = null;
        syncAttemptCount = 0;
        SynchronizationState = "NOT SYNCED";
        SynchronizationQuality = "—";
        ClockError = "—";
        BestRtt = "—";
        VerificationRtt = "—";
        AppliedOffset = "—";
        EstimatedMasterTime = "—";
        OnPropertyChanged(nameof(IsSynchronized));
    }

    public void UpdateEstimatedMasterTime(long masterNowMicroseconds)
    {
        if (!synchronizationAccepted || !residualErrorMicroseconds.HasValue)
        {
            EstimatedMasterTime = "—";
            return;
        }
        long deviceEstimate = masterNowMicroseconds + residualErrorMicroseconds.Value;
        EstimatedMasterTime = $"{deviceEstimate:N0} µs";
    }

    public void RefreshOnlineStatus(DateTimeOffset now)
    {
        DeviceAvailability availability = statusTracker.GetAvailability(now);
        OnlineText = availability switch
        {
            DeviceAvailability.Searching => "SEARCHING",
            DeviceAvailability.Online => "ONLINE",
            _ => "OFFLINE",
        };
        OnlineBrush = availability == DeviceAvailability.Online ? OnlineColor : OfflineColor;
    }

    public void BeginSearching()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        statusTracker.BeginSearching(now);
        RefreshOnlineStatus(now);
    }

    public void MarkOffline()
    {
        statusTracker.MarkOffline();
        OnlineText = "OFFLINE";
        OnlineBrush = OfflineColor;
        rssiDbm = null;
        wifiChannel = null;
        bssidValue = null;
        Rssi = "—";
        WifiChannelText = "—";
        Bssid = "—";
        ClearSynchronization();
    }

    private static string FormatMilliseconds(long microseconds) =>
        $"{microseconds / 1000d:F3} ms";

    private static string FormatSignedMilliseconds(long microseconds) =>
        $"{microseconds / 1000d:+0.000;-0.000;0.000} ms";
}
