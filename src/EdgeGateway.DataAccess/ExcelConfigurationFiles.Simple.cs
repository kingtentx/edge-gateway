using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

public sealed partial class ExcelConfigurationFiles
{
    private static readonly string[] InlineColumns = ["通道标识", "通道名称", "通道请求间隔毫秒", "通道说明", "驱动参数"];

    private static void WriteSimpleDevices(XLWorkbook excel, DeviceConfigurationWorkbook workbook, CancellationToken token)
    {
        var template = workbook.IsTemplate;
        WriteSheet(excel, "设备", new[] { "设备键" }.Concat(DeviceColumns.Select(c => c.Header)).Concat(InlineColumns),
            workbook.Devices.Select(device =>
            {
                var channel = device.ChannelId.HasValue ? workbook.Channels.SingleOrDefault(c => c.Id == device.ChannelId) : null;
                if (device.ChannelId.HasValue && channel is null) throw new InvalidDataException($"设备 [{device.Name}] 的通道定义缺失。");
                return new[] { template ? "" : device.Id.ToString("D") }
                    .Concat(Values(device, DeviceColumns).Select((value, index) => template && index == 0 ? "" : value))
                    .Concat(new[] { template ? "" : channel?.Id.ToString("D") ?? "", channel?.Name ?? "",
                        channel?.InterRequestDelayMs.ToString(CultureInfo.InvariantCulture) ?? "", channel?.Description ?? "",
                        string.Join("\n", device.Properties.Select(pair => EscapeParameter(pair.Key) + "=" + EscapeParameter(pair.Value))) });
            }), token);
        WriteSheet(excel, "点位", new[] { "设备键", "设备名称" }.Concat(TagColumns.Select(c => c.Header)),
            workbook.Devices.SelectMany(device => device.Tags.Select(tag =>
                new[] { template ? "" : device.Id.ToString("D"), device.Name }
                    .Concat(Values(tag, TagColumns).Select((value, index) => template && index == 0 ? "" : value)))), token);
        excel.Worksheet("设备").Cell(1, 1).GetComment().AddText("新增时键和标识留空，由系统生成。已有设备更新请保留导出的标识。");
        excel.Worksheet("点位").Cell(1, 2).GetComment().AddText("设备键留空时，按设备名称关联设备表；名称须唯一且一致。");
        var parameterColumn = 1 + DeviceColumns.Length + InlineColumns.Length;
        excel.Worksheet("设备").Cell(1, parameterColumn).GetComment().AddText("每行一个 参数名=值。空值写作 参数名=。反斜杠、换行、回车、等号分别写作 \\\\, \\n, \\r, \\=。");
        excel.Worksheet("设备").Column(parameterColumn).Width = 40;
    }

    private static DeviceConfigurationWorkbook ReadDevices(string path, CancellationToken token)
    {
        using var excel = Open(path);
        var sheet = RequiredSheet(excel, "设备");
        // Previously generated workbooks remain importable; all new exports use two sheets.
        if (sheet.Row(1).CellsUsed().Any(cell => CellText(cell) == "通道键")) return ReadLegacyDevices(excel, token);
        var result = new DeviceConfigurationWorkbook();
        var keyed = new Dictionary<string, DeviceConfiguration>(StringComparer.Ordinal);
        var channels = new Dictionary<string, ChannelConfiguration>(StringComparer.Ordinal);
        foreach (var row in ReadRows(sheet, new[] { "设备键" }.Concat(DeviceColumns.Select(c => c.Header)).Concat(InlineColumns), token))
        {
            var device = Parse<DeviceConfiguration>(sheet.Name, row, DeviceColumns);
            var key = row.Values["设备键"].Trim();
            if (key.Length > 0 && !keyed.TryAdd(key, device)) Fail(sheet.Name, row.Number, "设备键", "重复。");
            var channelId = row.Values["通道标识"].Trim();
            var channelName = row.Values["通道名称"];
            if (channelId.Length > 0 || !string.IsNullOrWhiteSpace(channelName))
            {
                Guid id;
                if (channelId.Length == 0) id = Guid.NewGuid();
                else if (!Guid.TryParseExact(channelId, "D", out id) || id == Guid.Empty)
                    throw new InvalidDataException($"设备 第 {row.Number} 行 [通道标识] 格式无效。");
                var delayText = row.Values["通道请求间隔毫秒"];
                if (!int.TryParse(delayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay) || delay < 0)
                    Fail(sheet.Name, row.Number, "通道请求间隔毫秒", "请填写非负整数。");
                var identity = channelId.Length > 0 ? "id:" + id : "name:" + channelName.Trim();
                var channel = new ChannelConfiguration { Id = id, Name = channelName, InterRequestDelayMs = delay, Description = row.Values["通道说明"] };
                if (channels.TryGetValue(identity, out var previous))
                {
                    if (previous.Name != channel.Name || previous.InterRequestDelayMs != delay || previous.Description != channel.Description)
                        Fail(sheet.Name, row.Number, "通道名称", "同一通道的参数不一致。");
                    channel = previous;
                }
                else { channels.Add(identity, channel); result.Channels.Add(channel); }
                device.ChannelId = channel.Id;
            }
            else if (row.Values["通道请求间隔毫秒"].Length > 0 || row.Values["通道说明"].Length > 0)
                Fail(sheet.Name, row.Number, "通道名称", "填写通道参数时必须填写通道名称。");
            device.Properties = ReadParameters(row.Values["驱动参数"], row.Number);
            result.Devices.Add(device);
        }
        if (result.Devices.Count is < 1 or > 500 || result.Channels.Count > 500)
            throw new InvalidDataException("设备工作簿须包含 1–500 台设备，最多 500 个通道。");
        var names = result.Devices.GroupBy(device => device.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadRows(RequiredSheet(excel, "点位"), new[] { "设备键", "设备名称" }.Concat(TagColumns.Select(c => c.Header)), token))
        {
            var key = row.Values["设备键"].Trim();
            DeviceConfiguration device;
            if (key.Length > 0)
                device = keyed.TryGetValue(key, out var found) ? found : throw new InvalidDataException($"点位 第 {row.Number} 行 [设备键] 在设备表中不存在。");
            else
            {
                var name = Required(row, "设备名称", "点位").Trim();
                if (!names.TryGetValue(name, out var matches) || matches.Length != 1)
                    throw new InvalidDataException($"点位 第 {row.Number} 行 [设备名称] 不存在或重名，请填写与设备表一致且唯一的名称。");
                device = matches[0];
            }
            device.Tags.Add(Parse<TagConfiguration>("点位", row, TagColumns));
            if (device.Tags.Count > 2000) Fail("点位", row.Number, "设备名称", "每台设备最多 2000 个点位。");
        }
        return result;
    }

    private static string EscapeParameter(string text) => text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("=", "\\=");

    private static Dictionary<string, string> ReadParameters(string text, int row)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var key = new StringBuilder(); var value = new StringBuilder(); var target = key;
            var escaped = false; var separator = false;
            foreach (var character in line)
            {
                if (escaped)
                {
                    target.Append(character switch { 'n' => '\n', 'r' => '\r', '\\' => '\\', '=' => '=',
                        _ => throw new InvalidDataException($"设备 第 {row} 行 [驱动参数] 包含无效转义。") });
                    escaped = false;
                }
                else if (character == '\\') escaped = true;
                else if (character == '=' && !separator) { separator = true; target = value; }
                else target.Append(character);
            }
            if (escaped || !separator || string.IsNullOrWhiteSpace(key.ToString()) || !result.TryAdd(key.ToString(), value.ToString()))
                throw new InvalidDataException($"设备 第 {row} 行 [驱动参数] 请按每行 参数名=值 填写，参数名不能重复。");
        }
        return result;
    }
}
