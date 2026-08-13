namespace PowerTools.Services;

public sealed class PowerQueryExportService
{
    private static readonly IReadOnlyDictionary<string, EntityDefinition> Definitions = BuildDefinitions();

    public IReadOnlyList<object> GetCatalog() => Definitions.Values
        .Select(definition => (object)new { definition.Name, definition.Description, definition.Columns })
        .ToList();

    public PowerQueryExport Export(ProjectSnapshot snapshot, string entity)
    {
        if (!Definitions.TryGetValue(Normalize(entity), out var definition))
            throw new KeyNotFoundException($"未知 Power Query 实体：{entity}。可用实体：{string.Join(", ", Definitions.Keys)}");

        return new PowerQueryExport(definition.Name, definition.Description, snapshot.Name, snapshot.Format,
            snapshot.ScannedAt, definition.Columns, definition.Rows(snapshot));
    }

    private static IReadOnlyDictionary<string, EntityDefinition> BuildDefinitions()
    {
        var definitions = new[]
        {
            Def("summary", "项目汇总指标", C("metric", "value"), s => SummaryRows(s)),
            Def("tables", "模型表", C("tableName", "description", "isHidden", "columnCount", "measureCount", "hierarchyCount", "partitionCount"), s => s.Tables.Select(t => Row(("tableName", t.Name), ("description", t.Description), ("isHidden", t.IsHidden), ("columnCount", t.Columns.Count), ("measureCount", t.Measures.Count), ("hierarchyCount", t.Hierarchies.Count), ("partitionCount", t.Partitions.Count)))),
            Def("columns", "模型字段", C("tableName", "columnName", "dataType", "isHidden", "isCalculated", "expression", "description"), s => s.Tables.SelectMany(t => t.Columns.Select(c => Row(("tableName", t.Name), ("columnName", c.Name), ("dataType", c.DataType), ("isHidden", c.IsHidden), ("isCalculated", c.IsCalculated), ("expression", c.Expression), ("description", c.Description))))),
            Def("measures", "DAX 度量值", C("tableName", "measureName", "expression", "formatString", "isHidden", "description", "displayFolder"), s => s.Tables.SelectMany(t => t.Measures.Select(m => Row(("tableName", t.Name), ("measureName", m.Name), ("expression", m.Expression), ("formatString", m.FormatString), ("isHidden", m.IsHidden), ("description", m.Description), ("displayFolder", m.DisplayFolder))))),
            Def("hierarchies", "层次结构", C("tableName", "hierarchyName", "levelCount"), s => s.Tables.SelectMany(t => t.Hierarchies.Select(h => Row(("tableName", t.Name), ("hierarchyName", h.Name), ("levelCount", h.Levels.Count))))),
            Def("hierarchy-levels", "层次结构级别", C("tableName", "hierarchyName", "levelName", "ordinal"), s => s.Tables.SelectMany(t => t.Hierarchies.SelectMany(h => h.Levels.Select((level, ordinal) => Row(("tableName", t.Name), ("hierarchyName", h.Name), ("levelName", level), ("ordinal", ordinal)))))),
            Def("partitions", "模型分区", C("tableName", "partitionName", "mode", "sourceType", "expression"), s => s.Tables.SelectMany(t => t.Partitions.Select(p => Row(("tableName", t.Name), ("partitionName", p.Name), ("mode", p.Mode), ("sourceType", p.SourceType), ("expression", p.Expression))))),
            Def("relationships", "表关系", C("relationshipName", "fromTable", "fromColumn", "toTable", "toColumn", "isActive", "crossFilteringBehavior", "fromCardinality", "toCardinality"), s => s.Relationships.Select(r => Row(("relationshipName", r.Name), ("fromTable", r.FromTable), ("fromColumn", r.FromColumn), ("toTable", r.ToTable), ("toColumn", r.ToColumn), ("isActive", r.IsActive), ("crossFilteringBehavior", r.CrossFilteringBehavior), ("fromCardinality", r.FromCardinality), ("toCardinality", r.ToCardinality)))),
            Def("calculation-groups", "计算组", C("calculationGroupName", "precedence", "isHidden", "itemCount"), s => s.CalculationGroups.Select(g => Row(("calculationGroupName", g.Name), ("precedence", g.Precedence), ("isHidden", g.IsHidden), ("itemCount", g.Items.Count)))),
            Def("calculation-items", "计算项", C("calculationGroupName", "calculationItemName", "expression", "formatStringExpression", "ordinal", "description"), s => s.CalculationGroups.SelectMany(g => g.Items.Select(i => Row(("calculationGroupName", g.Name), ("calculationItemName", i.Name), ("expression", i.Expression), ("formatStringExpression", i.FormatStringExpression), ("ordinal", i.Ordinal), ("description", i.Description))))),
            Def("roles", "安全角色", C("roleName", "modelPermission", "rlsRuleCount", "olsPermissionCount"), s => s.Roles.Select(r => Row(("roleName", r.Name), ("modelPermission", r.ModelPermission), ("rlsRuleCount", r.TablePermissions.Count), ("olsPermissionCount", r.ObjectPermissions.Count)))),
            Def("rls", "RLS 表筛选规则", C("roleName", "tableName", "expression"), s => s.Roles.SelectMany(r => r.TablePermissions.Select(p => Row(("roleName", r.Name), ("tableName", p.Table), ("expression", p.Expression))))),
            Def("ols", "OLS 对象权限", C("roleName", "objectName", "permission"), s => s.Roles.SelectMany(r => r.ObjectPermissions.Select(p => Row(("roleName", r.Name), ("objectName", p.Object), ("permission", p.Permission))))),
            Def("dependencies", "模型对象依赖", C("sourceId", "sourceName", "sourceType", "targetId", "targetName", "targetType", "reference"), s => s.Dependencies.Select(d => Row(("sourceId", d.SourceId), ("sourceName", d.SourceName), ("sourceType", d.SourceType), ("targetId", d.TargetId), ("targetName", d.TargetName), ("targetType", d.TargetType), ("reference", d.Reference)))),
            Def("pages", "报表页面", C("pageName", "displayName", "width", "height", "isHidden", "displayOption", "visualCount"), s => s.Pages.Select(p => Row(("pageName", p.Name), ("displayName", p.DisplayName), ("width", p.Width), ("height", p.Height), ("isHidden", p.IsHidden), ("displayOption", p.DisplayOption), ("visualCount", p.Visuals.Count)))),
            Def("visuals", "页面视觉对象", C("pageName", "pageDisplayName", "visualName", "title", "visualType", "x", "y", "width", "height", "z", "tabOrder", "isHidden", "fieldCount", "sourceFile"), s => s.Pages.SelectMany(p => p.Visuals.Select(v => Row(("pageName", p.Name), ("pageDisplayName", p.DisplayName), ("visualName", v.Name), ("title", v.Title), ("visualType", v.Type), ("x", v.X), ("y", v.Y), ("width", v.Width), ("height", v.Height), ("z", v.Z), ("tabOrder", v.TabOrder), ("isHidden", v.IsHidden), ("fieldCount", v.Fields.Count), ("sourceFile", v.SourceFile))))),
            Def("visual-fields", "视觉对象字段绑定", C("pageName", "visualName", "field", "ordinal"), s => s.Pages.SelectMany(p => p.Visuals.SelectMany(v => v.Fields.Select((field, ordinal) => Row(("pageName", p.Name), ("visualName", v.Name), ("field", field), ("ordinal", ordinal)))))),
            Def("bookmarks", "报表书签", C("bookmarkName", "displayName", "activePage", "activePageDisplayName", "isOrphaned", "applyOnlyToTargetVisuals", "suppressData", "hasDataState", "targetVisualCount", "visualStateCount", "reportFilterCount", "visualFilterCount", "sourceFile"), s => s.Bookmarks.Select(b => { var page = s.Pages.FirstOrDefault(p => p.Name.Equals(b.ActivePage, StringComparison.OrdinalIgnoreCase)); return Row(("bookmarkName", b.Name), ("displayName", b.DisplayName), ("activePage", b.ActivePage), ("activePageDisplayName", page?.DisplayName), ("isOrphaned", b.ActivePage is not null && page is null), ("applyOnlyToTargetVisuals", b.ApplyOnlyToTargetVisuals), ("suppressData", b.SuppressData), ("hasDataState", b.HasDataState), ("targetVisualCount", b.TargetVisualNames.Count), ("visualStateCount", b.VisualStates.Count), ("reportFilterCount", b.ReportFilterCount), ("visualFilterCount", b.VisualFilterCount), ("sourceFile", b.SourceFile)); })),
            Def("bookmark-groups", "书签组", C("bookmarkGroupName", "displayName", "order", "childCount"), s => s.BookmarkGroups.Select(g => Row(("bookmarkGroupName", g.Name), ("displayName", g.DisplayName), ("order", g.Order), ("childCount", g.Children.Count)))),
            Def("bookmark-group-items", "书签组成员", C("bookmarkGroupName", "bookmarkName", "ordinal"), s => s.BookmarkGroups.SelectMany(g => g.Children.Select((bookmark, ordinal) => Row(("bookmarkGroupName", g.Name), ("bookmarkName", bookmark), ("ordinal", ordinal))))),
            Def("bookmark-targets", "书签目标视觉对象", C("bookmarkName", "visualName", "ordinal"), s => s.Bookmarks.SelectMany(b => b.TargetVisualNames.Select((visual, ordinal) => Row(("bookmarkName", b.Name), ("visualName", visual), ("ordinal", ordinal))))),
            Def("bookmark-states", "书签视觉对象状态", C("bookmarkName", "pageName", "visualName", "isHidden", "visualType", "filterCount"), s => s.Bookmarks.SelectMany(b => b.VisualStates.Select(v => Row(("bookmarkName", b.Name), ("pageName", v.PageName), ("visualName", v.VisualName), ("isHidden", v.IsHidden), ("visualType", v.VisualType), ("filterCount", v.FilterCount))))),
            Def("issues", "质量检查问题", C("issueId", "severity", "category", "title", "detail", "objectName", "pageName"), s => s.Issues.Select(i => Row(("issueId", i.Id), ("severity", i.Severity), ("category", i.Category), ("title", i.Title), ("detail", i.Detail), ("objectName", i.ObjectName), ("pageName", i.PageName)))),
            Def("warnings", "解析提示", C("warning", "ordinal"), s => s.Warnings.Select((warning, ordinal) => Row(("warning", warning), ("ordinal", ordinal))))
        };
        return definitions.ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> SummaryRows(ProjectSnapshot s)
    {
        yield return Row(("metric", "tableCount"), ("value", s.Tables.Count));
        yield return Row(("metric", "columnCount"), ("value", s.Tables.Sum(t => t.Columns.Count)));
        yield return Row(("metric", "measureCount"), ("value", s.Tables.Sum(t => t.Measures.Count)));
        yield return Row(("metric", "relationshipCount"), ("value", s.Relationships.Count));
        yield return Row(("metric", "calculationGroupCount"), ("value", s.CalculationGroups.Count));
        yield return Row(("metric", "calculationItemCount"), ("value", s.CalculationGroups.Sum(g => g.Items.Count)));
        yield return Row(("metric", "roleCount"), ("value", s.Roles.Count));
        yield return Row(("metric", "dependencyCount"), ("value", s.Dependencies.Count));
        yield return Row(("metric", "pageCount"), ("value", s.Pages.Count));
        yield return Row(("metric", "visualCount"), ("value", s.Pages.Sum(p => p.Visuals.Count)));
        yield return Row(("metric", "bookmarkCount"), ("value", s.Bookmarks.Count));
        yield return Row(("metric", "issueCount"), ("value", s.Issues.Count));
    }

    private static EntityDefinition Def(string name, string description, IReadOnlyList<string> columns, Func<ProjectSnapshot, IEnumerable<IReadOnlyDictionary<string, object?>>> rows)
        => new(name, description, columns, snapshot => rows(snapshot).ToList());
    private static string[] C(params string[] columns) => columns;
    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values) => values.ToDictionary(x => x.Name, x => x.Value);
    private static string Normalize(string entity) => entity.Trim().ToLowerInvariant().Replace('_', '-');
    private sealed record EntityDefinition(string Name, string Description, IReadOnlyList<string> Columns, Func<ProjectSnapshot, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Rows);
}

public sealed record PowerQueryExport(
    string Entity,
    string Description,
    string ProjectName,
    string ProjectFormat,
    DateTimeOffset ScannedAt,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);
