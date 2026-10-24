using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class RecordingHardeningTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edge-hardening-" + Guid.NewGuid().ToString("N"));
    private static GatewayLogQuery All => new(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), GatewayLogLevel.Debug);

    [Fact]
    public async Task ExpiredLogsAreReclaimedBeforeLowDiskSpaceDecision()
    {
        Directory.CreateDirectory(_directory);
        var expired = Path.Combine(_directory, "gateway-20000101.log");
        await File.WriteAllTextAsync(expired, "old");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-20));
        // Available capacity models space occupied by the expired gateway file.
        await using var log = new GatewayLog(_directory, availableDiskBytes: _ => (File.Exists(expired) ? 1L : 100L) * 1048576);
        log.ApplyConfiguration(new() { MinimumFreeDiskSpaceMb = 16, DiskResumeMarginMb = 4, RetainedDays = 7 });
        log.Write(GatewayLogLevel.Warning, "retention before protection");
        await log.FlushAsync();
        Assert.False(File.Exists(expired));
        Assert.False(log.GetStatus().DiskProtectionActive);
        Assert.Equal(0, log.DroppedEntries);
        Assert.Equal("retention before protection", Assert.Single((await log.QueryAsync(All)).Entries).Message);
    }

    [Fact]
    public async Task HotRetentionAppliesWithoutProducingAnEventAndLeavesOtherFilesAlone()
    {
        Directory.CreateDirectory(_directory);
        var expired = Path.Combine(_directory, "gateway-20000101.log");
        var other = Path.Combine(_directory, "business.log");
        await File.WriteAllTextAsync(expired, "old"); await File.WriteAllTextAsync(other, "keep");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-20));
        await using var log = new GatewayLog(_directory, availableDiskBytes: _ => 100L * 1048576);
        log.ApplyConfiguration(new() { RetainedDays = 7, MinimumFreeDiskSpaceMb = 16 });
        await log.FlushAsync();
        Assert.False(File.Exists(expired)); Assert.Equal("keep", await File.ReadAllTextAsync(other));
        Assert.Empty(Directory.GetFiles(_directory, "gateway-*.log"));
        Assert.Equal(0, log.PendingEntries); Assert.Equal(0, log.PendingBytes);
    }

    [Fact]
    public async Task InvalidUtf8IsNotSilentlyChangedIntoAValidLogRecord()
    {
        Directory.CreateDirectory(_directory);
        var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, level = "Information", kind = "event", message = "badX" }) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line); bytes[Array.IndexOf(bytes, (byte)'X')] = 0xFF;
        await File.WriteAllBytesAsync(Path.Combine(_directory, "gateway-invalid.log"), bytes);
        await using var log = new GatewayLog(_directory);
        var result = await log.QueryAsync(All);
        Assert.Empty(result.Entries); Assert.True(result.IsPartial); Assert.Equal(1, result.InvalidLines);
    }

    [Fact]
    public async Task ProviderTimeoutIsCountedEvenWhenTheOperationAlreadyCompleted()
    {
        var (config, connection) = Configuration(); connection.MaxRetryAttempts = 2;
        var attempts = 0;
        var connector = new Connector(_ => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException(new TimeoutException("controlled timeout")) : Task.CompletedTask);
        await using var log = new GatewayLog();
        await using var sink = new AcquisitionDataService(connector, log);
        sink.UpdateConfiguration(config); await sink.StartAsync();
        await sink.AppendAsync([Sample(config)]); await sink.StopAsync();
        var status = Assert.Single(sink.GetStatuses());
        Assert.Equal(1, status.TimedOutOperations); Assert.Equal(1, status.RetriedBatches);
        Assert.Equal(1, status.FailedBatches); Assert.Equal(1, status.WrittenRows);
        Assert.Equal(0, status.QueuedRows); Assert.Equal(0, status.QueuedBytes); Assert.Equal(0, status.InFlightBatches);
    }

    [Fact]
    public async Task PermanentFailureCoolsDownNewBatchesInsteadOfHammeringTheDatabase()
    {
        var (config, _) = Configuration(); var attempts = 0;
        var connector = new Connector(_ => { Interlocked.Increment(ref attempts); return Task.FromException(new DatabaseWriteException("missing table", false)); });
        await using var log = new GatewayLog();
        await using var sink = new AcquisitionDataService(connector, log);
        sink.UpdateConfiguration(config); await sink.StartAsync();
        await sink.AppendAsync([Sample(config)]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Assert.Single(sink.GetStatuses()).DroppedRows == 0) await Task.Delay(10, deadline.Token);
        await sink.AppendAsync([Sample(config)]);
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Equal(1, Assert.Single(sink.GetStatuses()).QueuedRows);
        await sink.StopAsync();
        var status = Assert.Single(sink.GetStatuses());
        Assert.Equal(2, status.WrittenRows + status.DroppedRows);
        Assert.Equal(0, status.QueuedRows); Assert.Equal(0, status.QueuedBytes);
        Assert.Equal(0, status.RetriedBatches);
    }

    private static (GatewayConfiguration, DatabaseConnectionConfiguration) Configuration()
    {
        var connection = new DatabaseConnectionConfiguration { Enabled = true, BatchSize = 1, FlushIntervalMs = 50, QueueCapacityRows = 100 };
        var config = new GatewayConfiguration
        {
            Devices = [new() { DriverId = "simulator", Tags = [new() { Name = "counter" }] }],
            Databases = [connection], DatabaseWriteGroups = [new() { Enabled = true, ConnectionId = connection.Id }]
        };
        return (config, connection);
    }
    private static TelemetrySample Sample(GatewayConfiguration config)
    {
        var device = config.Devices[0]; var tag = device.Tags[0];
        return new(device.Id, device.Name, tag.Id, tag.Name, "0", 7, 7, SampleQuality.Good, DateTimeOffset.UtcNow, "");
    }
    private sealed class Connector(Func<CancellationToken, Task> write) : IExternalDatabaseConnector
    {
        public Task WriteRowsAsync(DatabaseConnectionConfiguration c, DatabaseWriteGroupConfiguration g, IReadOnlyList<DatabaseWriteRow> rows, CancellationToken cancellationToken = default) => write(cancellationToken);
        public Task<DatabaseConnectionTestResult> TestConnectionAsync(DatabaseConnectionConfiguration c, CancellationToken t = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(DatabaseConnectionConfiguration c, CancellationToken t = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(DatabaseConnectionConfiguration c, DatabaseTableInfo table, CancellationToken t = default) => throw new NotSupportedException();
        public Task EnsurePointTableAsync(DatabaseConnectionConfiguration c, DatabaseTableInfo table, CancellationToken t = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TelemetrySample>> QueryPointHistoryAsync(DatabaseConnectionConfiguration c, DatabaseWriteGroupConfiguration g, HistoryQuery q, CancellationToken t = default) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
