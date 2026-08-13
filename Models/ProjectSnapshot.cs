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
        issueCount = Issues.Count
    };
}

public sealed record ModelTable(
    string Name,
    string? Description,
    bool IsHidden,
    IReadOnlyList<ModelColumn> Columns,
    IReadOnlyList<ModelMeasure> Measures,
    IReadOnlyList<ModelHierarchy> Hierarchies,
    IReadOnlyList<ModelPartition> Partitions);

public sealed record ModelColumn(string Name, string DataType, bool IsHidden, bool IsCalculated, string? Expression, string? Description);
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
    IReadOnlyList<ReportVisual> Visuals);

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
    string SourceFile);

public sealed record QualityIssue(
    string Id,
    string Severity,
    string Category,
    string Title,
    string Detail,
    string ObjectName,
    string? PageName = null);
