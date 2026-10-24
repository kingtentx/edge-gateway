using System.Globalization;
using System.Text;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>
/// Pure, all-or-nothing tag exchange. Import returns a detached complete candidate and never saves or applies it.
/// The caller must validate that candidate against the selected driver and the complete gateway before saving.
/// </summary>
/// <remarks>
/// The UTF-8 CSV schema contains every TagConfiguration property except Id. Append creates new identities;
/// UpdateByName matches names with OrdinalIgnoreCase, preserves matched identities and appends new names.
/// The original 17 columns remain required. Expression, ScanIntervalMs and Deadband are optional
/// extensions; omitted extensions retain matched values in UpdateByName, or defaults for new tags.
/// Present empty Expression clears it; present numeric extensions require explicit valid numbers.
/// Column order is arbitrary, and unknown/duplicate columns are rejected.
/// Export includes a BOM and uses CRLF records; import also accepts UTF-8 without a BOM and LF/CR records.
/// Enum cells use names, booleans use true/false (import also accepts 1/0), numbers use invariant culture,
/// and an empty limit means null. Empty physical lines are ignored. LineNumber is a record's physical start.
/// Text cells beginning with an apostrophe, a leading tab/newline, or a formula prefix (=,+,-,@) after
/// whitespace receive one leading apostrophe. Import removes one apostrophe only before these escaped
/// forms. Thus exported text, including original leading apostrophes, round-trips without executing as
/// an Excel formula. Handwritten literal apostrophes before these prefixes must likewise be doubled.
/// Excel itself may reformat edited numbers/text; this convention guarantees our export/import round trip.
/// </remarks>
public static class TagCsvExchange
{
    public const int MaxFileBytes = 8 * 1024 * 1024;
    // Keep this consistent with ConfigurationValidator's per-device tag limit.
    public const int MaxTagsPerDevice = 2000;
    public const int MaxDataRows = MaxTagsPerDevice;
    private const int MaxColumns = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RequiredColumns =
    [
        nameof(TagConfiguration.Name), nameof(TagConfiguration.Address), nameof(TagConfiguration.DataType),
        nameof(TagConfiguration.Area), nameof(TagConfiguration.ByteOrder), nameof(TagConfiguration.ReadMethod),
        nameof(TagConfiguration.WriteMethod), nameof(TagConfiguration.Endian), nameof(TagConfiguration.Enabled),
        nameof(TagConfiguration.UploadEnabled), nameof(TagConfiguration.Writable), nameof(TagConfiguration.Scale),
        nameof(TagConfiguration.Offset), nameof(TagConfiguration.Unit), nameof(TagConfiguration.LowLimit),
        nameof(TagConfiguration.HighLimit), nameof(TagConfiguration.Description)
    ];

    private static readonly string[] ExtensionColumns =
    [nameof(TagConfiguration.Expression), nameof(TagConfiguration.ScanIntervalMs), nameof(TagConfiguration.Deadband)];

    public static IReadOnlyList<string> Columns { get; } = Array.AsReadOnly<string>([.. RequiredColumns, .. ExtensionColumns]);

    public static byte[] Export(IEnumerable<TagConfiguration> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var output = new StringBuilder();
        var byteCount = 3; // UTF-8 BOM is part of the file-size limit.
        AppendRecord(Columns);
        var count = 0;
        foreach (var tag in tags)
        {
            if (++count > MaxDataRows) throw new InvalidDataException($"CSV 最多导出 {MaxDataRows} 个点位。");
            ArgumentNullException.ThrowIfNull(tag);
            AppendRecord([
                EscapeText(tag.Name), EscapeText(tag.Address), EnumText(tag.DataType), EnumText(tag.Area),
                EnumText(tag.ByteOrder), EscapeText(tag.ReadMethod), EscapeText(tag.WriteMethod), EnumText(tag.Endian),
                BoolText(tag.Enabled), BoolText(tag.UploadEnabled), BoolText(tag.Writable), NumberText(tag.Scale),
                NumberText(tag.Offset), EscapeText(tag.Unit), NullableNumberText(tag.LowLimit),
                NullableNumberText(tag.HighLimit), EscapeText(tag.Description), EscapeText(tag.Expression),
                tag.ScanIntervalMs.ToString(CultureInfo.InvariantCulture), NumberText(tag.Deadband)
            ]);
        }
        return [0xEF, 0xBB, 0xBF, .. StrictUtf8.GetBytes(output.ToString())];

        void AppendRecord(IEnumerable<string> cells)
        {
            var record = string.Join(',', cells.Select(Quote)) + "\r\n";
            byteCount += StrictUtf8.GetByteCount(record);
            if (byteCount > MaxFileBytes) throw new InvalidDataException($"CSV 文件不能超过 {MaxFileBytes / 1024 / 1024} MiB。");
            output.Append(record);
        }
    }

    public static TagCsvImportResult Import(byte[] content, IReadOnlyList<TagConfiguration> existingTags,
        TagCsvImportMode mode = TagCsvImportMode.Append)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(existingTags);
        var original = existingTags.Select(Clone).ToArray();
        var errors = new List<TagCsvError>();
        TagCsvImportResult Failed() => new(original, 0, 0, errors.ToArray());

        if (!Enum.IsDefined(mode))
        {
            errors.Add(new(0, "导入模式", "请选择追加或按名称更新。"));
            return Failed();
        }
        if (content.Length > MaxFileBytes)
        {
            errors.Add(new(1, "文件", $"CSV 文件不能超过 {MaxFileBytes / 1024 / 1024} MiB。"));
            return Failed();
        }
        string text;
        try { text = StrictUtf8.GetString(content); }
        catch (DecoderFallbackException)
        {
            errors.Add(new(1, "编码", "文件不是有效的 UTF-8，请另存为 CSV UTF-8 后重试。"));
            return Failed();
        }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var records = ReadRecords(text, errors);
        if (records.Count == 0)
        {
            if (errors.Count == 0) errors.Add(new(1, "表头", "缺少表头，请使用点位 CSV 模板。"));
            return Failed();
        }

        var header = records[0];
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headerErrorCount = errors.Count;
        for (var index = 0; index < header.Cells.Count; index++)
        {
            var name = header.Cells[index].Trim();
            if (!columns.TryAdd(name, index)) errors.Add(new(header.LineNumber, name, "表头重复。"));
            if (!Columns.Contains(name, StringComparer.OrdinalIgnoreCase))
                errors.Add(new(header.LineNumber, string.IsNullOrEmpty(name) ? $"列 {index + 1}" : name,
                    "未知列；请使用模板列名，Id 不参与导入。"));
        }
        foreach (var name in RequiredColumns)
            if (!columns.ContainsKey(name)) errors.Add(new(header.LineNumber, name, "缺少必需列。"));
        if (errors.Count != headerErrorCount) return Failed();

        var existingByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < original.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(original[index].Name) || !existingByName.TryAdd(original[index].Name, index))
                errors.Add(new(0, nameof(TagConfiguration.Name), "已有点位的名称为空或重复，请先修复设备配置。"));
        }
        if (original.Length > MaxTagsPerDevice)
            errors.Add(new(0, "点位数", $"每台设备最多支持 {MaxTagsPerDevice} 个点位。"));

        var imported = new List<(TagConfiguration Tag, int LineNumber)>();
        var importedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Skip(1))
        {
            if (record.Cells.Count != header.Cells.Count)
            {
                errors.Add(new(record.LineNumber, "列数", $"应为 {header.Cells.Count} 列，实际为 {record.Cells.Count} 列。"));
                continue;
            }
            var name = UnescapeText(record.Cells[columns[nameof(TagConfiguration.Name)]]);
            var matched = mode == TagCsvImportMode.UpdateByName && existingByName.TryGetValue(name, out var matchedIndex)
                ? original[matchedIndex] : null;
            var tag = ReadTag(record, columns, errors, matched);
            if (!string.IsNullOrWhiteSpace(tag.Name) && !importedNames.TryAdd(tag.Name, record.LineNumber))
                errors.Add(new(record.LineNumber, nameof(TagConfiguration.Name), $"名称与第 {importedNames[tag.Name]} 行重复。"));
            imported.Add((tag, record.LineNumber));
        }

        var added = 0;
        var updated = 0;
        var limitReported = original.Length > MaxTagsPerDevice;
        foreach (var (tag, lineNumber) in imported)
        {
            if (existingByName.ContainsKey(tag.Name))
            {
                if (mode == TagCsvImportMode.Append)
                    errors.Add(new(lineNumber, nameof(TagConfiguration.Name), "名称已存在；请改名，或显式选择按名称更新。"));
                else updated++;
            }
            else added++;
            if (!limitReported && original.Length + added > MaxTagsPerDevice)
            {
                errors.Add(new(lineNumber, "点位数", $"导入后超过每台设备 {MaxTagsPerDevice} 个点位的上限。"));
                limitReported = true;
            }
        }
        if (errors.Count != 0) return Failed();

        var candidate = original.ToList();
        var identities = new HashSet<Guid>(original.Select(tag => tag.Id));
        foreach (var (tag, _) in imported)
        {
            if (mode == TagCsvImportMode.UpdateByName && existingByName.TryGetValue(tag.Name, out var index))
            {
                tag.Id = original[index].Id;
                candidate[index] = tag;
            }
            else
            {
                do { tag.Id = Guid.NewGuid(); } while (!identities.Add(tag.Id));
                candidate.Add(tag);
            }
        }
        return new(candidate.ToArray(), added, updated, Array.Empty<TagCsvError>());
    }

    private static TagConfiguration ReadTag(CsvRecord record, IReadOnlyDictionary<string, int> columns,
        List<TagCsvError> errors, TagConfiguration? matched)
    {
        var initialErrors = errors.Count;
        string Cell(string name) => record.Cells[columns[name]];
        string Text(string name) => UnescapeText(Cell(name));
        void Error(string column, string message) => errors.Add(new(record.LineNumber, column, message));
        T EnumValue<T>(string name) where T : struct, Enum
        {
            var value = Cell(name).Trim();
            if (Enum.GetNames<T>().Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) &&
                Enum.TryParse<T>(value, true, out var parsed)) return parsed;
            Error(name, $"无效枚举值；允许：{string.Join(", ", Enum.GetNames<T>())}。");
            return default;
        }
        bool Boolean(string name)
        {
            var value = Cell(name).Trim();
            if (bool.TryParse(value, out var parsed)) return parsed;
            if (value is "1" or "0") return value == "1";
            Error(name, "必须为 true / false 或 1 / 0。");
            return false;
        }
        double Number(string name)
        {
            if (double.TryParse(Cell(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
                return parsed;
            Error(name, "必须为有限数值，使用小数点 .，不使用千位分隔符。");
            return 0;
        }
        double? OptionalNumber(string name) => string.IsNullOrWhiteSpace(Cell(name)) ? null : Number(name);
        int Integer(string name)
        {
            if (int.TryParse(Cell(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            Error(name, "必须为整数毫秒数，0 表示使用设备采集周期。");
            return 0;
        }

        var tag = new TagConfiguration
        {
            Name = Text(nameof(TagConfiguration.Name)), Address = Text(nameof(TagConfiguration.Address)),
            DataType = EnumValue<TagDataType>(nameof(TagConfiguration.DataType)),
            Area = EnumValue<ModbusArea>(nameof(TagConfiguration.Area)),
            ByteOrder = EnumValue<RegisterByteOrder>(nameof(TagConfiguration.ByteOrder)),
            ReadMethod = Text(nameof(TagConfiguration.ReadMethod)), WriteMethod = Text(nameof(TagConfiguration.WriteMethod)),
            Endian = EnumValue<ProtocolEndian>(nameof(TagConfiguration.Endian)),
            Enabled = Boolean(nameof(TagConfiguration.Enabled)), UploadEnabled = Boolean(nameof(TagConfiguration.UploadEnabled)),
            Writable = Boolean(nameof(TagConfiguration.Writable)), Scale = Number(nameof(TagConfiguration.Scale)),
            Offset = Number(nameof(TagConfiguration.Offset)), Unit = Text(nameof(TagConfiguration.Unit)),
            LowLimit = OptionalNumber(nameof(TagConfiguration.LowLimit)), HighLimit = OptionalNumber(nameof(TagConfiguration.HighLimit)),
            Description = Text(nameof(TagConfiguration.Description)),
            Expression = columns.ContainsKey(nameof(TagConfiguration.Expression)) ? Text(nameof(TagConfiguration.Expression)) : matched?.Expression ?? "",
            ScanIntervalMs = columns.ContainsKey(nameof(TagConfiguration.ScanIntervalMs)) ? Integer(nameof(TagConfiguration.ScanIntervalMs)) : matched?.ScanIntervalMs ?? 0,
            Deadband = columns.ContainsKey(nameof(TagConfiguration.Deadband)) ? Number(nameof(TagConfiguration.Deadband)) : matched?.Deadband ?? 0
        };
        if (string.IsNullOrWhiteSpace(tag.Name)) Error(nameof(TagConfiguration.Name), "点位名称不能为空。");
        // Driver-specific addresses, methods and capability checks belong to full configuration validation.
        if (errors.Count == initialErrors)
        {
            if (tag.Writable && tag.Scale == 0) Error(nameof(TagConfiguration.Scale), "允许写入时倍率不能为 0。");
            if (!TagDataTypes.IsNumeric(tag.DataType) && (tag.Scale != 1 || tag.Offset != 0 || tag.LowLimit.HasValue || tag.HighLimit.HasValue))
                Error(nameof(TagConfiguration.DataType), "非数值类型的倍率须为 1、偏移须为 0，且不能配置数值阈值。");
            if (tag.LowLimit.HasValue && tag.HighLimit.HasValue && tag.LowLimit > tag.HighLimit)
                Error(nameof(TagConfiguration.LowLimit), "报警下限不能高于上限。");
            if (tag.ScanIntervalMs != 0 && tag.ScanIntervalMs is < 100 or > 3_600_000)
                Error(nameof(TagConfiguration.ScanIntervalMs), "须为 0（设备周期）或 100–3600000 ms。");
            if (tag.Deadband < 0 || !TagDataTypes.IsNumeric(tag.DataType) && tag.Deadband != 0)
                Error(nameof(TagConfiguration.Deadband), "死区须为非负有限数；非数值点位的死区须为 0。");
            if (!string.IsNullOrWhiteSpace(tag.Expression))
            {
                if (!TagDataTypes.IsNumeric(tag.DataType)) Error(nameof(TagConfiguration.Expression), "计算表达式仅支持数值点位。");
                if (tag.Writable) Error(nameof(TagConfiguration.Writable), "配置计算表达式的点位不能写入，无法安全反算原始值。");
            }
            if (NumericExpression.Validate(tag.Expression) is string expressionError)
                Error(nameof(TagConfiguration.Expression), expressionError);
        }
        return tag;
    }

    private sealed record CsvRecord(int LineNumber, IReadOnlyList<string> Cells);

    private static List<CsvRecord> ReadRecords(string text, List<TagCsvError> errors)
    {
        var records = new List<CsvRecord>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var quoted = false;
        var closedQuote = false;
        var hasContent = false;
        var position = 0;

        bool FinishCell()
        {
            if (cells.Count >= MaxColumns)
            {
                errors.Add(new(recordLine, "列数", $"单行列数超过 {MaxColumns}，请使用点位 CSV 模板。"));
                return false;
            }
            cells.Add(cell.ToString());
            cell.Clear();
            closedQuote = false;
            return true;
        }
        bool FinishRecord()
        {
            if (!hasContent && cells.Count == 0 && cell.Length == 0) return true;
            if (!FinishCell()) return false;
            if (records.Count >= MaxDataRows + 1)
            {
                errors.Add(new(recordLine, "行数", $"CSV 最多包含 {MaxDataRows} 行点位数据（不含表头）。"));
                return false;
            }
            records.Add(new(recordLine, cells.ToArray()));
            cells.Clear();
            hasContent = false;
            return true;
        }

        while (position < text.Length)
        {
            var character = text[position++];
            if (quoted)
            {
                if (character == '"')
                {
                    if (position < text.Length && text[position] == '"') { cell.Append('"'); position++; }
                    else { quoted = false; closedQuote = true; }
                }
                else
                {
                    cell.Append(character);
                    if (character == '\r' && position < text.Length && text[position] == '\n') cell.Append(text[position++]);
                    if (character is '\r' or '\n') line++;
                }
                continue;
            }
            if (character == ',')
            {
                hasContent = true;
                if (!FinishCell()) return records;
            }
            else if (character is '\r' or '\n')
            {
                if (!FinishRecord()) return records;
                if (character == '\r' && position < text.Length && text[position] == '\n') position++;
                line++;
                recordLine = line;
                closedQuote = false;
            }
            else if (closedQuote)
            {
                errors.Add(new(recordLine, $"列 {cells.Count + 1}", "结束引号后只能是逗号或换行。"));
                return records;
            }
            else if (character == '"')
            {
                if (cell.Length != 0)
                {
                    errors.Add(new(recordLine, $"列 {cells.Count + 1}", "引号必须位于字段开头，字段中的引号应写成两个双引号。"));
                    return records;
                }
                quoted = true;
                hasContent = true;
            }
            else
            {
                cell.Append(character);
                hasContent = true;
            }
        }
        if (quoted) errors.Add(new(recordLine, $"列 {cells.Count + 1}", "带引号的字段未闭合。"));
        else FinishRecord();
        return records;
    }

    private static string Quote(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
        ? '"' + value.Replace("\"", "\"\"") + '"' : value;

    private static bool NeedsFormulaEscape(string value)
    {
        if (value.Length == 0) return false;
        if (value[0] is '\t' or '\r' or '\n') return true;
        var first = 0;
        while (first < value.Length && char.IsWhiteSpace(value[first])) first++;
        return first < value.Length && value[first] is '=' or '+' or '-' or '@';
    }

    private static string EscapeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.StartsWith('\'') || NeedsFormulaEscape(value) ? '\'' + value : value;
    }

    private static string UnescapeText(string value) => value.StartsWith('\'') &&
        (value.AsSpan(1).StartsWith("'", StringComparison.Ordinal) || NeedsFormulaEscape(value[1..])) ? value[1..] : value;

    private static string BoolText(bool value) => value ? "true" : "false";
    private static string NumberText(double value) => double.IsFinite(value)
        ? value.ToString("R", CultureInfo.InvariantCulture) : throw new InvalidDataException("CSV 数值必须为有限数值。");
    private static string NullableNumberText(double? value) => value.HasValue ? NumberText(value.Value) : "";
    private static string EnumText<T>(T value) where T : struct, Enum => Enum.IsDefined(value)
        ? value.ToString() : throw new InvalidDataException("CSV 包含未定义的枚举值。");

    private static TagConfiguration Clone(TagConfiguration tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return new()
        {
            Id = tag.Id, Name = tag.Name, Address = tag.Address, DataType = tag.DataType, Area = tag.Area,
            ByteOrder = tag.ByteOrder, ReadMethod = tag.ReadMethod, WriteMethod = tag.WriteMethod, Endian = tag.Endian,
            Enabled = tag.Enabled, UploadEnabled = tag.UploadEnabled, Writable = tag.Writable, Scale = tag.Scale,
            Offset = tag.Offset, Unit = tag.Unit, LowLimit = tag.LowLimit, HighLimit = tag.HighLimit, Description = tag.Description,
            Expression = tag.Expression, ScanIntervalMs = tag.ScanIntervalMs, Deadband = tag.Deadband
        };
    }
}
