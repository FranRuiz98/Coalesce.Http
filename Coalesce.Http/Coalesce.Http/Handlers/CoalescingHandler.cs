using Coalesce.Http.Coalesce.Http.Coalescing;

namespace Coalesce.Http.Coalesce.Http.Handlers;

public sealed class CoalescingHandler(RequestCoalescer coalescer) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {        
        // Start only with GET
        if (request.Method != HttpMethod.Get)
        {
            return base.SendAsync(request, cancellationToken);
        }
        
        return coalescer.ExecuteAsync(RequestKey.Create(request), () => base.SendAsync(request, cancellationToken));
    }
}
