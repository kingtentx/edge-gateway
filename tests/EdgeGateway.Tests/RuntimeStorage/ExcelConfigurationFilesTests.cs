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
            Properties = new() { ["地址"] = "000001234567890123456789", ["literal"] = "=1+1", ["空值"] = "", ["特殊=参数"] = "C:\\Folder\n下一行\r结束" }, Tags = [Tag()]
        };
        var second = new DeviceConfiguration { Name = "设备二", ChannelId = channel.Id, Tags = [new() { Name = "另一点" }] };
        var input = new DeviceConfigurationWorkbook { Channels = [channel], Devices = [first, second] };
        await _files.WriteDevicesAsync(FilePath, input);
        var actual = await _files.ReadDevicesAsync(FilePath);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(input), System.Text.Json.JsonSerializer.Serialize(actual));
        using var excel = new XLWorkbook(FilePath);
        Assert.Equal(new[] { "设备", "点位" }, excel.Worksheets.Select(sheet => sheet.Name));
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

    [Fact]
    public async Task MissingDeviceKeyDoesNotSilentlyFallBackToName()
    {
        await _files.WriteDevicesAsync(FilePath, new() { Devices = [new() { Tags = [Tag()] }] });
        Edit(excel => Cell(excel.Worksheet("点位"), "设备键").Value = "missing");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _files.ReadDevicesAsync(FilePath));
        Assert.Contains("第 2 行", error.Message);
    }

    [Fact]
    public async Task TemplateLeavesAllKeysAndIdsBlankAndAssociatesMultipleDevicesByName()
    {
        var channel = new ChannelConfiguration { Name = "共享通道" };
        var input = new DeviceConfigurationWorkbook { IsTemplate = true, Channels = [channel], Devices = [
            new() { Name = "设备一", ChannelId = channel.Id, Tags = [Tag()] },
            new() { Name = "设备二", ChannelId = channel.Id, Tags = [Tag()] }] };
        await _files.WriteDevicesAsync(FilePath, input);
        using (var excel = new XLWorkbook(FilePath))
        {
            Assert.Equal(2, excel.Worksheets.Count);
            foreach (var sheet in excel.Worksheets)
                foreach (var header in sheet.Row(1).CellsUsed().Where(cell => cell.GetString().EndsWith("键") || cell.GetString().EndsWith("标识") && cell.GetString() != "驱动标识"))
                    Assert.All(sheet.Column(header.Address.ColumnNumber).CellsUsed().Where(cell => cell.Address.RowNumber > 1), cell => Assert.True(cell.IsEmpty()));
        }
        var actual = await _files.ReadDevicesAsync(FilePath);
        Assert.Equal(2, actual.Devices.Count);
        Assert.Single(actual.Channels);
        Assert.All(actual.Devices, device => Assert.Equal(actual.Channels[0].Id, device.ChannelId));
        Assert.All(actual.Devices, device => Assert.Single(device.Tags));
        Assert.DoesNotContain(actual.Devices[0].Id, input.Devices.Select(device => device.Id));
        Assert.NotEqual(actual.Devices[0].Tags[0].Id, actual.Devices[1].Tags[0].Id);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    public async Task BlankKeyRequiresMatchingDeviceName(string name)
    {
        await _files.WriteDevicesAsync(FilePath, new() { IsTemplate = true, Devices = [new() { Tags = [Tag()] }] });
        Edit(excel => Cell(excel.Worksheet("点位"), "设备名称").Value = name);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _files.ReadDevicesAsync(FilePath));
        Assert.Contains("设备名称", error.Message);
    }

    [Fact]
    public async Task AmbiguousNameWithBlankKeysIsRejected()
    {
        await _files.WriteDevicesAsync(FilePath, new() { IsTemplate = true, Devices = [new() { Name = "同名", Tags = [Tag()] }, new() { Name = "同名" }] });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _files.ReadDevicesAsync(FilePath));
        Assert.Contains("重名", error.Message);
    }

    [Fact]
    public async Task StandalonePointsHaveOnlyOneSheet()
    {
        await _files.WriteTagsAsync(FilePath, [Tag()]);
        using var excel = new XLWorkbook(FilePath);
        Assert.Equal("点位", Assert.Single(excel.Worksheets).Name);
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
