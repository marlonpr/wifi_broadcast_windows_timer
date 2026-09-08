using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace FactoryTimer.Controller.Core;

public interface IStatusRequestTransport
{
    Task SendStatusRequestAsync(
        ulong commandId,
        IPAddress destination,
        CancellationToken cancellationToken = default);
}

public interface ICommandIdGenerator
{
    ulong NextCommandId();
}

public sealed class CryptographicCommandIdGenerator : ICommandIdGenerator
{
    public ulong NextCommandId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong result;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            result = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        } while (result == 0);
        return result;
    }
}

public interface IStatusDiscoveryDelay
{
    Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken);
}

public sealed class SystemStatusDiscoveryDelay : IStatusDiscoveryDelay
{
    public Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken) =>
        Task.Delay(interval, cancellationToken);
}

public sealed class StatusDiscoveryService : IDisposable
{
    public static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(2);

    private readonly object gate = new();
    private readonly IStatusRequestTransport transport;
    private readonly ICommandIdGenerator commandIdGenerator;
    private readonly IStatusDiscoveryDelay delay;
    private CancellationTokenSource? cancellation;
    private Task completion = Task.CompletedTask;
    private long generation;
    private bool disposed;

    public StatusDiscoveryService(
        IStatusRequestTransport transport,
        ICommandIdGenerator? commandIdGenerator = null,
        IStatusDiscoveryDelay? delay = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.commandIdGenerator = commandIdGenerator ?? new CryptographicCommandIdGenerator();
        this.delay = delay ?? new SystemStatusDiscoveryDelay();
    }

    public event EventHandler<string>? DiscoveryError;

    public bool IsRunning
    {
        get
        {
            lock (gate) return cancellation is not null;
        }
    }

    public Task Completion
    {
        get
        {
            lock (gate) return completion;
        }
    }

    public void StartOrRestart(
        ControllerNetworkInterface selectedInterface,
        Func<string?> manualBroadcastOverrideProvider)
    {
        ArgumentNullException.ThrowIfNull(selectedInterface);
        ArgumentNullException.ThrowIfNull(manualBroadcastOverrideProvider);

        CancellationTokenSource? previousCancellation;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            previousCancellation = cancellation;
            var replacement = new CancellationTokenSource();
            cancellation = replacement;
            long activeGeneration = ++generation;
            Task previousCompletion = completion;
            completion = Task.Run(() => RunAfterPreviousAsync(
                previousCompletion,
                activeGeneration,
                selectedInterface,
                manualBroadcastOverrideProvider,
                replacement.Token));
        }

        CancelAndDispose(previousCancellation);
    }

    public void Stop()
    {
        CancellationTokenSource? oldCancellation;
        lock (gate)
        {
            oldCancellation = cancellation;
            cancellation = null;
            generation++;
        }
        CancelAndDispose(oldCancellation);
    }

    private async Task RunAfterPreviousAsync(
        Task previousCompletion,
        long activeGeneration,
        ControllerNetworkInterface selectedInterface,
        Func<string?> manualBroadcastOverrideProvider,
        CancellationToken token)
    {
        try
        {
            await previousCompletion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (!IsCurrent(activeGeneration) || token.IsCancellationRequested) return;

        while (!token.IsCancellationRequested && IsCurrent(activeGeneration))
        {
            try
            {
                BroadcastAddressResolution resolution = BroadcastAddressResolver.Resolve(
                    selectedInterface,
                    manualBroadcastOverrideProvider());
                if (!resolution.IsValid)
                {
                    DiscoveryError?.Invoke(this, resolution.Error!);
                }
                else
                {
                    ulong commandId = commandIdGenerator.NextCommandId();
                    if (commandId == 0)
                    {
                        throw new InvalidOperationException("The discovery command ID generator returned zero.");
                    }
                    await transport.SendStatusRequestAsync(
                        commandId,
                        resolution.Address!,
                        token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                DiscoveryError?.Invoke(this, $"STATUS_REQUEST discovery failed: {exception.Message}");
            }

            try
            {
                await delay.DelayAsync(DiscoveryInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private bool IsCurrent(long candidateGeneration)
    {
        lock (gate)
        {
            return !disposed && cancellation is not null && generation == candidateGeneration;
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null) return;
        source.Cancel();
        source.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource? oldCancellation;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            oldCancellation = cancellation;
            cancellation = null;
            generation++;
        }
        CancelAndDispose(oldCancellation);
        GC.SuppressFinalize(this);
    }
}
