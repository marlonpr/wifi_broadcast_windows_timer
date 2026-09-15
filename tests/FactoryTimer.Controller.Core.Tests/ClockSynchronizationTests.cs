using FactoryTimer.Controller.Core;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class ClockSynchronizationTests
{
    [TestMethod]
    public void SamplingExperimentProfilesMatchBaselineAndFourPlusFourCandidate()
    {
        SyncSamplingProfile baseline = SyncSamplingExperiment.GetProfile(
            SyncSamplingMode.Baseline8Plus8);
        SyncSamplingProfile candidate = SyncSamplingExperiment.GetProfile(
            SyncSamplingMode.Candidate4Plus4);

        Assert.AreEqual(new SyncSamplingProfile(8, 8, 3), baseline);
        Assert.AreEqual(new SyncSamplingProfile(4, 4, 3), candidate);
        Assert.AreEqual(16, baseline.ExchangesPerDevice);
        Assert.AreEqual(8, candidate.ExchangesPerDevice);
    }

    [TestMethod]
    public void NtpStyleSampleCalculatesRttAndMasterMinusLocalOffset()
    {
        var sample = new ClockSyncSample(
            MasterT1Microseconds: 100_000,
            DeviceT2Microseconds: 40_004,
            DeviceT3Microseconds: 40_006,
            MasterT4Microseconds: 100_012);

        Assert.AreEqual(10L, sample.NetworkRttMicroseconds);
        Assert.AreEqual(60_001L, sample.MasterMinusLocalOffsetMicroseconds);
        Assert.IsTrue(sample.IsValid);
    }

    [TestMethod]
    public void LowestRttSampleIsPreferred()
    {
        ClockSyncSample best = ClockSyncEstimator.SelectBest(
        [
            new ClockSyncSample(100_000, 40_010, 40_012, 100_030),
            new ClockSyncSample(200_000, 140_004, 140_006, 200_012),
            new ClockSyncSample(300_000, 240_020, 240_021, 300_050),
        ]);

        Assert.AreEqual(10L, best.NetworkRttMicroseconds);
        Assert.AreEqual(60_001L, best.MasterMinusLocalOffsetMicroseconds);
    }

    [TestMethod]
    public void ResidualPositiveMeansDeviceClockIsAheadOfMaster()
    {
        var verification = new ClockSyncSample(500_000, 440_010, 440_012, 500_022);
        long residual = ClockSyncEstimator.CalculateResidualErrorMicroseconds(
            appliedMasterMinusLocalOffsetMicroseconds: 60_005,
            verification);

        Assert.AreEqual(5L, residual);
    }

    [TestMethod]
    public void Symmetric250MillisecondDelayRaisesRttWithoutOffsetBias()
    {
        // True Master-minus-local offset is +60,000 us.
        // Forward=250 ms and reverse=250 ms.
        var sample = new ClockSyncSample(
            MasterT1Microseconds: 1_000_000,
            DeviceT2Microseconds: 1_190_000,
            DeviceT3Microseconds: 1_190_100,
            MasterT4Microseconds: 1_500_100);

        Assert.AreEqual(500_000L, sample.NetworkRttMicroseconds);
        Assert.AreEqual(60_000L, sample.MasterMinusLocalOffsetMicroseconds);
    }

    [TestMethod]
    public void ForwardOnly250MillisecondDelayBiasesOffsetByMinus125Milliseconds()
    {
        // Same true +60,000 us offset, but forward=250 ms and reverse=0.
        var delayed = new ClockSyncSample(
            MasterT1Microseconds: 1_000_000,
            DeviceT2Microseconds: 1_190_000,
            DeviceT3Microseconds: 1_190_100,
            MasterT4Microseconds: 1_250_100);

        Assert.AreEqual(250_000L, delayed.NetworkRttMicroseconds);
        Assert.AreEqual(-65_000L, delayed.MasterMinusLocalOffsetMicroseconds);
        Assert.AreEqual(-125_000L, delayed.MasterMinusLocalOffsetMicroseconds - 60_000L);
    }

    [TestMethod]
    public void ExperimentProfilesMatchRequestedModes()
    {
        SyncPathDelayProfile none = SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None);
        SyncPathDelayProfile symmetric = SyncPathDelayExperiment.GetProfile(
            SyncPathDelayMode.Symmetric250Milliseconds);
        SyncPathDelayProfile asymmetric = SyncPathDelayExperiment.GetProfile(
            SyncPathDelayMode.AsymmetricForward250Milliseconds);
        SyncPathDelayProfile reverse1 = SyncPathDelayExperiment.GetProfile(
            SyncPathDelayMode.AsymmetricReverse1Millisecond);
        SyncPathDelayProfile reverse4 = SyncPathDelayExperiment.GetProfile(
            SyncPathDelayMode.AsymmetricReverse4Milliseconds);
        SyncPathDelayProfile forward40 = SyncPathDelayExperiment.GetProfile(
            SyncPathDelayMode.AsymmetricForward40Milliseconds);

        Assert.AreEqual(new SyncPathDelayProfile(0, 0), none);
        Assert.AreEqual(new SyncPathDelayProfile(250, 250), symmetric);
        Assert.AreEqual(new SyncPathDelayProfile(250, 0), asymmetric);
        Assert.AreEqual(new SyncPathDelayProfile(0, 1), reverse1);
        Assert.AreEqual(new SyncPathDelayProfile(0, 4), reverse4);
        Assert.AreEqual(new SyncPathDelayProfile(40, 0), forward40);
        Assert.AreEqual(0L, symmetric.ExpectedOffsetBiasMicroseconds);
        Assert.AreEqual(-125_000L, asymmetric.ExpectedOffsetBiasMicroseconds);
        Assert.AreEqual(500L, reverse1.ExpectedOffsetBiasMicroseconds);
        Assert.AreEqual(2_000L, reverse4.ExpectedOffsetBiasMicroseconds);
        Assert.AreEqual(-20_000L, forward40.ExpectedOffsetBiasMicroseconds);
    }
    [TestMethod]
    public void SyncQualityPolicyUsesExpectedExperimentBias()
    {
        SyncQualityEvaluation none = SyncQualityPolicy.Evaluate(
            residualErrorMicroseconds: 2_400,
            expectedBiasMicroseconds: 0,
            thresholdMicroseconds: 3_000);
        Assert.IsTrue(none.IsAccepted);
        Assert.AreEqual(2_400L, none.DeviationMicroseconds);

        SyncQualityEvaluation retry = SyncQualityPolicy.Evaluate(
            residualErrorMicroseconds: -4_200,
            expectedBiasMicroseconds: 0,
            thresholdMicroseconds: 3_000);
        Assert.IsFalse(retry.IsAccepted);

        SyncQualityEvaluation asymmetric = SyncQualityPolicy.Evaluate(
            residualErrorMicroseconds: -124_100,
            expectedBiasMicroseconds: -125_000,
            thresholdMicroseconds: 3_000);
        Assert.IsTrue(asymmetric.IsAccepted);
        Assert.AreEqual(900L, asymmetric.DeviationMicroseconds);
    }

    [TestMethod]
    public void LowRttConsensusUsesMedianOffsetOfBestThreeRttSamples()
    {
        // Lowest RTT sample is deliberately offset-biased by +9 ms. A
        // lowest-RTT-only estimator would choose it; the 3-sample median does not.
        ClockSyncSample outlier = SampleWithRttAndOffset(8_000, 69_000);
        ClockSyncSample goodA = SampleWithRttAndOffset(9_000, 60_100);
        ClockSyncSample goodB = SampleWithRttAndOffset(10_000, 59_900);
        ClockSyncSample slower = SampleWithRttAndOffset(30_000, 72_000);

        ClockSyncConsensus consensus = ClockSyncEstimator.SelectLowRttMedianOffset(
            [outlier, goodA, goodB, slower],
            lowRttSampleCount: 3);

        Assert.AreEqual(60_100L, consensus.MasterMinusLocalOffsetMicroseconds);
        Assert.AreEqual(8_000L, consensus.BestRttMicroseconds);
        Assert.AreEqual(goodA, consensus.RepresentativeSample);
        Assert.AreEqual(3, consensus.LowRttSampleCount);
    }

    [TestMethod]
    public void LowRttConsensusPreservesPersistentAsymmetricDelayBias()
    {
        ClockSyncConsensus consensus = ClockSyncEstimator.SelectLowRttMedianOffset(
        [
            SampleWithRttAndOffset(250_000, -65_300),
            SampleWithRttAndOffset(251_000, -65_000),
            SampleWithRttAndOffset(252_000, -64_700),
            SampleWithRttAndOffset(400_000, 60_000),
        ],
        lowRttSampleCount: 3);

        // True offset in the experiment is +60 ms, so approximately -65 ms is
        // the intentional -125 ms forward-path bias. Consensus must not hide it.
        Assert.AreEqual(-65_000L, consensus.MasterMinusLocalOffsetMicroseconds);
        Assert.AreEqual(-125_000L, consensus.MasterMinusLocalOffsetMicroseconds - 60_000L);
    }

    [TestMethod]
    public void LowRttConsensusRequiresRequestedNumberOfValidSamples()
    {
        InvalidOperationException? exception = null;

        try
        {
            ClockSyncEstimator.SelectLowRttMedianOffset(
            [
                SampleWithRttAndOffset(8_000, 60_000),
                SampleWithRttAndOffset(9_000, 60_100),
            ],
            lowRttSampleCount: 3);
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        StringAssert.Contains(exception!.Message, "At least 3 valid");
    }

    [TestMethod]
    public void LowRttConsensusRejectsEvenCandidateCount()
    {
        bool threwExpectedException = false;

        try
        {
            ClockSyncEstimator.SelectLowRttMedianOffset(
                [SampleWithRttAndOffset(8_000, 60_000)],
                lowRttSampleCount: 2);
        }
        catch (ArgumentOutOfRangeException)
        {
            threwExpectedException = true;
        }

        Assert.IsTrue(threwExpectedException);
    }

    [TestMethod]
    public void InverseSquareWeightedConsensusUsesBestThreeRttSamples()
    {
        ClockSyncConsensus consensus = ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
        [
            SampleWithRttAndOffset(10_000, 100),
            SampleWithRttAndOffset(20_000, 200),
            SampleWithRttAndOffset(40_000, 400),
            SampleWithRttAndOffset(80_000, 50_000),
        ],
        lowRttSampleCount: 3);

        // Relative weights are 1, 1/4 and 1/16, so the weighted offset is
        // (100 + 50 + 25) / 1.3125 = 133.333... us -> 133 us.
        Assert.AreEqual(133L, consensus.MasterMinusLocalOffsetMicroseconds);
        Assert.AreEqual(10_000L, consensus.BestRttMicroseconds);
        Assert.AreEqual(10_000L, consensus.RepresentativeSample.NetworkRttMicroseconds);
        Assert.AreEqual(3, consensus.LowRttSampleCount);
    }

    [TestMethod]
    public void InverseSquareWeightedConsensusUsesWeightedMasterMidpointAsEffectiveEpoch()
    {
        ClockSyncSample a = new(1_000_000, 940_000, 940_000, 1_010_000);
        ClockSyncSample b = new(2_000_000, 1_940_000, 1_940_000, 2_010_000);
        ClockSyncSample c = new(3_000_000, 2_940_000, 2_940_000, 3_010_000);

        ClockSyncConsensus consensus = ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
            [a, b, c],
            lowRttSampleCount: 3);

        Assert.AreEqual(2_005_000L, consensus.EffectiveMasterEpochMicroseconds);
    }

    [TestMethod]
    public void InverseSquareWeightedConsensusPreservesPersistentAsymmetricDelayBias()
    {
        ClockSyncConsensus consensus = ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
        [
            SampleWithRttAndOffset(250_000, -65_300),
            SampleWithRttAndOffset(251_000, -65_000),
            SampleWithRttAndOffset(252_000, -64_700),
            SampleWithRttAndOffset(400_000, 60_000),
        ],
        lowRttSampleCount: 3);

        Assert.AreEqual(-65_002L, consensus.MasterMinusLocalOffsetMicroseconds);
        Assert.AreEqual(-125_002L, consensus.MasterMinusLocalOffsetMicroseconds - 60_000L);
    }

    [TestMethod]
    public void InverseSquareWeightedConsensusRequiresRequestedNumberOfValidSamples()
    {
        InvalidOperationException? exception = null;

        try
        {
            ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
            [
                SampleWithRttAndOffset(8_000, 60_000),
                SampleWithRttAndOffset(9_000, 60_100),
            ],
            lowRttSampleCount: 3);
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        StringAssert.Contains(exception!.Message, "At least 3 valid");
    }

    [TestMethod]
    public void InverseSquareWeightedConsensusRejectsNonPositiveCandidateCount()
    {
        bool threwExpectedException = false;

        try
        {
            ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
                [SampleWithRttAndOffset(8_000, 60_000)],
                lowRttSampleCount: 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            threwExpectedException = true;
        }

        Assert.IsTrue(threwExpectedException);
    }

    [TestMethod]
    public void InverseSquareWeightedConsensusKeepsObservedBorderlineCaseInsideThreeMilliseconds()
    {
        ClockSyncConsensus calibration = ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
        [
            SampleWithRttAndOffset(26_226, -598_669_250),
            SampleWithRttAndOffset(26_626, -598_665_146),
            SampleWithRttAndOffset(29_830, -598_661_175),
        ],
        lowRttSampleCount: 3);

        ClockSyncConsensus verification = ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
        [
            SampleWithRttAndOffset(19_604, -598_670_082),
            SampleWithRttAndOffset(20_832, -598_666_270),
            SampleWithRttAndOffset(26_426, -598_669_052),
        ],
        lowRttSampleCount: 3);

        long residual = calibration.MasterMinusLocalOffsetMicroseconds -
            verification.MasterMinusLocalOffsetMicroseconds;
        SyncQualityEvaluation quality = SyncQualityPolicy.Evaluate(residual, 0, 3_000);

        Assert.AreEqual(2_940L, residual);
        Assert.IsTrue(quality.IsAccepted);
    }

    [TestMethod]
    public void InverseSquareWeightedConsensusHandlesZeroRttWithoutDivisionByZero()
    {
        ClockSyncConsensus consensus = ClockSyncEstimator.SelectLowRttInverseSquareWeightedOffset(
        [
            SampleWithRttAndOffset(0, 60_000),
            SampleWithRttAndOffset(10_000, 80_000),
            SampleWithRttAndOffset(20_000, 40_000),
        ],
        lowRttSampleCount: 3);

        Assert.AreEqual(60_000L, consensus.MasterMinusLocalOffsetMicroseconds);
        Assert.AreEqual(0L, consensus.BestRttMicroseconds);
    }


    [TestMethod]
    public void SyncRetryPolicyRetriesTimeoutsAndTransientSocketErrors()
    {
        Assert.IsTrue(SyncRetryPolicy.IsRetryableTransportFailure(
            new TimeoutException("SYNC timed out.")));
        Assert.IsTrue(SyncRetryPolicy.IsRetryableTransportFailure(
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.TimedOut)));
        Assert.IsTrue(SyncRetryPolicy.IsRetryableTransportFailure(
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)));
    }

    [TestMethod]
    public void SyncRetryPolicyDoesNotRetryAdapterLossCancellationOrProtocolErrors()
    {
        Assert.IsFalse(SyncRetryPolicy.IsRetryableTransportFailure(
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkDown)));
        Assert.IsFalse(SyncRetryPolicy.IsRetryableTransportFailure(
            new OperationCanceledException("cancelled")));
        Assert.IsFalse(SyncRetryPolicy.IsRetryableTransportFailure(
            new InvalidOperationException("Unexpected SYNC_REPLY while synchronizing ESP03.")));
    }

    private static ClockSyncSample SampleWithRttAndOffset(long rttMicroseconds, long offsetMicroseconds)
    {
        // Device processing time is zero. Pick t1=1,000,000 us and solve a
        // symmetric synthetic sample for the requested RTT and NTP offset.
        const long t1 = 1_000_000;
        long oneWay = rttMicroseconds / 2;
        long t2 = t1 - offsetMicroseconds + oneWay;
        long t3 = t2;
        long t4 = t1 + rttMicroseconds;
        return new ClockSyncSample(t1, t2, t3, t4);
    }

}
