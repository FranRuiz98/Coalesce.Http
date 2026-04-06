using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coalesce.Http.Caching;

/// <summary>
/// Custom JSON converter for <see cref="CacheEntry"/> that handles required init-only properties
/// and <see cref="IReadOnlyDictionary{TKey, TValue}"/> header collections without reflection.
/// Used by <see cref="CacheEntryJsonContext"/> and therefore compatible with IL trimming and
/// native AOT.
/// </summary>
internal sealed class CacheEntryJsonConverter : JsonConverter<CacheEntry>
{
    public override CacheEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        int statusCode = 0;
        byte[]? body = null;
        Dictionary<string, string[]> headers = [];
        DateTimeOffset expiresAt = default;
        DateTimeOffset storedAt = default;
        string? eTag = null;
        DateTimeOffset? lastModified = null;
        string[] varyFields = [];
        Dictionary<string, string[]> varyValues = [];
        long staleIfErrorSeconds = 0;
        long staleWhileRevalidateSeconds = 0;
        bool mustRevalidate = false;
        bool immutable = false;

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of CacheEntry object.");
        }

        _ = reader.Read(); // advance past StartObject to first property name

        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Unexpected token {reader.TokenType} while reading CacheEntry.");
            }

            string propertyName = reader.GetString()!;
            _ = reader.Read();

            switch (propertyName)
            {
                case nameof(CacheEntry.StatusCode):
                    statusCode = reader.GetInt32();
                    break;
                case nameof(CacheEntry.Body):
                    body = reader.GetBytesFromBase64();
                    break;
                case nameof(CacheEntry.Headers):
                    headers = JsonSerializer.Deserialize(ref reader,
                        CacheEntryJsonContext.Default.DictionaryStringStringArray) ?? [];
                    break;
                case nameof(CacheEntry.ExpiresAt):
                    expiresAt = reader.GetDateTimeOffset();
                    break;
                case nameof(CacheEntry.StoredAt):
                    storedAt = reader.GetDateTimeOffset();
                    break;
                case nameof(CacheEntry.ETag):
                    eTag = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case nameof(CacheEntry.LastModified):
                    lastModified = reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset();
                    break;
                case nameof(CacheEntry.VaryFields):
                    varyFields = JsonSerializer.Deserialize(ref reader,
                        CacheEntryJsonContext.Default.StringArray) ?? [];
                    break;
                case nameof(CacheEntry.VaryValues):
                    varyValues = JsonSerializer.Deserialize(ref reader,
                        CacheEntryJsonContext.Default.DictionaryStringStringArray) ?? [];
                    break;
                case nameof(CacheEntry.StaleIfErrorSeconds):
                    staleIfErrorSeconds = reader.GetInt64();
                    break;
                case nameof(CacheEntry.StaleWhileRevalidateSeconds):
                    staleWhileRevalidateSeconds = reader.GetInt64();
                    break;
                case nameof(CacheEntry.MustRevalidate):
                    mustRevalidate = reader.GetBoolean();
                    break;
                case nameof(CacheEntry.Immutable):
                    immutable = reader.GetBoolean();
                    break;
                default:
                    reader.Skip();
                    break;
            }

            _ = reader.Read();
        }

        return new CacheEntry
        {
            StatusCode = statusCode,
            Body = body ?? [],
            Headers = headers,
            ExpiresAt = expiresAt,
            StoredAt = storedAt,
            ETag = eTag,
            LastModified = lastModified,
            VaryFields = varyFields,
            VaryValues = varyValues,
            StaleIfErrorSeconds = staleIfErrorSeconds,
            StaleWhileRevalidateSeconds = staleWhileRevalidateSeconds,
            MustRevalidate = mustRevalidate,
            Immutable = immutable
        };
    }

    public override void Write(Utf8JsonWriter writer, CacheEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteNumber(nameof(CacheEntry.StatusCode), value.StatusCode);
        writer.WriteBase64String(nameof(CacheEntry.Body), value.Body);
        writer.WritePropertyName(nameof(CacheEntry.Headers));
        JsonSerializer.Serialize(writer, value.Headers,
            CacheEntryJsonContext.Default.DictionaryStringStringArray);
        writer.WriteString(nameof(CacheEntry.ExpiresAt), value.ExpiresAt);
        writer.WriteString(nameof(CacheEntry.StoredAt), value.StoredAt);

        if (value.ETag is not null)
        {
            writer.WriteString(nameof(CacheEntry.ETag), value.ETag);
        }
        else
        {
            writer.WriteNull(nameof(CacheEntry.ETag));
        }

        if (value.LastModified is DateTimeOffset lastModified)
        {
            writer.WriteString(nameof(CacheEntry.LastModified), lastModified);
        }
        else
        {
            writer.WriteNull(nameof(CacheEntry.LastModified));
        }

        writer.WritePropertyName(nameof(CacheEntry.VaryFields));
        JsonSerializer.Serialize(writer, value.VaryFields,
            CacheEntryJsonContext.Default.StringArray);
        writer.WritePropertyName(nameof(CacheEntry.VaryValues));
        JsonSerializer.Serialize(writer, value.VaryValues,
            CacheEntryJsonContext.Default.DictionaryStringStringArray);
        writer.WriteNumber(nameof(CacheEntry.StaleIfErrorSeconds), value.StaleIfErrorSeconds);
        writer.WriteNumber(nameof(CacheEntry.StaleWhileRevalidateSeconds), value.StaleWhileRevalidateSeconds);
        writer.WriteBoolean(nameof(CacheEntry.MustRevalidate), value.MustRevalidate);
        writer.WriteBoolean(nameof(CacheEntry.Immutable), value.Immutable);

        writer.WriteEndObject();
    }
}
