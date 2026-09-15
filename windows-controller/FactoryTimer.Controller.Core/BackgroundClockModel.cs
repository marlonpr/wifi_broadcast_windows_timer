namespace FactoryTimer.Controller.Core;

public enum BackgroundClockModelState
{
    NoData = 0,
    OffsetOnly = 1,
    RateCandidate = 2,
    RateQualified = 3,
}

public sealed record BackgroundClockObservation(
    DateTimeOffset CapturedUtc,
    long EffectiveMasterEpochMicroseconds,
    long MasterMinusLocalOffsetMicroseconds,
    long BestRttMicroseconds,
    double? DieTemperatureCelsius = null);

public readonly record struct BackgroundClockModelPolicy(
    TimeSpan FitWindow,
    TimeSpan MinimumRateSpan,
    double MinimumRateSnr)
{
    public static BackgroundClockModelPolicy ShadowDefault => new(
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(10),
        3.0);
}

public sealed record BackgroundClockFit(
    BackgroundClockModelState State,
    int ObservationCount,
    double ObservationSpanSeconds,
    long ReferenceEpochMicroseconds,
    double OffsetAtReferenceMicroseconds,
    double RatePpm,
    double RateStandardErrorPpm,
    double RateSnr,
    double ResidualStandardDeviationMicroseconds,
    double FitMeanStandardErrorAtReferenceMicroseconds,
    long FitMeanEpochMicroseconds,
    double FitSxxSecondsSquared,
    double ResidualVarianceMicrosecondsSquared,
    bool RateStatisticallyQualified,
    bool PredictiveBenefitConfirmed)
{
    public double PredictOffsetMicroseconds(long masterEpochMicroseconds)
    {
        // Never extrapolate an unqualified slope. RateCandidate deliberately keeps
        // the regression-smoothed endpoint estimate, but freezes it at the
        // reference epoch until both statistical and predictive-benefit gates pass.
        double appliedRatePpm = State == BackgroundClockModelState.RateQualified
            ? RatePpm
            : 0d;
        return OffsetAtReferenceMicroseconds +
            appliedRatePpm * ((masterEpochMicroseconds - ReferenceEpochMicroseconds) / 1_000_000d);
    }

    public double PredictMeanStandardErrorMicroseconds(long masterEpochMicroseconds)
    {
        if (ObservationCount <= 0 ||
            !double.IsFinite(ResidualVarianceMicrosecondsSquared) ||
            ResidualVarianceMicrosecondsSquared < 0 ||
            !(FitSxxSecondsSquared > 0))
        {
            return double.PositiveInfinity;
        }

        double xSeconds =
            (masterEpochMicroseconds - FitMeanEpochMicroseconds) / 1_000_000d;
        double variance = ResidualVarianceMicrosecondsSquared *
            (1d / ObservationCount +
             (xSeconds * xSeconds) / FitSxxSecondsSquared);
        return variance >= 0 && double.IsFinite(variance)
            ? Math.Sqrt(variance)
            : double.PositiveInfinity;
    }

    public double EstimateAgeSeconds(long masterEpochMicroseconds) =>
        (masterEpochMicroseconds - ReferenceEpochMicroseconds) / 1_000_000d;
}

public static class BackgroundClockModel
{
    public static BackgroundClockFit Fit(
        IEnumerable<BackgroundClockObservation> observations,
        long predictionEpochMicroseconds,
        BackgroundClockModelPolicy policy,
        bool predictiveBenefitConfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (policy.FitWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Fit window must be positive.");
        }
        if (policy.MinimumRateSpan < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Minimum rate span cannot be negative.");
        }
        if (!double.IsFinite(policy.MinimumRateSnr) || policy.MinimumRateSnr < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Minimum rate SNR must be finite and nonnegative.");
        }

        long windowUs = checked((long)Math.Round(
            policy.FitWindow.TotalSeconds * 1_000_000d,
            MidpointRounding.AwayFromZero));
        long oldestEpoch = predictionEpochMicroseconds - windowUs;

        BackgroundClockObservation[] selected = observations
            .Where(observation =>
                observation.EffectiveMasterEpochMicroseconds <= predictionEpochMicroseconds &&
                observation.EffectiveMasterEpochMicroseconds >= oldestEpoch)
            .OrderBy(observation => observation.EffectiveMasterEpochMicroseconds)
            .ToArray();

        if (selected.Length == 0)
        {
            return new BackgroundClockFit(
                BackgroundClockModelState.NoData,
                0,
                0,
                predictionEpochMicroseconds,
                double.NaN,
                double.NaN,
                double.PositiveInfinity,
                0,
                double.NaN,
                double.PositiveInfinity,
                predictionEpochMicroseconds,
                double.NaN,
                double.NaN,
                false,
                predictiveBenefitConfirmed);
        }

        BackgroundClockObservation latest = selected[^1];
        double spanSeconds = selected.Length > 1
            ? (selected[^1].EffectiveMasterEpochMicroseconds - selected[0].EffectiveMasterEpochMicroseconds) / 1_000_000d
            : 0d;

        if (selected.Length < 3 || spanSeconds <= 0)
        {
            return new BackgroundClockFit(
                BackgroundClockModelState.OffsetOnly,
                selected.Length,
                spanSeconds,
                latest.EffectiveMasterEpochMicroseconds,
                latest.MasterMinusLocalOffsetMicroseconds,
                0,
                double.PositiveInfinity,
                0,
                double.NaN,
                double.PositiveInfinity,
                latest.EffectiveMasterEpochMicroseconds,
                double.NaN,
                double.NaN,
                false,
                predictiveBenefitConfirmed);
        }

        // Center x before fitting so the large absolute master timestamp never
        // enters the least-squares products. x is seconds and y is microseconds,
        // therefore the slope's numerical unit is microseconds/second == ppm.
        double meanEpochUs = selected.Average(
            observation => (double)observation.EffectiveMasterEpochMicroseconds);
        double meanOffsetUs = selected.Average(
            observation => (double)observation.MasterMinusLocalOffsetMicroseconds);

        double sxx = 0d;
        double sxy = 0d;
        foreach (BackgroundClockObservation observation in selected)
        {
            double xSeconds =
                (observation.EffectiveMasterEpochMicroseconds - meanEpochUs) / 1_000_000d;
            double yCentered = observation.MasterMinusLocalOffsetMicroseconds - meanOffsetUs;
            sxx += xSeconds * xSeconds;
            sxy += xSeconds * yCentered;
        }

        if (!(sxx > 0d) || !double.IsFinite(sxx))
        {
            return new BackgroundClockFit(
                BackgroundClockModelState.OffsetOnly,
                selected.Length,
                spanSeconds,
                latest.EffectiveMasterEpochMicroseconds,
                latest.MasterMinusLocalOffsetMicroseconds,
                0,
                double.PositiveInfinity,
                0,
                double.NaN,
                double.PositiveInfinity,
                latest.EffectiveMasterEpochMicroseconds,
                double.NaN,
                double.NaN,
                false,
                predictiveBenefitConfirmed);
        }

        double ratePpm = sxy / sxx;
        double sse = 0d;
        foreach (BackgroundClockObservation observation in selected)
        {
            double xSeconds =
                (observation.EffectiveMasterEpochMicroseconds - meanEpochUs) / 1_000_000d;
            double fitted = meanOffsetUs + ratePpm * xSeconds;
            double residual = observation.MasterMinusLocalOffsetMicroseconds - fitted;
            sse += residual * residual;
        }

        int residualDegreesOfFreedom = selected.Length - 2;
        double residualVariance = residualDegreesOfFreedom > 0
            ? sse / residualDegreesOfFreedom
            : double.NaN;
        double residualSd = residualVariance >= 0 && double.IsFinite(residualVariance)
            ? Math.Sqrt(residualVariance)
            : double.NaN;
        double rateSePpm = residualVariance >= 0 && double.IsFinite(residualVariance)
            ? Math.Sqrt(residualVariance / sxx)
            : double.PositiveInfinity;
        double rateSnr = rateSePpm > 0 && double.IsFinite(rateSePpm)
            ? Math.Abs(ratePpm) / rateSePpm
            : (ratePpm == 0 ? 0 : double.PositiveInfinity);

        long referenceEpochUs = latest.EffectiveMasterEpochMicroseconds;
        double referenceXSeconds = (referenceEpochUs - meanEpochUs) / 1_000_000d;
        double offsetAtReferenceUs = meanOffsetUs + ratePpm * referenceXSeconds;
        double meanFitSeAtReferenceUs = residualVariance >= 0 && double.IsFinite(residualVariance)
            ? Math.Sqrt(residualVariance *
                (1d / selected.Length + (referenceXSeconds * referenceXSeconds) / sxx))
            : double.PositiveInfinity;

        bool statisticallyQualified =
            spanSeconds >= policy.MinimumRateSpan.TotalSeconds &&
            rateSnr >= policy.MinimumRateSnr;
        BackgroundClockModelState state = statisticallyQualified && predictiveBenefitConfirmed
            ? BackgroundClockModelState.RateQualified
            : BackgroundClockModelState.RateCandidate;

        return new BackgroundClockFit(
            state,
            selected.Length,
            spanSeconds,
            referenceEpochUs,
            offsetAtReferenceUs,
            ratePpm,
            rateSePpm,
            rateSnr,
            residualSd,
            meanFitSeAtReferenceUs,
            checked((long)Math.Round(meanEpochUs, MidpointRounding.AwayFromZero)),
            sxx,
            residualVariance,
            statisticallyQualified,
            predictiveBenefitConfirmed);
    }
}
