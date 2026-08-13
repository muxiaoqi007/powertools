using System.Data.Common;
using Microsoft.AnalysisServices.AdomdClient;
using Tom = Microsoft.AnalysisServices.Tabular;

namespace PowerTools.Services;

public sealed class LivePowerBiModelService
{
    private readonly ILogger<LivePowerBiModelService> _logger;

    public LivePowerBiModelService(ILogger<LivePowerBiModelService> logger) => _logger = logger;

    public LiveModelContext? GetStartupContext()
    {
        var server = Environment.GetEnvironmentVariable("POWERTOOLS_LIVE_SERVER");
        var database = Environment.GetEnvironmentVariable("POWERTOOLS_LIVE_DATABASE");
        return string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) ? null : new(server, database);
    }

    public Task<ProjectSnapshot> ReadAsync(string serverName, string databaseName, CancellationToken cancellationToken) =>
        Task.Run(() => Read(serverName, databaseName, cancellationToken), cancellationToken);

    private ProjectSnapshot Read(string serverName, string databaseName, CancellationToken cancellationToken)
    {
        serverName = ValidateServer(serverName);
        databaseName = databaseName?.Trim() ?? "";
        if (databaseName.Length is 0 or > 256) throw new InvalidDataException("Power BI 模型名称无效。");

        var tomConnectionString = ConnectionString(serverName, databaseName, "PowerTools");
        using var server = new Tom.Server();
        server.Connect(tomConnectionString);
        cancellationToken.ThrowIfCancellationRequested();
        var database = server.Databases.FindByName(databaseName) ?? server.Databases.Find(databaseName)
            ?? throw new InvalidDataException($"未找到 Power BI 模型：{databaseName}");
        var model = database.Model ?? throw new InvalidDataException("当前数据库没有可读取的 Tabular 模型。");
        var warnings = new List<string> { "当前快照来自 Power BI Desktop 实时语义模型；页面、视觉对象与书签需从 PBIP/PBIR 目录读取。" };

        var tables = model.Tables.Select(MapTable).ToList();
        var relationships = model.Relationships.OfType<Tom.SingleColumnRelationship>().Select(MapRelationship).ToList();
        var calculationGroups = model.Tables.Where(table => table.CalculationGroup is not null).Select(MapCalculationGroup).ToList();
        var roles = model.Roles.Select(MapRole).ToList();
        var dependencies = PowerBiProjectParser.BuildDependencies(tables, calculationGroups);
        var storage = ReadStorageMetrics(serverName, databaseName, warnings, cancellationToken);
        var issues = PowerBiProjectParser.AnalyzeQuality(tables, relationships, calculationGroups, roles, Array.Empty<ReportPage>());
        var info = new LiveModelInfo(serverName, databaseName, model.Name ?? database.Name, database.CompatibilityLevel, DateTimeOffset.Now, storage.Count > 0);
        var snapshot = new ProjectSnapshot(model.Name ?? database.Name, $"Power BI Desktop · {databaseName}", "LIVE / TOM", DateTimeOffset.Now,
            tables, relationships, calculationGroups, roles, dependencies, Array.Empty<ReportBookmark>(), Array.Empty<BookmarkGroup>(),
            Array.Empty<ReportPage>(), issues, warnings) { LiveModel = info, StorageMetrics = storage };
        var optimization = ModelOptimizationAnalyzer.Analyze(snapshot);
        return snapshot with { RemovalCandidates = optimization.RemovalCandidates, OptimizationSuggestions = optimization.Suggestions };
    }

    private static ModelTable MapTable(Tom.Table table) => new(
        table.Name,
        table.Description,
        table.IsHidden,
        table.Columns.Select(column => new ModelColumn(column.Name, column.DataType.ToString(), column.IsHidden,
            column is Tom.CalculatedColumn, column is Tom.CalculatedColumn calculated ? calculated.Expression : null, column.Description,
            column.SortByColumn?.Name, column.IsKey, column.IsUnique)).ToList(),
        table.Measures.Select(measure => new ModelMeasure(measure.Name, measure.Expression ?? "", measure.FormatString, measure.IsHidden, measure.Description, measure.DisplayFolder)).ToList(),
        table.Hierarchies.Select(hierarchy => new ModelHierarchy(hierarchy.Name, hierarchy.Levels.OrderBy(level => level.Ordinal).Select(level => level.Column?.Name ?? level.Name).ToList())).ToList(),
        table.Partitions.Select(partition => new ModelPartition(partition.Name, partition.Mode.ToString(), partition.SourceType.ToString(), PartitionExpression(partition.Source))).ToList());

    private static string? PartitionExpression(Tom.PartitionSource? source) => source switch
    {
        Tom.MPartitionSource m => m.Expression,
        Tom.QueryPartitionSource query => query.Query,
        Tom.CalculatedPartitionSource calculated => calculated.Expression,
        _ => null
    };

    private static ModelRelationship MapRelationship(Tom.SingleColumnRelationship relationship) => new(
        relationship.Name,
        relationship.FromTable?.Name ?? "",
        relationship.FromColumn?.Name ?? "",
        relationship.ToTable?.Name ?? "",
        relationship.ToColumn?.Name ?? "",
        relationship.IsActive,
        relationship.CrossFilteringBehavior.ToString(),
        relationship.FromCardinality.ToString(),
        relationship.ToCardinality.ToString());

    private static CalculationGroup MapCalculationGroup(Tom.Table table)
    {
        var group = table.CalculationGroup!;
        return new CalculationGroup(table.Name, group.Precedence, table.IsHidden, group.CalculationItems.Select(item => new CalculationItem(
            item.Name, item.Expression ?? "", item.FormatStringDefinition?.Expression, item.Ordinal, item.Description)).ToList());
    }

    private static SecurityRole MapRole(Tom.ModelRole role)
    {
        var tablePermissions = role.TablePermissions.Where(permission => !string.IsNullOrWhiteSpace(permission.FilterExpression))
            .Select(permission => new TablePermission(permission.Table?.Name ?? permission.Name, permission.FilterExpression)).ToList();
        var objectPermissions = new List<ObjectPermission>();
        foreach (var permission in role.TablePermissions)
        {
            var tableName = permission.Table?.Name ?? permission.Name;
            if (!permission.MetadataPermission.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase))
                objectPermissions.Add(new ObjectPermission(tableName, permission.MetadataPermission.ToString()));
            objectPermissions.AddRange(permission.ColumnPermissions.Select(column => new ObjectPermission(
                $"{tableName}[{column.Column?.Name ?? column.Name}]", column.MetadataPermission.ToString())));
        }
        return new SecurityRole(role.Name, role.ModelPermission.ToString(), tablePermissions, objectPermissions);
    }

    private IReadOnlyList<ModelStorageMetric> ReadStorageMetrics(string serverName, string databaseName, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = new AdomdConnection(ConnectionString(serverName, databaseName, "PowerTools Storage Analyzer"));
            connection.Open();
            var columns = Query(connection, "SELECT * FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS", cancellationToken);
            var segments = Query(connection, "SELECT * FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS", cancellationToken);
            var tables = Query(connection, "SELECT * FROM $SYSTEM.DISCOVER_STORAGE_TABLES", cancellationToken);
            return BuildStorageMetrics(columns, segments, tables);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read VertiPaq storage DMVs for {Database}", databaseName);
            warnings.Add($"未能读取 VertiPaq 存储指标：{ex.Message}");
            return Array.Empty<ModelStorageMetric>();
        }
    }

    private static List<Dictionary<string, object?>> Query(AdomdConnection connection, string commandText, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 15;
        using var reader = command.ExecuteReader();
        var result = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    public static IReadOnlyList<ModelStorageMetric> BuildStorageMetrics(
        IReadOnlyList<Dictionary<string, object?>> columns,
        IReadOnlyList<Dictionary<string, object?>> segments,
        IReadOnlyList<Dictionary<string, object?>> tables)
    {
        static string Text(IReadOnlyDictionary<string, object?> row, params string[] names) => names.Select(name => row.TryGetValue(name, out var value) ? value?.ToString() : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        static long? Number(IReadOnlyDictionary<string, object?> row, params string[] names)
        {
            var text = Text(row, names);
            return long.TryParse(text, out var value) ? value : null;
        }
        var segmentGroups = segments.GroupBy(row => $"{Text(row, "TABLE_ID")}\u001f{Text(row, "COLUMN_ID")}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new
            {
                Size = group.Sum(row => Number(row, "USED_SIZE", "DATA_SIZE") ?? 0),
                Rows = group.Sum(row => Number(row, "RECORDS_COUNT") ?? 0),
                Count = group.Count()
            }, StringComparer.OrdinalIgnoreCase);
        var tableRows = tables.GroupBy(row => Text(row, "TABLE_ID"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => Number(row, "RECORDS_COUNT") ?? 0), StringComparer.OrdinalIgnoreCase);
        var metrics = new List<ModelStorageMetric>();
        foreach (var column in columns)
        {
            var tableId = Text(column, "TABLE_ID");
            var columnId = Text(column, "COLUMN_ID");
            var tableName = Text(column, "DIMENSION_NAME", "TABLE_NAME");
            var columnName = Text(column, "ATTRIBUTE_NAME", "COLUMN_NAME");
            if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(columnName) || columnName.StartsWith("RowNumber-", StringComparison.OrdinalIgnoreCase)) continue;
            segmentGroups.TryGetValue($"{tableId}\u001f{columnId}", out var segment);
            var dictionary = Number(column, "DICTIONARY_SIZE") ?? 0;
            var hierarchy = Number(column, "HIERARCHY_SIZE") ?? 0;
            var data = segment?.Size ?? Number(column, "USED_SIZE", "DATA_SIZE") ?? 0;
            var rows = tableRows.TryGetValue(tableId, out var tableRowCount) ? tableRowCount : segment?.Rows;
            metrics.Add(new ModelStorageMetric(tableName, columnName, rows, Number(column, "COLUMN_CARDINALITY", "CARDINALITY"), data, dictionary, hierarchy, data + dictionary + hierarchy, segment?.Count ?? 0));
        }
        return metrics.OrderByDescending(metric => metric.TotalSizeBytes).ToList();
    }

    private static string ValidateServer(string? value)
    {
        var server = value?.Trim() ?? "";
        var host = server.Split(':', 2)[0].Trim('[', ']');
        var loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("127.0.0.1") ||
            server.Equals("::1", StringComparison.OrdinalIgnoreCase) || server.StartsWith("[::1]:", StringComparison.OrdinalIgnoreCase);
        if (server.Length > 128 || !loopback)
            throw new UnauthorizedAccessException("实时连接仅允许 Power BI Desktop 的本机 Analysis Services 地址。");
        return server;
    }

    private static string ConnectionString(string server, string database, string applicationName)
    {
        var builder = new DbConnectionStringBuilder
        {
            ["Data Source"] = server,
            ["Initial Catalog"] = database,
            ["Application Name"] = applicationName,
            ["Connect Timeout"] = 10
        };
        return builder.ConnectionString;
    }
}

public sealed record LiveModelContext(string Server, string Database);
public sealed record OpenLiveModelRequest(string Server, string Database);
