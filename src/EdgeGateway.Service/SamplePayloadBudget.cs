using System.Buffers;
using System.Collections;
using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>
/// Bounded admission before retention/serialization. Scalars and strings use constant-time conservative
/// estimates; structured JSON is counted into a bounded reusable scratch buffer, never a full JSON string.
/// The budget bounds retained payload, rather than claiming an exact measurement of the CLR heap.
/// </summary>
internal static class SamplePayloadBudget
{
    internal const long MaximumSampleBytes = 1024 * 1024;
    private const int MaximumNodes = 4096;
    private const int MaximumDepth = 16;

    public static bool TryEstimate(TelemetrySample sample, out long bytes)
    {
        bytes = 512;
        var nodes = 0;
        try
        {
            return Text(sample.DeviceName, ref bytes) && Text(sample.TagName, ref bytes) && Text(sample.Address, ref bytes)
                && Text(sample.Unit, ref bytes) && Text(sample.Error, ref bytes)
                && Value(sample.RawValue, ref bytes, ref nodes, 0)
                && Value(sample.Value, ref bytes, ref nodes, 0);
        }
        catch (Exception exception) when (exception is PayloadLimitException or JsonException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    private static bool Text(string? text, ref long bytes)
    {
        // Six bytes per UTF-16 code unit covers JSON escaping and conservatively exceeds UTF-16 storage.
        bytes += 64 + (text?.Length ?? 0) * 6L;
        return bytes <= MaximumSampleBytes;
    }

    private static bool Value(object? value, ref long bytes, ref int nodes, int depth)
    {
        if (++nodes > MaximumNodes || depth > MaximumDepth) return false;
        bytes += 64;
        if (bytes > MaximumSampleBytes) return false;
        switch (value)
        {
            case null or DBNull or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal or DateTime or DateTimeOffset or Guid or TimeSpan:
                return true;
            case string text: return Text(text, ref bytes);
            case JsonDocument document: return Json(document.RootElement, ref bytes, ref nodes, depth);
            case JsonElement element: return Json(element, ref bytes, ref nodes, depth);
            case IDictionary dictionary:
                if (dictionary.Count > MaximumNodes - nodes) return false;
                foreach (DictionaryEntry entry in dictionary)
                    if (entry.Key is not string key || !Text(key, ref bytes) || !Value(entry.Value, ref bytes, ref nodes, depth + 1)) return false;
                return true;
            case IList list:
                if (list.Count > MaximumNodes - nodes) return false;
                foreach (var item in list) if (!Value(item, ref bytes, ref nodes, depth + 1)) return false;
                return true;
            default:
                // Drivers normalize vendor structures into JsonElement. Arbitrary object getters/enumerators
                // cannot be bounded without invoking user code, so do not retain them in a background queue.
                return false;
        }
    }

    private static bool Json(JsonElement element, ref long bytes, ref int nodes, int depth)
    {
        var before = nodes;
        if (!JsonComplexity(element, ref nodes, depth)) return false;
        bytes += 64L * (nodes - before);
        var remaining = (MaximumSampleBytes - bytes) / 2;
        if (remaining < 1) return false;
        var buffer = new CountingBufferWriter(remaining);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = MaximumDepth }))
        { element.WriteTo(writer); writer.Flush(); }
        bytes += buffer.Written * 2;
        return bytes <= MaximumSampleBytes;
    }

    private static bool JsonComplexity(JsonElement value, ref int nodes, int depth)
    {
        if (++nodes > MaximumNodes || depth > MaximumDepth) return false;
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > MaximumNodes - nodes) return false;
            foreach (var item in value.EnumerateArray()) if (!JsonComplexity(item, ref nodes, depth + 1)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) if (!JsonComplexity(property.Value, ref nodes, depth + 1)) return false;
        return true;
    }

    private sealed class CountingBufferWriter(long maximum) : IBufferWriter<byte>
    {
        private byte[] _buffer = [];
        public long Written { get; private set; }
        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length || Written + count > maximum) throw new PayloadLimitException();
            Written += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            sizeHint = Math.Max(1, sizeHint);
            var remaining = maximum - Written;
            if (sizeHint > remaining) throw new PayloadLimitException();
            if (_buffer.Length < sizeHint) _buffer = new byte[(int)Math.Min(remaining, Math.Max(4096, sizeHint))];
            return _buffer.AsMemory(0, (int)Math.Min(_buffer.Length, remaining));
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
    private sealed class PayloadLimitException : Exception { }
}
