namespace EdgeGateway.Model;

public sealed class GatewayLoggingConfiguration
{
    public GatewayLogLevel MinimumLevel { get; set; } = GatewayLogLevel.Information;
    public bool LogAcquisitionSamples { get; set; } = true;
    public int MaxFileSizeMb { get; set; } = 10;
    public int RetainedFileCount { get; set; } = 30;
    public int RetainedDays { get; set; } = 30;
    public int SampleMaxFileSizeMb { get; set; } = 10;
    public int SampleRetainedFileCount { get; set; } = 30;
    public int SampleRetainedDays { get; set; } = 30;
    public int MinimumFreeDiskSpaceMb { get; set; } = 256;
    public int DiskResumeMarginMb { get; set; } = 64;
}
public sealed record GatewayLoggingStatus(long PendingEntries, long DroppedEntries, long FailedEntries, string? FileError)
{
    public string? DirectoryPath { get; init; }
    public string? SampleDirectoryPath { get; init; }

    public long PendingBytes { get; init; }
    public long? AvailableDiskBytes { get; init; }
    public bool DiskProtectionActive { get; init; }
    public long DiskRejectedEntries { get; init; }
}

public enum ExternalDatabaseProvider { MySql, PostgreSql, Oracle }
public enum DatabaseStorageMode { PointRows, ColumnMapping, PointMapping }
public enum DatabaseValueSource
{
    EngineeringValue, RawValue, NumericValue, SampleTime, Quality, DeviceId, DeviceName,
    TagId, TagName, Address, Unit, Error, RecordTime, EventId, Constant
}
public enum DatabaseFieldType { Auto, Text, Int64, Decimal, Double, Boolean, Timestamp }

public sealed class DatabaseConnectionConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新数据库";
    public bool Enabled { get; set; }
    public ExternalDatabaseProvider Provider { get; set; } = ExternalDatabaseProvider.MySql;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "edge_gateway";
    public string Username { get; set; } = "edge_gateway";
    public string Password { get; set; } = "";
    public bool UseTls { get; set; }
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 200;
    public int FlushIntervalMs { get; set; } = 1000;
    public int QueueCapacityRows { get; set; } = 10000;
    public int QueueCapacityMb { get; set; } = 64;
    public int MaxRetryAttempts { get; set; } = 5;
}

public sealed class DatabaseWriteGroupConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "采集数据";
    public bool Enabled { get; set; }
    public Guid ConnectionId { get; set; }
    public DatabaseStorageMode Mode { get; set; } = DatabaseStorageMode.PointRows;
    public string Schema { get; set; } = "";
    public string Table { get; set; } = "gateway_point";
    // In PointRows/PointMapping an empty selection means all enabled points, independent of MQTT UploadEnabled.
    public List<Guid> TagIds { get; set; } = [];
    public List<DatabaseColumnMapping> Columns { get; set; } = [];
    // ColumnMapping emits complete, sufficiently fresh snapshots at this maximum frequency.
    public int WriteIntervalMs { get; set; } = 1000;
    public int MaxSampleAgeMs { get; set; } = 10000;
    public bool RequireGoodQuality { get; set; } = true;
}

public sealed class DatabaseColumnMapping
{
    public string Column { get; set; } = "";
    public DatabaseValueSource Source { get; set; } = DatabaseValueSource.EngineeringValue;
    public Guid? TagId { get; set; }
    public DatabaseFieldType FieldType { get; set; } = DatabaseFieldType.Auto;
    public string ConstantValue { get; set; } = "";
}

public sealed record DatabaseTableInfo(string Schema, string Name)
{
    public override string ToString() => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
}
public sealed record DatabaseColumnInfo(string Name, string DataType, bool IsNullable,
    bool HasDefault = false, bool IsGenerated = false);
public sealed record DatabaseConnectionTestResult(bool Success, string Message, string? ServerVersion = null);
public sealed record DatabaseWriteRow(string EventId, DateTimeOffset RecordedAt, IReadOnlyDictionary<string, object?> Values);
public sealed record ExternalDatabaseStatus(Guid ConnectionId, string Name, bool Enabled, long QueuedRows,
    long WrittenRows, long DroppedRows, long FailedBatches, long SkippedRows,
    DateTimeOffset? LastSuccess, string? Error, bool IsConnected)
{
    // Queue reservations include the in-flight batch until it succeeds or is discarded.
    public long QueuedBytes { get; init; }
    public long PeakQueuedRows { get; init; }
    public long RetriedBatches { get; init; }
    public long TimedOutOperations { get; init; }
    public int InFlightBatches { get; init; }
    public double LastWriteMilliseconds { get; init; }
    public DateTimeOffset? LastFailure { get; init; }
    public bool IsBackpressured { get; init; }
}

public static class DatabasePointTable
{
    public static IReadOnlyList<string> Columns { get; } =
        ["event_id", "device_id", "device_name", "tag_id", "tag_name", "address", "raw_value", "value_text", "timestamp", "quality"];
}
