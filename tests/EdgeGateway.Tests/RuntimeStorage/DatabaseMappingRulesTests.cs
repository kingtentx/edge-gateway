using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class DatabaseMappingRulesTests
{
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
