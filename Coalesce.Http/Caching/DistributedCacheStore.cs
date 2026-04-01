using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coalesce.Http.Caching;

/// <summary>
/// An <see cref="ICacheStore"/> implementation backed by <see cref="IDistributedCache"/>,
/// enabling shared caching across multiple application instances (e.g., Redis, SQL Server).
/// </summary>
/// <remarks>
/// <para>
/// Register via <c>UseDistributedCacheStore()</c> on the <c>IHttpClientBuilder</c> after calling
/// <c>AddCoalesceHttp()</c> or <c>AddCachingOnly()</c>:
/// </para>
/// <code>
/// services.AddStackExchangeRedisCache(o => o.Configuration = "localhost");
///
/// services.AddHttpClient("catalog")
///     .AddCoalesceHttp()
///     .UseDistributedCacheStore();
/// </code>
/// <para>
/// <see cref="CacheEntry"/> objects are serialized with <see cref="System.Text.Json"/> and stored
/// as UTF-8 bytes. The <see cref="DistributedCacheEntryOptions.AbsoluteExpiration"/> is extended
/// by <c>Max(StaleIfErrorSeconds, StaleWhileRevalidateSeconds)</c> beyond <see cref="CacheEntry.ExpiresAt"/>
/// so the backing store retains entries long enough for stale serving while still evicting them once
/// all configured windows have expired, even between process restarts.
/// </para>
/// </remarks>
public sealed class DistributedCacheStore(IDistributedCache distributedCache) : ICacheStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new CacheEntryJsonConverter() }
    };

    /// <inheritdoc/>
    public bool TryGetValue(string key, out CacheEntry? entry)
    {
        byte[]? bytes = distributedCache.Get(key);

        if (bytes is null)
        {
            entry = null;
            return false;
        }

        entry = JsonSerializer.Deserialize<CacheEntry>(bytes, s_jsonOptions);
        return entry is not null;
    }

    /// <inheritdoc/>
    public void Set(string key, CacheEntry entry)
    {
        (byte[] bytes, DistributedCacheEntryOptions entryOptions) = Serialize(entry);
        distributedCache.Set(key, bytes, entryOptions);
    }

    /// <inheritdoc/>
    public void Remove(string key)
    {
        distributedCache.Remove(key);
    }

    /// <inheritdoc/>
    public async ValueTask<CacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        byte[]? bytes = await distributedCache.GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<CacheEntry>(bytes, s_jsonOptions);
    }

    /// <inheritdoc/>
    public async ValueTask SetAsync(string key, CacheEntry entry, CancellationToken ct = default)
    {
        (byte[] bytes, DistributedCacheEntryOptions entryOptions) = Serialize(entry);
        await distributedCache.SetAsync(key, bytes, entryOptions, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask RemoveAsync(string key, CancellationToken ct = default)
    {
        await distributedCache.RemoveAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes <paramref name="entry"/> to UTF-8 JSON and builds the <see cref="DistributedCacheEntryOptions"/>
    /// with an <c>AbsoluteExpiration</c> extended by the maximum stale window so entries remain
    /// available for stale-if-error and stale-while-revalidate serving after <see cref="CacheEntry.ExpiresAt"/>.
    /// </summary>
    private static (byte[] Bytes, DistributedCacheEntryOptions Options) Serialize(CacheEntry entry)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, s_jsonOptions);

        long staleWindowSeconds = Math.Max(entry.StaleIfErrorSeconds, entry.StaleWhileRevalidateSeconds);
        DateTimeOffset absoluteExpiration = staleWindowSeconds > 0
            ? entry.ExpiresAt + TimeSpan.FromSeconds(staleWindowSeconds)
            : entry.ExpiresAt;

        return (bytes, new DistributedCacheEntryOptions { AbsoluteExpiration = absoluteExpiration });
    }

    /// <summary>
    /// Custom JSON converter for <see cref="CacheEntry"/> that handles the required init-only
    /// properties and the <see cref="IReadOnlyDictionary{TKey, TValue}"/> header collections.
    /// </summary>
    private sealed class CacheEntryJsonConverter : JsonConverter<CacheEntry>
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

            reader.Read(); // StartObject

            while (reader.TokenType != JsonTokenType.EndObject)
            {
                string propertyName = reader.GetString()!;
                reader.Read();

                switch (propertyName)
                {
                    case nameof(CacheEntry.StatusCode):
                        statusCode = reader.GetInt32();
                        break;
                    case nameof(CacheEntry.Body):
                        body = reader.GetBytesFromBase64();
                        break;
                    case nameof(CacheEntry.Headers):
                        headers = JsonSerializer.Deserialize<Dictionary<string, string[]>>(ref reader, options) ?? [];
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
                        varyFields = JsonSerializer.Deserialize<string[]>(ref reader, options) ?? [];
                        break;
                    case nameof(CacheEntry.VaryValues):
                        varyValues = JsonSerializer.Deserialize<Dictionary<string, string[]>>(ref reader, options) ?? [];
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
                    default:
                        reader.Skip();
                        break;
                }

                reader.Read();
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
                MustRevalidate = mustRevalidate
            };
        }

        public override void Write(Utf8JsonWriter writer, CacheEntry value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WriteNumber(nameof(CacheEntry.StatusCode), value.StatusCode);
            writer.WriteBase64String(nameof(CacheEntry.Body), value.Body);
            writer.WritePropertyName(nameof(CacheEntry.Headers));
            JsonSerializer.Serialize(writer, value.Headers, options);
            writer.WriteString(nameof(CacheEntry.ExpiresAt), value.ExpiresAt);
            writer.WriteString(nameof(CacheEntry.StoredAt), value.StoredAt);

            if (value.ETag is not null)
            {
                writer.WriteString(nameof(CacheEntry.ETag), value.ETag);
            }

            if (value.LastModified is DateTimeOffset lastModified)
            {
                writer.WriteString(nameof(CacheEntry.LastModified), lastModified);
            }

            writer.WritePropertyName(nameof(CacheEntry.VaryFields));
            JsonSerializer.Serialize(writer, value.VaryFields, options);
            writer.WritePropertyName(nameof(CacheEntry.VaryValues));
            JsonSerializer.Serialize(writer, value.VaryValues, options);
            writer.WriteNumber(nameof(CacheEntry.StaleIfErrorSeconds), value.StaleIfErrorSeconds);
            writer.WriteNumber(nameof(CacheEntry.StaleWhileRevalidateSeconds), value.StaleWhileRevalidateSeconds);
            writer.WriteBoolean(nameof(CacheEntry.MustRevalidate), value.MustRevalidate);

            writer.WriteEndObject();
        }
    }
}
