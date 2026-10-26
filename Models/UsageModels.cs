namespace PowerTools;

public sealed record UsageAnalysis(
    IReadOnlyList<PageUsage> Pages,
    IReadOnlyList<ObjectUsage> Objects,
    UsageSummary Summary);

public sealed record PageUsage(
    string PageName,
    string DisplayName,
    bool IsHidden,
    bool IsTooltip,
    bool IsDrillthrough,
    int VisualCount,
    int DataVisualCount,
    int ColumnCount,
    int MeasureCount,
    int FilterCount,
    int PageFilterCount,
    int VisualFilterCount,
    int TitleCount,
    int AltTextCount,
    IReadOnlyList<UsageTopObject> TopObjects);

public sealed record UsageTopObject(
    string ObjectType,
    string TableName,
    string ObjectName,
    int VisualCount);

public sealed record ObjectUsage(
    string ObjectType,
    string TableName,
    string ObjectName,
    bool IsHidden,
    string UsageStatus,
    int PageCount,
    int VisualCount,
    IReadOnlyList<UsagePageRef> Pages);

public sealed record UsagePageRef(string Name, string DisplayName);

public sealed record UsageSummary(
    int PageCount,
    int HiddenPageCount,
    int TooltipPageCount,
    int DrillthroughPageCount,
    int VisualCount,
    int DataVisualCount,
    int ReferencedColumnCount,
    int ReferencedMeasureCount,
    int CrossPageObjectCount,
    int ModelOnlyObjectCount,
    int UnusedObjectCount,
    int UnresolvedReferenceCount,
    IReadOnlyList<string> UnresolvedReferences);
