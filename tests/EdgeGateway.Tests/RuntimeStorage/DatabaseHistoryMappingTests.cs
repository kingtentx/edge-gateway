using EdgeGateway.Model;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class DatabaseHistoryMappingTests
{
    [Fact]
    public void StandardLayoutAndMappedAliasesHaveDifferentValueStorage()
    {
        Assert.Null(DatabaseHistoryMapping.GetUnavailableReason(new()));
        Assert.Equal(DatabasePointTable.Columns, DatabaseHistoryMapping.Columns(new()).Take(10));
        var group = Valid();
        Assert.Null(DatabaseHistoryMapping.GetUnavailableReason(group));
        var columns = DatabaseHistoryMapping.Columns(group);
        Assert.Equal("DeviceId", columns[1]); Assert.Equal("SampleTime", columns[8]);
        Assert.Null(columns[2]); Assert.Null(columns[6]); Assert.Null(columns[10]);
        group.Columns.Single(column => column.Source == DatabaseValueSource.EngineeringValue).Source = DatabaseValueSource.NumericValue;
        Assert.Null(DatabaseHistoryMapping.GetUnavailableReason(group));
        Assert.Equal("EngineeringValue", DatabaseHistoryMapping.Columns(group)[7]);
    }

    [Theory]
    [InlineData(DatabaseValueSource.DeviceId, "设备标识")]
    [InlineData(DatabaseValueSource.TagId, "点位标识")]
    [InlineData(DatabaseValueSource.SampleTime, "采样时间")]
    [InlineData(DatabaseValueSource.EngineeringValue, "工程值")]
    [InlineData(DatabaseValueSource.Quality, "质量")]
    public void MissingSemanticSourceExplainsWhyHistoryIsUnavailable(DatabaseValueSource source, string label)
    {
        var group = Valid(); group.Columns.Single(column => column.Source == source).Source = DatabaseValueSource.Constant;
        Assert.Contains(label, DatabaseHistoryMapping.GetUnavailableReason(group));
        Assert.Throws<ArgumentException>(() => DatabaseHistoryMapping.Columns(group));
    }

    [Fact]
    public void AmbiguousSourcesLossyTypesAndWholeRowMappingsAreRejected()
    {
        var group = Valid();
        group.Columns.Add(new() { Column = "second_device", Source = DatabaseValueSource.DeviceId });
        Assert.Contains("多个字段", DatabaseHistoryMapping.GetUnavailableReason(group));
        group.Columns.RemoveAt(group.Columns.Count - 1);
        group.Columns[0].FieldType = DatabaseFieldType.Double;
        Assert.Contains("文本类型", DatabaseHistoryMapping.GetUnavailableReason(group));
        group.Mode = DatabaseStorageMode.ColumnMapping;
        Assert.Contains("多个点位", DatabaseHistoryMapping.GetUnavailableReason(group));
    }

    private static DatabaseWriteGroupConfiguration Valid() => new()
    {
        Mode = DatabaseStorageMode.PointMapping,
        Columns = new[] { DatabaseValueSource.DeviceId, DatabaseValueSource.TagId, DatabaseValueSource.SampleTime,
            DatabaseValueSource.EngineeringValue, DatabaseValueSource.Quality }
            .Select(source => new DatabaseColumnMapping { Column = source.ToString(), Source = source }).ToList()
    };
}
