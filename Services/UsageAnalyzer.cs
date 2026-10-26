namespace PowerTools.Services;

public static class UsageAnalyzer
{
    public static UsageAnalysis Analyze(ProjectSnapshot snapshot)
    {
        var model = new ModelIndex(snapshot);
        var pageOrder = snapshot.Pages
            .Select((page, index) => (page.Name, index))
            .ToDictionary(x => x.Name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<PageUsage>(snapshot.Pages.Count);

        foreach (var page in snapshot.Pages)
        {
            var pageObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visualCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var visual in page.Visuals)
            {
                foreach (var field in visual.Fields)
                {
                    var target = model.Resolve(field);
                    if (target is null)
                    {
                        if (!string.IsNullOrWhiteSpace(field)) unresolved.Add(field.Trim());
                        continue;
                    }
                    pageObjects.Add(target.Key);
                    visualCounts[target.Key] = visualCounts.TryGetValue(target.Key, out var count) ? count + 1 : 1;
                    target.RegisterVisual(page.Name, DisplayName(page));
                }
            }

            var dataVisuals = page.Visuals.Where(IsDataVisual).ToList();
            var visualFilterCount = page.Visuals.Sum(visual => visual.FilterCount);
            var topObjects = visualCounts
                .Select(item => new { Object = model.Objects[item.Key], Count = item.Value })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Object.TableName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Object.ObjectName, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(item => new UsageTopObject(item.Object.ObjectType, item.Object.TableName, item.Object.ObjectName, item.Count))
                .ToList();

            pages.Add(new PageUsage(
                page.Name,
                DisplayName(page),
                page.IsHidden,
                page.IsTooltip,
                page.DrillthroughFilterCount > 0,
                page.Visuals.Count,
                dataVisuals.Count,
                pageObjects.Count(key => model.Objects[key].ObjectType == "column"),
                pageObjects.Count(key => model.Objects[key].ObjectType == "measure"),
                page.FilterCount + visualFilterCount,
                page.FilterCount,
                visualFilterCount,
                dataVisuals.Count(visual => visual.HasExplicitTitle),
                dataVisuals.Count(visual => visual.HasAltText),
                topObjects));
        }

        var candidates = snapshot.RemovalCandidates
            .GroupBy(candidate => Key(candidate.ObjectType, candidate.TableName, candidate.ObjectName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var objects = model.Objects.Values
            .Select(state =>
            {
                var status = state.VisualCount > 0
                    ? "page"
                    : candidates.TryGetValue(state.Key, out var candidate) && candidate.Status.Equals("blocked", StringComparison.OrdinalIgnoreCase)
                        ? "model"
                        : "unused";
                var usedPages = state.Pages.Values
                    .OrderBy(page => pageOrder.TryGetValue(page.Name, out var index) ? index : int.MaxValue)
                    .ThenBy(page => page.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new ObjectUsage(state.ObjectType, state.TableName, state.ObjectName, state.IsHidden, status,
                    usedPages.Count, state.VisualCount, usedPages);
            })
            .OrderBy(item => item.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ObjectType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summary = new UsageSummary(
            pages.Count,
            pages.Count(page => page.IsHidden),
            pages.Count(page => page.IsTooltip),
            pages.Count(page => page.IsDrillthrough),
            pages.Sum(page => page.VisualCount),
            pages.Sum(page => page.DataVisualCount),
            objects.Count(item => item.ObjectType == "column" && item.VisualCount > 0),
            objects.Count(item => item.ObjectType == "measure" && item.VisualCount > 0),
            objects.Count(item => item.PageCount >= 2),
            objects.Count(item => item.UsageStatus == "model"),
            objects.Count(item => item.UsageStatus == "unused"),
            unresolved.Count,
            unresolved.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList());

        return new UsageAnalysis(pages, objects, summary);
    }

    private static bool IsDataVisual(ReportVisual visual) => !visual.IsHidden && !visual.IsDecorative && !visual.IsGroup;

    private static string DisplayName(ReportPage page) => string.IsNullOrWhiteSpace(page.DisplayName) ? page.Name : page.DisplayName;

    private static string Key(string objectType, string tableName, string objectName) => $"{objectType}:{tableName}:{objectName}";

    private sealed class ModelIndex
    {
        private readonly Dictionary<string, ObjectState> _fullReferences = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ObjectState> _objects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ObjectState>> _measures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ObjectState>> _columns = new(StringComparer.OrdinalIgnoreCase);

        public ModelIndex(ProjectSnapshot snapshot)
        {
            foreach (var table in snapshot.Tables)
            {
                foreach (var measure in table.Measures)
                {
                    var state = new ObjectState("measure", table.Name, measure.Name, measure.IsHidden);
                    _fullReferences[FullReference(table.Name, measure.Name)] = state;
                    _objects[state.Key] = state;
                    Add(_measures, measure.Name, state);
                }
                foreach (var column in table.Columns)
                {
                    var state = new ObjectState("column", table.Name, column.Name, column.IsHidden);
                    _fullReferences[FullReference(table.Name, column.Name)] = state;
                    _objects[state.Key] = state;
                    Add(_columns, column.Name, state);
                }
            }
        }

        public IReadOnlyDictionary<string, ObjectState> Objects => _objects;

        public ObjectState? Resolve(string? reference)
        {
            var normalized = Normalize(reference);
            if (normalized.Length == 0) return null;
            if (_fullReferences.TryGetValue(normalized, out var exact)) return exact;
            var (table, name) = Split(normalized);
            if (table is not null) return null;
            return Unique(_measures, name) ?? Unique(_columns, name);
        }

        private static void Add(Dictionary<string, List<ObjectState>> index, string name, ObjectState state)
        {
            if (!index.TryGetValue(name, out var list)) index[name] = list = new List<ObjectState>();
            list.Add(state);
        }

        private static ObjectState? Unique(Dictionary<string, List<ObjectState>> index, string name) =>
            index.TryGetValue(name, out var list) && list.Count == 1 ? list[0] : null;

        private static string Normalize(string? value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0) return text;
            var open = text.IndexOf('[');
            var close = text.LastIndexOf(']');
            if (open <= 0 || close <= open) return text;
            var table = text[..open].Trim();
            if (table.Length >= 2 && table[0] == '\'' && table[^1] == '\'') table = table[1..^1].Replace("''", "'", StringComparison.Ordinal);
            return FullReference(table, text[(open + 1)..close].Trim());
        }

        private static (string? Table, string Name) Split(string normalized)
        {
            var open = normalized.IndexOf('[');
            var close = normalized.LastIndexOf(']');
            if (open == 0 && close == normalized.Length - 1) return (null, normalized[1..close].Trim());
            return open > 0 && close > open
                ? (normalized[..open].Trim(), normalized[(open + 1)..close].Trim())
                : (null, normalized.Trim());
        }

        private static string FullReference(string table, string name) => $"{table.Trim()}[{name.Trim()}]";
    }

    private sealed class ObjectState(string objectType, string tableName, string objectName, bool isHidden)
    {
        public string ObjectType { get; } = objectType;
        public string TableName { get; } = tableName;
        public string ObjectName { get; } = objectName;
        public bool IsHidden { get; } = isHidden;
        public string Key { get; } = $"{objectType}:{tableName}:{objectName}";
        public int VisualCount { get; private set; }
        public Dictionary<string, UsagePageRef> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterVisual(string pageName, string displayName)
        {
            VisualCount++;
            Pages.TryAdd(pageName, new UsagePageRef(pageName, displayName));
        }
    }
}
