using System.Text.Json;
using System.Text.Json.Serialization;
using PowerTools;
using PowerTools.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PowerBiProjectParser>();
builder.Services.AddSingleton<ProjectSnapshotCache>();
builder.Services.AddSingleton<PowerQueryExportService>();
builder.Services.AddSingleton<SnapshotComparisonService>();
builder.Services.Configure<ProjectAccessOptions>(builder.Configuration.GetSection(ProjectAccessOptions.SectionName));
builder.Services.AddSingleton<ProjectPathPolicy>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "0.5.0" }));
app.MapGet("/api/sample", () => Results.Ok(SampleProject.Create()));
app.MapGet("/api/powerquery/entities", (PowerQueryExportService exporter) => Results.Ok(exporter.GetCatalog()));
app.MapGet("/api/powerquery/{entity}", async (string entity, string? path, bool? refresh, ProjectSnapshotCache cache, ProjectPathPolicy paths, PowerQueryExportService exporter, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "缺少 path 查询参数。" });
    try { return Results.Ok(exporter.Export(await cache.GetAsync(paths.Resolve(path), refresh == true, cancellationToken), entity)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (DirectoryNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) { return Results.Problem(title: "Power Query 数据导出失败", detail: ex.Message, statusCode: 500); }
});
app.MapPost("/api/project/open", async (OpenProjectRequest request, ProjectSnapshotCache cache, ProjectPathPolicy paths, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "请输入 PBIP 项目或报表目录。" });

    try
    {
        return Results.Ok(await cache.GetAsync(paths.Resolve(request.Path), true, cancellationToken));
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 403);
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "项目解析失败", detail: ex.Message, statusCode: 500);
    }
});
app.MapPost("/api/project/compare", async (CompareProjectsRequest request, ProjectSnapshotCache cache, ProjectPathPolicy paths, SnapshotComparisonService comparison, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.BaselinePath) || string.IsNullOrWhiteSpace(request.CurrentPath))
        return Results.BadRequest(new { error = "请输入基线项目和当前项目目录。" });
    try
    {
        var baselineTask = cache.GetAsync(paths.Resolve(request.BaselinePath), request.Refresh, cancellationToken);
        var currentTask = cache.GetAsync(paths.Resolve(request.CurrentPath), request.Refresh, cancellationToken);
        await Task.WhenAll(baselineTask, currentTask);
        return Results.Ok(comparison.Compare(await baselineTask, await currentTask));
    }
    catch (DirectoryNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
    catch (Exception ex) { return Results.Problem(title: "项目比较失败", detail: ex.Message, statusCode: 500); }
});

app.MapFallbackToFile("index.html");
app.Run();

record OpenProjectRequest(string Path);
record CompareProjectsRequest(string BaselinePath, string CurrentPath, bool Refresh = false);
