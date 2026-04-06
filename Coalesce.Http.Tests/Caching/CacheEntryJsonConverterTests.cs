using Coalesce.Http.Caching;
using FluentAssertions;
using System.Text.Json;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies <see cref="CacheEntryJsonConverter"/> correctness: round-trips, optional fields,
/// forward-compatibility with unknown properties, and malformed-input rejection.
/// </summary>
public sealed class CacheEntryJsonConverterTests
{
    private static readonly JsonSerializerOptions Options =
        new(CacheEntryJsonContext.Default.Options);

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_AllFields_PreservesValues()
    {
        CacheEntry original = new()
        {
            StatusCode = 200,
            Body = [1, 2, 3, 4],
            Headers = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"],
                ["X-Custom"] = ["a", "b"]
            },
            ExpiresAt = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero),
            StoredAt = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero),
            ETag = "\"abc\"",
            LastModified = new DateTimeOffset(2025, 5, 31, 0, 0, 0, TimeSpan.Zero),
            VaryFields = ["Accept", "Accept-Encoding"],
            VaryValues = new Dictionary<string, string[]>
            {
                ["Accept"] = ["application/json"],
                ["Accept-Encoding"] = ["gzip"]
            },
            StaleIfErrorSeconds = 300,
            StaleWhileRevalidateSeconds = 60,
            MustRevalidate = true
        };

        string json = JsonSerializer.Serialize(original, CacheEntryJsonContext.Default.CacheEntry);
        CacheEntry? restored = JsonSerializer.Deserialize(json, CacheEntryJsonContext.Default.CacheEntry);

        restored.Should().NotBeNull();
        restored!.StatusCode.Should().Be(original.StatusCode);
        restored.Body.Should().Equal(original.Body);
        restored.Headers.Should().BeEquivalentTo(original.Headers);
        restored.ExpiresAt.Should().Be(original.ExpiresAt);
        restored.StoredAt.Should().Be(original.StoredAt);
        restored.ETag.Should().Be(original.ETag);
        restored.LastModified.Should().Be(original.LastModified);
        restored.VaryFields.Should().Equal(original.VaryFields);
        restored.VaryValues.Should().BeEquivalentTo(original.VaryValues);
        restored.StaleIfErrorSeconds.Should().Be(original.StaleIfErrorSeconds);
        restored.StaleWhileRevalidateSeconds.Should().Be(original.StaleWhileRevalidateSeconds);
        restored.MustRevalidate.Should().Be(original.MustRevalidate);
    }

    // ── Optional fields default to null/default when absent ──────────────────

    [Fact]
    public void Deserialize_MissingOptionalFields_DefaultsToNullOrDefault()
    {
        const string json = """
            {
                "StatusCode": 200,
                "Body": "AQID",
                "Headers": {},
                "ExpiresAt": "2025-01-01T00:00:00+00:00",
                "StoredAt": "2025-01-01T00:00:00+00:00"
            }
            """;

        CacheEntry? entry = JsonSerializer.Deserialize(json, CacheEntryJsonContext.Default.CacheEntry);

        entry.Should().NotBeNull();
        entry!.ETag.Should().BeNull();
        entry.LastModified.Should().BeNull();
        entry.VaryFields.Should().BeEmpty();
        entry.VaryValues.Should().BeEmpty();
        entry.StaleIfErrorSeconds.Should().Be(0);
        entry.StaleWhileRevalidateSeconds.Should().Be(0);
        entry.MustRevalidate.Should().BeFalse();
    }

    // ── Explicit null ETag/LastModified round-trips correctly ─────────────────

    [Fact]
    public void RoundTrip_NullETagAndLastModified_RemainsNull()
    {
        CacheEntry original = new()
        {
            StatusCode = 200,
            Body = [],
            Headers = new Dictionary<string, string[]>(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            StoredAt = DateTimeOffset.UtcNow,
            ETag = null,
            LastModified = null
        };

        string json = JsonSerializer.Serialize(original, CacheEntryJsonContext.Default.CacheEntry);
        json.Should().Contain("\"ETag\":null", "ETag must always be written");
        json.Should().Contain("\"LastModified\":null", "LastModified must always be written");

        CacheEntry? restored = JsonSerializer.Deserialize(json, CacheEntryJsonContext.Default.CacheEntry);
        restored!.ETag.Should().BeNull();
        restored.LastModified.Should().BeNull();
    }

    // ── Unknown future fields are skipped ────────────────────────────────────

    [Fact]
    public void Deserialize_UnknownFields_AreSkippedGracefully()
    {
        const string json = """
            {
                "StatusCode": 200,
                "Body": "",
                "Headers": {},
                "ExpiresAt": "2025-01-01T00:00:00+00:00",
                "StoredAt": "2025-01-01T00:00:00+00:00",
                "FutureField": "some-value",
                "AnotherNewField": { "nested": 42 }
            }
            """;

        Action act = () => JsonSerializer.Deserialize(json, CacheEntryJsonContext.Default.CacheEntry);

        act.Should().NotThrow("unknown fields must be skipped by the default: branch");

        CacheEntry? entry = JsonSerializer.Deserialize(json, CacheEntryJsonContext.Default.CacheEntry);
        entry.Should().NotBeNull();
        entry!.StatusCode.Should().Be(200);
    }

    // ── Malformed start token throws JsonException ────────────────────────────

    [Fact]
    public void Deserialize_StartArray_ThrowsJsonException()
    {
        const string json = "[1,2,3]";

        Action act = () => JsonSerializer.Deserialize(json, CacheEntryJsonContext.Default.CacheEntry);

        act.Should().Throw<JsonException>("a JSON array is not a valid CacheEntry object");
    }

    [Fact]
    public void Deserialize_StringToken_ThrowsJsonException()
    {
        const string json = "\"not-an-object\"";

        Action act = () => JsonSerializer.Deserialize(json, CacheEntryJsonContext.Default.CacheEntry);

        act.Should().Throw<JsonException>("a JSON string is not a valid CacheEntry object");
    }

    // ── Size: serialised output is deterministic ──────────────────────────────

    [Fact]
    public void Serialize_SameInput_ProducesSameOutput()
    {
        CacheEntry entry = new()
        {
            StatusCode = 200,
            Body = [10, 20],
            Headers = new Dictionary<string, string[]> { ["ETag"] = ["\"v1\""] },
            ExpiresAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            StoredAt = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero)
        };

        string json1 = JsonSerializer.Serialize(entry, CacheEntryJsonContext.Default.CacheEntry);
        string json2 = JsonSerializer.Serialize(entry, CacheEntryJsonContext.Default.CacheEntry);

        json1.Should().Be(json2, "serialisation must be deterministic");
    }
}
