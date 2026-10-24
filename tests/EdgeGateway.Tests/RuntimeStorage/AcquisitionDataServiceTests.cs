using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class AcquisitionDataServiceTests
{
    [Fact]
    public async Task DefaultOutputIsSampleLoggingWithoutAnyDatabaseWrites()
    {
        var configuration = Configuration();
        var connector = new Connector();
        var log = new SampleLog();
        await using var sink = new AcquisitionDataService(connector, log);
        sink.UpdateConfiguration(configuration);
        await sink.StartAsync();
        sink.SetDeviceActive(configuration.Devices[0].Id, true);
        await sink.AppendAsync([Sample(configuration, 0, 7L), Sample(configuration, 1, null, SampleQuality.Bad)]);
        await sink.StopAsync();
        Assert.Equal(2, log.Samples.Count);
        Assert.Empty(connector.Writes);
        Assert.Empty(sink.GetStatuses());

        configuration.Logging.LogAcquisitionSamples = false;
        sink.UpdateConfiguration(configuration);
        await sink.StartAsync();
        await sink.AppendAsync([Sample(configuration, 0, 8L)]);
        Assert.Equal(2, log.Samples.Count);
    }

    [Fact]
    public async Task PointRowsKeepExactValuesUtcAndQualityIndependentOfMqttUploadSwitch()
    {
        var configuration = Configuration();
        configuration.Devices[0].Tags[0].UploadEnabled = false;
        var connection = AddConnection(configuration);
        var group = AddPointGroup(configuration, connection);
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        var wideInteger = ulong.MaxValue;
        var timestamp = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        var first = Sample(configuration, 0, wideInteger) with { Timestamp = timestamp };
        var second = Sample(configuration, 1, null, SampleQuality.Bad) with { Error = "PLC timeout" };
        await sink.AppendAsync([first, second]);
        await sink.StopAsync();
        var rows = connector.Writes.SelectMany(write => write.Rows).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(connector.Writes, write => Assert.Equal(group.Id, write.GroupId));
        var row = rows.Single(item => (string)item.Values["tag_id"]! == first.TagId.ToString("D"));
        Assert.Equal("18446744073709551615", row.Values["value_text"]);
        Assert.Equal("18446744073709551615", row.Values["raw_value"]);
        var storedTime = Assert.IsType<DateTimeOffset>(row.Values["sample_time"]);
        Assert.Equal(TimeSpan.Zero, storedTime.Offset);
        Assert.Equal(timestamp, storedTime);
        Assert.Equal(row.EventId, row.Values["event_id"]);
        var bad = rows.Single(item => (string)item.Values["tag_id"]! == second.TagId.ToString("D"));
        Assert.Equal("Bad", bad.Values["quality"]);
        Assert.Null(bad.Values["value_text"]);
        Assert.Equal("PLC timeout", bad.Values["error"]);
        Assert.Equal(2, Assert.Single(sink.GetStatuses()).WrittenRows);
    }

    [Fact]
    public async Task CustomMappingWaitsForCompleteGoodSnapshotAndPreservesLargeInteger()
    {
        var configuration = Configuration();
        var connection = AddConnection(configuration);
        var group = AddMappedGroup(configuration, connection);
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, ulong.MaxValue)]);
        await Task.Delay(120);
        Assert.Empty(connector.Writes);
        await sink.AppendAsync([Sample(configuration, 1, 12.5m)]);
        await Until(() => connector.Writes.Count == 1);
        var row = Assert.Single(Assert.Single(connector.Writes).Rows);
        Assert.Equal(18446744073709551615m, row.Values["counter"]);
        Assert.Equal(12.5m, row.Values["temperature"]);
        Assert.Equal(row.EventId, row.Values["event_key"]);
        Assert.Equal("A01", row.Values["line_code"]);
        Assert.IsType<DateTimeOffset>(row.Values["recorded_at"]);
        await sink.AppendAsync([Sample(configuration, 0, null, SampleQuality.Bad)]);
        await Task.Delay(160);
        Assert.Single(connector.Writes);
        Assert.True(Assert.Single(sink.GetStatuses()).SkippedRows > 0);
        Assert.Equal(DatabaseStorageMode.ColumnMapping, group.Mode);
    }

    [Fact]
    public async Task StoppedRestartedAndStalePointsCannotGenerateNewGoodRows()
    {
        var configuration = Configuration();
        var connection = AddConnection(configuration);
        var group = AddMappedGroup(configuration, connection);
        group.MaxSampleAgeMs = 500;
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        var initial = Sample(configuration, 0, 10L);
        await sink.AppendAsync([initial]);
        await Task.Delay(650);
        await sink.AppendAsync([Sample(configuration, 1, 11L)]);
        await Task.Delay(120);
        Assert.Empty(connector.Writes);
        await sink.AppendAsync([Sample(configuration, 0, 12L), Sample(configuration, 1, 13L)]);
        await Until(() => connector.Writes.Count == 1);
        sink.SetDeviceActive(configuration.Devices[0].Id, false);
        await sink.AppendAsync([Sample(configuration, 0, 20L), Sample(configuration, 1, 21L)]);
        await Task.Delay(140);
        Assert.Single(connector.Writes);
        sink.SetDeviceActive(configuration.Devices[0].Id, true);
        // A batch from the old generation can still be in the runtime output channel.
        await sink.AppendAsync([initial, initial with { TagId = configuration.Devices[0].Tags[1].Id }]);
        await Task.Delay(140);
        Assert.Single(connector.Writes);
        await sink.AppendAsync([Sample(configuration, 0, 30L), Sample(configuration, 1, 31L)]);
        await Until(() => connector.Writes.Count == 2);
    }

    [Fact]
    public async Task ABlockedDatabaseDoesNotBlockOtherDestinationsAndFullQueueRejectsNewRows()
    {
        var configuration = Configuration();
        var slow = AddConnection(configuration, "slow", capacity: 2);
        var healthy = AddConnection(configuration, "healthy");
        AddPointGroup(configuration, slow, "slow group");
        AddPointGroup(configuration, healthy, "healthy group");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Connector
        {
            OnWrite = async (connection, _, _, token) =>
            {
                if (connection.Id != slow.Id) return;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, 1)]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        for (var i = 2; i <= 8; i++) await sink.AppendAsync([Sample(configuration, 0, i)]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "Appending must not await a blocked database.");
        await Until(() => sink.GetStatuses().Single(state => state.ConnectionId == healthy.Id).WrittenRows == 8);
        var status = sink.GetStatuses().Single(state => state.ConnectionId == slow.Id);
        Assert.InRange(status.QueuedRows, 1, 2);
        Assert.Equal(6, status.DroppedRows);
        release.TrySetResult();
        await sink.StopAsync();
        Assert.Equal(2, sink.GetStatuses().Single(state => state.ConnectionId == slow.Id).WrittenRows);
    }

    [Fact]
    public async Task RetryUsesSameEventIdAndCountsExhaustedBatchesExplicitly()
    {
        var configuration = Configuration();
        var connection = AddConnection(configuration);
        connection.MaxRetryAttempts = 2;
        AddPointGroup(configuration, connection);
        var attempts = new ConcurrentQueue<string>();
        var connector = new Connector
        {
            OnWrite = (_, _, rows, _) =>
            {
                attempts.Enqueue(rows[0].EventId);
                throw new IOException("test outage");
            }
        };
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, 1L)]);
        await Until(() => Assert.Single(sink.GetStatuses()).DroppedRows == 1 && Assert.Single(sink.GetStatuses()).QueuedRows == 0);
        var status = Assert.Single(sink.GetStatuses());
        Assert.Equal(2, status.FailedBatches);
        Assert.Equal(0, status.WrittenRows);
        Assert.Equal(0, status.QueuedRows);
        Assert.Equal(2, attempts.Count);
        Assert.Single(attempts.Distinct());
    }

    [Fact]
    public async Task ByteBudgetAndOversizedRowsAreCountedWithoutBlockingAcquisition()
    {
        var configuration = Configuration();
        var connection = AddConnection(configuration);
        connection.QueueCapacityMb = 1;
        AddPointGroup(configuration, connection);
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, new string('x', 300000))]);
        Assert.Equal(1, Assert.Single(sink.GetStatuses()).DroppedRows);
        Assert.Empty(connector.Writes);
        await sink.AppendAsync([Sample(configuration, 0, "small")]);
        await sink.StopAsync();
        Assert.Equal(1, Assert.Single(sink.GetStatuses()).WrittenRows);
    }

    [Fact]
    public async Task IntegerMappingsRejectFractionalValuesInsteadOfRounding()
    {
        var configuration = Configuration();
        var connection = AddConnection(configuration);
        var group = AddMappedGroup(configuration, connection);
        group.Columns[0].FieldType = DatabaseFieldType.Int64;
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, 1.5m), Sample(configuration, 1, 2m)]);
        await Until(() => Assert.Single(sink.GetStatuses()).SkippedRows > 0);
        Assert.Empty(connector.Writes);
        Assert.Contains("整数", Assert.Single(sink.GetStatuses()).Error);
    }

    [Fact]
    public void ValidationRejectsBadCredentialsIdentifiersMissingTagsAndUnboundedSettings()
    {
        var configuration = Configuration();
        var connection = AddConnection(configuration);
        var group = AddMappedGroup(configuration, connection);
        connection.PasswordEnvironmentVariable = "actual password;not a variable";
        connection.QueueCapacityRows = 0;
        connection.QueueCapacityMb = 0;
        connection.MaxRetryAttempts = 100;
        group.Table = "table; DROP TABLE other";
        group.Columns[0].TagId = Guid.NewGuid();
        group.Columns[1].Column = group.Columns[0].Column;
        configuration.Logging.MaxFileSizeMb = 0;
        var errors = DataRecordingConfigurationValidator.Validate(configuration);
        Assert.Contains(errors, error => error.Contains("环境变量"));
        Assert.Contains(errors, error => error.Contains("SQL 表达式"));
        Assert.Contains(errors, error => error.Contains("关联现有点位"));
        Assert.Contains(errors, error => error.Contains("重复的字段"));
        Assert.Contains(errors, error => error.Contains("内存限额"));
        Assert.Contains(errors, error => error.Contains("最大尝试"));
        Assert.Contains(errors, error => error.Contains("日志文件大小"));
    }

    [Theory]
    [InlineData("1e-29")]
    [InlineData("1.123456789012345678901234567891")]
    public void DecimalConstantsCannotSilentlyUnderflowOrRound(string constant)
    {
        var configuration = Configuration();
        var group = AddMappedGroup(configuration, AddConnection(configuration));
        group.Columns[0].Source = DatabaseValueSource.Constant;
        group.Columns[0].ConstantValue = constant;
        var errors = DataRecordingConfigurationValidator.Validate(configuration);
        Assert.Contains(errors, error => error.Contains("精度"));
    }

    [Fact]
    public async Task SuccessfulGroupDoesNotHideAnotherGroupsFailureOnTheSameConnection()
    {
        var configuration = Configuration();
        var connection = AddConnection(configuration);
        connection.MaxRetryAttempts = 1;
        var failing = AddPointGroup(configuration, connection, "failing table");
        AddPointGroup(configuration, connection, "healthy table");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Connector
        {
            OnWrite = async (_, group, _, token) =>
            {
                if (group.Id == failing.Id) { failed.TrySetResult(); throw new IOException("missing table"); }
                await failed.Task.WaitAsync(token);
                await Task.Delay(30, token);
            }
        };
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, 1L)]);
        await Until(() => Assert.Single(sink.GetStatuses()).WrittenRows == 1);
        var status = Assert.Single(sink.GetStatuses());
        Assert.Equal(1, status.DroppedRows);
        Assert.Contains("failing table", status.Error);
        Assert.False(status.IsConnected);
    }

    [Fact]
    public async Task UncooperativeDatabaseIsIsolatedAndLateSuccessCannotReactivateStoppedStatus()
    {
        var configuration = Configuration();
        AddPointGroup(configuration, AddConnection(configuration));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Connector
        {
            OnWrite = (_, _, _, _) => { entered.TrySetResult(); return release.Task; }
        };
        await using var sink = await Start(configuration, connector);
        try
        {
            await sink.AppendAsync([Sample(configuration, 0, 1L)]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await sink.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert.False(Assert.Single(sink.GetStatuses()).IsConnected);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sink.StartAsync());
            release.TrySetResult();
            await Until(() => Assert.Single(sink.GetStatuses()).WrittenRows == 1);
            Assert.False(Assert.Single(sink.GetStatuses()).IsConnected);
        }
        finally { release.TrySetResult(); }
    }

    private static GatewayConfiguration Configuration() => new()
    {
        Devices = [new DeviceConfiguration { Name = "Test", DriverId = "simulator", Tags = [new() { Name = "counter" }, new() { Name = "temperature" }] }]
    };
    private static DatabaseConnectionConfiguration AddConnection(GatewayConfiguration configuration, string name = "database", int capacity = 100)
    {
        var connection = new DatabaseConnectionConfiguration { Name = name, Enabled = true, BatchSize = 1, FlushIntervalMs = 50, QueueCapacityRows = capacity };
        configuration.Databases.Add(connection); return connection;
    }
    private static DatabaseWriteGroupConfiguration AddPointGroup(GatewayConfiguration configuration, DatabaseConnectionConfiguration connection, string name = "points")
    {
        var group = new DatabaseWriteGroupConfiguration { Name = name, Enabled = true, ConnectionId = connection.Id };
        configuration.DatabaseWriteGroups.Add(group); return group;
    }
    private static DatabaseWriteGroupConfiguration AddMappedGroup(GatewayConfiguration configuration, DatabaseConnectionConfiguration connection)
    {
        var group = new DatabaseWriteGroupConfiguration
        {
            Name = "mapped", Enabled = true, ConnectionId = connection.Id, Mode = DatabaseStorageMode.ColumnMapping,
            Table = "production", WriteIntervalMs = 100, MaxSampleAgeMs = 5000,
            Columns =
            [
                new() { Column = "counter", TagId = configuration.Devices[0].Tags[0].Id, FieldType = DatabaseFieldType.Decimal },
                new() { Column = "temperature", TagId = configuration.Devices[0].Tags[1].Id, FieldType = DatabaseFieldType.Decimal },
                new() { Column = "recorded_at", Source = DatabaseValueSource.RecordTime },
                new() { Column = "event_key", Source = DatabaseValueSource.EventId },
                new() { Column = "line_code", Source = DatabaseValueSource.Constant, ConstantValue = "A01" }
            ]
        };
        configuration.DatabaseWriteGroups.Add(group); return group;
    }
    private static TelemetrySample Sample(GatewayConfiguration configuration, int index, object? value, SampleQuality quality = SampleQuality.Good)
    {
        var device = configuration.Devices[0]; var tag = device.Tags[index];
        return new(device.Id, device.Name, tag.Id, tag.Name, tag.Address, value, value, quality, DateTimeOffset.UtcNow, "");
    }
    private static async Task<AcquisitionDataService> Start(GatewayConfiguration configuration, Connector connector)
    {
        var sink = new AcquisitionDataService(connector, new SampleLog());
        sink.UpdateConfiguration(configuration);
        await sink.StartAsync();
        foreach (var device in configuration.Devices) sink.SetDeviceActive(device.Id, true);
        return sink;
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed record Written(Guid ConnectionId, Guid GroupId, IReadOnlyList<DatabaseWriteRow> Rows);
    private sealed class Connector : IExternalDatabaseConnector
    {
        public ConcurrentQueue<Written> Writes { get; } = new();
        public Func<DatabaseConnectionConfiguration, DatabaseWriteGroupConfiguration, IReadOnlyList<DatabaseWriteRow>, CancellationToken, Task>? OnWrite;
        public async Task WriteRowsAsync(DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group, IReadOnlyList<DatabaseWriteRow> rows, CancellationToken cancellationToken = default)
        {
            if (OnWrite is not null) await OnWrite(connection, group, rows, cancellationToken);
            Writes.Enqueue(new(connection.Id, group.Id, rows.ToArray()));
        }
        public Task<DatabaseConnectionTestResult> TestConnectionAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default) => Task.FromResult(new DatabaseConnectionTestResult(true, "test"));
        public Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(DatabaseConnectionConfiguration connection, DatabaseTableInfo table, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsurePointTableAsync(DatabaseConnectionConfiguration connection, DatabaseTableInfo table, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TelemetrySample>> QueryPointHistoryAsync(DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group, HistoryQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class SampleLog : IConfigurableGatewayLog
    {
        private GatewayLoggingConfiguration _configuration = new();
        public ConcurrentQueue<TelemetrySample> Samples { get; } = new();
        public void ApplyConfiguration(GatewayLoggingConfiguration configuration) => _configuration = configuration;
        public void WriteSample(TelemetrySample sample) { if (_configuration.LogAcquisitionSamples && _configuration.MinimumLevel <= GatewayLogLevel.Information) Samples.Enqueue(sample); }
        public void Write(GatewayLogLevel level, string message) { }
        public IReadOnlyList<GatewayLogEntry> GetEntries(int limit = 500) => [];
    }
}
