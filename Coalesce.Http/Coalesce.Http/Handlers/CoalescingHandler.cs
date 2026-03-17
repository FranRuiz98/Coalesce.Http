using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coalesce.Http.Coalesce.Http.Handlers;

public sealed partial class CoalescingHandler(RequestCoalescer coalescer, CoalescerOptions? options = null, ILogger<CoalescingHandler>? logger = null) : DelegatingHandler
{
    private readonly ILogger logger = logger ?? NullLogger<CoalescingHandler>.Instance;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Bypass coalescing when disabled or for non-GET methods
        if (options?.Enabled == false || request.Method != HttpMethod.Get)
        {
            LogBypassed(request.Method, request.RequestUri);
            return base.SendAsync(request, cancellationToken);
        }

        return coalescer.ExecuteAsync(
            RequestKey.Create(request),
            () => base.SendAsync(request, CancellationToken.None),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Coalescing bypassed for {Method} {RequestUri}")]
    private partial void LogBypassed(HttpMethod method, Uri? requestUri);
}
