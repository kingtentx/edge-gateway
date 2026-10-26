using ClosedXML.Excel;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class ExcelConfigurationFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edge-excel-test-" + Guid.NewGuid().ToString("N"));
    private readonly ExcelConfigurationFiles _files = new();
    public ExcelConfigurationFilesTests() => Directory.CreateDirectory(_directory);
    private string FilePath => Path.Combine(_directory, "配置.xlsx");

    [Fact]
    public async Task DeviceWorkbookRoundTripsAllSettingsAndSharedChannel()
    {
        var channel = new ChannelConfiguration { Name = "车间总线", InterRequestDelayMs = 31, Description = "共享通道" };
        var first = new DeviceConfiguration
        {
            Name = "设备一", Group = "分组A", DriverId = "test-driver", ChannelId = channel.Id, Enabled = false,
            PollIntervalMs = 123, TimeoutMs = 567, ScanMode = DeviceScanMode.TagRate, AutoDemotionEnabled = true,
            DemotionFailureCount = 7, DemotionIntervalMs = 45678, Host = "192.0.2.1", Port = 1502, UnitId = 3,
            SerialPortName = "COM17", BaudRate = 19200, DataBits = 7, Parity = "Even", StopBits = "Two",
            Properties = new() { ["地址"] = "000001234567890123456789", ["literal"] = "=1+1" }, Tags = [Tag()]
        };
        var second = new DeviceConfiguration { Name = "设备二", ChannelId = channel.Id, Tags = [new() { Name = "另一点" }] };
        var input = new DeviceConfigurationWorkbook { Channels = [channel], Devices = [first, second] };
        await _files.WriteDevicesAsync(FilePath, input);
        var actual = await _files.ReadDevicesAsync(FilePath);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(input), System.Text.Json.JsonSerializer.Serialize(actual));
        using var excel = new XLWorkbook(FilePath);
        Assert.Equal(5, excel.Worksheets.Count);
        Assert.All(excel.Worksheets.SelectMany(sheet => sheet.CellsUsed()), cell => Assert.False(cell.HasFormula));
    }

    [Fact]
    public async Task PointsRoundTripUnicodeLeadingZerosLongIntegersAndFormulaLikeText()
    {
        var tag = Tag();
        tag.Address = "00009223372036854775807";
        tag.Description = "=SUM(A1:A2)\n中文,\"原样\"";
        await _files.WriteTagsAsync(FilePath, [tag]);
        var actual = Assert.Single(await _files.ReadTagsAsync(FilePath));
        Assert.NotEqual(tag.Id, actual.Id);
        actual.Id = tag.Id;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(tag), System.Text.Json.JsonSerializer.Serialize(actual));
    }

    [Theory]
    [InlineData("倍率", "abc")]
    [InlineData("倍率", "NaN")]
    [InlineData("倍率", "Infinity")]
    [InlineData("启用", "maybe")]
    [InlineData("数据类型", "999")]
    public async Task InvalidCellReportsSheetRowAndColumn(string column, string value)
    {
        await _files.WriteTagsAsync(FilePath, [Tag()]);
        Edit(excel => Cell(excel.Worksheet("点位"), column).Value = value);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _files.ReadTagsAsync(FilePath));
        Assert.Contains("点位 第 2 行 [" + column + "]", error.Message);
    }

    [Fact]
    public async Task FormulaIsRejectedWithoutEvaluation()
    {
        await _files.WriteTagsAsync(FilePath, [Tag()]);
        Edit(excel => Cell(excel.Worksheet("点位"), "倍率").FormulaA1 = "1+1");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _files.ReadTagsAsync(FilePath));
        Assert.Contains("公式", error.Message);
    }

    [Fact]
    public async Task DeviceWorkbookCannotAccidentallyImportAllItsPointsIntoOneDevice()
    {
        await _files.WriteDevicesAsync(FilePath, new() { Devices = [new() { Tags = [Tag()] }] });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _files.ReadTagsAsync(FilePath));
        Assert.Contains("设备键", error.Message);
    }

    [Theory]
    [InlineData("设备", "通道键")]
    [InlineData("点位", "设备键")]
    [InlineData("驱动参数", "设备键")]
    public async Task MissingCrossSheetReferencesAreRejected(string sheet, string column)
    {
        await _files.WriteDevicesAsync(FilePath, new() { Devices = [new() { Tags = [Tag()], Properties = new() { ["x"] = "y" } }] });
        Edit(excel => Cell(excel.Worksheet(sheet), column).Value = "missing");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _files.ReadDevicesAsync(FilePath));
        Assert.Contains("第 2 行", error.Message);
    }

    [Fact]
    public async Task BlankIdsReceiveStableIdsAndResolveChannelByWorkbookKey()
    {
        var channel = new ChannelConfiguration();
        await _files.WriteDevicesAsync(FilePath, new() { Channels = [channel], Devices = [new() { ChannelId = channel.Id, Tags = [Tag()] }] });
        Edit(excel =>
        {
            Cell(excel.Worksheet("设备"), "设备标识").Clear();
            Cell(excel.Worksheet("通道"), "通道标识").Clear();
            Cell(excel.Worksheet("点位"), "点位标识").Clear();
        });
        var actual = await _files.ReadDevicesAsync(FilePath);
        Assert.NotEqual(channel.Id, actual.Channels[0].Id);
        Assert.Equal(actual.Channels[0].Id, actual.Devices[0].ChannelId);
        Assert.NotEqual(Guid.Empty, actual.Devices[0].Tags[0].Id);
    }

    [Fact]
    public async Task OversizeAndCancelledExportLeaveOriginalFileIntact()
    {
        await _files.WriteTagsAsync(FilePath, [Tag()]);
        var original = await File.ReadAllBytesAsync(FilePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _files.WriteTagsAsync(FilePath, [], new(true)));
        await Assert.ThrowsAsync<InvalidDataException>(() => _files.WriteTagsAsync(FilePath, Enumerable.Range(0, 2001).Select(_ => Tag()).ToList()));
        Assert.Equal(original, await File.ReadAllBytesAsync(FilePath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task OversizeInputIsRejectedBeforeWorkbookParsing()
    {
        using (var stream = File.Create(FilePath)) stream.SetLength(ExcelConfigurationFiles.MaxFileBytes + 1L);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _files.ReadDevicesAsync(FilePath));
        Assert.Contains("8 M", error.Message);
    }

    private static TagConfiguration Tag() => new()
    {
        Name = "温度一", Address = "00001", DataType = TagDataType.Float64, Area = ModbusArea.InputRegister,
        ByteOrder = RegisterByteOrder.DCBA, ReadMethod = "ReadDouble", WriteMethod = "Write", Endian = ProtocolEndian.LittleEndian,
        Enabled = false, UploadEnabled = false, Writable = true, Scale = 1.25, Offset = -2.5, Expression = "x*2",
        ScanIntervalMs = 234, Deadband = 0.02, Unit = "℃", LowLimit = -30, HighLimit = 180, Description = "中文\n第二行"
    };
    private static IXLCell Cell(IXLWorksheet sheet, string header) => sheet.Cell(2,
        sheet.Row(1).CellsUsed().Single(cell => cell.GetString() == header).Address.ColumnNumber);
    private void Edit(Action<XLWorkbook> edit)
    {
        using var excel = new XLWorkbook(FilePath);
        edit(excel);
        excel.Save();
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
