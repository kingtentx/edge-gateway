using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class RecordingDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edge-phase2-" + Guid.NewGuid().ToString("N"));
    private string Logs => Path.Combine(_root, "logs");
    private static GatewayLogQuery Query(string text = "", bool samples = true, int limit = 500, int budget = 32) =>
        new(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), GatewayLogLevel.Information, text, samples, limit, budget);

    [Fact]
    public async Task DiskProtectionHasHysteresisAndDoesNotReplayRejectedEntries()
    {
        long free = 15L * 1024 * 1024;
        var clock = new Clock();
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => free, timeProvider: clock);
        log.ApplyConfiguration(new() { MinimumFreeDiskSpaceMb = 16, DiskResumeMarginMb = 4 });
        log.Write(GatewayLogLevel.Warning, "被保护丢弃一"); await log.FlushAsync();
        Assert.True(log.GetStatus().DiskProtectionActive);
        Assert.Equal(1, log.GetStatus().DiskRejectedEntries);
        Assert.Contains(log.GetEntries(), item => item.Message == "被保护丢弃一");
        free = 18L * 1024 * 1024; clock.Advance();
        log.Write(GatewayLogLevel.Warning, "恢复余量不足"); await log.FlushAsync();
        Assert.True(log.GetStatus().DiskProtectionActive);
        free = 24L * 1024 * 1024; clock.Advance();
        log.Write(GatewayLogLevel.Warning, "恢复后正常"); await log.FlushAsync();
        Assert.False(log.GetStatus().DiskProtectionActive);
        Assert.Equal(2, log.GetStatus().DiskRejectedEntries);
        Assert.Equal(0, log.PendingEntries); Assert.Equal(0, log.PendingBytes);
        var result = await log.QueryAsync(Query());
        Assert.Equal("恢复后正常", Assert.Single(result.Entries).Message);
    }

    [Fact]
    public async Task UnreadableDiskCapacityFailsClosedAndHotApplyRechecksCapacity()
    {
        var fail = true;
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => fail ? throw new IOException("offline") : 100L * 1024 * 1024);
        log.ApplyConfiguration(new() { MinimumFreeDiskSpaceMb = 16, DiskResumeMarginMb = 1 });
        log.Write(GatewayLogLevel.Error, "capacity unavailable"); await log.FlushAsync();
        Assert.True(log.GetStatus().DiskProtectionActive); Assert.Null(log.GetStatus().AvailableDiskBytes);
        fail = false;
        var policy = new GatewayLoggingConfiguration { MinimumFreeDiskSpaceMb = 16, DiskResumeMarginMb = 1 };
        log.ApplyConfiguration(policy); policy.MinimumFreeDiskSpaceMb = 100000;
        log.Write(GatewayLogLevel.Error, "capacity recovered"); await log.FlushAsync();
        Assert.False(log.GetStatus().DiskProtectionActive); Assert.Null(log.FileError);
        Assert.Single((await log.QueryAsync(Query())).Entries);
    }

    [Theory]
    [InlineData(0, 64)] [InlineData(16, 0)] [InlineData(1048577, 1)] [InlineData(16, 65537)]
    public async Task InvalidDiskPoliciesAreRejected(int reserve, int margin)
    {
        await using var log = new GatewayLog();
        Assert.Throws<ArgumentException>(() => log.ApplyConfiguration(new() { MinimumFreeDiskSpaceMb = reserve, DiskResumeMarginMb = margin }));
    }

    [Fact]
    public async Task ArchiveFiltersPreserveUnicodeAndExportTheExecutedSnapshot()
    {
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => long.MaxValue);
        log.Write(GatewayLogLevel.Information, "温度正常"); log.Write(GatewayLogLevel.Error, "温度异常");
        log.WriteSample(new(Guid.NewGuid(), "设备", Guid.NewGuid(), "温度", "0", 5, 5, SampleQuality.Good, DateTimeOffset.UtcNow, "℃"));
        await log.FlushAsync();
        var result = await log.QueryAsync(Query("温度", false) with { MinimumLevel = GatewayLogLevel.Error });
        Assert.Equal("温度异常", Assert.Single(result.Entries).Message);
        log.Write(GatewayLogLevel.Error, "温度异常后续"); await log.FlushAsync();
        var destination = Path.Combine(_root, "export.jsonl");
        await log.ExportAsync(result, destination);
        var lines = await File.ReadAllLinesAsync(destination);
        Assert.Equal(2, lines.Length);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal("export", header.RootElement.GetProperty("kind").GetString());
        Assert.Equal(result.Entries[0].Json, lines[1]);
        Assert.Equal(4, (await log.QueryAsync(Query())).Entries.Count);
    }

    [Fact]
    public async Task ArchiveReportsInvalidLinesIncompleteTailAndRowLimits()
    {
        Directory.CreateDirectory(Logs);
        var now = DateTimeOffset.UtcNow.ToString("O");
        string Line(string message) => JsonSerializer.Serialize(new { timestamp = now, level = "Information", kind = "event", message }) + "\n";
        await File.WriteAllTextAsync(Path.Combine(Logs, "gateway-fixture.log"), Line("first") + "broken\n" + Line("latest") + "{unfinished");
        await using var log = new GatewayLog(Logs);
        var result = await log.QueryAsync(Query());
        Assert.True(result.IsPartial); Assert.Equal(1, result.InvalidLines); Assert.Equal(2, result.Entries.Count);
        var limited = await log.QueryAsync(Query(limit: 1));
        Assert.True(limited.IsPartial); Assert.Equal("latest", Assert.Single(limited.Entries).Message);
    }

    [Fact]
    public async Task CancelledQueriesAndExportsDoNotReplaceExistingFilesOrWriteRuntimeLogs()
    {
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => long.MaxValue);
        log.Write(GatewayLogLevel.Error, "keep"); await log.FlushAsync();
        var result = await log.QueryAsync(Query());
        var target = Path.Combine(_root, "existing.jsonl"); await File.WriteAllTextAsync(target, "original");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.QueryAsync(Query(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.ExportAsync(result, target, cancelled.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        await Assert.ThrowsAsync<ArgumentException>(() => log.ExportAsync(result, Path.Combine(Logs, "overwrite.jsonl")));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void PreviewUsesExactRuntimeConversionsAndFlagsStoppedStaleOrBadSamples()
    {
        var now = DateTimeOffset.UtcNow; var tag = Guid.NewGuid(); var device = Guid.NewGuid();
        var sample = new TelemetrySample(device, "设备", tag, "计数", "0", ulong.MaxValue, ulong.MaxValue, SampleQuality.Good, now, "");
        var connection = new DatabaseConnectionConfiguration();
        var group = new DatabaseWriteGroupConfiguration { ConnectionId = connection.Id, Mode = DatabaseStorageMode.ColumnMapping,
            Columns = [new() { Column = "counter", TagId = tag, Source = DatabaseValueSource.RawValue, FieldType = DatabaseFieldType.Decimal }] };
        DatabaseWritePreview Inspect(TelemetrySample current, bool active = true) => DatabaseWriteInspector.Inspect(connection, group,
            [new("counter", "decimal(20,0)", false)], new Dictionary<Guid, TelemetrySample> { [tag] = current }, new HashSet<Guid> { tag },
            active ? new HashSet<Guid> { device } : new HashSet<Guid>(), now);
        var preview = Inspect(sample);
        Assert.True(preview.SchemaValid); Assert.True(preview.RowReady);
        Assert.Equal("18446744073709551615", Assert.Single(preview.Columns).Value);
        Assert.False(Inspect(sample, false).RowReady);
        Assert.False(Inspect(sample with { Timestamp = now.AddHours(-1) }).RowReady);
        Assert.False(Inspect(sample with { Timestamp = now.AddMinutes(1) }).RowReady);
        Assert.False(Inspect(sample with { Quality = SampleQuality.Bad }).RowReady);
        group.Columns[0].FieldType = DatabaseFieldType.Int64;
        Assert.False(Inspect(sample).RowReady);
    }

    [Fact]
    public void PreviewDetectsMissingGeneratedRequiredAndOracleEmptyStringFields()
    {
        var now = DateTimeOffset.UtcNow; var tag = Guid.NewGuid(); var device = Guid.NewGuid();
        var connection = new DatabaseConnectionConfiguration { Provider = ExternalDatabaseProvider.Oracle };
        var group = new DatabaseWriteGroupConfiguration { Mode = DatabaseStorageMode.ColumnMapping,
            Columns = [new() { Column = "note", TagId = tag, Source = DatabaseValueSource.EngineeringValue }] };
        var samples = new Dictionary<Guid, TelemetrySample> { [tag] = new(device, "d", tag, "t", "0", "", "", SampleQuality.Good, now, "") };
        DatabaseWritePreview Inspect(params DatabaseColumnInfo[] metadata) => DatabaseWriteInspector.Inspect(connection, group, metadata,
            samples, new HashSet<Guid> { tag }, new HashSet<Guid> { device }, now);
        Assert.False(Inspect(new DatabaseColumnInfo("NOTE", "VARCHAR2", true)).SchemaValid);
        Assert.False(Inspect(new DatabaseColumnInfo("note", "VARCHAR2", true, IsGenerated: true)).SchemaValid);
        Assert.False(Inspect(new DatabaseColumnInfo("note", "VARCHAR2", true), new("required", "NUMBER", false)).SchemaValid);
        var empty = Inspect(new DatabaseColumnInfo("note", "VARCHAR2", false));
        Assert.True(empty.SchemaValid); Assert.False(empty.RowReady); Assert.Contains(empty.Errors, text => text.Contains("Oracle"));
        Assert.True(Inspect(new DatabaseColumnInfo("note", "VARCHAR2", true), new("generated", "NUMBER", false, IsGenerated: true)).RowReady);
    }

    [Theory]
    [InlineData(3113, true)] [InlineData(12541, true)] [InlineData(60, true)]
    [InlineData(1017, false)] [InlineData(942, false)] [InlineData(1400, false)] [InlineData(1, false)]
    public void OracleTransientClassificationDoesNotRetrySchemaCredentialsOrConstraints(int code, bool expected) =>
        Assert.Equal(expected, ExternalDatabaseConnector.IsTransientOracleCode(code));

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond * 2);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
