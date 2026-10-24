namespace EdgeGateway.Model;

public sealed record GatewayLogQuery(DateTimeOffset From, DateTimeOffset To,
    GatewayLogLevel MinimumLevel = GatewayLogLevel.Information, string Text = "",
    bool IncludeSamples = true, int Limit = 500, int ScanLimitMb = 32);

public sealed record GatewayLogFileEntry(DateTimeOffset Timestamp, GatewayLogLevel Level,
    string Kind, string Message, string FileName, string Json);

/// <summary>Query and rows are the executed snapshot, not the current editor draft.</summary>
public sealed record GatewayLogQueryResult(GatewayLogQuery Query, IReadOnlyList<GatewayLogFileEntry> Entries,
    long ScannedBytes, int InvalidLines, bool IsPartial, IReadOnlyList<string> Warnings);

public sealed record DatabasePreviewColumn(string Column, string DatabaseType, string Source,
    string Value, DateTimeOffset? SampleTime, string? Error);

public sealed record DatabaseWritePreview(DateTimeOffset ObservedAt, bool SchemaValid, bool RowReady,
    IReadOnlyList<DatabasePreviewColumn> Columns, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
