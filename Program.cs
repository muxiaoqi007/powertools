using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using PowerTools;
using PowerTools.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PowerBiProjectParser>();
builder.Services.AddSingleton<ProjectSnapshotCache>();
builder.Services.AddSingleton<PowerQueryExportService>();
builder.Services.AddSingleton<SnapshotComparisonService>();
builder.Services.AddSingleton<LivePowerBiModelService>();
builder.Services.Configure<ProjectAccessOptions>(builder.Configuration.GetSection(ProjectAccessOptions.SectionName));
builder.Services.AddSingleton<ProjectPathPolicy>();
builder.Services.AddResponseCompression();
builder.Services.AddRateLimiter(options => options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 120,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 20,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    })));
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffK");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
var startedAt = DateTimeOffset.UtcNow;
app.UseResponseCompression();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health/live", () => Results.Ok(new { status = "live", version = "0.8.0", startedAt, uptimeSeconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds }));
app.MapGet("/health/ready", (ProjectSnapshotCache cache) => Results.Ok(new { status = "ready", version = "0.8.0", cache = cache.GetDiagnostics() }));
app.MapGet("/api/v1/diagnostics", (ProjectSnapshotCache cache, ProjectPathPolicy paths, LivePowerBiModelService live) => Results.Ok(new { version = "0.8.0", startedAt, cache = cache.GetDiagnostics(), allowedRootCount = paths.AllowedRoots.Count, liveContextAvailable = live.GetStartupContext() is not null }));
app.MapPowerToolsApi("/api");
app.MapPowerToolsApi("/api/v1");

app.MapFallbackToFile("index.html");
app.Run();
