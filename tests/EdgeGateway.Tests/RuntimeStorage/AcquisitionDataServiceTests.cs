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
        var storedTime = Assert.IsType<DateTimeOffset>(row.Values["timestamp"]);
        Assert.Equal(TimeSpan.Zero, storedTime.Offset);
        Assert.Equal(timestamp, storedTime);
        Assert.Equal(row.EventId, row.Values["event_id"]);
        var bad = rows.Single(item => (string)item.Values["tag_id"]! == second.TagId.ToString("D"));
        Assert.Equal("Bad", bad.Values["quality"]);
        Assert.Null(bad.Values["value_text"]);
        Assert.Equal(DatabasePointTable.Columns.Order(), bad.Values.Keys.Order());
        Assert.DoesNotContain("error", bad.Values.Keys);
        Assert.Equal(2, Assert.Single(sink.GetStatuses()).WrittenRows);
    }

    [Fact]
    public async Task CustomPointMappingWritesEachSelectedPointIndependentlyWithManualConstants()
    {
        var configuration = Configuration(); var connection = AddConnection(configuration);
        var group = AddPointMappedGroup(configuration, connection);
        configuration.Devices[0].Tags[0].UploadEnabled = false;
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        var first = Sample(configuration, 0, ulong.MaxValue);
        await sink.AppendAsync([first]);
        await Until(() => connector.Writes.Count == 1); // Does not wait for the second point.
        await sink.AppendAsync([Sample(configuration, 1, "状态文本")]);
        await sink.StopAsync();
        var rows = connector.Writes.SelectMany(write => write.Rows).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal(7L, row.Values["_QUALITY"]);
            Assert.Equal(configuration.Devices[0].Name, row.Values["_NUMERICID"]);
            Assert.DoesNotContain("id", row.Values.Keys);
            Assert.Equal(5, row.Values.Count);
        });
        var counter = rows.Single(row => Equals(row.Values["_NAME"], first.TagName));
        Assert.Equal("18446744073709551615", counter.Values["_VALUE"]);
        Assert.Equal(first.Timestamp.ToUniversalTime(), counter.Values["_TIMESTAMP"]);
        Assert.Contains(rows, row => Equals(row.Values["_VALUE"], "状态文本"));
        Assert.All(connector.Writes, write => Assert.Equal(group.Id, write.GroupId));
    }

    [Fact]
    public async Task CustomPointMappingFiltersPointsQualityAndOldDeviceSessions()
    {
        var configuration = Configuration(); var connection = AddConnection(configuration);
        var group = AddPointMappedGroup(configuration, connection);
        group.TagIds = [configuration.Devices[0].Tags[0].Id];
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 1, 99), Sample(configuration, 0, null, SampleQuality.Bad),
            Sample(configuration, 0, 100) with { Timestamp = DateTimeOffset.UtcNow.AddHours(-1) },
            Sample(configuration, 0, 101) with { Timestamp = DateTimeOffset.UtcNow.AddMinutes(1) }]);
        sink.SetDeviceActive(configuration.Devices[0].Id, false);
        var previousSession = Sample(configuration, 0, 102);
        await sink.AppendAsync([previousSession]);
        sink.SetDeviceActive(configuration.Devices[0].Id, true);
        await sink.AppendAsync([previousSession with { Timestamp = previousSession.Timestamp.AddSeconds(-1) }, Sample(configuration, 0, 42)]);
        await sink.StopAsync();
        Assert.Equal("42", Assert.Single(connector.Writes.SelectMany(write => write.Rows)).Values["_VALUE"]);
        Assert.Equal(5, Assert.Single(sink.GetStatuses()).SkippedRows);
    }

    [Fact]
    public async Task CustomPointMappingCanPreserveBadSampleWithoutRepeatingItOnTheTicker()
    {
        var configuration = Configuration(); var connection = AddConnection(configuration);
        var group = AddPointMappedGroup(configuration, connection); group.RequireGoodQuality = false;
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, null, SampleQuality.Bad)]);
        await Until(() => connector.Writes.Count == 1);
        await Task.Delay(160);
        await sink.StopAsync();
        var row = Assert.Single(connector.Writes.SelectMany(write => write.Rows));
        Assert.Null(row.Values["_VALUE"]);
        Assert.Equal(7L, row.Values["_QUALITY"]); // Explicit business constant, not inferred sample quality.
    }

    [Fact]
    public async Task CustomPointMappingConversionFailureDoesNotBlockOtherPoints()
    {
        var configuration = Configuration(); var connection = AddConnection(configuration);
        var group = AddPointMappedGroup(configuration, connection);
        group.Columns.Single(column => column.Column == "_VALUE").FieldType = DatabaseFieldType.Int64;
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, 1.5m), Sample(configuration, 1, 2L)]);
        await sink.StopAsync();
        Assert.Equal(2L, Assert.Single(connector.Writes.SelectMany(write => write.Rows)).Values["_VALUE"]);
        Assert.Equal(1, Assert.Single(sink.GetStatuses()).DroppedRows);
    }

    [Fact]
    public async Task CustomPointMappingWritesManualIntegerStatusIndependentOfSampleQuality()
    {
        var configuration = Configuration(); var connection = AddConnection(configuration);
        var group = AddPointMappedGroup(configuration, connection); group.RequireGoodQuality = false;
        var quality = group.Columns.Single(column => column.Column == "_QUALITY");
        quality.Source = DatabaseValueSource.Quality;
        Assert.Contains(DataRecordingConfigurationValidator.Validate(configuration), error => error.Contains("Good / Bad / Uncertain"));
        quality.Source = DatabaseValueSource.Constant; quality.ConstantValue = "192";
        quality.FieldType = DatabaseMappingRules.InferFieldType("int");
        Assert.Empty(DataRecordingConfigurationValidator.Validate(configuration));
        var connector = new Connector();
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, 12L), Sample(configuration, 1, null, SampleQuality.Bad)]);
        await sink.StopAsync();
        var rows = connector.Writes.SelectMany(write => write.Rows).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal(192L, Assert.IsType<long>(row.Values["_QUALITY"])));
    }

    [Fact]
    public void CustomPointMappingConfigurationRoundTripsAndRejectsFixedBindingsAndInvalidIntegerConstants()
    {
        var configuration = Configuration(); var connection = AddConnection(configuration);
        var group = AddPointMappedGroup(configuration, connection);
        var copy = GatewayJson.Clone(configuration);
        Assert.Empty(DataRecordingConfigurationValidator.Validate(copy));
        Assert.Equal(DatabaseStorageMode.PointMapping, Assert.Single(copy.DatabaseWriteGroups).Mode);
        group.Columns[0].TagId = configuration.Devices[0].Tags[0].Id;
        Assert.Contains(DataRecordingConfigurationValidator.Validate(configuration), error => error.Contains("不能固定关联"));
        group.Columns[0].TagId = null;
        group.Columns.Single(column => column.Column == "_QUALITY").ConstantValue = "not-an-integer";
        Assert.Contains(DataRecordingConfigurationValidator.Validate(configuration), error => error.Contains("_QUALITY") && error.Contains("常量"));
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
        connection.Password = null!;
        connection.QueueCapacityRows = 0;
        connection.QueueCapacityMb = 0;
        connection.MaxRetryAttempts = 100;
        group.Table = "table; DROP TABLE other";
        group.Columns[0].TagId = Guid.NewGuid();
        group.Columns[1].Column = group.Columns[0].Column;
        configuration.Logging.MaxFileSizeMb = 0;
        var errors = DataRecordingConfigurationValidator.Validate(configuration);
        Assert.Contains(errors, error => error.Contains("密码"));
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

    [Fact]
    public async Task PermanentFailuresStopRetryingAndReleaseAllReservations()
    {
        var configuration = Configuration(); var connection = AddConnection(configuration);
        connection.MaxRetryAttempts = 5; AddPointGroup(configuration, connection);
        var attempts = 0;
        var connector = new Connector { OnWrite = (_, _, _, _) => { Interlocked.Increment(ref attempts); throw new DatabaseWriteException("bad schema", false); } };
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, 1)]); await sink.StopAsync();
        var status = Assert.Single(sink.GetStatuses());
        Assert.Equal(1, attempts); Assert.Equal(0, status.RetriedBatches); Assert.Equal(1, status.FailedBatches);
        Assert.Equal(1, status.DroppedRows); Assert.Equal(0, status.QueuedRows); Assert.Equal(0, status.QueuedBytes);
        Assert.Equal(0, status.InFlightBatches); Assert.NotNull(status.LastFailure); Assert.Equal(1, status.PeakQueuedRows);
    }

    [Fact]
    public async Task TransientRecoveryKeepsEventIdsAndExposesRetryMetrics()
    {
        var configuration = Configuration(); var connection = AddConnection(configuration); AddPointGroup(configuration, connection);
        var identifiers = new ConcurrentQueue<string>(); var attempts = 0;
        var connector = new Connector { OnWrite = (_, _, rows, _) =>
        {
            identifiers.Enqueue(rows[0].EventId);
            if (Interlocked.Increment(ref attempts) < 3) throw new DatabaseWriteException("temporarily offline", true);
            return Task.CompletedTask;
        } };
        await using var sink = await Start(configuration, connector);
        await sink.AppendAsync([Sample(configuration, 0, 1)]); await sink.StopAsync();
        var status = Assert.Single(sink.GetStatuses());
        Assert.Equal(3, attempts); Assert.Single(identifiers.Distinct()); Assert.Equal(2, status.RetriedBatches);
        Assert.Equal(1, status.WrittenRows); Assert.Equal(0, status.DroppedRows); Assert.Equal(0, status.InFlightBatches);
    }

    [Fact]
    public async Task TimeoutQuarantinesOriginalOperationWithoutOverlappingRetry()
    {
        var configuration = Configuration(); var connection = AddConnection(configuration, capacity: 2);
        connection.ConnectionTimeoutSeconds = connection.CommandTimeoutSeconds = 1;
        AddPointGroup(configuration, connection);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var attempts = 0;
        var connector = new Connector { OnWrite = (_, _, _, _) => { Interlocked.Increment(ref attempts); return release.Task; } };
        await using var sink = await Start(configuration, connector);
        try
        {
            await sink.AppendAsync([Sample(configuration, 0, 1)]);
            await Until(() => Assert.Single(sink.GetStatuses()).TimedOutOperations == 1);
            var status = Assert.Single(sink.GetStatuses());
            Assert.Equal(1, attempts); Assert.Equal(1, status.InFlightBatches); Assert.Equal(1, status.QueuedRows);
            Assert.True(status.QueuedBytes > 0); Assert.Equal(0, status.RetriedBatches);
            release.TrySetResult(); await sink.StopAsync();
            Assert.Equal(1, Assert.Single(sink.GetStatuses()).WrittenRows);
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
    private static DatabaseWriteGroupConfiguration AddPointMappedGroup(GatewayConfiguration configuration, DatabaseConnectionConfiguration connection)
    {
        var group = new DatabaseWriteGroupConfiguration
        {
            Name = "custom points", Enabled = true, ConnectionId = connection.Id, Mode = DatabaseStorageMode.PointMapping,
            Table = "custom_samples", Columns =
            [
                new() { Column = "_NAME", Source = DatabaseValueSource.TagName, FieldType = DatabaseFieldType.Text },
                new() { Column = "_NUMERICID", Source = DatabaseValueSource.DeviceName, FieldType = DatabaseFieldType.Text },
                new() { Column = "_VALUE", Source = DatabaseValueSource.EngineeringValue, FieldType = DatabaseFieldType.Text },
                new() { Column = "_TIMESTAMP", Source = DatabaseValueSource.SampleTime, FieldType = DatabaseFieldType.Timestamp },
                new() { Column = "_QUALITY", Source = DatabaseValueSource.Constant, FieldType = DatabaseFieldType.Int64, ConstantValue = "7" }
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
