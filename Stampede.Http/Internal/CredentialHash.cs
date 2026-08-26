using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Stampede.Http.Internal;

/// <summary>
/// Produces a short, non-reversible fingerprint of a request's <c>Authorization</c> header value, for use
/// in cache and coalescing keys.
/// </summary>
/// <remarks>
/// Never fold the raw credential itself into a key: a cache key can end up in a distributed store's key
/// listing (<c>redis-cli --scan</c>), a debugger inspecting <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>,
/// or — for the coalescer — a debug log line (<c>RequestKey.ToString()</c>). A truncated SHA-256 hash gives
/// more than enough collision resistance for what this is used for (telling distinct credentials apart so
/// they are never coalesced or cache-hit against each other) without ever placing the bearer token itself
/// anywhere it could leak.
/// </remarks>
internal static class CredentialHash
{
    /// <summary>Truncation length in bytes (128 bits) — negligible collision risk for key discrimination.</summary>
    private const int TruncatedBytes = 16;

    /// <summary>
    /// Returns a lowercase hex fingerprint of <paramref name="authorization"/>, or <see langword="null"/>
    /// when no <c>Authorization</c> header is present.
    /// </summary>
    public static string? OfAuthorization(AuthenticationHeaderValue? authorization)
    {
        if (authorization is null)
        {
            return null;
        }

        // ToString() includes both scheme and parameter (e.g. "Bearer eyJhbGci..."), so requests using
        // different auth schemes are distinguished too, not just different tokens under the same scheme.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization.ToString()));
        return Convert.ToHexString(hash, 0, TruncatedBytes).ToLowerInvariant();
    }
}
