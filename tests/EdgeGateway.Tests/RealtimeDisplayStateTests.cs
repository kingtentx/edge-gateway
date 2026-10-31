using EdgeGateway.Model;
using EdgeGateway.WinForms;

namespace EdgeGateway.Tests;

public sealed class RealtimeDisplayStateTests
{
    private readonly DeviceConfiguration _device = new()
    {
        Name = "设备", DriverId = "simulator",
        Tags = [new() { Name = "温度", Address = "sine", Unit = "℃" }]
    };
    private readonly RealtimeDisplayState _view = new();
    private TelemetrySample Sample(int value, int second) => new(_device.Id, _device.Name,
        _device.Tags[0].Id, "温度", "sine", value, value, SampleQuality.Good,
        new DateTimeOffset(2026, 11, 1, 0, 0, second, TimeSpan.Zero), "℃");
    private RealtimeDisplayPoint Update(DeviceState state, params TelemetrySample[] samples) => Assert.Single(
        _view.Update([_device], [new(_device.Id, _device.Name, state, null, 0, null)], samples));

    [Theory]
    [InlineData(DeviceState.Stopped)]
    [InlineData(DeviceState.Connecting)]
    [InlineData(DeviceState.Reconnecting)]
    [InlineData(DeviceState.Faulted)]
    public void OfflineFreezesValuesAndTimeButReportsCurrentFailure(DeviceState state)
    {
        var last = Sample(10, 1);
        Update(DeviceState.Running, last);
        var incoming = Sample(90, 2) with { Quality = SampleQuality.Bad, Error = "读取失败" };
        var displayed = Update(state, incoming).Sample;
        Assert.Equal(last.Value, displayed.Value);
        Assert.Equal(last.RawValue, displayed.RawValue);
        Assert.Equal(last.Timestamp, displayed.Timestamp);
        Assert.Equal(SampleQuality.Bad, displayed.Quality);
        Assert.Contains("读取失败", displayed.Error);
        Assert.Equal(90, incoming.Value); // Display retention does not mutate acquisition data.
        var recovered = Update(DeviceState.Running, Sample(20, 3)).Sample;
        Assert.Equal(20, recovered.Value);
        Assert.Equal(Sample(20, 3).Timestamp, recovered.Timestamp);
        Assert.Equal(SampleQuality.Good, recovered.Quality);
    }

    [Fact]
    public void FirstOfflineViewShowsLastKnownSampleWithoutClaimingItIsCurrent()
    {
        var shown = Update(DeviceState.Stopped, Sample(10, 1));
        Assert.True(shown.HasSample);
        Assert.Equal(10, shown.Sample.Value);
        Assert.Equal(SampleQuality.Uncertain, shown.Sample.Quality);
        Assert.Contains("保留最后采样值", shown.Sample.Error);
        Assert.Equal(10, Update(DeviceState.Stopped, Sample(20, 2)).Sample.Value);
    }

    [Fact]
    public void ConfiguredUnsampledPointsAreVisibleAndNeverFabricateValues()
    {
        var shown = Update(DeviceState.Stopped);
        Assert.False(shown.HasSample);
        Assert.Equal(_device.Tags[0].Id, shown.Sample.TagId);
        Assert.Null(shown.Sample.Value);
        Assert.NotEqual(SampleQuality.Good, shown.Sample.Quality);
    }

    [Fact]
    public void DisabledDeviceOrPointCannotAdvanceValues()
    {
        Update(DeviceState.Running, Sample(10, 1));
        _device.Enabled = false;
        Assert.Equal(10, Update(DeviceState.Running, Sample(20, 2)).Sample.Value);
        _device.Enabled = true; _device.Tags[0].Enabled = false;
        Assert.Equal(10, Update(DeviceState.Running, Sample(30, 3)).Sample.Value);
    }

    [Fact]
    public void RemovedPointOrChangedAddressCannotReuseHeldValues()
    {
        Update(DeviceState.Running, Sample(10, 1));
        var tag = _device.Tags[0];
        _device.Tags.Clear();
        Assert.Empty(_view.Update([_device], [], []));
        _device.Tags.Add(tag);
        Assert.False(Update(DeviceState.Stopped).HasSample);
        Update(DeviceState.Running, Sample(20, 2));
        tag.Address = "ramp";
        Assert.False(Update(DeviceState.Stopped, Sample(20, 2)).HasSample);
    }

    [Fact]
    public void OnlineAndOfflineDevicesAreUpdatedIndependentlyByStableIds()
    {
        var other = new DeviceConfiguration { Name = _device.Name, DriverId = "simulator",
            Tags = [new() { Name = "温度", Address = "sine" }] };
        var first = Sample(10, 1);
        TelemetrySample Other(int value, int second) => Sample(value, second) with
            { DeviceId = other.Id, TagId = other.Tags[0].Id };
        DeviceRuntimeStatus[] states = [new(_device.Id, _device.Name, DeviceState.Stopped, null, 0, null),
            new(other.Id, other.Name, DeviceState.Running, null, 0, null)];
        _view.Update([_device, other], states, [first, Other(20, 1)]);
        var shown = _view.Update([_device, other], states, [Sample(11, 2), Other(21, 2)]);
        Assert.Equal(10, shown.Single(p => p.Sample.DeviceId == _device.Id).Sample.Value);
        Assert.Equal(21, shown.Single(p => p.Sample.DeviceId == other.Id).Sample.Value);
    }
}
