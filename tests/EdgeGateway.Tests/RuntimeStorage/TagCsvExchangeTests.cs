using System.Globalization;
using System.Text;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class TagCsvExchangeTests
{
    [Fact]
    public void ExportIncludesBomAndEveryBusinessFieldRoundTripsWithoutReusingIdentity()
    {
        var source = new TagConfiguration
        {
            Name = "温度,\"出水\"\n北区", Address = "DB1.DBD0", DataType = TagDataType.Float64,
            Area = ModbusArea.InputRegister, ByteOrder = RegisterByteOrder.DCBA,
            ReadMethod = "读取,\"方法\"\r\n下一行", WriteMethod = "写入方法", Endian = ProtocolEndian.LittleEndianSwap,
            Enabled = false, UploadEnabled = false, Writable = false, Scale = 0.125, Offset = -3.75,
            Unit = "°C", LowLimit = -40.25, HighLimit = 120.75, Description = "中文说明\r第二行\n第三行\r\n\"末行\"",
            Expression = "round(x,2)", ScanIntervalMs = 500, Deadband = 0.25
        };
        var bytes = TagCsvExchange.Export([source]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Contains("\"\"出水\"\"", Encoding.UTF8.GetString(bytes));
        var result = TagCsvExchange.Import(bytes, []);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        var imported = Assert.Single(result.Tags);
        AssertBusinessFieldsEqual(source, imported);
        Assert.NotEqual(source.Id, imported.Id);
        Assert.NotEqual(Guid.Empty, imported.Id);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.TotalCount);

        var modelColumns = typeof(TagConfiguration).GetProperties().Select(property => property.Name)
            .Where(name => name != nameof(TagConfiguration.Id)).OrderBy(name => name).ToArray();
        Assert.Equal(modelColumns, TagCsvExchange.Columns.OrderBy(name => name).ToArray());
    }

    [Fact]
    public void HeaderOnlyTemplateIsAValidDetachedNoOp()
    {
        var source = new TagConfiguration { Name = "原点位" };
        var template = TagCsvExchange.Export([]);
        var result = TagCsvExchange.Import(template, [source]);
        Assert.True(result.IsValid);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        var retained = Assert.Single(result.Tags);
        Assert.Equal(source.Id, retained.Id);
        Assert.NotSame(source, retained);
        retained.Description = "候选变化";
        Assert.Equal("", source.Description);
        Assert.True(TagCsvExchange.Import(template, []).IsValid);
    }

    [Fact]
    public void AppendCreatesFreshIdentitiesForEveryDestinationAndPreservesExistingObjects()
    {
        var exported = new TagConfiguration { Name = "新增点位", UploadEnabled = false };
        var existing = new TagConfiguration { Name = "原点位", Address = "7" };
        var content = TagCsvExchange.Export([exported]);
        var first = TagCsvExchange.Import(content, [existing]);
        var second = TagCsvExchange.Import(content, []);
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(existing.Id, first.Tags[0].Id);
        Assert.NotSame(existing, first.Tags[0]);
        Assert.NotEqual(exported.Id, first.Tags[1].Id);
        Assert.NotEqual(first.Tags[1].Id, second.Tags[0].Id);
        first.Tags[0].Address = "changed";
        Assert.Equal("7", existing.Address);
        Assert.False(first.Tags[1].UploadEnabled);
    }

    [Fact]
    public void UpdateByNameIsExplicitCaseInsensitiveRetainsIdsAndDoesNotDeleteAbsentTags()
    {
        var existing = new TagConfiguration { Name = "Pump", Unit = "旧单位" };
        var retained = new TagConfiguration { Name = "未出现点位", Description = "保留" };
        var replacement = new TagConfiguration { Name = "pUmP", Unit = "新单位", UploadEnabled = false };
        var added = new TagConfiguration { Name = "新名称" };
        var content = TagCsvExchange.Export([replacement, added]);
        var append = TagCsvExchange.Import(content, [existing, retained]);
        Assert.False(append.IsValid);
        Assert.Equal(0, append.Added);
        Assert.Equal(0, append.Updated);
        Assert.Equal(2, append.TotalCount);
        Assert.Equal("旧单位", append.Tags[0].Unit);

        var update = TagCsvExchange.Import(content, [existing, retained], TagCsvImportMode.UpdateByName);
        Assert.True(update.IsValid);
        Assert.Equal(1, update.Added);
        Assert.Equal(1, update.Updated);
        Assert.Equal(3, update.TotalCount);
        Assert.Equal(existing.Id, update.Tags[0].Id);
        Assert.Equal("新单位", update.Tags[0].Unit);
        Assert.False(update.Tags[0].UploadEnabled);
        Assert.Equal(retained.Id, update.Tags[1].Id);
        AssertBusinessFieldsEqual(retained, update.Tags[1]);
        Assert.NotEqual(added.Id, update.Tags[2].Id);
        Assert.Equal("旧单位", existing.Unit);
    }

    [Fact]
    public void NumericRoundTripUsesInvariantCultureAndPreservesNullLimits()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var source = new TagConfiguration
            {
                Name = "温度", Scale = 1.2345678901234567, Offset = -5.5E-17,
                LowLimit = null, HighLimit = 25.75
            };
            var content = TagCsvExchange.Export([source]);
            Assert.Contains("1.2345678901234567", Encoding.UTF8.GetString(content));
            var result = TagCsvExchange.Import(content, []);
            Assert.True(result.IsValid);
            AssertBusinessFieldsEqual(source, Assert.Single(result.Tags));
            var commaNumber = TagCsvExchange.Import(Csv(Row(("Scale", "1,25"))), []);
            Assert.False(commaNumber.IsValid);
            Assert.Contains(commaNumber.Errors, error => error.Column == "Scale" && error.LineNumber == 2);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+CMD")]
    [InlineData("-CMD")]
    [InlineData("@SUM(A1)")]
    [InlineData("  =1+1")]
    [InlineData("\t正文")]
    [InlineData("\r正文")]
    [InlineData("\n正文")]
    [InlineData("'正文")]
    [InlineData("'=1+1")]
    [InlineData("''正文")]
    public void SpreadsheetFormulaPrefixesAndLiteralApostrophesAreEscapedReversibly(string value)
    {
        var source = new TagConfiguration
        {
            Name = value, Address = value, ReadMethod = value, WriteMethod = value, Unit = value, Description = value
        };
        var content = TagCsvExchange.Export([source]);
        Assert.Contains("'" + value, Encoding.UTF8.GetString(content));
        var result = TagCsvExchange.Import(content, []);
        Assert.True(result.IsValid);
        AssertBusinessFieldsEqual(source, Assert.Single(result.Tags));
    }

    [Fact]
    public void HeadersCanBeReorderedCaseInsensitivelyAndBooleanDigitsAreAccepted()
    {
        var values = Defaults();
        values["Name"] = "空格保留 ";
        values["DataType"] = "float64";
        values["Enabled"] = "0";
        values["UploadEnabled"] = "1";
        var names = TagCsvExchange.Columns.Reverse().ToArray();
        var csv = string.Join(',', names.Select(name => " " + name.ToLowerInvariant() + " ")) + "\n" +
                  string.Join(',', names.Select(name => Quote(values[name]))) + "\n";
        var result = TagCsvExchange.Import(Encoding.UTF8.GetBytes(csv), []);
        Assert.True(result.IsValid);
        var tag = Assert.Single(result.Tags);
        Assert.Equal("空格保留 ", tag.Name);
        Assert.Equal(TagDataType.Float64, tag.DataType);
        Assert.False(tag.Enabled);
        Assert.True(tag.UploadEnabled);
    }

    [Fact]
    public void ErrorsAreCollectedWithPhysicalRecordStartLinesAndNoRowsArePartiallyImported()
    {
        var existing = new TagConfiguration { Name = "保留", Address = "原地址" };
        var content = Csv(
            Row(("Name", "错误一"), ("DataType", "unknown"), ("Enabled", "yes"), ("Scale", "n/a"), ("Description", "第一行\r\n第二行")),
            Row(("Name", ""), ("LowLimit", "Infinity")),
            Row(("Name", "有效但不应导入")));
        var result = TagCsvExchange.Import(content, [existing]);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.LineNumber == 2 && error.Column == "DataType");
        Assert.Contains(result.Errors, error => error.LineNumber == 2 && error.Column == "Enabled");
        Assert.Contains(result.Errors, error => error.LineNumber == 2 && error.Column == "Scale");
        Assert.Contains(result.Errors, error => error.LineNumber == 4 && error.Column == "LowLimit");
        Assert.Contains(result.Errors, error => error.LineNumber == 4 && error.Column == "Name");
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(existing.Id, Assert.Single(result.Tags).Id);
        Assert.NotSame(existing, result.Tags[0]);
        Assert.Contains("第 4 行", result.Errors.First(error => error.LineNumber == 4).Formatted);
        Assert.All(result.Errors, error => Assert.Equal(error.Formatted, error.ToString()));
    }

    [Fact]
    public void DuplicateMissingAndUnknownHeadersAreRejectedTogether()
    {
        var headers = TagCsvExchange.Columns.ToArray();
        headers[0] = "Address";
        headers[Array.IndexOf(headers, "Description")] = "Id";
        var csv = string.Join(',', headers) + "\r\n" + Row();
        var result = TagCsvExchange.Import(Encoding.UTF8.GetBytes(csv), []);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Column == "Address" && error.Message.Contains("重复"));
        Assert.Contains(result.Errors, error => error.Column == "Name" && error.Message.Contains("缺少"));
        Assert.Contains(result.Errors, error => error.Column == "Description" && error.Message.Contains("缺少"));
        Assert.Contains(result.Errors, error => error.Column == "Id" && error.Message.Contains("未知"));
        Assert.All(result.Errors, error => Assert.Equal(1, error.LineNumber));
    }

    [Fact]
    public void DuplicateNamesWithinCsvAreRejectedEvenInUpdateMode()
    {
        var existing = new TagConfiguration { Name = "Same", Address = "原始" };
        var result = TagCsvExchange.Import(Csv(Row(("Name", "same")), Row(("Name", "SAME"))), [existing], TagCsvImportMode.UpdateByName);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.LineNumber == 3 && error.Column == "Name");
        Assert.Equal("原始", Assert.Single(result.Tags).Address);
        Assert.Equal(0, result.Updated);
    }

    [Theory]
    [InlineData("DataType", "1")]
    [InlineData("Area", "99")]
    [InlineData("ByteOrder", "ABDC")]
    [InlineData("Endian", "255")]
    [InlineData("Enabled", "是")]
    [InlineData("UploadEnabled", "yes")]
    [InlineData("Writable", "on")]
    [InlineData("Scale", "NaN")]
    [InlineData("Offset", "Infinity")]
    [InlineData("LowLimit", "-Infinity")]
    [InlineData("HighLimit", "1e999")]
    [InlineData("Expression", "x.GetType()")]
    [InlineData("ScanIntervalMs", "")]
    [InlineData("ScanIntervalMs", "1.5")]
    [InlineData("ScanIntervalMs", "50")]
    [InlineData("ScanIntervalMs", "3600001")]
    [InlineData("Deadband", "")]
    [InlineData("Deadband", "NaN")]
    [InlineData("Deadband", "-1")]
    public void InvalidTypedCellsDoNotFallBackSilently(string column, string value)
    {
        var result = TagCsvExchange.Import(Csv(Row((column, value))), []);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.LineNumber == 2 && error.Column == column);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void GenericTagRulesAreValidatedWithoutOpeningAnyDriver()
    {
        var result = TagCsvExchange.Import(Csv(
            Row(("Name", "写入倍率"), ("Writable", "true"), ("Scale", "0")),
            Row(("Name", "布尔倍率"), ("DataType", "Boolean"), ("Scale", "2")),
            Row(("Name", "阈值"), ("LowLimit", "30"), ("HighLimit", "20"))), []);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.LineNumber == 2 && error.Column == "Scale");
        Assert.Contains(result.Errors, error => error.LineNumber == 3 && error.Column == "DataType");
        Assert.Contains(result.Errors, error => error.LineNumber == 4 && error.Column == "LowLimit");
    }

    [Theory]
    [InlineData("\"未闭合")]
    [InlineData("未引用\"字段")]
    [InlineData("\"字段\"多余字符")]
    public void MalformedQuotedRecordsFailWithoutReturningPartialCandidates(string invalidRow)
    {
        var result = TagCsvExchange.Import(Csv(invalidRow), []);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.LineNumber == 2);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void WrongColumnCountInvalidUtf8AndMissingHeaderAreReported()
    {
        var missingCell = TagCsvExchange.Import(Csv("one,two"), []);
        Assert.Contains(missingCell.Errors, error => error.LineNumber == 2 && error.Column == "列数");
        var invalidUtf8 = TagCsvExchange.Import([0xEF, 0xBB, 0xBF, 0xC3, 0x28], []);
        Assert.Contains(invalidUtf8.Errors, error => error.Column == "编码");
        var missingHeader = TagCsvExchange.Import([], []);
        Assert.Contains(missingHeader.Errors, error => error.LineNumber == 1 && error.Column == "表头");
    }

    [Fact]
    public void FileSizeIsBoundedOnBothImportAndExport()
    {
        var result = TagCsvExchange.Import(new byte[TagCsvExchange.MaxFileBytes + 1], []);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Column == "文件");
        Assert.Throws<InvalidDataException>(() => TagCsvExchange.Export([
            new TagConfiguration { Description = new string('x', TagCsvExchange.MaxFileBytes) }
        ]));
    }

    [Fact]
    public void DataRowAndCombinedDeviceLimitsAreEnforcedButUpdatesAtCapacitySucceed()
    {
        var rows = Enumerable.Range(0, TagCsvExchange.MaxDataRows + 1).Select(index => Row(("Name", "点" + index))).ToArray();
        var overflow = TagCsvExchange.Import(Csv(rows), []);
        Assert.False(overflow.IsValid);
        Assert.Contains(overflow.Errors, error => error.Column == "行数" && error.LineNumber == TagCsvExchange.MaxDataRows + 2);
        var existing = Enumerable.Range(0, TagCsvExchange.MaxTagsPerDevice)
            .Select(index => new TagConfiguration { Name = "点" + index }).ToArray();
        var combined = TagCsvExchange.Import(Csv(Row(("Name", "额外"))), existing);
        Assert.False(combined.IsValid);
        Assert.Contains(combined.Errors, error => error.LineNumber == 2 && error.Column == "点位数");
        Assert.Equal(existing.Length, combined.TotalCount);
        var update = TagCsvExchange.Import(Csv(Row(("Name", "点0"), ("UploadEnabled", "false"))), existing, TagCsvImportMode.UpdateByName);
        Assert.True(update.IsValid);
        Assert.Equal(1, update.Updated);
        Assert.Equal(existing.Length, update.TotalCount);
        Assert.Equal(existing[0].Id, update.Tags[0].Id);
        Assert.False(update.Tags[0].UploadEnabled);
        Assert.True(existing[0].UploadEnabled);
        Assert.Throws<InvalidDataException>(() => TagCsvExchange.Export(System.Linq.Enumerable.Append(existing, new TagConfiguration { Name = "额外" })));
    }

    [Fact]
    public void LegacySeventeenColumnsPreserveMatchedExtensionSettingsAndDefaultNewTags()
    {
        var existing = new TagConfiguration
        { Name = "Pump", Expression = "x+1", ScanIntervalMs = 1500, Deadband = 0.125, Description = "旧说明" };
        var source = Defaults();
        source["Name"] = "pump";
        source["Description"] = "新说明";
        var appended = Defaults();
        appended["Name"] = "New";
        var columns = TagCsvExchange.Columns.Where(column => column is not "Expression" and not "ScanIntervalMs" and not "Deadband").ToArray();
        Assert.Equal(17, columns.Length);
        var content = CsvWithColumns(columns, source, appended);
        var result = TagCsvExchange.Import(content, [existing], TagCsvImportMode.UpdateByName);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Added);
        var changed = result.Tags[0];
        Assert.Equal(existing.Id, changed.Id);
        Assert.Equal("新说明", changed.Description);
        Assert.Equal("x+1", changed.Expression);
        Assert.Equal(1500, changed.ScanIntervalMs);
        Assert.Equal(0.125, changed.Deadband);
        Assert.Equal("", result.Tags[1].Expression);
        Assert.Equal(0, result.Tags[1].ScanIntervalMs);
        Assert.Equal(0, result.Tags[1].Deadband);
        Assert.Equal("旧说明", existing.Description);
        changed.Expression = "x+2";
        Assert.Equal("x+1", existing.Expression);
    }

    [Fact]
    public void PresentExtensionsExplicitlyClearOrReplaceWithoutResettingOmittedExtensions()
    {
        var existing = new TagConfiguration { Name = "点位", Expression = "x+1", ScanIntervalMs = 1500, Deadband = 2 };
        var values = Defaults();
        values["Expression"] = "";
        values["Deadband"] = "0";
        var columns = TagCsvExchange.Columns.Where(column => column != "ScanIntervalMs").ToArray();
        var result = TagCsvExchange.Import(CsvWithColumns(columns, values), [existing], TagCsvImportMode.UpdateByName);
        Assert.True(result.IsValid);
        var changed = Assert.Single(result.Tags);
        Assert.Equal("", changed.Expression);
        Assert.Equal(1500, changed.ScanIntervalMs);
        Assert.Equal(0, changed.Deadband);
    }

    [Fact]
    public void InheritedExpressionsCannotBypassChangedTypeAndWritableValidation()
    {
        var existing = new TagConfiguration { Name = "点位", Expression = "x+1", ScanIntervalMs = 1500, Deadband = 2 };
        var values = Defaults();
        values["DataType"] = "Boolean";
        values["Writable"] = "true";
        var columns = TagCsvExchange.Columns.Where(column => column is not "Expression" and not "ScanIntervalMs" and not "Deadband").ToArray();
        var result = TagCsvExchange.Import(CsvWithColumns(columns, values), [existing], TagCsvImportMode.UpdateByName);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Column == "Expression");
        Assert.Contains(result.Errors, error => error.Column == "Deadband");
        Assert.Contains(result.Errors, error => error.Column == "Writable");
        var retained = Assert.Single(result.Tags);
        AssertBusinessFieldsEqual(existing, retained);
        Assert.Equal(existing.Id, retained.Id);
    }

    [Fact]
    public void ExpressionFormulaPrefixAndFunctionCommasRoundTrip()
    {
        var source = new TagConfiguration { Expression = "-min(x,3)", ScanIntervalMs = 100, Deadband = 1 };
        var content = TagCsvExchange.Export([source]);
        Assert.Contains("\"'-min(x,3)\"", Encoding.UTF8.GetString(content));
        var result = TagCsvExchange.Import(content, []);
        Assert.True(result.IsValid);
        AssertBusinessFieldsEqual(source, Assert.Single(result.Tags));
    }

    private static byte[] CsvWithColumns(IReadOnlyList<string> columns, params Dictionary<string, string>[] rows) =>
        Encoding.UTF8.GetBytes(string.Join(',', columns) + "\r\n" +
            string.Join("\r\n", rows.Select(row => string.Join(',', columns.Select(column => Quote(row[column]))))) + "\r\n");

    private static void AssertBusinessFieldsEqual(TagConfiguration expected, TagConfiguration actual)
    {
        foreach (var property in typeof(TagConfiguration).GetProperties().Where(property => property.Name != nameof(TagConfiguration.Id)))
            Assert.True(Equals(property.GetValue(expected), property.GetValue(actual)), $"字段 {property.Name} 未保留。");
    }

    private static Dictionary<string, string> Defaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = "点位", ["Address"] = "0", ["DataType"] = "UInt16", ["Area"] = "HoldingRegister",
        ["ByteOrder"] = "ABCD", ["ReadMethod"] = "", ["WriteMethod"] = "", ["Endian"] = "FromByteOrder",
        ["Enabled"] = "true", ["UploadEnabled"] = "true", ["Writable"] = "false", ["Scale"] = "1",
        ["Offset"] = "0", ["Unit"] = "", ["LowLimit"] = "", ["HighLimit"] = "", ["Description"] = "",
        ["Expression"] = "", ["ScanIntervalMs"] = "0", ["Deadband"] = "0"
    };

    private static string Row(params (string Column, string Value)[] changes)
    {
        var values = Defaults();
        foreach (var (column, value) in changes) values[column] = value;
        return string.Join(',', TagCsvExchange.Columns.Select(column => Quote(values[column])));
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static byte[] Csv(params string[] rows) => Encoding.UTF8.GetBytes(
        string.Join(',', TagCsvExchange.Columns) + "\r\n" + string.Join("\r\n", rows) + "\r\n");
}
