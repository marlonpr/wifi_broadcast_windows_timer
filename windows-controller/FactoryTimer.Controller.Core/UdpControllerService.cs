using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core;

public sealed class PacketReceivedEventArgs(
    InboundPacket packet,
    IPEndPoint remoteEndPoint,
    ControllerNetworkInterface networkInterface,
    long masterReceiveMicroseconds) : EventArgs
{
    public InboundPacket Packet { get; } = packet;
    public IPEndPoint RemoteEndPoint { get; } = remoteEndPoint;
    public ControllerNetworkInterface NetworkInterface { get; } = networkInterface;
    public long MasterReceiveMicroseconds { get; } = masterReceiveMicroseconds;
}

public sealed record ReceivedSyncReply(SyncReplyPacket Packet, long MasterT4Microseconds);

public enum UdpReceiveTimestampMode
{
    AsyncAwait = 0,
    DedicatedBlockingThread = 1,
}

public readonly record struct TimestampedUdpReceiveResult(
    UdpReceiveResult Result,
    long MasterReceiveMicroseconds);

public interface ITimestampedBlockingControllerUdpSocket
{
    TimestampedUdpReceiveResult ReceiveTimestampedBlocking();
}

public interface IControllerUdpSocket : IDisposable
{
    ValueTask<int> SendAsync(
        ReadOnlyMemory<byte> datagram,
        IPEndPoint destination,
        CancellationToken cancellationToken);

    ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IControllerUdpSocketFactory
{
    IControllerUdpSocket CreateBound(IPAddress localAddress, bool enableBroadcast);
}

public sealed class SystemControllerUdpSocketFactory : IControllerUdpSocketFactory
{
    public IControllerUdpSocket CreateBound(IPAddress localAddress, bool enableBroadcast) =>
        new SystemControllerUdpSocket(localAddress, enableBroadcast);

    private sealed class SystemControllerUdpSocket : IControllerUdpSocket, ITimestampedBlockingControllerUdpSocket
    {
        private readonly UdpClient client;

        public SystemControllerUdpSocket(IPAddress localAddress, bool enableBroadcast)
        {
            if (localAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("An IPv4 address is required.", nameof(localAddress));
            }

            client = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = enableBroadcast,
            };
            client.Client.Bind(new IPEndPoint(localAddress, 0));
        }

        public ValueTask<int> SendAsync(
            ReadOnlyMemory<byte> datagram,
            IPEndPoint destination,
            CancellationToken cancellationToken) =>
            client.SendAsync(datagram, destination, cancellationToken);

        public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken) =>
            client.ReceiveAsync(cancellationToken);

        public TimestampedUdpReceiveResult ReceiveTimestampedBlocking()
        {
            byte[] buffer = new byte[2048];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received = client.Client.ReceiveFrom(buffer, ref remote);
            long masterReceiveMicroseconds = MasterClock.NowMicroseconds;

            byte[] datagram = buffer.AsSpan(0, received).ToArray();
            return new TimestampedUdpReceiveResult(
                new UdpReceiveResult(datagram, (IPEndPoint)remote),
                masterReceiveMicroseconds);
        }

        public void Dispose() => client.Dispose();
    }
}

public sealed class UdpControllerService : IStatusRequestTransport, IDisposable
{
    private const int CommandPort = 5000;
    private static readonly TimeSpan DefaultSyncTimeout = TimeSpan.FromMilliseconds(750);
    private readonly object gate = new();
    private readonly object changeGate = new();
    private readonly IControllerUdpSocketFactory socketFactory;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ReceivedSyncReply>> syncReplyWaiters = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SyncAppliedPacket>> syncAppliedWaiters = new();
    private SocketSession? session;
    private UdpReceiveTimestampMode receiveTimestampMode = UdpReceiveTimestampMode.AsyncAwait;
    private bool disposed;

    public UdpControllerService(IControllerUdpSocketFactory? socketFactory = null)
    {
        this.socketFactory = socketFactory ?? new SystemControllerUdpSocketFactory();
    }

    public UdpReceiveTimestampMode ReceiveTimestampMode
    {
        get
        {
            lock (gate) return receiveTimestampMode;
        }
    }

    public event EventHandler<PacketReceivedEventArgs>? PacketReceived;
    public event EventHandler<string>? ReceiveError;

    public ControllerNetworkInterface? Selection
    {
        get
        {
            lock (gate) return session?.Selection;
        }
    }

    public bool IsReady
    {
        get
        {
            lock (gate) return session is not null;
        }
    }

    public void ChangeSelection(ControllerNetworkInterface? selection)
    {
        lock (changeGate)
        {
            SocketSession? oldSession;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (session?.Selection == selection) return;
                oldSession = session;
                session = null;
            }

            StopSession(oldSession);
            CancelSyncWaiters();
            if (selection is null) return;

            StartNewSession(selection);
        }
    }

    public void ChangeReceiveTimestampMode(UdpReceiveTimestampMode mode)
    {
        if (!Enum.IsDefined(typeof(UdpReceiveTimestampMode), mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        lock (changeGate)
        {
            SocketSession? oldSession;
            ControllerNetworkInterface? selection;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (receiveTimestampMode == mode) return;
                receiveTimestampMode = mode;
                oldSession = session;
                selection = oldSession?.Selection;
                session = null;
            }

            StopSession(oldSession);
            CancelSyncWaiters();
            if (selection is not null)
            {
                StartNewSession(selection);
            }
        }
    }

    private void StartNewSession(ControllerNetworkInterface selection)
    {
        IControllerUdpSocket socket = socketFactory.CreateBound(selection.LocalAddress, enableBroadcast: true);
        var replacement = new SocketSession(selection, socket);
        lock (gate) session = replacement;

        if (ReceiveTimestampMode == UdpReceiveTimestampMode.DedicatedBlockingThread)
        {
            if (socket is not ITimestampedBlockingControllerUdpSocket)
            {
                lock (gate)
                {
                    if (ReferenceEquals(session, replacement)) session = null;
                }
                socket.Dispose();
                replacement.Cancellation.Dispose();
                throw new NotSupportedException(
                    "The selected UDP socket implementation does not support the dedicated blocking T4 receiver.");
            }

            replacement.ReceiverThread = new Thread(() => ReceiveLoopBlocking(replacement))
            {
                Name = "FactoryTimer UDP T4 receiver",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            replacement.ReceiverThread.Start();
        }
        else
        {
            replacement.ReceiverTask = ReceiveLoopAsync(replacement);
        }
    }

    public Task SendCommandAsync(
        CommandPacket command,
        IPAddress broadcast,
        CancellationToken token = default) =>
        SendRepeatedAsync(FactoryProtocol.SerializeCommandBytes(command), broadcast, token);

    public Task SendCommandUnicastAsync(
        CommandPacket command,
        IPAddress destination,
        CancellationToken token = default) =>
        SendRepeatedAsync(FactoryProtocol.SerializeCommandBytes(command), destination, token);

    public async Task<ReceivedSyncReply> ExchangeSyncAsync(
        ulong syncId,
        long masterT1Microseconds,
        IPAddress destination,
        TimeSpan? masterToDeviceArtificialDelay = null,
        uint deviceToMasterArtificialDelayMicroseconds = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        SocketSession activeSession = GetActiveSession();
        var waiter = new TaskCompletionSource<ReceivedSyncReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!syncReplyWaiters.TryAdd(syncId, waiter))
        {
            throw new InvalidOperationException($"Synchronization ID {syncId:X16} is already in use.");
        }

        try
        {
            // t1 is captured by the caller BEFORE this optional delay. This is
            // intentional: the delay must appear as Master->ESP path latency.
            TimeSpan forwardDelay = masterToDeviceArtificialDelay ?? TimeSpan.Zero;
            if (forwardDelay > TimeSpan.Zero)
            {
                await Task.Delay(forwardDelay, cancellationToken).ConfigureAwait(false);
            }

            byte[] packet = FactoryProtocol.SerializeSyncRequestBytes(
                new SyncRequestPacket(
                    syncId,
                    masterT1Microseconds,
                    deviceToMasterArtificialDelayMicroseconds));
            await activeSession.Socket.SendAsync(
                packet,
                new IPEndPoint(destination, CommandPort),
                cancellationToken);

            using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultSyncTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            try
            {
                return await waiter.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"SYNC {syncId:X16} timed out for {destination}.");
            }
        }
        finally
        {
            syncReplyWaiters.TryRemove(syncId, out _);
        }
    }

    public async Task<SyncAppliedPacket> ApplySyncAsync(
        SyncSetPacket syncSet,
        IPAddress destination,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        SocketSession activeSession = GetActiveSession();
        var waiter = new TaskCompletionSource<SyncAppliedPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!syncAppliedWaiters.TryAdd(syncSet.SyncId, waiter))
        {
            throw new InvalidOperationException($"Synchronization ID {syncSet.SyncId:X16} is already in use.");
        }

        try
        {
            byte[] packet = FactoryProtocol.SerializeSyncSetBytes(syncSet);
            await activeSession.Socket.SendAsync(
                packet,
                new IPEndPoint(destination, CommandPort),
                cancellationToken);

            using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultSyncTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            try
            {
                return await waiter.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"SYNC_SET {syncSet.SyncId:X16} timed out for {destination}.");
            }
        }
        finally
        {
            syncAppliedWaiters.TryRemove(syncSet.SyncId, out _);
        }
    }

    public async Task SendStatusRequestAsync(
        ulong commandId,
        IPAddress destination,
        CancellationToken cancellationToken = default)
    {
        SocketSession activeSession = GetActiveSession();
        var command = new CommandPacket(CommandType.StatusRequest, commandId, 0, 0);
        byte[] packet = FactoryProtocol.SerializeCommandBytes(command);
        await activeSession.Socket.SendAsync(
            packet,
            new IPEndPoint(destination, CommandPort),
            cancellationToken);
    }

    private async Task SendRepeatedAsync(
        byte[] packet,
        IPAddress destinationAddress,
        CancellationToken token)
    {
        SocketSession activeSession = GetActiveSession();
        var destination = new IPEndPoint(destinationAddress, CommandPort);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await activeSession.Socket.SendAsync(packet, destination, token);
            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40), token);
            }
        }
    }

    private SocketSession GetActiveSession()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return session ?? throw new InvalidOperationException(
                "No usable network interface is selected.");
        }
    }

    private async Task ReceiveLoopAsync(SocketSession activeSession)
    {
        CancellationToken token = activeSession.Cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await activeSession.Socket.ReceiveAsync(token);
                long masterReceiveMicroseconds = MasterClock.NowMicroseconds;
                if (!IsCurrent(activeSession)) break;
                ProcessReceivedDatagram(activeSession, result, masterReceiveMicroseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested || !IsCurrent(activeSession))
            {
                break;
            }
            catch (SocketException exception) when (!token.IsCancellationRequested && IsCurrent(activeSession))
            {
                ReceiveError?.Invoke(this, $"UDP receive error: {exception.Message}");
                try
                {
                    await Task.Delay(250, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void ReceiveLoopBlocking(SocketSession activeSession)
    {
        CancellationToken token = activeSession.Cancellation.Token;
        var blockingSocket = (ITimestampedBlockingControllerUdpSocket)activeSession.Socket;

        while (!token.IsCancellationRequested)
        {
            try
            {
                TimestampedUdpReceiveResult received = blockingSocket.ReceiveTimestampedBlocking();
                if (!IsCurrent(activeSession)) break;
                ProcessReceivedDatagram(
                    activeSession,
                    received.Result,
                    received.MasterReceiveMicroseconds);
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested || !IsCurrent(activeSession))
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested || !IsCurrent(activeSession))
            {
                break;
            }
            catch (SocketException exception)
            {
                ReceiveError?.Invoke(this, $"UDP blocking receive error: {exception.Message}");
                if (token.WaitHandle.WaitOne(250)) break;
            }
        }
    }

    private void ProcessReceivedDatagram(
        SocketSession activeSession,
        UdpReceiveResult result,
        long masterReceiveMicroseconds)
    {
        if (FactoryProtocol.TryParseInbound(
            result.Buffer,
            out InboundPacket? packet,
            out ProtocolParseError error))
        {
            if (packet is SyncReplyPacket syncReply &&
                syncReplyWaiters.TryGetValue(syncReply.SyncId, out TaskCompletionSource<ReceivedSyncReply>? syncWaiter))
            {
                syncWaiter.TrySetResult(new ReceivedSyncReply(syncReply, masterReceiveMicroseconds));
            }
            else if (packet is SyncAppliedPacket syncApplied &&
                     syncAppliedWaiters.TryGetValue(syncApplied.SyncId, out TaskCompletionSource<SyncAppliedPacket>? appliedWaiter))
            {
                appliedWaiter.TrySetResult(syncApplied);
            }

            PacketReceived?.Invoke(
                this,
                new PacketReceivedEventArgs(
                    packet!,
                    result.RemoteEndPoint,
                    activeSession.Selection,
                    masterReceiveMicroseconds));
        }
        else
        {
            ReceiveError?.Invoke(
                this,
                $"Discarded malformed response from {result.RemoteEndPoint}: {error}");
        }
    }

    private bool IsCurrent(SocketSession candidate)
    {
        lock (gate) return ReferenceEquals(session, candidate);
    }

    private static void StopSession(SocketSession? oldSession)
    {
        if (oldSession is null) return;
        oldSession.Cancellation.Cancel();
        oldSession.Socket.Dispose();

        Thread? receiverThread = oldSession.ReceiverThread;
        if (receiverThread is not null && receiverThread.IsAlive && receiverThread != Thread.CurrentThread)
        {
            receiverThread.Join(TimeSpan.FromSeconds(2));
        }

        oldSession.Cancellation.Dispose();
    }

    private void CancelSyncWaiters()
    {
        foreach (TaskCompletionSource<ReceivedSyncReply> waiter in syncReplyWaiters.Values)
        {
            waiter.TrySetCanceled();
        }
        syncReplyWaiters.Clear();
        foreach (TaskCompletionSource<SyncAppliedPacket> waiter in syncAppliedWaiters.Values)
        {
            waiter.TrySetCanceled();
        }
        syncAppliedWaiters.Clear();
    }

    public void Dispose()
    {
        lock (changeGate)
        {
            SocketSession? oldSession;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                oldSession = session;
                session = null;
            }
            StopSession(oldSession);
            CancelSyncWaiters();
        }
        GC.SuppressFinalize(this);
    }

    private sealed class SocketSession(
        ControllerNetworkInterface selection,
        IControllerUdpSocket socket)
    {
        public ControllerNetworkInterface Selection { get; } = selection;
        public IControllerUdpSocket Socket { get; } = socket;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? ReceiverTask { get; set; }
        public Thread? ReceiverThread { get; set; }
    }
}
