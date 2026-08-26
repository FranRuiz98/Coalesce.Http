using Stampede.Http.Coalescing;
using Stampede.Http.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stampede.Http.Handlers;

internal sealed partial class CoalescingHandler(RequestCoalescer coalescer,
                                                IOptionsMonitor<CoalescerOptions> optionsMonitor,
                                                string clientName,
                                                ILogger<CoalescingHandler>? logger = null) : DelegatingHandler
{
    private readonly ILogger logger = logger ?? NullLogger<CoalescingHandler>.Instance;

    private CoalescerOptions Options => optionsMonitor.Get(clientName);

    /// <summary>
    /// Convenience constructor for testing — wraps a static options instance.
    /// </summary>
    internal CoalescingHandler(RequestCoalescer coalescer, CoalescerOptions? options = null, ILogger<CoalescingHandler>? logger = null)
        : this(coalescer, new StaticOptionsMonitor<CoalescerOptions>(options ?? new()), string.Empty, logger) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bool coalesceable = IsCoalesceableMethod(request.Method) || IsExplicitlyCoalesceable(request);

        // Bypass coalescing when disabled, for non-coalesceable methods, or when the per-request policy opts out
        if (Options.Enabled == false || !coalesceable || IsBypassRequested(request))
        {
            LogBypassed(request.Method, request.RequestUri);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // GET/HEAD are never keyed on a body (they don't carry one that matters here). A method matched by
        // ShouldCoalesce discriminates by body content too — see RequestKey.CreateWithBodyAsync.
        RequestKey? key = IsCoalesceableMethod(request.Method)
            ? RequestKey.Create(request, Options.CoalesceKeyHeaders)
            : await RequestKey.CreateWithBodyAsync(request, Options.CoalesceKeyHeaders, Options.MaxCoalescedRequestBodyBytes, cancellationToken)
                .ConfigureAwait(false);

        if (key is null)
        {
            // Body too large to buffer for hashing — execute independently rather than fail the request.
            LogBodyTooLargeForCoalescing(request.Method, request.RequestUri);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await coalescer.ExecuteAsync(
            key.Value,
            () => base.SendAsync(request, CancellationToken.None),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsCoalesceableMethod(HttpMethod method)
    {
        return method == HttpMethod.Get || method == HttpMethod.Head;
    }

    /// <summary>Returns <see langword="true"/> when a non-GET/HEAD request is explicitly opted into coalescing.</summary>
    private bool IsExplicitlyCoalesceable(HttpRequestMessage request)
    {
        return Options.ShouldCoalesce?.Invoke(request) == true;
    }

    private static bool IsBypassRequested(HttpRequestMessage request)
    {
        return request.Options.TryGetValue(CoalescingRequestPolicy.BypassCoalescing, out bool bypass) && bypass;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Coalescing bypassed for {Method} {RequestUri}")]
    private partial void LogBypassed(HttpMethod method, Uri? requestUri);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Coalescing: body too large to hash for {Method} {RequestUri}, executing independently")]
    private partial void LogBodyTooLargeForCoalescing(HttpMethod method, Uri? requestUri);
}
