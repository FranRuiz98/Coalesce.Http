using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Options;

namespace Coalesce.Http.Coalesce.Http.Handlers;

public sealed class CoalescingHandler(RequestCoalescer coalescer, CoalescerOptions? options = null) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Bypass coalescing when disabled or for non-GET methods
        if (options?.Enabled == false || request.Method != HttpMethod.Get)
        {
            return base.SendAsync(request, cancellationToken);
        }

        return coalescer.ExecuteAsync(
            RequestKey.Create(request),
            () => base.SendAsync(request, CancellationToken.None),
            cancellationToken);
    }
}
