using System.Globalization;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>Read-only schema and live-value preview. No SQL, DDL or synthetic samples.</summary>
public static class DatabaseWriteInspector
{
    public static DatabaseWritePreview Inspect(DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group,
        IReadOnlyList<DatabaseColumnInfo> metadata, IReadOnlyDictionary<Guid, TelemetrySample> samples,
        IReadOnlySet<Guid> enabledTags, IReadOnlySet<Guid> activeDevices, DateTimeOffset now)
    {
        var errors = new List<string>();
        var warnings = new List<string>
        {
            "只读预览不写入数据，不证明 INSERT 权限、唯一键、触发器或 precision/scale 一定满足；实际写入仍由数据库事务校验。",
            "预览是当前时刻快照，不重放本次预览值；自定义整行模式运行时会重新检查点位活跃状态、质量和时效。"
        };
        if (!connection.Enabled || !group.Enabled) warnings.Add("连接或写入组未启用；本预览不会改变启用状态。");
        var columns = metadata.ToDictionary(column => column.Name, StringComparer.Ordinal);
        string Target(string column) => group.Mode == DatabaseStorageMode.PointRows && connection.Provider == ExternalDatabaseProvider.Oracle
            ? column.ToUpperInvariant() : column;
        var targets = (group.Mode == DatabaseStorageMode.PointRows ? DatabasePointTable.Columns : group.Columns.Select(column => column.Column))
            .Select(Target).ToHashSet(StringComparer.Ordinal);
        if (metadata.Count == 0) errors.Add("目标表不存在、不可见或没有可读取的字段元数据。");
        foreach (var target in targets)
        {
            if (!columns.TryGetValue(target, out var column)) errors.Add($"目标字段 [{target}] 不存在（按实际标识符大小写检查）。");
            else if (column.IsGenerated) errors.Add($"目标字段 [{target}] 由数据库生成，不能直接绑定写入。");
        }
        foreach (var column in metadata.Where(column => !targets.Contains(column.Name) && !column.IsNullable && !column.HasDefault && !column.IsGenerated))
            errors.Add($"必填字段 [{column.Name}] 没有映射、默认值或自动生成规则。");
        var schemaValid = errors.Count == 0;
        var preview = new List<DatabasePreviewColumn>();
        string? CheckSample(TelemetrySample? sample, Guid? tag)
        {
            if (!tag.HasValue || !enabledTags.Contains(tag.Value)) return "关联点位不存在或未启用。";
            if (sample is null) return "尚无真实采样，不使用模拟值填充预览。";
            if (!activeDevices.Contains(sample.DeviceId)) return "设备已停止，缓存值不作为可写的新采样。";
            if (now - sample.Timestamp > TimeSpan.FromMilliseconds(group.MaxSampleAgeMs) || sample.Timestamp - now > TimeSpan.FromSeconds(5))
                return "采样已过期或源时间超前。";
            if (group.Mode != DatabaseStorageMode.PointRows && group.RequireGoodQuality && sample.Quality != SampleQuality.Good)
                return "采样质量不满足 Good 要求。";
            return null;
        }
        void Add(string target, string source, object? value, TelemetrySample? sample, string? error)
        {
            columns.TryGetValue(target, out var column);
            if (error is null && column is not null && !column.IsNullable && (value is null ||
                connection.Provider == ExternalDatabaseProvider.Oracle && value is string { Length: 0 }))
                error = "目标为非空字段，但映射结果为 NULL（Oracle 空字符串也按 NULL 处理）。";
            if (error is not null) errors.Add($"字段 [{target}]：{error}");
            var text = value is null ? "NULL" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            preview.Add(new(target, column?.DataType ?? "未发现", source, text.Length <= 2000 ? text : text[..2000] + "…（仅显示截断）", sample?.Timestamp, error));
        }
        if (group.Mode == DatabaseStorageMode.PointRows)
        {
            var sample = samples.Values.Where(sample => enabledTags.Contains(sample.TagId) && (group.TagIds.Count == 0 || group.TagIds.Contains(sample.TagId))
                    && activeDevices.Contains(sample.DeviceId)).OrderByDescending(sample => sample.Timestamp).FirstOrDefault();
            var error = sample is null ? "没有可用于预览的活跃点位采样。" : CheckSample(sample, sample.TagId);
            var row = sample is not null && error is null && SamplePayloadBudget.TryEstimate(sample, out _) ? DatabaseRowMapper.PointRow(sample) : null;
            if (sample is not null && error is null && row is null) error = "单条采样超出载荷限制。";
            foreach (var name in DatabasePointTable.Columns) Add(Target(name), "标准点行 / " + name, row?.Values.GetValueOrDefault(name), sample, error);
            warnings.Add("标准点行仅预览最近一个符合条件的点位，不代表全部点位已就绪；运行时保留实际 Bad/Uncertain 质量。");
        }
        else
        {
            var eventId = Guid.NewGuid().ToString("N");
            var currentPoint = group.Mode == DatabaseStorageMode.PointMapping
                ? samples.Values.Where(sample => enabledTags.Contains(sample.TagId)
                    && (group.TagIds.Count == 0 || group.TagIds.Contains(sample.TagId)) && activeDevices.Contains(sample.DeviceId))
                    .OrderByDescending(sample => sample.Timestamp).FirstOrDefault() : null;
            if (group.Mode == DatabaseStorageMode.PointMapping)
                warnings.Add("自定义逐点仅预览最近一个选中点位；运行时每个选中点位各生成一行，共用字段映射。常量状态值为用户业务值，不代表真实采样质量。");
            foreach (var mapping in group.Columns)
            {
                var requiresSample = mapping.Source is not (DatabaseValueSource.Constant or DatabaseValueSource.RecordTime or DatabaseValueSource.EventId);
                var sample = !requiresSample ? null : group.Mode == DatabaseStorageMode.PointMapping
                    ? currentPoint : mapping.TagId.HasValue ? samples.GetValueOrDefault(mapping.TagId.Value) : null;
                var error = requiresSample ? CheckSample(sample, group.Mode == DatabaseStorageMode.PointMapping ? sample?.TagId : mapping.TagId) : null;
                object? value = null;
                if (error is null)
                {
                    try
                    {
                        if (sample is not null && !SamplePayloadBudget.TryEstimate(sample, out _)) throw new InvalidDataException("单条采样超出载荷限制。");
                        value = DatabaseRowMapper.ResolveField(mapping, sample, eventId, now);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
                    { error = exception.Message; }
                }
                Add(mapping.Column, mapping.Source + " → " + mapping.FieldType, value, sample, error);
            }
        }
        return new(now, schemaValid, schemaValid && errors.Count == 0, preview, errors.Distinct().ToArray(), warnings);
    }
}
