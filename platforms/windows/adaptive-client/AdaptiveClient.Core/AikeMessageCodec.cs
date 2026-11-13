using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaptiveClient.Core.Models;

namespace AdaptiveClient.Core;

public static class AikeMessageCodec
{
    internal static readonly JsonSerializerOptions StableOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public static string Encode(AikeMessage message)
    {
        return JsonSerializer.Serialize(message, StableOptions);
    }

    public static byte[] EncodeToBytes(AikeMessage message)
    {
        return Encoding.UTF8.GetBytes(Encode(message));
    }

    public static AikeMessage Decode(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("json payload is empty", nameof(json));
        }

        return JsonSerializer.Deserialize<AikeMessage>(json, StableOptions)
            ?? throw new InvalidOperationException("message decode produced no envelope");
    }

    public static AikeMessage Decode(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            throw new ArgumentException("payload bytes are empty", nameof(bytes));
        }

        return JsonSerializer.Deserialize<AikeMessage>(bytes, StableOptions)
            ?? throw new InvalidOperationException("message decode produced no envelope");
    }
}
