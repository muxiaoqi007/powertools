using Microsoft.Extensions.Options;
using PowerTools;
using PowerTools.Services;
using PowerTools.Updater;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PowerTools.Tests;

public sealed class PowerToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "powertools-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _managedRoot = Path.Combine(Path.GetTempPath(), "powertools-managed-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Storage_dmvs_are_combined_into_column_metrics()
    {
        static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) => values.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var columns = new[]
        {
            Row(("TABLE_ID", "1"), ("COLUMN_ID", "2"), ("DIMENSION_NAME", "Sales"), ("ATTRIBUTE_NAME", "Order Id"), ("COLUMN_TYPE", "BASIC_DATA"), ("DICTIONARY_SIZE", 120L)),
            Row(("TABLE_ID", "H$1"), ("COLUMN_ID", "POS_TO_ID"), ("DIMENSION_NAME", "Sales"), ("ATTRIBUTE_NAME", "Order Id"), ("COLUMN_TYPE", "HIERARCHY_POSITION_TO_DATAID"), ("DICTIONARY_SIZE", 0L))
        };
        var segments = new[]
        {
            Row(("TABLE_ID", "1"), ("COLUMN_ID", "2"), ("USED_SIZE", 1000L), ("RECORDS_COUNT", 500L)),
            Row(("TABLE_ID", "1"), ("COLUMN_ID", "2"), ("USED_SIZE", 900L), ("RECORDS_COUNT", 500L))
        };
        var tables = Array.Empty<Dictionary<string, object?>>();
        var schemaTables = new[] { Row(("ID", 10L), ("Name", "Sales")) };
        var schemaColumns = new[] { Row(("ID", 20L), ("TableID", 10L), ("ExplicitName", "Order Id"), ("ColumnStorageID", 30L)) };
        var columnStorages = new[] { Row(("ID", 30L), ("Statistics_RowCount", 1000L), ("Statistics_DistinctStates", 800L)) };

        var metric = Assert.Single(LivePowerBiModelService.BuildStorageMetrics(columns, segments, tables, schemaTables, schemaColumns, columnStorages));
        Assert.Equal("Sales", metric.TableName);
        Assert.Equal("Order Id", metric.ColumnName);
        Assert.Equal(1000, metric.RowCount);
        Assert.Equal(800, metric.Cardinality);
        Assert.Equal(2020, metric.TotalSizeBytes);
        Assert.Equal(2, metric.SegmentCount);
    }

    [Fact]
    public void Sample_exports_every_power_query_entity_with_stable_columns()
    {
        var exporter = new PowerQueryExportService();
        var sample = SampleProject.Create();
        foreach (var item in exporter.GetCatalog())
        {
            var result = exporter.Export(sample, item.Name);
            Assert.NotEmpty(result.Columns);
            Assert.All(result.Rows, row => Assert.All(row.Keys, key => Assert.Contains(key, result.Columns)));
        }
    }

    [Fact]
    public void Comparison_detects_added_removed_and_modified_objects()
    {
        var baseline = SampleProject.Create();
        var changedTables = baseline.Tables.Select(table => table.Name == "Sales"
            ? table with { Measures = table.Measures.Where(m => m.Name != "Orders").Select(m => m.Name == "Revenue" ? m with { Expression = "SUMX(Sales, Sales[Net Sales])" } : m).Append(new ModelMeasure("Margin", "[Revenue]", null, false, null, null)).ToList() }
            : table).ToList();
        var current = baseline with { Tables = changedTables };

        var result = new SnapshotComparisonService().Compare(baseline, current);
        Assert.Contains(result.Changes, x => x.ChangeType == "removed" && x.ObjectName == "Sales[Orders]");
        Assert.Contains(result.Changes, x => x.ChangeType == "modified" && x.ObjectName == "Sales[Revenue]");
        Assert.Contains(result.Changes, x => x.ChangeType == "added" && x.ObjectName == "Sales[Margin]");
    }

    [Fact]
    public void Path_policy_rejects_paths_outside_whitelist()
    {
        var allowed = Path.Combine(_root, "allowed"); Directory.CreateDirectory(allowed);
        var policy = new ProjectPathPolicy(Options.Create(new ProjectAccessOptions { AllowedRoots = new[] { allowed } }));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowed)), policy.Resolve(allowed));
        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve(Path.Combine(_root, "elsewhere")));
    }

    [Fact]
    public async Task Cache_coalesces_parallel_first_loads_and_invalidates_after_file_change()
    {
        var project = CreateMinimalProject();
        var cache = new ProjectSnapshotCache(new PowerBiProjectParser());
        var first = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync(project)));
        Assert.Single(first.Select(x => x.ScannedAt).Distinct());

        await Task.Delay(20);
        File.AppendAllText(Path.Combine(project, "Retail.SemanticModel", "definition", "tables", "Sales.tmdl"), Environment.NewLine + "\tcolumn Extra\n\t\tdataType: string\n");
        var changed = await cache.GetAsync(project);
        Assert.NotEqual(first[0].ScannedAt, changed.ScannedAt);
    }

    [Fact]
    public async Task Cache_returns_last_successful_snapshot_when_new_parse_fails()
    {
        var project = CreateMinimalProject();
        var cache = new ProjectSnapshotCache(new PowerBiProjectParser());
        var first = await cache.GetAsync(project);
        Directory.Delete(project, true);
        var fallback = await cache.GetAsync(project, refresh: true);
        Assert.Equal(first.Name, fallback.Name);
        Assert.Contains(fallback.Warnings, warning => warning.Contains("上一次成功快照"));
    }

    [Fact]
    public void Parser_reads_tmdl_measure_page_and_visual()
    {
        var project = CreateMinimalProject();
        var result = new PowerBiProjectParser().Parse(project);
        Assert.Equal("SUM(Sales[Amount])", Assert.Single(Assert.Single(result.Tables).Measures).Expression);
        var page = Assert.Single(result.Pages);
        var visual = Assert.Single(page.Visuals);
        Assert.Equal(2, result.ReportFilterCount);
        Assert.Equal(1, page.FilterCount);
        Assert.Equal(2, visual.FilterCount);
        Assert.Contains(result.Issues, issue => issue.Id == "REPORT-VISUAL-TITLE" && issue.TargetId == "Visual1");
        Assert.Contains(result.Issues, issue => issue.Id == "REPORT-VISUAL-ALT-TEXT" && issue.TargetId == "Visual1");
    }

    [Fact]
    public void Report_quality_excludes_bookmark_managed_visual_layers_from_overlap_rules()
    {
        var project = CreateMinimalProject();
        var secondVisualRoot = Path.Combine(project, "Retail.Report", "definition", "pages", "Page1", "visuals", "Visual2");
        var bookmarksRoot = Path.Combine(project, "Retail.Report", "definition", "bookmarks");
        Directory.CreateDirectory(secondVisualRoot);
        Directory.CreateDirectory(bookmarksRoot);
        File.WriteAllText(Path.Combine(secondVisualRoot, "visual.json"), "{\"name\":\"Visual2\",\"position\":{\"x\":10,\"y\":10,\"width\":200,\"height\":100,\"tabOrder\":2},\"visual\":{\"visualType\":\"card\"}}");
        File.WriteAllText(Path.Combine(bookmarksRoot, "Bookmark1.bookmark.json"), "{\"name\":\"Bookmark1\",\"options\":{\"applyOnlyToTargetVisuals\":true,\"targetVisualNames\":[\"Visual1\",\"Visual2\"]},\"explorationState\":{\"activeSection\":\"Page1\"}}");

        var result = new PowerBiProjectParser().Parse(project);

        Assert.DoesNotContain(result.Issues, issue => issue.Id is "REPORT-OVERLAP" or "REPORT-DUPLICATE-VISUAL");
    }

    [Fact]
    public void Optimizer_blocks_referenced_objects_and_marks_unreferenced_as_candidates()
    {
        var snapshot = SampleProject.Create();
        var result = ModelOptimizationAnalyzer.Analyze(snapshot);
        Assert.Contains(result.RemovalCandidates, x => x.ObjectType == "measure" && x.ObjectName == "Revenue" && x.Status == "blocked");
        Assert.Contains(result.RemovalCandidates, x => x.ObjectType == "column" && x.ObjectName == "Product Key" && x.Status == "blocked");
        Assert.Contains(result.RemovalCandidates, x => x.ObjectType == "column" && x.ObjectName == "Region" && x.Status == "blocked");
    }

    [Fact]
    public void Optimizer_detects_public_dax_best_practices()
    {
        var baseline = SampleProject.Create();
        var measures = new[]
        {
            new ModelMeasure("Unsafe Ratio", "IF([Revenue] = 0, BLANK(), [Orders] / [Revenue])", null, false, null, null),
            new ModelMeasure("Filtered", "CALCULATE([Revenue], FILTER(Sales, Sales[Quantity] > 0))", null, false, null, null)
        };
        var snapshot = baseline with { Tables = baseline.Tables.Select(t => t.Name == "Sales" ? t with { Measures = measures } : t).ToList() };
        var result = ModelOptimizationAnalyzer.Analyze(snapshot);
        Assert.Contains(result.Suggestions, x => x.RuleId == "DAX-002" && x.MeasureName == "Unsafe Ratio");
        Assert.Contains(result.Suggestions, x => x.RuleId == "DAX-003" && x.MeasureName == "Filtered");
    }

    [Fact]
    public async Task Safe_change_applies_only_to_managed_copy_and_rollback_restores_it()
    {
        var project = CreateMinimalProject();
        var sourceFile = Path.Combine(project, "Retail.SemanticModel", "definition", "tables", "Sales.tmdl");
        var sourceBefore = await File.ReadAllBytesAsync(sourceFile);
        var service = CreateSafeChangeService();

        var plan = await service.CreatePlanAsync(project, new[] { new SafeChangeSelection("measure", "Sales", "Revenue") }, CancellationToken.None);
        service = CreateSafeChangeService(); // 模拟进程重启后从持久化计划继续。
        var applied = await service.ApplyAsync(plan.PlanId, plan.ConfirmationPhrase, CancellationToken.None);

        Assert.Equal("applied", applied.Status);
        Assert.NotNull(applied.WorkspacePath);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(sourceFile));
        var workspaceFile = Path.Combine(applied.WorkspacePath!, "Retail.SemanticModel", "definition", "tables", "Sales.tmdl");
        Assert.Contains("\tisHidden", await File.ReadAllTextAsync(workspaceFile));
        Assert.True(File.Exists(Path.Combine(applied.WorkspacePath!, ".powertools", "audit.json")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(applied.WorkspacePath!, ".powertools", "backups"), "Sales.tmdl", SearchOption.AllDirectories).Any());

        service = CreateSafeChangeService();
        var rolledBack = await service.RollbackAsync(plan.PlanId, plan.RollbackPhrase, CancellationToken.None);
        Assert.Equal("rolled-back", rolledBack.Status);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(sourceFile));
        Assert.DoesNotContain("\tisHidden", await File.ReadAllTextAsync(workspaceFile));
    }

    [Fact]
    public async Task Safe_change_rejects_blocked_candidates()
    {
        var project = CreateMinimalProject();
        var service = CreateSafeChangeService();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.CreatePlanAsync(
            project, new[] { new SafeChangeSelection("column", "Sales", "Amount") }, CancellationToken.None));

        Assert.Contains("风险门禁", error.Message);
    }

    [Fact]
    public async Task Safe_change_detects_source_drift_before_copying()
    {
        var project = CreateMinimalProject();
        var sourceFile = Path.Combine(project, "Retail.SemanticModel", "definition", "tables", "Sales.tmdl");
        var service = CreateSafeChangeService();
        var plan = await service.CreatePlanAsync(project, new[] { new SafeChangeSelection("measure", "Sales", "Revenue") }, CancellationToken.None);
        await File.AppendAllTextAsync(sourceFile, "\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(plan.PlanId, plan.ConfirmationPhrase, CancellationToken.None));

        Assert.Contains("发生变化", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_managedRoot, "workspaces")) && Directory.EnumerateDirectories(Path.Combine(_managedRoot, "workspaces")).Any());
    }

    [Fact]
    public async Task Safe_change_rejects_control_directories_inside_source()
    {
        var project = CreateMinimalProject();
        var service = new SafeChangeService(new ProjectSnapshotCache(new PowerBiProjectParser()), Options.Create(new SafeChangeOptions
        {
            WorkspaceRoot = Path.Combine(project, ".managed-workspaces"),
            PlanRoot = Path.Combine(_managedRoot, "plans")
        }));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.CreatePlanAsync(
            project, new[] { new SafeChangeSelection("measure", "Sales", "Revenue") }, CancellationToken.None));

        Assert.Contains("不能配置", error.Message);
    }

    [Fact]
    public async Task Update_service_prefers_verified_delta_and_validates_download()
    {
        var bytes = Encoding.UTF8.GetBytes("verified delta package");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var release = JsonSerializer.Serialize(new
        {
            tag_name = "v1.1.0",
            name = "PowerTools 1.1.0",
            body = "安全更新",
            html_url = "https://github.com/muxiaoqi007/powertools/releases/tag/v1.1.0",
            published_at = "2026-08-14T00:00:00Z",
            assets = new[]
            {
                new { name = "PowerTools-Delta-1.0.0-to-1.1.0-win-x64.zip", browser_download_url = "https://github.com/muxiaoqi007/powertools/releases/download/v1.1.0/delta.zip", size = bytes.Length, digest = "sha256:" + sha },
                new { name = "PowerTools-Setup-1.1.0-win-x64.exe", browser_download_url = "https://github.com/muxiaoqi007/powertools/releases/download/v1.1.0/setup.exe", size = 1000, digest = "sha256:" + new string('a', 64) }
            }
        });
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var service = new UpdateService(new HttpClient(handler), Options.Create(new UpdateOptions
        {
            CurrentVersionOverride = "1.0.0",
            ChannelManifestName = "",
            StagingRoot = Path.Combine(_managedRoot, "updates")
        }));

        var check = await service.CheckAsync(true, CancellationToken.None);
        var staged = await service.DownloadAsync(false, CancellationToken.None);

        Assert.True(check.UpdateAvailable);
        Assert.Equal("delta", check.Mode);
        Assert.True(check.AutomaticInstallSupported);
        Assert.Equal(sha, staged.PackageSha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(staged.PackagePath));
    }

    [Fact]
    public async Task Update_service_falls_back_to_verified_full_installer()
    {
        var release = """
        {"tag_name":"v2.0.0","name":"2.0","body":"","html_url":"https://github.com/muxiaoqi007/powertools/releases/tag/v2.0.0","assets":[{"name":"PowerTools-Setup-2.0.0-win-x64.exe","browser_download_url":"https://github.com/muxiaoqi007/powertools/releases/download/v2.0.0/setup.exe","size":10,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
        """;
        var service = new UpdateService(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(release, Encoding.UTF8, "application/json")
        })), Options.Create(new UpdateOptions { CurrentVersionOverride = "1.5.0", ChannelManifestName = "", StagingRoot = Path.Combine(_managedRoot, "updates-full") }));

        var check = await service.CheckAsync(true, CancellationToken.None);

        Assert.Equal("full", check.Mode);
        Assert.Equal("PowerTools-Setup-2.0.0-win-x64.exe", check.AssetName);
    }

    [Fact]
    public async Task Update_service_reads_quota_free_release_channel_manifest()
    {
        var calls = 0;
        var channel = """
        {"schemaVersion":1,"version":"1.2.0","name":"PowerTools 1.2.0","notes":"notes","publishedAt":"2026-08-14T00:00:00Z","releaseUrl":"https://github.com/muxiaoqi007/powertools/releases/tag/v1.2.0","assets":[{"name":"PowerTools-Setup-1.2.0-win-x64.exe","url":"https://github.com/muxiaoqi007/powertools/releases/download/v1.2.0/PowerTools-Setup-1.2.0-win-x64.exe","size":20,"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]}
        """;
        var service = new UpdateService(new HttpClient(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(channel, Encoding.UTF8, "application/json") };
        })), Options.Create(new UpdateOptions { CurrentVersionOverride = "1.0.0", StagingRoot = Path.Combine(_managedRoot, "updates-channel") }));

        var check = await service.CheckAsync(true, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("1.2.0", check.LatestVersion);
        Assert.Equal("full", check.Mode);
    }

    [Fact]
    public void Delta_engine_applies_changed_files_and_keeps_removed_files_in_backup()
    {
        var install = Path.Combine(_managedRoot, "installed");
        var work = Path.Combine(_managedRoot, "apply-work");
        var package = Path.Combine(_managedRoot, "delta.zip");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "PowerTools.Desktop.exe"), "fake executable");
        File.WriteAllText(Path.Combine(install, "changed.txt"), "old");
        File.WriteAllText(Path.Combine(install, "removed.txt"), "keep in backup");
        CreateDeltaPackage(package, "1.0.0", "1.1.0", new Dictionary<string, string>
        {
            ["changed.txt"] = "new",
            ["folder/added.txt"] = "added"
        }, new[] { "removed.txt" });

        var result = DeltaUpdateEngine.Apply(package, install, work, "1.0.0", "1.1.0");

        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "changed.txt")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(install, "folder", "added.txt")));
        Assert.False(File.Exists(Path.Combine(install, "removed.txt")));
        Assert.Equal("keep in backup", File.ReadAllText(Path.Combine(result.BackupPath, "removed.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(result.BackupPath, "changed.txt")));
    }

    [Fact]
    public void Delta_engine_rejects_windows_alternate_stream_paths()
    {
        var install = Path.Combine(_managedRoot, "installed-unsafe");
        var package = Path.Combine(_managedRoot, "unsafe-delta.zip");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "PowerTools.Desktop.exe"), "fake executable");
        CreateDeltaPackage(package, "1.0.0", "1.1.0", new Dictionary<string, string> { ["safe.txt:stream"] = "bad" }, Array.Empty<string>());

        Assert.Throws<InvalidDataException>(() => DeltaUpdateEngine.Apply(package, install, Path.Combine(_managedRoot, "unsafe-work"), "1.0.0", "1.1.0"));
        Assert.False(File.Exists(Path.Combine(install, "safe.txt")));
    }

    private static void CreateDeltaPackage(string package, string fromVersion, string toVersion, IReadOnlyDictionary<string, string> files, IReadOnlyList<string> removed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        using var archive = ZipFile.Open(package, ZipArchiveMode.Create);
        var manifestFiles = new List<UpdatePackageFile>();
        foreach (var item in files)
        {
            var bytes = Encoding.UTF8.GetBytes(item.Value);
            manifestFiles.Add(new UpdatePackageFile(item.Key, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length));
            var entry = archive.CreateEntry("payload/" + item.Key.Replace('\\', '/'));
            using var stream = entry.Open();
            stream.Write(bytes);
        }
        var manifest = new UpdatePackageManifest(1, fromVersion, toVersion, "win-x64", DateTimeOffset.UtcNow, manifestFiles, removed);
        var manifestEntry = archive.CreateEntry("update-package.json");
        using var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }

    private SafeChangeService CreateSafeChangeService()
    {
        var options = Options.Create(new SafeChangeOptions
        {
            WorkspaceRoot = Path.Combine(_managedRoot, "workspaces"),
            PlanRoot = Path.Combine(_managedRoot, "plans"),
            MaxOperations = 20
        });
        return new SafeChangeService(new ProjectSnapshotCache(new PowerBiProjectParser()), options);
    }

    private string CreateMinimalProject()
    {
        var tableRoot = Path.Combine(_root, "Retail.SemanticModel", "definition", "tables");
        var visualRoot = Path.Combine(_root, "Retail.Report", "definition", "pages", "Page1", "visuals", "Visual1");
        Directory.CreateDirectory(tableRoot); Directory.CreateDirectory(visualRoot);
        File.WriteAllText(Path.Combine(tableRoot, "Sales.tmdl"), "table Sales\n\tcolumn Amount\n\t\tdataType: decimal\n\tmeasure Revenue = SUM(Sales[Amount])\n");
        File.WriteAllText(Path.Combine(_root, "Retail.Report", "definition", "report.json"), "{\"filterConfig\":{\"filters\":[{},{}]}}");
        File.WriteAllText(Path.Combine(_root, "Retail.Report", "definition", "pages", "pages.json"), "{\"pageOrder\":[\"Page1\"]}");
        File.WriteAllText(Path.Combine(_root, "Retail.Report", "definition", "pages", "Page1", "page.json"), "{\"name\":\"Page1\",\"displayName\":\"Overview\",\"width\":1280,\"height\":720,\"filterConfig\":{\"filters\":[{}]}}");
        File.WriteAllText(Path.Combine(visualRoot, "visual.json"), "{\"name\":\"Visual1\",\"position\":{\"x\":10,\"y\":10,\"width\":200,\"height\":100,\"tabOrder\":-1},\"visual\":{\"visualType\":\"card\"},\"filterConfig\":{\"filters\":[{},{}]}}");
        return _root;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_managedRoot)) Directory.Delete(_managedRoot, true);
    }
}
