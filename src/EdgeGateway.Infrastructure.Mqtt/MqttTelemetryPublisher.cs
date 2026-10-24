using System.Security.Authentication;
using System.Text;
using System.Threading.Channels;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace EdgeGateway.Infrastructure.Mqtt;

/// <summary>QoS 1 completion means broker acknowledgement. Duplicates remain possible and consumers must deduplicate messageId.</summary>
public sealed partial class MqttTelemetryPublisher : ITelemetryPublisher, IMqttRpcTransport, IMqttPlatformTransport
{
    public const int MaximumRpcPayloadBytes = MqttTransportLimits.MaximumRpcPayloadBytes;
    private readonly IMqttClient _client = new MqttFactory().CreateMqttClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<MqttInboundMessage> _incoming = Channel.CreateBounded<MqttInboundMessage>(new BoundedChannelOptions(64)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private string? _settingsKey;
    private string[] _subscriptions = [];
    private long _connectionVersion;
    private long _droppedMessages;
    private bool _disposed;
    public bool IsConnected => !_disposed && _client.IsConnected;
    public long ConnectionVersion => Interlocked.Read(ref _connectionVersion);
    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);

    public MqttTelemetryPublisher() => _client.ApplicationMessageReceivedAsync += ReceiveMessageAsync;

    private Task ReceiveMessageAsync(MqttApplicationMessageReceivedEventArgs arguments)
    {
        var message = arguments.ApplicationMessage;
        // Retained commands can unexpectedly actuate equipment after a subscription/reconnect.
        // Oversized, invalid UTF-8 and overflow packets are discarded without touching a device.
        if (message.Retain || message.PayloadSegment.Count > MaximumRpcPayloadBytes)
        {
            Interlocked.Increment(ref _droppedMessages);
            return Task.CompletedTask;
        }
        try
        {
            var payload = new UTF8Encoding(false, true).GetString(message.PayloadSegment);
            if (TryHandlePlatformResponse(message.Topic, payload)) return Task.CompletedTask;
            if (!_incoming.Writer.TryWrite(new(message.Topic, payload, DateTimeOffset.UtcNow)))
                Interlocked.Increment(ref _droppedMessages);
        }
        catch (DecoderFallbackException) { Interlocked.Increment(ref _droppedMessages); }
        return Task.CompletedTask;
    }

    public ValueTask<MqttInboundMessage> ReceiveAsync(CancellationToken cancellationToken) => _incoming.Reader.ReadAsync(cancellationToken);

    public async Task EnsureConnectedAsync(MqttConfiguration settings, IReadOnlyList<string> subscriptionTopics, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new InvalidOperationException("MQTT 上报未启用。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.TimeoutMs);
            var subscriptions = subscriptionTopics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (!_subscriptions.SequenceEqual(subscriptions, StringComparer.Ordinal))
            {
                await DisconnectCoreAsync(timeout.Token).ConfigureAwait(false);
                _subscriptions = subscriptions;
            }
            await ConnectCoreAsync(settings, timeout.Token).ConfigureAwait(false);
        }
        catch { await DisconnectAfterFailureAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    private async Task ConnectCoreAsync(MqttConfiguration settings, CancellationToken cancellationToken)
    {
        var key = $"{settings.Host}\n{settings.Port}\n{settings.ClientId}\n{settings.UseTls}\n{settings.Username}\n{settings.UsernameEnvironmentVariable}\n{settings.PasswordEnvironmentVariable}\n{settings.Platform}";
        if (_settingsKey != key && _client.IsConnected) await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
        if (_client.IsConnected) return;
        _activePlatform = settings.Platform;
        var builder = new MqttClientOptionsBuilder().WithClientId(settings.ClientId)
            .WithTcpServer(settings.Host, settings.Port).WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession().WithKeepAlivePeriod(TimeSpan.FromSeconds(30));
        var username = settings.Username;
        if (!string.IsNullOrEmpty(settings.UsernameEnvironmentVariable))
            username = Environment.GetEnvironmentVariable(settings.UsernameEnvironmentVariable)
                ?? throw new InvalidOperationException($"未设置 MQTT 用户名/令牌环境变量 {settings.UsernameEnvironmentVariable}。");
        if (!string.IsNullOrEmpty(username))
        {
            var password = string.IsNullOrEmpty(settings.PasswordEnvironmentVariable) ? null :
                Environment.GetEnvironmentVariable(settings.PasswordEnvironmentVariable);
            if (!string.IsNullOrEmpty(settings.PasswordEnvironmentVariable) && password is null)
                throw new InvalidOperationException($"未设置 MQTT 密码环境变量 {settings.PasswordEnvironmentVariable}。");
            builder.WithCredentials(username, password);
        }
        if (settings.UseTls)
            builder.WithTlsOptions(options => options.UseTls().WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13));
        var connected = await _client.ConnectAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
        if (connected.ResultCode != MqttClientConnectResultCode.Success)
            throw new IOException($"MQTT 连接被拒绝：{connected.ResultCode}");
        var subscriptions = settings.Platform == MqttPlatform.ThingsCloud
            ? _subscriptions.Concat(["gateway/connect/response", "gateway/attributes/response"]).Distinct(StringComparer.Ordinal).ToArray()
            : _subscriptions;
        if (subscriptions.Length > 0)
        {
            var subscribe = new MqttClientSubscribeOptionsBuilder();
            foreach (var topic in subscriptions)
                subscribe.WithTopicFilter(filter => filter.WithTopic(topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
            var result = await _client.SubscribeAsync(subscribe.Build(), cancellationToken).ConfigureAwait(false);
            if (result.Items.Count != subscriptions.Length || result.Items.Any(item => (int)item.ResultCode >= 128))
                throw new IOException("MQTT RPC 主题订阅被 Broker 拒绝。");
        }
        _cloudConnectedDevices.Clear();
        _settingsKey = key;
        Interlocked.Increment(ref _connectionVersion);
    }

    public async Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new InvalidOperationException("MQTT 上报未启用。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.TimeoutMs);
            await ConnectCoreAsync(settings, timeout.Token).ConfigureAwait(false);
            await PublishMessageCoreAsync(settings, topic, payload, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // A timed-out publish has an uncertain outcome. Keep the outbox row and reconnect before retry.
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task DisconnectAfterFailureAsync()
    {
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await DisconnectCoreAsync(timeout.Token).ConfigureAwait(false); }
        catch { /* Preserve the original error for diagnostics. */ }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_disposed) await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
            while (_incoming.Reader.TryRead(out _)) { }
        }
        finally { _gate.Release(); }
    }

    private Task DisconnectCoreAsync(CancellationToken cancellationToken) => _client.IsConnected
        ? _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken)
        : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await DisconnectCoreAsync(timeout.Token).ConfigureAwait(false); }
            finally { _disposed = true; _incoming.Writer.TryComplete(); _client.Dispose(); }
        }
        finally { _gate.Release(); }
    }
}
