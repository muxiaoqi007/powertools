namespace PowerTools;

public sealed record ProjectSnapshot(
    string Name,
    string Path,
    string Format,
    DateTimeOffset ScannedAt,
    IReadOnlyList<ModelTable> Tables,
    IReadOnlyList<ModelRelationship> Relationships,
    IReadOnlyList<CalculationGroup> CalculationGroups,
    IReadOnlyList<SecurityRole> Roles,
    IReadOnlyList<ModelDependency> Dependencies,
    IReadOnlyList<ReportBookmark> Bookmarks,
    IReadOnlyList<BookmarkGroup> BookmarkGroups,
    IReadOnlyList<ReportPage> Pages,
    IReadOnlyList<QualityIssue> Issues,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<RemovalCandidate> RemovalCandidates { get; init; } = Array.Empty<RemovalCandidate>();
    public IReadOnlyList<MeasureOptimizationSuggestion> OptimizationSuggestions { get; init; } = Array.Empty<MeasureOptimizationSuggestion>();
    public LiveModelInfo? LiveModel { get; init; }
    public IReadOnlyList<ModelStorageMetric> StorageMetrics { get; init; } = Array.Empty<ModelStorageMetric>();
    public int ReportFilterCount { get; init; }

    public object ReportQuality => new
    {
        reportFilterCount = ReportFilterCount,
        pageFilterCount = Pages.Sum(page => page.FilterCount),
        visualFilterCount = Pages.Sum(page => page.Visuals.Sum(visual => visual.FilterCount)),
        tooltipPageCount = Pages.Count(page => page.IsTooltip),
        drillthroughPageCount = Pages.Count(page => page.DrillthroughFilterCount > 0),
        dataVisualCount = Pages.Sum(page => page.Visuals.Count(visual => !visual.IsHidden && !visual.IsDecorative && !visual.IsGroup)),
        explicitTitleCount = Pages.Sum(page => page.Visuals.Count(visual => !visual.IsHidden && !visual.IsDecorative && !visual.IsGroup && visual.HasExplicitTitle)),
        altTextCount = Pages.Sum(page => page.Visuals.Count(visual => !visual.IsHidden && !visual.IsDecorative && !visual.IsGroup && visual.HasAltText)),
        filteredVisualCount = Pages.Sum(page => page.Visuals.Count(visual => visual.FilterCount > 0))
    };

    public object Summary => new
    {
        tableCount = Tables.Count,
        columnCount = Tables.Sum(table => table.Columns.Count),
        measureCount = Tables.Sum(table => table.Measures.Count),
        relationshipCount = Relationships.Count,
        calculationGroupCount = CalculationGroups.Count,
        calculationItemCount = CalculationGroups.Sum(group => group.Items.Count),
        roleCount = Roles.Count,
        rlsRuleCount = Roles.Sum(role => role.TablePermissions.Count),
        dependencyCount = Dependencies.Count,
        bookmarkCount = Bookmarks.Count,
        bookmarkGroupCount = BookmarkGroups.Count,
        pageCount = Pages.Count,
        visualCount = Pages.Sum(page => page.Visuals.Count),
        reportFilterCount = ReportFilterCount,
        pageFilterCount = Pages.Sum(page => page.FilterCount),
        visualFilterCount = Pages.Sum(page => page.Visuals.Sum(visual => visual.FilterCount)),
        storageSizeBytes = StorageMetrics.Sum(metric => metric.TotalSizeBytes ?? 0),
        issueCount = Issues.Count
    };
}

public sealed record RemovalCandidate(
    string ObjectType,
    string TableName,
    string ObjectName,
    string Status,
    string Confidence,
    int RiskScore,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Evidence,
    long? EstimatedSavingsBytes = null);

public sealed record LiveModelInfo(
    string Server,
    string Database,
    string ModelName,
    int CompatibilityLevel,
    DateTimeOffset ConnectedAt,
    bool StorageMetricsAvailable);

public sealed record ModelStorageMetric(
    string TableName,
    string? ColumnName,
    long? RowCount,
    long? Cardinality,
    long? DataSizeBytes,
    long? DictionarySizeBytes,
    long? HierarchySizeBytes,
    long? TotalSizeBytes,
    int SegmentCount);

public sealed record MeasureOptimizationSuggestion(
    string RuleId,
    string Category,
    string Severity,
    int Priority,
    string TableName,
    string MeasureName,
    string Title,
    string Detail,
    string Recommendation,
    string SourceName,
    string SourceUrl);

public sealed record ModelTable(
    string Name,
    string? Description,
    bool IsHidden,
    IReadOnlyList<ModelColumn> Columns,
    IReadOnlyList<ModelMeasure> Measures,
    IReadOnlyList<ModelHierarchy> Hierarchies,
    IReadOnlyList<ModelPartition> Partitions);

public sealed record ModelColumn(
    string Name,
    string DataType,
    bool IsHidden,
    bool IsCalculated,
    string? Expression,
    string? Description,
    string? SortByColumn = null,
    bool IsKey = false,
    bool IsUnique = false);
public sealed record ModelMeasure(string Name, string Expression, string? FormatString, bool IsHidden, string? Description, string? DisplayFolder);
public sealed record ModelHierarchy(string Name, IReadOnlyList<string> Levels);
public sealed record ModelPartition(string Name, string? Mode, string? SourceType, string? Expression);

public sealed record CalculationGroup(
    string Name,
    int Precedence,
    bool IsHidden,
    IReadOnlyList<CalculationItem> Items);

public sealed record CalculationItem(
    string Name,
    string Expression,
    string? FormatStringExpression,
    int? Ordinal,
    string? Description);

public sealed record SecurityRole(
    string Name,
    string ModelPermission,
    IReadOnlyList<TablePermission> TablePermissions,
    IReadOnlyList<ObjectPermission> ObjectPermissions);

public sealed record TablePermission(string Table, string Expression);
public sealed record ObjectPermission(string Object, string Permission);

public sealed record ModelDependency(
    string SourceId,
    string SourceName,
    string SourceType,
    string TargetId,
    string TargetName,
    string TargetType,
    string Reference);

public sealed record ModelRelationship(
    string Name,
    string FromTable,
    string FromColumn,
    string ToTable,
    string ToColumn,
    bool IsActive,
    string CrossFilteringBehavior,
    string FromCardinality,
    string ToCardinality);

public sealed record ReportBookmark(
    string Name,
    string DisplayName,
    string? ActivePage,
    bool ApplyOnlyToTargetVisuals,
    bool SuppressData,
    IReadOnlyList<string> TargetVisualNames,
    IReadOnlyList<BookmarkVisualState> VisualStates,
    int ReportFilterCount,
    int VisualFilterCount,
    bool HasDataState,
    string SourceFile);

public sealed record BookmarkVisualState(
    string PageName,
    string VisualName,
    bool? IsHidden,
    string? VisualType,
    int FilterCount);

public sealed record BookmarkGroup(
    string Name,
    string DisplayName,
    IReadOnlyList<string> Children,
    int Order);

public sealed record ReportPage(
    string Name,
    string DisplayName,
    double Width,
    double Height,
    bool IsHidden,
    string? DisplayOption,
    IReadOnlyList<ReportVisual> Visuals,
    int FilterCount = 0,
    int DrillthroughFilterCount = 0,
    bool IsTooltip = false);

public sealed record ReportVisual(
    string Name,
    string Title,
    string Type,
    double X,
    double Y,
    double Width,
    double Height,
    double Z,
    int TabOrder,
    bool IsHidden,
    IReadOnlyList<string> Fields,
    string SourceFile,
    int FilterCount = 0,
    bool HasExplicitTitle = false,
    bool HasAltText = false,
    bool HasTooltip = false,
    bool DrillFilterOtherVisuals = false,
    bool IsGroup = false)
{
    public bool IsDecorative => Type is "shape" or "image" or "textbox" or "actionButton" or "pageNavigator" or "bookmarkNavigator";
}

public sealed record QualityIssue(
    string Id,
    string Severity,
    string Category,
    string Title,
    string Detail,
    string ObjectName,
    string? PageName = null,
    string? TargetId = null);
