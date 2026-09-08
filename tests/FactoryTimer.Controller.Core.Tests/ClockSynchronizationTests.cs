using FactoryTimer.Controller.Core;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class ClockSynchronizationTests
{
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
    public void ExperimentProfilesMatchRequestedThreeModes()
    {
        SyncPathDelayProfile none = SyncPathDelayExperiment.GetProfile(SyncPathDelayMode.None);
        SyncPathDelayProfile symmetric = SyncPathDelayExperiment.GetProfile(
            SyncPathDelayMode.Symmetric250Milliseconds);
        SyncPathDelayProfile asymmetric = SyncPathDelayExperiment.GetProfile(
            SyncPathDelayMode.AsymmetricForward250Milliseconds);

        Assert.AreEqual(new SyncPathDelayProfile(0, 0), none);
        Assert.AreEqual(new SyncPathDelayProfile(250, 250), symmetric);
        Assert.AreEqual(new SyncPathDelayProfile(250, 0), asymmetric);
        Assert.AreEqual(0L, symmetric.ExpectedOffsetBiasMicroseconds);
        Assert.AreEqual(-125_000L, asymmetric.ExpectedOffsetBiasMicroseconds);
    }
}
