using System.Collections.Concurrent;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class TelemetryPipelineTests
{
    [Fact]
    public async Task FailureIsRedactedAndRecoveryRequiresPublisherAcknowledgement()
    {
        var variable = "EDGE_PIPELINE_TEST_" + Guid.NewGuid().ToString("N");
        const string secret = "diagnostic-test-secret";
        Environment.SetEnvironmentVariable(variable, secret);
        try
        {
            await using var harness = new Harness();
            harness.Configuration.Mqtt.PasswordEnvironmentVariable = variable;
            harness.Publisher.Fail = true;
            harness.Publisher.FailureMessage = "connection rejected " + secret;
            harness.Runtime.Configure(harness.Configuration);
            await harness.Runtime.StartAsync();
            await Until(() => harness.Runtime.GetTelemetryStatus().ConsecutiveFailures > 0);
            var failed = harness.Runtime.GetTelemetryStatus();
            Assert.True(failed.Enabled);
            Assert.True(failed.Running);
            Assert.NotNull(failed.LastEnqueuedAt);
            Assert.NotNull(failed.LastSendAttemptAt);
            Assert.NotNull(failed.LastSendFailureAt);
            Assert.NotNull(failed.NextRetryAt);
            Assert.Contains("***", failed.SendError);
            Assert.DoesNotContain(secret, failed.SendError);
            Assert.Null(failed.LastBrokerAcknowledgedAt);
            Assert.Equal(0, failed.AcknowledgedMessages);
            Assert.Null(failed.PersistenceError);
            Assert.NotEmpty(harness.Store.Outbox);

            harness.Publisher.Block = true;
            harness.Publisher.Fail = false;
            await Until(() => harness.Publisher.Attempts >= 2 && harness.Runtime.GetTelemetryStatus().Sending);
            Assert.Equal(0, harness.Runtime.GetTelemetryStatus().AcknowledgedMessages);
            Assert.Null(harness.Runtime.GetTelemetryStatus().LastBrokerAcknowledgedAt);
            harness.Publisher.AllowAcknowledgement.TrySetResult(true);
            await Until(() => harness.Runtime.GetTelemetryStatus().AcknowledgedMessages > 0
                && harness.Runtime.GetTelemetryStatus().SendError is null);
            var recovered = harness.Runtime.GetTelemetryStatus();
            Assert.Equal(0, recovered.ConsecutiveFailures);
            Assert.Null(recovered.NextRetryAt);
            Assert.NotNull(recovered.LastBrokerAcknowledgedAt);
            Assert.True(recovered.LastSendFailureAt >= failed.LastSendFailureAt);
            Assert.NotNull(failed.SendError); // The previously returned snapshot must not mutate.
            Assert.Equal(0, failed.AcknowledgedMessages);
            Assert.All(harness.Log.GetEntries(), entry => Assert.DoesNotContain(secret, entry.Message));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task StoppingDuringPublishRetainsUnacknowledgedMessageWithoutRecordingFailure()
    {
        await using var harness = new Harness();
        harness.Publisher.Block = true;
        harness.Store.Seed("old/topic", "{\"messageId\":\"awaiting-ack\"}");
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetTelemetryStatus().Sending);
        await harness.Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var status = harness.Runtime.GetTelemetryStatus();
        Assert.False(status.Running);
        Assert.False(status.Sending);
        Assert.Equal(0, status.AcknowledgedMessages);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.LastSendFailureAt);
        Assert.Null(status.SendError);
        Assert.Null(status.PersistenceError);
        Assert.Null(status.NextRetryAt);
        Assert.Contains(harness.Store.Outbox.Values, row => row.Payload.Contains("awaiting-ack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CapacityAndPersistenceErrorsRecoverWithoutBlockingAcquisition()
    {
        var variable = "EDGE_QUEUE_TEST_" + Guid.NewGuid().ToString("N");
        const string secret = "queue-error-test-secret";
        Environment.SetEnvironmentVariable(variable, secret);
        try
        {
            await using var harness = new Harness();
            harness.Configuration.Mqtt.PasswordEnvironmentVariable = variable;
            harness.Store.EnqueueMode = EnqueueBehavior.Full;
            harness.Store.FailureMessage = "disk error " + secret;
            harness.Runtime.Configure(harness.Configuration);
            await harness.Runtime.StartAsync();
            await Until(() => harness.Runtime.GetTelemetryStatus().QueueCapacityBlocked);
            var full = harness.Runtime.GetTelemetryStatus();
            Assert.NotNull(full.LastCapacityBlockedAt);
            Assert.NotNull(full.LastEnqueueAttemptAt);
            Assert.Null(full.PersistenceError);
            Assert.Null(full.SendError);
            var collected = harness.Store.Samples.Count;
            await Until(() => harness.Store.Samples.Count > collected + 2);

            harness.Store.EnqueueMode = EnqueueBehavior.Throw;
            await Until(() => harness.Runtime.GetTelemetryStatus().PersistenceError is not null);
            var failed = harness.Runtime.GetTelemetryStatus();
            Assert.False(failed.QueueCapacityBlocked);
            Assert.NotNull(failed.LastPersistenceFailureAt);
            Assert.Contains("***", failed.PersistenceError);
            Assert.DoesNotContain(secret, failed.PersistenceError);
            Assert.Null(failed.SendError);
            Assert.Equal(0, failed.ConsecutiveFailures);

            harness.Store.EnqueueMode = EnqueueBehavior.Normal;
            await Until(() => harness.Runtime.GetTelemetryStatus().LastEnqueuedAt is not null
                && harness.Runtime.GetTelemetryStatus().PersistenceError is null);
            var recovered = harness.Runtime.GetTelemetryStatus();
            Assert.False(recovered.QueueCapacityBlocked);
            Assert.True(recovered.LastCapacityBlockedAt >= full.LastCapacityBlockedAt);
            Assert.True(recovered.LastPersistenceFailureAt >= failed.LastPersistenceFailureAt);
            Assert.All(harness.Log.GetEntries(), entry => Assert.DoesNotContain(secret, entry.Message));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task UploadSelectionDoesNotRemoveHistoryOrPreviouslyDurableMessages()
    {
        await using var harness = new Harness();
        var device = harness.Configuration.Devices[0];
        var uploaded = device.Tags[0];
        var localOnly = new TagConfiguration { Name = "本地记录", Address = "1", DataType = TagDataType.Float64, UploadEnabled = false };
        var disabled = new TagConfiguration { Name = "停用点位", Address = "2", DataType = TagDataType.Float64, Enabled = false };
        device.Tags.AddRange([localOnly, disabled]);
        var oldPayload = JsonSerializer.Serialize(new
        {
            messageId = "before-upload-was-disabled",
            samples = new[] { new { deviceId = device.Id, tagId = localOnly.Id, value = 123 } }
        });
        harness.Store.Seed("original/topic", oldPayload);
        harness.Runtime.Configure(harness.Configuration);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Publisher.Delivered.Any(message => message.Payload != oldPayload));
        Assert.Contains(harness.Publisher.Delivered, message => message.Topic == "original/topic" && message.Payload == oldPayload);
        var fresh = harness.Publisher.Delivered.Where(message => message.Payload != oldPayload).ToArray();
        Assert.NotEmpty(fresh);
        Assert.All(fresh, message => Assert.Equal([uploaded.Id], TagIds(message.Payload)));
        Assert.Contains(harness.Store.Samples, sample => sample.TagId == localOnly.Id && sample.Quality == SampleQuality.Good);
        Assert.DoesNotContain(harness.Store.Samples, sample => sample.TagId == disabled.Id);
        Assert.Contains(harness.Runtime.GetLatest(), sample => sample.TagId == localOnly.Id);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("point")]
    [InlineData("upload")]
    public async Task PendingSnapshotIsRegeneratedAfterUploadSelectionChanges(string disabledSetting)
    {
        await using var harness = new Harness();
        var first = harness.Configuration.Devices[0];
        var second = Device("第二台设备");
        harness.Configuration.Devices.Add(second);
        harness.Store.EnqueueMode = EnqueueBehavior.Full;
        harness.Runtime.Configure(harness.Configuration);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count == 2 && harness.Runtime.GetTelemetryStatus().QueueCapacityBlocked);
        await Until(() => harness.Store.EnqueueAttempts.Any(attempt => TagIds(attempt.Payload).Contains(second.Tags[0].Id)));
        await harness.Runtime.StopDeviceAsync(second.Id);

        var updated = Clone(harness.Configuration);
        var changed = updated.Devices.Single(device => device.Id == second.Id);
        if (disabledSetting == "device") changed.Enabled = false;
        else if (disabledSetting == "point") changed.Tags[0].Enabled = false;
        else changed.Tags[0].UploadEnabled = false;
        await harness.Runtime.ApplyConfigurationAsync(updated, new MemoryConfigurationStore());
        harness.Store.EnqueueMode = EnqueueBehavior.Normal;

        await Until(() => !harness.Publisher.Delivered.IsEmpty);
        Assert.All(harness.Publisher.Delivered, message => Assert.Equal([first.Tags[0].Id], TagIds(message.Payload)));
        Assert.True(harness.Runtime.IsDeviceRunning(first.Id));
        Assert.False(harness.Runtime.IsDeviceRunning(second.Id));
    }

    [Fact]
    public async Task RecoveredQueueReadClearsItsErrorEvenWhenBrokerStillFails()
    {
        await using var harness = new Harness();
        harness.Configuration.Devices[0].Tags[0].UploadEnabled = false;
        harness.Runtime.Configure(harness.Configuration);
        harness.Store.FailRead = true;
        harness.Publisher.Fail = true;
        harness.Store.Seed("original/topic", "{\"messageId\":\"read-recovery\"}");
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetTelemetryStatus().PersistenceError is not null);
        var failed = harness.Runtime.GetTelemetryStatus();
        Assert.Contains("读取补传队列失败", failed.PersistenceError);
        Assert.Null(failed.LastSendAttemptAt);

        harness.Store.FailRead = false;
        await Until(() => harness.Runtime.GetTelemetryStatus().SendError?.Contains("MQTT 发送", StringComparison.Ordinal) == true);
        var mqttFailure = harness.Runtime.GetTelemetryStatus();
        Assert.Null(mqttFailure.PersistenceError);
        Assert.True(mqttFailure.LastPersistenceFailureAt >= failed.LastPersistenceFailureAt);
        Assert.NotNull(mqttFailure.LastSendAttemptAt);
        Assert.Null(mqttFailure.LastBrokerAcknowledgedAt);
        Assert.True(mqttFailure.ConsecutiveFailures > 0);
    }

    [Fact]
    public async Task BrokerAcknowledgementAndLocalRemovalFailureAreReportedSeparately()
    {
        await using var harness = new Harness();
        harness.Configuration.Devices[0].Tags[0].UploadEnabled = false;
        harness.Runtime.Configure(harness.Configuration);
        harness.Store.FailAcknowledgement = true;
        var id = harness.Store.Seed("original/topic", "{\"messageId\":\"retained-after-broker-ack\"}");
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetTelemetryStatus().PersistenceError is not null);
        var failed = harness.Runtime.GetTelemetryStatus();
        Assert.True(failed.AcknowledgedMessages >= 1);
        Assert.NotNull(failed.LastBrokerAcknowledgedAt);
        Assert.Contains("发送已确认", failed.SendError);
        Assert.Contains("本地队列确认失败", failed.PersistenceError);
        Assert.True(harness.Store.Outbox.ContainsKey(id));

        harness.Publisher.Fail = true;
        await Until(() => harness.Runtime.GetTelemetryStatus().SendError?.Contains("MQTT 发送", StringComparison.Ordinal) == true);
        Assert.Contains("本地队列确认失败", harness.Runtime.GetTelemetryStatus().PersistenceError);
        var confirmedBeforeRecovery = harness.Runtime.GetTelemetryStatus().AcknowledgedMessages;
        harness.Publisher.Fail = false;
        harness.Store.FailAcknowledgement = false;
        await Until(() => !harness.Store.Outbox.ContainsKey(id) && harness.Runtime.GetTelemetryStatus().SendError is null);
        var recovered = harness.Runtime.GetTelemetryStatus();
        Assert.Equal(confirmedBeforeRecovery + 1, recovered.AcknowledgedMessages); // Retrying the retained row acknowledges it again.
        Assert.Null(recovered.PersistenceError);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.NotNull(recovered.LastPersistenceFailureAt);
        Assert.Equal(recovered.AcknowledgedMessages, harness.Publisher.Delivered.Count);
    }

    [Fact]
    public async Task SqliteStatisticsFollowTheOldestUnacknowledgedRow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-telemetry-statistics-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new SqliteGatewayStore(Path.Combine(directory, "gateway.db"));
            await store.InitializeAsync();
            Assert.Null((await store.GetStatisticsAsync()).OldestOutboxAt);
            await store.EnqueueAsync("topic", "first", 10);
            await store.EnqueueAsync("topic", "second", 10);
            var rows = await store.ReadOutboxAsync(10);
            Assert.Equal(rows[0].CreatedAt, (await store.GetStatisticsAsync()).OldestOutboxAt);
            await store.AcknowledgeAsync(rows[0].Id);
            Assert.Equal(rows[1].CreatedAt, (await store.GetStatisticsAsync()).OldestOutboxAt);
            await store.AcknowledgeAsync(rows[1].Id);
            var empty = await store.GetStatisticsAsync();
            Assert.Equal(0, empty.OutboxCount);
            Assert.Null(empty.OldestOutboxAt);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static Guid[] TagIds(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("samples").EnumerateArray()
            .Select(sample => sample.GetProperty("tagId").GetGuid()).ToArray();
    }

    private static DeviceConfiguration Device(string name) => new()
    {
        Name = name, DriverId = "test-pipeline", PollIntervalMs = 100, TimeoutMs = 1000,
        Tags = [new TagConfiguration { Name = "温度", Address = "0", DataType = TagDataType.Float64 }]
    };

    private static GatewayConfiguration Clone(GatewayConfiguration configuration) =>
        JsonSerializer.Deserialize<GatewayConfiguration>(JsonSerializer.Serialize(configuration))!;

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("未在期限内观察到上报诊断状态。");
            await Task.Delay(20);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public GatewayConfiguration Configuration { get; } = new()
        {
            PublishIntervalMs = 100,
            Mqtt = new MqttConfiguration { Enabled = true },
            Devices = [Device("测试设备")]
        };
        public PipelineStore Store { get; } = new();
        public PipelinePublisher Publisher { get; } = new();
        public GatewayLog Log { get; } = new();
        public GatewayRuntime Runtime { get; }
        public Harness() => Runtime = new GatewayRuntime(Configuration, new DriverRegistry([new PipelineFactory()], Log), Store, Publisher, Log);
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class PipelineFactory : IDriverFactory
    {
        public DriverDescriptor Descriptor { get; } = new("test-pipeline", "上报测试驱动", "in-memory test device", false);
        public IDeviceDriver Create(DeviceConfiguration configuration) => new PipelineDriver();
        public IReadOnlyList<string> Validate(DeviceConfiguration configuration) => [];
        private sealed class PipelineDriver : IDeviceDriver
        {
            public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<TagReadResult>>(tags.Select(tag => new TagReadResult(tag.Id, 42d)).ToArray());
            public Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken) => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed record PublishedMessage(string Topic, string Payload);
    private sealed class PipelinePublisher : ITelemetryPublisher
    {
        public volatile bool Fail;
        public volatile bool Block;
        public string FailureMessage { get; set; } = "broker unavailable";
        public int Attempts;
        public bool IsConnected => !Fail;
        public TaskCompletionSource<bool> AllowAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<PublishedMessage> Delivered { get; } = new();
        public async Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new IOException(FailureMessage);
            if (Block) await AllowAcknowledgement.Task.WaitAsync(cancellationToken);
            Delivered.Enqueue(new PublishedMessage(topic, payload));
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private enum EnqueueBehavior { Normal, Full, Throw }
    private sealed class PipelineStore : IGatewayStore
    {
        private long _id;
        public volatile EnqueueBehavior EnqueueMode;
        public volatile bool FailRead;
        public volatile bool FailAcknowledgement;
        public string FailureMessage { get; set; } = "storage unavailable";
        public ConcurrentQueue<TelemetrySample> Samples { get; } = new();
        public ConcurrentDictionary<long, OutboxMessage> Outbox { get; } = new();
        public ConcurrentQueue<PublishedMessage> EnqueueAttempts { get; } = new();
        public long Seed(string topic, string payload)
        {
            var id = Interlocked.Increment(ref _id);
            Outbox[id] = new OutboxMessage(id, topic, payload, DateTimeOffset.UtcNow);
            return id;
        }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendSamplesAsync(IReadOnlyList<TelemetrySample> samples, CancellationToken cancellationToken = default)
        { foreach (var sample in samples) Samples.Enqueue(sample); return Task.CompletedTask; }
        public Task<IReadOnlyList<TelemetrySample>> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TelemetrySample>>(Samples.ToArray());
        public Task<bool> EnqueueAsync(string topic, string payload, int maxMessages, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnqueueAttempts.Enqueue(new PublishedMessage(topic, payload));
            if (EnqueueMode == EnqueueBehavior.Throw) throw new IOException(FailureMessage);
            if (EnqueueMode == EnqueueBehavior.Full || Outbox.Count >= maxMessages) return Task.FromResult(false);
            Seed(topic, payload);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(int limit, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRead) throw new IOException("queue read failed");
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(Outbox.Values.OrderBy(row => row.Id).Take(limit).ToArray());
        }
        public Task AcknowledgeAsync(long id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailAcknowledgement) throw new IOException("queue removal failed");
            Outbox.TryRemove(id, out _);
            return Task.CompletedTask;
        }
        public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoreStatistics(Samples.Count, Outbox.Count, Outbox.Values.OrderBy(row => row.Id).FirstOrDefault()?.CreatedAt));
        public Task PruneHistoryAsync(int retentionDays, int maxRows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryConfigurationStore : IConfigurationStore
    {
        private GatewayConfiguration? _saved;
        public Task<GatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Clone(_saved!));
        public Task SaveAsync(GatewayConfiguration configuration, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _saved = Clone(configuration); return Task.CompletedTask; }
    }
}
