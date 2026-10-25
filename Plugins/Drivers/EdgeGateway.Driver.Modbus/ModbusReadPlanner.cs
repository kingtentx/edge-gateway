using EdgeGateway.Model;

namespace EdgeGateway.Driver.Modbus;

/// <summary>Plans only the supplied scan subset; never bridges unrequested address gaps.</summary>
internal static class ModbusReadPlanner
{
    public static IReadOnlyList<ModbusReadBatch> Build(IReadOnlyList<TagConfiguration> tags)
    {
        var points = tags.Where(tag => tag.Enabled).Select((tag, index) =>
        {
            var address = ModbusValidation.ValidateTag(tag);
            var function = tag.Area switch
            {
                ModbusArea.Coil => (byte)1, ModbusArea.DiscreteInput => (byte)2,
                ModbusArea.HoldingRegister => (byte)3, ModbusArea.InputRegister => (byte)4,
                _ => throw new ArgumentException("寄存区无效。")
            };
            return new ModbusReadPoint(tag, index, function, address,
                function <= 2 ? 1 : RegisterCodec.GetRegisterCount(tag.DataType));
        }).OrderBy(point => point.Function).ThenBy(point => point.Address).ToArray();

        var batches = new List<ModbusReadBatch>();
        var pending = new List<ModbusReadPoint>();
        var end = 0;
        foreach (var point in points)
        {
            var nextEnd = Math.Max(end, point.Address + point.Quantity);
            if (pending.Count > 0 && (point.Function != pending[0].Function || point.Address > end
                || nextEnd - pending[0].Address > (point.Function <= 2 ? 2000 : 125)))
            {
                batches.Add(new(pending[0].Function, pending[0].Address, end - pending[0].Address, pending.ToArray()));
                pending.Clear();
            }
            if (pending.Count == 0) end = point.Address + point.Quantity;
            else end = nextEnd;
            pending.Add(point);
        }
        if (pending.Count > 0)
            batches.Add(new(pending[0].Function, pending[0].Address, end - pending[0].Address, pending.ToArray()));
        return batches;
    }
}

internal sealed record ModbusReadPoint(TagConfiguration Tag, int Index, byte Function, ushort Address, int Quantity);
internal sealed record ModbusReadBatch(byte Function, ushort Address, int Quantity, IReadOnlyList<ModbusReadPoint> Points);
