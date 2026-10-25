using EdgeGateway.Hosting;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class OverviewTests
{
    [Fact]
    public void Summary_filters_removed_disabled_and_stopped_samples_and_exposes_partial_errors()
    {
        var devices = Enumerable.Range(0, 7).Select(i => new DeviceConfiguration
        { Name = "device-" + i, Tags = [new() { Name = "value", Address = "0" }] }).ToList();
        devices[6].Enabled = false;
        devices[0].Tags.Add(new() { Name = "disabled", Enabled = false });
        var states = new[] { DeviceState.Running, DeviceState.Running, DeviceState.Connecting,
            DeviceState.Reconnecting, DeviceState.Faulted, DeviceState.Stopped, DeviceState.Running };
        var statuses = devices.Select((device, i) => new DeviceRuntimeStatus(device.Id, device.Name, states[i],
            DateTimeOffset.UtcNow, 0, i == 1 ? "one tag failed" : null)).ToArray();
        var samples = devices.Select((device, i) => new TelemetrySample(device.Id, device.Name, device.Tags[0].Id,
            "value", "0", 1, 1, i == 1 || i == 3 ? SampleQuality.Bad : SampleQuality.Good, DateTimeOffset.UtcNow, "")).ToList();
        samples.RemoveAt(2); // Connecting device has never produced a sample.
        samples.Add(samples[0] with { TagId = devices[0].Tags[1].Id });
        samples.Add(samples[0] with { TagId = Guid.NewGuid() });
        var active = devices.Take(4).Select(device => device.Id).ToHashSet();
        var result = OverviewSummaryBuilder.Build(new() { Devices = devices }, statuses, samples, active);
        Assert.Equal(7, result.TotalDevices); Assert.Equal(6, result.EnabledDevices);
        Assert.Equal(2, result.OnlineDevices); Assert.Equal(3, result.AbnormalDevices);
        Assert.Equal(2, result.StoppedDevices); Assert.Equal(1, result.ConnectingDevices);
        Assert.Equal(1, result.ReconnectingDevices); Assert.Equal(1, result.FaultedDevices);
        Assert.Equal(6, result.EnabledPoints); Assert.Equal(1, result.GoodPoints);
        Assert.Equal(2, result.BadPoints); Assert.Equal(3, result.WaitingPoints);
        Assert.Equal(100d / 3, result.OnlinePercent!.Value, 8);
    }

    [Fact]
    public void Empty_summary_has_no_invented_online_rate()
    {
        var summary = OverviewSummaryBuilder.Build(new() { Devices = [] }, [], [], new HashSet<Guid>());
        Assert.Null(summary.OnlinePercent);
        Assert.Equal(0, summary.OnlineDevices);
    }

    [Fact]
    public async Task Cpu_uses_delta_of_system_counters_and_is_unknown_during_warmup_or_reset()
    {
        var readings = new Queue<ServerResourceMonitor.CpuTimes>([new(100, 200, 100), new(150, 260, 140), new(10, 20, 10)]);
        var monitor = new ServerResourceMonitor(() => readings.Dequeue(), () => new(16000, 4000), () => []);
        Assert.Null((await monitor.ReadAsync()).CpuUsagePercent);
        var second = await monitor.ReadAsync();
        Assert.Equal(50d, second.CpuUsagePercent);
        Assert.Equal(75d, second.Memory!.UsedPercent);
        Assert.Null((await monitor.ReadAsync()).CpuUsagePercent);
        Assert.Null(ServerResourceMonitor.CalculateCpu(new(1, 2, 3), new(1, 2, 3)));
        Assert.Null(ServerResourceMonitor.CalculateCpu(new(1, 2, 3), new(20, 3, 4)));
    }

    [Fact]
    public async Task Resource_failure_does_not_hide_other_metrics_or_reuse_old_cpu_baseline()
    {
        var calls = 0;
        var monitor = new ServerResourceMonitor(() => ++calls == 2 ? throw new IOException("counter unavailable")
            : new((ulong)calls * 10, (ulong)calls * 20, (ulong)calls * 10), () => throw new IOException("memory unavailable"),
            () => [new("C:\\", 1000, 250), new("D:\\", null, null, "access denied")]);
        await monitor.ReadAsync();
        var failed = await monitor.ReadAsync();
        Assert.Null(failed.CpuUsagePercent); Assert.Null(failed.Memory);
        Assert.Equal(2, failed.Errors.Count); Assert.Equal(75d, failed.Disks[0].UsedPercent);
        Assert.Null(failed.Disks[1].UsedPercent);
        Assert.Null((await monitor.ReadAsync()).CpuUsagePercent);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.ReadAsync(cancelled.Token));
    }

    [Fact]
    public async Task Native_host_metrics_are_real_or_explicitly_unavailable()
    {
        var result = await new ServerResourceMonitor().ReadAsync();
        Assert.NotEmpty(result.MachineName);
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(result.Memory);
            Assert.InRange(result.Memory!.UsedPercent, 0, 100);
            Assert.True(result.Memory.TotalBytes > 0);
            Assert.Contains(result.Disks, disk => disk.UsedPercent is >= 0 and <= 100);
        }
        else Assert.Contains(result.Errors, error => error.Contains("Windows"));
    }
}
