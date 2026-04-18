using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Coalesce.Http.Options;

/// <summary>
/// A trivial <see cref="IOptionsMonitor{TOptions}"/> that always returns a fixed instance.
/// Used internally so that test code and production code share the same constructor shape.
/// </summary>
internal sealed class StaticOptionsMonitor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(TOptions value) : IOptionsMonitor<TOptions>
{
    public TOptions CurrentValue => value;

    public TOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
