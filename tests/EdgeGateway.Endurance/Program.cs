using System.Diagnostics;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;

// Synthetic pipeline endurance, not real PLC, physical-network or database acceptance.
var seconds = 45;
var output = Path.GetFullPath(Path.Combine("artifacts", "endurance"));
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--seconds" && ++index < args.Length && int.TryParse(args[index], out var parsed)) seconds = parsed;
    else if (args[index] == "--output" && ++index < args.Length) output = Path.GetFullPath(args[index]);
    else throw new ArgumentException("Usage: --seconds 30..86400 --output <dedicated evidence directory>");
}
if (seconds is < 30 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
Directory.CreateDirectory(output);
var configuration = new GatewayConfiguration
{
    Logging = new() { MaxFileSizeMb = 1, RetainedFileCount = 3, MinimumFreeDiskSpaceMb = 16, DiskResumeMarginMb = 4 },
    Devices = [new() { Name = "synthetic-endurance-device", DriverId = "simulator",
        Tags = Enumerable.Range(0, 12).Select(index => new TagConfiguration { Name = "point-" + index, Address = index.ToString() }).ToList() }]
};
var slow = new DatabaseConnectionConfiguration { Name = "fault-injected-sink", Enabled = true, BatchSize = 20, FlushIntervalMs = 100,
    QueueCapacityRows = 200, QueueCapacityMb = 1, MaxRetryAttempts = 2 };
var fast = new DatabaseConnectionConfiguration { Name = "healthy-sink", Enabled = true, BatchSize = 20, FlushIntervalMs = 100,
    QueueCapacityRows = 1000, QueueCapacityMb = 2, MaxRetryAttempts = 2 };
configuration.Databases = [slow, fast];
configuration.DatabaseWriteGroups = [new() { Name = "slow-group", ConnectionId = slow.Id, Enabled = true }, new() { Name = "fast-group", ConnectionId = fast.Id, Enabled = true }];
await using var log = new GatewayLog(Path.Combine(output, "logs"));
var connector = new FaultConnector(slow.Id);
await using var sink = new AcquisitionDataService(connector, log);
sink.UpdateConfiguration(configuration); await sink.StartAsync();
var device = configuration.Devices[0]; sink.SetDeviceActive(device.Id, true);
var timer = Stopwatch.StartNew(); long submitted = 0; var snapshots = new List<object>();
var memoryBefore = GC.GetTotalMemory(false); long peakHeap = memoryBefore; var nextObservation = TimeSpan.Zero;
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try
{
    using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
    while (timer.Elapsed < TimeSpan.FromSeconds(seconds) && await tick.WaitForNextTickAsync(shutdown.Token))
    {
        var now = DateTimeOffset.UtcNow;
        var samples = device.Tags.Select(tag => new TelemetrySample(device.Id, device.Name, tag.Id, tag.Name, tag.Address,
            submitted, submitted, SampleQuality.Good, now, "count")).ToArray();
        await sink.AppendAsync(samples, shutdown.Token); submitted += samples.Length;
        foreach (var status in sink.GetStatuses())
        {
            var connection = configuration.Databases.Single(item => item.Id == status.ConnectionId);
            if (status.QueuedRows < 0 || status.QueuedRows > connection.QueueCapacityRows || status.QueuedBytes < 0
                || status.QueuedBytes > connection.QueueCapacityMb * 1048576L || status.PeakQueuedRows > connection.QueueCapacityRows)
                throw new InvalidOperationException("Queue quota or accounting invariant violated: " + status.Name);
        }
        peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
        if (timer.Elapsed >= nextObservation)
        {
            // Bounded evidence even during a 24-hour run: at most one snapshot per minute after the first minute.
            snapshots.Add(new { elapsedSeconds = timer.Elapsed.TotalSeconds, submitted, statuses = sink.GetStatuses(), heapBytes = GC.GetTotalMemory(false) });
            nextObservation = timer.Elapsed + (timer.Elapsed < TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1));
        }
    }
}
finally { await sink.StopAsync(); await log.FlushAsync(); }
var final = sink.GetStatuses();
foreach (var status in final)
    if (status.QueuedRows != 0 || status.QueuedBytes != 0 || status.InFlightBatches != 0 || status.WrittenRows + status.DroppedRows != submitted)
        throw new InvalidOperationException("Final per-sink row accounting did not balance: " + status.Name);
var healthy = final.Single(status => status.ConnectionId == fast.Id);
var faulted = final.Single(status => status.ConnectionId == slow.Id);
if (healthy.DroppedRows != 0 || healthy.WrittenRows != submitted || faulted.FailedBatches == 0 || faulted.WrittenRows == 0
    || faulted.RetriedBatches == 0 || faulted.DroppedRows == 0)
    throw new InvalidOperationException("Healthy isolation, failure injection, recovery or backpressure was not exercised.");
var report = new
{
    source = "synthetic samples and deterministic fault-injected connector; not a real PLC/database test",
    requestedSeconds = seconds, elapsedSeconds = timer.Elapsed.TotalSeconds, submittedPerSink = submitted,
    memoryBefore, memoryAfter = GC.GetTotalMemory(false), peakObservedManagedHeapBytes = peakHeap,
    note = "Observed heap is diagnostic only, not a proof of absence of leaks; volatile queues may drop records by explicit policy.",
    passed = true, final, logging = log.GetStatus(), snapshots
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), json);
Console.WriteLine(json);

sealed class FaultConnector(Guid slowId) : IExternalDatabaseConnector
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public async Task WriteRowsAsync(DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group,
        IReadOnlyList<DatabaseWriteRow> rows, CancellationToken cancellationToken = default)
    {
        if (connection.Id == slowId)
        {
            // Repeating simulated outages with slow recovery; never blocks the healthy sink worker.
            var phase = _clock.Elapsed.TotalSeconds % 30;
            if (phase is >= 8 and < 16) throw new DatabaseWriteException("injected connection outage", true);
            await Task.Delay(300, cancellationToken);
        }
        else await Task.Delay(2, cancellationToken);
    }
    public Task<DatabaseConnectionTestResult> TestConnectionAsync(DatabaseConnectionConfiguration c, CancellationToken token = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(DatabaseConnectionConfiguration c, CancellationToken token = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(DatabaseConnectionConfiguration c, DatabaseTableInfo table, CancellationToken token = default) => throw new NotSupportedException();
    public Task EnsurePointTableAsync(DatabaseConnectionConfiguration c, DatabaseTableInfo table, CancellationToken token = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<TelemetrySample>> QueryPointHistoryAsync(DatabaseConnectionConfiguration c, DatabaseWriteGroupConfiguration group, HistoryQuery query, CancellationToken token = default) => throw new NotSupportedException();
}
