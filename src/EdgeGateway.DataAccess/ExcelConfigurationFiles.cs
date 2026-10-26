using System.Globalization;
using System.IO.Compression;
using ClosedXML.Excel;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

/// <summary>Bounded .xlsx exchange with literal text cells, explicit column maps and no formula evaluation.</summary>
public sealed partial class ExcelConfigurationFiles : IConfigurationWorkbookFiles
{
    public const int MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxRows = 20000;
    private sealed record Column(string Property, string Header);
    private static readonly Column[] DeviceColumns =
    [
        new(nameof(DeviceConfiguration.Id), "设备标识"),
        new(nameof(DeviceConfiguration.Name), "设备名称"),
        new(nameof(DeviceConfiguration.Group), "分组"),
        new(nameof(DeviceConfiguration.DriverId), "驱动标识"),
        new(nameof(DeviceConfiguration.Enabled), "启用"),
        new(nameof(DeviceConfiguration.PollIntervalMs), "采集周期毫秒"),
        new(nameof(DeviceConfiguration.TimeoutMs), "超时毫秒"),
        new(nameof(DeviceConfiguration.ScanMode), "扫描模式"),
        new(nameof(DeviceConfiguration.AutoDemotionEnabled), "故障降速"),
        new(nameof(DeviceConfiguration.DemotionFailureCount), "降速失败次数"),
        new(nameof(DeviceConfiguration.DemotionIntervalMs), "降速间隔毫秒"),
        new(nameof(DeviceConfiguration.Host), "主机"),
        new(nameof(DeviceConfiguration.Port), "端口"),
        new(nameof(DeviceConfiguration.UnitId), "站号"),
        new(nameof(DeviceConfiguration.SerialPortName), "串口"),
        new(nameof(DeviceConfiguration.BaudRate), "波特率"),
        new(nameof(DeviceConfiguration.DataBits), "数据位"),
        new(nameof(DeviceConfiguration.Parity), "校验位"),
        new(nameof(DeviceConfiguration.StopBits), "停止位")
    ];
    private static readonly Column[] TagColumns =
    [
        new(nameof(TagConfiguration.Id), "点位标识"),
        new(nameof(TagConfiguration.Name), "点位名称"),
        new(nameof(TagConfiguration.Address), "地址"),
        new(nameof(TagConfiguration.DataType), "数据类型"),
        new(nameof(TagConfiguration.Area), "寄存区"),
        new(nameof(TagConfiguration.ByteOrder), "字节序"),
        new(nameof(TagConfiguration.ReadMethod), "读取方法"),
        new(nameof(TagConfiguration.WriteMethod), "写入方法"),
        new(nameof(TagConfiguration.Endian), "协议字节序"),
        new(nameof(TagConfiguration.Enabled), "启用"),
        new(nameof(TagConfiguration.UploadEnabled), "上传平台"),
        new(nameof(TagConfiguration.Writable), "允许写入"),
        new(nameof(TagConfiguration.Scale), "倍率"),
        new(nameof(TagConfiguration.Offset), "偏移量"),
        new(nameof(TagConfiguration.Expression), "表达式"),
        new(nameof(TagConfiguration.ScanIntervalMs), "扫描周期毫秒"),
        new(nameof(TagConfiguration.Deadband), "上报死区"),
        new(nameof(TagConfiguration.Unit), "单位"),
        new(nameof(TagConfiguration.LowLimit), "报警下限"),
        new(nameof(TagConfiguration.HighLimit), "报警上限"),
        new(nameof(TagConfiguration.Description), "说明")
    ];
    private static readonly Column[] ChannelColumns =
    [
        new(nameof(ChannelConfiguration.Id), "通道标识"),
        new(nameof(ChannelConfiguration.Name), "通道名称"),
        new(nameof(ChannelConfiguration.InterRequestDelayMs), "请求间隔毫秒"),
        new(nameof(ChannelConfiguration.Description), "说明")
    ];
    private static Column[] PointColumns => TagColumns.Where(column => column.Property != nameof(TagConfiguration.Id)).ToArray();

    public Task<DeviceConfigurationWorkbook> ReadDevicesAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadDevices(path, cancellationToken), cancellationToken);
    public Task<IReadOnlyList<TagConfiguration>> ReadTagsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<TagConfiguration>>(() =>
        {
            using var workbook = Open(path);
            var sheet = RequiredSheet(workbook, "点位");
            var rows = ReadRows(sheet, PointColumns.Select(column => column.Header), cancellationToken);
            if (rows.Count > 2000) throw new InvalidDataException("每台设备最多导入 2000 个点位。");
            return rows.Select(row => Parse<TagConfiguration>(sheet.Name, row, PointColumns)).ToList();
        }, cancellationToken);

    public Task WriteDevicesAsync(string path, DeviceConfigurationWorkbook workbook, CancellationToken cancellationToken = default)
    {
        var captured = GatewayJson.Clone(workbook);
        return Task.Run(() =>
        {
            using var excel = new XLWorkbook();
            WriteSimpleDevices(excel, captured, cancellationToken);
            Save(excel, path, cancellationToken);
        }, cancellationToken);
    }

    public Task WriteTagsAsync(string path, IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken = default)
    {
        var captured = GatewayJson.Clone(tags);
        return Task.Run(() =>
        {
            if (captured.Count > 2000) throw new InvalidDataException("每台设备最多导出 2000 个点位。");
            using var excel = new XLWorkbook();
            WriteSheet(excel, "点位", PointColumns.Select(c => c.Header), captured.Select(tag => Values(tag, PointColumns)), cancellationToken);
            Save(excel, path, cancellationToken);
        }, cancellationToken);
    }

    private static DeviceConfigurationWorkbook ReadLegacyDevices(XLWorkbook excel, CancellationToken token)
    {
        var channels = new Dictionary<string, ChannelConfiguration>(StringComparer.Ordinal);
        foreach (var row in ReadRows(RequiredSheet(excel, "通道"), new[] { "通道键" }.Concat(ChannelColumns.Select(c => c.Header)), token))
        {
            var key = Required(row, "通道键", "通道");
            if (!channels.TryAdd(key, Parse<ChannelConfiguration>("通道", row, ChannelColumns))) Fail("通道", row.Number, "通道键", "重复。");
        }
        var devices = new Dictionary<string, DeviceConfiguration>(StringComparer.Ordinal);
        foreach (var row in ReadRows(RequiredSheet(excel, "设备"), new[] { "设备键", "通道键" }.Concat(DeviceColumns.Select(c => c.Header)), token))
        {
            var device = Parse<DeviceConfiguration>("设备", row, DeviceColumns);
            var channelKey = row.Values["通道键"];
            if (channelKey.Length > 0)
            {
                if (!channels.TryGetValue(channelKey, out var channel)) Fail("设备", row.Number, "通道键", "在通道工作表中不存在。");
                device.ChannelId = channels[channelKey].Id;
            }
            if (!devices.TryAdd(Required(row, "设备键", "设备"), device)) Fail("设备", row.Number, "设备键", "重复。");
        }
        if (devices.Count is < 1 or > 500 || channels.Count > 500) throw new InvalidDataException("设备工作簿须包含 1–500 台设备，最多 500 个通道。");
        foreach (var row in ReadRows(RequiredSheet(excel, "驱动参数"), ["设备键", "参数名", "参数值"], token))
        {
            var device = ResolveDevice(devices, row, "驱动参数");
            if (!device.Properties.TryAdd(Required(row, "参数名", "驱动参数"), row.Values["参数值"])) Fail("驱动参数", row.Number, "参数名", "同一设备参数重复。");
        }
        foreach (var row in ReadRows(RequiredSheet(excel, "点位"), new[] { "设备键" }.Concat(TagColumns.Select(c => c.Header)), token))
        {
            var device = ResolveDevice(devices, row, "点位");
            device.Tags.Add(Parse<TagConfiguration>("点位", row, TagColumns));
            if (device.Tags.Count > 2000) Fail("点位", row.Number, "设备键", "每台设备最多 2000 个点位。");
        }
        return new() { Devices = devices.Values.ToList(), Channels = channels.Values.ToList() };
    }

    private sealed record Row(int Number, Dictionary<string, string> Values);
    private static DeviceConfiguration ResolveDevice(Dictionary<string, DeviceConfiguration> devices, Row row, string sheet) =>
        devices.TryGetValue(Required(row, "设备键", sheet), out var device) ? device : throw new InvalidDataException($"{sheet} 第 {row.Number} 行：设备键在设备工作表中不存在。");
    private static string Required(Row row, string column, string sheet) => !string.IsNullOrWhiteSpace(row.Values[column])
        ? row.Values[column] : throw new InvalidDataException($"{sheet} 第 {row.Number} 行 [{column}] 不能为空。");
    private static void Fail(string sheet, int row, string column, string message) => throw new InvalidDataException($"{sheet} 第 {row} 行 [{column}] {message}");

    private static List<Row> ReadRows(IXLWorksheet sheet, IEnumerable<string> expected, CancellationToken token)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastRow > MaxRows + 1 || lastColumn > 64) throw new InvalidDataException($"{sheet.Name} 超过 20000 行或 64 列，请拆分文件。");
        var headers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i <= lastColumn; i++)
        {
            var text = CellText(sheet.Cell(1, i));
            if (text.Length > 0 && !headers.TryAdd(text, i)) Fail(sheet.Name, 1, text, "表头重复。");
        }
        var expectedHeaders = expected.ToHashSet(StringComparer.Ordinal);
        foreach (var header in headers.Keys)
            if (!expectedHeaders.Contains(header)) Fail(sheet.Name, 1, header, "不属于此类模板；请分别使用设备或点位模板。");
        foreach (var header in expectedHeaders)
            if (!headers.ContainsKey(header)) Fail(sheet.Name, 1, header, "缺少必需表头，请使用导出文件或模板。");
        var result = new List<Row>();
        for (var i = 2; i <= lastRow; i++)
        {
            token.ThrowIfCancellationRequested();
            if (sheet.Row(i).CellsUsed().All(cell => cell.IsEmpty())) continue;
            var values = headers.ToDictionary(pair => pair.Key, pair => CellText(sheet.Cell(i, pair.Value)), StringComparer.Ordinal);
            if (values.Values.All(string.IsNullOrEmpty)) continue;
            result.Add(new(i, values));
        }
        return result;
    }

    private static string CellText(IXLCell cell)
    {
        if (cell.HasFormula || cell.DataType == XLDataType.Error)
            throw new InvalidDataException($"{cell.Worksheet.Name} 单元格 {cell.Address} 包含公式或错误，请粘贴为值后导入。");
        return cell.DataType switch
        {
            XLDataType.Number => cell.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            XLDataType.Boolean => cell.GetBoolean() ? "是" : "否",
            _ => cell.GetString()
        };
    }

    private static T Parse<T>(string sheet, Row row, Column[] columns) where T : new()
    {
        var result = new T();
        foreach (var column in columns)
        {
            var property = typeof(T).GetProperty(column.Property)!;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var text = row.Values[column.Header];
            try
            {
                object? value;
                if (type == typeof(string)) value = text;
                else if (string.IsNullOrWhiteSpace(text) && Nullable.GetUnderlyingType(property.PropertyType) is not null) value = null;
                else if (type == typeof(Guid))
                {
                    if (string.IsNullOrWhiteSpace(text)) continue; // New device/channel/tag receives one stable ID during parsing.
                    value = Guid.ParseExact(text.Trim(), "D");
                    if ((Guid)value == Guid.Empty) throw new FormatException();
                }
                else if (type == typeof(bool)) value = text.Trim().ToLowerInvariant() switch
                {
                    "是" or "true" or "1" => true, "否" or "false" or "0" => false, _ => throw new FormatException()
                };
                else if (type == typeof(int)) value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                else if (type == typeof(double))
                {
                    value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (!double.IsFinite((double)value)) throw new FormatException();
                }
                else if (type.IsEnum)
                {
                    value = Enum.Parse(type, text.Trim(), true);
                    if (!Enum.IsDefined(type, value) || long.TryParse(text, out _)) throw new FormatException();
                }
                else throw new InvalidOperationException("不支持的配置字段类型。");
                property.SetValue(result, value);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                Fail(sheet, row.Number, column.Header, type == typeof(bool) ? "请填写 是/否。" : "格式无效，请按模板填写；数值不能留空。");
            }
        }
        return result;
    }

    private static IEnumerable<string> Values<T>(T item, Column[] columns) => columns.Select(column =>
    {
        var value = typeof(T).GetProperty(column.Property)!.GetValue(item);
        return value switch { null => "", bool flag => flag ? "是" : "否", double number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString()! };
    });

    private static IXLWorksheet RequiredSheet(XLWorkbook workbook, string name) => workbook.TryGetWorksheet(name, out var sheet)
        ? sheet : throw new InvalidDataException($"缺少工作表 [{name}]，请使用导出的 Excel 文件或模板。");

    private static XLWorkbook Open(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 .xlsx 工作簿，旧 .xls 格式请先另存为 .xlsx。");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxFileBytes) throw new InvalidDataException("Excel 文件不能超过 8 M，请拆分后导入。");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > 512 || zip.Entries.Sum(entry => entry.Length) > 64L * 1024 * 1024)
                throw new InvalidDataException("Excel 解压内容超过限额，请拆分文件。");
            using var content = new MemoryStream(bytes);
            return new XLWorkbook(content);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is FormatException or System.Xml.XmlException or ArgumentException)
        { throw new InvalidDataException("Excel 工作簿格式损坏，原文件未修改。", ex); }
    }

    private static void WriteSheet(XLWorkbook workbook, string name, IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows, CancellationToken token)
    {
        var sheet = workbook.AddWorksheet(name);
        var columns = headers.ToArray();
        sheet.Style.Font.FontName = "微软雅黑";
        sheet.Style.Font.FontSize = 11;
        sheet.Columns(1, columns.Length).Width = 22;
        sheet.Columns(1, columns.Length).Style.NumberFormat.Format = "@";
        for (var i = 0; i < columns.Length; i++) sheet.Cell(1, i + 1).Value = columns[i];
        var header = sheet.Range(1, 1, 1, columns.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DBEAFE");
        sheet.SheetView.FreezeRows(1);
        var rowIndex = 2;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (rowIndex > MaxRows + 1) throw new InvalidDataException($"{name} 超过 20000 行，请分批导出设备。");
            var index = 1;
            foreach (var value in row)
            {
                if (value.Length > 32767) throw new InvalidDataException($"{name} 第 {rowIndex} 行内容超过 Excel 单元格上限。");
                sheet.Cell(rowIndex, index++).Value = value; // Always a literal, including strings beginning with '='.
            }
            rowIndex++;
        }
        sheet.Range(1, 1, Math.Max(1, rowIndex - 1), columns.Length).SetAutoFilter();
    }

    private static void Save(XLWorkbook workbook, string path, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) workbook.SaveAs(output);
            if (new FileInfo(temporary).Length > MaxFileBytes) throw new InvalidDataException("导出文件超过 8 M，请分批导出。");
            using (var zip = ZipFile.OpenRead(temporary))
                if (zip.Entries.Sum(entry => entry.Length) > 64L * 1024 * 1024) throw new InvalidDataException("导出内容过大，请分批导出。");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
