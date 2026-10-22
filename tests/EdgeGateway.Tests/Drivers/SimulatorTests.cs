using EdgeGateway.Driver.Simulator;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.Drivers;

public sealed class SimulatorTests
{
    [Fact]
    public async Task Demo_values_have_requested_types_and_disabled_tags_are_omitted()
    {
        var tags = new List<TagConfiguration>
        {
            new() { Address = "constant:-12.5", DataType = TagDataType.Float64 },
            new() { Address = "sine", DataType = TagDataType.Float32 },
            new() { Address = "ramp", DataType = TagDataType.UInt16 },
            new() { Address = "random", DataType = TagDataType.Float64 },
            new() { Address = "bool", DataType = TagDataType.Boolean },
            new() { Address = "constant:99", Enabled = false }
        };
        await using var driver = new SimulatorDriverFactory().Create(new() { Tags = tags });
        await driver.ConnectAsync(CancellationToken.None);
        var values = await driver.ReadAsync(tags, CancellationToken.None);
        Assert.Equal(5, values.Count);
        Assert.Equal(-12.5, Assert.IsType<double>(values[0].Value));
        Assert.InRange(Assert.IsType<float>(values[1].Value), 30, 70);
        Assert.InRange(Assert.IsType<ushort>(values[2].Value), (ushort)0, (ushort)100);
        Assert.InRange(Assert.IsType<double>(values[3].Value), 0, 100);
        Assert.IsType<bool>(values[4].Value);
        Assert.All(values, value => Assert.Equal(SampleQuality.Good, value.Quality));
    }

    [Fact]
    public async Task Writes_are_explicit_typed_and_retained_for_the_instance()
    {
        var tag = new TagConfiguration { Address = "ramp", Writable = true };
        await using var driver = new SimulatorDriverFactory().Create(new() { Tags = [tag] });
        await driver.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => driver.WriteAsync(tag, 1.5, CancellationToken.None));
        await driver.WriteAsync(tag, 25, CancellationToken.None);
        Assert.Equal((ushort)25, (await driver.ReadAsync([tag], CancellationToken.None))[0].Value);
        tag.Writable = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.WriteAsync(tag, 30, CancellationToken.None));
    }
}
