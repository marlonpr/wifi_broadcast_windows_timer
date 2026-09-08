using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FactoryTimer.Controller.Core;

public enum ControllerInterfaceType
{
    Wifi,
    Ethernet,
}

public sealed record Ipv4AddressSnapshot(IPAddress Address, int PrefixLength, bool IsDnsEligible = true);

public sealed record NetworkAdapterSnapshot(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType InterfaceType,
    OperationalStatus OperationalStatus,
    int PhysicalAddressLength,
    bool HasIpv4Gateway,
    IReadOnlyList<Ipv4AddressSnapshot> Ipv4Addresses);

public sealed record ControllerNetworkInterface(
    string Id,
    ControllerInterfaceType InterfaceType,
    string FriendlyName,
    IPAddress LocalAddress,
    int PrefixLength)
{
    public IPAddress SubnetMask => Ipv4Network.CalculateSubnetMask(PrefixLength);
    public IPAddress BroadcastAddress => Ipv4Network.CalculateBroadcast(LocalAddress, PrefixLength);
    public string TypeName => InterfaceType == ControllerInterfaceType.Wifi ? "Wi-Fi" : "Ethernet";
    public string DisplayName => $"{TypeName} \u2014 {FriendlyName} \u2014 {LocalAddress} /{PrefixLength}";
}

public static class Ipv4Network
{
    public static IPAddress CalculateBroadcast(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("An IPv4 address is required.", nameof(address));
        }

        byte[] addressBytes = address.GetAddressBytes();
        byte[] maskBytes = CalculateSubnetMask(prefixLength).GetAddressBytes();
        byte[] broadcast = new byte[4];
        for (int index = 0; index < broadcast.Length; index++)
        {
            broadcast[index] = (byte)(addressBytes[index] | ~maskBytes[index]);
        }

        return new IPAddress(broadcast);
    }

    public static IPAddress CalculateSubnetMask(int prefixLength)
    {
        if (prefixLength is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }

        byte[] mask = new byte[4];
        int bitsRemaining = prefixLength;
        for (int index = 0; index < mask.Length; index++)
        {
            int networkBits = Math.Clamp(bitsRemaining, 0, 8);
            mask[index] = networkBits == 0 ? (byte)0 : (byte)(0xff << (8 - networkBits));
            bitsRemaining -= networkBits;
        }

        return new IPAddress(mask);
    }
}

public static class PhysicalNetworkInterfaceDiscovery
{
    private static readonly string[] VirtualAdapterMarkers =
    [
        "virtual", "vethernet", "hyper-v", "hyperv", "vmware", "vpn", "wireguard",
        "openvpn", "tailscale", "zerotier", "tunnel", "loopback", "pseudo", "npcap",
        "wsl", "container", "docker", "bluetooth", "hamachi", "fortinet", "anyconnect",
        "juniper", "nordlynx", "protonvpn", " tap", "tap ", "tap-", " tun", "tun ", "tun-",
    ];

    public static IReadOnlyList<ControllerNetworkInterface> FindUsable(
        IEnumerable<NetworkAdapterSnapshot> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        return adapters
            .Where(IsUsablePhysicalAdapter)
            .Select(adapter => new
            {
                Adapter = adapter,
                Address = adapter.Ipv4Addresses
                    .Where(IsUsableAddress)
                    .OrderByDescending(address => address.IsDnsEligible)
                    .FirstOrDefault(),
            })
            .Where(candidate => candidate.Address is not null)
            .OrderByDescending(candidate => candidate.Adapter.HasIpv4Gateway)
            .ThenBy(candidate => candidate.Adapter.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new ControllerNetworkInterface(
                candidate.Adapter.Id,
                GetControllerType(candidate.Adapter.InterfaceType)!.Value,
                GetFriendlyName(candidate.Adapter),
                candidate.Address!.Address,
                candidate.Address.PrefixLength))
            .ToArray();
    }

    private static bool IsUsablePhysicalAdapter(NetworkAdapterSnapshot adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up ||
            adapter.PhysicalAddressLength == 0 ||
            GetControllerType(adapter.InterfaceType) is null)
        {
            return false;
        }

        string identity = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        return !VirtualAdapterMarkers.Any(identity.Contains);
    }

    private static bool IsUsableAddress(Ipv4AddressSnapshot address)
    {
        if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
            address.PrefixLength is < 0 or > 32 ||
            IPAddress.IsLoopback(address.Address) ||
            address.Address.Equals(IPAddress.Any) ||
            address.Address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        byte[] bytes = address.Address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254) && bytes[0] is not (0 or >= 224);
    }

    private static ControllerInterfaceType? GetControllerType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => ControllerInterfaceType.Wifi,
        NetworkInterfaceType.Ethernet or
        NetworkInterfaceType.Ethernet3Megabit or
        NetworkInterfaceType.FastEthernetFx or
        NetworkInterfaceType.FastEthernetT or
        NetworkInterfaceType.GigabitEthernet => ControllerInterfaceType.Ethernet,
        _ => null,
    };

    private static string GetFriendlyName(NetworkAdapterSnapshot adapter) =>
        string.IsNullOrWhiteSpace(adapter.Description) ? adapter.Name : adapter.Description;
}

public interface INetworkInterfaceProvider : IDisposable
{
    event EventHandler? NetworkAddressChanged;

    IReadOnlyList<ControllerNetworkInterface> GetUsableInterfaces();
}

public sealed class SystemNetworkInterfaceProvider : INetworkInterfaceProvider
{
    private bool disposed;

    public SystemNetworkInterfaceProvider()
    {
        NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
    }

    public event EventHandler? NetworkAddressChanged;

    public IReadOnlyList<ControllerNetworkInterface> GetUsableInterfaces()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var snapshots = new List<NetworkAdapterSnapshot>();
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                Ipv4AddressSnapshot[] addresses = properties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => new Ipv4AddressSnapshot(
                        address.Address,
                        address.PrefixLength))
                    .ToArray();
                bool hasGateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !gateway.Address.Equals(IPAddress.Any));
                snapshots.Add(new NetworkAdapterSnapshot(
                    adapter.Id,
                    adapter.Name,
                    adapter.Description,
                    adapter.NetworkInterfaceType,
                    adapter.OperationalStatus,
                    adapter.GetPhysicalAddress().GetAddressBytes().Length,
                    hasGateway,
                    addresses));
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear while Windows is enumerating it. The next address-change
                // notification will refresh the list again.
            }
        }

        return PhysicalNetworkInterfaceDiscovery.FindUsable(snapshots);
    }

    private void NetworkChange_NetworkAddressChanged(object? sender, EventArgs e) =>
        NetworkAddressChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged;
        GC.SuppressFinalize(this);
    }
}
