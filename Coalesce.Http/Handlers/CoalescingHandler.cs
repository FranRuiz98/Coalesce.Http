using Coalesce.Http.Coalescing;
using Coalesce.Http.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coalesce.Http.Handlers;

internal sealed partial class CoalescingHandler(RequestCoalescer coalescer,
                                                CoalescerOptions? options = null,
                                                ILogger<CoalescingHandler>? logger = null) : DelegatingHandler
{
    private readonly ILogger logger = logger ?? NullLogger<CoalescingHandler>.Instance;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Bypass coalescing when disabled, for non-coalesceable methods, or when the per-request policy opts out
        if (options?.Enabled == false || !IsCoalesceableMethod(request.Method) || IsBypassRequested(request))
        {
            LogBypassed(request.Method, request.RequestUri);
            return base.SendAsync(request, cancellationToken);
        }

        return coalescer.ExecuteAsync(
            RequestKey.Create(request),
            () => base.SendAsync(request, CancellationToken.None),
            cancellationToken);
    }

    private static bool IsCoalesceableMethod(HttpMethod method)
    {
        return method == HttpMethod.Get || method == HttpMethod.Head;
    }

    private static bool IsBypassRequested(HttpRequestMessage request)
    {
        return request.Options.TryGetValue(CoalescingRequestPolicy.BypassCoalescing, out bool bypass) && bypass;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Coalescing bypassed for {Method} {RequestUri}")]
    private partial void LogBypassed(HttpMethod method, Uri? requestUri);
}
