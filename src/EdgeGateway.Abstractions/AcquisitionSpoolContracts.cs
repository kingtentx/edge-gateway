using EdgeGateway.Model;

namespace EdgeGateway.Abstractions;

/// <summary>
/// Durable write-ahead journal for acquisition rows destined for external databases.
/// Persisted rows are retained until the target database write is acknowledged.
/// </summary>
public interface IAcquisitionSpool : IAsyncDisposable
{
    Task<string> AppendAsync(Guid groupId, IReadOnlyList<DatabaseWriteRow> rows, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DurableAcquisitionBatch>> ReadPendingAsync(Guid groupId, int maxBatches = 32, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(Guid groupId, string batchId, CancellationToken cancellationToken = default);
    Task<AcquisitionSpoolStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

public sealed record DurableAcquisitionBatch(Guid GroupId, string BatchId, DateTimeOffset CreatedAt,
    IReadOnlyList<DatabaseWriteRow> Rows, long PayloadBytes);
public sealed record AcquisitionSpoolStatistics(long PendingBatches, long PendingRows, long PendingBytes,
    long AcknowledgedBatches, long CapacityRejectedBatches, string? LastError);
