using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Hosting;

/// <summary>Worker-owned sampling, independent of page visibility and IPC request timing.</summary>
internal sealed class ServerResourceSampler : IServerResourceMonitor, IAsyncDisposable
{
    private readonly IServerResourceMonitor _monitor;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _firstReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ServerResourceReading> _history = [];
    private readonly Task _sampling;
    private ServerResourceSnapshot? _latest;

    internal ServerResourceSampler(IServerResourceMonitor monitor, TimeSpan? interval = null)
    {
        _monitor = monitor;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        _sampling = Task.Run(SampleAsync);
    }

    public async Task<ServerResourceSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _firstReading.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Volatile.Read(ref _latest)!;
    }

    private async Task SampleAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                ServerResourceSnapshot snapshot;
                try { snapshot = await _monitor.ReadAsync(_shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    snapshot = new(DateTimeOffset.UtcNow, Environment.MachineName, Environment.ProcessorCount,
                        null, null, [], ["资源采样失败：" + exception.Message]);
                }
                if (_history.Count > 0 && snapshot.Timestamp < _history[^1].Timestamp) _history.Clear();
                if (_history.Count > 0 && snapshot.Timestamp == _history[^1].Timestamp) _history.RemoveAt(_history.Count - 1);
                _history.Add(new(snapshot.Timestamp, snapshot.CpuUsagePercent, snapshot.Memory?.UsedPercent));
                _history.RemoveAll(reading => reading.Timestamp < snapshot.Timestamp.AddSeconds(-60));
                if (_history.Count > 121) _history.RemoveRange(0, _history.Count - 121);
                Volatile.Write(ref _latest, snapshot with { History = Array.AsReadOnly(_history.ToArray()) });
                _firstReading.TrySetResult();
            } while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { _firstReading.TrySetCanceled(_shutdown.Token); }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _sampling.ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
