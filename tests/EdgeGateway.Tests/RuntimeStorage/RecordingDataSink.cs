using System.Collections.Concurrent;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.RuntimeStorage;

/// <summary>Observes accepted output batches in runtime lifecycle tests; it does not emulate local persistence.</summary>
internal sealed class RecordingDataSink(ConcurrentQueue<TelemetrySample> samples) : IAcquisitionDataSink
{
    public void UpdateConfiguration(GatewayConfiguration configuration) { }
    public void SetDeviceActive(Guid deviceId, bool active) { }
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync(IReadOnlyList<TelemetrySample> batch, CancellationToken cancellationToken = default)
    { foreach (var sample in batch) samples.Enqueue(sample); return Task.CompletedTask; }
    public Task StopAsync() => Task.CompletedTask;
    public IReadOnlyList<ExternalDatabaseStatus> GetStatuses() => [];
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
