using System.Security.Authentication;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace EdgeGateway.Service;

/// <summary>QoS 1 completion means broker acknowledgement. Duplicates remain possible and consumers must deduplicate messageId.</summary>
public sealed class MqttTelemetryPublisher : ITelemetryPublisher
{
    private readonly IMqttClient _client = new MqttFactory().CreateMqttClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _settingsKey;
    private bool _disposed;
    public bool IsConnected => !_disposed && _client.IsConnected;

    public async Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new InvalidOperationException("MQTT 上报未启用。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.TimeoutMs);
            var key = $"{settings.Host}\n{settings.Port}\n{settings.ClientId}\n{settings.UseTls}\n{settings.Username}\n{settings.PasswordEnvironmentVariable}";
            if (_settingsKey != key && _client.IsConnected) await DisconnectCoreAsync(timeout.Token).ConfigureAwait(false);
            if (!_client.IsConnected)
            {
                var builder = new MqttClientOptionsBuilder().WithClientId(settings.ClientId)
                    .WithTcpServer(settings.Host, settings.Port).WithProtocolVersion(MqttProtocolVersion.V311)
                    .WithCleanSession().WithKeepAlivePeriod(TimeSpan.FromSeconds(30));
                if (!string.IsNullOrEmpty(settings.Username))
                {
                    var password = string.IsNullOrEmpty(settings.PasswordEnvironmentVariable) ? null :
                        Environment.GetEnvironmentVariable(settings.PasswordEnvironmentVariable);
                    if (!string.IsNullOrEmpty(settings.PasswordEnvironmentVariable) && password is null)
                        throw new InvalidOperationException($"未设置 MQTT 密码环境变量 {settings.PasswordEnvironmentVariable}。");
                    builder.WithCredentials(settings.Username, password);
                }
                if (settings.UseTls)
                    builder.WithTlsOptions(options => options.UseTls().WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13));
                var connected = await _client.ConnectAsync(builder.Build(), timeout.Token).ConfigureAwait(false);
                if (connected.ResultCode != MqttClientConnectResultCode.Success)
                    throw new IOException($"MQTT 连接被拒绝：{connected.ResultCode}");
                _settingsKey = key;
            }
            var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build();
            var result = await _client.PublishAsync(message, timeout.Token).ConfigureAwait(false);
            if (!result.IsSuccess) throw new IOException($"MQTT 发布未确认：{result.ReasonCode}");
        }
        catch
        {
            // A timed-out publish has an uncertain outcome. Keep the outbox row and reconnect before retry.
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await DisconnectCoreAsync(timeout.Token).ConfigureAwait(false); }
            catch { /* Original error is the one the runtime should report. */ }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (!_disposed) await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false); }
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
            finally { _disposed = true; _client.Dispose(); }
        }
        finally { _gate.Release(); }
    }
}
