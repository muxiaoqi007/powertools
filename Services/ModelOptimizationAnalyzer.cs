using System.Text.RegularExpressions;

namespace PowerTools.Services;

public static partial class ModelOptimizationAnalyzer
{
    public static ModelOptimizationResult Analyze(ProjectSnapshot snapshot)
    {
        var candidates = AnalyzeRemovalCandidates(snapshot);
        var suggestions = AnalyzeMeasures(snapshot);
        return new ModelOptimizationResult(candidates, suggestions);
    }

    private static List<RemovalCandidate> AnalyzeRemovalCandidates(ProjectSnapshot snapshot)
    {
        var result = new List<RemovalCandidate>();
        var visualFields = snapshot.Pages.SelectMany(p => p.Visuals.SelectMany(v => v.Fields.Select(f => (Page: p.DisplayName, Visual: v.Title, Field: NormalizeReference(f))))).ToList();
        var dependencyTargets = snapshot.Dependencies.GroupBy(d => d.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var table in snapshot.Tables)
        {
            foreach (var measure in table.Measures)
            {
                var id = $"measure:{table.Name}:{measure.Name}";
                var reference = NormalizeReference($"{table.Name}[{measure.Name}]");
                var evidence = new List<string>();
                if (dependencyTargets.TryGetValue(id, out var consumers)) evidence.AddRange(consumers.Take(8).Select(x => $"被 {x.SourceName} 引用"));
                evidence.AddRange(visualFields.Where(x => x.Field == reference).Take(8).Select(x => $"页面“{x.Page}”视觉对象“{x.Visual}”使用"));
                var externalRisk = "无法检测其他 PBIX、Excel、XMLA 客户端或动态字符串引用";
                if (evidence.Count > 0)
                    result.Add(new RemovalCandidate("measure", table.Name, measure.Name, "blocked", "high", 100, new[] { "检测到项目内引用，不能删除" }, evidence));
                else
                    result.Add(new RemovalCandidate("measure", table.Name, measure.Name, "candidate", "medium", measure.IsHidden ? 35 : 45,
                        new[] { "未检测到当前项目内的 DAX 或视觉对象引用", externalRisk }, Array.Empty<string>()));
            }

            foreach (var column in table.Columns)
            {
                var id = $"column:{table.Name}:{column.Name}";
                var reference = NormalizeReference($"{table.Name}[{column.Name}]");
                var evidence = new List<string>();
                if (dependencyTargets.TryGetValue(id, out var consumers)) evidence.AddRange(consumers.Take(8).Select(x => $"被 {x.SourceName} 引用"));
                evidence.AddRange(visualFields.Where(x => x.Field == reference).Take(8).Select(x => $"页面“{x.Page}”视觉对象“{x.Visual}”使用"));
                evidence.AddRange(snapshot.Relationships.Where(r =>
                    r.FromTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && r.FromColumn.Equals(column.Name, StringComparison.OrdinalIgnoreCase) ||
                    r.ToTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && r.ToColumn.Equals(column.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(r => $"关系“{r.Name}”使用"));
                evidence.AddRange(table.Hierarchies.Where(h => h.Levels.Any(level => level.Equals(column.Name, StringComparison.OrdinalIgnoreCase))).Select(h => $"层次结构“{h.Name}”使用"));
                evidence.AddRange(table.Columns.Where(other => other.SortByColumn?.Equals(column.Name, StringComparison.OrdinalIgnoreCase) == true).Select(other => $"字段“{other.Name}”的排序依据"));
                if (column.IsKey) evidence.Add("表的键字段");
                evidence.AddRange(snapshot.Roles.SelectMany(role => role.TablePermissions.Where(p => p.Table.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && ExpressionReferences(p.Expression, table.Name, column.Name)).Select(_ => $"RLS 角色“{role.Name}”使用")));
                evidence.AddRange(snapshot.Roles.SelectMany(role => role.ObjectPermissions.Where(p => NormalizeReference(p.Object) == reference).Select(_ => $"OLS 角色“{role.Name}”保护")));
                var storage = snapshot.StorageMetrics.FirstOrDefault(metric => metric.TableName.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && metric.ColumnName?.Equals(column.Name, StringComparison.OrdinalIgnoreCase) == true);
                if (storage?.TotalSizeBytes > 0) evidence.Add($"VertiPaq 占用 {FormatBytes(storage.TotalSizeBytes.Value)}，基数 {storage.Cardinality?.ToString("N0") ?? "未知"}");
                if (evidence.Count > 0)
                {
                    var hasBlockingEvidence = evidence.Any(item => !item.StartsWith("VertiPaq", StringComparison.Ordinal));
                    if (hasBlockingEvidence)
                        result.Add(new RemovalCandidate("column", table.Name, column.Name, "blocked", "high", 100, new[] { "检测到项目内结构或表达式引用，不能删除" }, evidence.Distinct().Take(12).ToList(), storage?.TotalSizeBytes));
                    else
                        result.Add(new RemovalCandidate("column", table.Name, column.Name, "candidate", snapshot.LiveModel is null ? "low" : "medium", column.IsHidden ? 45 : 60,
                            new[] { "未检测到当前模型内引用", "已读取实时模型，但仍需检查其他报表、Excel/XMLA、动态引用和源系统依赖" }, evidence, storage?.TotalSizeBytes));
                }
                else
                    result.Add(new RemovalCandidate("column", table.Name, column.Name, "candidate", snapshot.LiveModel is null ? "low" : "medium", column.IsHidden ? 55 : 70,
                        new[] { "未检测到当前项目内引用", "仍需检查 SortByColumn、分区源查询、外部报表、Excel 和动态引用" }, Array.Empty<string>(), storage?.TotalSizeBytes));
            }
        }
        return result.OrderBy(x => x.Status).ThenBy(x => x.RiskScore).ThenByDescending(x => x.EstimatedSavingsBytes ?? 0).ThenBy(x => x.TableName).ThenBy(x => x.ObjectName).ToList();
    }

    private static List<MeasureOptimizationSuggestion> AnalyzeMeasures(ProjectSnapshot snapshot)
    {
        var suggestions = new List<MeasureOptimizationSuggestion>();
        foreach (var table in snapshot.Tables)
        foreach (var measure in table.Measures)
        {
            var expression = StripCommentsAndStrings(measure.Expression);
            AddIf(string.IsNullOrWhiteSpace(measure.Description), "META-001", "Maintainability", "info", 20, "缺少业务说明", "度量值没有 description，难以确认业务口径和删除风险。", "补充业务定义、筛选上下文和单位。", "Microsoft Power BI guidance", "https://learn.microsoft.com/power-bi/guidance/");
            AddIf(string.IsNullOrWhiteSpace(measure.FormatString), "META-002", "Maintainability", "info", 15, "缺少格式字符串", "未设置默认格式，可能导致不同视觉对象显示不一致。", "根据指标类型设置数字、货币、百分比或动态格式字符串。", "Microsoft Power BI optimization guide", "https://learn.microsoft.com/power-bi/guidance/power-bi-optimization");
            AddIf(expression.Length > 500 && !VarRegex().IsMatch(expression), "DAX-001", "Performance", "warning", 70, "复杂表达式未使用变量", $"表达式长度为 {expression.Length}，但没有 VAR。重复逻辑可能被多次计算。", "提取重复或昂贵子表达式为 VAR，并在 RETURN 中复用。", "Microsoft: Use variables", "https://learn.microsoft.com/dax/best-practices/dax-variables");
            AddIf(UnsafeDivisionRegex().IsMatch(expression), "DAX-002", "Performance", "warning", 65, "可能使用不安全除法", "检测到除法运算符，分母若为表达式可能产生零值检查和错误处理成本。", "分母不是常量时评估改用 DIVIDE；常量分母保留 /。", "Microsoft: DIVIDE vs operator", "https://learn.microsoft.com/dax/best-practices/dax-divide-function-operator");
            AddIf(CalculateFilterRegex().IsMatch(expression), "DAX-003", "Performance", "warning", 75, "CALCULATE 中使用 FILTER", "FILTER 作为 CALCULATE 参数可能迭代整张表并增加公式引擎工作量。", "能表达为布尔筛选时直接使用列条件；需要保留现有筛选时评估 KEEPFILTERS。", "Microsoft Power BI guidance", "https://learn.microsoft.com/power-bi/guidance/");
            AddIf(CountColumnRegex().IsMatch(expression), "DAX-004", "Performance", "info", 40, "评估 COUNTROWS 替代 COUNT", "若目标是统计表行数，COUNTROWS 语义更清晰，通常也更高效。", "确认业务语义后使用 COUNTROWS；若必须忽略空值则保留 COUNT。", "Microsoft Power BI guidance", "https://learn.microsoft.com/power-bi/guidance/");
            AddIf(HasOneValueValuesRegex().IsMatch(expression), "DAX-005", "Maintainability", "info", 45, "可评估 SELECTEDVALUE", "HASONEVALUE 与 VALUES 的组合通常可由 SELECTEDVALUE 更简洁地表达。", "确认备用结果后改用 SELECTEDVALUE(column, alternateResult)。", "Microsoft Power BI guidance", "https://learn.microsoft.com/power-bi/guidance/");
            AddIf(NestedIteratorRegex().IsMatch(expression), "DAX-006", "Performance", "error", 90, "检测到嵌套迭代器", "嵌套 X 迭代器可能创建大型中间结果并触发昂贵的公式引擎计算。", "检查能否预聚合、合并迭代表达式或将计算下推到存储引擎；用 Server Timings 验证。", "SQLBI: Optimizing DAX", "https://www.sqlbi.com/articles/optimizing-nested-iterators-in-dax/");
            var duplicateReferences = MeasureReferenceRegex().Matches(expression).Select(m => m.Value).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3).ToList();
            AddIf(duplicateReferences.Count > 0 && !VarRegex().IsMatch(expression), "DAX-007", "Performance", "warning", 68, "重复计算度量值引用", $"同一度量值被重复引用至少 3 次：{string.Join(", ", duplicateReferences.Take(3).Select(g => g.Key))}。", "使用 VAR 缓存重复度量值结果，避免在不同分支重复计算。", "Microsoft: Use variables", "https://learn.microsoft.com/dax/best-practices/dax-variables");
            AddIf(SwitchSelectedValueRegex().IsMatch(expression) && VarBeforeSwitchRegex().IsMatch(expression), "DAX-008", "Performance", "info", 55, "检查 SWITCH 分支优化", "SWITCH 的选择表达式或分支度量若提前存入变量，可能迫使多个分支被求值。", "让 SWITCH 直接基于筛选列选择，并将分支计算保留在分支内；用查询计划验证。", "SQLBI: SWITCH optimization", "https://docs.sqlbi.com/dax-internals/optimization-notes/switch-optimization");
            var fullyQualifiedMeasures = snapshot.Dependencies.Where(d => d.SourceId.Equals($"measure:{table.Name}:{measure.Name}", StringComparison.OrdinalIgnoreCase) && d.TargetType == "measure" && d.Reference.Contains('[') && !d.Reference.TrimStart().StartsWith("[")).ToList();
            AddIf(fullyQualifiedMeasures.Count > 0, "DAX-009", "Robustness", "info", 35, "度量值引用带表名", "度量值是模型级对象，限定 Home Table 后在移动度量值时可能导致表达式失效。", "度量值使用 [Measure]；字段保持 Table[Column] 完整限定。", "Microsoft: Column and measure references", "https://learn.microsoft.com/dax/best-practices/dax-column-measure-references");

            void AddIf(bool condition, string id, string category, string severity, int priority, string title, string detail, string recommendation, string source, string url)
            {
                if (condition) suggestions.Add(new MeasureOptimizationSuggestion(id, category, severity, priority, table.Name, measure.Name, title, detail, recommendation, source, url));
            }
        }
        return suggestions.OrderByDescending(x => x.Priority).ThenBy(x => x.TableName).ThenBy(x => x.MeasureName).ToList();
    }

    private static string NormalizeReference(string value) => Regex.Replace(value.Replace("'", ""), @"\s+", "").ToLowerInvariant();
    private static string FormatBytes(long value) => value switch
    {
        >= 1_073_741_824 => $"{value / 1_073_741_824d:0.##} GB",
        >= 1_048_576 => $"{value / 1_048_576d:0.##} MB",
        >= 1024 => $"{value / 1024d:0.##} KB",
        _ => $"{value} B"
    };
    private static bool ExpressionReferences(string expression, string table, string column) => NormalizeReference(expression).Contains(NormalizeReference($"{table}[{column}]")) || Regex.IsMatch(expression, $@"\[{Regex.Escape(column)}\]", RegexOptions.IgnoreCase);
    private static string StripCommentsAndStrings(string expression)
    {
        var noBlockComments = Regex.Replace(expression ?? "", @"(?s)/\*.*?\*/", "");
        var noLineComments = Regex.Replace(noBlockComments, @"--[^\r\n]*", "");
        return Regex.Replace(noLineComments, "\"(?:\"\"|[^\"])*\"", "\"\"");
    }

    [GeneratedRegex(@"\bVAR\b", RegexOptions.IgnoreCase)] private static partial Regex VarRegex();
    [GeneratedRegex(@"(?<!/)/(?!/)")] private static partial Regex UnsafeDivisionRegex();
    [GeneratedRegex(@"\bCALCULATE(?:TABLE)?\s*\([\s\S]{0,500}?\bFILTER\s*\(", RegexOptions.IgnoreCase)] private static partial Regex CalculateFilterRegex();
    [GeneratedRegex(@"\bCOUNT\s*\(\s*(?:'[^']+'|[\w.]+)?\s*\[[^\]]+\]\s*\)", RegexOptions.IgnoreCase)] private static partial Regex CountColumnRegex();
    [GeneratedRegex(@"\bHASONEVALUE\s*\([\s\S]{0,200}?\bVALUES\s*\(", RegexOptions.IgnoreCase)] private static partial Regex HasOneValueValuesRegex();
    [GeneratedRegex(@"\b(?:SUMX|AVERAGEX|MINX|MAXX|RANKX|FILTER)\s*\([\s\S]{0,500}?\b(?:SUMX|AVERAGEX|MINX|MAXX|RANKX|FILTER)\s*\(", RegexOptions.IgnoreCase)] private static partial Regex NestedIteratorRegex();
    [GeneratedRegex(@"(?<![\w'])\[[^\]]+\]", RegexOptions.IgnoreCase)] private static partial Regex MeasureReferenceRegex();
    [GeneratedRegex(@"\bSWITCH\s*\([\s\r\n]*SELECTEDVALUE\s*\(", RegexOptions.IgnoreCase)] private static partial Regex SwitchSelectedValueRegex();
    [GeneratedRegex(@"\bVAR\b[\s\S]{0,800}?\bSWITCH\s*\(", RegexOptions.IgnoreCase)] private static partial Regex VarBeforeSwitchRegex();
}

public sealed record ModelOptimizationResult(IReadOnlyList<RemovalCandidate> RemovalCandidates, IReadOnlyList<MeasureOptimizationSuggestion> Suggestions);
