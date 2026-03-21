namespace Coalesce.Http.Caching;

/// <summary>
/// Defines the contract for building cache keys from HTTP request messages.
/// </summary>
/// <remarks>
/// Implement this interface to customise how cache keys are derived from requests.
/// The default implementation, <see cref="DefaultCacheKeyBuilder"/>, produces keys in the form <c>METHOD:absoluteUri</c>.
/// </remarks>
public interface ICacheKeyBuilder
{
    /// <summary>
    /// Builds a cache key for the specified HTTP request.
    /// </summary>
    /// <param name="request">The HTTP request message to derive the key from.</param>
    /// <returns>A string that uniquely identifies the cacheable resource represented by <paramref name="request"/>.</returns>
    string Build(HttpRequestMessage request);
}
