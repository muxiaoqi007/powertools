using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerTools.Services;

public sealed class SnapshotComparisonService
{
    public SnapshotComparison Compare(ProjectSnapshot baseline, ProjectSnapshot current)
    {
        var changes = new List<SnapshotChange>();
        CompareObjects("Table", baseline.Tables.Select(t => new { t.Name, t.Description, t.IsHidden }), current.Tables.Select(t => new { t.Name, t.Description, t.IsHidden }), x => x.Name, changes);
        CompareObjects("Column", FlattenColumns(baseline), FlattenColumns(current), x => x.Key, changes);
        CompareObjects("Measure", FlattenMeasures(baseline), FlattenMeasures(current), x => x.Key, changes);
        CompareObjects("Relationship", baseline.Relationships, current.Relationships, x => x.Name, changes);
        CompareObjects("CalculationGroup", baseline.CalculationGroups.Select(g => new { g.Name, g.Precedence, g.IsHidden }), current.CalculationGroups.Select(g => new { g.Name, g.Precedence, g.IsHidden }), x => x.Name, changes);
        CompareObjects("CalculationItem", FlattenCalculationItems(baseline), FlattenCalculationItems(current), x => x.Key, changes);
        CompareObjects("Role", baseline.Roles, current.Roles, x => x.Name, changes);
        CompareObjects("Dependency", baseline.Dependencies, current.Dependencies, x => $"{x.SourceId}>{x.TargetId}", changes);
        CompareObjects("Page", baseline.Pages.Select(p => new { p.Name, p.DisplayName, p.Width, p.Height, p.IsHidden, p.DisplayOption }), current.Pages.Select(p => new { p.Name, p.DisplayName, p.Width, p.Height, p.IsHidden, p.DisplayOption }), x => x.Name, changes);
        CompareObjects("Visual", FlattenVisuals(baseline), FlattenVisuals(current), x => x.Key, changes);
        CompareObjects("Bookmark", baseline.Bookmarks, current.Bookmarks, x => x.Name, changes);
        CompareObjects("BookmarkGroup", baseline.BookmarkGroups, current.BookmarkGroups, x => x.Name, changes);

        return new SnapshotComparison(baseline.Name, baseline.Path, current.Name, current.Path, DateTimeOffset.Now,
            changes.Count(x => x.ChangeType == "added"), changes.Count(x => x.ChangeType == "removed"),
            changes.Count(x => x.ChangeType == "modified"), changes.OrderBy(x => x.ObjectType).ThenBy(x => x.ObjectName).ToList());
    }

    private static IEnumerable<NamedObject<ModelColumn>> FlattenColumns(ProjectSnapshot snapshot) => snapshot.Tables.SelectMany(t => t.Columns.Select(x => new NamedObject<ModelColumn>($"{t.Name}[{x.Name}]", x)));
    private static IEnumerable<NamedObject<ModelMeasure>> FlattenMeasures(ProjectSnapshot snapshot) => snapshot.Tables.SelectMany(t => t.Measures.Select(x => new NamedObject<ModelMeasure>($"{t.Name}[{x.Name}]", x)));
    private static IEnumerable<NamedObject<CalculationItem>> FlattenCalculationItems(ProjectSnapshot snapshot) => snapshot.CalculationGroups.SelectMany(g => g.Items.Select(x => new NamedObject<CalculationItem>($"{g.Name}[{x.Name}]", x)));
    private static IEnumerable<NamedObject<ReportVisual>> FlattenVisuals(ProjectSnapshot snapshot) => snapshot.Pages.SelectMany(p => p.Visuals.Select(x => new NamedObject<ReportVisual>($"{p.Name}/{x.Name}", x)));

    private static void CompareObjects<T>(string type, IEnumerable<T> baseline, IEnumerable<T> current, Func<T, string> keySelector, List<SnapshotChange> changes)
    {
        var oldMap = baseline.ToDictionary(keySelector, StringComparer.OrdinalIgnoreCase);
        var newMap = current.ToDictionary(keySelector, StringComparer.OrdinalIgnoreCase);
        foreach (var key in newMap.Keys.Except(oldMap.Keys, StringComparer.OrdinalIgnoreCase)) changes.Add(new SnapshotChange("added", type, key, null, Summarize(newMap[key])));
        foreach (var key in oldMap.Keys.Except(newMap.Keys, StringComparer.OrdinalIgnoreCase)) changes.Add(new SnapshotChange("removed", type, key, Summarize(oldMap[key]), null));
        foreach (var key in oldMap.Keys.Intersect(newMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var beforeHash = Fingerprint(oldMap[key]); var afterHash = Fingerprint(newMap[key]);
            if (beforeHash != afterHash) changes.Add(new SnapshotChange("modified", type, key, Summarize(oldMap[key]), Summarize(newMap[key])));
        }
    }

    private static string Fingerprint<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12];
    }

    private static string Summarize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return json.Length <= 800 ? json : json[..797] + "...";
    }

    private sealed record NamedObject<T>(string Key, T Value);
}

public sealed record SnapshotComparison(string BaselineName, string BaselinePath, string CurrentName, string CurrentPath,
    DateTimeOffset ComparedAt, int AddedCount, int RemovedCount, int ModifiedCount, IReadOnlyList<SnapshotChange> Changes);
public sealed record SnapshotChange(string ChangeType, string ObjectType, string ObjectName, string? Before, string? After);
