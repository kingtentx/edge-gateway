using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed partial class GatewayRuntime
{
    private CancellationTokenSource? _platformCancellation;
    private volatile bool _platformRunning;
    private bool _platformPaused;
    private bool _platformIndependent;

    public async Task StartPlatformAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateConfiguration();
            if (!_configuration.Mqtt.Enabled) throw new InvalidOperationException("请先启用平台上报并应用设置，再启动平台连接。");
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // An explicit start also permits connection and backlog delivery without devices.
            _platformIndependent = true;
            _platformPaused = false;
            StartPlatformCore();
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopPlatformAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _platformPaused = true;
            _platformIndependent = false;
            await StopPlatformCoreAsync().ConfigureAwait(false);
            _log.Write(GatewayLogLevel.Information, "平台连接已停止，未确认消息保留；设备采集与数据库写入继续。");
        }
        finally { _lifecycle.Release(); }
    }

    // Caller holds _lifecycle and has initialized the message store. Never use the
    // device cancellation source: platform maintenance must not cancel acquisition.
    private void StartPlatformCore()
    {
        if (_platformRunning || _platformPaused || !_configuration.Mqtt.Enabled) return;
        _platformCancellation = new CancellationTokenSource();
        var token = _platformCancellation.Token;
        _platformRunning = true;
        _sharedWorkers = [Task.Run(() => QueueTelemetryAsync(token)), Task.Run(() => SendOutboxAsync(token)),
            Task.Run(() => RunPlatformSessionAsync(token))];
        _log.Write(GatewayLogLevel.Information, "平台连接任务已启动，实际连接与发送结果请查看通信诊断。");
    }

    private async Task StopPlatformCoreAsync()
    {
        _platformRunning = false;
        _platformCancellation?.Cancel();
        try { await Task.WhenAll(_sharedWorkers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _log.Write(GatewayLogLevel.Error, $"停止平台任务时发生错误：{Redact(exception.Message)}"); }
        _sharedWorkers = [];
        _platformCancellation?.Dispose();
        _platformCancellation = null;
        // Disconnect also drains pending inbound RPC messages. Never let an old
        // connection's request execute under the newly committed credentials.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await _publisher.DisconnectAsync(timeout.Token).ConfigureAwait(false);
    }

    private static bool PlatformSettingsEqual(GatewayConfiguration first, GatewayConfiguration second) =>
        first.GatewayId == second.GatewayId && ConfigurationValuesEqual(first.Mqtt, second.Mqtt)
        && first.PublishIntervalMs == second.PublishIntervalMs && first.ReportMode == second.ReportMode
        && first.HeartbeatIntervalMs == second.HeartbeatIntervalMs && first.MaxOutboxMessages == second.MaxOutboxMessages;
}
