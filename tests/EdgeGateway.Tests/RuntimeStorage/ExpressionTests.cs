using System.Globalization;
using System.Text.Json;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class NumericExpressionTests
{
    [Theory]
    [InlineData("2+3*4", "0", "14")]
    [InlineData("(2+3)*4", "0", "20")]
    [InlineData("x*2+1", "1.5", "4")]
    [InlineData("-2^2", "0", "-4")]
    [InlineData("2^3^2", "0", "512")]
    [InlineData("2^-2", "0", "0.25")]
    [InlineData("pow(x,3)", "2", "8")]
    [InlineData("10%3", "0", "1")]
    [InlineData("round(x,2)", "1.235", "1.24")]
    [InlineData("round(x)", "-1.5", "-2")]
    [InlineData("floor(x)", "-1.1", "-2")]
    [InlineData("ceil(x)", "-1.1", "-1")]
    [InlineData("truncate(x)", "-1.9", "-1")]
    [InlineData("clamp(x,0,100)", "-5", "0")]
    [InlineData("clamp(x,0,100)", "105", "100")]
    [InlineData("max(abs(x),min(5,3))", "-4", "4")]
    [InlineData("1e2 + X * .5", "2", "101")]
    [InlineData("", "18446744073709551615", "18446744073709551615")]
    [InlineData("x+1", "18446744073709551614", "18446744073709551615")]
    [InlineData("x-1", "-9223372036854775807", "-9223372036854775808")]
    public void EvaluatesDocumentedLanguageWithDecimalPrecision(string expression, string input, string expected)
    {
        Assert.Null(NumericExpression.Validate(expression));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            NumericExpression.Evaluate(expression, decimal.Parse(input, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText('x')")]
    [InlineData("x.GetType()")]
    [InlineData("while(true){}")]
    [InlineData("x=3")]
    [InlineData("[1,2]")]
    [InlineData("y+1")]
    [InlineData("sin(x)")]
    [InlineData("1,5")]
    [InlineData("NaN")]
    [InlineData("1e999")]
    [InlineData("1e-100")]
    [InlineData("(x+1")]
    [InlineData("2x")]
    [InlineData(".")]
    [InlineData("abs()")]
    [InlineData("min(x)")]
    [InlineData("min(x,1,2)")]
    [InlineData("x/0")]
    [InlineData("x+(1/0)")]
    [InlineData("x+1e28*10")]
    [InlineData("round(x,29)")]
    [InlineData("pow(x,0.5)")]
    [InlineData("pow(x,29)")]
    [InlineData("clamp(x,2,1)")]
    public void RejectsUnknownCodeMalformedSyntaxAndImpossibleConstantArguments(string expression)
    {
        Assert.False(string.IsNullOrEmpty(NumericExpression.Validate(expression)));
        Assert.Throws<InvalidDataException>(() => NumericExpression.Evaluate(expression, 2));
    }

    [Fact]
    public void DynamicDomainErrorsAreReportedWithoutRejectingValidInputDomain()
    {
        Assert.Null(NumericExpression.Validate("x/(x-2)"));
        Assert.Equal(2, NumericExpression.Evaluate("x/(x-2)", 4));
        Assert.Throws<InvalidDataException>(() => NumericExpression.Evaluate("x/(x-2)", 2));
        Assert.Throws<InvalidDataException>(() => NumericExpression.Evaluate("pow(2,x)", 29));
        Assert.Throws<InvalidDataException>(() => NumericExpression.Evaluate("round(2,x)", 0.5m));
        Assert.Throws<InvalidDataException>(() => NumericExpression.Evaluate("clamp(2,x,3)", 4));
        Assert.Throws<InvalidDataException>(() => NumericExpression.Evaluate("x*10", decimal.MaxValue));
        Assert.Throws<InvalidDataException>(() => NumericExpression.Evaluate("x/10", 0.0000000000000000000000000001m));
    }

    [Fact]
    public void LimitsInputLengthRecursionAndBalancedNodeCount()
    {
        Assert.NotNull(NumericExpression.Validate(new string(' ', NumericExpression.MaximumLength + 1)));
        Assert.NotNull(NumericExpression.Validate(new string('-', NumericExpression.MaximumDepth + 1) + "x"));
        Assert.NotNull(NumericExpression.Validate(new string('(', NumericExpression.MaximumDepth + 1) + "x" +
            new string(')', NumericExpression.MaximumDepth + 1)));
        Assert.NotNull(NumericExpression.Validate(string.Join('+', Enumerable.Repeat("x", 40))));
        Assert.NotNull(NumericExpression.Validate(BalancedSum(65))); // 129 nodes; shallow tree, under 512 characters.
        Assert.Equal(64, NumericExpression.Evaluate(BalancedSum(64), 1));
    }

    [Fact]
    public void PreviewUsesInvariantInputsAndReportsTheSameErrorsAsAcquisition()
    {
        var successful = NumericExpression.Preview("x+0.5", "1.5");
        Assert.True(successful.Success);
        Assert.Equal(2, successful.Value);
        Assert.Null(successful.Error);
        Assert.False(NumericExpression.Preview("x+0.5", "1,5").Success);
        var failed = NumericExpression.Preview("x/(x-2)", "2");
        Assert.False(failed.Success);
        Assert.Null(failed.Value);
        Assert.Contains("除数", failed.Error);
    }

    [Theory]
    [InlineData("1e-40")]
    [InlineData("-1e-40")]
    [InlineData("5E-324")]
    [InlineData("0.0000000000000000000000000000000000000001")]
    public void PreviewRejectsNonzeroEngineeringInputUnderflowInsteadOfReturningZero(string input)
    {
        var preview = NumericExpression.Preview("x", input);
        Assert.False(preview.Success);
        Assert.Null(preview.Value);
        Assert.Contains("非零工程输入", preview.Error);
    }

    [Theory]
    [InlineData("0e-40")]
    [InlineData("-0E-40")]
    [InlineData("0.0000000000000000000000000000000000000000")]
    public void PreviewAcceptsAnActualZeroEvenWhenItsExponentIsBelowDecimalRange(string input)
    {
        var preview = NumericExpression.Preview("x", input);
        Assert.True(preview.Success, preview.Error);
        Assert.Equal(0m, preview.Value);
        Assert.Null(preview.Error);
    }

    [Fact]
    public async Task RepeatedConfigurationEditsAndConcurrentEvaluationsKeepCorrectCompiledExpressions()
    {
        var results = await Task.WhenAll(Enumerable.Range(1, NumericExpression.MaximumCachedExpressions * 2)
            .Select(index => Task.Run(() => (Index: index, Value: NumericExpression.Evaluate($"x+{index}", 7)))));
        Assert.All(results, result => Assert.Equal(result.Index + 7, result.Value));
        Assert.Equal(8, NumericExpression.Evaluate("x+1", 7));
    }

    private static string BalancedSum(int count) => count == 1 ? "x" :
        "(" + BalancedSum(count / 2) + "+" + BalancedSum(count - count / 2) + ")";
}

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task ExpressionRunsAfterEngineeringScaleAndFeedsHistoryWithRawValueUnchanged()
    {
        await using var harness = new Harness();
        var tag = harness.Config.Devices[0].Tags[0];
        tag.Scale = 2;
        tag.Offset = 10;
        tag.Expression = "x^2+1";
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Store.Samples.Any(sample => sample.Quality == SampleQuality.Good));
        var sample = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(2d, sample.RawValue);
        Assert.Equal(197m, Assert.IsType<decimal>(sample.Value));
        Assert.Contains(harness.Store.Samples, stored => stored.Value is decimal value && value == 197m);
    }

    [Fact]
    public async Task ExpressionKeepsUInt64PrecisionWithoutConvertingThroughDouble()
    {
        await using var harness = new Harness();
        harness.Factory.Value = ulong.MaxValue;
        var tag = harness.Config.Devices[0].Tags[0];
        tag.DataType = TagDataType.UInt64;
        tag.Expression = "x-1";
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count > 0);
        var sample = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(sample.RawValue));
        Assert.Equal(18446744073709551614m, Assert.IsType<decimal>(sample.Value));
        Assert.Equal(SampleQuality.Good, sample.Quality);
    }

    [Fact]
    public async Task ExpressionFailureBecomesBadAndRecoversOnlyOnARealSuccessfulSample()
    {
        await using var harness = new Harness();
        harness.Config.Devices[0].Tags[0].Expression = "x/(x-2)";
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count > 0);
        var failed = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(SampleQuality.Bad, failed.Quality);
        Assert.Null(failed.Value);
        Assert.Null(failed.RawValue);
        Assert.Contains("计算表达式", failed.Error);
        harness.Factory.Value = 4d;
        await Until(() => harness.Runtime.GetLatest().Any(sample => sample.Quality == SampleQuality.Good));
        var recovered = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(2m, Assert.IsType<decimal>(recovered.Value));
        Assert.True(recovered.Timestamp > failed.Timestamp);
    }

    [Theory]
    [InlineData(1e-40)]
    [InlineData(-1e-40)]
    [InlineData(1e-300)]
    public async Task NonzeroExpressionInputUnderflowProducesBadQualityAndOnlyRealZeroCanRecover(double input)
    {
        await using var harness = new Harness();
        harness.Factory.Value = input;
        harness.Config.Devices[0].Tags[0].Expression = "x";
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count > 0);
        var failed = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(SampleQuality.Bad, failed.Quality);
        Assert.Null(failed.Value);
        Assert.Null(failed.RawValue);
        Assert.Contains("非零工程输入", failed.Error);
        harness.Factory.Value = 0d;
        await Until(() => harness.Runtime.GetLatest().Any(sample => sample.Quality == SampleQuality.Good));
        var recovered = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(0m, Assert.IsType<decimal>(recovered.Value));
        Assert.Equal(0d, Assert.IsType<double>(recovered.RawValue));
        Assert.True(recovered.Timestamp > failed.Timestamp);
    }

    [Fact]
    public async Task NumericTypeBatchCannotPretendToBeAnExpressionScalar()
    {
        await using var harness = new Harness();
        harness.Factory.Value = JsonSerializer.SerializeToElement(new[] { 1.25f, 2.5f });
        harness.Config.Devices[0].Tags[0].DataType = TagDataType.Float32;
        harness.Config.Devices[0].Tags[0].Expression = "x*2";
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count > 0);
        var sample = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(SampleQuality.Bad, sample.Quality);
        Assert.Null(sample.Value);
        Assert.Contains("结构化", sample.Error);
    }

    [Fact]
    public async Task ValidatorRejectsInvalidExpressionReadTypesAndInverseWriteAssumptions()
    {
        await using var harness = new Harness();
        var tag = harness.Config.Devices[0].Tags[0];
        tag.Expression = "x.GetType()";
        Assert.Throws<ArgumentException>(() => harness.Runtime.Configure(harness.Config));
        tag.Expression = "x+1";
        tag.DataType = TagDataType.Utf8String;
        Assert.Throws<ArgumentException>(() => harness.Runtime.Configure(harness.Config));
        tag.DataType = TagDataType.Float64;
        tag.Writable = true;
        Assert.Throws<ArgumentException>(() => harness.Runtime.Configure(harness.Config));
    }

    [Fact]
    public void OldTagJsonDefaultsToNoExpression()
    {
        var tag = JsonSerializer.Deserialize<TagConfiguration>("{\"Name\":\"旧点位\",\"DataType\":2}")!;
        Assert.Equal("", tag.Expression);
    }
}
