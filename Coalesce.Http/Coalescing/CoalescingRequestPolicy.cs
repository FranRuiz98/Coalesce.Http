namespace Coalesce.Http.Coalescing;

/// <summary>
/// Provides per-request coalescing policy overrides via <see cref="HttpRequestMessage.Options"/>.
/// </summary>
/// <remarks>
/// Use these keys to control coalescing behavior on individual requests without changing global <see cref="Options.CoalescerOptions"/>.
/// <para>
/// <b>Example:</b>
/// <code>
/// var request = new HttpRequestMessage(HttpMethod.Get, "/api/data");
/// request.Options.Set(CoalescingRequestPolicy.BypassCoalescing, true);
/// </code>
/// </para>
/// </remarks>
public static class CoalescingRequestPolicy
{
    /// <summary>
    /// When set to <see langword="true"/>, the request bypasses coalescing entirely:
    /// no deduplication with other concurrent requests. The request is forwarded
    /// directly to the inner handler as an independent call.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> BypassCoalescing = new("Coalesce.Http.BypassCoalescing");
}
