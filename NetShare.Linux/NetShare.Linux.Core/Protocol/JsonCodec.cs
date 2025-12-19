using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetShare.Linux.Core.Protocol;

public sealed class JsonCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public byte[] Encode<T>(T obj)
    {
        return JsonSerializer.SerializeToUtf8Bytes(obj, Options);
    }

    public T? Decode<T>(byte[] utf8)
    {
        return JsonSerializer.Deserialize<T>(utf8, Options);
    }

    public object? DecodeUntyped(byte[] utf8)
    {
        // Windows uses Dictionary<string, object> with boxed JSON primitives.
        // Using JsonElement keeps fidelity; we convert to a simple object graph.
        var doc = JsonDocument.Parse(utf8);
        return ConvertElement(doc.RootElement);
    }

    private static object? ConvertElement(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => ConvertElement(p.Value)),
            JsonValueKind.Array => el.EnumerateArray().Select(ConvertElement).ToList(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el.ToString(),
        };
    }
}
