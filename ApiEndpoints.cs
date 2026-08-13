using PowerTools.Services;

namespace PowerTools;

public static class ApiEndpoints
{
    public static void MapPowerToolsApi(this WebApplication app, string prefix)
    {
        var api = app.MapGroup(prefix);
        api.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.6.0" }));
        api.MapGet("/sample", () => Results.Ok(SampleProject.Create()));
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
}

public sealed record OpenProjectRequest(string Path);
public sealed record CompareProjectsRequest(string BaselinePath, string CurrentPath, bool Refresh = false);
