using Coalesce.Http.Caching;
using FluentAssertions;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies <see cref="MemoryCacheStore.ComputeSize"/> accuracy:
/// entries with headers/vary metadata report a larger size than body-only entries.
/// </summary>
public sealed class MemoryCacheStoreSizeTests
{
    private static CacheEntry MakeEntry(
        byte[] body,
        Dictionary<string, string[]>? headers = null,
        string[]? varyFields = null,
        Dictionary<string, string[]>? varyValues = null,
        string? eTag = null)
    {
        return new CacheEntry
        {
            StatusCode = 200,
            Body = body,
            Headers = headers ?? new Dictionary<string, string[]>(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            StoredAt = DateTimeOffset.UtcNow,
            VaryFields = varyFields ?? [],
            VaryValues = varyValues ?? new Dictionary<string, string[]>(),
            ETag = eTag
        };
    }

    // ── Entry with no headers has size ≥ body length ─────────────────────────

    [Fact]
    public void EntryWithNoHeaders_SizeIsAtLeastBodyLength()
    {
        byte[] body = new byte[512];
        CacheEntry entry = MakeEntry(body);

        long size = MemoryCacheStore.ComputeSize(entry);

        size.Should().BeGreaterThanOrEqualTo(body.Length,
            "size must always cover at least the body bytes");
    }

    // ── Entry with headers has size > body-only ───────────────────────────────

    [Fact]
    public void EntryWithHeaders_SizeIsGreaterThanBodyOnly()
    {
        byte[] body = new byte[100];

        CacheEntry bodyOnly = MakeEntry(body);
        CacheEntry withHeaders = MakeEntry(body, headers: new Dictionary<string, string[]>
        {
            ["Content-Type"] = ["application/json; charset=utf-8"],
            ["X-Request-Id"] = ["abc-123-def"],
            ["Cache-Control"] = ["max-age=300"]
        });

        long sizeBodyOnly = MemoryCacheStore.ComputeSize(bodyOnly);
        long sizeWithHeaders = MemoryCacheStore.ComputeSize(withHeaders);

        sizeWithHeaders.Should().BeGreaterThan(sizeBodyOnly,
            "headers contribute additional bytes to the size estimate");
    }

    // ── Entry with vary metadata has size > body-only ─────────────────────────

    [Fact]
    public void EntryWithVaryMetadata_SizeIsGreaterThanBodyOnly()
    {
        byte[] body = new byte[50];

        CacheEntry bodyOnly = MakeEntry(body);
        CacheEntry withVary = MakeEntry(
            body,
            varyFields: ["Accept", "Accept-Encoding", "Accept-Language"],
            varyValues: new Dictionary<string, string[]>
            {
                ["Accept"] = ["application/json"],
                ["Accept-Encoding"] = ["gzip, deflate"],
                ["Accept-Language"] = ["en-US,en;q=0.9"]
            });

        long sizeBodyOnly = MemoryCacheStore.ComputeSize(bodyOnly);
        long sizeWithVary = MemoryCacheStore.ComputeSize(withVary);

        sizeWithVary.Should().BeGreaterThan(sizeBodyOnly,
            "vary fields and values contribute to the size estimate");
    }

    // ── Size is deterministic ─────────────────────────────────────────────────

    [Fact]
    public void ComputeSize_SameInput_ReturnsSameValue()
    {
        CacheEntry entry = MakeEntry(
            body: new byte[200],
            headers: new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["text/plain"],
                ["ETag"] = ["\"v1\""]
            },
            varyFields: ["Accept"],
            varyValues: new Dictionary<string, string[]> { ["Accept"] = ["text/plain"] },
            eTag: "\"v1\"");

        long size1 = MemoryCacheStore.ComputeSize(entry);
        long size2 = MemoryCacheStore.ComputeSize(entry);

        size1.Should().Be(size2, "ComputeSize must be deterministic for the same input");
    }

    // ── ETag contributes to size ──────────────────────────────────────────────

    [Fact]
    public void EntryWithETag_SizeIsGreaterThanWithoutETag()
    {
        byte[] body = new byte[64];

        CacheEntry withoutEtag = MakeEntry(body);
        CacheEntry withEtag = MakeEntry(body, eTag: "\"some-long-etag-value\"");

        long sizeWithout = MemoryCacheStore.ComputeSize(withoutEtag);
        long sizeWith = MemoryCacheStore.ComputeSize(withEtag);

        sizeWith.Should().BeGreaterThan(sizeWithout,
            "the ETag string length must be included in the size estimate");
    }
}
