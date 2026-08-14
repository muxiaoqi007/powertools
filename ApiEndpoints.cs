using PowerTools.Services;

namespace PowerTools;

public static class ApiEndpoints
{
    public static void MapPowerToolsApi(this WebApplication app, string prefix)
    {
        var api = app.MapGroup(prefix);
        api.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.8.0" }));
        api.MapGet("/sample", () => Results.Ok(SampleProject.Create()));
        api.MapGet("/live/context", (LivePowerBiModelService live) => Results.Ok(new { available = live.GetStartupContext() is not null }));
        api.MapGet("/live/current", OpenCurrentLiveModel);
        api.MapPost("/live/open", OpenLiveModel);
        api.MapGet("/powerquery/entities", (PowerQueryExportService exporter) => Results.Ok(exporter.GetCatalog()));
        api.MapGet("/powerquery/{entity}", ExportPowerQuery);
        api.MapPost("/project/open", OpenProject);
        api.MapPost("/project/compare", CompareProjects);
    }

    private static async Task<IResult> ExportPowerQuery(string entity, string? path, bool? refresh, ProjectSnapshotCache cache, ProjectPathPolicy paths, PowerQueryExportService exporter, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "缺少 path 查询参数。" });
        try { return Results.Ok(exporter.Export(await cache.GetAsync(paths.Resolve(path), refresh == true, cancellationToken), entity)); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (DirectoryNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return Results.Problem(title: "Power Query 数据导出失败", detail: ex.Message, statusCode: 500); }
    }

    private static async Task<IResult> OpenProject(OpenProjectRequest request, ProjectSnapshotCache cache, ProjectPathPolicy paths, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path)) return Results.BadRequest(new { error = "请输入 PBIP 项目或报表目录。" });
        try { return Results.Ok(await cache.GetAsync(paths.Resolve(request.Path), true, cancellationToken)); }
        catch (DirectoryNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
        catch (Exception ex) { return Results.Problem(title: "项目解析失败", detail: ex.Message, statusCode: 500); }
    }

    private static async Task<IResult> CompareProjects(CompareProjectsRequest request, ProjectSnapshotCache cache, ProjectPathPolicy paths, SnapshotComparisonService comparison, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaselinePath) || string.IsNullOrWhiteSpace(request.CurrentPath)) return Results.BadRequest(new { error = "请输入基线项目和当前项目目录。" });
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
    }

    private static Task<IResult> OpenCurrentLiveModel(LivePowerBiModelService live, CancellationToken cancellationToken)
    {
        var context = live.GetStartupContext();
        return context is null
            ? Task.FromResult<IResult>(Results.BadRequest(new { error = "当前实例不是从 Power BI 外部工具启动的。" }))
            : ReadLiveModel(context.Server, context.Database, live, cancellationToken);
    }

    private static Task<IResult> OpenLiveModel(OpenLiveModelRequest request, LivePowerBiModelService live, CancellationToken cancellationToken) =>
        ReadLiveModel(request.Server, request.Database, live, cancellationToken);

    private static async Task<IResult> ReadLiveModel(string server, string database, LivePowerBiModelService live, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database)) return Results.BadRequest(new { error = "缺少 Power BI server 或 database 参数。" });
        try { return Results.Ok(await live.ReadAsync(server, database, cancellationToken)); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return Results.Problem(title: "实时模型读取失败", detail: ex.Message, statusCode: 500); }
    }
}

public sealed record OpenProjectRequest(string Path);
public sealed record CompareProjectsRequest(string BaselinePath, string CurrentPath, bool Refresh = false);
