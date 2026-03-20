using Coalesce.Http.Caching;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

public sealed class FreshnessCalculatorTests
{
    private readonly CacheOptions _defaultOptions = new();

    // ── ComputeExpiresAt ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeExpiresAt_SMaxAge_TakesPriorityOverMaxAge()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = new CacheControlHeaderValue
        {
            SharedMaxAge = TimeSpan.FromSeconds(120),
            MaxAge = TimeSpan.FromSeconds(60)
        };

        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset result = FreshnessCalculator.ComputeExpiresAt(response, _defaultOptions);

        result.Should().BeOnOrAfter(before + TimeSpan.FromSeconds(120));
        result.Should().BeOnOrBefore(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void ComputeExpiresAt_MaxAge_UsedWhenNoSMaxAge()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = new CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromSeconds(90)
        };

        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset result = FreshnessCalculator.ComputeExpiresAt(response, _defaultOptions);

        result.Should().BeOnOrAfter(before + TimeSpan.FromSeconds(90));
        result.Should().BeOnOrBefore(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void ComputeExpiresAt_ExpiresHeader_UsedWhenNoCacheControl()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("body")
        };
        response.Headers.Date = now;
        response.Content.Headers.Expires = now + TimeSpan.FromSeconds(200);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset result = FreshnessCalculator.ComputeExpiresAt(response, _defaultOptions);

        result.Should().BeCloseTo(before + TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ComputeExpiresAt_ExpiresInPast_ClampsToZero()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("body")
        };
        response.Headers.Date = now;
        response.Content.Headers.Expires = now - TimeSpan.FromSeconds(100);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset result = FreshnessCalculator.ComputeExpiresAt(response, _defaultOptions);

        // Negative age is clamped to zero, so result ≈ now
        result.Should().BeCloseTo(before, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ComputeExpiresAt_NoCacheDirectives_FallsBackToDefaultTtl()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset result = FreshnessCalculator.ComputeExpiresAt(response, _defaultOptions);

        // Default TTL is 30 seconds
        result.Should().BeCloseTo(before + TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ComputeExpiresAt_CustomDefaultTtl_IsRespected()
    {
        var options = new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset result = FreshnessCalculator.ComputeExpiresAt(response, options);

        result.Should().BeCloseTo(before + TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ComputeExpiresAt_ExpiresWithoutDateHeader_UsesNowAsFallback()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("body")
        };
        // No Date header set
        response.Content.Headers.Expires = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset result = FreshnessCalculator.ComputeExpiresAt(response, _defaultOptions);

        // With no Date header, age = Expires - now ≈ 60s, so result ≈ now + 60s
        result.Should().BeCloseTo(before + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2));
    }

    // ── ExtractStaleIfError ───────────────────────────────────────────────────

    [Fact]
    public void ExtractStaleIfError_DirectivePresent_ReturnsValueFromHeader()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=60, stale-if-error=300");

        long result = FreshnessCalculator.ExtractStaleIfError(response, _defaultOptions);

        result.Should().Be(300);
    }

    [Fact]
    public void ExtractStaleIfError_DirectiveAbsent_ReturnsFallback()
    {
        var options = new CacheOptions { DefaultStaleIfErrorSeconds = 600 };
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };

        long result = FreshnessCalculator.ExtractStaleIfError(response, options);

        result.Should().Be(600);
    }

    [Fact]
    public void ExtractStaleIfError_NoCacheControl_ReturnsFallback()
    {
        var options = new CacheOptions { DefaultStaleIfErrorSeconds = 120 };
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        long result = FreshnessCalculator.ExtractStaleIfError(response, options);

        result.Should().Be(120);
    }

    [Fact]
    public void ExtractStaleIfError_DefaultOptionsNoDirective_ReturnsZero()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        long result = FreshnessCalculator.ExtractStaleIfError(response, _defaultOptions);

        result.Should().Be(0);
    }

    [Fact]
    public void ExtractStaleIfError_NegativeValue_IgnoredReturnsFallback()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=60, stale-if-error=-1");

        long result = FreshnessCalculator.ExtractStaleIfError(response, _defaultOptions);

        // Negative value fails the >= 0 check, so falls back to default (0)
        result.Should().Be(0);
    }

    // ── ExtractStaleWhileRevalidate ───────────────────────────────────────────

    [Fact]
    public void ExtractStaleWhileRevalidate_DirectivePresent_ReturnsValueFromHeader()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=60, stale-while-revalidate=120");

        long result = FreshnessCalculator.ExtractStaleWhileRevalidate(response, _defaultOptions);

        result.Should().Be(120);
    }

    [Fact]
    public void ExtractStaleWhileRevalidate_DirectiveAbsent_ReturnsFallback()
    {
        var options = new CacheOptions { DefaultStaleWhileRevalidateSeconds = 300 };
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };

        long result = FreshnessCalculator.ExtractStaleWhileRevalidate(response, options);

        result.Should().Be(300);
    }

    [Fact]
    public void ExtractStaleWhileRevalidate_NoCacheControl_ReturnsFallback()
    {
        var options = new CacheOptions { DefaultStaleWhileRevalidateSeconds = 60 };
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        long result = FreshnessCalculator.ExtractStaleWhileRevalidate(response, options);

        result.Should().Be(60);
    }

    [Fact]
    public void ExtractStaleWhileRevalidate_DefaultOptionsNoDirective_ReturnsZero()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        long result = FreshnessCalculator.ExtractStaleWhileRevalidate(response, _defaultOptions);

        result.Should().Be(0);
    }

    [Fact]
    public void ExtractStaleWhileRevalidate_NegativeValue_IgnoredReturnsFallback()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=60, stale-while-revalidate=-1");

        long result = FreshnessCalculator.ExtractStaleWhileRevalidate(response, _defaultOptions);

        // Negative value fails the >= 0 check, so falls back to default (0)
        result.Should().Be(0);
    }
}
