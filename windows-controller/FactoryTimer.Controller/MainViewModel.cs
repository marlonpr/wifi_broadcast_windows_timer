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
    private static readonly TimeSpan RtcQualificationWaitTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RtcQualificationObservationInterval = TimeSpan.FromMilliseconds(500);
    private const int ConsensusLowRttSampleCount = 3;
    private const long SyncQualityThresholdMicroseconds = 3_000; // legacy/manual diagnostics only
    private const long ForwardTop3SpreadRetryLimitMicroseconds = 150;
    // Diagnostic/uncertainty reference only in v6.22.7.  Cal/verification
    // disagreement no longer triggers a retry; only the cumulative pooled top-3
    // forward-floor spread does.
    private const long ForwardCalibrationVerificationDeltaReferenceMicroseconds = 500;
    private const int ForwardRoundRobinInterDeviceDelayMilliseconds = 2;
    private const int MaxSynchronizationAttempts = 5;
    private const int SynchronizationRetryQuietMilliseconds = 150;
    private const int StatusDiscoveryDrainMilliseconds = 100;
    private const int RecoveredRunningStatusGraceMilliseconds = 2000;
    private readonly DispatcherQueue dispatcherQueue;
    private readonly DispatcherQueueTimer uiTimer;
    private readonly UdpControllerService udpService;
    private readonly StatusDiscoveryService statusDiscovery;
    private readonly NetworkInterfaceSelectionManager networkSelectionManager;
    private readonly ControllerClock clock = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly ParticipantReservationManager reservationManager = new();
    private readonly object controllerTxTraceGate = new();
    private ControllerTxTraceSession? controllerTxTraceSession;
    private CancellationTokenSource? productionSilentRunCancellation;
    private Task productionSilentRunTask = Task.CompletedTask;
    private bool productionSilentRunActive;
    private bool productionSilentRunOwnsExactWindow;
    private bool degradedStartPending;
    private string degradedStartParticipantKey = string.Empty;
    private bool allowDegradedSyncStartOnce;
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
    private string startSynchronizationResult = "Ready — press START to check all start requirements.";
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
    private string controllerTxTracePath = "TX trace: —";
    private string analyzerComPort = string.Empty;
    private bool analyzerEnabled;
    private double benchmarkTrialsInput = 10;
    private int syncPathDelayModeIndex;
    private int syncPathDelayTargetIndex = 1; // ESP02 remains the default experiment target.
    private int syncSamplingModeIndex;
    private int receiveTimestampModeIndex = (int)UdpReceiveTimestampMode.DedicatedBlockingThread;
    private int productionSyncOrderModeIndex;
    private int productionSyncSeriesCompletedStarts;
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
        udpService.PacketSent += UdpService_PacketSent;
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
    public string ControllerTxTracePath { get => controllerTxTracePath; private set => SetProperty(ref controllerTxTracePath, value); }
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
    public bool CanStartDegraded => degradedStartPending && CanSend;
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

    public int ProductionSyncOrderModeIndex
    {
        get => productionSyncOrderModeIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, 2);
            if (!SetProperty(ref productionSyncOrderModeIndex, clamped)) return;
            productionSyncSeriesCompletedStarts = 0;
            OnPropertyChanged(nameof(ProductionSyncOrderDescription));
            StatusMessage = clamped switch
            {
                0 => "Round-robin sync base order: normal. The first device rotates every round.",
                1 => "Round-robin sync base order: reverse. The first device rotates every round.",
                2 => "Round-robin diagnostic series enabled: successful STARTs alternate the base order; every round still rotates its first device.",
                _ => "Round-robin sync base order updated.",
            };
        }
    }

    public string ProductionSyncOrderDescription => ProductionSyncOrderModeIndex switch
    {
        0 => "Normal base order; each of the 16 production sync rounds rotates the first device.",
        1 => "Reverse base order; each of the 16 production sync rounds rotates the first device.",
        2 => $"Alternate diagnostic: next successful run #{productionSyncSeriesCompletedStarts + 1} uses " +
             (((productionSyncSeriesCompletedStarts + 1) & 1) == 1 ? "NORMAL" : "REVERSE") +
             " as the base order. Each round rotates the first device; counter advances only after a successful production START.",
        _ => string.Empty,
    };

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

                if (SelectedNetworkInterface is not null && !timingQuietPeriodActive && !productionSilentRunActive)
                {
                    statusDiscovery.StartOrRestart(
                        SelectedNetworkInterface,
                        () => Volatile.Read(ref manualBroadcastOverride),
                        GetRunStatusUnicastTargets);
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

    public Task SendDegradedStartAsync()
    {
        if (!degradedStartPending)
        {
            SetStartDiagnostic(
                "START ANYWAY is unavailable: no previously qualified degraded synchronization is waiting for confirmation.");
            return Task.CompletedTask;
        }

        allowDegradedSyncStartOnce = true;
        return SendStartAtAsync();
    }

    public Task PrepareManualStartAtTestAsync() => PrepareManualStartAtTestInternalAsync();

    public Task SendManualStartAtToDeviceAsync(string deviceId) =>
        SendManualStartAtToDeviceInternalAsync(deviceId);

    public Task CancelManualStartAtTestAsync() => CancelManualStartAtTestInternalAsync();

    public Task SendResetAsync() => SendResetInternalAsync();

    public Task SendBrightnessAsync() => SendBrightnessInternalAsync();

    public Task RefreshSelectedStatusAsync() => RefreshSelectedStatusInternalAsync();

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

    private static ForwardSyncConsensus SelectForwardIngressMedianOfThree(
        IReadOnlyList<SyncMeasurement> samples,
        int sampleCount)
    {
        // Production v6.22.7 keeps the median of the three fastest forward-ingress
        // samples (the second-fastest sample).  The 10-run post-v6.22 analyzer
        // series exposed one case where the third-fastest sample was ~200 us
        // slower than the first two; averaging all three moved b0 by ~60 us.
        // The median keeps the one-way/reply-path immunity of v6.22 while being
        // robust to one unusually slow member of the top-three set.
        if (sampleCount != 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleCount),
                sampleCount,
                "Forward-only production synchronization requires exactly three floor candidates.");
        }

        var selected = samples
            .Where(item => item.Sample.IsValid &&
                           item.IngressLocalUs > 0 &&
                           item.IngressLocalUs <= item.Sample.DeviceT2Microseconds &&
                           item.Sample.DeviceT2Microseconds - item.IngressLocalUs <= 20_000)
            .Select(item => new
            {
                Measurement = item,
                ForwardOffsetUs = checked(
                    item.Sample.MasterT1Microseconds - item.IngressLocalUs),
            })
            .OrderByDescending(item => item.ForwardOffsetUs)
            .ThenBy(item => item.Measurement.Sample.NetworkRttMicroseconds)
            .Take(sampleCount)
            .ToArray();

        if (selected.Length < sampleCount)
        {
            throw new InvalidOperationException(
                $"Forward-only synchronization requires {sampleCount} valid v6.22 ingress timestamps; " +
                $"received {selected.Length}. Flash v6.22 firmware on this timer.");
        }

        // selected[] is ordered fastest-to-slowest, so index 1 is the median
        // of the three floor candidates.  Keep the estimator epoch tied to the
        // same physical ingress sample so firmware can propagate it through
        // the disciplined timebase without an artificial averaging epoch.
        var representative = selected[1];
        long offsetUs = representative.ForwardOffsetUs;
        long epochLocalUs = representative.Measurement.IngressLocalUs;
        long effectiveMasterEpochUs = checked(epochLocalUs + offsetUs);
        long spreadUs = checked(selected[0].ForwardOffsetUs - selected[2].ForwardOffsetUs);
        long bestRttUs = selected.Min(item => item.Measurement.Sample.NetworkRttMicroseconds);
        string provenance = string.Join(
            ",",
            selected.Select(item =>
            {
                SyncMeasurement measurement = item.Measurement;
                string phase = measurement.ForwardPhase switch
                {
                    ForwardSyncPhase.Calibration => "C",
                    ForwardSyncPhase.Verification => "V",
                    _ => "?",
                };
                return $"a{measurement.ForwardAttempt}{phase}r{measurement.ForwardRound + 1}";
            }));

        return new ForwardSyncConsensus(
            representative.Measurement.SyncId,
            offsetUs,
            epochLocalUs,
            effectiveMasterEpochUs,
            bestRttUs,
            spreadUs,
            provenance);
    }

    private static ForwardSyncAttemptCandidate BuildForwardSyncAttemptCandidate(
        IReadOnlyList<SyncMeasurement> calibration,
        IReadOnlyList<SyncMeasurement> verification,
        SyncSamplingProfile samplingProfile,
        int attempt,
        long? initialResidualMicroseconds = null)
    {
        ForwardSyncConsensus calibrationConsensus = SelectForwardIngressMedianOfThree(
            calibration,
            samplingProfile.LowRttSampleCount);
        ForwardSyncConsensus verificationConsensus = SelectForwardIngressMedianOfThree(
            verification,
            samplingProfile.LowRttSampleCount);
        ForwardSyncConsensus finalConsensus = SelectForwardIngressMedianOfThree(
            calibration.Concat(verification).ToArray(),
            samplingProfile.LowRttSampleCount);

        long residual = checked(
            calibrationConsensus.MasterMinusLocalOffsetMicroseconds -
            verificationConsensus.MasterMinusLocalOffsetMicroseconds);
        long absoluteDelta = residual == long.MinValue ? long.MaxValue : Math.Abs(residual);
        long uncertainty = checked(
            absoluteDelta + finalConsensus.TopSampleSpreadMicroseconds);

        return new ForwardSyncAttemptCandidate(
            calibrationConsensus,
            verificationConsensus,
            finalConsensus,
            residual,
            uncertainty,
            attempt,
            initialResidualMicroseconds ?? residual);
    }

    private static bool ForwardSyncNeedsRetry(ForwardSyncAttemptCandidate candidate) =>
        candidate.FinalConsensus.TopSampleSpreadMicroseconds >
            ForwardTop3SpreadRetryLimitMicroseconds;

    private void RecordForwardSyncCandidate(
        DeviceViewModel device,
        ForwardSyncAttemptCandidate candidate,
        bool accepted,
        bool degraded)
    {
        SyncQualityEvaluation legacyQuality = SyncQualityPolicy.Evaluate(
            candidate.ResidualMicroseconds,
            expectedBiasMicroseconds: 0,
            thresholdMicroseconds: SyncQualityThresholdMicroseconds);

        device.ApplySynchronizationMeasurement(
            candidate.FinalConsensus.MasterMinusLocalOffsetMicroseconds,
            candidate.FinalConsensus.BestRttMicroseconds,
            candidate.ResidualMicroseconds,
            candidate.VerificationConsensus.BestRttMicroseconds,
            candidate.VerificationConsensus.MasterMinusLocalOffsetMicroseconds,
            candidate.InitialResidualMicroseconds,
            0,
            legacyQuality.DeviationMicroseconds,
            SyncQualityThresholdMicroseconds,
            candidate.FinalConsensus.EffectiveMasterEpochMicroseconds,
            candidate.Attempt,
            MaxSynchronizationAttempts,
            accepted,
            measuredUncertaintyMicroseconds: candidate.UncertaintyMicroseconds,
            measuredForwardTop3SpreadMicroseconds:
                candidate.FinalConsensus.TopSampleSpreadMicroseconds,
            degraded: degraded);
    }

    private async Task ApplyForwardSyncCandidateAsync(
        DeviceViewModel device,
        IPAddress address,
        ForwardSyncAttemptCandidate candidate,
        bool degraded,
        CancellationToken cancellationToken)
    {
        var syncSet = new SyncSetPacket(
            candidate.FinalConsensus.RepresentativeSyncId,
            candidate.FinalConsensus.MasterMinusLocalOffsetMicroseconds,
            candidate.FinalConsensus.BestRttMicroseconds,
            candidate.FinalConsensus.OffsetEpochLocalMicroseconds);

        SyncAppliedPacket applied = await udpService.ApplySyncAsync(
            syncSet,
            address,
            cancellationToken: cancellationToken);
        if (!string.Equals(applied.DeviceId, device.DeviceId, StringComparison.Ordinal) ||
            applied.MasterMinusLocalOffsetMicroseconds != syncSet.MasterMinusLocalOffsetMicroseconds ||
            applied.OffsetEpochLocalMicroseconds != syncSet.OffsetEpochLocalMicroseconds)
        {
            throw new InvalidOperationException(
                $"{device.DeviceId} returned an inconsistent v6.22 SYNC_APPLIED response.");
        }

        RecordForwardSyncCandidate(device, candidate, accepted: true, degraded: degraded);
    }

    private static DeviceViewModel[] RotateRoundRobinOrder(
        IReadOnlyList<DeviceViewModel> participants,
        int round)
    {
        if (participants.Count == 0) return [];
        int start = ((round % participants.Count) + participants.Count) % participants.Count;
        var ordered = new DeviceViewModel[participants.Count];
        for (int index = 0; index < participants.Count; index++)
        {
            ordered[index] = participants[(start + index) % participants.Count];
        }
        return ordered;
    }

    private async Task SynchronizeParticipantsForwardOnlyRoundRobinAsync(
        DeviceViewModel[] baseOrder,
        SyncSamplingProfile samplingProfile,
        CancellationToken cancellationToken = default)
    {
        if (baseOrder.Length == 0) return;

        SyncPathDelayProfile noDelay = SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None);
        var addresses = new Dictionary<string, IPAddress>(StringComparer.Ordinal);
        var calibrationPool = new Dictionary<string, List<SyncMeasurement>>(StringComparer.Ordinal);
        var verificationPool = new Dictionary<string, List<SyncMeasurement>>(StringComparer.Ordinal);
        var initialResidual = new Dictionary<string, long?>(StringComparer.Ordinal);
        var latestCandidate = new Dictionary<string, ForwardSyncAttemptCandidate>(StringComparer.Ordinal);
        var pending = new List<DeviceViewModel>(baseOrder);
        int roundSequence = 0;

        foreach (DeviceViewModel device in baseOrder)
        {
            if (!device.TryGetIpAddress(out IPAddress? address) || address is null)
            {
                throw new InvalidOperationException($"{device.DeviceId} has no current IP address.");
            }
            addresses[device.DeviceId] = address;
            calibrationPool[device.DeviceId] = [];
            verificationPool[device.DeviceId] = [];
            initialResidual[device.DeviceId] = null;
        }

        async Task MeasureRoundAsync(
            IReadOnlyList<DeviceViewModel> retrySet,
            int attempt,
            Dictionary<string, List<SyncMeasurement>> destination,
            ForwardSyncPhase phase)
        {
            if (retrySet.Count == 0) return;

            DeviceViewModel[] order = RotateRoundRobinOrder(retrySet, roundSequence);
            int thisRound = roundSequence++;
            string phaseName = phase == ForwardSyncPhase.Calibration ? "calibration" : "verification";

            for (int index = 0; index < order.Length; index++)
            {
                DeviceViewModel device = order[index];
                SetStartDiagnostic(
                    $"START: pooled forward-sync attempt {attempt}/{MaxSynchronizationAttempts}, " +
                    $"{phaseName} round {thisRound + 1}: {device.DisplayName} " +
                    $"({index + 1}/{order.Length}), first={order[0].DeviceId}.");
                try
                {
                    ulong syncId = CreateCommandId();
                    SyncMeasurement measurement = await MeasureSyncAsync(
                        device.DeviceId,
                        addresses[device.DeviceId],
                        noDelay,
                        syncId,
                        cancellationToken);
                    destination[device.DeviceId].Add(measurement with
                    {
                        ForwardAttempt = attempt,
                        ForwardPhase = phase,
                        ForwardRound = thisRound,
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Keep every successful observation already collected.  A transport
                    // miss never erases prior floor evidence; the device remains eligible
                    // for later pooled retry rounds.
                }

                if (index + 1 < order.Length)
                {
                    await Task.Delay(ForwardRoundRobinInterDeviceDelayMilliseconds, cancellationToken);
                }
            }
        }

        for (int attempt = 1; attempt <= MaxSynchronizationAttempts && pending.Count > 0; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(SynchronizationRetryQuietMilliseconds, cancellationToken);
            }

            DeviceViewModel[] retrySet = pending.ToArray();
            int retrySetSize = retrySet.Length;

            for (int round = 0; round < samplingProfile.CalibrationSampleCount; round++)
            {
                await MeasureRoundAsync(
                    retrySet,
                    attempt,
                    calibrationPool,
                    ForwardSyncPhase.Calibration);
            }

            await Task.Delay(25, cancellationToken);

            for (int round = 0; round < samplingProfile.VerificationSampleCount; round++)
            {
                await MeasureRoundAsync(
                    retrySet,
                    attempt,
                    verificationPool,
                    ForwardSyncPhase.Verification);
            }

            var stillPending = new List<DeviceViewModel>();
            foreach (DeviceViewModel device in retrySet)
            {
                List<SyncMeasurement> cal = calibrationPool[device.DeviceId];
                List<SyncMeasurement> ver = verificationPool[device.DeviceId];

                if (cal.Count < samplingProfile.LowRttSampleCount ||
                    ver.Count < samplingProfile.LowRttSampleCount)
                {
                    device.MarkSynchronizationAttemptFailed(
                        attempt,
                        MaxSynchronizationAttempts,
                        $"pooled sample shortage ({cal.Count}+{ver.Count})");
                    stillPending.Add(device);
                    SetStartDiagnostic(
                        $"START: {device.DeviceId} pooled RETRY {attempt}/{MaxSynchronizationAttempts} — " +
                        $"only {cal.Count} calibration + {ver.Count} verification samples available; " +
                        $"prior valid samples are retained for the next round-robin retry.");
                    continue;
                }

                ForwardSyncAttemptCandidate candidate = BuildForwardSyncAttemptCandidate(
                    cal,
                    ver,
                    samplingProfile,
                    attempt,
                    initialResidual[device.DeviceId]);
                initialResidual[device.DeviceId] ??= candidate.ResidualMicroseconds;
                latestCandidate[device.DeviceId] = candidate;

                if (ForwardSyncNeedsRetry(candidate))
                {
                    RecordForwardSyncCandidate(
                        device,
                        candidate,
                        accepted: false,
                        degraded: false);
                    stillPending.Add(device);

                    SetStartDiagnostic(
                        $"START: {device.DeviceId} pooled RETRY {attempt}/{MaxSynchronizationAttempts} — " +
                        $"pool={cal.Count + ver.Count} samples, pooled top3 spread=" +
                        $"{candidate.FinalConsensus.TopSampleSpreadMicroseconds} us " +
                        $"(retry >{ForwardTop3SpreadRetryLimitMicroseconds}); " +
                        $"pooled cal/verify delta={candidate.ResidualMicroseconds:+#;-#;0} us " +
                        $"(diagnostic/uncertainty only; reference " +
                        $"{ForwardCalibrationVerificationDeltaReferenceMicroseconds} us), " +
                        $"uncertainty ±{candidate.UncertaintyMicroseconds} us; " +
                        $"top3={candidate.FinalConsensus.Top3Provenance}. " +
                        $"Retry set size={retrySetSize}; all successful evidence is retained.");
                    continue;
                }

                try
                {
                    await ApplyForwardSyncCandidateAsync(
                        device,
                        addresses[device.DeviceId],
                        candidate,
                        degraded: false,
                        cancellationToken);
                    StatusMessage =
                        $"{device.DeviceId} pooled forward-only sync PASS: med3 of " +
                        $"{cal.Count + ver.Count} retained ingress samples; " +
                        $"top3 spread={candidate.FinalConsensus.TopSampleSpreadMicroseconds} us, " +
                        $"cal/verify delta={candidate.ResidualMicroseconds:+#;-#;0} us " +
                        $"(diagnostic only), uncertainty ±{candidate.UncertaintyMicroseconds} us; " +
                        $"top3={candidate.FinalConsensus.Top3Provenance}.";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    SelectedNetworkInterface is not null &&
                    udpService.IsReady &&
                    SyncRetryPolicy.IsRetryableTransportFailure(exception))
                {
                    device.MarkSynchronizationAttemptFailed(
                        attempt,
                        MaxSynchronizationAttempts,
                        "pooled SYNC_SET transport");
                    stillPending.Add(device);
                    SetStartDiagnostic(
                        $"START: {device.DeviceId} pooled SYNC_SET RETRY {attempt}/{MaxSynchronizationAttempts} — " +
                        $"{exception.Message}. Existing pooled evidence is retained.");
                }
            }

            pending = stillPending;
        }

        if (pending.Count > 0)
        {
            foreach (DeviceViewModel device in pending)
            {
                if (!latestCandidate.TryGetValue(device.DeviceId, out ForwardSyncAttemptCandidate? candidate) ||
                    candidate is null)
                {
                    device.MarkSynchronizationQualityFailed(MaxSynchronizationAttempts);
                    throw new InvalidOperationException(
                        $"{device.DeviceId} produced fewer than {samplingProfile.LowRttSampleCount} usable " +
                        $"calibration or verification observations after {MaxSynchronizationAttempts} pooled attempts.");
                }

                await ApplyForwardSyncCandidateAsync(
                    device,
                    addresses[device.DeviceId],
                    candidate,
                    degraded: true,
                    cancellationToken);

                SetStartDiagnostic(
                    $"START: {device.DeviceId} pooled retries exhausted; retaining cumulative DEGRADED sync " +
                    $"after {candidate.Attempt}/{MaxSynchronizationAttempts} attempts: " +
                    $"pool={calibrationPool[device.DeviceId].Count + verificationPool[device.DeviceId].Count}, " +
                    $"top3 spread={candidate.FinalConsensus.TopSampleSpreadMicroseconds} us, " +
                    $"cal/verify delta={candidate.ResidualMicroseconds:+#;-#;0} us, " +
                    $"measured uncertainty ±{candidate.UncertaintyMicroseconds} us, " +
                    $"top3={candidate.FinalConsensus.Top3Provenance}. " +
                    $"The 20 ms fleet readiness bound remains authoritative.");
            }
        }

        int degradedCount = baseOrder.Count(device => device.SynchronizationDegraded);
        SetStartDiagnostic(
            $"START: pooled round-robin forward sync complete on {baseOrder.Length} timer(s): " +
            $"cumulative med3 floor; retry only when pooled top3 spread >" +
            $"{ForwardTop3SpreadRetryLimitMicroseconds} us; cal/verify delta is diagnostic + uncertainty only; " +
            $"retrying devices were sampled together in rotating rounds; degraded-after-retry={degradedCount}. " +
            $"The 20 ms readiness gate remains authoritative.");
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
            reply.ActualArtificialReplyDelayMicroseconds,
            reply.IngressLocalMicroseconds);
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

    private DeviceViewModel[] ResolveProductionSyncOrder(
        DeviceViewModel[] participants,
        out string orderLabel,
        out int? seriesRunNumber)
    {
        seriesRunNumber = null;
        bool reverse = ProductionSyncOrderModeIndex == 1;
        if (ProductionSyncOrderModeIndex == 2)
        {
            seriesRunNumber = productionSyncSeriesCompletedStarts + 1;
            reverse = (seriesRunNumber.Value & 1) == 0;
        }

        DeviceViewModel[] ordered = reverse
            ? participants.Reverse().ToArray()
            : participants.ToArray();
        orderLabel = string.Join(">", ordered.Select(device => device.DeviceId));
        return ordered;
    }

    private void RecordSuccessfulProductionSyncSeriesStart()
    {
        if (ProductionSyncOrderModeIndex != 2) return;
        productionSyncSeriesCompletedStarts++;
        OnPropertyChanged(nameof(ProductionSyncOrderDescription));
    }

    private static string BuildProductionParticipantKey(IEnumerable<DeviceViewModel> participants) =>
        string.Join("|", participants.Select(device => device.DeviceId).OrderBy(id => id, StringComparer.Ordinal));

    private void SetDegradedStartPending(IReadOnlyList<DeviceViewModel> participants)
    {
        degradedStartParticipantKey = BuildProductionParticipantKey(participants);
        degradedStartPending = true;
        OnPropertyChanged(nameof(CanStartDegraded));
    }

    private void ClearDegradedStartPending()
    {
        degradedStartPending = false;
        degradedStartParticipantKey = string.Empty;
        OnPropertyChanged(nameof(CanStartDegraded));
    }

    private bool CanReusePendingDegradedSync(IReadOnlyList<DeviceViewModel> participants)
    {
        if (!degradedStartPending ||
            !string.Equals(
                degradedStartParticipantKey,
                BuildProductionParticipantKey(participants),
                StringComparison.Ordinal))
        {
            return false;
        }

        bool anyDegraded = false;
        foreach (DeviceViewModel device in participants)
        {
            ParticipantSessionSnapshot session = device.SessionSnapshot;
            if (!device.IsSynchronized ||
                !device.SynchronizationSessionGeneration.HasValue ||
                device.SynchronizationSessionGeneration.Value != session.Generation)
            {
                return false;
            }
            anyDegraded |= device.SynchronizationDegraded;
        }
        return anyDegraded;
    }

    private static string BuildDegradedSyncSummary(IEnumerable<DeviceViewModel> devices)
    {
        return string.Join(
            "; ",
            devices
                .Where(device => device.SynchronizationDegraded)
                .Select(device =>
                    $"{device.DeviceId}: ±{device.SynchronizationUncertaintyMicroseconds ?? 0} us " +
                    $"(final spread {device.ForwardTop3SpreadMicroseconds ?? 0} us, " +
                    $"delta {(device.ResidualErrorMicroseconds ?? 0):+#;-#;0} us)"));
    }

    private static string BuildSyncQualityTraceSummary(IEnumerable<PreparedParticipant> participants)
    {
        return string.Join(
            ";",
            participants.Select(participant =>
            {
                DeviceViewModel device = participant.Device;
                return $"{device.DeviceId}:degraded={(device.SynchronizationDegraded ? 1 : 0)}" +
                    $":delta={device.ResidualErrorMicroseconds ?? 0}" +
                    $":spread={device.ForwardTop3SpreadMicroseconds ?? 0}" +
                    $":uncertainty={device.SynchronizationUncertaintyMicroseconds ?? 0}";
            }));
    }

    private async Task SendStartAtAsync()
    {
        bool requestedDegradedConfirmation = allowDegradedSyncStartOnce;
        allowDegradedSyncStartOnce = false;

        SetStartDiagnostic(
            requestedDegradedConfirmation
                ? "START ANYWAY (DEGRADED): validating the retained synchronization and 20 ms readiness bound..."
                : "START: checking network, duration, selected participants, RTC qualification, synchronization and ARM requirements...");

        if (SelectedNetworkInterface is null || !udpService.IsReady)
        {
            SetStartDiagnostic(
                "START BLOCKED — NETWORK: Select an active physical network interface before sending a command.");
            return;
        }
        if (!TryDuration(out uint duration))
        {
            StartSynchronizationResult = $"START BLOCKED — DURATION: {StatusMessage}";
            return;
        }

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

        if (requestedDegradedConfirmation &&
            (!degradedStartPending ||
             !string.Equals(
                 degradedStartParticipantKey,
                 BuildProductionParticipantKey(participants),
                 StringComparison.Ordinal)))
        {
            ClearDegradedStartPending();
            SetStartDiagnostic(
                "START ANYWAY cancelled: the selected participant set no longer matches the retained degraded synchronization. Press normal START for a fresh sync.");
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
            if (!await sendLock.WaitAsync(0))
            {
                SetStartDiagnostic(
                    "START BLOCKED — CONTROLLER BUSY: Another controller operation is still active. Wait for it to finish and press START again.");
                return;
            }

            bool timingQuiet = false;
            PreparedStartRun? preparedForAbort = null;
            bool startAtMayHaveBeenSent = false;
            isSending = true;
            productionStartTelemetryContext = null;
            UpdateCanSend();
            try
            {
                SetStartDiagnostic(
                    $"START: preflight check for {participants.Length} selected timer(s). Requesting fresh STATUS and RTC qualification...");

                // A fresh preflight STATUS prevents a restarted controller from sending
                // SYNC_SET to a device that is already ARMED/RUNNING in an unknown run.
                DateTimeOffset preflightAfterUtc = DateTimeOffset.UtcNow;
                IReadOnlyList<StartBlock> preflightBlocks = await RefreshAndValidatePreflightAsync(
                    participants,
                    preflightAfterUtc,
                    CancellationToken.None,
                    waitForRtcQualification: true);
                if (preflightBlocks.Count != 0)
                {
                    ReportStartBlocks(preflightBlocks);
                    return;
                }

                DeviceViewModel[] syncParticipants = ResolveProductionSyncOrder(
                    participants,
                    out string productionSyncOrderLabel,
                    out int? productionSyncSeriesRunNumber);

                bool reusePendingDegradedSync =
                    requestedDegradedConfirmation && CanReusePendingDegradedSync(participants);
                if (requestedDegradedConfirmation && !reusePendingDegradedSync)
                {
                    ClearDegradedStartPending();
                    SetStartDiagnostic(
                        "START ANYWAY cancelled: the retained degraded sync is stale or the device session changed. Press normal START for a fresh sync.");
                    return;
                }

                if (!reusePendingDegradedSync)
                {
                    ClearDegradedStartPending();
                    SetStartDiagnostic(
                        "START: preflight requirements passed. Entering timing-quiet synchronization...");
                    await EnterTimingQuietPeriodAsync(CancellationToken.None);
                    timingQuiet = true;

                    SyncSamplingProfile productionSampling =
                        SyncSamplingExperiment.GetProfile(SyncSamplingMode.Baseline8Plus8);

                    foreach (DeviceViewModel device in participants)
                    {
                        device.MarkSynchronizing();
                        device.ClearStartMeasurement();
                    }

                    try
                    {
                        string syncStartMessage = productionSyncSeriesRunNumber.HasValue
                            ? $"START: SYNC series run {productionSyncSeriesRunNumber}; base order {productionSyncOrderLabel}; starting cumulative pooled round-robin med3 synchronization..."
                            : $"START: SYNC base order {productionSyncOrderLabel}; starting cumulative pooled round-robin med3 synchronization...";
                        SetStartDiagnostic(syncStartMessage);
                        await SynchronizeParticipantsForwardOnlyRoundRobinAsync(
                            syncParticipants,
                            productionSampling,
                            CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        ReportStartBlocks([
                            new StartBlock(
                                StartBlockReason.SyncFailed,
                                "SYNC",
                                $"Press START again for a fresh sync. {exception.Message}")]);
                        return;
                    }

                    // Resume ordinary STATUS only after all foreground SYNC traffic is done.
                    ExitTimingQuietPeriod();
                    timingQuiet = false;
                }
                else
                {
                    SetStartDiagnostic(
                        "START ANYWAY (DEGRADED): reusing the retained synchronization; no new SYNC traffic is being generated. Rechecking STATUS/session and the 20 ms readiness bound...");
                }

                SetStartDiagnostic(
                    "START: synchronization passed. Requesting fresh post-sync STATUS from every selected timer...");
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
                    ClearDegradedStartPending();
                    ReportStartBlocks(gateResult.Blocks);
                    return;
                }

                DeviceViewModel[] degradedDevices = participants
                    .Where(device => device.SynchronizationDegraded)
                    .ToArray();
                if (degradedDevices.Length != 0 && !requestedDegradedConfirmation)
                {
                    SetDegradedStartPending(participants);
                    string degradedSummary = BuildDegradedSyncSummary(degradedDevices);
                    string pairText = gateResult.WorstPairStartUncertaintyMicroseconds.HasValue
                        ? $"{gateResult.WorstPairStartUncertaintyMicroseconds.Value} us"
                        : "single-device / no pair value";
                    SetStartDiagnostic(
                        $"START PAUSED — DEGRADED SYNC retained after retry budget. {degradedSummary}. " +
                        $"The existing 20 ms readiness gate PASSES (predicted worst pair {pairText}). " +
                        "Click START ANYWAY (DEGRADED) to reuse this exact synchronization without rerunning SYNC, " +
                        "or click normal START for a fresh synchronization.");
                    StatusMessage =
                        $"Degraded synchronization is within the 20 ms safety bound; explicit confirmation required. {degradedSummary}";
                    return;
                }

                if (degradedDevices.Length != 0)
                {
                    ClearDegradedStartPending();
                    string degradedSummary = BuildDegradedSyncSummary(degradedDevices);
                    string pairText = gateResult.WorstPairStartUncertaintyMicroseconds.HasValue
                        ? $"{gateResult.WorstPairStartUncertaintyMicroseconds.Value} us"
                        : "single-device / no pair value";
                    SetStartDiagnostic(
                        $"START ANYWAY CONFIRMED — DEGRADED SYNC: {degradedSummary}. " +
                        $"20 ms readiness gate PASSED; predicted worst pair {pairText}. Proceeding to ARMING...");
                }
                else
                {
                    ClearDegradedStartPending();
                    SetStartDiagnostic(
                        $"START: all readiness requirements passed for {participants.Length} timer(s). Freezing participant set and preparing ARMING...");
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

                // Start a low-overhead in-memory TX trace before any START_AT
                // arm traffic is sent. Every application UDP send is timestamped
                // in the same QPC-derived master domain as T*. File I/O is
                // deferred until after the countdown, so tracing does not move
                // the packet timestamp or add disk latency to the send path.
                BeginControllerTxTrace(
                    prepared.CommandId,
                    prepared.TargetMasterMicroseconds,
                    prepared.DurationSeconds,
                    prepared.Participants.Select(participant => participant.Address).ToArray(),
                    prepared.Participants.Select(participant => participant.Device.DeviceId).ToArray(),
                    productionSyncOrderLabel,
                    BuildSyncQualityTraceSummary(prepared.Participants));

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
                SetStartDiagnostic(
                    $"START: ARMING {prepared.Participants.Count} selected timer(s); waiting for ACKs and final STATUS barrier...");

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

                await StartProductionSilentRunAsync(prepared, CancellationToken.None);
                RecordSuccessfulProductionSyncSeriesStart();

                preparedForAbort = null;
                productionStartTelemetryContext = new StartTelemetryContext(
                    prepared.CommandId,
                    prepared.Participants.Select(participant => participant.Device).ToArray());
                RefreshStartSynchronizationResult(
                    productionStartTelemetryContext.CommandId,
                    productionStartTelemetryContext.Participants,
                    "Production START");
                string seriesPrefix = productionSyncSeriesRunNumber.HasValue
                    ? $"Series run {productionSyncSeriesRunNumber} sync order {productionSyncOrderLabel}. "
                    : $"Sync order {productionSyncOrderLabel}. ";
                StatusMessage =
                    seriesPrefix +
                    $"Countdown prepared. {prepared.Participants.Count} selected timer(s) will start together. " +
                    "Automatic STATUS polling is paused while RUNNING; use REFRESH STATUS (SELECTED) only when needed.";
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
                    SetStartDiagnostic($"START BLOCKED — CONTROLLER ERROR: {exception.Message}");
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
        CancellationToken cancellationToken,
        bool waitForRtcQualification = false)
    {
        List<StartBlock> missingAddressBlocks = participants
            .Where(device => !device.TryGetIpAddress(out _))
            .Select(device => new StartBlock(
                StartBlockReason.MissingExpectedDevice,
                device.DeviceId,
                "Wait for discovery or deselect this participant."))
            .ToList();
        if (missingAddressBlocks.Count != 0) return missingAddressBlocks;

        if (waitForRtcQualification)
        {
            IReadOnlyList<StartBlock> rtcWaitBlocks = await WaitForRtcStartQualificationAsync(
                participants, cancellationToken);
            if (rtcWaitBlocks.Count != 0) return rtcWaitBlocks;
            requireStatusAfterUtc = DateTimeOffset.UtcNow;
        }

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
            StartBlock? rtcBlock = RtcStartQualification.Evaluate(
                device.DeviceId, session.RtcState, session.RtcFitPoints, session.RtcFitRmsMicroseconds,
                session.RtcQueueDrops, session.RtcTemperatureValid);
            if (rtcBlock is not null) blocks.Add(rtcBlock);
        }
        return blocks;
    }

    private async Task<IReadOnlyList<StartBlock>> WaitForRtcStartQualificationAsync(
        IReadOnlyList<DeviceViewModel> participants,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadlineUtc = DateTimeOffset.UtcNow + RtcQualificationWaitTimeout;

        // One explicit refresh at entry. After this, observe the normal pre-run
        // discovery stream instead of generating one STATUS request per second;
        // network traffic itself can perturb SQW timestamp capture.
        DateTimeOffset initialStatusAfterUtc = DateTimeOffset.UtcNow;
        await RequestFreshStatusesAsync(participants, cancellationToken);
        await WaitForFreshStatusesAsync(participants, initialStatusAfterUtc, cancellationToken);

        while (true)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool anyStale = participants.Any(device =>
            {
                ParticipantSessionSnapshot session = device.SessionSnapshot;
                return !session.LastStatusAtUtc.HasValue ||
                    now - session.LastStatusAtUtc.Value >= StartReadinessGate.MaximumStatusAge;
            });

            if (anyStale)
            {
                DateTimeOffset refreshAfterUtc = DateTimeOffset.UtcNow;
                await RequestFreshStatusesAsync(participants, cancellationToken);
                await WaitForFreshStatusesAsync(participants, refreshAfterUtc, cancellationToken);
                now = DateTimeOffset.UtcNow;
            }

            var rtcBlocks = new List<StartBlock>();
            foreach (DeviceViewModel device in participants)
            {
                ParticipantSessionSnapshot session = device.SessionSnapshot;
                if (!session.LastStatusAtUtc.HasValue ||
                    now - session.LastStatusAtUtc.Value >= StartReadinessGate.MaximumStatusAge)
                {
                    return [new StartBlock(
                        StartBlockReason.StatusStale,
                        device.DeviceId,
                        "RTC qualification wait did not receive a fresh STATUS; check the device/network and press START again.")];
                }
                if (!device.IsOnlineAt(now))
                {
                    return [new StartBlock(
                        StartBlockReason.Offline,
                        device.DeviceId,
                        "Device went offline while waiting for RTC qualification; reconnect it and press START again.")];
                }
                if (session.TimerState is TimerState.Armed or TimerState.Running)
                {
                    return [new StartBlock(
                        StartBlockReason.ParticipantBusy,
                        device.DeviceId,
                        "Device entered ARMED/RUNNING while waiting for RTC qualification; use normal RESET if recovery is required.")];
                }

                StartBlock? rtcBlock = RtcStartQualification.Evaluate(
                    device.DeviceId, session.RtcState, session.RtcFitPoints, session.RtcFitRmsMicroseconds,
                    session.RtcQueueDrops, session.RtcTemperatureValid);
                if (rtcBlock is not null)
                {
                    // Missing v6.21 metrics and queue drops cannot become healthy merely
                    // by waiting. Surface them immediately instead of consuming 90 s.
                    bool metricsUnavailable = session.RtcState == RtcDisciplineState.Locked &&
                        (!session.RtcFitPoints.HasValue || !session.RtcFitRmsMicroseconds.HasValue ||
                         !session.RtcQueueDrops.HasValue || !session.RtcTemperatureValid.HasValue);
                    if (metricsUnavailable || session.RtcQueueDrops is > 0) return [rtcBlock];
                    rtcBlocks.Add(rtcBlock);
                }
            }

            if (rtcBlocks.Count == 0)
            {
                SetStartDiagnostic(
                    $"START: RTC qualification ready on all {participants.Count} selected timer(s); starting clock synchronization...");
                return [];
            }

            if (now >= deadlineUtc)
            {
                return rtcBlocks.Select(block => block with
                {
                    Remedy = $"RTC qualification timed out after {RtcQualificationWaitTimeout.TotalSeconds:F0} s. {block.Remedy}"
                }).ToArray();
            }

            string summary = string.Join("; ", rtcBlocks.Take(4)
                .Select(block => $"{block.DeviceId}: {block.Remedy}"));
            if (rtcBlocks.Count > 4) summary += $"; +{rtcBlocks.Count - 4} more";
            int remainingSeconds = Math.Max(0, (int)Math.Ceiling((deadlineUtc - now).TotalSeconds));
            SetStartDiagnostic(
                $"START waiting for RTC qualification ({remainingSeconds}s timeout): {summary}");
            await Task.Delay(RtcQualificationObservationInterval, cancellationToken);
        }
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
            StartBlock? rtcBlock = RtcStartQualification.Evaluate(
                device.DeviceId, session.RtcState, session.RtcFitPoints, session.RtcFitRmsMicroseconds,
                session.RtcQueueDrops, session.RtcTemperatureValid, "Post-SYNC RTC");
            if (rtcBlock is not null)
            {
                blocks.Add(rtcBlock);
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

    private async Task RefreshSelectedStatusInternalAsync()
    {
        if (SelectedNetworkInterface is null || !udpService.IsReady)
        {
            StatusMessage = "Select an active physical network interface before requesting status.";
            return;
        }

        DeviceViewModel[] selected = Devices.Where(device => device.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "Select at least one timer before requesting status.";
            return;
        }
        if (!await sendLock.WaitAsync(0)) return;

        isSending = true;
        UpdateCanSend();
        try
        {
            var sent = new List<string>();
            var skipped = new List<string>();
            foreach (DeviceViewModel device in selected)
            {
                if (!device.TryGetIpAddress(out IPAddress? address) || address is null)
                {
                    skipped.Add(device.DisplayName);
                    continue;
                }

                ulong commandId = CreateCommandId();
                await udpService.SendTaggedStatusRequestAsync(
                    commandId,
                    address,
                    "MANUAL_STATUS_REQUEST");
                sent.Add(device.DisplayName);
            }

            if (sent.Count == 0)
            {
                StatusMessage =
                    "No selected timer has a known IP address. Wait for discovery, then try REFRESH STATUS again.";
                return;
            }

            string skippedText = skipped.Count == 0
                ? string.Empty
                : $" No known IP for: {string.Join(", ", skipped)}.";
            StatusMessage =
                $"One-shot STATUS requested from {string.Join(", ", sent)}.{skippedText} " +
                "Automatic polling remains paused while a production countdown is RUNNING.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Manual STATUS request failed: {exception.Message}";
        }
        finally
        {
            isSending = false;
            UpdateCanSend();
            sendLock.Release();
        }
    }

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

        ClearDegradedStartPending();
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

    private void SetStartDiagnostic(string message)
    {
        StartSynchronizationResult = message;
        StatusMessage = message;
    }

    private static string FormatStartBlockReason(StartBlockReason reason) => reason switch
    {
        StartBlockReason.Offline => "OFFLINE",
        StartBlockReason.StatusStale => "STATUS STALE",
        StartBlockReason.ParticipantBusy => "PARTICIPANT BUSY",
        StartBlockReason.RtcNotLocked => "RTC NOT LOCKED",
        StartBlockReason.RtcNotQualified => "RTC NOT QUALIFIED",
        StartBlockReason.SyncFailed => "SYNC FAILED",
        StartBlockReason.SyncInvalidated => "SYNC INVALIDATED",
        StartBlockReason.TimingBudgetExceeded => "TIMING BUDGET EXCEEDED",
        StartBlockReason.ArmAckMissing => "ARM ACK MISSING",
        StartBlockReason.AbortUnconfirmed => "ABORT UNCONFIRMED",
        StartBlockReason.MissingExpectedDevice => "MISSING PARTICIPANT",
        _ => reason.ToString().ToUpperInvariant(),
    };

    private void ReportStartBlocks(IReadOnlyList<StartBlock> blocks)
    {
        if (blocks.Count == 0) return;

        string[] diagnosticLines = blocks.Select(block =>
            $"• {FormatStartBlockReason(block.Reason)} — {OperatorDeviceName(block.DeviceId)}: {block.Remedy}").ToArray();
        StartSynchronizationResult =
            $"START BLOCKED — {blocks.Count} requirement{(blocks.Count == 1 ? string.Empty : "s")} not satisfied.\n" +
            string.Join("\n", diagnosticLines);

        StartBlock first = blocks[0];
        string more = blocks.Count > 1 ? $" (+{blocks.Count - 1} more; see START diagnostics)" : string.Empty;
        StatusMessage =
            $"START blocked — {FormatStartBlockReason(first.Reason)}: " +
            $"{OperatorDeviceName(first.DeviceId)}. {first.Remedy}{more}";
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
                    if (!timingQuietPeriodActive && !productionSilentRunActive)
                    {
                        statusDiscovery.StartOrRestart(
                            selectedNetworkInterface,
                            () => Volatile.Read(ref manualBroadcastOverride),
                            GetRunStatusUnicastTargets);
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
            foreach (DeviceViewModel device in Devices)
            {
                device.EndIntentionalStatusPause();
            }
            ControllerNetworkInterface? selection = SelectedNetworkInterface;
            if (!disposed && selection is not null && udpService.IsReady && !productionSilentRunActive)
            {
                statusDiscovery.StartOrRestart(
                    selection,
                    () => Volatile.Read(ref manualBroadcastOverride),
                    GetRunStatusUnicastTargets);
            }
        }
        finally
        {
            timingQuietPeriodActive = false;
        }
    }

    private async Task StartProductionSilentRunAsync(
        PreparedStartRun prepared,
        CancellationToken cancellationToken)
    {
        productionSilentRunCancellation?.Cancel();
        productionSilentRunCancellation?.Dispose();
        productionSilentRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = productionSilentRunCancellation.Token;
        productionSilentRunActive = true;
        productionSilentRunOwnsExactWindow = true;

        // v6.20 production policy: once the final ARM barrier has passed, stop all
        // automatic application STATUS traffic for the RUNNING window. Keep UDP RX
        // alive so RESET and an operator-requested one-shot STATUS remain available.
        foreach (DeviceViewModel device in Devices)
        {
            device.BeginIntentionalStatusPause();
        }
        await statusDiscovery.StopAndWaitAsync();
        await Task.Delay(StatusDiscoveryDrainMilliseconds, token);

        productionSilentRunTask = HoldProductionSilentWindowAsync(prepared, token);
        _ = productionSilentRunTask;
    }

    private async Task HoldProductionSilentWindowAsync(
        PreparedStartRun prepared,
        CancellationToken cancellationToken)
    {
        try
        {
            long resumeUs = checked(
                prepared.TargetMasterMicroseconds +
                (long)prepared.DurationSeconds * 1_000_000L +
                500_000L);
            await WaitUntilMasterTimeAsync(resumeUs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            dispatcherQueue.TryEnqueue(() =>
            {
                StatusMessage = $"Production-silent RUNNING hold failed: {exception.Message}";
            });
        }

        dispatcherQueue.TryEnqueue(ResumeAutomaticStatusDiscoveryAfterRun);
    }

    private void ObserveProductionRunningStatus(StatusPacket status)
    {
        // Keep qualification/manual-test workflows behaviorally unchanged.
        if (IsBenchmarkRunning || isPreparingManualStartAtTest || manualStartAtSession is not null) return;

        if (status.State == TimerState.Running)
        {
            if (productionSilentRunActive)
            {
                if (!productionSilentRunOwnsExactWindow)
                {
                    ScheduleRecoveredRunningStatusPause(status.RemainingSeconds);
                }
                return;
            }

            // Controller restart/reconnect recovery: the first normal discovery
            // request tells us a timer is already RUNNING. Stop future automatic
            // polling immediately; the in-flight discovery datagram may still yield
            // replies from the other timers, which is exactly the one-shot recovery
            // snapshot we want.
            productionSilentRunActive = true;
            productionSilentRunOwnsExactWindow = false;
            foreach (DeviceViewModel device in Devices)
            {
                device.BeginIntentionalStatusPause();
            }
            statusDiscovery.Stop();
            ScheduleRecoveredRunningStatusPause(status.RemainingSeconds);
            StatusMessage =
                "RUNNING timer detected. Automatic STATUS polling is paused for timing performance; use REFRESH STATUS when needed.";
            return;
        }

        if (!productionSilentRunActive || productionSilentRunOwnsExactWindow) return;
        if (Devices.Any(device => device.SessionSnapshot.TimerState == TimerState.Running)) return;

        productionSilentRunCancellation?.Cancel();
        ResumeAutomaticStatusDiscoveryAfterRun();
    }

    private void ScheduleRecoveredRunningStatusPause(uint remainingSeconds)
    {
        productionSilentRunCancellation?.Cancel();
        productionSilentRunCancellation?.Dispose();
        productionSilentRunCancellation = new CancellationTokenSource();
        CancellationToken token = productionSilentRunCancellation.Token;
        productionSilentRunTask = HoldRecoveredRunningStatusPauseAsync(remainingSeconds, token);
        _ = productionSilentRunTask;
    }

    private async Task HoldRecoveredRunningStatusPauseAsync(
        uint remainingSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan delay = TimeSpan.FromMilliseconds(
                checked((long)remainingSeconds * 1000L + RecoveredRunningStatusGraceMilliseconds));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        dispatcherQueue.TryEnqueue(ResumeAutomaticStatusDiscoveryAfterRun);
    }

    private void ResumeAutomaticStatusDiscoveryAfterRun()
    {
        productionSilentRunActive = false;
        productionSilentRunOwnsExactWindow = false;
        productionSilentRunCancellation?.Dispose();
        productionSilentRunCancellation = null;
        foreach (DeviceViewModel device in Devices)
        {
            device.EndIntentionalStatusPause();
        }

        ControllerNetworkInterface? selection = SelectedNetworkInterface;
        if (!disposed && selection is not null && udpService.IsReady && !timingQuietPeriodActive)
        {
            statusDiscovery.StartOrRestart(
                selection,
                () => Volatile.Read(ref manualBroadcastOverride),
                GetRunStatusUnicastTargets);
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
        OnPropertyChanged(nameof(CanStartDegraded));
        RefreshManualStartAtButtonStates();
    }

    private IReadOnlyList<IPAddress>? GetRunStatusUnicastTargets()
    {
        lock (controllerTxTraceGate)
        {
            ControllerTxTraceSession? session = controllerTxTraceSession;
            if (session is null || session.StatusUnicastTargets.Count == 0) return null;
            return session.StatusUnicastTargets.ToArray();
        }
    }

    private void BeginControllerTxTrace(
        ulong commandId,
        long targetMasterMicroseconds,
        uint durationSeconds,
        IReadOnlyList<IPAddress> statusUnicastTargets,
        IReadOnlyList<string> participantDeviceIds,
        string syncOrder,
        string syncQualitySummary)
    {
        var session = new ControllerTxTraceSession(
            commandId,
            targetMasterMicroseconds,
            durationSeconds,
            statusUnicastTargets,
            participantDeviceIds,
            syncOrder,
            syncQualitySummary);

        lock (controllerTxTraceGate)
        {
            controllerTxTraceSession = session;
        }

        ControllerTxTracePath = "TX trace: Capturing...";
        _ = CompleteControllerTxTraceAfterRunAsync(session);
    }

    private void UdpService_PacketSent(object? sender, PacketSentEventArgs e)
    {
        lock (controllerTxTraceGate)
        {
            ControllerTxTraceSession? session = controllerTxTraceSession;
            if (session is null) return;

            long deltaUs = e.MasterSendMicroseconds - session.TargetMasterMicroseconds;
            long phaseUs = PositiveModulo(deltaUs, 1_000_000L);
            long leadToNextBoundaryUs = phaseUs == 0 ? 0 : 1_000_000L - phaseUs;

            session.Rows.Add(new ControllerTxTraceRow(
                e.MasterSendMicroseconds,
                deltaUs,
                phaseUs,
                leadToNextBoundaryUs,
                e.PacketType,
                e.Destination.ToString(),
                e.Attempt,
                e.DatagramBytes,
                e.CorrelationId));
        }
    }

    private void RecordControllerHealthSnapshot(StatusPacket status)
    {
        lock (controllerTxTraceGate)
        {
            ControllerTxTraceSession? session = controllerTxTraceSession;
            if (session is null || status.CommandId != session.CommandId ||
                !session.ParticipantDeviceIds.Contains(status.DeviceId))
            {
                return;
            }

            DeviceHealthSnapshot snapshot = DeviceHealthSnapshot.FromStatus(
                status, MasterClock.NowMicroseconds);

            if (status.State == TimerState.Running)
            {
                // Preserve the first RUNNING status: firmware sends it immediately
                // after START, so rtc_rate_ppm is the start-adjacent qualification
                // value rather than a later manual-refresh sample.
                if (!session.StartHealthByDevice.ContainsKey(status.DeviceId))
                {
                    session.StartHealthByDevice[status.DeviceId] = snapshot;
                }
            }
            else if (status.State == TimerState.Finished)
            {
                // A later post-run STATUS_REQUEST may contain a more complete
                // display summary than the immediate FINISHED state-change reply.
                // Keep the latest FINISHED snapshot.
                session.PostRunHealthByDevice[status.DeviceId] = snapshot;
            }
        }
    }

    private async Task CompleteControllerTxTraceAfterRunAsync(
        ControllerTxTraceSession session)
    {
        try
        {
            // After the countdown is over, explicitly fetch a compact FINISHED
            // health snapshot from each frozen participant. This is outside the
            // timing-critical run and avoids relying on 15 serial monitors or on
            // the periodic discovery loop happening to poll every device before
            // the trace is written.
            long postRunHealthBeginUs = checked(
                session.TargetMasterMicroseconds +
                (long)session.DurationSeconds * 1_000_000L +
                250_000L);
            await WaitUntilMasterTimeAsync(postRunHealthBeginUs, CancellationToken.None)
                .ConfigureAwait(false);
            await CollectPostRunHealthAsync(session, CancellationToken.None)
                .ConfigureAwait(false);

            ControllerTxTraceRow[] rows;
            Dictionary<string, DeviceHealthSnapshot> startHealth;
            Dictionary<string, DeviceHealthSnapshot> postRunHealth;
            lock (controllerTxTraceGate)
            {
                if (!ReferenceEquals(controllerTxTraceSession, session)) return;
                controllerTxTraceSession = null;
                rows = session.Rows.ToArray();
                startHealth = new Dictionary<string, DeviceHealthSnapshot>(
                    session.StartHealthByDevice, StringComparer.Ordinal);
                postRunHealth = new Dictionary<string, DeviceHealthSnapshot>(
                    session.PostRunHealthByDevice, StringComparer.Ordinal);
            }

            string fileStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            string txPath = await WriteControllerTxTraceCsvAsync(session, rows, fileStamp)
                .ConfigureAwait(false);
            string healthPath = await WriteControllerHealthCsvAsync(
                    session, startHealth, postRunHealth, fileStamp)
                .ConfigureAwait(false);
            dispatcherQueue.TryEnqueue(() =>
            {
                ControllerTxTracePath = $"TX trace: {txPath} | Health: {healthPath}";
                StatusMessage = $"Controller timing + fleet-health traces saved: {txPath}; {healthPath}";
            });
        }
        catch (Exception exception)
        {
            dispatcherQueue.TryEnqueue(() =>
            {
                ControllerTxTracePath = $"TX trace save failed: {exception.Message}";
            });
        }
    }

    private async Task CollectPostRunHealthAsync(
        ControllerTxTraceSession session,
        CancellationToken cancellationToken)
    {
        const int maxRounds = 2;
        for (int round = 1; round <= maxRounds; round++)
        {
            KeyValuePair<string, IPAddress>[] missing;
            lock (controllerTxTraceGate)
            {
                if (!ReferenceEquals(controllerTxTraceSession, session)) return;
                missing = session.ParticipantAddresses
                    .Where(pair => !session.PostRunHealthByDevice.ContainsKey(pair.Key))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToArray();
            }

            if (missing.Length == 0) return;

            foreach (KeyValuePair<string, IPAddress> target in missing)
            {
                await udpService.SendTaggedStatusRequestAsync(
                    CreateCommandId(),
                    target.Value,
                    "HEALTH_STATUS_REQUEST",
                    cancellationToken).ConfigureAwait(false);
            }

            // Replies are normally immediate. Give the full retry set enough
            // time to arrive before deciding which devices still need a second
            // post-run request.
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> WriteControllerTxTraceCsvAsync(
        ControllerTxTraceSession session,
        IReadOnlyList<ControllerTxTraceRow> rows,
        string fileStamp)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        string directory = Path.Combine(documents, "FactoryTimer", "TimingQualification");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"controller_tx_{fileStamp}_{session.CommandId:X16}.csv");

        var builder = new StringBuilder();
        builder.AppendLine(
            "RunCommandId,TStarMasterUs,DurationSeconds,PacketMasterUs,DeltaFromTStarUs,PhaseUs,LeadToNextBoundaryUs,PacketType,Destination,Attempt,Bytes,CorrelationId,SyncOrder,SyncQualitySummary");

        foreach (ControllerTxTraceRow row in rows)
        {
            builder.Append(session.CommandId.ToString("X16")).Append(',')
                .Append(session.TargetMasterMicroseconds).Append(',')
                .Append(session.DurationSeconds).Append(',')
                .Append(row.MasterSendMicroseconds).Append(',')
                .Append(row.DeltaFromTStarMicroseconds).Append(',')
                .Append(row.PhaseMicroseconds).Append(',')
                .Append(row.LeadToNextBoundaryMicroseconds).Append(',')
                .Append(row.PacketType).Append(',')
                .Append(row.Destination).Append(',')
                .Append(row.Attempt).Append(',')
                .Append(row.DatagramBytes).Append(',')
                .Append(row.CorrelationId.ToString("X16")).Append(',')
                .Append(session.SyncOrder).Append(',')
                .Append(session.SyncQualitySummary)
                .AppendLine();
        }

        await File.WriteAllTextAsync(
            path,
            builder.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(false);
        return path;
    }

    private static async Task<string> WriteControllerHealthCsvAsync(
        ControllerTxTraceSession session,
        IReadOnlyDictionary<string, DeviceHealthSnapshot> startHealth,
        IReadOnlyDictionary<string, DeviceHealthSnapshot> postRunHealth,
        string fileStamp)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        string directory = Path.Combine(documents, "FactoryTimer", "TimingQualification");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"controller_health_{fileStamp}_{session.CommandId:X16}.csv");

        var builder = new StringBuilder();
        builder.AppendLine(DeviceHealthSnapshot.CsvHeader);
        foreach (string deviceId in session.ParticipantDeviceIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            startHealth.TryGetValue(deviceId, out DeviceHealthSnapshot? start);
            postRunHealth.TryGetValue(deviceId, out DeviceHealthSnapshot? post);
            builder.AppendLine(DeviceHealthSnapshot.ToCsv(
                session.CommandId, session.TargetMasterMicroseconds, session.DurationSeconds,
                deviceId, "START", start));
            builder.AppendLine(DeviceHealthSnapshot.ToCsv(
                session.CommandId, session.TargetMasterMicroseconds, session.DurationSeconds,
                deviceId, "POST_RUN", post));
        }

        await File.WriteAllTextAsync(
            path,
            builder.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(false);
        return path;
    }

    private static long PositiveModulo(long value, long modulus)
    {
        long remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
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
                    RecordControllerHealthSnapshot(status);
                    device.Apply(status, e.RemoteEndPoint);
                    ObserveProductionRunningStatus(status);
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
            device.UpdateRunningRemainingEstimate(now);
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
        productionSilentRunCancellation?.Cancel();
        productionSilentRunCancellation?.Dispose();
        productionSilentRunCancellation = null;
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
        udpService.PacketSent -= UdpService_PacketSent;
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

    private sealed class ControllerTxTraceSession(
        ulong commandId,
        long targetMasterMicroseconds,
        uint durationSeconds,
        IReadOnlyList<IPAddress> statusUnicastTargets,
        IReadOnlyList<string> participantDeviceIds,
        string syncOrder,
        string syncQualitySummary)
    {
        public ulong CommandId { get; } = commandId;
        public long TargetMasterMicroseconds { get; } = targetMasterMicroseconds;
        public uint DurationSeconds { get; } = durationSeconds;
        public IReadOnlyDictionary<string, IPAddress> ParticipantAddresses { get; } =
            BuildParticipantAddressMap(participantDeviceIds, statusUnicastTargets);
        public IReadOnlyList<IPAddress> StatusUnicastTargets =>
            ParticipantAddresses.Values.Distinct().ToArray();
        public HashSet<string> ParticipantDeviceIds { get; } =
            new(participantDeviceIds, StringComparer.Ordinal);
        public string SyncOrder { get; } = syncOrder;
        public string SyncQualitySummary { get; } = syncQualitySummary;
        public List<ControllerTxTraceRow> Rows { get; } = [];
        public Dictionary<string, DeviceHealthSnapshot> StartHealthByDevice { get; } =
            new(StringComparer.Ordinal);
        public Dictionary<string, DeviceHealthSnapshot> PostRunHealthByDevice { get; } =
            new(StringComparer.Ordinal);

        private static IReadOnlyDictionary<string, IPAddress> BuildParticipantAddressMap(
            IReadOnlyList<string> deviceIds,
            IReadOnlyList<IPAddress> addresses)
        {
            if (deviceIds.Count != addresses.Count)
            {
                throw new ArgumentException("Participant device/address counts must match.");
            }

            var result = new Dictionary<string, IPAddress>(StringComparer.Ordinal);
            for (int i = 0; i < deviceIds.Count; i++)
            {
                result[deviceIds[i]] = addresses[i];
            }
            return result;
        }
    }

    private sealed record DeviceHealthSnapshot(
        long CapturedMasterMicroseconds,
        TimerState State,
        RtcDisciplineState RtcState,
        ushort? RtcFitPoints,
        double? RtcFitRmsMicroseconds,
        uint? RtcQueueDrops,
        bool? RtcTemperatureValid,
        double? RtcRatePpmVsRtc,
        ulong? RtcFitOutliers,
        ulong? RtcAcceptedEdges,
        ulong? RtcInferredMissingEdges,
        ulong? RtcHoldoverEntries,
        double? RtcTemperatureC,
        int? RtcSqwCore,
        byte? HealthFlags,
        long? SyncSourceOffsetMicroseconds,
        long? SyncEpochLocalMicroseconds,
        long? SyncEpochDisciplinedMicroseconds,
        long? SyncMasterMinusDisciplinedMicroseconds,
        long? SyncEpochMasterMicroseconds,
        long? StartErrorMicroseconds,
        long? SchedulerLatenessMicroseconds,
        long? StartPublishLatenessMicroseconds,
        long? WorstPublishLatenessMicroseconds,
        uint? FrameNotReadyCount)
    {
        public const string CsvHeader =
            "RunCommandId,TStarMasterUs,DurationSeconds,DeviceId,Phase,StatusCaptured,CapturedMasterUs,TimerState,RtcState," +
            "RtcRatePpmVsRtc,RtcFitPoints,RtcFitRmsUs,RtcFitOutliers,RtcAcceptedEdges,RtcInferredMissingEdges,RtcHoldoverEntries,RtcQueueDrops,RtcTemperatureValid,RtcTemperatureC,RtcSqwCore,HealthFlags," +
            "SyncEpochMasterMinusLocalUs,SyncEpochLocalUs,SyncEpochDisciplinedUs,SyncEpochMasterUs,SyncEpochMasterMinusDisciplinedUs," +
            "StartErrorUs,SchedulerLatenessUs,StartPublishLatenessUs,WorstPublishLatenessUs,FrameNotReady";

        public static DeviceHealthSnapshot FromStatus(StatusPacket status, long capturedMasterMicroseconds)
        {
            long? epochMasterUs = null;
            if (status.SyncSourceOffsetMicroseconds.HasValue && status.SyncEpochLocalMicroseconds.HasValue)
            {
                epochMasterUs = checked(
                    status.SyncSourceOffsetMicroseconds.Value +
                    status.SyncEpochLocalMicroseconds.Value);
            }

            return new DeviceHealthSnapshot(
                capturedMasterMicroseconds,
                status.State,
                status.RtcState,
                status.RtcFitPoints,
                status.RtcFitRmsMicroseconds,
                status.RtcQueueDrops,
                status.RtcTemperatureValid,
                status.RtcRatePpmVsRtc,
                status.RtcFitOutliers,
                status.RtcAcceptedEdges,
                status.RtcInferredMissingEdges,
                status.RtcHoldoverEntries,
                status.RtcTemperatureC,
                status.RtcSqwCore,
                status.HealthFlags,
                status.SyncSourceOffsetMicroseconds,
                status.SyncEpochLocalMicroseconds,
                status.SyncEpochDisciplinedMicroseconds,
                status.SyncMasterMinusDisciplinedMicroseconds,
                epochMasterUs,
                status.StartErrorMicroseconds,
                status.SchedulerLatenessMicroseconds,
                status.StartPublishLatenessMicroseconds,
                status.WorstPublishLatenessMicroseconds,
                status.FrameNotReadyCount);
        }

        public static string ToCsv(
            ulong commandId,
            long targetMasterMicroseconds,
            uint durationSeconds,
            string deviceId,
            string phase,
            DeviceHealthSnapshot? health)
        {
            static string Long(long? value) => value?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            static string ULong(ulong? value) => value?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            static string UInt(uint? value) => value?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            static string UShort(ushort? value) => value?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            static string Int(int? value) => value?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            static string Byte(byte? value) => value?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            static string Double(double? value, string format) => value?.ToString(
                format, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            static string Bool(bool? value) => value.HasValue ? (value.Value ? "1" : "0") : string.Empty;

            return string.Join(
                ",",
                commandId.ToString("X16"),
                targetMasterMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                deviceId,
                phase,
                health is null ? "0" : "1",
                health is null ? string.Empty : health.CapturedMasterMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                health?.State.ToString().ToUpperInvariant() ?? string.Empty,
                health?.RtcState.ToString().ToUpperInvariant() ?? string.Empty,
                Double(health?.RtcRatePpmVsRtc, "F6"),
                UShort(health?.RtcFitPoints),
                Double(health?.RtcFitRmsMicroseconds, "F3"),
                ULong(health?.RtcFitOutliers),
                ULong(health?.RtcAcceptedEdges),
                ULong(health?.RtcInferredMissingEdges),
                ULong(health?.RtcHoldoverEntries),
                UInt(health?.RtcQueueDrops),
                Bool(health?.RtcTemperatureValid),
                Double(health?.RtcTemperatureC, "F2"),
                Int(health?.RtcSqwCore),
                Byte(health?.HealthFlags),
                Long(health?.SyncSourceOffsetMicroseconds),
                Long(health?.SyncEpochLocalMicroseconds),
                Long(health?.SyncEpochDisciplinedMicroseconds),
                Long(health?.SyncEpochMasterMicroseconds),
                Long(health?.SyncMasterMinusDisciplinedMicroseconds),
                Long(health?.StartErrorMicroseconds),
                Long(health?.SchedulerLatenessMicroseconds),
                Long(health?.StartPublishLatenessMicroseconds),
                Long(health?.WorstPublishLatenessMicroseconds),
                UInt(health?.FrameNotReadyCount));
        }
    }

    private sealed record ControllerTxTraceRow(
        long MasterSendMicroseconds,
        long DeltaFromTStarMicroseconds,
        long PhaseMicroseconds,
        long LeadToNextBoundaryMicroseconds,
        string PacketType,
        string Destination,
        int Attempt,
        int DatagramBytes,
        ulong CorrelationId);

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

    private enum ForwardSyncPhase
    {
        Unspecified = 0,
        Calibration = 1,
        Verification = 2,
    }

    private sealed record SyncMeasurement(
        ulong SyncId,
        ClockSyncSample Sample,
        long ActualForwardDelayUs,
        uint ActualReverseDelayUs,
        long IngressLocalUs,
        int ForwardAttempt = 0,
        ForwardSyncPhase ForwardPhase = ForwardSyncPhase.Unspecified,
        int ForwardRound = -1);

    private sealed record ForwardSyncConsensus(
        ulong RepresentativeSyncId,
        long MasterMinusLocalOffsetMicroseconds,
        long OffsetEpochLocalMicroseconds,
        long EffectiveMasterEpochMicroseconds,
        long BestRttMicroseconds,
        long TopSampleSpreadMicroseconds,
        string Top3Provenance);

    private sealed record ForwardSyncAttemptCandidate(
        ForwardSyncConsensus CalibrationConsensus,
        ForwardSyncConsensus VerificationConsensus,
        ForwardSyncConsensus FinalConsensus,
        long ResidualMicroseconds,
        long UncertaintyMicroseconds,
        int Attempt,
        long InitialResidualMicroseconds);
}
