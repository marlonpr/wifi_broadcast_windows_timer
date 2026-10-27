namespace FactoryTimer.Controller.Core;

public readonly record struct InitialRosterParticipant(string DeviceId, bool HasAddress, bool Online);

public static class InitialRosterAdmission
{
    public static IReadOnlyList<StartBlock> Evaluate(IEnumerable<InitialRosterParticipant> participants)
    {
        InitialRosterParticipant[] roster = participants.ToArray();
        if (roster.Length == 0)
            return [new(StartBlockReason.MissingExpectedDevice, "(none)", "Select at least one timer before START.")];
        List<StartBlock> blocks = [];
        foreach (InitialRosterParticipant participant in roster)
            if (!participant.HasAddress)
                blocks.Add(new(StartBlockReason.MissingExpectedDevice, participant.DeviceId,
                    "Wait for discovery or deselect this timer."));
            else if (!participant.Online)
                blocks.Add(new(StartBlockReason.Offline, participant.DeviceId,
                    "Reconnect this timer or deselect it."));
        return blocks;
    }
}
