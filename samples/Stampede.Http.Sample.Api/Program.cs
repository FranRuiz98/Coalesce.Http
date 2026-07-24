using System.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Stampede.Http.Sample.Api;
using Stampede.Http.Sample.Api.Endpoints;

// ---------------------------------------------------------------------------
// Sample origin API — every endpoint drives Stampede.Http purely through
// standard HTTP caching headers. There is no Stampede.Http code here: the
// origin declares policy, the client-side cache obeys it.
//
// The origin also exports its own metrics (/metrics) and traces (OTLP), so the
// dashboards can show the number that actually matters: how much traffic the
// clients kept off the origin.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<OriginState>();
builder.Services.AddSingleton<OriginMetrics>();

string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "sample-api",
        serviceInstanceId: Environment.MachineName))
    .WithMetrics(m => m
        .AddMeter(OriginMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter())
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation(o =>
            // Scrapes and healthchecks would drown out the interesting spans.
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/metrics")
                              && !ctx.Request.Path.StartsWithSegments("/health"));

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            t.AddOtlpExporter();
        }
    });

var app = builder.Build();

// Every request that gets here is, by definition, a request the client cache did
// not absorb. Attribute it to the endpoint and to the calling client.
app.Use(async (ctx, next) =>
{
    long start = Stopwatch.GetTimestamp();
    await next(ctx);

    string endpoint = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? ctx.Request.Path.ToString();
    string client = ctx.Request.Headers["X-Client"].ToString();

    ctx.RequestServices.GetRequiredService<OriginMetrics>().Record(
        endpoint,
        string.IsNullOrWhiteSpace(client) ? "unknown" : client,
        ctx.Response.StatusCode,
        Stopwatch.GetElapsedTime(start).TotalMilliseconds);
});

app.MapCoreEndpoints();
app.MapVariantEndpoints();
app.MapDiagnosticsEndpoints();
app.MapPrometheusScrapingEndpoint();

app.Run();
