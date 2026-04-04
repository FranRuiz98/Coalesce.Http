using System.Text.Json.Serialization;

namespace Coalesce.Http.Caching;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <see cref="CacheEntry"/> serialization.
/// Enables IL-trimming and native AOT compatibility for <see cref="DistributedCacheStore"/> by
/// providing compile-time-generated <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/>
/// instances instead of runtime reflection.
/// </summary>
[JsonSerializable(typeof(CacheEntry))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(string[]))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CacheEntryJsonContext : JsonSerializerContext
{
}
