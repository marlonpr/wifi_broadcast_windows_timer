namespace FactoryTimer.Controller.Core;

public enum SyncPathDelayMode
{
    None = 0,
    Symmetric250Milliseconds = 1,
    AsymmetricForward250Milliseconds = 2,
}

public readonly record struct SyncPathDelayProfile(
    int MasterToDeviceDelayMilliseconds,
    int DeviceToMasterDelayMilliseconds)
{
    public int AddedRoundTripDelayMilliseconds =>
        MasterToDeviceDelayMilliseconds + DeviceToMasterDelayMilliseconds;

    // NTP-style offset bias = (reverse_delay - forward_delay) / 2.
    public long ExpectedOffsetBiasMicroseconds =>
        (DeviceToMasterDelayMilliseconds - MasterToDeviceDelayMilliseconds) * 1000L / 2L;
}

public static class SyncPathDelayExperiment
{
    public const int ExperimentDelayMilliseconds = 250;

    public static SyncPathDelayProfile GetProfile(SyncPathDelayMode mode) => mode switch
    {
        SyncPathDelayMode.None => new(0, 0),
        SyncPathDelayMode.Symmetric250Milliseconds =>
            new(ExperimentDelayMilliseconds, ExperimentDelayMilliseconds),
        SyncPathDelayMode.AsymmetricForward250Milliseconds =>
            new(ExperimentDelayMilliseconds, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

public sealed record ClockSyncSample(
    long MasterT1Microseconds,
    long DeviceT2Microseconds,
    long DeviceT3Microseconds,
    long MasterT4Microseconds)
{
    public long NetworkRttMicroseconds =>
        (MasterT4Microseconds - MasterT1Microseconds) -
        (DeviceT3Microseconds - DeviceT2Microseconds);

    // Sign convention used by both controller and firmware:
    // MasterTime = DeviceLocalTime + MasterMinusLocalOffsetMicroseconds.
    public long MasterMinusLocalOffsetMicroseconds =>
        ((MasterT1Microseconds - DeviceT2Microseconds) +
         (MasterT4Microseconds - DeviceT3Microseconds)) / 2;

    public bool IsValid =>
        MasterT4Microseconds >= MasterT1Microseconds &&
        DeviceT3Microseconds >= DeviceT2Microseconds &&
        NetworkRttMicroseconds >= 0;
}

public static class ClockSyncEstimator
{
    public static ClockSyncSample SelectBest(IEnumerable<ClockSyncSample> samples)
    {
        ClockSyncSample? best = samples
            .Where(sample => sample.IsValid)
            .OrderBy(sample => sample.NetworkRttMicroseconds)
            .FirstOrDefault();
        return best ?? throw new InvalidOperationException("No valid clock synchronization sample was received.");
    }

    // Positive means the device's reconstructed Master clock is ahead of Master.
    public static long CalculateResidualErrorMicroseconds(
        long appliedMasterMinusLocalOffsetMicroseconds,
        ClockSyncSample verificationSample) =>
        appliedMasterMinusLocalOffsetMicroseconds -
        verificationSample.MasterMinusLocalOffsetMicroseconds;
}
