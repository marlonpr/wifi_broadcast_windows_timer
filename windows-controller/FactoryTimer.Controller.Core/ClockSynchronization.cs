using System.Net.Sockets;

namespace FactoryTimer.Controller.Core;


public enum SyncSamplingMode
{
    Baseline8Plus8 = 0,
    Candidate4Plus4 = 1,
}

public readonly record struct SyncSamplingProfile(
    int CalibrationSampleCount,
    int VerificationSampleCount,
    int LowRttSampleCount)
{
    public int ExchangesPerDevice => CalibrationSampleCount + VerificationSampleCount;
}

public static class SyncSamplingExperiment
{
    public static SyncSamplingProfile GetProfile(SyncSamplingMode mode) => mode switch
    {
        SyncSamplingMode.Baseline8Plus8 => new(8, 8, 3),
        SyncSamplingMode.Candidate4Plus4 => new(4, 4, 3),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

public enum SyncPathDelayMode
{
    None = 0,
    Symmetric250Milliseconds = 1,
    AsymmetricForward250Milliseconds = 2,
    AsymmetricReverse1Millisecond = 3,
    AsymmetricReverse4Milliseconds = 4,
    AsymmetricForward40Milliseconds = 5,
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
        SyncPathDelayMode.AsymmetricReverse1Millisecond => new(0, 1),
        SyncPathDelayMode.AsymmetricReverse4Milliseconds => new(0, 4),
        SyncPathDelayMode.AsymmetricForward40Milliseconds => new(40, 0),
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

public readonly record struct ClockSyncConsensus(
    ClockSyncSample RepresentativeSample,
    long MasterMinusLocalOffsetMicroseconds,
    long BestRttMicroseconds,
    int LowRttSampleCount);

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

    // Historical v3 estimator retained for regression tests: first retain the N valid
    // samples with the lowest network RTT, then use the median offset inside that
    // low-RTT set. v5 production synchronization uses inverse-square weighting.
    // With an odd sample count the median is an actual measured sample.
    public static ClockSyncConsensus SelectLowRttMedianOffset(
        IEnumerable<ClockSyncSample> samples,
        int lowRttSampleCount)
    {
        if (lowRttSampleCount <= 0 || (lowRttSampleCount & 1) == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lowRttSampleCount),
                "The low-RTT consensus sample count must be a positive odd number.");
        }

        ClockSyncSample[] lowRttSamples = samples
            .Where(sample => sample.IsValid)
            .OrderBy(sample => sample.NetworkRttMicroseconds)
            .ThenBy(sample => sample.MasterMinusLocalOffsetMicroseconds)
            .Take(lowRttSampleCount)
            .ToArray();

        if (lowRttSamples.Length < lowRttSampleCount)
        {
            throw new InvalidOperationException(
                $"At least {lowRttSampleCount} valid clock synchronization samples are required for low-RTT consensus.");
        }

        ClockSyncSample representative = lowRttSamples
            .OrderBy(sample => sample.MasterMinusLocalOffsetMicroseconds)
            .ThenBy(sample => sample.NetworkRttMicroseconds)
            .ElementAt(lowRttSampleCount / 2);

        return new ClockSyncConsensus(
            representative,
            representative.MasterMinusLocalOffsetMicroseconds,
            lowRttSamples.Min(sample => sample.NetworkRttMicroseconds),
            lowRttSampleCount);
    }

    // Production estimator used by v5: retain the N valid samples with the
    // lowest network RTT, then compute an inverse-RTT-squared weighted offset.
    // Scaling each weight by the minimum RTT keeps the arithmetic numerically
    // well-conditioned while preserving the same relative 1/RTT^2 weighting.
    // The lowest-RTT sample is retained as the representative sample only so
    // SYNC_SET can carry a real synchronization ID; its measured offset is not
    // substituted for the weighted estimate.
    public static ClockSyncConsensus SelectLowRttInverseSquareWeightedOffset(
        IEnumerable<ClockSyncSample> samples,
        int lowRttSampleCount)
    {
        if (lowRttSampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lowRttSampleCount),
                "The low-RTT weighted sample count must be positive.");
        }

        ClockSyncSample[] lowRttSamples = samples
            .Where(sample => sample.IsValid)
            .OrderBy(sample => sample.NetworkRttMicroseconds)
            .ThenBy(sample => sample.MasterMinusLocalOffsetMicroseconds)
            .Take(lowRttSampleCount)
            .ToArray();

        if (lowRttSamples.Length < lowRttSampleCount)
        {
            throw new InvalidOperationException(
                $"At least {lowRttSampleCount} valid clock synchronization samples are required for low-RTT weighted consensus.");
        }

        long bestRtt = lowRttSamples[0].NetworkRttMicroseconds;
        ClockSyncSample representative = lowRttSamples[0];

        double weightedOffsetSum = 0.0;
        double weightSum = 0.0;

        if (bestRtt == 0)
        {
            // A zero-RTT sample cannot be weighted with 1/RTT^2. If such samples
            // ever occur, use only the zero-RTT subset because they dominate the
            // inverse-square limit as RTT approaches zero.
            ClockSyncSample[] zeroRttSamples = lowRttSamples
                .Where(sample => sample.NetworkRttMicroseconds == 0)
                .ToArray();
            long zeroRttOffset = checked((long)Math.Round(
                zeroRttSamples.Average(sample => (double)sample.MasterMinusLocalOffsetMicroseconds),
                MidpointRounding.AwayFromZero));

            return new ClockSyncConsensus(
                representative,
                zeroRttOffset,
                bestRtt,
                lowRttSampleCount);
        }

        foreach (ClockSyncSample sample in lowRttSamples)
        {
            double ratio = (double)bestRtt / sample.NetworkRttMicroseconds;
            double weight = ratio * ratio;
            weightedOffsetSum += weight * sample.MasterMinusLocalOffsetMicroseconds;
            weightSum += weight;
        }

        long weightedOffset = checked((long)Math.Round(
            weightedOffsetSum / weightSum,
            MidpointRounding.AwayFromZero));

        return new ClockSyncConsensus(
            representative,
            weightedOffset,
            bestRtt,
            lowRttSampleCount);
    }

    // Positive means the device's reconstructed Master clock is ahead of Master.
    public static long CalculateResidualErrorMicroseconds(
        long appliedMasterMinusLocalOffsetMicroseconds,
        ClockSyncSample verificationSample) =>
        appliedMasterMinusLocalOffsetMicroseconds -
        verificationSample.MasterMinusLocalOffsetMicroseconds;
}

public readonly record struct SyncQualityEvaluation(
    long ResidualErrorMicroseconds,
    long ExpectedBiasMicroseconds,
    long DeviationMicroseconds,
    long ThresholdMicroseconds)
{
    public bool IsAccepted => Math.Abs(DeviationMicroseconds) <= ThresholdMicroseconds;
}

public static class SyncQualityPolicy
{
    public static SyncQualityEvaluation Evaluate(
        long residualErrorMicroseconds,
        long expectedBiasMicroseconds,
        long thresholdMicroseconds)
    {
        if (thresholdMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdMicroseconds));
        }

        return new SyncQualityEvaluation(
            residualErrorMicroseconds,
            expectedBiasMicroseconds,
            residualErrorMicroseconds - expectedBiasMicroseconds,
            thresholdMicroseconds);
    }
}


public static class SyncRetryPolicy
{
    public static bool IsRetryableTransportFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            TimeoutException => true,
            SocketException socketException => IsRetryableSocketError(socketException.SocketErrorCode),
            IOException { InnerException: SocketException socketException } =>
                IsRetryableSocketError(socketException.SocketErrorCode),
            _ => false,
        };
    }

    private static bool IsRetryableSocketError(SocketError error) => error switch
    {
        SocketError.TimedOut => true,
        SocketError.WouldBlock => true,
        SocketError.TryAgain => true,
        SocketError.NoBufferSpaceAvailable => true,
        SocketError.ConnectionReset => true,
        SocketError.HostUnreachable => true,
        _ => false,
    };
}
