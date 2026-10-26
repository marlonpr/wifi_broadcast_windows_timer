// WARNING: deliberately unsafe engineering-only fault injection.
// Absent from every ordinary build unless FACTORY_ARM_FAULT_TEST is defined.
#if FACTORY_ARM_FAULT_TEST
using System.Net;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core;

internal static class ArmFaultTestHook
{
    private const string ConfirmValue = "I_ACCEPT_PARTIAL_FLEET_EXECUTION";
    private static readonly object Gate = new();
    private static readonly string? Mode = Environment.GetEnvironmentVariable("FT_ARM_TEST_MODE");
    private static readonly string? IpString = Environment.GetEnvironmentVariable("FT_ARM_TEST_IP");
    private static readonly string? SegmentString = Environment.GetEnvironmentVariable("FT_ARM_TEST_SEGMENT");
    private static readonly bool Confirmed = Environment.GetEnvironmentVariable("FT_ARM_TEST_ENABLE") == ConfirmValue;
    private static readonly HashSet<ulong> TargetCommandIds = [];
    private static bool activated;

    private static bool Configured => Confirmed &&
        (Mode is "drop_start" or "drop_start_reset" or "ignore_ack_reset") &&
        IPAddress.TryParse(IpString, out _) &&
        uint.TryParse(SegmentString, out uint segment) && segment > 0;

    private static bool MatchIp(IPAddress address) =>
        IPAddress.TryParse(IpString, out IPAddress? configured) && address.Equals(configured);

    internal static void ObserveStartAt(CommandPacket command, IPAddress destination)
    {
        if (!Configured || !MatchIp(destination) ||
            command.CommandType != CommandType.StartAt || command.PlanId == 0 ||
            !uint.TryParse(SegmentString, out uint segment) || command.SegmentIndex != segment)
            return;
        lock (Gate)
        {
            activated = true;
            TargetCommandIds.Add(command.CommandId);
        }
        System.Diagnostics.Debug.WriteLine($"ARM_FAULT_TEST armed mode={Mode} ip={IpString} segment={segment} cmd={command.CommandId:X16}");
    }

    internal static bool DropOutbound(string packetType, ulong commandId, IPAddress destination)
    {
        if (!Configured || !MatchIp(destination)) return false;
        lock (Gate)
        {
            if (!activated) return false;
            if (packetType == "STARTAT") // protocol enum ToString(): StartAt -> STARTAT
                return TargetCommandIds.Contains(commandId) && (Mode is "drop_start" or "drop_start_reset");
            if (packetType == "RESET")
                return Mode is "drop_start_reset" or "ignore_ack_reset";
            return false;
        }
    }

    internal static bool DropInboundAck(AckPacket ack, IPAddress source)
    {
        if (!Configured || Mode != "ignore_ack_reset" || !MatchIp(source) ||
            ack.CommandType != CommandType.StartAt) return false;
        lock (Gate) return activated && TargetCommandIds.Contains(ack.CommandId);
    }
}
#endif
