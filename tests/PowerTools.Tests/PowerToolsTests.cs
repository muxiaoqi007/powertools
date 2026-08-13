using Microsoft.Extensions.Options;
using PowerTools;
using PowerTools.Services;
using Xunit;

namespace PowerTools.Tests;

public sealed class PowerToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "powertools-tests-" + Guid.NewGuid().ToString("N"));

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
        Assert.Single(result.Pages);
        Assert.Single(result.Pages[0].Visuals);
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

    private string CreateMinimalProject()
    {
        var tableRoot = Path.Combine(_root, "Retail.SemanticModel", "definition", "tables");
        var visualRoot = Path.Combine(_root, "Retail.Report", "definition", "pages", "Page1", "visuals", "Visual1");
        Directory.CreateDirectory(tableRoot); Directory.CreateDirectory(visualRoot);
        File.WriteAllText(Path.Combine(tableRoot, "Sales.tmdl"), "table Sales\n\tcolumn Amount\n\t\tdataType: decimal\n\tmeasure Revenue = SUM(Sales[Amount])\n");
        File.WriteAllText(Path.Combine(_root, "Retail.Report", "definition", "pages", "pages.json"), "{\"pageOrder\":[\"Page1\"]}");
        File.WriteAllText(Path.Combine(_root, "Retail.Report", "definition", "pages", "Page1", "page.json"), "{\"name\":\"Page1\",\"displayName\":\"Overview\",\"width\":1280,\"height\":720}");
        File.WriteAllText(Path.Combine(visualRoot, "visual.json"), "{\"name\":\"Visual1\",\"position\":{\"x\":10,\"y\":10,\"width\":200,\"height\":100},\"visual\":{\"visualType\":\"card\"}}");
        return _root;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
