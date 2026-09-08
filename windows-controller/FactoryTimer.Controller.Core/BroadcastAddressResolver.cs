using System.Net;
using System.Net.Sockets;

namespace FactoryTimer.Controller.Core;

public sealed record BroadcastAddressResolution(IPAddress? Address, string? Error)
{
    public bool IsValid => Address is not null;
}

public static class BroadcastAddressResolver
{
    public static BroadcastAddressResolution Resolve(
        ControllerNetworkInterface? selectedInterface,
        string? manualOverride)
    {
        if (selectedInterface is null)
        {
            return new(null, "Select an active physical network interface before sending a command.");
        }

        if (string.IsNullOrWhiteSpace(manualOverride))
        {
            return new(selectedInterface.BroadcastAddress, null);
        }

        if (!IPAddress.TryParse(manualOverride.Trim(), out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return new(null, "The manual broadcast override must be a valid IPv4 address.");
        }

        return new(address, null);
    }
}
