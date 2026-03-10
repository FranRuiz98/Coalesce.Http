using Coalesce.Http.Coalesce.Http.Coalescing;

namespace Coalesce.Http.Coalesce.Http.Handlers;

public sealed class CoalescingHandler(RequestCoalescer coalescer) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Start only with GET
        return request.Method != HttpMethod.Get
            ? base.SendAsync(request, cancellationToken)
            : coalescer.ExecuteAsync(
                RequestKey.Create(request),
                () => base.SendAsync(request, CancellationToken.None),
                cancellationToken);
    }
}
