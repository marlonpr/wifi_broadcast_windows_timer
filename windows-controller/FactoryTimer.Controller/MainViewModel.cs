using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;
using Microsoft.UI.Dispatching;

namespace FactoryTimer.Controller;

internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int StartLeadTimeMilliseconds = 2000;
    private const int ConsensusLowRttSampleCount = 3;
    private const long SyncQualityThresholdMicroseconds = 3_000;
    private const int MaxSynchronizationAttempts = 5;
    private const int SynchronizationRetryQuietMilliseconds = 150;
    private const int StatusDiscoveryDrainMilliseconds = 100;
    private readonly DispatcherQueue dispatcherQueue;
    private readonly DispatcherQueueTimer uiTimer;
    private readonly UdpControllerService udpService;
    private readonly StatusDiscoveryService statusDiscovery;
    private readonly NetworkInterfaceSelectionManager networkSelectionManager;
    private readonly ControllerClock clock = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private IReadOnlyList<ControllerNetworkInterface> networkInterfaces = [];
    private ControllerNetworkInterface? selectedNetworkInterface;
    private double durationInput = 20;
    private string manualBroadcastOverride = string.Empty;
    private string controllerRemaining = "20";
    private string controllerState = "READY";
    private string masterTime = "0 µs";
    private string synchronizationResolution = "Not measured";
    private string startSynchronizationResult = "Not measured";
    private string networkSummary = "Detecting usable physical network interfaces...";
    private string selectedLocalAddress = "Unavailable";
    private string selectedSubnetMask = "Unavailable";
    private string calculatedBroadcastAddress = "Unavailable";
    private string effectiveBroadcastAddress = "Unavailable";
    private string statusMessage = "Ready";
    private string benchmarkProgress = "Not run";
    private string benchmarkCsvPath = "—";
    private string benchmarkDiagnosticsCsvPath = "—";
    private double benchmarkTrialsInput = 10;
    private int syncPathDelayModeIndex;
    private int syncSamplingModeIndex;
    private int receiveTimestampModeIndex = (int)UdpReceiveTimestampMode.DedicatedBlockingThread;
    private bool canSend;
    private bool isSending;
    private bool isBenchmarkRunning;
    private CancellationTokenSource? benchmarkCancellation;
    private bool applyingNetworkState;
    private bool initialized;
    private bool disposed;
    private bool timingQuietPeriodActive;

    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        this.dispatcherQueue = dispatcherQueue;
        udpService = new UdpControllerService();
        udpService.ChangeReceiveTimestampMode(UdpReceiveTimestampMode.DedicatedBlockingThread);
        statusDiscovery = new StatusDiscoveryService(udpService);
        networkSelectionManager = new NetworkInterfaceSelectionManager(
            new SystemNetworkInterfaceProvider(),
            new FileSelectedInterfaceStore());
        uiTimer = dispatcherQueue.CreateTimer();
        uiTimer.Interval = TimeSpan.FromMilliseconds(100);
        uiTimer.Tick += UiTimer_Tick;
        udpService.PacketReceived += UdpService_PacketReceived;
        udpService.ReceiveError += UdpService_ReceiveError;
        statusDiscovery.DiscoveryError += StatusDiscovery_DiscoveryError;
        networkSelectionManager.StateChanged += NetworkSelectionManager_StateChanged;
    }

    public DeviceViewModel Esp01 { get; } = new("ESP01");
    public DeviceViewModel Esp02 { get; } = new("ESP02");
    public DeviceViewModel Esp03 { get; } = new("ESP03");
    public DeviceViewModel Esp04 { get; } = new("ESP04");
    public DeviceViewModel Esp05 { get; } = new("ESP05");
    private DeviceViewModel[] Devices => new[] { Esp01, Esp02, Esp03, Esp04, Esp05 };
    public IReadOnlyList<ControllerNetworkInterface> NetworkInterfaces
    {
        get => networkInterfaces;
        private set => SetProperty(ref networkInterfaces, value);
    }
    public ControllerNetworkInterface? SelectedNetworkInterface
    {
        get => selectedNetworkInterface;
        set
        {
            if (applyingNetworkState || value is null || value == selectedNetworkInterface) return;
            try
            {
                networkSelectionManager.Select(value.Id);
            }
            catch (ArgumentException exception)
            {
                StatusMessage = exception.Message;
            }
        }
    }
    public double DurationInput { get => durationInput; set => SetProperty(ref durationInput, value); }
    public string ManualBroadcastOverride
    {
        get => manualBroadcastOverride;
        set
        {
            if (SetProperty(ref manualBroadcastOverride, value)) RefreshNetworkDetails();
        }
    }
    public string ControllerRemaining { get => controllerRemaining; private set => SetProperty(ref controllerRemaining, value); }
    public string ControllerState { get => controllerState; private set => SetProperty(ref controllerState, value); }
    public string MasterTime { get => masterTime; private set => SetProperty(ref masterTime, value); }
    public string SynchronizationResolution { get => synchronizationResolution; private set => SetProperty(ref synchronizationResolution, value); }
    public string StartSynchronizationResult { get => startSynchronizationResult; private set => SetProperty(ref startSynchronizationResult, value); }
    public string NetworkSummary { get => networkSummary; private set => SetProperty(ref networkSummary, value); }
    public string SelectedLocalAddress { get => selectedLocalAddress; private set => SetProperty(ref selectedLocalAddress, value); }
    public string SelectedSubnetMask { get => selectedSubnetMask; private set => SetProperty(ref selectedSubnetMask, value); }
    public string CalculatedBroadcastAddress { get => calculatedBroadcastAddress; private set => SetProperty(ref calculatedBroadcastAddress, value); }
    public string EffectiveBroadcastAddress { get => effectiveBroadcastAddress; private set => SetProperty(ref effectiveBroadcastAddress, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string BenchmarkProgress { get => benchmarkProgress; private set => SetProperty(ref benchmarkProgress, value); }
    public string BenchmarkCsvPath { get => benchmarkCsvPath; private set => SetProperty(ref benchmarkCsvPath, value); }
    public string BenchmarkDiagnosticsCsvPath { get => benchmarkDiagnosticsCsvPath; private set => SetProperty(ref benchmarkDiagnosticsCsvPath, value); }
    public double BenchmarkTrialsInput { get => benchmarkTrialsInput; set => SetProperty(ref benchmarkTrialsInput, value); }
    public bool CanSend { get => canSend; private set => SetProperty(ref canSend, value); }
    public bool IsBenchmarkRunning
    {
        get => isBenchmarkRunning;
        private set
        {
            if (SetProperty(ref isBenchmarkRunning, value))
            {
                OnPropertyChanged(nameof(CanCancelBenchmark));
            }
        }
    }
    public bool CanCancelBenchmark => IsBenchmarkRunning;
    public int SyncPathDelayModeIndex
    {
        get => syncPathDelayModeIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, 2);
            if (SetProperty(ref syncPathDelayModeIndex, clamped))
            {
                OnPropertyChanged(nameof(SyncPathDelayDescription));

                // A path-delay selection only affects future calibration samples.
                // Never leave an older synchronization result marked as valid after
                // the operator changes the experiment mode, otherwise START_AT could
                // accidentally reuse an offset measured under the previous mode.
                foreach (DeviceViewModel device in Devices)
                {
                    device.ClearSynchronization();
                    device.ClearStartMeasurement();
                }
                SynchronizationResolution =
                    "SYNC mode changed; press SYNC CLOCKS to measure the selected path.";
                StartSynchronizationResult =
                    "START_AT requires a fresh synchronization after changing SYNC mode.";
                StatusMessage =
                    $"SYNC path-delay mode changed to {BuildSyncPathDelayModeLabel(clamped)}. Press SYNC CLOCKS before START_AT.";
            }
        }
    }
    public int SyncSamplingModeIndex
    {
        get => syncSamplingModeIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, 1);
            if (!SetProperty(ref syncSamplingModeIndex, clamped)) return;
            OnPropertyChanged(nameof(SyncSamplingModeDescription));

            foreach (DeviceViewModel device in Devices)
            {
                device.ClearSynchronization();
                device.ClearStartMeasurement();
            }

            SyncSamplingProfile profile = SyncSamplingExperiment.GetProfile((SyncSamplingMode)clamped);
            SynchronizationResolution =
                "SYNC sample count changed; press SYNC CLOCKS again.";
            StartSynchronizationResult =
                "START_AT requires a fresh synchronization after changing sample count.";
            StatusMessage =
                $"SYNC sampling changed to {BuildSyncSamplingModeLabel(clamped)} " +
                $"({profile.CalibrationSampleCount}+{profile.VerificationSampleCount}, best {profile.LowRttSampleCount}); press SYNC CLOCKS before START_AT.";
        }
    }

    public int ReceiveTimestampModeIndex
    {
        get => receiveTimestampModeIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, 1);
            if (!SetProperty(ref receiveTimestampModeIndex, clamped)) return;
            OnPropertyChanged(nameof(ReceiveTimestampModeDescription));

            statusDiscovery.Stop();
            try
            {
                udpService.ChangeReceiveTimestampMode((UdpReceiveTimestampMode)clamped);
                foreach (DeviceViewModel device in Devices)
                {
                    device.ClearSynchronization();
                    device.ClearStartMeasurement();
                }

                SynchronizationResolution =
                    "UDP T4 receive path changed; press SYNC CLOCKS again.";
                StartSynchronizationResult =
                    "START_AT requires a fresh synchronization after changing the T4 receive path.";

                if (SelectedNetworkInterface is not null && !timingQuietPeriodActive)
                {
                    statusDiscovery.StartOrRestart(
                        SelectedNetworkInterface,
                        () => Volatile.Read(ref manualBroadcastOverride));
                }

                StatusMessage =
                    $"UDP T4 receive path changed to {BuildReceiveTimestampModeLabel(clamped)}. Press SYNC CLOCKS before START_AT.";
            }
            catch (Exception exception)
            {
                StatusMessage = $"Could not change UDP T4 receive path: {exception.Message}";
            }
        }
    }

    public string ReceiveTimestampModeDescription => ReceiveTimestampModeIndex switch
    {
        0 => "Diagnostic/reference mode: T4 is captured immediately after await ReceiveAsync resumes.",
        1 => "Production default: a dedicated AboveNormal OS thread blocks in ReceiveFrom and captures T4 immediately after the socket call returns.",
        _ => string.Empty,
    };

    public string SyncSamplingModeDescription
    {
        get
        {
            SyncSamplingProfile profile = SyncSamplingExperiment.GetProfile(
                (SyncSamplingMode)Math.Clamp(SyncSamplingModeIndex, 0, 1));
            return $"{profile.CalibrationSampleCount} calibration + {profile.VerificationSampleCount} verification exchanges per device; " +
                   $"each phase keeps the {profile.LowRttSampleCount} lowest-RTT valid samples and uses the unchanged 1/RTT² weighted estimator.";
        }
    }

    public string SyncPathDelayDescription => BuildSyncPathDelayDescription();

    public void Initialize()
    {
        if (initialized) return;
        initialized = true;
        networkSelectionManager.Initialize();
        uiTimer.Start();
    }

    public Task SendStartAsync() => SendStartAtAsync();

    public Task SendResetAsync() => SendResetInternalAsync();

    public Task RunBenchmarkAsync() => RunBenchmarkInternalAsync();

    public void CancelBenchmark() => benchmarkCancellation?.Cancel();

    public async Task SynchronizeAsync()
    {
        if (!TryGetReadyDevices(out List<(DeviceViewModel Device, IPAddress Address)> readyDevices)) return;
        if (!await sendLock.WaitAsync(0)) return;

        isSending = true;
        UpdateCanSend();
        long fleetSyncStartUs = MasterClock.NowMicroseconds;
        try
        {
            await EnterTimingQuietPeriodAsync(CancellationToken.None);
            foreach (DeviceViewModel device in Devices)
            {
                device.MarkSynchronizing();
            }
            SynchronizationResolution = "Measuring 5-device fleet...";

            SyncPathDelayProfile noDelay =
                SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None);
            SyncPathDelayProfile esp02CalibrationDelay =
                SyncPathDelayExperiment.GetProfile((SyncPathDelayMode)SyncPathDelayModeIndex);
            SyncSamplingProfile samplingProfile =
                SyncSamplingExperiment.GetProfile((SyncSamplingMode)SyncSamplingModeIndex);

            for (int index = 0; index < readyDevices.Count; index++)
            {
                (DeviceViewModel device, IPAddress address) = readyDevices[index];
                SyncPathDelayProfile calibrationDelay = ReferenceEquals(device, Esp02)
                    ? esp02CalibrationDelay
                    : noDelay;
                StatusMessage =
                    $"Synchronizing {device.DeviceId} ({index + 1}/{readyDevices.Count}): " +
                    $"{samplingProfile.CalibrationSampleCount} calibration + {samplingProfile.VerificationSampleCount} verification samples, " +
                    $"1/RTT² weighted offset of best {samplingProfile.LowRttSampleCount} RTTs; " +
                    $"calibration delay {calibrationDelay.MasterToDeviceDelayMilliseconds}/{calibrationDelay.DeviceToMasterDelayMilliseconds} ms, verification 0/0 ms...";
                await SynchronizeDeviceAsync(device, address, calibrationDelay, samplingProfile);
            }

            long fleetSyncDurationUs = MasterClock.NowMicroseconds - fleetSyncStartUs;
            RefreshSynchronizationResolution();
            int totalRetries = Devices.Sum(device => device.SyncRetryCount);
            StatusMessage =
                $"5-device synchronization complete in {fleetSyncDurationUs / 1000d:F1} ms with {totalRetries} quality retries using " +
                $"{samplingProfile.CalibrationSampleCount}+{samplingProfile.VerificationSampleCount} samples/device. " +
                $"ESP02 calibration={esp02CalibrationDelay.MasterToDeviceDelayMilliseconds}/{esp02CalibrationDelay.DeviceToMasterDelayMilliseconds} ms; " +
                $"ESP01/03/04/05 and all verification samples=0/0 ms.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Synchronization failed: {exception.Message}";
            RefreshSynchronizationResolution();
        }
        finally
        {
            ExitTimingQuietPeriod();
            isSending = false;
            UpdateCanSend();
            sendLock.Release();
        }
    }

    private async Task SynchronizeDeviceAsync(
        DeviceViewModel device,
        IPAddress address,
        SyncPathDelayProfile calibrationDelay,
        SyncSamplingProfile samplingProfile,
        CancellationToken cancellationToken = default,
        ICollection<SyncSampleDiagnosticCsvRow>? diagnosticRows = null,
        int benchmarkTrial = 0,
        DateTimeOffset? benchmarkTimestampUtc = null,
        string? benchmarkSyncMode = null)
    {
        long? initialResidualMicroseconds = null;
        long expectedBiasMicroseconds = calibrationDelay.ExpectedOffsetBiasMicroseconds;

        for (int attempt = 1; attempt <= MaxSynchronizationAttempts; attempt++)
        {
            List<SyncMeasurement> samples = [];
            List<SyncMeasurement> verificationSamples = [];
            ClockSyncConsensus? calibrationConsensus = null;
            ClockSyncConsensus? verificationConsensus = null;
            SyncMeasurement? representativeCalibration = null;
            long residual = 0;
            SyncQualityEvaluation quality = default;
            string activePhase = "CALIBRATION";
            int activeSampleIndex = 0;
            ulong? activeSyncId = null;

            try
            {
                for (int index = 0; index < samplingProfile.CalibrationSampleCount; index++)
                {
                    activePhase = "CALIBRATION";
                    activeSampleIndex = index + 1;
                    activeSyncId = CreateCommandId();
                    samples.Add(await MeasureSyncAsync(
                        device.DeviceId,
                        address,
                        calibrationDelay,
                        activeSyncId.Value,
                        cancellationToken));
                    activeSyncId = null;
                    activeSampleIndex = 0;
                    await Task.Delay(15, cancellationToken);
                }

                activePhase = "CALIBRATION_ESTIMATOR";
                calibrationConsensus = ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
                    samples.Select(item => item.Sample),
                    samplingProfile.LowRttSampleCount);
                representativeCalibration = samples.First(item =>
                    item.Sample == calibrationConsensus.Value.RepresentativeSample);

                var syncSet = new SyncSetPacket(
                    representativeCalibration.SyncId,
                    calibrationConsensus.Value.MasterMinusLocalOffsetMicroseconds,
                    calibrationConsensus.Value.BestRttMicroseconds);

                activePhase = "SYNC_SET";
                activeSampleIndex = 0;
                activeSyncId = representativeCalibration.SyncId;
                SyncAppliedPacket applied = await udpService.ApplySyncAsync(
                    syncSet,
                    address,
                    cancellationToken: cancellationToken);
                if (!string.Equals(applied.DeviceId, device.DeviceId, StringComparison.Ordinal) ||
                    applied.MasterMinusLocalOffsetMicroseconds != syncSet.MasterMinusLocalOffsetMicroseconds)
                {
                    throw new InvalidOperationException($"{device.DeviceId} returned an inconsistent SYNC_APPLIED response.");
                }
                activeSyncId = null;

                await Task.Delay(25, cancellationToken);
                for (int index = 0; index < samplingProfile.VerificationSampleCount; index++)
                {
                    activePhase = "VERIFICATION";
                    activeSampleIndex = index + 1;
                    activeSyncId = CreateCommandId();
                    verificationSamples.Add(await MeasureSyncAsync(
                        device.DeviceId,
                        address,
                        SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None),
                        activeSyncId.Value,
                        cancellationToken));
                    activeSyncId = null;
                    activeSampleIndex = 0;
                    await Task.Delay(15, cancellationToken);
                }

                activePhase = "VERIFICATION_ESTIMATOR";
                verificationConsensus = ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
                    verificationSamples.Select(item => item.Sample),
                    samplingProfile.LowRttSampleCount);

                residual =
                    syncSet.MasterMinusLocalOffsetMicroseconds -
                    verificationConsensus.Value.MasterMinusLocalOffsetMicroseconds;
                initialResidualMicroseconds ??= residual;
                quality = SyncQualityPolicy.Evaluate(
                    residual,
                    expectedBiasMicroseconds,
                    SyncQualityThresholdMicroseconds);

                if (diagnosticRows is not null)
                {
                    AppendSyncAttemptDiagnostics(
                        diagnosticRows,
                        benchmarkTrial,
                        benchmarkTimestampUtc ?? DateTimeOffset.UtcNow,
                        benchmarkSyncMode ?? string.Empty,
                        device,
                        attempt,
                        calibrationDelay,
                        samples,
                        calibrationConsensus.Value,
                        representativeCalibration,
                        verificationSamples,
                        verificationConsensus.Value,
                        residual,
                        expectedBiasMicroseconds,
                        quality);
                }
            }
            catch (OperationCanceledException exception)
            {
                if (diagnosticRows is not null)
                {
                    AppendIncompleteSyncAttemptDiagnostics(
                        diagnosticRows,
                        benchmarkTrial,
                        benchmarkTimestampUtc ?? DateTimeOffset.UtcNow,
                        benchmarkSyncMode ?? string.Empty,
                        device,
                        attempt,
                        calibrationDelay,
                        samples,
                        calibrationConsensus,
                        verificationSamples,
                        verificationConsensus,
                        activePhase,
                        activeSampleIndex,
                        activeSyncId,
                        exception);
                }
                throw;
            }
            catch (Exception exception)
            {
                string failureKind = ClassifySyncFailure(exception);
                device.MarkSynchronizationAttemptFailed(
                    attempt,
                    MaxSynchronizationAttempts,
                    failureKind);
                if (diagnosticRows is not null)
                {
                    AppendIncompleteSyncAttemptDiagnostics(
                        diagnosticRows,
                        benchmarkTrial,
                        benchmarkTimestampUtc ?? DateTimeOffset.UtcNow,
                        benchmarkSyncMode ?? string.Empty,
                        device,
                        attempt,
                        calibrationDelay,
                        samples,
                        calibrationConsensus,
                        verificationSamples,
                        verificationConsensus,
                        activePhase,
                        activeSampleIndex,
                        activeSyncId,
                        exception);
                }

                bool canRetryTransportFailure =
                    attempt < MaxSynchronizationAttempts &&
                    SelectedNetworkInterface is not null &&
                    udpService.IsReady &&
                    SyncRetryPolicy.IsRetryableTransportFailure(exception);

                if (canRetryTransportFailure)
                {
                    StatusMessage =
                        $"{device.DeviceId} transient sync transport retry {attempt}/{MaxSynchronizationAttempts}: " +
                        $"{failureKind}: {exception.Message}";
                    await Task.Delay(SynchronizationRetryQuietMilliseconds, cancellationToken);
                    continue;
                }

                throw;
            }

            device.ApplySynchronizationMeasurement(
                calibrationConsensus!.Value.MasterMinusLocalOffsetMicroseconds,
                calibrationConsensus.Value.BestRttMicroseconds,
                residual,
                verificationConsensus!.Value.BestRttMicroseconds,
                verificationConsensus.Value.MasterMinusLocalOffsetMicroseconds,
                initialResidualMicroseconds!.Value,
                expectedBiasMicroseconds,
                quality.DeviationMicroseconds,
                SyncQualityThresholdMicroseconds,
                attempt,
                MaxSynchronizationAttempts,
                quality.IsAccepted);

            if (quality.IsAccepted)
            {
                return;
            }

            if (attempt < MaxSynchronizationAttempts)
            {
                StatusMessage =
                    $"{device.DeviceId} verification quality retry {attempt}/{MaxSynchronizationAttempts}: " +
                    $"residual={residual / 1000d:+0.000;-0.000;0.000} ms, " +
                    $"expected={expectedBiasMicroseconds / 1000d:+0.000;-0.000;0.000} ms, " +
                    $"deviation={quality.DeviationMicroseconds / 1000d:+0.000;-0.000;0.000} ms; " +
                    $"limit=±{SyncQualityThresholdMicroseconds / 1000d:F3} ms.";
                await Task.Delay(SynchronizationRetryQuietMilliseconds, cancellationToken);
                continue;
            }

            device.MarkSynchronizationQualityFailed(MaxSynchronizationAttempts);
            throw new InvalidOperationException(
                $"{device.DeviceId} failed synchronization quality after {MaxSynchronizationAttempts} attempts: " +
                $"final deviation={quality.DeviationMicroseconds / 1000d:+0.000;-0.000;0.000} ms " +
                $"from expected bias {expectedBiasMicroseconds / 1000d:+0.000;-0.000;0.000} ms " +
                $"(limit ±{SyncQualityThresholdMicroseconds / 1000d:F3} ms).");
        }
    }

    private async Task<SyncMeasurement> MeasureSyncAsync(
        string expectedDeviceId,
        IPAddress address,
        SyncPathDelayProfile pathDelay,
        ulong syncId,
        CancellationToken cancellationToken = default)
    {
        // t1 MUST be captured before the optional forward delay. Otherwise the
        // injected Master->ESP delay would disappear from the NTP-style sample.
        long t1 = MasterClock.NowMicroseconds;
        uint reverseDelayUs = checked(
            (uint)pathDelay.DeviceToMasterDelayMilliseconds * 1000U);
        ReceivedSyncReply received = await udpService.ExchangeSyncAsync(
            syncId,
            t1,
            address,
            masterToDeviceArtificialDelay: TimeSpan.FromMilliseconds(
                pathDelay.MasterToDeviceDelayMilliseconds),
            deviceToMasterArtificialDelayMicroseconds: reverseDelayUs,
            timeout: TimeSpan.FromMilliseconds(1500),
            cancellationToken: cancellationToken);
        SyncReplyPacket reply = received.Packet;
        if (!string.Equals(reply.DeviceId, expectedDeviceId, StringComparison.Ordinal) ||
            reply.SyncId != syncId || reply.MasterT1Microseconds != t1)
        {
            throw new InvalidOperationException($"Unexpected SYNC_REPLY while synchronizing {expectedDeviceId}.");
        }
        var sample = new ClockSyncSample(
            t1,
            reply.LocalT2Microseconds,
            reply.LocalT3Microseconds,
            received.MasterT4Microseconds);
        if (!sample.IsValid)
        {
            throw new InvalidOperationException($"Invalid synchronization timing sample from {expectedDeviceId}.");
        }
        return new SyncMeasurement(syncId, sample);
    }

    private async Task RunBenchmarkInternalAsync()
    {
        if (!TryBenchmarkTrialCount(out int trialCount)) return;
        if (!TryDuration(out uint duration)) return;
        if (!TryGetReadyDevices(out List<(DeviceViewModel Device, IPAddress Address)> readyDevices)) return;
        if (!await sendLock.WaitAsync(0)) return;

        isSending = true;
        IsBenchmarkRunning = true;
        UpdateCanSend();
        benchmarkCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = benchmarkCancellation.Token;
        var rows = new List<BenchmarkCsvRow>();
        var diagnosticRows = new List<SyncSampleDiagnosticCsvRow>();
        var summaries = new List<BenchmarkTrialSummary>();
        string benchmarkRunId = DateTime.Now.ToString(
            "yyyyMMdd_HHmmss",
            System.Globalization.CultureInfo.InvariantCulture);
        bool cancelled = false;

        try
        {
            BenchmarkCsvPath = "—";
            BenchmarkDiagnosticsCsvPath = "—";
            SyncPathDelayProfile noDelay =
                SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None);
            SyncPathDelayProfile esp02CalibrationDelay =
                SyncPathDelayExperiment.GetProfile((SyncPathDelayMode)SyncPathDelayModeIndex);
            SyncSamplingProfile samplingProfile =
                SyncSamplingExperiment.GetProfile((SyncSamplingMode)SyncSamplingModeIndex);
            string benchmarkSyncMode =
                $"{BuildSyncPathDelayModeLabel(SyncPathDelayModeIndex)} | RX={BuildReceiveTimestampModeLabel(ReceiveTimestampModeIndex)} | " +
                $"SAMPLES={BuildSyncSamplingModeLabel(SyncSamplingModeIndex)}";

            for (int trial = 1; trial <= trialCount; trial++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTimeOffset trialUtc = DateTimeOffset.UtcNow;
                ulong commandId = 0;
                long targetMasterUs = 0;
                long syncDurationUs = 0;
                long syncStartUs = 0;
                string failure = string.Empty;

                try
                {
                    if (!TryGetReadyDevices(out readyDevices))
                    {
                        throw new InvalidOperationException("All five devices must remain discoverable for every benchmark trial.");
                    }
                    await EnterTimingQuietPeriodAsync(cancellationToken);
                    BenchmarkProgress =
                        $"Trial {trial}/{trialCount}: synchronizing five devices...";
                    foreach (DeviceViewModel device in Devices)
                    {
                        device.MarkSynchronizing();
                        device.ClearStartMeasurement();
                    }

                    syncStartUs = MasterClock.NowMicroseconds;
                    for (int index = 0; index < readyDevices.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        (DeviceViewModel device, IPAddress address) = readyDevices[index];
                        SyncPathDelayProfile calibrationDelay = ReferenceEquals(device, Esp02)
                            ? esp02CalibrationDelay
                            : noDelay;
                        BenchmarkProgress =
                            $"Trial {trial}/{trialCount}: SYNC {device.DeviceId} ({index + 1}/5)...";
                        await SynchronizeDeviceAsync(
                            device,
                            address,
                            calibrationDelay,
                            samplingProfile,
                            cancellationToken,
                            diagnosticRows,
                            trial,
                            trialUtc,
                            benchmarkSyncMode);
                    }
                    syncDurationUs = MasterClock.NowMicroseconds - syncStartUs;
                    RefreshSynchronizationResolution();

                    cancellationToken.ThrowIfCancellationRequested();
                    BenchmarkProgress =
                        $"Trial {trial}/{trialCount}: broadcasting START_AT...";
                    (commandId, targetMasterUs) = await SendBenchmarkStartAtAsync(
                        duration,
                        cancellationToken);

                    BenchmarkProgress =
                        $"Trial {trial}/{trialCount}: waiting for 5/5 STARTED telemetry...";
                    await WaitForStartedTelemetryAsync(
                        commandId,
                        TimeSpan.FromSeconds(6),
                        cancellationToken);
                    RefreshStartSynchronizationResult();

                    long[] clockErrors = Devices
                        .Select(device => device.ResidualErrorMicroseconds
                            ?? throw new InvalidOperationException($"{device.DeviceId} is missing verification error."))
                        .ToArray();
                    long[] startErrors = Devices
                        .Select(device => device.LastStartErrorMicroseconds
                            ?? throw new InvalidOperationException($"{device.DeviceId} is missing STARTED telemetry."))
                        .ToArray();
                    long worstClockErrorUs = clockErrors.Max(value => Math.Abs(value));
                    long fleetClockSpreadUs = clockErrors.Max() - clockErrors.Min();
                    long worstStartErrorUs = startErrors.Max(value => Math.Abs(value));
                    long fleetStartSpreadUs = startErrors.Max() - startErrors.Min();

                    foreach (DeviceViewModel device in Devices)
                    {
                        rows.Add(BenchmarkCsvRow.CreateSuccess(
                            trial,
                            trialUtc,
                            benchmarkSyncMode,
                            device,
                            commandId,
                            targetMasterUs,
                            syncDurationUs,
                            worstClockErrorUs,
                            fleetClockSpreadUs,
                            worstStartErrorUs,
                            fleetStartSpreadUs));
                    }
                    summaries.Add(new BenchmarkTrialSummary(
                        trial,
                        syncDurationUs,
                        fleetClockSpreadUs,
                        fleetStartSpreadUs,
                        worstClockErrorUs,
                        worstStartErrorUs,
                        Devices.Sum(device => device.SyncRetryCount),
                        true));
                    int trialRetries = Devices.Sum(device => device.SyncRetryCount);
                    BenchmarkProgress =
                        $"Trial {trial}/{trialCount} complete: START spread {fleetStartSpreadUs / 1000d:F3} ms; " +
                        $"worst |START error| {worstStartErrorUs / 1000d:F3} ms; quality retries {trialRetries}.";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (syncStartUs > 0 && syncDurationUs == 0)
                    {
                        syncDurationUs = MasterClock.NowMicroseconds - syncStartUs;
                    }
                    failure = exception.Message;
                    foreach (DeviceViewModel device in Devices)
                    {
                        rows.Add(BenchmarkCsvRow.CreateFailure(
                            trial,
                            trialUtc,
                            benchmarkSyncMode,
                            device,
                            commandId,
                            targetMasterUs,
                            syncDurationUs,
                            failure));
                    }
                    summaries.Add(new BenchmarkTrialSummary(
                        trial,
                        syncDurationUs,
                        null,
                        null,
                        null,
                        null,
                        Devices.Sum(device => device.SyncRetryCount),
                        false));
                    BenchmarkProgress =
                        $"Trial {trial}/{trialCount} failed: {failure}. Continuing...";
                }
                finally
                {
                    await BestEffortBenchmarkResetAsync(duration);
                    ExitTimingQuietPeriod();
                }

                if (trial < trialCount)
                {
                    await Task.Delay(350, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            BenchmarkProgress = "Benchmark cancellation requested; saving completed trials...";
        }
        finally
        {
            try
            {
                if (rows.Count > 0)
                {
                    string path = await WriteBenchmarkCsvAsync(rows, benchmarkRunId);
                    BenchmarkCsvPath = path;
                }
            }
            catch (Exception exception)
            {
                BenchmarkCsvPath = $"Summary CSV save failed: {exception.Message}";
            }

            try
            {
                if (diagnosticRows.Count > 0)
                {
                    string diagnosticsPath = await WriteSyncDiagnosticsCsvAsync(
                        diagnosticRows,
                        benchmarkRunId);
                    BenchmarkDiagnosticsCsvPath = diagnosticsPath;
                }
            }
            catch (Exception exception)
            {
                BenchmarkDiagnosticsCsvPath = $"Raw SYNC CSV save failed: {exception.Message}";
            }

            BenchmarkProgress = BuildBenchmarkSummary(
                summaries,
                trialCount,
                cancelled,
                BenchmarkCsvPath,
                BenchmarkDiagnosticsCsvPath);
            StatusMessage = BenchmarkProgress;
            benchmarkCancellation?.Dispose();
            benchmarkCancellation = null;
            IsBenchmarkRunning = false;
            isSending = false;
            UpdateCanSend();
            sendLock.Release();
        }
    }

    private async Task<(ulong CommandId, long TargetMasterUs)> SendBenchmarkStartAtAsync(
        uint duration,
        CancellationToken cancellationToken)
    {
        ControllerNetworkInterface selection = SelectedNetworkInterface
            ?? throw new InvalidOperationException("No network interface is selected.");
        BroadcastAddressResolution resolution = BroadcastAddressResolver.Resolve(
            selection,
            ManualBroadcastOverride);
        if (!resolution.IsValid || resolution.Address is null)
        {
            throw new InvalidOperationException(resolution.Error ?? "No valid broadcast destination.");
        }

        ulong commandId = CreateCommandId();
        long targetMasterUs = MasterClock.NowMicroseconds + StartLeadTimeMilliseconds * 1000L;
        var command = new CommandPacket(
            CommandType.StartAt,
            commandId,
            duration,
            0,
            targetMasterUs);
        foreach (DeviceViewModel device in Devices)
        {
            device.MarkPending(commandId);
            device.ClearStartMeasurement();
        }
        StartSynchronizationResult = "Benchmark: waiting for STARTED telemetry...";
        clock.ArmAt(duration, targetMasterUs);
        await udpService.SendCommandAsync(
            command,
            resolution.Address,
            cancellationToken);
        return (commandId, targetMasterUs);
    }

    private async Task WaitForStartedTelemetryAsync(
        ulong commandId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long deadlineUs = MasterClock.NowMicroseconds + (long)timeout.TotalMilliseconds * 1000L;
        while (MasterClock.NowMicroseconds < deadlineUs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int received = Devices.Count(device => device.LastStartedCommandId == commandId);
            if (received == Devices.Length)
            {
                return;
            }
            BenchmarkProgress = $"Waiting for STARTED telemetry: {received}/5 received...";
            await Task.Delay(20, cancellationToken);
        }

        string missing = string.Join(
            ", ",
            Devices
                .Where(device => device.LastStartedCommandId != commandId)
                .Select(device => device.DeviceId));
        throw new TimeoutException($"STARTED telemetry timed out; missing {missing}.");
    }

    private async Task BestEffortBenchmarkResetAsync(uint duration)
    {
        try
        {
            if (SelectedNetworkInterface is null || !udpService.IsReady) return;
            BroadcastAddressResolution resolution = BroadcastAddressResolver.Resolve(
                SelectedNetworkInterface,
                ManualBroadcastOverride);
            if (!resolution.IsValid || resolution.Address is null) return;

            ulong commandId = CreateCommandId();
            var command = new CommandPacket(CommandType.Reset, commandId, duration, 0);
            await udpService.SendCommandAsync(command, resolution.Address);
            clock.Reset(duration);
            RefreshClockProperties();
            await Task.Delay(100);
        }
        catch
        {
            // Benchmark cleanup is best-effort. The next trial performs a fresh
            // synchronization and uses a new absolute START_AT command ID.
        }
    }

    private bool TryBenchmarkTrialCount(out int trialCount)
    {
        trialCount = 0;
        if (double.IsNaN(BenchmarkTrialsInput) ||
            BenchmarkTrialsInput != Math.Truncate(BenchmarkTrialsInput) ||
            BenchmarkTrialsInput < 1 || BenchmarkTrialsInput > 100)
        {
            StatusMessage = "Benchmark trials must be a whole number from 1 through 100.";
            return false;
        }
        trialCount = (int)BenchmarkTrialsInput;
        return true;
    }

    private static string GetHardwareFamily(string deviceId) => deviceId switch
    {
        "ESP01" or "ESP02" or "ESP04" => "ESP32",
        "ESP03" or "ESP05" => "ESP32-S3",
        _ => "Unknown",
    };

    private static async Task<string> WriteBenchmarkCsvAsync(
        IReadOnlyList<BenchmarkCsvRow> rows,
        string benchmarkRunId)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string root = string.IsNullOrWhiteSpace(documents)
            ? AppContext.BaseDirectory
            : documents;
        string directory = Path.Combine(root, "FactoryTimerBenchmarks");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"factory_timer_5_device_benchmark_{benchmarkRunId}.csv");

        var builder = new StringBuilder();
        builder.AppendLine(
            "Trial,TimestampUtc,Success,Failure,SyncMode,Device,Hardware,IpAddress," +
            "BestSyncRttUs,VerifyRttUs,AppliedOffsetUs,VerificationOffsetUs,ClockErrorUs," +
            "SyncAttempts,SyncRetries,InitialClockErrorUs,ExpectedSyncBiasUs,SyncQualityDeviationUs,SyncQualityThresholdUs,SyncQualityAccepted," +
            "RssiDbm,WifiChannel,Bssid," +
            "CommandId,TargetMasterUs,VerifiedStartMasterUs,StartErrorUs,SchedulerLatenessUs," +
            "FleetSyncDurationUs,WorstClockErrorUs,FleetClockSpreadUs,WorstStartErrorUs,FleetStartSpreadUs");
        foreach (BenchmarkCsvRow row in rows)
        {
            builder.AppendLine(row.ToCsv());
        }
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static async Task<string> WriteSyncDiagnosticsCsvAsync(
        IReadOnlyList<SyncSampleDiagnosticCsvRow> rows,
        string benchmarkRunId)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string root = string.IsNullOrWhiteSpace(documents)
            ? AppContext.BaseDirectory
            : documents;
        string directory = Path.Combine(root, "FactoryTimerBenchmarks");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"factory_timer_5_device_sync_samples_{benchmarkRunId}.csv");

        var builder = new StringBuilder();
        builder.AppendLine(SyncSampleDiagnosticCsvRow.Header);
        foreach (SyncSampleDiagnosticCsvRow row in rows)
        {
            builder.AppendLine(row.ToCsv());
        }

        await File.WriteAllTextAsync(
            path,
            builder.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static void AppendSyncAttemptDiagnostics(
        ICollection<SyncSampleDiagnosticCsvRow> rows,
        int trial,
        DateTimeOffset timestampUtc,
        string syncMode,
        DeviceViewModel device,
        int attempt,
        SyncPathDelayProfile calibrationDelay,
        IReadOnlyList<SyncMeasurement> calibrationSamples,
        ClockSyncConsensus calibrationConsensus,
        SyncMeasurement representativeCalibration,
        IReadOnlyList<SyncMeasurement> verificationSamples,
        ClockSyncConsensus verificationConsensus,
        long residualErrorMicroseconds,
        long expectedBiasMicroseconds,
        SyncQualityEvaluation quality)
    {
        HashSet<ulong> selectedCalibrationIds = SelectLowRttSyncIds(
            calibrationSamples,
            ConsensusLowRttSampleCount);
        HashSet<ulong> selectedVerificationIds = SelectLowRttSyncIds(
            verificationSamples,
            ConsensusLowRttSampleCount);
        SyncMeasurement representativeVerification = verificationSamples.First(item =>
            item.Sample == verificationConsensus.RepresentativeSample);
        string outcome = quality.IsAccepted ? "ACCEPTED" : "REJECTED_QUALITY";
        string failureKind = quality.IsAccepted ? string.Empty : "QUALITY_GATE";
        string failureMessage = quality.IsAccepted
            ? string.Empty
            : $"deviation={quality.DeviationMicroseconds} us, threshold=±{quality.ThresholdMicroseconds} us";

        AppendSyncPhaseDiagnostics(
            rows,
            trial,
            timestampUtc,
            syncMode,
            device,
            attempt,
            "CALIBRATION",
            calibrationDelay,
            calibrationSamples,
            selectedCalibrationIds,
            representativeCalibration.SyncId,
            calibrationConsensus,
            residualErrorMicroseconds,
            expectedBiasMicroseconds,
            quality.DeviationMicroseconds,
            quality.ThresholdMicroseconds,
            quality.IsAccepted,
            outcome,
            failureKind,
            failureMessage);

        AppendSyncPhaseDiagnostics(
            rows,
            trial,
            timestampUtc,
            syncMode,
            device,
            attempt,
            "VERIFICATION",
            SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None),
            verificationSamples,
            selectedVerificationIds,
            representativeVerification.SyncId,
            verificationConsensus,
            residualErrorMicroseconds,
            expectedBiasMicroseconds,
            quality.DeviationMicroseconds,
            quality.ThresholdMicroseconds,
            quality.IsAccepted,
            outcome,
            failureKind,
            failureMessage);
    }

    private static void AppendIncompleteSyncAttemptDiagnostics(
        ICollection<SyncSampleDiagnosticCsvRow> rows,
        int trial,
        DateTimeOffset timestampUtc,
        string syncMode,
        DeviceViewModel device,
        int attempt,
        SyncPathDelayProfile calibrationDelay,
        IReadOnlyList<SyncMeasurement> calibrationSamples,
        ClockSyncConsensus? calibrationConsensus,
        IReadOnlyList<SyncMeasurement> verificationSamples,
        ClockSyncConsensus? verificationConsensus,
        string activePhase,
        int activeSampleIndex,
        ulong? activeSyncId,
        Exception exception)
    {
        string failureKind = ClassifySyncFailure(exception);
        string outcome = exception is OperationCanceledException ? "CANCELLED" : "ERROR";
        string failureMessage = exception.Message;

        if (calibrationSamples.Count > 0)
        {
            HashSet<ulong> selectedCalibrationIds = SelectLowRttSyncIds(
                calibrationSamples,
                Math.Min(ConsensusLowRttSampleCount, calibrationSamples.Count));
            ulong? representativeCalibrationId = FindRepresentativeSyncId(
                calibrationSamples,
                calibrationConsensus);
            AppendSyncPhaseDiagnostics(
                rows,
                trial,
                timestampUtc,
                syncMode,
                device,
                attempt,
                "CALIBRATION",
                calibrationDelay,
                calibrationSamples,
                selectedCalibrationIds,
                representativeCalibrationId,
                calibrationConsensus,
                null,
                calibrationDelay.ExpectedOffsetBiasMicroseconds,
                null,
                SyncQualityThresholdMicroseconds,
                null,
                outcome,
                failureKind,
                failureMessage);
        }

        if (verificationSamples.Count > 0)
        {
            HashSet<ulong> selectedVerificationIds = SelectLowRttSyncIds(
                verificationSamples,
                Math.Min(ConsensusLowRttSampleCount, verificationSamples.Count));
            ulong? representativeVerificationId = FindRepresentativeSyncId(
                verificationSamples,
                verificationConsensus);
            AppendSyncPhaseDiagnostics(
                rows,
                trial,
                timestampUtc,
                syncMode,
                device,
                attempt,
                "VERIFICATION",
                SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None),
                verificationSamples,
                selectedVerificationIds,
                representativeVerificationId,
                verificationConsensus,
                null,
                calibrationDelay.ExpectedOffsetBiasMicroseconds,
                null,
                SyncQualityThresholdMicroseconds,
                null,
                outcome,
                failureKind,
                failureMessage);
        }

        SyncPathDelayProfile markerDelay = activePhase.StartsWith("VERIFICATION", StringComparison.Ordinal)
            ? SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None)
            : calibrationDelay;
        rows.Add(SyncSampleDiagnosticCsvRow.CreateFailureMarker(
            trial,
            timestampUtc,
            syncMode,
            device.DeviceId,
            GetHardwareFamily(device.DeviceId),
            device.IpAddress,
            attempt,
            activePhase,
            activeSampleIndex,
            activeSyncId,
            markerDelay.MasterToDeviceDelayMilliseconds,
            markerDelay.DeviceToMasterDelayMilliseconds,
            outcome,
            failureKind,
            failureMessage));
    }

    private static ulong? FindRepresentativeSyncId(
        IReadOnlyList<SyncMeasurement> samples,
        ClockSyncConsensus? consensus)
    {
        if (!consensus.HasValue)
        {
            return null;
        }

        SyncMeasurement? representative = samples.FirstOrDefault(item =>
            item.Sample == consensus.Value.RepresentativeSample);
        return representative?.SyncId;
    }

    private static string ClassifySyncFailure(Exception exception) => exception switch
    {
        TimeoutException => "TIMEOUT",
        OperationCanceledException => "CANCELLED",
        System.Net.Sockets.SocketException => "TRANSPORT_ERROR",
        IOException => "IO_ERROR",
        InvalidOperationException when exception.Message.Contains("At least ", StringComparison.Ordinal) =>
            "INSUFFICIENT_SAMPLES",
        InvalidOperationException when
            exception.Message.Contains("SYNC_REPLY", StringComparison.Ordinal) ||
            exception.Message.Contains("SYNC_APPLIED", StringComparison.Ordinal) ||
            exception.Message.Contains("synchronization timing sample", StringComparison.Ordinal) =>
            "PROTOCOL_ERROR",
        InvalidOperationException => "INVALID_OPERATION",
        _ => "ERROR",
    };

    private static HashSet<ulong> SelectLowRttSyncIds(
        IReadOnlyList<SyncMeasurement> samples,
        int lowRttSampleCount)
    {
        if (lowRttSampleCount <= 0)
        {
            return [];
        }

        return samples
            .Where(item => item.Sample.IsValid)
            .OrderBy(item => item.Sample.NetworkRttMicroseconds)
            .ThenBy(item => item.Sample.MasterMinusLocalOffsetMicroseconds)
            .Take(lowRttSampleCount)
            .Select(item => item.SyncId)
            .ToHashSet();
    }

    private static void AppendSyncPhaseDiagnostics(
        ICollection<SyncSampleDiagnosticCsvRow> rows,
        int trial,
        DateTimeOffset timestampUtc,
        string syncMode,
        DeviceViewModel device,
        int attempt,
        string phase,
        SyncPathDelayProfile pathDelay,
        IReadOnlyList<SyncMeasurement> samples,
        IReadOnlySet<ulong> selectedLowRttSyncIds,
        ulong? representativeSyncId,
        ClockSyncConsensus? consensus,
        long? residualErrorMicroseconds,
        long? expectedBiasMicroseconds,
        long? deviationMicroseconds,
        long? thresholdMicroseconds,
        bool? attemptAccepted,
        string attemptOutcome,
        string failureKind,
        string failureMessage)
    {
        for (int index = 0; index < samples.Count; index++)
        {
            SyncMeasurement measurement = samples[index];
            ClockSyncSample sample = measurement.Sample;
            rows.Add(new SyncSampleDiagnosticCsvRow(
                trial,
                timestampUtc,
                syncMode,
                device.DeviceId,
                GetHardwareFamily(device.DeviceId),
                device.IpAddress,
                attempt,
                phase,
                index + 1,
                measurement.SyncId,
                pathDelay.MasterToDeviceDelayMilliseconds,
                pathDelay.DeviceToMasterDelayMilliseconds,
                sample.MasterT1Microseconds,
                sample.DeviceT2Microseconds,
                sample.DeviceT3Microseconds,
                sample.MasterT4Microseconds,
                sample.NetworkRttMicroseconds,
                sample.MasterMinusLocalOffsetMicroseconds,
                selectedLowRttSyncIds.Contains(measurement.SyncId),
                representativeSyncId.HasValue && measurement.SyncId == representativeSyncId.Value,
                consensus?.MasterMinusLocalOffsetMicroseconds,
                consensus?.BestRttMicroseconds,
                residualErrorMicroseconds,
                expectedBiasMicroseconds,
                deviationMicroseconds,
                thresholdMicroseconds,
                attemptAccepted,
                attemptOutcome,
                failureKind,
                failureMessage));
        }
    }

    private static string BuildBenchmarkSummary(
        IReadOnlyList<BenchmarkTrialSummary> summaries,
        int requestedTrials,
        bool cancelled,
        string csvPath,
        string diagnosticsCsvPath)
    {
        BenchmarkTrialSummary[] successful = summaries.Where(item => item.Success).ToArray();
        string prefix = cancelled ? "Benchmark stopped." : "Benchmark complete.";
        if (successful.Length == 0)
        {
            return $"{prefix} 0/{requestedTrials} successful trials. " +
                   $"Summary CSV: {csvPath}; sync-sample CSV: {diagnosticsCsvPath}";
        }

        long[] orderedSpreads = successful
            .Select(item => item.FleetStartSpreadUs!.Value)
            .OrderBy(value => value)
            .ToArray();
        int p95Index = Math.Clamp((int)Math.Ceiling(orderedSpreads.Length * 0.95) - 1, 0, orderedSpreads.Length - 1);
        double meanStartSpreadMs = orderedSpreads.Average() / 1000d;
        double p95StartSpreadMs = orderedSpreads[p95Index] / 1000d;
        double maxStartSpreadMs = orderedSpreads[^1] / 1000d;
        double meanWorstStartMs = successful.Average(item => item.WorstStartErrorUs!.Value) / 1000d;
        double meanSyncMs = successful.Average(item => item.SyncDurationUs) / 1000d;
        int totalRetries = summaries.Sum(item => item.TotalSyncRetries);
        return $"{prefix} {successful.Length}/{requestedTrials} successful; " +
               $"mean START spread {meanStartSpreadMs:F3} ms, P95 {p95StartSpreadMs:F3} ms, max {maxStartSpreadMs:F3} ms, " +
               $"mean worst |START error| {meanWorstStartMs:F3} ms, mean fleet SYNC {meanSyncMs:F1} ms, " +
               $"quality retries {totalRetries}. Summary CSV: {csvPath}; sync-sample CSV: {diagnosticsCsvPath}";
    }

    private async Task SendStartAtAsync()
    {
        if (SelectedNetworkInterface is null || !udpService.IsReady)
        {
            StatusMessage = "Select an active physical network interface before sending a command.";
            return;
        }
        if (Devices.Any(device => !device.IsSynchronized))
        {
            StatusMessage = "Synchronize all five devices before START_AT.";
            return;
        }
        if (!TryDuration(out uint duration)) return;
        if (!await sendLock.WaitAsync(0)) return;

        isSending = true;
        UpdateCanSend();
        try
        {
            ulong commandId = CreateCommandId();
            long targetMasterUs = MasterClock.NowMicroseconds + StartLeadTimeMilliseconds * 1000L;
            var command = new CommandPacket(
                CommandType.StartAt,
                commandId,
                duration,
                0,
                targetMasterUs);
            foreach (DeviceViewModel device in Devices)
            {
                device.MarkPending(commandId);
                device.ClearStartMeasurement();
            }
            StartSynchronizationResult = "Waiting for STARTED telemetry...";
            clock.ArmAt(duration, targetMasterUs);

            // START_AT is deliberately NOT delayed by the SYNC experiment. The
            // experiment isolates clock-offset estimation; every device receives
            // the same absolute target through the normal three-copy broadcast.
            BroadcastAddressResolution resolution = BroadcastAddressResolver.Resolve(
                SelectedNetworkInterface,
                ManualBroadcastOverride);
            if (!resolution.IsValid)
            {
                StatusMessage = resolution.Error!;
                return;
            }
            StatusMessage =
                $"Broadcasting START_AT={targetMasterUs} µs three times to {resolution.Address}; no START-path artificial delay...";
            await udpService.SendCommandAsync(command, resolution.Address!);
            StatusMessage =
                $"START_AT sent three times; all five devices target {targetMasterUs} µs Master time.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"START_AT transmission failed: {exception.Message}";
        }
        finally
        {
            isSending = false;
            UpdateCanSend();
            sendLock.Release();
        }
    }

    private async Task SendResetInternalAsync()
    {
        if (SelectedNetworkInterface is null || !udpService.IsReady)
        {
            StatusMessage = "Select an active physical network interface before sending a command.";
            return;
        }
        if (!TryDuration(out uint duration)) return;
        BroadcastAddressResolution resolution = BroadcastAddressResolver.Resolve(
            SelectedNetworkInterface,
            ManualBroadcastOverride);
        if (!resolution.IsValid)
        {
            StatusMessage = resolution.Error!;
            return;
        }
        if (!await sendLock.WaitAsync(0)) return;

        isSending = true;
        UpdateCanSend();
        try
        {
            ulong commandId = CreateCommandId();
            var command = new CommandPacket(CommandType.Reset, commandId, duration, 0);
            foreach (DeviceViewModel device in Devices)
            {
                device.MarkPending(commandId);
                device.ClearStartMeasurement();
            }
            clock.Reset(duration);
            StartSynchronizationResult = "Not measured";
            RefreshClockProperties();
            StatusMessage = $"Sending RESET {FactoryProtocol.FormatCommandId(commandId)}...";
            await udpService.SendCommandAsync(command, resolution.Address!);
            StatusMessage = "RESET sent three times.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"RESET transmission failed: {exception.Message}";
        }
        finally
        {
            isSending = false;
            UpdateCanSend();
            sendLock.Release();
        }
    }

    private bool TryGetReadyDevices(out List<(DeviceViewModel Device, IPAddress Address)> readyDevices)
    {
        readyDevices = [];
        if (SelectedNetworkInterface is null || !udpService.IsReady)
        {
            StatusMessage = "Select an active physical network interface first.";
            return false;
        }

        foreach (DeviceViewModel device in Devices)
        {
            if (!device.TryGetIpAddress(out IPAddress? address) || address is null)
            {
                readyDevices.Clear();
                StatusMessage = $"Wait until all five devices are discovered. {device.DeviceId} does not have a valid IP address yet.";
                return false;
            }
            readyDevices.Add((device, address));
        }
        return true;
    }

    private static string BuildSyncSamplingModeLabel(int modeIndex) => modeIndex switch
    {
        0 => "8+8_BASELINE",
        1 => "4+4_CANDIDATE",
        _ => "UNKNOWN",
    };

    private static string BuildReceiveTimestampModeLabel(int modeIndex) => modeIndex switch
    {
        0 => "ASYNC_AWAIT",
        1 => "BLOCKING_THREAD_T4",
        _ => "UNKNOWN",
    };

    private static string BuildSyncPathDelayModeLabel(int modeIndex) => modeIndex switch
    {
        0 => "NONE (0/0 ms)",
        1 => "SYMMETRIC (250/250 ms)",
        2 => "ASYMMETRIC (250/0 ms)",
        _ => "UNKNOWN",
    };

    private string BuildSyncPathDelayDescription()
    {
        SyncPathDelayMode mode = (SyncPathDelayMode)Math.Clamp(SyncPathDelayModeIndex, 0, 2);
        SyncPathDelayProfile profile = SyncPathDelayExperiment.GetProfile(mode);
        string expectedBias = mode switch
        {
            SyncPathDelayMode.None => "Expected artificial offset bias: 0 ms.",
            SyncPathDelayMode.Symmetric250Milliseconds =>
                "Expected artificial offset bias: ~0 ms; calibration RTT increases by ~500 ms.",
            SyncPathDelayMode.AsymmetricForward250Milliseconds =>
                "Expected applied-offset bias: ~-125 ms; ESP02 physical START should appear ~125 ms late when checked with delay-free verification.",
            _ => string.Empty,
        };
        return $"ESP01/ESP03/ESP04/ESP05 are 0/0 ms controls. ESP02 calibration is {profile.MasterToDeviceDelayMilliseconds}/{profile.DeviceToMasterDelayMilliseconds} ms (Master→ESP / ESP→Master). Verification is always 0/0 ms for all five devices. {expectedBias}";
    }

    private bool TryDuration(out uint duration)
    {
        duration = 0;
        if (double.IsNaN(DurationInput) || DurationInput != Math.Truncate(DurationInput) ||
            DurationInput < FactoryProtocol.MinimumDurationSeconds ||
            DurationInput > FactoryProtocol.MaximumDurationSeconds)
        {
            StatusMessage = "Duration must be a whole number from 1 through 86400 seconds.";
            return false;
        }
        duration = (uint)DurationInput;
        return true;
    }

    private void RefreshStartSynchronizationResult()
    {
        long[] errors = Devices
            .Where(device => device.LastStartErrorMicroseconds.HasValue)
            .Select(device => device.LastStartErrorMicroseconds!.Value)
            .ToArray();
        if (errors.Length != Devices.Length)
        {
            StartSynchronizationResult = $"Waiting for STARTED telemetry from all five devices ({errors.Length}/5 received)...";
            return;
        }

        long worst = errors.Max(error => Math.Abs(error));
        long spread = errors.Max() - errors.Min();
        StartSynchronizationResult =
            $"Verification-corrected START: worst |error| ≈ {worst / 1000d:F3} ms; 5-device start spread ≈ {spread / 1000d:F3} ms";
    }

    private void RefreshSynchronizationResolution()
    {
        long[] errors = Devices
            .Where(device => device.IsSynchronized && device.ResidualErrorMicroseconds.HasValue)
            .Select(device => device.ResidualErrorMicroseconds!.Value)
            .ToArray();
        if (errors.Length != Devices.Length)
        {
            SynchronizationResolution = $"All five devices must complete delay-free verification ({errors.Length}/5 complete).";
            return;
        }

        long worst = errors.Max(error => Math.Abs(error));
        long spread = errors.Max() - errors.Min();
        SynchronizationResolution =
            $"Delay-free verify: worst |device−Master| ≈ {worst / 1000d:F3} ms; 5-device clock spread ≈ {spread / 1000d:F3} ms";
    }

    private void NetworkSelectionManager_StateChanged(
        object? sender,
        NetworkInterfaceSelectionChangedEventArgs e)
    {
        if (dispatcherQueue.HasThreadAccess)
        {
            ApplyNetworkState(e.State);
        }
        else
        {
            dispatcherQueue.TryEnqueue(() => ApplyNetworkState(e.State));
        }
    }

    private void ApplyNetworkState(NetworkInterfaceSelectionState state)
    {
        if (disposed) return;

        ControllerNetworkInterface? previous = selectedNetworkInterface;
        NetworkInterfaces = state.AvailableInterfaces;
        applyingNetworkState = true;
        if (!ReferenceEquals(selectedNetworkInterface, state.SelectedInterface))
        {
            selectedNetworkInterface = state.SelectedInterface;
            OnPropertyChanged(nameof(SelectedNetworkInterface));
        }
        applyingNetworkState = false;

        bool selectionChanged = previous != selectedNetworkInterface;
        if (selectionChanged || (selectedNetworkInterface is not null && !udpService.IsReady))
        {
            statusDiscovery.Stop();
            foreach (DeviceViewModel device in Devices)
            {
                device.MarkOffline();
            }
            SynchronizationResolution = "Not measured";
            try
            {
                udpService.ChangeSelection(selectedNetworkInterface);
                if (selectedNetworkInterface is not null)
                {
                    foreach (DeviceViewModel device in Devices)
                    {
                        device.BeginSearching();
                    }
                    if (!timingQuietPeriodActive)
                    {
                        statusDiscovery.StartOrRestart(
                            selectedNetworkInterface,
                            () => Volatile.Read(ref manualBroadcastOverride));
                    }
                }
                StatusMessage = state.Message;
            }
            catch (Exception exception)
            {
                udpService.ChangeSelection(null);
                StatusMessage = $"{state.Message} Could not bind UDP to the selected address: {exception.Message}";
            }
        }
        else
        {
            StatusMessage = state.Message;
        }

        NetworkSummary = state.Message;
        RefreshNetworkDetails();
        UpdateCanSend();
    }

    private async Task EnterTimingQuietPeriodAsync(CancellationToken cancellationToken)
    {
        timingQuietPeriodActive = true;

        // Stop the periodic STATUS_REQUEST loop and wait until its current iteration
        // has fully exited. Then leave a short drain interval so replies to a STATUS
        // request that was already on the air cannot overlap the first SYNC sample.
        await statusDiscovery.StopAndWaitAsync();
        await Task.Delay(StatusDiscoveryDrainMilliseconds, cancellationToken);
    }

    private void ExitTimingQuietPeriod()
    {
        try
        {
            ControllerNetworkInterface? selection = SelectedNetworkInterface;
            if (!disposed && selection is not null && udpService.IsReady)
            {
                statusDiscovery.StartOrRestart(
                    selection,
                    () => Volatile.Read(ref manualBroadcastOverride));
            }
        }
        finally
        {
            timingQuietPeriodActive = false;
        }
    }

    private void RefreshNetworkDetails()
    {
        ControllerNetworkInterface? selection = SelectedNetworkInterface;
        SelectedLocalAddress = selection?.LocalAddress.ToString() ?? "Unavailable";
        SelectedSubnetMask = selection is null
            ? "Unavailable"
            : $"{selection.SubnetMask} (/{selection.PrefixLength})";
        CalculatedBroadcastAddress = selection?.BroadcastAddress.ToString() ?? "Unavailable";
        BroadcastAddressResolution resolution = BroadcastAddressResolver.Resolve(
            selection,
            ManualBroadcastOverride);
        EffectiveBroadcastAddress = resolution.Address?.ToString() ??
            (string.IsNullOrWhiteSpace(ManualBroadcastOverride) ? "Unavailable" : "Invalid override");
    }

    private void UpdateCanSend() =>
        CanSend = !isSending && SelectedNetworkInterface is not null && udpService.IsReady;

    private void UdpService_PacketReceived(object? sender, PacketReceivedEventArgs e)
    {
        dispatcherQueue.TryEnqueue(() =>
        {
            if (e.NetworkInterface != SelectedNetworkInterface) return;

            DeviceViewModel? device = e.Packet.DeviceId switch
            {
                "ESP01" => Esp01,
                "ESP02" => Esp02,
                "ESP03" => Esp03,
                "ESP04" => Esp04,
                "ESP05" => Esp05,
                _ => null,
            };
            if (device is null)
            {
                StatusMessage = $"Ignored response from unconfigured device {e.Packet.DeviceId}.";
                return;
            }
            switch (e.Packet)
            {
                case AckPacket ack:
                    device.Apply(ack, e.RemoteEndPoint);
                    break;
                case StatusPacket status:
                    device.Apply(status, e.RemoteEndPoint);
                    break;
                case StartedPacket started:
                    device.Apply(started, e.RemoteEndPoint);
                    RefreshStartSynchronizationResult();
                    break;
            }
        });
    }

    private void UdpService_ReceiveError(object? sender, string message) =>
        dispatcherQueue.TryEnqueue(() => StatusMessage = message);

    private void StatusDiscovery_DiscoveryError(object? sender, string message) =>
        dispatcherQueue.TryEnqueue(() => StatusMessage = message);

    private void UiTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        clock.Update();
        RefreshClockProperties();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!timingQuietPeriodActive)
        {
            foreach (DeviceViewModel device in Devices)
            {
                device.RefreshOnlineStatus(now);
            }
        }
        long masterNowUs = MasterClock.NowMicroseconds;
        MasterTime = $"{masterNowUs:N0} µs";
        foreach (DeviceViewModel device in Devices)
        {
            device.UpdateEstimatedMasterTime(masterNowUs);
        }
    }

    private void RefreshClockProperties()
    {
        ControllerRemaining = clock.RemainingSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ControllerState = clock.State.ToString().ToUpperInvariant();
    }

    private static ulong CreateCommandId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong result;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            result = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        } while (result == 0);
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        benchmarkCancellation?.Cancel();
        benchmarkCancellation?.Dispose();
        benchmarkCancellation = null;
        uiTimer.Stop();
        statusDiscovery.DiscoveryError -= StatusDiscovery_DiscoveryError;
        statusDiscovery.Dispose();
        networkSelectionManager.StateChanged -= NetworkSelectionManager_StateChanged;
        networkSelectionManager.Dispose();
        udpService.Dispose();
        sendLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record BenchmarkTrialSummary(
        int Trial,
        long SyncDurationUs,
        long? FleetClockSpreadUs,
        long? FleetStartSpreadUs,
        long? WorstClockErrorUs,
        long? WorstStartErrorUs,
        int TotalSyncRetries,
        bool Success);

    private sealed record BenchmarkCsvRow(
        int Trial,
        DateTimeOffset TimestampUtc,
        bool Success,
        string Failure,
        string SyncMode,
        string Device,
        string Hardware,
        string IpAddress,
        long? BestSyncRttUs,
        long? VerifyRttUs,
        long? AppliedOffsetUs,
        long? VerificationOffsetUs,
        long? ClockErrorUs,
        int SyncAttempts,
        int SyncRetries,
        long? InitialClockErrorUs,
        long? ExpectedSyncBiasUs,
        long? SyncQualityDeviationUs,
        long? SyncQualityThresholdUs,
        bool SyncQualityAccepted,
        int? RssiDbm,
        int? WifiChannel,
        string Bssid,
        ulong? CommandId,
        long? TargetMasterUs,
        long? VerifiedStartMasterUs,
        long? StartErrorUs,
        long? SchedulerLatenessUs,
        long SyncDurationUs,
        long? WorstClockErrorUs,
        long? FleetClockSpreadUs,
        long? WorstStartErrorUs,
        long? FleetStartSpreadUs)
    {
        public static BenchmarkCsvRow CreateSuccess(
            int trial,
            DateTimeOffset timestampUtc,
            string syncMode,
            DeviceViewModel device,
            ulong commandId,
            long targetMasterUs,
            long syncDurationUs,
            long worstClockErrorUs,
            long fleetClockSpreadUs,
            long worstStartErrorUs,
            long fleetStartSpreadUs) =>
            new(
                trial,
                timestampUtc,
                true,
                string.Empty,
                syncMode,
                device.DeviceId,
                GetHardwareFamily(device.DeviceId),
                device.IpAddress,
                device.BestRttMicroseconds,
                device.VerificationRttMicroseconds,
                device.AppliedOffsetMicroseconds,
                device.VerificationOffsetMicroseconds,
                device.ResidualErrorMicroseconds,
                device.SyncAttemptCount,
                device.SyncRetryCount,
                device.InitialResidualErrorMicroseconds,
                device.ExpectedSyncBiasMicroseconds,
                device.SyncQualityDeviationMicroseconds,
                device.SyncQualityThresholdMicroseconds,
                device.SyncQualityAccepted,
                device.RssiDbm,
                device.WifiChannel,
                device.BssidValue ?? string.Empty,
                commandId,
                targetMasterUs,
                device.LastActualStartMasterMicroseconds,
                device.LastStartErrorMicroseconds,
                device.LastSchedulerLatenessMicroseconds,
                syncDurationUs,
                worstClockErrorUs,
                fleetClockSpreadUs,
                worstStartErrorUs,
                fleetStartSpreadUs);

        public static BenchmarkCsvRow CreateFailure(
            int trial,
            DateTimeOffset timestampUtc,
            string syncMode,
            DeviceViewModel device,
            ulong commandId,
            long targetMasterUs,
            long syncDurationUs,
            string failure) =>
            new(
                trial,
                timestampUtc,
                false,
                failure,
                syncMode,
                device.DeviceId,
                GetHardwareFamily(device.DeviceId),
                device.IpAddress,
                device.BestRttMicroseconds,
                device.VerificationRttMicroseconds,
                device.AppliedOffsetMicroseconds,
                device.VerificationOffsetMicroseconds,
                device.ResidualErrorMicroseconds,
                device.SyncAttemptCount,
                device.SyncRetryCount,
                device.InitialResidualErrorMicroseconds,
                device.ExpectedSyncBiasMicroseconds,
                device.SyncQualityDeviationMicroseconds,
                device.SyncQualityThresholdMicroseconds,
                device.SyncQualityAccepted,
                device.RssiDbm,
                device.WifiChannel,
                device.BssidValue ?? string.Empty,
                commandId == 0 ? null : commandId,
                targetMasterUs == 0 ? null : targetMasterUs,
                device.LastActualStartMasterMicroseconds,
                device.LastStartErrorMicroseconds,
                device.LastSchedulerLatenessMicroseconds,
                syncDurationUs,
                null,
                null,
                null,
                null);

        public string ToCsv() => string.Join(
            ",",
            Trial.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Csv(TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            Success ? "1" : "0",
            Csv(Failure),
            Csv(SyncMode),
            Csv(Device),
            Csv(Hardware),
            Csv(IpAddress),
            Number(BestSyncRttUs),
            Number(VerifyRttUs),
            Number(AppliedOffsetUs),
            Number(VerificationOffsetUs),
            Number(ClockErrorUs),
            SyncAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SyncRetries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Number(InitialClockErrorUs),
            Number(ExpectedSyncBiasUs),
            Number(SyncQualityDeviationUs),
            Number(SyncQualityThresholdUs),
            SyncQualityAccepted ? "1" : "0",
            Number(RssiDbm),
            Number(WifiChannel),
            Csv(Bssid),
            CommandId.HasValue ? FactoryProtocol.FormatCommandId(CommandId.Value) : string.Empty,
            Number(TargetMasterUs),
            Number(VerifiedStartMasterUs),
            Number(StartErrorUs),
            Number(SchedulerLatenessUs),
            SyncDurationUs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Number(WorstClockErrorUs),
            Number(FleetClockSpreadUs),
            Number(WorstStartErrorUs),
            Number(FleetStartSpreadUs));

        private static string Number(long? value) => value?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        private static string Number(int? value) => value?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        private static string Csv(string value)
        {
            if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            {
                return value;
            }
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }

    private sealed record SyncSampleDiagnosticCsvRow(
        int Trial,
        DateTimeOffset TimestampUtc,
        string SyncMode,
        string Device,
        string Hardware,
        string IpAddress,
        int Attempt,
        string Phase,
        int SampleIndex,
        ulong? SyncId,
        int PathForwardDelayMs,
        int PathReverseDelayMs,
        long? MasterT1Us,
        long? DeviceT2Us,
        long? DeviceT3Us,
        long? MasterT4Us,
        long? RttUs,
        long? OffsetUs,
        bool SelectedLowRtt,
        bool ConsensusRepresentative,
        long? ConsensusOffsetUs,
        long? BestRttUs,
        long? ResidualErrorUs,
        long? ExpectedBiasUs,
        long? DeviationUs,
        long? ThresholdUs,
        bool? AttemptAccepted,
        string AttemptOutcome,
        string FailureKind,
        string FailureMessage)
    {
        public const string EstimatorName = "LOW_RTT_INV_RTT2_BEST3";

        public const string Header =
            "Trial,TimestampUtc,SyncMode,Device,Hardware,IpAddress,Attempt,Phase,SampleIndex,SyncId," +
            "PathForwardDelayMs,PathReverseDelayMs,MasterT1Us,DeviceT2Us,DeviceT3Us,MasterT4Us," +
            "RttUs,OffsetUs,SelectedLowRtt,ConsensusRepresentative,Estimator,ConsensusOffsetUs,BestRttUs," +
            "ResidualErrorUs,ExpectedBiasUs,DeviationUs,ThresholdUs,AttemptAccepted,AttemptOutcome," +
            "FailureKind,FailureMessage";

        public static SyncSampleDiagnosticCsvRow CreateFailureMarker(
            int trial,
            DateTimeOffset timestampUtc,
            string syncMode,
            string device,
            string hardware,
            string ipAddress,
            int attempt,
            string phase,
            int sampleIndex,
            ulong? syncId,
            int pathForwardDelayMs,
            int pathReverseDelayMs,
            string attemptOutcome,
            string failureKind,
            string failureMessage) =>
            new(
                trial,
                timestampUtc,
                syncMode,
                device,
                hardware,
                ipAddress,
                attempt,
                phase,
                sampleIndex,
                syncId,
                pathForwardDelayMs,
                pathReverseDelayMs,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                null,
                null,
                null,
                null,
                null,
                SyncQualityThresholdMicroseconds,
                null,
                attemptOutcome,
                failureKind,
                failureMessage);

        public string ToCsv() => string.Join(
            ",",
            Trial.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Csv(TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            Csv(SyncMode),
            Csv(Device),
            Csv(Hardware),
            Csv(IpAddress),
            Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Csv(Phase),
            SampleIndex == 0
                ? string.Empty
                : SampleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SyncId.HasValue ? FactoryProtocol.FormatCommandId(SyncId.Value) : string.Empty,
            PathForwardDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PathReverseDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Number(MasterT1Us),
            Number(DeviceT2Us),
            Number(DeviceT3Us),
            Number(MasterT4Us),
            Number(RttUs),
            Number(OffsetUs),
            SelectedLowRtt ? "1" : "0",
            ConsensusRepresentative ? "1" : "0",
            EstimatorName,
            Number(ConsensusOffsetUs),
            Number(BestRttUs),
            Number(ResidualErrorUs),
            Number(ExpectedBiasUs),
            Number(DeviationUs),
            Number(ThresholdUs),
            AttemptAccepted.HasValue ? (AttemptAccepted.Value ? "1" : "0") : string.Empty,
            Csv(AttemptOutcome),
            Csv(FailureKind),
            Csv(FailureMessage));

        private static string Number(long? value) => value?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        private static string Csv(string value)
        {
            if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            {
                return value;
            }
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }

    private sealed record SyncMeasurement(ulong SyncId, ClockSyncSample Sample);
}
