namespace EdgeGateway.Model;

/// <summary>
/// Identities affected by a durable configuration commit. Restarted sessions are scheduled,
/// not necessarily connected; Stopped excludes Restarted. Added devices remain stopped.
/// </summary>
public sealed record ConfigurationApplyResult(
    IReadOnlyList<Guid> Added,
    IReadOnlyList<Guid> Updated,
    IReadOnlyList<Guid> Removed,
    IReadOnlyList<Guid> Restarted,
    IReadOnlyList<Guid> Stopped);

public enum TagCsvImportMode { Append, UpdateByName }

public sealed record TagCsvError(int LineNumber, string Column, string Message)
{
    public string Formatted => LineNumber > 0 ? $"第 {LineNumber} 行 [{Column}]：{Message}" : $"[{Column}]：{Message}";
    public override string ToString() => Formatted;
}

public sealed record TagCsvImportResult(
    IReadOnlyList<TagConfiguration> Tags, int Added, int Updated, IReadOnlyList<TagCsvError> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public int TotalCount => Tags.Count;
}

public sealed record ExpressionPreview(bool Success, decimal? Value, string? Error);
