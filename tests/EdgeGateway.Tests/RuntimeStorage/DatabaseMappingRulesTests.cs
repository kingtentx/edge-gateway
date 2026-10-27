using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class DatabaseMappingRulesTests
{
    [Fact]
    public void TimestampAndTextMappingsUseServerClockAndKeepOriginalSampleInstant()
    {
        var instant = new DateTimeOffset(2026, 10, 26, 23, 45, 30, TimeSpan.Zero);
        var expected = instant.ToLocalTime();
        var sample = new TelemetrySample(Guid.NewGuid(), "设备", Guid.NewGuid(), "点", "0", 1, 1, SampleQuality.Good, instant, "");
        var mapping = new DatabaseColumnMapping { Column = "at", Source = DatabaseValueSource.SampleTime,
            TagId = sample.TagId, FieldType = DatabaseFieldType.Timestamp };
        var group = new DatabaseWriteGroupConfiguration { Mode = DatabaseStorageMode.ColumnMapping, Columns = [mapping] };
        DatabaseWritePreview Preview() => DatabaseWriteInspector.Inspect(new(), group,
            [new("at", "datetime", false, false, false)], new Dictionary<Guid, TelemetrySample> { [sample.TagId] = sample },
            new HashSet<Guid> { sample.TagId }, new HashSet<Guid> { sample.DeviceId }, instant.AddSeconds(1));
        var preview = Preview();
        Assert.True(preview.RowReady);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(preview.Columns).Value);
        mapping.FieldType = DatabaseFieldType.Text;
        Assert.Equal(expected.ToString("O"), Assert.Single(Preview().Columns).Value);
        mapping.Source = DatabaseValueSource.Constant; mapping.TagId = null; mapping.FieldType = DatabaseFieldType.Timestamp;
        mapping.ConstantValue = "2026-10-27T10:20:30";
        var constant = new DateTimeOffset(new DateTime(2026, 10, 27, 10, 20, 30));
        Assert.Equal(constant.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(Preview().Columns).Value);
    }

    [Theory]
    [InlineData("int", DatabaseFieldType.Int64)]
    [InlineData("INTEGER", DatabaseFieldType.Int64)]
    [InlineData("bigint unsigned", DatabaseFieldType.Int64)]
    [InlineData("NUMBER", DatabaseFieldType.Decimal)]
    [InlineData("numeric(12, 2)", DatabaseFieldType.Decimal)]
    [InlineData("character varying", DatabaseFieldType.Text)]
    [InlineData("VARCHAR2(128 CHAR)", DatabaseFieldType.Text)]
    [InlineData("timestamp without time zone", DatabaseFieldType.Timestamp)]
    [InlineData("user_defined", DatabaseFieldType.Auto)]
    public void MetadataTypesResolveWithoutInventingUnsupportedConversions(string databaseType, DatabaseFieldType expected)
        => Assert.Equal(expected, DatabaseMappingRules.InferFieldType(databaseType));

    [Theory]
    [InlineData(DatabaseFieldType.Int64)]
    [InlineData(DatabaseFieldType.Decimal)]
    [InlineData(DatabaseFieldType.Boolean)]
    public void QualityTextCannotBeConfiguredAsNumericStatus(DatabaseFieldType type)
    {
        var mapping = new DatabaseColumnMapping { Column = "_QUALITY", Source = DatabaseValueSource.Quality, FieldType = type };
        Assert.Contains("Good / Bad / Uncertain", DatabaseMappingRules.ValidateValue(mapping));
        mapping.FieldType = DatabaseFieldType.Text;
        Assert.Null(DatabaseMappingRules.ValidateValue(mapping));
    }

    [Theory]
    [InlineData("Good")]
    [InlineData("192.5")]
    [InlineData("9223372036854775808")]
    public void InvalidIntegerConstantsFailBeforeSaving(string constant)
    {
        var mapping = new DatabaseColumnMapping { Column = "_QUALITY", Source = DatabaseValueSource.Constant,
            FieldType = DatabaseMappingRules.InferFieldType("int"), ConstantValue = constant };
        Assert.Contains("常量", DatabaseMappingRules.ValidateValue(mapping));
        mapping.ConstantValue = "192";
        Assert.Null(DatabaseMappingRules.ValidateValue(mapping));
        var copy = GatewayJson.Clone(mapping);
        Assert.Equal(DatabaseFieldType.Int64, copy.FieldType);
        Assert.Equal("192", copy.ConstantValue);
    }
}
