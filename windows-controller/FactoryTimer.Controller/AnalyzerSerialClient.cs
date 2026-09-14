using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace FactoryTimer.Controller;

internal sealed class AnalyzerSerialClient : IDisposable
{
    private const int BaudRate = 115200;
    private readonly ConcurrentQueue<AnalyzerRecord> records = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> waiters = new();
    private SerialPort? port;
    private CancellationTokenSource? readerCancellation;
    private Task? readerTask;
    private bool disposed;

    public bool IsConnected => port?.IsOpen == true;

    public async Task ConnectAsync(string portName, CancellationToken cancellationToken)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(AnalyzerSerialClient));
        }
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("Enter the ESP32 analyzer COM port, for example COM6.", nameof(portName));
        }
        if (IsConnected) return;

        var serial = new SerialPort(portName.Trim(), BaudRate, Parity.None, 8, StopBits.One)
        {
            DtrEnable = false,
            RtsEnable = false,
            Handshake = Handshake.None,
            NewLine = "\n",
            ReadTimeout = 200,
            WriteTimeout = 1000,
            Encoding = Encoding.ASCII,
        };

        try
        {
            serial.Open();
            port = serial;
            readerCancellation = new CancellationTokenSource();
            readerTask = Task.Run(() => ReaderLoop(serial, readerCancellation.Token));

            // Opening an ESP32 development-board serial port may reset the board
            // once through its auto-reset circuit. Repeated PINGs make startup
            // deterministic without using DTR/RTS as a marker signal.
            long deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pong = NewWaiter("PONG");
                serial.WriteLine("PING");
                if (await WaitOrTimeoutAsync(pong.Task, TimeSpan.FromMilliseconds(600), cancellationToken))
                {
                    return;
                }
                waiters.TryRemove("PONG", out _);
                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException($"No PONG received from ESP32 analyzer on {portName}.");
        }
        catch
        {
            DisposePort();
            throw;
        }
    }

    public async Task BeginTrialAsync(ulong runId, int trial, CancellationToken cancellationToken)
    {
        SerialPort serial = RequirePort();
        string key = BeginKey(runId, trial);
        TaskCompletionSource<bool> waiter = NewWaiter(key);
        serial.WriteLine(FormattableString.Invariant($"BEGIN|{runId}|{trial}"));
        if (!await WaitOrTimeoutAsync(waiter.Task, TimeSpan.FromSeconds(1), cancellationToken))
        {
            waiters.TryRemove(key, out _);
            throw new TimeoutException($"Analyzer did not ACK BEGIN for run {runId}, trial {trial}.");
        }
    }

    public async Task EndTrialAsync(ulong runId, int trial, CancellationToken cancellationToken)
    {
        SerialPort serial = RequirePort();
        string key = SummaryKey(runId, trial);
        TaskCompletionSource<bool> waiter = NewWaiter(key);
        serial.WriteLine(FormattableString.Invariant($"END|{runId}|{trial}"));
        if (!await WaitOrTimeoutAsync(waiter.Task, TimeSpan.FromSeconds(1), cancellationToken))
        {
            waiters.TryRemove(key, out _);
            throw new TimeoutException($"Analyzer did not return SUMMARY for run {runId}, trial {trial}.");
        }
    }

    public async Task<string> WriteCsvAsync(string benchmarkRunId)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string root = string.IsNullOrWhiteSpace(documents) ? AppContext.BaseDirectory : documents;
        string directory = Path.Combine(root, "FactoryTimerBenchmarks");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"factory_timer_esp32_analyzer_{benchmarkRunId}.csv");

        var builder = new StringBuilder();
        builder.AppendLine("HostTimestampUtc,RunId,Trial,RecordType,Channel,Device,AnalyzerTimestampUs,EdgeMask,EdgeCount,Raw");
        foreach (AnalyzerRecord row in records)
        {
            builder.AppendLine(row.ToCsv());
        }
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private void ReaderLoop(SerialPort serial, CancellationToken token)
    {
        while (!token.IsCancellationRequested && serial.IsOpen)
        {
            string line;
            try
            {
                line = serial.ReadLine().Trim('\r', '\n', ' ');
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                return;
            }

            if (line.Length == 0) continue;
            ProcessLine(line, DateTimeOffset.UtcNow);
        }
    }

    private void ProcessLine(string line, DateTimeOffset hostTimestampUtc)
    {
        // Ignore bootloader/ESP-IDF logging that may share UART0. Analyzer
        // protocol records always begin with ANZ|.
        if (!line.StartsWith("ANZ|", StringComparison.Ordinal)) return;

        string[] fields = line.Split('|');
        if (fields.Length >= 2 && fields[1] == "PONG")
        {
            Signal("PONG");
            records.Enqueue(AnalyzerRecord.CreateRaw(hostTimestampUtc, "PONG", line));
            return;
        }

        if (fields.Length == 5 && fields[1] == "ACK" && fields[2] == "BEGIN" &&
            ulong.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out ulong ackRun) &&
            int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int ackTrial))
        {
            Signal(BeginKey(ackRun, ackTrial));
            records.Enqueue(AnalyzerRecord.ForTrial(hostTimestampUtc, ackRun, ackTrial, "BEGIN_ACK", line));
            return;
        }

        if (fields.Length == 7 && fields[1] == "EDGE" &&
            ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong edgeRun) &&
            int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out int edgeTrial) &&
            int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int channel) &&
            long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestampUs))
        {
            records.Enqueue(new AnalyzerRecord(
                hostTimestampUtc,
                edgeRun,
                edgeTrial,
                "EDGE",
                channel,
                fields[5],
                timestampUs,
                string.Empty,
                null,
                line));
            return;
        }

        if (fields.Length == 6 && fields[1] == "SUMMARY" &&
            ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong summaryRun) &&
            int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out int summaryTrial) &&
            int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out int edgeCount))
        {
            records.Enqueue(new AnalyzerRecord(
                hostTimestampUtc,
                summaryRun,
                summaryTrial,
                "SUMMARY",
                null,
                string.Empty,
                null,
                fields[4],
                edgeCount,
                line));
            Signal(SummaryKey(summaryRun, summaryTrial));
            return;
        }

        records.Enqueue(AnalyzerRecord.CreateRaw(hostTimestampUtc, "RAW", line));
    }

    private TaskCompletionSource<bool> NewWaiter(string key)
    {
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!waiters.TryAdd(key, waiter))
        {
            waiters.TryRemove(key, out _);
            if (!waiters.TryAdd(key, waiter))
            {
                throw new InvalidOperationException($"Analyzer waiter already exists: {key}");
            }
        }
        return waiter;
    }

    private void Signal(string key)
    {
        if (waiters.TryRemove(key, out TaskCompletionSource<bool>? waiter))
        {
            waiter.TrySetResult(true);
        }
    }

    private static async Task<bool> WaitOrTimeoutAsync(
        Task task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await task.WaitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private SerialPort RequirePort() => port is { IsOpen: true } serial
        ? serial
        : throw new InvalidOperationException("ESP32 analyzer is not connected.");

    private static string BeginKey(ulong runId, int trial) => FormattableString.Invariant($"BEGIN:{runId}:{trial}");
    private static string SummaryKey(ulong runId, int trial) => FormattableString.Invariant($"SUMMARY:{runId}:{trial}");

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DisposePort();
        GC.SuppressFinalize(this);
    }

    private void DisposePort()
    {
        if (readerCancellation is not null)
        {
            readerCancellation.Cancel();
        }
        try
        {
            port?.Close();
        }
        catch
        {
        }
        try
        {
            readerTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
        }
        port?.Dispose();
        readerCancellation?.Dispose();
        port = null;
        readerCancellation = null;
        readerTask = null;
        foreach (TaskCompletionSource<bool> waiter in waiters.Values)
        {
            waiter.TrySetCanceled();
        }
        waiters.Clear();
    }

    private sealed record AnalyzerRecord(
        DateTimeOffset HostTimestampUtc,
        ulong? RunId,
        int? Trial,
        string RecordType,
        int? Channel,
        string Device,
        long? AnalyzerTimestampUs,
        string EdgeMask,
        int? EdgeCount,
        string Raw)
    {
        public static AnalyzerRecord CreateRaw(DateTimeOffset timestamp, string recordType, string raw) =>
            new(timestamp, null, null, recordType, null, string.Empty, null, string.Empty, null, raw);

        public static AnalyzerRecord ForTrial(
            DateTimeOffset timestamp,
            ulong runId,
            int trial,
            string recordType,
            string raw) =>
            new(timestamp, runId, trial, recordType, null, string.Empty, null, string.Empty, null, raw);

        public string ToCsv() => string.Join(
            ",",
            Csv(HostTimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
            RunId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Trial?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Csv(RecordType),
            Channel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Csv(Device),
            AnalyzerTimestampUs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Csv(EdgeMask),
            EdgeCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Csv(Raw));

        private static string Csv(string value)
        {
            if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            {
                return value;
            }
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}
