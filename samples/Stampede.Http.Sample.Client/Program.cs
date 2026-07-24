using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Stampede.Http.Metrics;
using Stampede.Http.Sample.Client;
using Stampede.Http.Sample.Client.Endpoints;
using Stampede.Http.Sample.Client.Workload;

// ---------------------------------------------------------------------------
// Sample client — an ordinary ASP.NET Core service that happens to call another
// service. The Stampede.Http pipeline lives entirely in PipelineRegistration;
// nothing else in this app knows it exists.
//
//   CachingMiddleware      ← RFC 9111, entries shared across instances via Redis
//     └─ CoalescingHandler ← per-process request deduplication
//          └─ Polly        ← retry with exponential backoff + per-attempt timeout
//               └─ SocketsHttpHandler → the sample API
//
// The same image runs three ways in docker compose: two Stampede.Http instances
// (client-a, client-b) and one control instance with the handlers removed
// (client-baseline), so the dashboards can show the difference as a number.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// A mounted directory rather than a mounted file: editing a bind-mounted file on the host
// usually replaces the inode, and the container would keep reading the old one.
builder.Configuration.AddJsonFile("config/client.json", optional: true, reloadOnChange: true);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddOptions<SampleOptions>()
    .Bind(builder.Configuration.GetSection(SampleOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Also needed eagerly: the pipeline shape is decided at registration time.
SampleOptions sample = builder.Configuration.GetSection(SampleOptions.SectionName).Get<SampleOptions>() ?? new SampleOptions();

builder.Services.AddOriginClient(builder.Configuration, sample);
builder.Services.AddSingleton<StampedeCounters>();
builder.Services.AddTransient<FeatureTour>();
builder.Services.AddHostedService<WorkloadService>();

string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(serviceName: "sample-client", serviceInstanceId: sample.Instance)
        .AddAttributes([new KeyValuePair<string, object>("stampede.enabled", sample.Pipeline.Enabled)]))
    .WithMetrics(m => m
        .AddMeter(StampedeHttpMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddPrometheusExporter())
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation(o =>
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/metrics")
                              && !ctx.Request.Path.StartsWithSegments("/healthz"));

        // The outgoing spans are the interesting ones: a coalesced burst produces a single
        // child span for N inbound requests, which is the whole story in one screenshot.
        t.AddHttpClientInstrumentation();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            t.AddOtlpExporter();
        }
    });

var app = builder.Build();

app.MapBffEndpoints();
app.MapPrometheusScrapingEndpoint();

app.Logger.LogInformation(
    "Starting {Instance} — origin: {ApiBaseUrl}, Stampede.Http: {Enabled}, Redis: {Redis}",
    sample.Instance,
    sample.ApiBaseUrl,
    sample.Pipeline.Enabled ? "enabled" : "DISABLED (control group)",
    sample.Pipeline is { Enabled: true, UseRedis: true } && !string.IsNullOrWhiteSpace(sample.RedisConnection)
        ? sample.RedisConnection
        : "in-memory store");

app.Run();
