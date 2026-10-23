using System.Collections.Concurrent;
using System.Diagnostics;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed partial class GatewayRuntime
{
    private readonly ConcurrentDictionary<Guid, ChannelRuntime> _channels = new();
    private readonly ConcurrentDictionary<Guid, DeviceCommunicationStatistics> _communicationStatistics = new();
    private readonly ConcurrentDictionary<Guid, long> _deviceGenerations = new();

    public IReadOnlyList<DeviceCommunicationStatistics> GetCommunicationStatistics() => _configuration.Devices
        .Select(d => _communicationStatistics.GetValueOrDefault(d.Id) ?? new(d.Id, 0, 0, 0, 0, null, null, null)).ToArray();

    private async ValueTask<IAsyncDisposable> EnterChannelAsync(DeviceConfiguration device, CancellationToken token)
    {
        if (device.ChannelId is not Guid channelId) return EmptyLease.Instance;
        var configuration = _configuration.Channels.First(c => c.Id == channelId);
        var channel = _channels.GetOrAdd(channelId, _ => new ChannelRuntime());
        await channel.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (channel.LastCompleted != 0 && configuration.InterRequestDelayMs > 0)
            {
                var remaining = TimeSpan.FromMilliseconds(configuration.InterRequestDelayMs) - Stopwatch.GetElapsedTime(channel.LastCompleted);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return new ChannelLease(channel);
        }
        catch { channel.Gate.Release(); throw; }
    }

    private void RecordDeviceOperation(Guid deviceId, bool write, bool success, TimeSpan elapsed, string? error = null)
    {
        var now = DateTimeOffset.UtcNow;
        _communicationStatistics.AddOrUpdate(deviceId,
            _ => new(deviceId, write ? 0 : 1, write ? 1 : 0, success ? 0 : 1, elapsed.TotalMilliseconds, now, error, null),
            (_, old) => old with
            {
                ReadRequests = old.ReadRequests + (write ? 0 : 1), WriteRequests = old.WriteRequests + (write ? 1 : 0),
                FailedRequests = old.FailedRequests + (success ? 0 : 1), LastDurationMs = elapsed.TotalMilliseconds,
                LastRequestAt = now, LastError = success ? null : error
            });
    }

    private int ReconnectDelay(DeviceConfiguration device, int failures)
    {
        var demoted = device.AutoDemotionEnabled && failures >= device.DemotionFailureCount;
        var delay = demoted ? device.DemotionIntervalMs : Backoff(failures);
        SetDemotion(device.Id, demoted ? DateTimeOffset.UtcNow.AddMilliseconds(delay) : null);
        return delay;
    }

    private void SetDemotion(Guid deviceId, DateTimeOffset? until) => _communicationStatistics.AddOrUpdate(deviceId,
        _ => new(deviceId, 0, 0, 0, 0, null, null, until), (_, old) => old with { DemotedUntil = until });

    private sealed class ChannelRuntime
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long LastCompleted;
    }

    private sealed class ChannelLease(ChannelRuntime channel) : IAsyncDisposable
    {
        private ChannelRuntime? _channel = channel;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _channel, null) is { } owned)
            {
                owned.LastCompleted = Stopwatch.GetTimestamp();
                owned.Gate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyLease : IAsyncDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
