namespace FactoryTimer.Controller.Core;

public static class StopResetAddressGate
{
    public static bool HasFreshAddressEvidence(DateTimeOffset? latestStatusAtUtc, DateTimeOffset challengeStartedAtUtc) =>
        latestStatusAtUtc is DateTimeOffset observed && observed >= challengeStartedAtUtc;
}
