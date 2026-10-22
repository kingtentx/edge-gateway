using System.Diagnostics;
using System.Globalization;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Driver.Simulator;

public sealed class SimulatorDriverFactory : IDriverFactory
{
    public DriverDescriptor Descriptor { get; } = new("simulator", "模拟数据", "sine、ramp、random、constant:数值、bool；仅用于离线演示与联调", true);

    public IDeviceDriver Create(DeviceConfiguration configuration)
    {
        var errors = Validate(configuration);
        if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
        return new SimulatorDriver();
    }

    public IReadOnlyList<string> Validate(DeviceConfiguration configuration)
    {
        var errors = new List<string>();
        foreach (var tag in configuration.Tags.Where(t => t.Enabled))
        {
            if (!Enum.IsDefined(tag.DataType)) errors.Add($"点位“{tag.Name}”的数据类型无效。");
            var address = (tag.Address ?? "").Trim().ToLowerInvariant();
            if (address is "sine" or "ramp" or "random" or "bool") continue;
            if (address.StartsWith("constant:", StringComparison.Ordinal) &&
                double.TryParse(address[9..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                double.IsFinite(value))
            {
                try { SimulatorDriver.ConvertValue(value, tag.DataType); }
                catch (Exception ex) when (ex is OverflowException or ArgumentException)
                { errors.Add($"点位“{tag.Name}”的常量超出数据类型范围。"); }
                continue;
            }
            errors.Add($"点位“{tag.Name}”的模拟地址无效，支持 sine、ramp、random、constant:数值、bool。");
        }
        return errors;
    }
}

internal sealed class SimulatorDriver : IDeviceDriver
{
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<Guid, object> _writtenValues = [];
    private bool _connected;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _clock.Restart();
        _connected = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken)
    {
        if (!_connected) throw new InvalidOperationException("模拟驱动尚未连接。");
        var results = new List<TagReadResult>(tags.Count);
        foreach (var tag in tags.Where(t => t.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_writtenValues.TryGetValue(tag.Id, out var written))
            {
                results.Add(new(tag.Id, written));
                continue;
            }
            var seconds = _clock.Elapsed.TotalSeconds;
            var address = tag.Address.Trim().ToLowerInvariant();
            double raw = address switch
            {
                "sine" => 50 + 20 * Math.Sin(seconds / 5),
                "ramp" => seconds % 100,
                "random" => Random.Shared.NextDouble() * 100,
                "bool" => ((int)seconds % 2 == 0) ? 1 : 0,
                _ when address.StartsWith("constant:", StringComparison.Ordinal) => double.Parse(address[9..], CultureInfo.InvariantCulture),
                _ => throw new ArgumentException($"未知模拟地址：{tag.Address}")
            };
            try { results.Add(new(tag.Id, ConvertValue(raw, tag.DataType))); }
            catch (Exception ex) when (ex is OverflowException or ArgumentException)
            { results.Add(new(tag.Id, null, SampleQuality.Bad, ex.Message)); }
        }
        return Task.FromResult<IReadOnlyList<TagReadResult>>(results);
    }

    public Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected) throw new InvalidOperationException("模拟驱动尚未连接。");
        if (!tag.Enabled || !tag.Writable) throw new InvalidOperationException("该点位未启用或未允许写入。");
        if (tag.DataType == TagDataType.Boolean)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (bool.TryParse(text, out var boolean)) _writtenValues[tag.Id] = boolean;
            else if (text is "0" or "1") _writtenValues[tag.Id] = text == "1";
            else throw new ArgumentException("Boolean 写入值必须为 true、false、0 或 1。");
            return Task.CompletedTask;
        }
        var number = value is bool flag ? flag ? 1d : 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new ArgumentException("写入值必须为有限数值。");
        if (tag.DataType is TagDataType.Int16 or TagDataType.UInt16 or TagDataType.Int32 or TagDataType.UInt32 && number != Math.Truncate(number))
            throw new ArgumentException("整数点位不能写入小数。");
        _writtenValues[tag.Id] = ConvertValue(number, tag.DataType);
        return Task.CompletedTask;
    }

    internal static object ConvertValue(double value, TagDataType type) => type switch
    {
        TagDataType.Boolean => value != 0,
        TagDataType.Int16 => checked((short)value),
        TagDataType.UInt16 => checked((ushort)value),
        TagDataType.Int32 => checked((int)value),
        TagDataType.UInt32 => checked((uint)value),
        TagDataType.Float32 when Math.Abs(value) <= float.MaxValue => (float)value,
        TagDataType.Float64 when double.IsFinite(value) => value,
        _ => throw new ArgumentException("数据类型无效或数值超出范围。")
    };

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _clock.Stop();
        _writtenValues.Clear();
        return ValueTask.CompletedTask;
    }
}
