using System.Text.Json;
using System.Text.Json.Serialization;
using PowerTools;
using PowerTools.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PowerBiProjectParser>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "0.2.0" }));
app.MapGet("/api/sample", () => Results.Ok(SampleProject.Create()));
app.MapPost("/api/project/open", (OpenProjectRequest request, PowerBiProjectParser parser) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "请输入 PBIP 项目或报表目录。" });

    try
    {
        return Results.Ok(parser.Parse(request.Path.Trim()));
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "项目解析失败", detail: ex.Message, statusCode: 500);
    }
});

app.MapFallbackToFile("index.html");
app.Run();

record OpenProjectRequest(string Path);
