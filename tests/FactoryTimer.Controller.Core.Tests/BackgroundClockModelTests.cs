using FactoryTimer.Controller.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class BackgroundClockModelTests
{
    [TestMethod]
    public void RateFitRecoversKnownPpmWithoutLargeEpochPrecisionLoss()
    {
        const long baseEpochUs = 9_000_000_000;
        const double ratePpm = 33.9;
        var observations = new List<BackgroundClockObservation>();
        for (int index = 0; index < 16; index++)
        {
            long epochUs = baseEpochUs + index * 60_000_000L;
            double elapsedSeconds = index * 60d;
            long offsetUs = checked((long)Math.Round(
                125_000d + ratePpm * elapsedSeconds,
                MidpointRounding.AwayFromZero));
            observations.Add(new BackgroundClockObservation(
                DateTimeOffset.UnixEpoch.AddSeconds(index * 60),
                epochUs,
                offsetUs,
                7_000));
        }

        BackgroundClockFit fit = BackgroundClockModel.Fit(
            observations,
            observations[^1].EffectiveMasterEpochMicroseconds,
            BackgroundClockModelPolicy.ShadowDefault);

        Assert.AreEqual(ratePpm, fit.RatePpm, 0.01);
        Assert.IsTrue(fit.RateStatisticallyQualified);
        Assert.AreEqual(BackgroundClockModelState.RateCandidate, fit.State);
        Assert.IsFalse(fit.PredictiveBenefitConfirmed);
    }

    [TestMethod]
    public void QualificationStillRequiresOfflinePredictiveBenefitConfirmation()
    {
        const long baseEpochUs = 2_000_000_000;
        var observations = Enumerable.Range(0, 20)
            .Select(index => new BackgroundClockObservation(
                DateTimeOffset.UnixEpoch.AddSeconds(index * 60),
                baseEpochUs + index * 60_000_000L,
                100_000 + index * 2_000L,
                6_000))
            .ToArray();

        BackgroundClockFit shadow = BackgroundClockModel.Fit(
            observations,
            observations[^1].EffectiveMasterEpochMicroseconds,
            BackgroundClockModelPolicy.ShadowDefault,
            predictiveBenefitConfirmed: false);
        BackgroundClockFit confirmed = BackgroundClockModel.Fit(
            observations,
            observations[^1].EffectiveMasterEpochMicroseconds,
            BackgroundClockModelPolicy.ShadowDefault,
            predictiveBenefitConfirmed: true);

        Assert.IsTrue(shadow.RateStatisticallyQualified);
        Assert.AreEqual(BackgroundClockModelState.RateCandidate, shadow.State);
        Assert.AreEqual(BackgroundClockModelState.RateQualified, confirmed.State);
    }


    [TestMethod]
    public void CandidateRateIsNotAppliedUntilRateQualified()
    {
        const long baseEpochUs = 3_000_000_000;
        var observations = Enumerable.Range(0, 20)
            .Select(index => new BackgroundClockObservation(
                DateTimeOffset.UnixEpoch.AddSeconds(index * 60),
                baseEpochUs + index * 60_000_000L,
                50_000 + index * 1_800L,
                6_000))
            .ToArray();

        BackgroundClockFit candidate = BackgroundClockModel.Fit(
            observations,
            observations[^1].EffectiveMasterEpochMicroseconds,
            BackgroundClockModelPolicy.ShadowDefault,
            predictiveBenefitConfirmed: false);
        BackgroundClockFit qualified = BackgroundClockModel.Fit(
            observations,
            observations[^1].EffectiveMasterEpochMicroseconds,
            BackgroundClockModelPolicy.ShadowDefault,
            predictiveBenefitConfirmed: true);

        long futureEpochUs = observations[^1].EffectiveMasterEpochMicroseconds + 300_000_000L;

        Assert.AreEqual(BackgroundClockModelState.RateCandidate, candidate.State);
        Assert.AreEqual(
            candidate.OffsetAtReferenceMicroseconds,
            candidate.PredictOffsetMicroseconds(futureEpochUs),
            0.001);
        Assert.AreEqual(BackgroundClockModelState.RateQualified, qualified.State);
        Assert.AreEqual(
            qualified.OffsetAtReferenceMicroseconds + qualified.RatePpm * 300d,
            qualified.PredictOffsetMicroseconds(futureEpochUs),
            0.001);
    }

    [TestMethod]
    public void HalfPpmDeviceStaysUnqualifiedAtMeasuredNoiseInThirtyMinuteWindow()
    {
        const long baseEpochUs = 7_000_000_000;
        const double ratePpm = 0.5;
        const int count = 31;

        // Use an even quadratic residual shape around the midpoint. It is
        // orthogonal to centered time, so it raises residual variance without
        // biasing the fitted slope. Scale it to approximately 830 us residual SD.
        double[] shape = Enumerable.Range(0, count)
            .Select(index =>
            {
                double x = index - (count - 1) / 2d;
                return x * x;
            })
            .ToArray();
        double shapeMean = shape.Average();
        double shapeSampleSd = Math.Sqrt(
            shape.Sum(value => Math.Pow(value - shapeMean, 2)) / (count - 1));
        double scale = 830d / shapeSampleSd;

        var observations = new List<BackgroundClockObservation>(count);
        for (int index = 0; index < count; index++)
        {
            long epochUs = baseEpochUs + index * 60_000_000L;
            double elapsedSeconds = index * 60d;
            double residualUs = (shape[index] - shapeMean) * scale;
            long offsetUs = checked((long)Math.Round(
                100_000d + ratePpm * elapsedSeconds + residualUs,
                MidpointRounding.AwayFromZero));
            observations.Add(new BackgroundClockObservation(
                DateTimeOffset.UnixEpoch.AddSeconds(index * 60),
                epochUs,
                offsetUs,
                7_000));
        }

        BackgroundClockFit fit = BackgroundClockModel.Fit(
            observations,
            observations[^1].EffectiveMasterEpochMicroseconds,
            BackgroundClockModelPolicy.ShadowDefault,
            predictiveBenefitConfirmed: true);

        Assert.AreEqual(ratePpm, fit.RatePpm, 0.01);
        Assert.IsTrue(fit.RateStandardErrorPpm > 0.20 && fit.RateStandardErrorPpm < 0.40);
        Assert.IsTrue(fit.RateSnr < 3.0);
        Assert.IsFalse(fit.RateStatisticallyQualified);
        Assert.AreEqual(BackgroundClockModelState.RateCandidate, fit.State);
    }

    [TestMethod]
    public void PredictionMeanStandardErrorGrowsWithExtrapolationHorizon()
    {
        const long baseEpochUs = 5_000_000_000;
        var observations = new List<BackgroundClockObservation>();
        for (int index = 0; index < 20; index++)
        {
            long epochUs = baseEpochUs + index * 60_000_000L;
            long deterministic = 200_000 + index * 600L;
            long alternatingNoise = (index & 1) == 0 ? 300 : -300;
            observations.Add(new BackgroundClockObservation(
                DateTimeOffset.UnixEpoch.AddSeconds(index * 60),
                epochUs,
                deterministic + alternatingNoise,
                7_000));
        }

        BackgroundClockFit fit = BackgroundClockModel.Fit(
            observations,
            observations[^1].EffectiveMasterEpochMicroseconds,
            BackgroundClockModelPolicy.ShadowDefault);

        double atReference = fit.PredictMeanStandardErrorMicroseconds(
            fit.ReferenceEpochMicroseconds);
        double afterFiveMinutes = fit.PredictMeanStandardErrorMicroseconds(
            fit.ReferenceEpochMicroseconds + 300_000_000L);

        Assert.IsTrue(double.IsFinite(atReference));
        Assert.IsTrue(afterFiveMinutes > atReference);
    }

    [TestMethod]
    public void FitWindowExcludesOldObservationsCausally()
    {
        const long nowUs = 10_000_000_000;
        var observations = new[]
        {
            new BackgroundClockObservation(DateTimeOffset.UtcNow, nowUs - 2_000_000_000, -9_999_999, 7_000),
            new BackgroundClockObservation(DateTimeOffset.UtcNow, nowUs - 120_000_000, 10_000, 7_000),
            new BackgroundClockObservation(DateTimeOffset.UtcNow, nowUs - 60_000_000, 10_060, 7_000),
            new BackgroundClockObservation(DateTimeOffset.UtcNow, nowUs, 10_120, 7_000),
        };
        var policy = new BackgroundClockModelPolicy(
            TimeSpan.FromMinutes(5),
            TimeSpan.Zero,
            0);

        BackgroundClockFit fit = BackgroundClockModel.Fit(observations, nowUs, policy);

        Assert.AreEqual(3, fit.ObservationCount);
        Assert.AreEqual(1.0, fit.RatePpm, 0.001);
    }
}
