using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace Coalesce.Http.Coalesce.Http.Extensions;

public static class HttpClientBuilderExtensions
{
    public static IHttpClientBuilder AddCoalescing(this IHttpClientBuilder builder)
    {
        _ = builder.Services.AddSingleton<RequestCoalescer>();
        _ = builder.Services.AddTransient<CoalescingHandler>();
        _ = builder.AddHttpMessageHandler<CoalescingHandler>();
        return builder;
    }
}
