using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class NetworkInterfaceTests
{
    [TestMethod]
    public void DiscoversPhysicalWifiInterface()
    {
        NetworkAdapterSnapshot wifi = Adapter(
            "wifi-id",
            "Wi-Fi",
            "Intel Wi-Fi",
            NetworkInterfaceType.Wireless80211,
            "192.168.5.168",
            16);

        ControllerNetworkInterface selection = AssertSingle(
            PhysicalNetworkInterfaceDiscovery.FindUsable([wifi]));

        Assert.AreEqual(ControllerInterfaceType.Wifi, selection.InterfaceType);
        Assert.AreEqual("Intel Wi-Fi", selection.FriendlyName);
        Assert.AreEqual("Wi-Fi \u2014 Intel Wi-Fi \u2014 192.168.5.168 /16", selection.DisplayName);
    }

    [TestMethod]
    public void DiscoversPhysicalEthernetInterface()
    {
        NetworkAdapterSnapshot ethernet = Adapter(
            "ethernet-id",
            "Ethernet",
            "Intel Ethernet",
            NetworkInterfaceType.GigabitEthernet,
            "192.168.0.103",
            24);

        ControllerNetworkInterface selection = AssertSingle(
            PhysicalNetworkInterfaceDiscovery.FindUsable([ethernet]));

        Assert.AreEqual(ControllerInterfaceType.Ethernet, selection.InterfaceType);
        Assert.AreEqual(IPAddress.Parse("192.168.0.103"), selection.LocalAddress);
        Assert.AreEqual(24, selection.PrefixLength);
    }

    [TestMethod]
    public void ExcludesVirtualTunnelLoopbackAndDisconnectedAdapters()
    {
        NetworkAdapterSnapshot physical = Adapter(
            "physical", "Ethernet", "Intel I225-V", NetworkInterfaceType.Ethernet, "10.1.2.3", 24);
        NetworkAdapterSnapshot hyperV = Adapter(
            "hyperv", "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter",
            NetworkInterfaceType.Ethernet, "172.20.0.1", 20);
        NetworkAdapterSnapshot vmware = Adapter(
            "vmware", "VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter",
            NetworkInterfaceType.Ethernet, "192.168.220.1", 24);
        NetworkAdapterSnapshot vpn = Adapter(
            "vpn", "VPN", "WireGuard Tunnel", NetworkInterfaceType.Ethernet, "10.9.0.2", 32);
        NetworkAdapterSnapshot disconnected = Adapter(
            "down", "Ethernet 2", "USB Ethernet", NetworkInterfaceType.Ethernet, "192.168.4.3", 24,
            OperationalStatus.Down);
        NetworkAdapterSnapshot tunnel = Adapter(
            "tunnel", "Tunnel", "Microsoft Teredo Tunneling Adapter", NetworkInterfaceType.Tunnel,
            "192.0.0.1", 32);
        NetworkAdapterSnapshot loopback = Adapter(
            "loopback", "Loopback", "Software Loopback Interface", NetworkInterfaceType.Loopback,
            "127.0.0.1", 8);

        IReadOnlyList<ControllerNetworkInterface> result = PhysicalNetworkInterfaceDiscovery.FindUsable(
            [physical, hyperV, vmware, vpn, disconnected, tunnel, loopback]);

        ControllerNetworkInterface selection = AssertSingle(result);
        Assert.AreEqual("physical", selection.Id);
    }

    [TestMethod]
    public void KeepsMultiplePhysicalAdaptersAsSeparateOptions()
    {
        NetworkAdapterSnapshot wifi = Adapter(
            "wifi", "Wi-Fi", "Intel AX211", NetworkInterfaceType.Wireless80211, "192.168.5.20", 24);
        NetworkAdapterSnapshot onboard = Adapter(
            "onboard", "Ethernet", "Intel I225-V", NetworkInterfaceType.Ethernet, "10.0.0.20", 24);
        NetworkAdapterSnapshot usb = Adapter(
            "usb", "Ethernet 2", "Realtek USB GbE", NetworkInterfaceType.GigabitEthernet, "172.16.1.20", 16);

        IReadOnlyList<ControllerNetworkInterface> result =
            PhysicalNetworkInterfaceDiscovery.FindUsable([wifi, onboard, usb]);

        Assert.HasCount(3, result);
        CollectionAssert.AreEquivalent(
            new[] { "wifi", "onboard", "usb" },
            result.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    [DataRow("192.168.5.168", 16, "255.255.0.0", "192.168.255.255")]
    [DataRow("192.168.0.103", 24, "255.255.255.0", "192.168.0.255")]
    public void CalculatesSubnetMaskAndDirectedBroadcast(
        string localAddress,
        int prefixLength,
        string expectedMask,
        string expectedBroadcast)
    {
        Assert.AreEqual(
            IPAddress.Parse(expectedMask),
            Ipv4Network.CalculateSubnetMask(prefixLength));
        Assert.AreEqual(
            IPAddress.Parse(expectedBroadcast),
            Ipv4Network.CalculateBroadcast(IPAddress.Parse(localAddress), prefixLength));
    }

    [TestMethod]
    public void RestoresSavedAdapterByStableId()
    {
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.1.10", 24);
        ControllerNetworkInterface ethernet = Interface(
            "ethernet", ControllerInterfaceType.Ethernet, "Intel Ethernet", "10.0.0.10", 24);
        using var provider = new FakeNetworkInterfaceProvider([wifi, ethernet]);
        var store = new MemorySelectedInterfaceStore { Value = "ethernet" };
        using var manager = new NetworkInterfaceSelectionManager(provider, store);

        NetworkInterfaceSelectionState state = manager.Initialize();

        Assert.AreEqual(ethernet, state.SelectedInterface);
        StringAssert.Contains(state.Message, "Restored saved network interface");
        Assert.AreEqual("ethernet", store.Value);
    }

    [TestMethod]
    public void MissingSavedAdapterFallsBackToFirstUsableInterfaceAndReportsChange()
    {
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.1.10", 24);
        using var provider = new FakeNetworkInterfaceProvider([wifi]);
        var store = new MemorySelectedInterfaceStore { Value = "missing-adapter" };
        using var manager = new NetworkInterfaceSelectionManager(provider, store);

        NetworkInterfaceSelectionState state = manager.Initialize();

        Assert.AreEqual(wifi, state.SelectedInterface);
        Assert.AreEqual("wifi", store.Value);
        StringAssert.Contains(state.Message, "saved network interface is unavailable");
        StringAssert.Contains(state.Message, "Switched to");
    }

    [TestMethod]
    public void SelectedAdapterDisappearingLeavesNoSelectionAndDoesNotChooseAnother()
    {
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.1.10", 24);
        ControllerNetworkInterface ethernet = Interface(
            "ethernet", ControllerInterfaceType.Ethernet, "Intel Ethernet", "10.0.0.10", 24);
        using var provider = new FakeNetworkInterfaceProvider([wifi, ethernet]);
        var store = new MemorySelectedInterfaceStore { Value = "wifi" };
        using var manager = new NetworkInterfaceSelectionManager(provider, store);
        manager.Initialize();

        provider.SetInterfacesAndRaise([ethernet]);

        Assert.IsNull(manager.State.SelectedInterface);
        Assert.HasCount(1, manager.State.AvailableInterfaces);
        StringAssert.Contains(manager.State.Message, "unavailable");
        Assert.AreEqual("wifi", store.Value);
    }

    [TestMethod]
    public void ManualBroadcastOverrideReplacesCalculatedDestination()
    {
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.5.168", 16);

        BroadcastAddressResolution calculated = BroadcastAddressResolver.Resolve(wifi, "  ");
        BroadcastAddressResolution overridden = BroadcastAddressResolver.Resolve(wifi, "10.20.30.255");

        Assert.AreEqual(IPAddress.Parse("192.168.255.255"), calculated.Address);
        Assert.AreEqual(IPAddress.Parse("10.20.30.255"), overridden.Address);
        Assert.IsTrue(overridden.IsValid);
    }

    [TestMethod]
    public void ChangingSelectionDisposesOldSocketAndCreatesNewBoundSocket()
    {
        var factory = new FakeUdpSocketFactory();
        using var service = new UdpControllerService(factory);
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.5.168", 16);
        ControllerNetworkInterface ethernet = Interface(
            "ethernet", ControllerInterfaceType.Ethernet, "Intel Ethernet", "192.168.0.103", 24);

        service.ChangeSelection(wifi);
        service.ChangeSelection(ethernet);

        Assert.HasCount(2, factory.Created);
        Assert.AreEqual(wifi.LocalAddress, factory.Created[0].LocalAddress);
        Assert.IsTrue(factory.Created[0].EnableBroadcast);
        Assert.IsTrue(factory.Created[0].Socket.IsDisposed);
        Assert.AreEqual(ethernet.LocalAddress, factory.Created[1].LocalAddress);
        Assert.IsTrue(factory.Created[1].EnableBroadcast);
        Assert.IsFalse(factory.Created[1].Socket.IsDisposed);
        Assert.AreEqual(ethernet, service.Selection);
        Assert.IsTrue(service.IsReady);
    }

    [TestMethod]
    public void ChangingReceiveTimestampModeRecreatesSelectedSocket()
    {
        var factory = new FakeUdpSocketFactory();
        using var service = new UdpControllerService(factory);
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.5.168", 16);

        service.ChangeSelection(wifi);
        service.ChangeReceiveTimestampMode(UdpReceiveTimestampMode.DedicatedBlockingThread);

        Assert.AreEqual(UdpReceiveTimestampMode.DedicatedBlockingThread, service.ReceiveTimestampMode);
        Assert.HasCount(2, factory.Created);
        Assert.IsTrue(factory.Created[0].Socket.IsDisposed);
        Assert.IsFalse(factory.Created[1].Socket.IsDisposed);
        Assert.AreEqual(wifi, service.Selection);
        Assert.IsTrue(service.IsReady);
    }

    [TestMethod]
    public async Task SendsAllThreeCommandCopiesThroughSelectedSocketAndExistingPort()
    {
        var factory = new FakeUdpSocketFactory();
        using var service = new UdpControllerService(factory);
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.5.168", 16);
        service.ChangeSelection(wifi);
        var command = new CommandPacket(CommandType.Reset, 0x1234, 20, 0);
        IPAddress destination = IPAddress.Parse("10.20.30.255");

        await service.SendCommandAsync(command, destination);

        FakeUdpSocket socket = factory.Created[0].Socket;
        Assert.HasCount(3, socket.Destinations);
        Assert.IsTrue(socket.Destinations.All(endpoint => endpoint.Address.Equals(destination)));
        Assert.IsTrue(socket.Destinations.All(endpoint => endpoint.Port == 5000));
    }

    private static NetworkAdapterSnapshot Adapter(
        string id,
        string name,
        string description,
        NetworkInterfaceType type,
        string address,
        int prefixLength,
        OperationalStatus status = OperationalStatus.Up) =>
        new(
            id,
            name,
            description,
            type,
            status,
            PhysicalAddressLength: 6,
            HasIpv4Gateway: true,
            [new Ipv4AddressSnapshot(IPAddress.Parse(address), prefixLength)]);

    private static ControllerNetworkInterface Interface(
        string id,
        ControllerInterfaceType type,
        string friendlyName,
        string address,
        int prefixLength) =>
        new(id, type, friendlyName, IPAddress.Parse(address), prefixLength);

    private static ControllerNetworkInterface AssertSingle(
        IReadOnlyList<ControllerNetworkInterface> interfaces)
    {
        Assert.HasCount(1, interfaces);
        return interfaces[0];
    }

    private sealed class FakeNetworkInterfaceProvider(
        IReadOnlyList<ControllerNetworkInterface> interfaces) : INetworkInterfaceProvider
    {
        private IReadOnlyList<ControllerNetworkInterface> interfaces = interfaces;

        public event EventHandler? NetworkAddressChanged;

        public IReadOnlyList<ControllerNetworkInterface> GetUsableInterfaces() => interfaces;

        public void SetInterfacesAndRaise(IReadOnlyList<ControllerNetworkInterface> value)
        {
            interfaces = value;
            NetworkAddressChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }

    private sealed class MemorySelectedInterfaceStore : ISelectedInterfaceStore
    {
        public string? Value { get; set; }

        public string? LoadSelectedInterfaceId() => Value;

        public void SaveSelectedInterfaceId(string interfaceId) => Value = interfaceId;
    }

    private sealed class FakeUdpSocketFactory : IControllerUdpSocketFactory
    {
        public List<CreatedSocket> Created { get; } = [];

        public IControllerUdpSocket CreateBound(IPAddress localAddress, bool enableBroadcast)
        {
            var socket = new FakeUdpSocket();
            Created.Add(new CreatedSocket(localAddress, enableBroadcast, socket));
            return socket;
        }
    }

    private sealed record CreatedSocket(
        IPAddress LocalAddress,
        bool EnableBroadcast,
        FakeUdpSocket Socket);

    private sealed class FakeUdpSocket : IControllerUdpSocket, ITimestampedBlockingControllerUdpSocket
    {
        private readonly ManualResetEventSlim disposedSignal = new(false);
        public bool IsDisposed { get; private set; }
        public List<IPEndPoint> Destinations { get; } = [];

        public ValueTask<int> SendAsync(
            ReadOnlyMemory<byte> datagram,
            IPEndPoint destination,
            CancellationToken cancellationToken)
        {
            Destinations.Add(destination);
            return ValueTask.FromResult(datagram.Length);
        }

        public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The infinite receive should only end by cancellation.");
        }

        public TimestampedUdpReceiveResult ReceiveTimestampedBlocking()
        {
            disposedSignal.Wait();
            throw new ObjectDisposedException(nameof(FakeUdpSocket));
        }

        public void Dispose()
        {
            IsDisposed = true;
            disposedSignal.Set();
        }
    }
}
