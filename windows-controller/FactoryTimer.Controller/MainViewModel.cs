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
    private const int StartLeadTimeMilliseconds = 2000; // benchmark only
    private const long ProductionStartLeadMicroseconds = 5_000_000;
    private const int ArmRetryIntervalMilliseconds = 80;
    private const int FinalBarrierRetryCount = 3;
    private const int FinalBarrierRetryWaitMilliseconds = 150;
    private const int AbortResetRetryWaitMilliseconds = 100;
    private const long ManualStartAtTargetLeadMicroseconds = 30_000_000;
    private const long ManualStartAtCutoffLeadMicroseconds = 2_000_000;
    private const long ManualStartAtStartedTelemetryGraceMicroseconds = 2_000_000;
    private const long ManualStartAtMaximumSchedulerLatenessMicroseconds = 1;
    private const long ManualStartAtMinimumSendGapMicroseconds = 1_000_000;
    private static readonly TimeSpan FreshStatusWaitTimeout = TimeSpan.FromSeconds(2);
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
    private readonly ParticipantReservationManager reservationManager = new();
    private ArmAcknowledgementTracker? activeArmAcknowledgementTracker;
    private ResetAcknowledgementTracker? activeAbortResetAcknowledgementTracker;
    private IReadOnlyList<ControllerNetworkInterface> networkInterfaces = [];
    private ControllerNetworkInterface? selectedNetworkInterface;
    private double durationMinutesInput = 0;
    private double durationSecondsInput = 20;
    private double brightnessPercentInput = 50;
    private string brightnessStatus = "Runtime brightness: not changed by controller";
    private string manualBroadcastOverride = string.Empty;
    private string controllerRemaining = "00:20";
    private string controllerState = "READY";
    private string masterTime = "0 µs";
    private string synchronizationResolution = "Not measured";
    private string startSynchronizationResult = "Not measured";
    private string staggeredTestResult = "Not prepared";
    private string manualStartAtCountdown = "Not prepared";
    private ManualStartAtSession? manualStartAtSession;
    private StartTelemetryContext? productionStartTelemetryContext;
    private bool isPreparingManualStartAtTest;
    private string networkSummary = "Detecting usable physical network interfaces...";
    private string selectedLocalAddress = "Unavailable";
    private string selectedSubnetMask = "Unavailable";
    private string calculatedBroadcastAddress = "Unavailable";
    private string effectiveBroadcastAddress = "Unavailable";
    private string statusMessage = "Ready";
    private string benchmarkProgress = "Not run";
    private string benchmarkCsvPath = "—";
    private string benchmarkDiagnosticsCsvPath = "—";
    private string analyzerCsvPath = "—";
    private string analyzerStatus = "Disabled";
    private string analyzerComPort = string.Empty;
    private bool analyzerEnabled;
    private double benchmarkTrialsInput = 10;
    private int syncPathDelayModeIndex;
    private int syncPathDelayTargetIndex = 1; // ESP02 remains the default experiment target.
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
        Devices =
        [
            Esp01, Esp02, Esp03, Esp04, Esp05,
            Esp06, Esp07, Esp08, Esp09, Esp10,
            Esp11, Esp12, Esp13, Esp14, Esp15,
        ];

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
    // Preserve the existing five-device selection on upgrade. TIMER06-TIMER15
    // are available immediately and can be selected individually or with SELECT ALL.
    public DeviceViewModel Esp06 { get; } = new("ESP06", false);
    public DeviceViewModel Esp07 { get; } = new("ESP07", false);
    public DeviceViewModel Esp08 { get; } = new("ESP08", false);
    public DeviceViewModel Esp09 { get; } = new("ESP09", false);
    public DeviceViewModel Esp10 { get; } = new("ESP10", false);
    public DeviceViewModel Esp11 { get; } = new("ESP11", false);
    public DeviceViewModel Esp12 { get; } = new("ESP12", false);
    public DeviceViewModel Esp13 { get; } = new("ESP13", false);
    public DeviceViewModel Esp14 { get; } = new("ESP14", false);
    public DeviceViewModel Esp15 { get; } = new("ESP15", false);
    public DeviceViewModel[] Devices { get; }
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
    public double DurationMinutesInput
    {
        get => durationMinutesInput;
        set => SetProperty(ref durationMinutesInput, value);
    }
    public double DurationSecondsInput
    {
        get => durationSecondsInput;
        set => SetProperty(ref durationSecondsInput, value);
    }
    public double BrightnessPercentInput
    {
        get => brightnessPercentInput;
        set => SetProperty(ref brightnessPercentInput, Math.Clamp(value, 0, 100));
    }
    public string BrightnessStatus
    {
        get => brightnessStatus;
        private set => SetProperty(ref brightnessStatus, value);
    }
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
    public string StaggeredTestResult { get => staggeredTestResult; private set => SetProperty(ref staggeredTestResult, value); }
    public string ManualStartAtCountdown { get => manualStartAtCountdown; private set => SetProperty(ref manualStartAtCountdown, value); }
    public bool CanManualSendEsp01 => CanSendManualStartAtTo("ESP01");
    public bool CanManualSendEsp02 => CanSendManualStartAtTo("ESP02");
    public bool CanManualSendEsp03 => CanSendManualStartAtTo("ESP03");
    public bool CanManualSendEsp04 => CanSendManualStartAtTo("ESP04");
    public bool CanManualSendEsp05 => CanSendManualStartAtTo("ESP05");
    public bool CanCancelManualStartAtTest => manualStartAtSession is not null && !isSending;
    public bool HasManualStartAtActivity => isPreparingManualStartAtTest || manualStartAtSession is not null;

    public void ReportCloseBlockedByManualStartAtActivity()
    {
        if (isPreparingManualStartAtTest || isSending)
        {
            StatusMessage =
                "Close blocked: a manual START_AT operation is still running. Wait for it to finish; if a session is then live, use CANCEL / RESET before closing.";
            return;
        }

        StatusMessage =
            "Close blocked: a manual START_AT session is live. Use CANCEL / RESET first so the frozen participant set is reset before the UDP socket is closed.";
    }
    public string NetworkSummary { get => networkSummary; private set => SetProperty(ref networkSummary, value); }
    public string SelectedLocalAddress { get => selectedLocalAddress; private set => SetProperty(ref selectedLocalAddress, value); }
    public string SelectedSubnetMask { get => selectedSubnetMask; private set => SetProperty(ref selectedSubnetMask, value); }
    public string CalculatedBroadcastAddress { get => calculatedBroadcastAddress; private set => SetProperty(ref calculatedBroadcastAddress, value); }
    public string EffectiveBroadcastAddress { get => effectiveBroadcastAddress; private set => SetProperty(ref effectiveBroadcastAddress, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string BenchmarkProgress { get => benchmarkProgress; private set => SetProperty(ref benchmarkProgress, value); }
    public string BenchmarkCsvPath { get => benchmarkCsvPath; private set => SetProperty(ref benchmarkCsvPath, value); }
    public string BenchmarkDiagnosticsCsvPath { get => benchmarkDiagnosticsCsvPath; private set => SetProperty(ref benchmarkDiagnosticsCsvPath, value); }
    public string AnalyzerCsvPath { get => analyzerCsvPath; private set => SetProperty(ref analyzerCsvPath, value); }
    public string AnalyzerStatus { get => analyzerStatus; private set => SetProperty(ref analyzerStatus, value); }
    public string AnalyzerComPort { get => analyzerComPort; set => SetProperty(ref analyzerComPort, value); }
    public bool AnalyzerEnabled
    {
        get => analyzerEnabled;
        set
        {
            if (SetProperty(ref analyzerEnabled, value))
            {
                AnalyzerStatus = value
                    ? "Enabled; analyzer will connect when RUN BENCHMARK starts."
                    : "Disabled";
            }
        }
    }
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

    public int SyncPathDelayTargetIndex
    {
        get => syncPathDelayTargetIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, 4);
            if (SetProperty(ref syncPathDelayTargetIndex, clamped))
            {
                OnPropertyChanged(nameof(SyncPathDelayDescription));
                foreach (DeviceViewModel device in Devices)
                {
                    device.ClearSynchronization();
                    device.ClearStartMeasurement();
                }
                string target = BuildSyncPathDelayTargetLabel(clamped);
                SynchronizationResolution =
                    "SYNC injection target changed; press SYNC CLOCKS again.";
                StartSynchronizationResult =
                    "START_AT requires a fresh synchronization after changing the injection target.";
                StatusMessage =
                    $"SYNC path-delay target changed to {target}. Press SYNC CLOCKS before START_AT.";
            }
        }
    }

    public int SyncPathDelayModeIndex
    {
        get => syncPathDelayModeIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, 5);
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

    public Task PrepareManualStartAtTestAsync() => PrepareManualStartAtTestInternalAsync();

    public Task SendManualStartAtToDeviceAsync(string deviceId) =>
        SendManualStartAtToDeviceInternalAsync(deviceId);

    public Task CancelManualStartAtTestAsync() => CancelManualStartAtTestInternalAsync();

    public Task SendResetAsync() => SendResetInternalAsync();

    public Task SendBrightnessAsync() => SendBrightnessInternalAsync();

    public void SelectAllParticipants()
    {
        if (!CanSend) return;
        foreach (DeviceViewModel device in Devices) device.IsSelected = true;
        StatusMessage = "All 15 timers selected.";
    }

    public void DeselectAllParticipants()
    {
        if (!CanSend) return;
        foreach (DeviceViewModel device in Devices) device.IsSelected = false;
        StatusMessage = "All timers deselected. Select one or more timers before START.";
    }

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
            SyncPathDelayProfile injectedCalibrationDelay =
                SyncPathDelayExperiment.GetProfile((SyncPathDelayMode)SyncPathDelayModeIndex);
            string injectionTargetDeviceId =
                BuildSyncPathDelayTargetLabel(SyncPathDelayTargetIndex);
            SyncSamplingProfile samplingProfile =
                SyncSamplingExperiment.GetProfile((SyncSamplingMode)SyncSamplingModeIndex);

            for (int index = 0; index < readyDevices.Count; index++)
            {
                (DeviceViewModel device, IPAddress address) = readyDevices[index];
                SyncPathDelayProfile calibrationDelay =
                    string.Equals(device.DeviceId, injectionTargetDeviceId, StringComparison.Ordinal)
                        ? injectedCalibrationDelay
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
                $"{injectionTargetDeviceId} calibration={injectedCalibrationDelay.MasterToDeviceDelayMilliseconds}/{injectedCalibrationDelay.DeviceToMasterDelayMilliseconds} ms; " +
                $"all non-target devices and all verification samples=0/0 ms.";
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

        for (int attempt = 1; attempt <= MaxSynchronizationAttempts; attempt++)
        {
            long expectedBiasMicroseconds = calibrationDelay.ExpectedOffsetBiasMicroseconds;
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

                // The quality gate must follow the delay that was actually
                // injected into the exact low-RTT samples used by the offset
                // estimator. This avoids Windows/FreeRTOS scheduling overshoot
                // being misclassified as synchronization error.
                expectedBiasMicroseconds = CalculateActualExpectedBiasMicroseconds(
                    samples,
                    samplingProfile.LowRttSampleCount);

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
                calibrationConsensus.Value.EffectiveMasterEpochMicroseconds,
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
        if (reverseDelayUs > 0 && reply.ActualArtificialReplyDelayMicroseconds == 0)
        {
            throw new InvalidOperationException(
                $"{expectedDeviceId} did not report the measured reverse-path delay. " +
                "Flash the logic-validation firmware with microsecond delay reporting before running reverse-path controls.");
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
        DeviceViewModel? observedDevice = Devices.FirstOrDefault(device =>
            string.Equals(device.DeviceId, expectedDeviceId, StringComparison.Ordinal));
        observedDevice?.ObserveSyncLocalTimestamp(reply.LocalT3Microseconds);
        return new SyncMeasurement(
            syncId,
            sample,
            received.ActualMasterToDeviceArtificialDelayMicroseconds,
            reply.ActualArtificialReplyDelayMicroseconds);
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
        AnalyzerSerialClient? analyzer = null;
        ulong analyzerRunId = ulong.Parse(
            benchmarkRunId.Replace("_", string.Empty, StringComparison.Ordinal),
            System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            BenchmarkCsvPath = "—";
            BenchmarkDiagnosticsCsvPath = "—";
            AnalyzerCsvPath = "—";

            if (AnalyzerEnabled)
            {
                if (string.IsNullOrWhiteSpace(AnalyzerComPort))
                {
                    throw new InvalidOperationException(
                        "ESP32 analyzer is enabled but no COM port is configured.");
                }

                AnalyzerStatus = $"Connecting to ESP32 analyzer on {AnalyzerComPort.Trim()}...";
                analyzer = new AnalyzerSerialClient();
                await analyzer.ConnectAsync(AnalyzerComPort, cancellationToken);
                AnalyzerStatus =
                    $"Connected on {AnalyzerComPort.Trim()}; run ID {analyzerRunId}.";
            }
            SyncPathDelayProfile noDelay =
                SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None);
            SyncPathDelayProfile injectedCalibrationDelay =
                SyncPathDelayExperiment.GetProfile((SyncPathDelayMode)SyncPathDelayModeIndex);
            string injectionTargetDeviceId =
                BuildSyncPathDelayTargetLabel(SyncPathDelayTargetIndex);
            SyncSamplingProfile samplingProfile =
                SyncSamplingExperiment.GetProfile((SyncSamplingMode)SyncSamplingModeIndex);
            string benchmarkSyncMode =
                $"{BuildSyncPathDelayModeLabel(SyncPathDelayModeIndex)} | TARGET={injectionTargetDeviceId} | " +
                $"RX={BuildReceiveTimestampModeLabel(ReceiveTimestampModeIndex)} | " +
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
                bool analyzerTrialBegun = false;

                try
                {
                    if (analyzer is not null)
                    {
                        BenchmarkProgress =
                            $"Trial {trial}/{trialCount}: arming ESP32 analyzer...";
                        await analyzer.BeginTrialAsync(analyzerRunId, trial, cancellationToken);
                        analyzerTrialBegun = true;
                    }

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
                        SyncPathDelayProfile calibrationDelay =
                            string.Equals(device.DeviceId, injectionTargetDeviceId, StringComparison.Ordinal)
                                ? injectedCalibrationDelay
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
                    RefreshStartSynchronizationResult(
                        commandId,
                        Devices.ToArray(),
                        "Benchmark START");

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
                    if (analyzer is not null && analyzerTrialBegun)
                    {
                        try
                        {
                            await analyzer.EndTrialAsync(
                                analyzerRunId,
                                trial,
                                CancellationToken.None);
                        }
                        catch (Exception analyzerException)
                        {
                            AnalyzerStatus =
                                $"Analyzer END/SUMMARY failed on trial {trial}: {analyzerException.Message}";
                        }
                    }

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
        catch (Exception exception)
        {
            cancelled = true;
            BenchmarkProgress = $"Benchmark stopped before completion: {exception.Message}";
            StatusMessage = BenchmarkProgress;
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

            try
            {
                if (analyzer is not null)
                {
                    AnalyzerCsvPath = await analyzer.WriteCsvAsync(benchmarkRunId);
                    AnalyzerStatus = $"Capture saved: {AnalyzerCsvPath}";
                }
            }
            catch (Exception exception)
            {
                AnalyzerCsvPath = $"Analyzer CSV save failed: {exception.Message}";
                AnalyzerStatus = AnalyzerCsvPath;
            }
            finally
            {
                analyzer?.Dispose();
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
            "CommandId,TargetMasterUs,VerifiedStartMasterUs,StartErrorUs,SchedulerLatenessUs,StartedTelemetryReceived," +
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
        long expectedBiasForDiagnostics = calibrationDelay.ExpectedOffsetBiasMicroseconds;
        if (calibrationSamples.Count >= ConsensusLowRttSampleCount)
        {
            expectedBiasForDiagnostics = CalculateActualExpectedBiasMicroseconds(
                calibrationSamples,
                ConsensusLowRttSampleCount);
        }

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
                expectedBiasForDiagnostics,
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
                expectedBiasForDiagnostics,
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

    private static long CalculateActualExpectedBiasMicroseconds(
        IReadOnlyList<SyncMeasurement> samples,
        int lowRttSampleCount)
    {
        SyncMeasurement[] selected = samples
            .Where(item => item.Sample.IsValid)
            .OrderBy(item => item.Sample.NetworkRttMicroseconds)
            .ThenBy(item => item.Sample.MasterMinusLocalOffsetMicroseconds)
            .Take(lowRttSampleCount)
            .ToArray();

        if (selected.Length < lowRttSampleCount)
        {
            throw new InvalidOperationException(
                $"At least {lowRttSampleCount} valid synchronization samples are required to calculate actual injected bias.");
        }

        static double SampleBiasUs(SyncMeasurement measurement) =>
            (measurement.ActualReverseDelayUs - measurement.ActualForwardDelayUs) / 2d;

        long bestRttUs = selected[0].Sample.NetworkRttMicroseconds;
        if (bestRttUs == 0)
        {
            SyncMeasurement[] zeroRtt = selected
                .Where(item => item.Sample.NetworkRttMicroseconds == 0)
                .ToArray();
            return checked((long)Math.Round(
                zeroRtt.Average(SampleBiasUs),
                MidpointRounding.AwayFromZero));
        }

        double weightedBiasSum = 0d;
        double weightSum = 0d;
        foreach (SyncMeasurement measurement in selected)
        {
            double ratio = (double)bestRttUs / measurement.Sample.NetworkRttMicroseconds;
            double weight = ratio * ratio;
            weightedBiasSum += weight * SampleBiasUs(measurement);
            weightSum += weight;
        }

        return checked((long)Math.Round(
            weightedBiasSum / weightSum,
            MidpointRounding.AwayFromZero));
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
                measurement.ActualForwardDelayUs,
                measurement.ActualReverseDelayUs,
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

    private async Task PrepareManualStartAtTestInternalAsync()
    {
        if (manualStartAtSession is not null)
        {
            StatusMessage = "A manual START_AT proof is already prepared. Cancel it before preparing another.";
            return;
        }
        if (SelectedNetworkInterface is null || !udpService.IsReady)
        {
            StatusMessage = "Select an active physical network interface before preparing the manual test.";
            return;
        }
        if (!TryDuration(out uint duration)) return;

        DeviceViewModel[] participants = Devices
            .Where(device => device.IsSelected)
            .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
            .ToArray();
        if (participants.Length != 3)
        {
            StaggeredTestResult =
                $"Select exactly 3 participants. Currently selected: {participants.Length}.";
            StatusMessage = "The manual START_AT proof requires exactly three selected participants.";
            return;
        }

        string[] participantIds = participants.Select(device => device.DeviceId).ToArray();
        if (!reservationManager.TryReserveAll(
                participantIds,
                out ParticipantReservationManager.ParticipantReservation? reservation) ||
            reservation is null)
        {
            IReadOnlyList<string> conflicts = reservationManager.GetConflicts(participantIds);
            string conflictText = conflicts.Count == 0
                ? string.Join(", ", participantIds)
                : string.Join(", ", conflicts);
            StaggeredTestResult = $"Reservation failed: {conflictText} busy.";
            StatusMessage = StaggeredTestResult;
            return;
        }

        if (!await sendLock.WaitAsync(0))
        {
            reservation.Dispose();
            return;
        }

        bool timingQuiet = false;
        bool reservationTransferred = false;
        isPreparingManualStartAtTest = true;
        isSending = true;
        UpdateCanSend();
        try
        {
            StaggeredTestResult =
                "Preparing manual proof: fresh STATUS + production 8+8 sync on all three devices...";
            ManualStartAtCountdown = "Preparing...";

            DateTimeOffset preflightAfterUtc = DateTimeOffset.UtcNow;
            IReadOnlyList<StartBlock> preflightBlocks = await RefreshAndValidatePreflightAsync(
                participants,
                preflightAfterUtc,
                CancellationToken.None);
            if (preflightBlocks.Count != 0)
            {
                ReportStartBlocks(preflightBlocks);
                StaggeredTestResult =
                    "Preflight failed. See the structured START block message above.";
                ManualStartAtCountdown = "Not prepared";
                return;
            }

            await EnterTimingQuietPeriodAsync(CancellationToken.None);
            timingQuiet = true;

            SyncSamplingProfile productionSampling =
                SyncSamplingExperiment.GetProfile(SyncSamplingMode.Baseline8Plus8);
            SyncPathDelayProfile noDelay =
                SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None);

            foreach (DeviceViewModel device in participants)
            {
                device.MarkSynchronizing();
                device.ClearStartMeasurement();
            }

            for (int index = 0; index < participants.Length; index++)
            {
                DeviceViewModel device = participants[index];
                if (!device.TryGetIpAddress(out IPAddress? address) || address is null)
                {
                    StaggeredTestResult = $"{device.DeviceId}: no IP address at synchronization time.";
                    ManualStartAtCountdown = "Not prepared";
                    return;
                }

                StatusMessage =
                    $"Manual proof: synchronizing {device.DeviceId} ({index + 1}/3) with production 8+8...";
                try
                {
                    await SynchronizeDeviceAsync(
                        device,
                        address,
                        noDelay,
                        productionSampling,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    StaggeredTestResult =
                        $"SYNC failed for {device.DeviceId}: {exception.Message}";
                    ManualStartAtCountdown = "Not prepared";
                    return;
                }
            }

            ExitTimingQuietPeriod();
            timingQuiet = false;

            DateTimeOffset postSyncStatusAfterUtc = DateTimeOffset.UtcNow;
            IReadOnlyList<StartBlock> postSyncBlocks = await RefreshAndValidatePostSyncStatusAsync(
                participants,
                postSyncStatusAfterUtc,
                CancellationToken.None);
            if (postSyncBlocks.Count != 0)
            {
                ReportStartBlocks(postSyncBlocks);
                StaggeredTestResult =
                    "Post-SYNC validation failed. See the structured START block message above.";
                ManualStartAtCountdown = "Not prepared";
                return;
            }

            DateTimeOffset gateNowUtc = DateTimeOffset.UtcNow;
            long targetMasterUs = checked(
                MasterClock.NowMicroseconds + ManualStartAtTargetLeadMicroseconds);
            StartParticipantGateInput[] gateInputs = participants
                .Select(device => device.BuildStartGateInput(gateNowUtc))
                .ToArray();
            StartGateResult gateResult = StartReadinessGate.Evaluate(
                gateInputs,
                gateNowUtc,
                targetMasterUs);
            if (!gateResult.Accepted)
            {
                ReportStartBlocks(gateResult.Blocks);
                StaggeredTestResult =
                    "Readiness gate failed. See the structured START block message above.";
                ManualStartAtCountdown = "Not prepared";
                return;
            }

            ulong commandId = CreateCommandId();
            Dictionary<string, StartParticipantGateInput> frozenGateInputs = gateInputs
                .ToDictionary(input => input.DeviceId, StringComparer.Ordinal);
            PreparedParticipant[] frozenParticipants = participants
                .Select(device =>
                {
                    StartParticipantGateInput frozenGate = frozenGateInputs[device.DeviceId];
                    IPAddress address = frozenGate.ReportedIpAddress
                        ?? throw new InvalidOperationException(
                            $"{device.DeviceId} has no STATUS-reported IP at freeze time.");
                    return new PreparedParticipant(device, address, frozenGate);
                })
                .ToArray();
            var prepared = new PreparedStartRun(
                commandId,
                duration,
                targetMasterUs,
                frozenParticipants);

            foreach (PreparedParticipant participant in prepared.Participants)
            {
                participant.Device.ClearStartMeasurement();
            }

            var ackTracker = new ArmAcknowledgementTracker(
                prepared.CommandId,
                prepared.Participants.Select(participant => participant.Device.DeviceId));
            var session = new ManualStartAtSession(prepared, reservation, ackTracker);
            manualStartAtSession = session;
            reservationTransferred = true;
            Volatile.Write(ref activeArmAcknowledgementTracker, ackTracker);

            clock.ArmAt(prepared.DurationSeconds, prepared.TargetMasterMicroseconds);
            RefreshClockProperties();
            RefreshManualStartAtUi();
            StaggeredTestResult = BuildManualStartAtProgressText(session);
            StartSynchronizationResult =
                "Manual 3-device proof prepared; STARTED progress/results are shown for the frozen 3-device set.";
            StatusMessage =
                "Manual START_AT proof prepared. Click the three device buttons at visibly different times; all use the same frozen T*.";

            _ = MonitorManualStartAtSessionAsync(session);
        }
        catch (Exception exception)
        {
            StaggeredTestResult = $"Manual START_AT test preparation failed: {exception.Message}";
            ManualStartAtCountdown = "Not prepared";
            StatusMessage = StaggeredTestResult;
        }
        finally
        {
            if (timingQuiet)
            {
                ExitTimingQuietPeriod();
            }
            if (!reservationTransferred)
            {
                reservation.Dispose();
            }
            isPreparingManualStartAtTest = false;
            isSending = false;
            UpdateCanSend();
            sendLock.Release();
        }
    }

    private async Task SendManualStartAtToDeviceInternalAsync(string deviceId)
    {
        ManualStartAtSession? session = manualStartAtSession;
        if (session is null)
        {
            StatusMessage = "Prepare the manual START_AT proof first.";
            return;
        }

        PreparedParticipant? participant = session.Prepared.Participants
            .FirstOrDefault(item => string.Equals(item.Device.DeviceId, deviceId, StringComparison.Ordinal));
        if (participant is null)
        {
            StatusMessage = $"{deviceId} is not in the frozen manual-test participant set.";
            return;
        }
        if (session.Sends.ContainsKey(deviceId))
        {
            StatusMessage = $"START_AT was already sent to {deviceId} for this manual test.";
            return;
        }

        long cutoffMasterUs = checked(
            session.Prepared.TargetMasterMicroseconds - ManualStartAtCutoffLeadMicroseconds);
        if (MasterClock.NowMicroseconds >= cutoffMasterUs)
        {
            StatusMessage =
                $"Too late to send {deviceId}: the manual safety cutoff is T* - {ManualStartAtCutoffLeadMicroseconds / 1_000_000d:F1} s.";
            RefreshManualStartAtUi();
            return;
        }

        if (!await sendLock.WaitAsync(0)) return;
        isSending = true;
        UpdateCanSend();
        try
        {
            if (!ReferenceEquals(manualStartAtSession, session)) return;

            long nowUs = MasterClock.NowMicroseconds;
            if (nowUs >= cutoffMasterUs)
            {
                StatusMessage =
                    $"Too late to send {deviceId}: the manual safety cutoff has been reached.";
                return;
            }

            var command = new CommandPacket(
                CommandType.StartAt,
                session.Prepared.CommandId,
                session.Prepared.DurationSeconds,
                0,
                session.Prepared.TargetMasterMicroseconds);

            participant.Device.MarkPending(session.Prepared.CommandId);
            participant.Device.ClearStartMeasurement();
            long sentMasterUs = MasterClock.NowMicroseconds;
            await udpService.SendCommandUnicastAsync(
                command,
                participant.Address,
                CancellationToken.None);

            session.Sends[deviceId] = new ManualStartAtSendRecord(
                deviceId,
                sentMasterUs,
                session.Prepared.TargetMasterMicroseconds - sentMasterUs);

            StaggeredTestResult = BuildManualStartAtProgressText(session);
            StatusMessage =
                $"START_AT manually sent to {deviceId} {Math.Max(0, session.Prepared.TargetMasterMicroseconds - sentMasterUs) / 1_000_000d:F3} s before the common T*.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Manual START_AT send to {deviceId} failed: {exception.Message}";
        }
        finally
        {
            isSending = false;
            UpdateCanSend();
            sendLock.Release();
        }
    }

    private async Task CancelManualStartAtTestInternalAsync()
    {
        ManualStartAtSession? session = manualStartAtSession;
        if (session is null)
        {
            StatusMessage = "No manual START_AT proof is currently prepared.";
            return;
        }
        if (!await sendLock.WaitAsync(0)) return;

        isSending = true;
        UpdateCanSend();
        try
        {
            if (!ReferenceEquals(manualStartAtSession, session)) return;
            session.Cancellation.Cancel();
            AbortConfirmationResult abort = await ConfirmPreparedRunAbortAsync(
                session.Prepared,
                CancellationToken.None);
            clock.Reset(session.Prepared.DurationSeconds);
            RefreshClockProperties();
            StaggeredTestResult = BuildManualStartAtProgressText(session) +
                (abort.Confirmed
                    ? "\nCANCELLED: abort confirmed by RESET ACK + fresh READY STATUS for the frozen set."
                    : "\nCANCELLED, BUT ABORT UNCONFIRMED: one or more frozen devices may still start at T*.");
            StartSynchronizationResult = abort.Confirmed
                ? "Manual 3-device proof cancelled; frozen-set cancellation confirmed."
                : "Manual 3-device proof cancelled, but frozen-set cancellation is UNCONFIRMED.";
            StatusMessage = abort.Confirmed
                ? "Manual START_AT proof cancelled safely; frozen-set RESET was confirmed."
                : "Manual START_AT cancellation is UNCONFIRMED; wait through T* and verify device state.";
            ReleaseManualStartAtSession(session);
        }
        finally
        {
            isSending = false;
            UpdateCanSend();
            sendLock.Release();
        }
    }

    private async Task MonitorManualStartAtSessionAsync(ManualStartAtSession session)
    {
        try
        {
            long cutoffMasterUs = checked(
                session.Prepared.TargetMasterMicroseconds - ManualStartAtCutoffLeadMicroseconds);
            await WaitUntilMasterTimeAsync(cutoffMasterUs, session.Cancellation.Token);
            if (!ReferenceEquals(manualStartAtSession, session)) return;

            await sendLock.WaitAsync(session.Cancellation.Token);
            isSending = true;
            UpdateCanSend();
            try
            {
                if (!ReferenceEquals(manualStartAtSession, session)) return;

                bool allSent = session.Sends.Count == session.Prepared.Participants.Count;
                IReadOnlyDictionary<string, AckResult> rejections = session.AckTracker.Rejections;
                bool allArmed = session.AckTracker.AllArmed;
                if (!allSent || rejections.Count != 0 || !allArmed)
                {
                    string detail;
                    if (!allSent)
                    {
                        string[] unsent = session.Prepared.Participants
                            .Select(participant => participant.Device.DeviceId)
                            .Where(id => !session.Sends.ContainsKey(id))
                            .ToArray();
                        detail = $"not sent: {string.Join(", ", unsent)}";
                    }
                    else if (rejections.Count != 0)
                    {
                        detail = string.Join(", ", rejections.Select(pair => $"{pair.Key}={pair.Value}"));
                    }
                    else
                    {
                        detail = $"missing ACK: {string.Join(", ", session.AckTracker.MissingParticipants)}";
                    }

                    AbortConfirmationResult abort = await ConfirmPreparedRunAbortAsync(
                        session.Prepared,
                        CancellationToken.None);
                    clock.Reset(session.Prepared.DurationSeconds);
                    RefreshClockProperties();
                    StaggeredTestResult = BuildManualStartAtProgressText(session) +
                        (abort.Confirmed
                            ? $"\nABORT at T* - {ManualStartAtCutoffLeadMicroseconds / 1_000_000d:F1} s: {detail}. Cancellation confirmed."
                            : $"\nABORT at T* - {ManualStartAtCutoffLeadMicroseconds / 1_000_000d:F1} s: {detail}. ABORT UNCONFIRMED; a device may still start.");
                    StartSynchronizationResult = abort.Confirmed
                        ? $"Manual 3-device proof aborted before T*: {detail}. Frozen-set cancellation confirmed."
                        : $"Manual 3-device proof aborted before T*: {detail}. ABORT UNCONFIRMED.";
                    StatusMessage = abort.Confirmed
                        ? "Manual START_AT proof aborted safely; RESET was positively confirmed."
                        : "Manual START_AT abort is UNCONFIRMED; wait through T* and verify device state.";
                    ReleaseManualStartAtSession(session);
                    return;
                }

                StaggeredTestResult = BuildManualStartAtProgressText(session) +
                    "\nAll three START_AT commands are ACKed; waiting for the common T*.";
                StatusMessage = "Manual proof armed: all three devices ACKed the same T*.";
            }
            finally
            {
                isSending = false;
                UpdateCanSend();
                sendLock.Release();
            }

            long startedDeadlineMasterUs = checked(
                session.Prepared.TargetMasterMicroseconds + ManualStartAtStartedTelemetryGraceMicroseconds);
            while (MasterClock.NowMicroseconds < startedDeadlineMasterUs)
            {
                if (!ReferenceEquals(manualStartAtSession, session)) return;
                if (session.Prepared.Participants.All(participant =>
                        participant.Device.LastStartedCommandId == session.Prepared.CommandId))
                {
                    break;
                }
                await Task.Delay(20, session.Cancellation.Token);
            }

            if (!ReferenceEquals(manualStartAtSession, session)) return;
            PreparedParticipant[] missingStarted = session.Prepared.Participants
                .Where(participant => participant.Device.LastStartedCommandId != session.Prepared.CommandId)
                .ToArray();
            if (missingStarted.Length != 0)
            {
                string missingStartedIds = string.Join(", ", missingStarted.Select(p => p.Device.DeviceId));
                StaggeredTestResult = BuildManualStartAtProgressText(session) +
                    $"\nFAIL: no STARTED telemetry from {missingStartedIds}.";
                StartSynchronizationResult =
                    $"Manual 3-device proof FAIL: no STARTED telemetry from {missingStartedIds}.";
                StatusMessage = "Manual proof reached T*, but STARTED telemetry was incomplete.";
                ReleaseManualStartAtSession(session);
                return;
            }

            long[] startErrors = session.Prepared.Participants
                .Select(participant => participant.Device.LastStartErrorMicroseconds!.Value)
                .ToArray();
            long fleetSpreadUs = startErrors.Max() - startErrors.Min();
            long worstSchedulerLatenessUs = session.Prepared.Participants
                .Max(participant => Math.Abs(participant.Device.LastSchedulerLatenessMicroseconds ?? long.MaxValue));

            var result = new StringBuilder();
            result.Append(BuildManualStartAtProgressText(session));
            result.AppendLine();
            result.AppendLine("STARTED telemetry:");

            bool exactnessInputsComplete = true;
            long worstIdentityDeltaUs = 0;
            foreach (PreparedParticipant participant in session.Prepared.Participants)
            {
                DeviceViewModel device = participant.Device;
                long startErrorUs = device.LastStartErrorMicroseconds!.Value;
                long schedulerLatenessUs = device.LastSchedulerLatenessMicroseconds!.Value;
                string residualText;
                string identityText;
                if (device.ResidualErrorMicroseconds is long residualUs)
                {
                    // Applied-offset consistency identity:
                    // start_error = (local_start + verification_offset - T*)
                    // scheduler_lateness = (local_start + applied_offset - T*)
                    // residual = applied_offset - verification_offset
                    // therefore start_error == scheduler_lateness - residual.
                    // verification_offset cancels from identity_delta; zero proves
                    // consistency with the controller-recorded SYNC_APPLIED offset,
                    // not correctness of the verification estimate itself.
                    long expectedStartErrorUs = checked(schedulerLatenessUs - residualUs);
                    long identityDeltaUs = checked(startErrorUs - expectedStartErrorUs);
                    worstIdentityDeltaUs = Math.Max(worstIdentityDeltaUs, Math.Abs(identityDeltaUs));
                    residualText = FormatSignedMicrosecondsAsMilliseconds(residualUs);
                    identityText = $"identity_delta={identityDeltaUs:+#;-#;0} µs";
                }
                else
                {
                    exactnessInputsComplete = false;
                    residualText = "n/a";
                    identityText = "identity_delta=n/a";
                }

                result.AppendLine(
                    $"{device.DeviceId}: sync residual={residualText}, " +
                    $"start error={FormatSignedMicrosecondsAsMilliseconds(startErrorUs)}, " +
                    $"scheduler_lateness={schedulerLatenessUs:+#;-#;0} µs, {identityText}");
            }
            long worstAbsoluteStartErrorUs = startErrors.Max(error => Math.Abs(error));

            ManualStartAtSendRecord[] orderedSends = session.Prepared.Participants
                .Select(participant => session.Sends[participant.Device.DeviceId])
                .OrderBy(send => send.SentMasterMicroseconds)
                .ToArray();
            long sendSpanUs =
                orderedSends[^1].SentMasterMicroseconds - orderedSends[0].SentMasterMicroseconds;
            long minimumSendGapUs = orderedSends
                .Zip(
                    orderedSends.Skip(1),
                    (earlier, later) => later.SentMasterMicroseconds - earlier.SentMasterMicroseconds)
                .Min();
            string chronologicalSendOrder = string.Join(
                " -> ",
                orderedSends.Select(send => send.DeviceId));

            bool schedulerExecutionPassed =
                worstSchedulerLatenessUs <= ManualStartAtMaximumSchedulerLatenessMicroseconds;
            bool manualDeliverySeparated =
                minimumSendGapUs >= ManualStartAtMinimumSendGapMicroseconds;
            bool exactnessPassed = exactnessInputsComplete && worstIdentityDeltaUs == 0;
            bool proofPassed = schedulerExecutionPassed && manualDeliverySeparated && exactnessPassed;

            result.AppendLine(
                $"Sync-level information (not a PASS criterion): fleet start-error spread={fleetSpreadUs / 1000d:F3} ms; " +
                $"worst |start error|={worstAbsoluteStartErrorUs / 1000d:F3} ms.");
            result.AppendLine(
                $"Execution evidence: worst |scheduler lateness|={worstSchedulerLatenessUs} µs; " +
                $"manual send span={sendSpanUs / 1_000_000d:F3} s; " +
                $"minimum adjacent send gap={minimumSendGapUs / 1_000_000d:F3} s; " +
                $"chronological send order={chronologicalSendOrder}.");
            result.AppendLine(
                exactnessPassed
                    ? "Applied-offset consistency: PASS; every row satisfies start error = scheduler lateness - sync residual exactly (identity delta 0 µs). Verification accuracy is not implied."
                    : $"Applied-offset consistency: FAIL; worst |identity delta|={worstIdentityDeltaUs} µs or a residual was unavailable.");
            result.Append(
                proofPassed
                    ? $"PASS: all three devices reported STARTED on the frozen CommandId; " +
                      $"max |scheduler lateness| <= {ManualStartAtMaximumSchedulerLatenessMicroseconds} µs; " +
                      $"minimum manual send gap >= {ManualStartAtMinimumSendGapMicroseconds / 1_000_000d:F3} s; " +
                      "and the applied-offset consistency identity is exact on all three rows."
                    : $"FAIL: this proof requires all three devices to report STARTED on the frozen CommandId, " +
                      $"max |scheduler lateness| <= {ManualStartAtMaximumSchedulerLatenessMicroseconds} µs, " +
                      $"minimum manual send gap >= {ManualStartAtMinimumSendGapMicroseconds / 1_000_000d:F3} s, " +
                      "and exact applied-offset consistency on all three rows.");
            StaggeredTestResult = result.ToString();
            StartSynchronizationResult =
                $"Manual 3-device proof: worst |scheduler lateness|={worstSchedulerLatenessUs} µs; " +
                $"send span={sendSpanUs / 1_000_000d:F3} s; minimum send gap={minimumSendGapUs / 1_000_000d:F3} s; " +
                $"identity delta max={worstIdentityDeltaUs} µs; start-error spread={fleetSpreadUs / 1000d:F3} ms (sync-level info); " +
                (proofPassed ? "PASS" : "FAIL");
            StatusMessage = proofPassed
                ? $"Manual START_AT proof PASS: execution <= {ManualStartAtMaximumSchedulerLatenessMicroseconds} µs late, manual sends were separated by at least {ManualStartAtMinimumSendGapMicroseconds / 1_000_000d:F1} s, and exactness identity held."
                : $"Manual START_AT proof FAIL: worst |scheduler lateness|={worstSchedulerLatenessUs} µs; minimum send gap={minimumSendGapUs / 1_000_000d:F3} s; worst identity delta={worstIdentityDeltaUs} µs.";
            ReleaseManualStartAtSession(session);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal path when the operator presses CANCEL / RESET
            // or when the view model is disposed.
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(manualStartAtSession, session))
            {
                StaggeredTestResult = $"Manual START_AT monitor failed: {exception.Message}";
                StartSynchronizationResult = $"Manual 3-device proof failed: {exception.Message}";
                StatusMessage = StaggeredTestResult;
                ReleaseManualStartAtSession(session);
            }
        }
    }

    private bool CanSendManualStartAtTo(string deviceId)
    {
        ManualStartAtSession? session = manualStartAtSession;
        if (session is null || isSending || !udpService.IsReady) return false;
        if (!session.Prepared.Participants.Any(participant =>
                string.Equals(participant.Device.DeviceId, deviceId, StringComparison.Ordinal)))
        {
            return false;
        }
        if (session.Sends.ContainsKey(deviceId)) return false;
        long cutoffMasterUs = session.Prepared.TargetMasterMicroseconds - ManualStartAtCutoffLeadMicroseconds;
        return MasterClock.NowMicroseconds < cutoffMasterUs;
    }

    private void RefreshManualStartAtButtonStates()
    {
        OnPropertyChanged(nameof(CanManualSendEsp01));
        OnPropertyChanged(nameof(CanManualSendEsp02));
        OnPropertyChanged(nameof(CanManualSendEsp03));
        OnPropertyChanged(nameof(CanManualSendEsp04));
        OnPropertyChanged(nameof(CanManualSendEsp05));
        OnPropertyChanged(nameof(CanCancelManualStartAtTest));
    }

    private void RefreshManualStartAtUi()
    {
        ManualStartAtSession? session = manualStartAtSession;
        if (session is null)
        {
            ManualStartAtCountdown = isPreparingManualStartAtTest ? "Preparing..." : "Not prepared";
            RefreshManualStartAtButtonStates();
            return;
        }

        long remainingUs = session.Prepared.TargetMasterMicroseconds - MasterClock.NowMicroseconds;
        ManualStartAtCountdown = remainingUs > 0
            ? $"Common T* in {remainingUs / 1_000_000d:F1} s"
            : $"Common T* reached {Math.Abs(remainingUs) / 1_000_000d:F1} s ago";
        RefreshManualStartAtButtonStates();
    }

    private string BuildManualStartAtProgressText(ManualStartAtSession session)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"Common T*={session.Prepared.TargetMasterMicroseconds:N0} µs; command={FactoryProtocol.FormatCommandId(session.Prepared.CommandId)}");
        builder.AppendLine(
            $"Duration={session.Prepared.DurationSeconds} s; manual send cutoff=T* - {ManualStartAtCutoffLeadMicroseconds / 1_000_000d:F1} s");

        IReadOnlyDictionary<string, AckResult> rejections = session.AckTracker.Rejections;
        IReadOnlyList<string> missingAcks = session.AckTracker.MissingParticipants;
        foreach (PreparedParticipant participant in session.Prepared.Participants)
        {
            string id = participant.Device.DeviceId;
            if (!session.Sends.TryGetValue(id, out ManualStartAtSendRecord? send))
            {
                builder.AppendLine($"{id}: NOT SENT");
                continue;
            }

            string ackText;
            if (rejections.TryGetValue(id, out AckResult rejection))
            {
                ackText = $"ACK {rejection}";
            }
            else if (missingAcks.Contains(id, StringComparer.Ordinal))
            {
                ackText = "ACK waiting";
            }
            else
            {
                ackText = "ACK accepted";
            }

            builder.AppendLine(
                $"{id}: sent {send.LeadMicroseconds / 1_000_000d:F3} s before T* " +
                $"(master={send.SentMasterMicroseconds:N0} µs), {ackText}");
        }
        return builder.ToString().TrimEnd();
    }

    private void ReleaseManualStartAtSession(ManualStartAtSession session)
    {
        if (!ReferenceEquals(manualStartAtSession, session)) return;
        Volatile.Write(ref activeArmAcknowledgementTracker, null);
        manualStartAtSession = null;
        session.Dispose();
        ManualStartAtCountdown = "Not prepared";
        UpdateCanSend();
    }

    private static async Task WaitUntilMasterTimeAsync(
        long targetMasterMicroseconds,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            long remainingUs = targetMasterMicroseconds - MasterClock.NowMicroseconds;
            if (remainingUs <= 0) return;

            int delayMs = (int)Math.Min(
                50L,
                Math.Max(1L, remainingUs / 1000L));
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    private static string FormatSignedMicrosecondsAsMilliseconds(long microseconds) =>
        $"{microseconds / 1000d:+0.000;-0.000;0.000} ms";

    private sealed record ManualStartAtSendRecord(
        string DeviceId,
        long SentMasterMicroseconds,
        long LeadMicroseconds);

    private async Task SendStartAtAsync()
    {
        if (SelectedNetworkInterface is null || !udpService.IsReady)
        {
            StatusMessage = "Select an active physical network interface before sending a command.";
            return;
        }
        if (!TryDuration(out uint duration)) return;

        DeviceViewModel[] participants = Devices.Where(device => device.IsSelected).ToArray();
        if (participants.Length == 0)
        {
            ReportStartBlocks([
                new StartBlock(
                    StartBlockReason.MissingExpectedDevice,
                    "(none)",
                    "Select at least one participant.")]);
            return;
        }

        string[] participantIds = participants.Select(device => device.DeviceId).ToArray();
        if (!reservationManager.TryReserveAll(
                participantIds,
                out ParticipantReservationManager.ParticipantReservation? reservation) ||
            reservation is null)
        {
            IReadOnlyList<string> conflicts = reservationManager.GetConflicts(participantIds);
            if (conflicts.Count == 0) conflicts = participantIds;
            ReportStartBlocks(conflicts.Select(deviceId => new StartBlock(
                StartBlockReason.ParticipantBusy,
                deviceId,
                "Another prepare currently holds this participant; wait for it to finish and press START again.")).ToArray());
            return;
        }

        using (reservation)
        {
            if (!await sendLock.WaitAsync(0)) return;

            bool timingQuiet = false;
            PreparedStartRun? preparedForAbort = null;
            bool startAtMayHaveBeenSent = false;
            isSending = true;
            productionStartTelemetryContext = null;
            UpdateCanSend();
            try
            {
                // A fresh preflight STATUS prevents a restarted controller from sending
                // SYNC_SET to a device that is already ARMED/RUNNING in an unknown run.
                DateTimeOffset preflightAfterUtc = DateTimeOffset.UtcNow;
                IReadOnlyList<StartBlock> preflightBlocks = await RefreshAndValidatePreflightAsync(
                    participants,
                    preflightAfterUtc,
                    CancellationToken.None);
                if (preflightBlocks.Count != 0)
                {
                    ReportStartBlocks(preflightBlocks);
                    return;
                }

                await EnterTimingQuietPeriodAsync(CancellationToken.None);
                timingQuiet = true;

                SyncSamplingProfile productionSampling =
                    SyncSamplingExperiment.GetProfile(SyncSamplingMode.Baseline8Plus8);
                SyncPathDelayProfile noDelay =
                    SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None);

                foreach (DeviceViewModel device in participants)
                {
                    device.MarkSynchronizing();
                    device.ClearStartMeasurement();
                }

                for (int index = 0; index < participants.Length; index++)
                {
                    DeviceViewModel device = participants[index];
                    if (!device.TryGetIpAddress(out IPAddress? address) || address is null)
                    {
                        ReportStartBlocks([
                            new StartBlock(
                                StartBlockReason.MissingExpectedDevice,
                                device.DeviceId,
                                "Wait for discovery or deselect this participant.")]);
                        return;
                    }

                    StatusMessage =
                        $"Preparing {device.DisplayName} ({index + 1}/{participants.Length})...";
                    try
                    {
                        await SynchronizeDeviceAsync(
                            device,
                            address,
                            noDelay,
                            productionSampling,
                            CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        ReportStartBlocks([
                            new StartBlock(
                                StartBlockReason.SyncFailed,
                                device.DeviceId,
                                $"Press START again for a fresh sync. {exception.Message}")]);
                        return;
                    }
                }

                // Resume ordinary STATUS only after all foreground SYNC traffic is done.
                ExitTimingQuietPeriod();
                timingQuiet = false;

                DateTimeOffset postSyncStatusAfterUtc = DateTimeOffset.UtcNow;
                IReadOnlyList<StartBlock> postSyncStatusBlocks = await RefreshAndValidatePostSyncStatusAsync(
                    participants,
                    postSyncStatusAfterUtc,
                    CancellationToken.None);
                if (postSyncStatusBlocks.Count != 0)
                {
                    ReportStartBlocks(postSyncStatusBlocks);
                    return;
                }

                DateTimeOffset gateNowUtc = DateTimeOffset.UtcNow;
                long targetCandidateUs = checked(
                    MasterClock.NowMicroseconds + ProductionStartLeadMicroseconds);
                StartParticipantGateInput[] gateInputs = participants
                    .Select(device => device.BuildStartGateInput(gateNowUtc))
                    .ToArray();
                StartGateResult gateResult = StartReadinessGate.Evaluate(
                    gateInputs,
                    gateNowUtc,
                    targetCandidateUs);
                if (!gateResult.Accepted)
                {
                    ReportStartBlocks(gateResult.Blocks);
                    return;
                }

                ulong commandId = CreateCommandId();
                Dictionary<string, StartParticipantGateInput> frozenGateInputs = gateInputs
                    .ToDictionary(input => input.DeviceId, StringComparer.Ordinal);
                PreparedParticipant[] frozenParticipants = participants
                    .Select(device =>
                    {
                        StartParticipantGateInput frozenGate = frozenGateInputs[device.DeviceId];
                        IPAddress address = frozenGate.ReportedIpAddress
                            ?? throw new InvalidOperationException(
                                $"{device.DisplayName} has no current network address.");
                        return new PreparedParticipant(device, address, frozenGate);
                    })
                    .ToArray();
                var prepared = new PreparedStartRun(
                    commandId,
                    duration,
                    targetCandidateUs,
                    frozenParticipants);
                preparedForAbort = prepared;

                var command = new CommandPacket(
                    CommandType.StartAt,
                    prepared.CommandId,
                    prepared.DurationSeconds,
                    0,
                    prepared.TargetMasterMicroseconds);

                foreach (PreparedParticipant participant in prepared.Participants)
                {
                    participant.Device.MarkPending(prepared.CommandId);
                    participant.Device.ClearStartMeasurement();
                }

                clock.ArmAt(prepared.DurationSeconds, prepared.TargetMasterMicroseconds);
                StartSynchronizationResult = "ARMING selected participants...";

                startAtMayHaveBeenSent = true;
                StartBlock? armFailure = await ArmPreparedRunAsync(
                    prepared,
                    command,
                    CancellationToken.None);
                if (armFailure is not null)
                {
                    Volatile.Write(ref activeArmAcknowledgementTracker, null);
                    AbortConfirmationResult abort = await ConfirmPreparedRunAbortAsync(
                        prepared,
                        CancellationToken.None);
                    clock.Reset(prepared.DurationSeconds);
                    RefreshClockProperties();
                    ReportPreparedRunAbort(armFailure, abort);
                    preparedForAbort = null;
                    return;
                }

                preparedForAbort = null;
                productionStartTelemetryContext = new StartTelemetryContext(
                    prepared.CommandId,
                    prepared.Participants.Select(participant => participant.Device).ToArray());
                RefreshStartSynchronizationResult(
                    productionStartTelemetryContext.CommandId,
                    productionStartTelemetryContext.Participants,
                    "Production START");
                StatusMessage =
                    $"Countdown prepared. {prepared.Participants.Count} selected timer(s) will start together.";
            }
            catch (Exception exception)
            {
                if (startAtMayHaveBeenSent && preparedForAbort is not null)
                {
                    Volatile.Write(ref activeArmAcknowledgementTracker, null);
                    AbortConfirmationResult abort = await ConfirmPreparedRunAbortAsync(
                        preparedForAbort,
                        CancellationToken.None);
                    clock.Reset(preparedForAbort.DurationSeconds);
                    RefreshClockProperties();
                    ReportPreparedRunAbort(
                        new StartBlock(
                            StartBlockReason.ArmAckMissing,
                            "controller",
                            $"START prepare raised an exception after ARMING began: {exception.Message}"),
                        abort);
                    preparedForAbort = null;
                }
                else
                {
                    StatusMessage = $"START prepare failed: {exception.Message}";
                }
            }
            finally
            {
                Volatile.Write(ref activeArmAcknowledgementTracker, null);
                if (timingQuiet)
                {
                    ExitTimingQuietPeriod();
                }
                isSending = false;
                UpdateCanSend();
                sendLock.Release();
            }
        }
    }

    private async Task<IReadOnlyList<StartBlock>> RefreshAndValidatePreflightAsync(
        IReadOnlyList<DeviceViewModel> participants,
        DateTimeOffset requireStatusAfterUtc,
        CancellationToken cancellationToken)
    {
        List<StartBlock> missingAddressBlocks = participants
            .Where(device => !device.TryGetIpAddress(out _))
            .Select(device => new StartBlock(
                StartBlockReason.MissingExpectedDevice,
                device.DeviceId,
                "Wait for discovery or deselect this participant."))
            .ToList();
        if (missingAddressBlocks.Count != 0) return missingAddressBlocks;

        await RequestFreshStatusesAsync(participants, cancellationToken);
        await WaitForFreshStatusesAsync(participants, requireStatusAfterUtc, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var blocks = new List<StartBlock>();
        foreach (DeviceViewModel device in participants)
        {
            ParticipantSessionSnapshot session = device.SessionSnapshot;
            if (session.LastStatusAtUtc.HasValue &&
                (session.LastStatusAtUtc.Value < requireStatusAfterUtc ||
                 now - session.LastStatusAtUtc.Value >= StartReadinessGate.MaximumStatusAge))
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.StatusStale,
                    device.DeviceId,
                    "Wait for a fresh STATUS report and press START again."));
                continue;
            }
            if (!device.IsOnlineAt(now) || !session.LastStatusAtUtc.HasValue)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.Offline,
                    device.DeviceId,
                    "Reconnect the device or deselect it."));
                continue;
            }
            if (session.TimerState is TimerState.Armed or TimerState.Running)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.ParticipantBusy,
                    device.DeviceId,
                    "Wait for completion or use the normal manual RESET action."));
            }
        }
        return blocks;
    }

    private async Task<IReadOnlyList<StartBlock>> RefreshAndValidatePostSyncStatusAsync(
        IReadOnlyList<DeviceViewModel> participants,
        DateTimeOffset requireStatusAfterUtc,
        CancellationToken cancellationToken)
    {
        await RequestFreshStatusesAsync(participants, cancellationToken);
        await WaitForFreshStatusesAsync(participants, requireStatusAfterUtc, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var blocks = new List<StartBlock>();
        foreach (DeviceViewModel device in participants)
        {
            ParticipantSessionSnapshot session = device.SessionSnapshot;
            if (session.LastStatusAtUtc.HasValue &&
                (session.LastStatusAtUtc.Value < requireStatusAfterUtc ||
                 now - session.LastStatusAtUtc.Value >= StartReadinessGate.MaximumStatusAge))
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.StatusStale,
                    device.DeviceId,
                    "Wait for a fresh STATUS report and press START again."));
                continue;
            }
            if (!device.IsOnlineAt(now) || !session.LastStatusAtUtc.HasValue)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.Offline,
                    device.DeviceId,
                    "Reconnect the device or deselect it."));
                continue;
            }
            if (session.TimerState is TimerState.Armed or TimerState.Running)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.ParticipantBusy,
                    device.DeviceId,
                    "Wait for completion or use the normal manual RESET action."));
                continue;
            }
            if (!device.SynchronizationSessionGeneration.HasValue ||
                device.SynchronizationSessionGeneration.Value != session.Generation)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.SyncInvalidated,
                    device.DeviceId,
                    "Press START again to synchronize the current device session."));
            }
        }
        return blocks;
    }

    private async Task RequestFreshStatusesAsync(
        IReadOnlyList<DeviceViewModel> participants,
        CancellationToken cancellationToken)
    {
        foreach (DeviceViewModel device in participants)
        {
            if (device.TryGetIpAddress(out IPAddress? address) && address is not null)
            {
                await udpService.SendStatusRequestAsync(
                    CreateCommandId(),
                    address,
                    cancellationToken);
            }
        }
    }

    private static async Task WaitForFreshStatusesAsync(
        IReadOnlyList<DeviceViewModel> participants,
        DateTimeOffset requireStatusAfterUtc,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + FreshStatusWaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (participants.All(device =>
                    device.SessionSnapshot.LastStatusAtUtc is DateTimeOffset statusAt &&
                    statusAt >= requireStatusAfterUtc))
            {
                return;
            }
            await Task.Delay(40, cancellationToken);
        }
    }

    private async Task<StartBlock?> ArmPreparedRunAsync(
        PreparedStartRun prepared,
        CommandPacket command,
        CancellationToken cancellationToken)
    {
        long cutoffMasterUs = checked(
            prepared.TargetMasterMicroseconds - StartArmWindow.CutoffLeadMicroseconds);
        long normalArmDeadlineMasterUs = checked(
            prepared.TargetMasterMicroseconds - StartArmWindow.NormalArmDeadlineLeadMicroseconds);
        long finalBarrierStartMasterUs = checked(
            prepared.TargetMasterMicroseconds - StartArmWindow.FinalBarrierStartLeadMicroseconds);
        var ackTracker = new ArmAcknowledgementTracker(
            prepared.CommandId,
            prepared.Participants.Select(participant => participant.Device.DeviceId));
        Volatile.Write(ref activeArmAcknowledgementTracker, ackTracker);

        while (StartArmWindow.IsNormalArmOpen(
            MasterClock.NowMicroseconds,
            prepared.TargetMasterMicroseconds))
        {
            foreach (PreparedParticipant participant in prepared.Participants)
            {
                StartBlock? sessionBlock = StartArmSessionGuard.Evaluate(
                    participant.Device.DeviceId,
                    participant.GateSnapshot,
                    participant.Device.SessionSnapshot,
                    prepared.CommandId);
                if (sessionBlock is not null) return sessionBlock;
            }

            IReadOnlyDictionary<string, AckResult> rejections = ackTracker.Rejections;
            if (rejections.Count != 0)
            {
                KeyValuePair<string, AckResult> rejection = rejections.First();
                return rejection.Value == AckResult.NotSynced
                    ? new StartBlock(
                        StartBlockReason.SyncInvalidated,
                        rejection.Key,
                        "Firmware rejected START_AT as NotSynced. Press START again for fresh synchronization.")
                    : new StartBlock(
                        StartBlockReason.ArmAckMissing,
                        rejection.Key,
                        "Firmware rejected START_AT as Late. Press START again for a new target.");
            }

            if (!ackTracker.AllArmed)
            {
                HashSet<string> missing = ackTracker.MissingParticipants.ToHashSet(StringComparer.Ordinal);
                foreach (PreparedParticipant participant in prepared.Participants)
                {
                    if (!missing.Contains(participant.Device.DeviceId)) continue;
                    await udpService.SendCommandUnicastOnceAsync(
                        command,
                        participant.Address,
                        cancellationToken);
                }
            }
            else if (MasterClock.NowMicroseconds >= finalBarrierStartMasterUs)
            {
                StartBlock? barrierFailure = await VerifyFinalArmBarrierAsync(
                    prepared,
                    cancellationToken);
                if (barrierFailure is not null) return barrierFailure;

                // Keep the existing finite ARMING session guard active until the
                // exact cutoff. The final STATUS round-trip is not repeated; this
                // simply preserves reaction to any state/session change that is
                // independently observed before T* - 2 s.
                while (StartArmWindow.IsOpen(
                    MasterClock.NowMicroseconds,
                    prepared.TargetMasterMicroseconds))
                {
                    foreach (PreparedParticipant participant in prepared.Participants)
                    {
                        StartBlock? sessionBlock = StartArmSessionGuard.Evaluate(
                            participant.Device.DeviceId,
                            participant.GateSnapshot,
                            participant.Device.SessionSnapshot,
                            prepared.CommandId);
                        if (sessionBlock is not null) return sessionBlock;
                    }

                    long remainingToCutoffUs = cutoffMasterUs - MasterClock.NowMicroseconds;
                    if (remainingToCutoffUs <= 0) break;
                    await Task.Delay(
                        (int)Math.Min(20L, Math.Max(1L, remainingToCutoffUs / 1000L)),
                        cancellationToken);
                }

                return null;
            }

            long nextBoundaryUs = ackTracker.AllArmed
                ? finalBarrierStartMasterUs
                : normalArmDeadlineMasterUs;
            long remainingUs = nextBoundaryUs - MasterClock.NowMicroseconds;
            if (remainingUs <= 0) continue;
            int delayMs = (int)Math.Min(
                ackTracker.AllArmed ? FinalBarrierRetryWaitMilliseconds : ArmRetryIntervalMilliseconds,
                Math.Max(1L, remainingUs / 1000L));
            await Task.Delay(delayMs, cancellationToken);
        }

        if (!ackTracker.AllArmed)
        {
            string missingNames = string.Join(", ", ackTracker.MissingParticipants);
            return new StartBlock(
                StartBlockReason.ArmAckMissing,
                missingNames,
                "Verify the named device/network and press START again.");
        }

        // All ACKs arrived, but normal ARMING did not complete before T* - 2.5 s.
        // The caller now enters the dedicated confirmed-cancellation window rather
        // than relying on an unverified best-effort RESET.
        return new StartBlock(
            StartBlockReason.StatusStale,
            string.Join(", ", prepared.Participants.Select(p => p.Device.DeviceId)),
            "Normal ARM verification did not complete before T* - 2.5 s; cancelling the frozen run.");
    }

    private async Task<StartBlock?> VerifyFinalArmBarrierAsync(
        PreparedStartRun prepared,
        CancellationToken cancellationToken)
    {
        DateTimeOffset requireStatusAfterUtc = DateTimeOffset.UtcNow;
        var pending = prepared.Participants
            .ToDictionary(
                participant => participant.Device.DeviceId,
                participant => participant,
                StringComparer.Ordinal);

        for (int attempt = 0;
             attempt < FinalBarrierRetryCount &&
             StartArmWindow.IsNormalArmOpen(MasterClock.NowMicroseconds, prepared.TargetMasterMicroseconds);
             attempt++)
        {
            foreach (PreparedParticipant participant in pending.Values.ToArray())
            {
                await udpService.SendStatusRequestAsync(
                    CreateCommandId(),
                    participant.Address,
                    cancellationToken);
            }

            long normalArmDeadlineMasterUs = checked(
                prepared.TargetMasterMicroseconds - StartArmWindow.NormalArmDeadlineLeadMicroseconds);
            long attemptDeadlineMasterUs = Math.Min(
                normalArmDeadlineMasterUs,
                checked(MasterClock.NowMicroseconds +
                    FinalBarrierRetryWaitMilliseconds * 1000L));

            while (MasterClock.NowMicroseconds < attemptDeadlineMasterUs)
            {
                StartBlock? failure = EvaluateFinalBarrierReplies(
                    prepared,
                    requireStatusAfterUtc,
                    pending);
                if (failure is not null) return failure;
                if (pending.Count == 0) return null;

                long remainingUs = attemptDeadlineMasterUs - MasterClock.NowMicroseconds;
                if (remainingUs <= 0) break;
                await Task.Delay(
                    (int)Math.Min(10L, Math.Max(1L, remainingUs / 1000L)),
                    cancellationToken);
            }

            StartBlock? attemptFailure = EvaluateFinalBarrierReplies(
                prepared,
                requireStatusAfterUtc,
                pending);
            if (attemptFailure is not null) return attemptFailure;
            if (pending.Count == 0) return null;
        }

        if (pending.Count == 0) return null;
        return new StartBlock(
            StartBlockReason.StatusStale,
            string.Join(", ", pending.Keys.OrderBy(id => id, StringComparer.Ordinal)),
            "No fresh final STATUS arrived before the T* - 2.5 s normal ARM deadline; cancelling the frozen run.");
    }

    private static StartBlock? EvaluateFinalBarrierReplies(
        PreparedStartRun prepared,
        DateTimeOffset requireStatusAfterUtc,
        Dictionary<string, PreparedParticipant> pending)
    {
        foreach (string deviceId in pending.Keys.ToArray())
        {
            PreparedParticipant participant = pending[deviceId];
            ParticipantSessionSnapshot current = participant.Device.SessionSnapshot;
            if (!current.LastStatusAtUtc.HasValue ||
                current.LastStatusAtUtc.Value < requireStatusAfterUtc)
            {
                continue;
            }

            StartBlock? block = StartFinalBarrier.EvaluateParticipant(
                deviceId,
                participant.GateSnapshot,
                current,
                prepared.CommandId,
                requireStatusAfterUtc);
            if (block is not null) return block;
            pending.Remove(deviceId);
        }

        return null;
    }

    private async Task<AbortConfirmationResult> ConfirmPreparedRunAbortAsync(
        PreparedStartRun prepared,
        CancellationToken cancellationToken)
    {
        ulong resetCommandId = CreateCommandId();
        var reset = new CommandPacket(
            CommandType.Reset,
            resetCommandId,
            prepared.DurationSeconds,
            0);
        var ackTracker = new ResetAcknowledgementTracker(
            resetCommandId,
            prepared.Participants.Select(participant => participant.Device.DeviceId));
        var pendingStatus = prepared.Participants.ToDictionary(
            participant => participant.Device.DeviceId,
            participant => participant,
            StringComparer.Ordinal);
        DateTimeOffset requireStatusAfterUtc = DateTimeOffset.UtcNow;
        long absoluteConfirmationDeadlineMasterUs = checked(
            prepared.TargetMasterMicroseconds - StartArmWindow.AbortConfirmationDeadlineLeadMicroseconds);
        long confirmationDeadlineMasterUs = Math.Min(
            absoluteConfirmationDeadlineMasterUs,
            checked(MasterClock.NowMicroseconds + StartArmWindow.AbortConfirmationMaximumWindowMicroseconds));

        foreach (PreparedParticipant participant in prepared.Participants)
        {
            participant.Device.MarkPending(resetCommandId);
        }

        Volatile.Write(ref activeAbortResetAcknowledgementTracker, ackTracker);
        try
        {
            while (StartArmWindow.IsAbortConfirmationOpen(
                       MasterClock.NowMicroseconds,
                       prepared.TargetMasterMicroseconds) &&
                   MasterClock.NowMicroseconds < confirmationDeadlineMasterUs)
            {
                HashSet<string> missingAck = ackTracker.MissingParticipants
                    .ToHashSet(StringComparer.Ordinal);

                foreach (PreparedParticipant participant in prepared.Participants)
                {
                    string deviceId = participant.Device.DeviceId;
                    if (missingAck.Contains(deviceId))
                    {
                        await udpService.SendCommandUnicastOnceAsync(
                            reset,
                            participant.Address,
                            cancellationToken);
                    }
                    else if (pendingStatus.ContainsKey(deviceId))
                    {
                        // ACK proves RESET command processing, while this explicit
                        // STATUS request recovers independently if the RESET's
                        // immediate STATUS datagram was the packet that got lost.
                        await udpService.SendStatusRequestAsync(
                            CreateCommandId(),
                            participant.Address,
                            cancellationToken);
                    }
                }

                EvaluateAbortConfirmationStatuses(
                    prepared,
                    resetCommandId,
                    requireStatusAfterUtc,
                    pendingStatus);
                if (ackTracker.AllAcknowledged && pendingStatus.Count == 0)
                {
                    return new AbortConfirmationResult(
                        true,
                        resetCommandId,
                        [],
                        []);
                }

                long remainingUs = confirmationDeadlineMasterUs - MasterClock.NowMicroseconds;
                if (remainingUs <= 0) break;
                await Task.Delay(
                    (int)Math.Min(
                        AbortResetRetryWaitMilliseconds,
                        Math.Max(1L, remainingUs / 1000L)),
                    cancellationToken);

                EvaluateAbortConfirmationStatuses(
                    prepared,
                    resetCommandId,
                    requireStatusAfterUtc,
                    pendingStatus);
                if (ackTracker.AllAcknowledged && pendingStatus.Count == 0)
                {
                    return new AbortConfirmationResult(
                        true,
                        resetCommandId,
                        [],
                        []);
                }
            }

            EvaluateAbortConfirmationStatuses(
                prepared,
                resetCommandId,
                requireStatusAfterUtc,
                pendingStatus);

            IReadOnlyList<string> missingResetAcks = ackTracker.MissingParticipants;
            string[] missingSafeStatuses = pendingStatus.Keys
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            // Confirmation has closed, but one final redundant RESET burst can still
            // cancel an armed device before T*. It is deliberately not treated as
            // proof because no time remains for the required ACK + STATUS evidence.
            foreach (PreparedParticipant participant in prepared.Participants)
            {
                if (!missingResetAcks.Contains(participant.Device.DeviceId, StringComparer.Ordinal) &&
                    !missingSafeStatuses.Contains(participant.Device.DeviceId, StringComparer.Ordinal))
                {
                    continue;
                }

                try
                {
                    await udpService.SendCommandUnicastAsync(
                        reset,
                        participant.Address,
                        CancellationToken.None);
                }
                catch
                {
                    // The result below remains AbortUnconfirmed.
                }
            }

            return new AbortConfirmationResult(
                false,
                resetCommandId,
                missingResetAcks,
                missingSafeStatuses);
        }
        catch
        {
            return new AbortConfirmationResult(
                false,
                resetCommandId,
                ackTracker.MissingParticipants,
                pendingStatus.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Volatile.Write(ref activeAbortResetAcknowledgementTracker, null);
        }
    }

    private static void EvaluateAbortConfirmationStatuses(
        PreparedStartRun prepared,
        ulong resetCommandId,
        DateTimeOffset requireStatusAfterUtc,
        Dictionary<string, PreparedParticipant> pendingStatus)
    {
        foreach (string deviceId in pendingStatus.Keys.ToArray())
        {
            ParticipantSessionSnapshot current = pendingStatus[deviceId].Device.SessionSnapshot;
            if (StartAbortConfirmation.IsParticipantCancellationConfirmed(
                current,
                prepared.CommandId,
                resetCommandId,
                requireStatusAfterUtc))
            {
                pendingStatus.Remove(deviceId);
            }
        }
    }

    private void ReportPreparedRunAbort(
        StartBlock originalFailure,
        AbortConfirmationResult abort)
    {
        string original =
            $"{originalFailure.Reason}: {OperatorDeviceName(originalFailure.DeviceId)}. {originalFailure.Remedy}";
        if (abort.Confirmed)
        {
            StartSynchronizationResult =
                $"{original} | ABORT CONFIRMED: RESET ACK + fresh READY STATUS verified " +
                $"for the full frozen set before T* - " +
                $"{StartArmWindow.AbortConfirmationDeadlineLeadMicroseconds / 1_000_000d:F1} s.";
            StatusMessage =
                "START aborted safely. Cancellation was positively confirmed for every frozen participant; " +
                "press START again for a new synchronization/target.";
            return;
        }

        string missingAck = abort.MissingResetAcknowledgements.Count == 0
            ? "none"
            : string.Join(", ", abort.MissingResetAcknowledgements);
        string missingStatus = abort.MissingSafeStatuses.Count == 0
            ? "none"
            : string.Join(", ", abort.MissingSafeStatuses);
        var unconfirmed = new StartBlock(
            StartBlockReason.AbortUnconfirmed,
            string.Join(", ", abort.MissingResetAcknowledgements
                .Concat(abort.MissingSafeStatuses)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)),
            $"RESET ACK missing: {missingAck}; fresh READY confirmation missing: {missingStatus}. " +
            "One or more devices may still start at the abandoned T*. Do not rely on this run.");
        string abortText = $"{unconfirmed.Reason}: {OperatorDeviceName(unconfirmed.DeviceId)}. {unconfirmed.Remedy}";
        StartSynchronizationResult = $"{original} | {abortText}";
        StatusMessage =
            "ABORT UNCONFIRMED. One or more frozen devices may still execute the abandoned START_AT. " +
            "Wait through T* and verify device state before starting another production run.";
    }

    private sealed record AbortConfirmationResult(
        bool Confirmed,
        ulong ResetCommandId,
        IReadOnlyList<string> MissingResetAcknowledgements,
        IReadOnlyList<string> MissingSafeStatuses);

    private async Task SendBrightnessInternalAsync()
    {
        if (SelectedNetworkInterface is null || !udpService.IsReady)
        {
            StatusMessage = "Select an active physical network interface before changing brightness.";
            return;
        }
        if (double.IsNaN(BrightnessPercentInput) ||
            BrightnessPercentInput != Math.Truncate(BrightnessPercentInput) ||
            BrightnessPercentInput < 0 ||
            BrightnessPercentInput > FactoryProtocol.MaximumBrightnessPercent)
        {
            StatusMessage = "Brightness must be a whole percentage from 0 through 100.";
            return;
        }

        DeviceViewModel[] selected = Devices.Where(device => device.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "Select at least one participant before changing brightness.";
            return;
        }
        if (!await sendLock.WaitAsync(0)) return;

        isSending = true;
        UpdateCanSend();
        try
        {
            byte brightness = checked((byte)BrightnessPercentInput);
            ulong commandId = CreateCommandId();
            var command = new CommandPacket(
                CommandType.Brightness, commandId, 0, 0, 0, brightness);

            var sent = new List<string>();
            var skipped = new List<string>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (DeviceViewModel device in selected)
            {
                if (!device.IsOnlineAt(now) || !device.TryGetIpAddress(out IPAddress? address) ||
                    address is null)
                {
                    skipped.Add(device.DisplayName);
                    continue;
                }

                device.MarkPending(commandId);
                await udpService.SendCommandUnicastAsync(command, address);
                sent.Add(device.DisplayName);
            }

            if (sent.Count == 0)
            {
                BrightnessStatus = "No selected online device received the brightness command.";
                StatusMessage = BrightnessStatus;
                return;
            }

            string skippedText = skipped.Count > 0
                ? $" Skipped offline/unresolved: {string.Join(", ", skipped)}."
                : string.Empty;
            BrightnessStatus =
                $"Brightness {brightness}% sent to {string.Join(", ", sent)}.{skippedText}";
            StatusMessage = BrightnessStatus +
                " The setting is runtime-only; reboot restores the firmware default.";
        }
        catch (Exception exception)
        {
            BrightnessStatus = $"Brightness command failed: {exception.Message}";
            StatusMessage = BrightnessStatus;
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

        DeviceViewModel[] participants = Devices.Where(device => device.IsSelected).ToArray();
        if (participants.Length == 0)
        {
            StatusMessage = "Select at least one participant to RESET.";
            return;
        }
        if (!await sendLock.WaitAsync(0)) return;

        isSending = true;
        UpdateCanSend();
        try
        {
            ulong commandId = CreateCommandId();
            var command = new CommandPacket(CommandType.Reset, commandId, duration, 0);
            var missing = new List<string>();
            foreach (DeviceViewModel device in participants)
            {
                if (!device.TryGetIpAddress(out IPAddress? address) || address is null)
                {
                    missing.Add(device.DisplayName);
                    continue;
                }
                device.MarkPending(commandId);
                device.ClearStartMeasurement();
                await udpService.SendCommandUnicastAsync(command, address);
            }
            clock.Reset(duration);
            productionStartTelemetryContext = null;
            StartSynchronizationResult = "Not measured";
            RefreshClockProperties();
            StatusMessage = missing.Count == 0
                ? $"RESET sent by unicast to {participants.Length} selected participant(s)."
                : $"RESET sent to reachable selected participants; no valid IP for {string.Join(", ", missing)}.";
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

    private static IPAddress ParseRequiredAddress(DeviceViewModel device)
    {
        if (device.TryGetIpAddress(out IPAddress? address) && address is not null) return address;
        throw new InvalidOperationException($"{device.DisplayName} no longer has a valid IP address.");
    }

    private string OperatorDeviceName(string deviceId) =>
        Devices.FirstOrDefault(device =>
            string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal))?.DisplayName ?? deviceId;

    private void ReportStartBlocks(IReadOnlyList<StartBlock> blocks)
    {
        if (blocks.Count == 0) return;
        string details = string.Join(
            " | ",
            blocks.Select(block =>
                $"{OperatorDeviceName(block.DeviceId)}: {block.Remedy}"));
        StartSynchronizationResult = string.Join(
            " | ",
            blocks.Select(block =>
                $"{block.Reason}: {OperatorDeviceName(block.DeviceId)}. {block.Remedy}"));
        StatusMessage = $"START could not begin. {details}";
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
                StatusMessage = $"Wait until all configured devices are discovered. {device.DeviceId} does not have a valid IP address yet.";
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

    private static string BuildSyncPathDelayTargetLabel(int targetIndex) => targetIndex switch
    {
        0 => "ESP01",
        1 => "ESP02",
        2 => "ESP03",
        3 => "ESP04",
        4 => "ESP05",
        _ => "ESP02",
    };

    private static string BuildSyncPathDelayModeLabel(int modeIndex) => modeIndex switch
    {
        0 => "NONE (0/0 ms)",
        1 => "SYMMETRIC (250/250 ms)",
        2 => "ASYMMETRIC FORWARD (250/0 ms)",
        3 => "ASYMMETRIC REVERSE (0/1 ms)",
        4 => "ASYMMETRIC REVERSE (0/4 ms)",
        5 => "ASYMMETRIC FORWARD (40/0 ms)",
        _ => "UNKNOWN",
    };

    private string BuildSyncPathDelayDescription()
    {
        SyncPathDelayMode mode = (SyncPathDelayMode)Math.Clamp(SyncPathDelayModeIndex, 0, 5);
        SyncPathDelayProfile profile = SyncPathDelayExperiment.GetProfile(mode);
        string expectedBias = mode switch
        {
            SyncPathDelayMode.None => "Expected artificial offset bias: 0 ms.",
            SyncPathDelayMode.Symmetric250Milliseconds =>
                "Expected artificial offset bias: ~0 ms; calibration RTT increases by ~500 ms.",
            SyncPathDelayMode.AsymmetricForward250Milliseconds =>
                "Expected applied-offset bias: ~-125 ms; use only as a gross diagnostic.",
            SyncPathDelayMode.AsymmetricReverse1Millisecond =>
                "Expected applied-offset bias: ~+0.5 ms; precise reverse-path positive control.",
            SyncPathDelayMode.AsymmetricReverse4Milliseconds =>
                "Expected applied-offset bias: ~+2.0 ms; precise reverse-path positive control.",
            SyncPathDelayMode.AsymmetricForward40Milliseconds =>
                "Expected applied-offset bias: ~-20 ms; opposite-polarity transfer-function control.",
            _ => string.Empty,
        };
        string target = BuildSyncPathDelayTargetLabel(SyncPathDelayTargetIndex);
        return $"{target} calibration is {profile.MasterToDeviceDelayMilliseconds}/{profile.DeviceToMasterDelayMilliseconds} ms (Master→ESP / ESP→Master). All non-target devices and all verification samples are 0/0 ms. {expectedBias}";
    }

    private bool TryDuration(out uint duration)
    {
        duration = 0;
        if (double.IsNaN(DurationMinutesInput) ||
            double.IsNaN(DurationSecondsInput) ||
            DurationMinutesInput != Math.Truncate(DurationMinutesInput) ||
            DurationSecondsInput != Math.Truncate(DurationSecondsInput) ||
            DurationMinutesInput < 0 || DurationMinutesInput > 99 ||
            DurationSecondsInput < 0 || DurationSecondsInput > 59)
        {
            StatusMessage = "Duration must use whole minutes 0-99 and seconds 0-59.";
            return false;
        }

        uint minutes = (uint)DurationMinutesInput;
        uint seconds = (uint)DurationSecondsInput;
        duration = checked(minutes * 60u + seconds);
        if (duration < FactoryProtocol.MinimumDurationSeconds || duration > 5_999u)
        {
            StatusMessage = "Duration must be from 00:01 through 99:59.";
            duration = 0;
            return false;
        }
        return true;
    }

    private void RefreshStartSynchronizationResult(
        ulong commandId,
        IReadOnlyList<DeviceViewModel> participants,
        string contextLabel)
    {
        DeviceViewModel[] received = participants
            .Where(device =>
                device.LastStartedCommandId == commandId &&
                device.LastStartErrorMicroseconds.HasValue)
            .ToArray();
        if (received.Length != participants.Count)
        {
            StartSynchronizationResult =
                $"{contextLabel}: waiting for STARTED telemetry from frozen set " +
                $"({received.Length}/{participants.Count} received)...";
            return;
        }

        long[] errors = received
            .Select(device => device.LastStartErrorMicroseconds!.Value)
            .ToArray();
        long worst = errors.Max(error => Math.Abs(error));
        long spread = errors.Max() - errors.Min();
        long worstSchedulerLatenessUs = received
            .Max(device => Math.Abs(device.LastSchedulerLatenessMicroseconds ?? long.MaxValue));

        StartSynchronizationResult =
            $"{contextLabel}: worst |error| ≈ {worst / 1000d:F3} ms; " +
            $"{participants.Count}-device start spread ≈ {spread / 1000d:F3} ms; " +
            $"worst |scheduler lateness|={worstSchedulerLatenessUs} µs";
    }

    private void RefreshSynchronizationResolution()
    {
        long[] errors = Devices
            .Where(device => device.IsSynchronized && device.ResidualErrorMicroseconds.HasValue)
            .Select(device => device.ResidualErrorMicroseconds!.Value)
            .ToArray();
        if (errors.Length != Devices.Length)
        {
            SynchronizationResolution = $"All configured devices must complete delay-free verification ({errors.Length}/{Devices.Length} complete).";
            return;
        }

        long worst = errors.Max(error => Math.Abs(error));
        long spread = errors.Max() - errors.Min();
        SynchronizationResolution =
            $"Delay-free verify: worst |device−Master| ≈ {worst / 1000d:F3} ms; {Devices.Length}-device clock spread ≈ {spread / 1000d:F3} ms";
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
        foreach (DeviceViewModel device in Devices)
        {
            device.BeginIntentionalStatusPause();
        }

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

    private void UpdateCanSend()
    {
        CanSend = !isSending && manualStartAtSession is null &&
            SelectedNetworkInterface is not null && udpService.IsReady;
        RefreshManualStartAtButtonStates();
    }

    private void UdpService_PacketReceived(object? sender, PacketReceivedEventArgs e)
    {
        if (e.NetworkInterface == SelectedNetworkInterface && e.Packet is AckPacket commandAck)
        {
            Volatile.Read(ref activeArmAcknowledgementTracker)?.Observe(commandAck);
            Volatile.Read(ref activeAbortResetAcknowledgementTracker)?.Observe(commandAck);
        }

        dispatcherQueue.TryEnqueue(() =>
        {
            if (e.NetworkInterface != SelectedNetworkInterface) return;

            DeviceViewModel? device = Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceId, e.Packet.DeviceId, StringComparison.Ordinal));
            if (device is null)
            {
                StatusMessage = $"Ignored response from unconfigured device {e.Packet.DeviceId}.";
                return;
            }
            switch (e.Packet)
            {
                case AckPacket ack:
                {
                    device.Apply(ack, e.RemoteEndPoint);
                    ManualStartAtSession? manualSession = manualStartAtSession;
                    if (manualSession is not null && ack.CommandId == manualSession.Prepared.CommandId)
                    {
                        StaggeredTestResult = BuildManualStartAtProgressText(manualSession);
                        RefreshManualStartAtButtonStates();
                    }
                    break;
                }
                case StatusPacket status:
                    device.Apply(status, e.RemoteEndPoint);
                    break;
                case StartedPacket started:
                    device.Apply(started, e.RemoteEndPoint);
                    ManualStartAtSession? manualStartedSession = manualStartAtSession;
                    if (manualStartedSession is not null &&
                        started.CommandId == manualStartedSession.Prepared.CommandId)
                    {
                        int received = manualStartedSession.Prepared.Participants.Count(participant =>
                            participant.Device.LastStartedCommandId == manualStartedSession.Prepared.CommandId);
                        StartSynchronizationResult =
                            $"Manual 3-device proof: waiting for frozen-set STARTED telemetry ({received}/{manualStartedSession.Prepared.Participants.Count} received)...";
                    }
                    else
                    {
                        StartTelemetryContext? productionContext = productionStartTelemetryContext;
                        if (productionContext is not null &&
                            started.CommandId == productionContext.CommandId &&
                            productionContext.Participants.Contains(device))
                        {
                            RefreshStartSynchronizationResult(
                                productionContext.CommandId,
                                productionContext.Participants,
                                "Production START");
                        }
                    }
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
        RefreshManualStartAtUi();
    }

    private void RefreshClockProperties()
    {
        ControllerRemaining = FormatMinutesSeconds(clock.RemainingSeconds);
        ControllerState = clock.State.ToString().ToUpperInvariant();
    }

    private static string FormatMinutesSeconds(uint totalSeconds)
    {
        uint minutes = totalSeconds / 60u;
        uint seconds = totalSeconds % 60u;
        return $"{minutes:00}:{seconds:00}";
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
        ManualStartAtSession? manualSession = manualStartAtSession;
        manualStartAtSession = null;
        Volatile.Write(ref activeArmAcknowledgementTracker, null);
        Volatile.Write(ref activeAbortResetAcknowledgementTracker, null);
        manualSession?.Dispose();
        uiTimer.Stop();
        statusDiscovery.DiscoveryError -= StatusDiscovery_DiscoveryError;
        statusDiscovery.Dispose();
        networkSelectionManager.StateChanged -= NetworkSelectionManager_StateChanged;
        networkSelectionManager.Dispose();
        udpService.Dispose();
        sendLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record PreparedParticipant(
        DeviceViewModel Device,
        IPAddress Address,
        StartParticipantGateInput GateSnapshot);

    private sealed record PreparedStartRun(
        ulong CommandId,
        uint DurationSeconds,
        long TargetMasterMicroseconds,
        IReadOnlyList<PreparedParticipant> Participants);

    private sealed record StartTelemetryContext(
        ulong CommandId,
        IReadOnlyList<DeviceViewModel> Participants);

    private sealed class ManualStartAtSession : IDisposable
    {
        private bool disposed;

        public ManualStartAtSession(
            PreparedStartRun prepared,
            ParticipantReservationManager.ParticipantReservation reservation,
            ArmAcknowledgementTracker ackTracker)
        {
            Prepared = prepared;
            Reservation = reservation;
            AckTracker = ackTracker;
        }

        public PreparedStartRun Prepared { get; }
        public ParticipantReservationManager.ParticipantReservation Reservation { get; }
        public ArmAcknowledgementTracker AckTracker { get; }
        public Dictionary<string, ManualStartAtSendRecord> Sends { get; } =
            new(StringComparer.Ordinal);
        public CancellationTokenSource Cancellation { get; } = new();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Cancellation.Cancel();
            Cancellation.Dispose();
            Reservation.Dispose();
        }
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
        bool StartedTelemetryReceived,
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
                true,
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
                commandId != 0 && device.LastStartedCommandId == commandId,
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
            StartedTelemetryReceived ? "1" : "0",
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
        long? ActualForwardDelayUs,
        long? ActualReverseDelayUs,
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
            "PathForwardDelayMs,PathReverseDelayMs,ActualForwardDelayUs,ActualReverseDelayUs,MasterT1Us,DeviceT2Us,DeviceT3Us,MasterT4Us," +
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
            Number(ActualForwardDelayUs),
            Number(ActualReverseDelayUs),
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

    private sealed record SyncMeasurement(
        ulong SyncId,
        ClockSyncSample Sample,
        long ActualForwardDelayUs,
        uint ActualReverseDelayUs);
}
