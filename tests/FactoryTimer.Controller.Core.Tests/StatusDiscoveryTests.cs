using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FactoryTimer.Controller.Core;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class StatusDiscoveryTests
{
    [TestMethod]
    public async Task DiscoveryBeginsImmediatelyWithoutButtonInteraction()
    {
        var transport = new RecordingStatusTransport();
        var delay = new ControlledDiscoveryDelay();
        using var discovery = new StatusDiscoveryService(
            transport,
            new SequenceCommandIdGenerator(101),
            delay);
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.5.168", 16);

        discovery.StartOrRestart(wifi, () => string.Empty);
        await WaitUntilAsync(() => transport.Requests.Count == 1);

        StatusRequestRecord request = transport.Requests.Single();
        Assert.AreEqual(101UL, request.CommandId);
        Assert.AreEqual(IPAddress.Parse("192.168.255.255"), request.Destination);
        Assert.IsTrue(discovery.IsRunning);
    }

    [TestMethod]
    public async Task EveryDiscoveryCycleUsesANewNonzeroCommandId()
    {
        var transport = new RecordingStatusTransport();
        var delay = new ControlledDiscoveryDelay();
        using var discovery = new StatusDiscoveryService(
            transport,
            new SequenceCommandIdGenerator(501, 502),
            delay);
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.1.10", 24);

        discovery.StartOrRestart(wifi, () => null);
        await WaitUntilAsync(() => transport.Requests.Count == 1);
        delay.ReleaseNext();
        await WaitUntilAsync(() => transport.Requests.Count == 2);

        ulong[] ids = transport.Requests.Select(request => request.CommandId).ToArray();
        CollectionAssert.AreEqual(new ulong[] { 501, 502 }, ids);
        Assert.IsTrue(ids.All(id => id != 0));
    }

    [TestMethod]
    public async Task InterfaceChangeCancelsOldLoopAndRestartsExactlyOnce()
    {
        var transport = new RecordingStatusTransport();
        var delay = new ControlledDiscoveryDelay();
        using var discovery = new StatusDiscoveryService(
            transport,
            new SequenceCommandIdGenerator(1, 2),
            delay);
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.5.168", 16);
        ControllerNetworkInterface ethernet = Interface(
            "ethernet", ControllerInterfaceType.Ethernet, "Intel Ethernet", "10.20.30.40", 24);

        discovery.StartOrRestart(wifi, () => null);
        await WaitUntilAsync(() => transport.Requests.Count == 1);
        discovery.StartOrRestart(ethernet, () => null);
        await WaitUntilAsync(() => transport.Requests.Count == 2);
        await Task.Delay(50);

        Assert.HasCount(2, transport.Requests);
        Assert.AreEqual(IPAddress.Parse("192.168.255.255"), transport.Requests[0].Destination);
        Assert.AreEqual(IPAddress.Parse("10.20.30.255"), transport.Requests[1].Destination);
    }

    [TestMethod]
    public async Task DiscoveryUsesSelectedBoundAdapterAndManualBroadcastOverride()
    {
        var socketFactory = new DiscoveryUdpSocketFactory();
        using var udp = new UdpControllerService(socketFactory);
        var delay = new ControlledDiscoveryDelay();
        using var discovery = new StatusDiscoveryService(
            udp,
            new SequenceCommandIdGenerator(0x1234),
            delay);
        ControllerNetworkInterface ethernet = Interface(
            "ethernet", ControllerInterfaceType.Ethernet, "Intel Ethernet", "192.168.0.103", 24);
        udp.ChangeSelection(ethernet);

        discovery.StartOrRestart(ethernet, () => "10.20.30.255");
        await WaitUntilAsync(() => socketFactory.Created[0].Socket.Sends.Count == 1);

        CreatedDiscoverySocket created = socketFactory.Created.Single();
        SentDatagram sent = created.Socket.Sends.Single();
        Assert.AreEqual(ethernet.LocalAddress, created.LocalAddress);
        Assert.AreEqual(IPAddress.Parse("10.20.30.255"), sent.Destination.Address);
        Assert.AreEqual(5000, sent.Destination.Port);
        Assert.AreEqual(
            "FCT1|CMD|STATUS_REQUEST|0000000000001234|0|0",
            Encoding.ASCII.GetString(sent.Bytes));
    }

    [TestMethod]
    public void DeviceIsSearchingThenOnlineOnlyAfterStatusAndOfflineAfterSixSeconds()
    {
        var tracker = new DeviceStatusTracker();
        DateTimeOffset started = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        tracker.BeginSearching(started);
        Assert.AreEqual(DeviceAvailability.Searching, tracker.GetAvailability(started));
        Assert.AreEqual(
            DeviceAvailability.Offline,
            tracker.GetAvailability(started + TimeSpan.FromSeconds(6)));

        DateTimeOffset statusReceived = started + TimeSpan.FromSeconds(7);
        tracker.RecordValidStatus(statusReceived);
        Assert.AreEqual(DeviceAvailability.Online, tracker.GetAvailability(statusReceived));
        Assert.AreEqual(
            DeviceAvailability.Online,
            tracker.GetAvailability(statusReceived + TimeSpan.FromMilliseconds(5999)));
        Assert.AreEqual(
            DeviceAvailability.Offline,
            tracker.GetAvailability(statusReceived + TimeSpan.FromSeconds(6)));
    }

    [TestMethod]
    public async Task StoppingDiscoveryCancelsTheActiveLoop()
    {
        var transport = new RecordingStatusTransport();
        var delay = new ControlledDiscoveryDelay();
        using var discovery = new StatusDiscoveryService(
            transport,
            new SequenceCommandIdGenerator(77),
            delay);
        ControllerNetworkInterface wifi = Interface(
            "wifi", ControllerInterfaceType.Wifi, "Intel Wi-Fi", "192.168.1.10", 24);
        discovery.StartOrRestart(wifi, () => null);
        await WaitUntilAsync(() => transport.Requests.Count == 1);

        discovery.Stop();
        await discovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(discovery.IsRunning);
        Assert.HasCount(1, transport.Requests);
    }

    private static ControllerNetworkInterface Interface(
        string id,
        ControllerInterfaceType type,
        string friendlyName,
        string address,
        int prefixLength) =>
        new(id, type, friendlyName, IPAddress.Parse(address), prefixLength);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingStatusTransport : IStatusRequestTransport
    {
        private readonly object gate = new();
        private readonly List<StatusRequestRecord> requests = [];

        public IReadOnlyList<StatusRequestRecord> Requests
        {
            get
            {
                lock (gate) return requests.ToArray();
            }
        }

        public Task SendStatusRequestAsync(
            ulong commandId,
            IPAddress destination,
            CancellationToken cancellationToken = default)
        {
            lock (gate) requests.Add(new(commandId, destination));
            return Task.CompletedTask;
        }
    }

    private sealed record StatusRequestRecord(ulong CommandId, IPAddress Destination);

    private sealed class SequenceCommandIdGenerator(params ulong[] values) : ICommandIdGenerator
    {
        private int index;

        public ulong NextCommandId()
        {
            if (index >= values.Length) throw new InvalidOperationException("No command ID remains.");
            return values[index++];
        }
    }

    private sealed class ControlledDiscoveryDelay : IStatusDiscoveryDelay
    {
        private readonly ConcurrentQueue<TaskCompletionSource> waiters = new();

        public Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            Assert.AreEqual(StatusDiscoveryService.DiscoveryInterval, interval);
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Enqueue(waiter);
            return WaitWithCancellationAsync(waiter, cancellationToken);
        }

        public void ReleaseNext()
        {
            Assert.IsTrue(waiters.TryDequeue(out TaskCompletionSource? waiter));
            waiter.SetResult();
        }

        private static async Task WaitWithCancellationAsync(
            TaskCompletionSource waiter,
            CancellationToken token)
        {
            using CancellationTokenRegistration registration = token.Register(
                () => waiter.TrySetCanceled(token));
            await waiter.Task;
        }
    }

    private sealed class DiscoveryUdpSocketFactory : IControllerUdpSocketFactory
    {
        public List<CreatedDiscoverySocket> Created { get; } = [];

        public IControllerUdpSocket CreateBound(IPAddress localAddress, bool enableBroadcast)
        {
            var socket = new DiscoveryUdpSocket();
            Created.Add(new(localAddress, enableBroadcast, socket));
            return socket;
        }
    }

    private sealed record CreatedDiscoverySocket(
        IPAddress LocalAddress,
        bool EnableBroadcast,
        DiscoveryUdpSocket Socket);

    private sealed class DiscoveryUdpSocket : IControllerUdpSocket
    {
        public List<SentDatagram> Sends { get; } = [];

        public ValueTask<int> SendAsync(
            ReadOnlyMemory<byte> datagram,
            IPEndPoint destination,
            CancellationToken cancellationToken)
        {
            byte[] bytes = datagram.ToArray();
            Sends.Add(new(bytes, destination));
            return ValueTask.FromResult(bytes.Length);
        }

        public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Receive should end only by cancellation.");
        }

        public void Dispose()
        {
        }
    }

    private sealed record SentDatagram(byte[] Bytes, IPEndPoint Destination);
}
