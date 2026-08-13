using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerTools.Services;

public sealed class PowerBiProjectParser
{
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public ProjectSnapshot Parse(string inputPath)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(inputPath.Trim('"')));
        if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"目录不存在：{path}");

        var warnings = new List<string>();
        var reportRoot = FindComponentRoot(path, ".Report") ?? path;
        var modelRoot = FindComponentRoot(path, ".SemanticModel") ?? path;
        var tables = ParseModel(modelRoot, warnings);
        var relationships = ParseRelationships(modelRoot, warnings);
        var calculationGroups = ParseCalculationGroups(modelRoot, warnings);
        var roles = ParseRoles(modelRoot, warnings);
        var dependencies = BuildDependencies(tables, calculationGroups);
        var (bookmarks, bookmarkGroups) = ParseBookmarks(reportRoot, warnings);
        var pages = ParsePages(reportRoot, warnings);

        if (tables.Count == 0 && pages.Count == 0)
            throw new InvalidDataException("未找到可解析的 PBIP/PBIR/TMDL 或 model.bim 内容。请选择项目根目录、*.Report 或 *.SemanticModel 目录。");

        var name = ResolveProjectName(path, reportRoot, modelRoot);
        var format = DetectFormat(reportRoot, modelRoot);
        var issues = AnalyzeQuality(tables, relationships, calculationGroups, roles, pages);
        return new ProjectSnapshot(name, path, format, DateTimeOffset.Now, tables, relationships, calculationGroups, roles, dependencies, bookmarks, bookmarkGroups, pages, issues, warnings);
    }

    private static string? FindComponentRoot(string root, string suffix)
    {
        if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return root;
        try
        {
            return Directory.EnumerateDirectories(root, $"*{suffix}", SearchOption.TopDirectoryOnly).FirstOrDefault()
                ?? Directory.EnumerateDirectories(root, $"*{suffix}", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    private static string ResolveProjectName(string root, string reportRoot, string modelRoot)
    {
        var candidate = new[] { reportRoot, modelRoot, root }
            .Select(Path.GetFileName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && (name.EndsWith(".Report") || name.EndsWith(".SemanticModel")))
            ?? Path.GetFileName(root);
        return Regex.Replace(candidate, @"\.(Report|SemanticModel)$", "", RegexOptions.IgnoreCase);
    }

    private static string DetectFormat(string reportRoot, string modelRoot)
    {
        var formats = new List<string>();
        if (Directory.Exists(Path.Combine(reportRoot, "definition", "pages"))) formats.Add("PBIR");
        if (Directory.Exists(Path.Combine(modelRoot, "definition")) && Directory.EnumerateFiles(Path.Combine(modelRoot, "definition"), "*.tmdl", SearchOption.AllDirectories).Any()) formats.Add("TMDL");
        if (Directory.EnumerateFiles(modelRoot, "model.bim", SearchOption.AllDirectories).Any()) formats.Add("Model BIM");
        return formats.Count > 0 ? string.Join(" + ", formats.Distinct()) : "Power BI Project";
    }

    private static List<ModelTable> ParseModel(string root, List<string> warnings)
    {
        var definition = Path.Combine(root, "definition");
        if (Directory.Exists(definition))
        {
            var tableDir = Path.Combine(definition, "tables");
            var files = Directory.Exists(tableDir)
                ? Directory.EnumerateFiles(tableDir, "*.tmdl", SearchOption.AllDirectories).ToList()
                : Directory.EnumerateFiles(definition, "*.tmdl", SearchOption.AllDirectories).Where(file => !Path.GetFileName(file).Equals("relationships.tmdl", StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Count > 0)
            {
                var result = files.Select(ParseTmdlTable).Where(table => table is not null).Cast<ModelTable>().ToList();
                if (result.Count > 0) return result;
            }
        }

        var bim = Directory.EnumerateFiles(root, "model.bim", SearchOption.AllDirectories).FirstOrDefault();
        if (bim is not null)
        {
            try { return ParseBimModel(bim); }
            catch (Exception ex) { warnings.Add($"model.bim 解析失败：{ex.Message}"); }
        }
        return new List<ModelTable>();
    }

    private static ModelTable? ParseTmdlTable(string file)
    {
        var lines = File.ReadAllLines(file);
        var header = lines.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("table ", StringComparison.OrdinalIgnoreCase));
        if (header is null) return null;
        var name = Unquote(header[6..].Trim());
        var columns = new List<ModelColumn>();
        var measures = new List<ModelMeasure>();
        var hierarchies = new List<ModelHierarchy>();
        var partitions = new List<ModelPartition>();
        string? description = null;
        var tableHidden = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("///") && description is null) description = trimmed[3..].Trim();
            if (trimmed.Equals("isHidden", StringComparison.OrdinalIgnoreCase) && LeadingWhitespace(raw) <= 1) tableHidden = true;

            if (!TryParseTmdlDeclaration(trimmed, out var kind, out var objectName, out var inlineExpression)) continue;
            if (kind is not ("column" or "measure" or "hierarchy" or "partition")) continue;
            var objectDescription = ReadPrecedingDocComment(lines, i);
            var baseIndent = LeadingWhitespace(raw);
            var block = new List<string>();
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                var next = lines[j];
                if (!string.IsNullOrWhiteSpace(next) && LeadingWhitespace(next) <= baseIndent) break;
                block.Add(next);
            }
            i = j - 1;

            var props = ParseTmdlProperties(block);
            var isHidden = props.ContainsKey("isHidden") && !props["isHidden"].Equals("false", StringComparison.OrdinalIgnoreCase);
            if (kind == "column")
            {
                var expression = ReadObjectExpression(inlineExpression, block, "expression");
                columns.Add(new ModelColumn(objectName, props.GetValueOrDefault("dataType") ?? "unknown", isHidden, expression is not null || props.ContainsKey("type") && props["type"].Contains("calculated", StringComparison.OrdinalIgnoreCase), expression, objectDescription ?? props.GetValueOrDefault("description")));
            }
            else if (kind == "measure")
            {
                var expression = ReadObjectExpression(inlineExpression, block);
                measures.Add(new ModelMeasure(objectName, expression ?? string.Empty, props.GetValueOrDefault("formatString"), isHidden, objectDescription ?? props.GetValueOrDefault("description"), props.GetValueOrDefault("displayFolder")));
            }
            else if (kind == "hierarchy")
            {
                var levels = block.Select(line => Regex.Match(line.Trim(), @"^level\s+(.+?)(?:\s*=|$)", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => Unquote(m.Groups[1].Value)).ToList();
                hierarchies.Add(new ModelHierarchy(objectName, levels));
            }
            else if (kind == "partition")
            {
                partitions.Add(new ModelPartition(objectName, props.GetValueOrDefault("mode"), ParsePartitionSourceType(inlineExpression), ReadObjectExpression(null, block, "source")));
            }
        }
        return new ModelTable(name, description, tableHidden, columns, measures, hierarchies, partitions);
    }

    private static Dictionary<string, string> ParseTmdlProperties(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? doc = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("///")) { doc = string.Join(" ", new[] { doc, trimmed[3..].Trim() }.Where(x => !string.IsNullOrWhiteSpace(x))); continue; }
            var index = trimmed.IndexOf(':');
            if (index > 0) result[trimmed[..index].Trim()] = Unquote(trimmed[(index + 1)..].Trim());
            else if (trimmed.Equals("isHidden", StringComparison.OrdinalIgnoreCase)) result["isHidden"] = "true";
        }
        if (doc is not null) result["description"] = doc;
        return result;
    }

    private static bool TryParseTmdlDeclaration(string text, out string kind, out string name, out string? expression)
    {
        kind = string.Empty; name = string.Empty; expression = null;
        var firstSpace = text.IndexOfAny(new[] { ' ', '\t' });
        if (firstSpace <= 0) return false;
        kind = text[..firstSpace].Trim().ToLowerInvariant();
        if (kind is not ("column" or "measure" or "hierarchy" or "partition" or "calculationitem" or "tablepermission" or "objectpermission" or "columnpermission")) return false;
        var remainder = text[(firstSpace + 1)..].Trim();
        var equals = FindUnquotedEquals(remainder);
        name = Unquote((equals >= 0 ? remainder[..equals] : remainder).Trim());
        expression = equals >= 0 ? remainder[(equals + 1)..].Trim() : null;
        return name.Length > 0;
    }

    private static int FindUnquotedEquals(string text)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'' && (i + 1 >= text.Length || text[i + 1] != '\'')) quoted = !quoted;
            else if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'') i++;
            else if (text[i] == '=' && !quoted) return i;
        }
        return -1;
    }

    private static string? ReadObjectExpression(string? inline, IReadOnlyList<string> block, string? propertyName = null)
    {
        if (!string.IsNullOrWhiteSpace(inline))
        {
            if (inline == "```") return CleanFencedExpression(ReadUntilFence(block));
            return CleanFencedExpression(inline);
        }

        if (propertyName is not null)
        {
            for (var i = 0; i < block.Count; i++)
            {
                var trimmed = block[i].Trim();
                if (!trimmed.StartsWith(propertyName, StringComparison.OrdinalIgnoreCase)) continue;
                var suffix = trimmed[propertyName.Length..].TrimStart();
                if (suffix.StartsWith(':') || suffix.StartsWith('=')) suffix = suffix[1..].Trim();
                if (!string.IsNullOrWhiteSpace(suffix) && suffix != "```") return CleanFencedExpression(suffix);
                var following = block.Skip(i + 1).TakeWhile(line => string.IsNullOrWhiteSpace(line) || LeadingWhitespace(line) > LeadingWhitespace(block[i])).ToList();
                return CleanFencedExpression(string.Join(Environment.NewLine, following.Select(TrimOneIndent)));
            }
            return null;
        }

        var propertyStart = Enumerable.Range(0, block.Count).FirstOrDefault(index => IsObjectPropertyLine(block[index].Trim()), -1);
        var expressionLines = propertyStart < 0 ? block : block.Take(propertyStart);
        return CleanFencedExpression(string.Join(Environment.NewLine, expressionLines.Select(TrimOneIndent)));
    }

    private static string ReadUntilFence(IReadOnlyList<string> block)
    {
        var result = new List<string>();
        foreach (var line in block)
        {
            if (line.Trim() == "```") break;
            result.Add(TrimOneIndent(line));
        }
        return string.Join(Environment.NewLine, result);
    }

    private static bool IsObjectPropertyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text, @"^(formatString|formatStringDefinition|isHidden|displayFolder|lineageTag|description|dataCategory|detailRowsDefinition|changedProperty|annotation|dataType|summarizeBy|sourceColumn|sortByColumn|isKey|mode|source)\b", RegexOptions.IgnoreCase);
    }

    private static string? CleanFencedExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        var value = expression.Trim();
        if (value.StartsWith("```")) value = value[3..];
        if (value.EndsWith("```")) value = value[..^3];
        return value.Trim();
    }

    private static string TrimOneIndent(string value)
    {
        var index = 0;
        while (index < value.Length && index < 2 && (value[index] == '\t' || value[index] == ' ')) index++;
        return value[index..];
    }

    private static string? ParsePartitionSourceType(string? inline)
    {
        if (string.IsNullOrWhiteSpace(inline)) return null;
        var value = inline.Trim();
        return value.Equals("m", StringComparison.OrdinalIgnoreCase) ? "m" : value.Equals("calculated", StringComparison.OrdinalIgnoreCase) ? "calculated" : value;
    }

    private static List<ModelTable> ParseBimModel(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
        var model = document.RootElement.TryGetProperty("model", out var m) ? m : document.RootElement;
        if (!model.TryGetProperty("tables", out var tablesElement)) return new();
        var result = new List<ModelTable>();
        foreach (var table in tablesElement.EnumerateArray())
        {
            var columns = ReadArray(table, "columns").Select(column => new ModelColumn(
                GetString(column, "name") ?? "Column", GetString(column, "dataType") ?? "unknown", GetBool(column, "isHidden"),
                column.TryGetProperty("expression", out _), GetExpression(column), GetString(column, "description"))).ToList();
            var measures = ReadArray(table, "measures").Select(measure => new ModelMeasure(
                GetString(measure, "name") ?? "Measure", GetExpression(measure) ?? string.Empty, GetString(measure, "formatString"), GetBool(measure, "isHidden"), GetString(measure, "description"), GetString(measure, "displayFolder"))).ToList();
            var hierarchies = ReadArray(table, "hierarchies").Select(h => new ModelHierarchy(GetString(h, "name") ?? "Hierarchy", ReadArray(h, "levels").Select(l => GetString(l, "name") ?? "Level").ToList())).ToList();
            var partitions = ReadArray(table, "partitions").Select(p => new ModelPartition(GetString(p, "name") ?? "Partition", GetString(p, "mode"), p.TryGetProperty("source", out var s) ? GetString(s, "type") : null, p.TryGetProperty("source", out s) ? GetExpression(s) : null)).ToList();
            result.Add(new ModelTable(GetString(table, "name") ?? "Table", GetString(table, "description"), GetBool(table, "isHidden"), columns, measures, hierarchies, partitions));
        }
        return result;
    }

    private static List<ModelRelationship> ParseRelationships(string root, List<string> warnings)
    {
        var result = new List<ModelRelationship>();
        var relationFile = Directory.Exists(Path.Combine(root, "definition"))
            ? Directory.EnumerateFiles(Path.Combine(root, "definition"), "relationships.tmdl", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (relationFile is not null)
        {
            var lines = File.ReadAllLines(relationFile);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = Regex.Match(lines[i].Trim(), @"^relationship\s+(.+)$", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                var indent = LeadingWhitespace(lines[i]);
                var block = new List<string>();
                for (i++; i < lines.Length && (string.IsNullOrWhiteSpace(lines[i]) || LeadingWhitespace(lines[i]) > indent); i++) block.Add(lines[i]);
                i--;
                var props = ParseTmdlProperties(block);
                var from = ParseReference(props.GetValueOrDefault("fromColumn"));
                var to = ParseReference(props.GetValueOrDefault("toColumn"));
                result.Add(new ModelRelationship(Unquote(match.Groups[1].Value), from.table, from.column, to.table, to.column,
                    !props.TryGetValue("isActive", out var active) || !active.Equals("false", StringComparison.OrdinalIgnoreCase),
                    props.GetValueOrDefault("crossFilteringBehavior") ?? "oneDirection", props.GetValueOrDefault("fromCardinality") ?? "many", props.GetValueOrDefault("toCardinality") ?? "one"));
            }
            return result;
        }

        var bim = Directory.EnumerateFiles(root, "model.bim", SearchOption.AllDirectories).FirstOrDefault();
        if (bim is null) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(bim), JsonOptions);
            var model = document.RootElement.TryGetProperty("model", out var m) ? m : document.RootElement;
            foreach (var rel in ReadArray(model, "relationships"))
                result.Add(new ModelRelationship(GetString(rel, "name") ?? Guid.NewGuid().ToString("N"), GetString(rel, "fromTable") ?? "", GetString(rel, "fromColumn") ?? "", GetString(rel, "toTable") ?? "", GetString(rel, "toColumn") ?? "", !rel.TryGetProperty("isActive", out _) || GetBool(rel, "isActive"), GetString(rel, "crossFilteringBehavior") ?? "oneDirection", GetString(rel, "fromCardinality") ?? "many", GetString(rel, "toCardinality") ?? "one"));
        }
        catch (Exception ex) { warnings.Add($"关系解析失败：{ex.Message}"); }
        return result;
    }

    private static List<CalculationGroup> ParseCalculationGroups(string root, List<string> warnings)
    {
        var result = new List<CalculationGroup>();
        var tableRoot = Path.Combine(root, "definition", "tables");
        if (!Directory.Exists(tableRoot)) return result;
        foreach (var file in Directory.EnumerateFiles(tableRoot, "*.tmdl", SearchOption.AllDirectories))
        {
            try
            {
                var lines = File.ReadAllLines(file);
                var tableLine = lines.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("table ", StringComparison.OrdinalIgnoreCase));
                var groupIndex = Array.FindIndex(lines, line => line.Trim().Equals("calculationGroup", StringComparison.OrdinalIgnoreCase));
                if (tableLine is null || groupIndex < 0) continue;
                var tableName = Unquote(tableLine[6..].Trim());
                var groupIndent = LeadingWhitespace(lines[groupIndex]);
                var groupEnd = FindBlockEnd(lines, groupIndex, groupIndent);
                var groupLines = lines.Skip(groupIndex + 1).Take(groupEnd - groupIndex - 1).ToList();
                var groupProps = ParseTmdlProperties(groupLines);
                var precedence = int.TryParse(groupProps.GetValueOrDefault("precedence"), out var parsedPrecedence) ? parsedPrecedence : 0;
                var items = new List<CalculationItem>();
                for (var i = groupIndex + 1; i < groupEnd; i++)
                {
                    if (!TryParseTmdlDeclaration(lines[i].Trim(), out var kind, out var itemName, out var inline) || kind != "calculationitem") continue;
                    var itemIndent = LeadingWhitespace(lines[i]);
                    var itemEnd = FindBlockEnd(lines, i, itemIndent, groupEnd);
                    var block = lines.Skip(i + 1).Take(itemEnd - i - 1).ToList();
                    var props = ParseTmdlProperties(block);
                    var formatExpression = ReadFormatStringExpression(block);
                    int? ordinal = int.TryParse(props.GetValueOrDefault("ordinal"), out var ordinalValue) ? ordinalValue : null;
                    items.Add(new CalculationItem(itemName, ReadObjectExpression(inline, block) ?? string.Empty, formatExpression, ordinal, ReadPrecedingDocComment(lines, i) ?? props.GetValueOrDefault("description")));
                    i = itemEnd - 1;
                }
                var hidden = lines.Take(groupIndex).Any(line => line.Trim().Equals("isHidden", StringComparison.OrdinalIgnoreCase));
                result.Add(new CalculationGroup(tableName, precedence, hidden, items));
            }
            catch (Exception ex) { warnings.Add($"计算组 {Path.GetFileName(file)} 解析失败：{ex.Message}"); }
        }
        if (result.Count == 0) result.AddRange(ParseBimCalculationGroups(root, warnings));
        return result;
    }

    private static List<SecurityRole> ParseRoles(string root, List<string> warnings)
    {
        var result = new List<SecurityRole>();
        var rolesRoot = Path.Combine(root, "definition", "roles");
        if (!Directory.Exists(rolesRoot)) return ParseBimRoles(root, warnings);
        foreach (var file in Directory.EnumerateFiles(rolesRoot, "*.tmdl", SearchOption.AllDirectories))
        {
            try
            {
                var lines = File.ReadAllLines(file);
                var roleLine = lines.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("role ", StringComparison.OrdinalIgnoreCase));
                if (roleLine is null) continue;
                var roleName = Unquote(roleLine[5..].Trim());
                var permission = lines.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("modelPermission:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim() ?? "read";
                var tablePermissions = new List<TablePermission>();
                var objectPermissions = new List<ObjectPermission>();
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!TryParseTmdlDeclaration(lines[i].Trim(), out var kind, out var objectName, out var inline) || kind is not ("tablepermission" or "objectpermission" or "columnpermission")) continue;
                    var end = FindBlockEnd(lines, i, LeadingWhitespace(lines[i]));
                    var block = lines.Skip(i + 1).Take(end - i - 1).ToList();
                    var expression = ReadObjectExpression(inline, block) ?? string.Empty;
                    if (kind == "tablepermission")
                    {
                        tablePermissions.Add(new TablePermission(objectName, expression));
                        foreach (var nested in ParseNestedColumnPermissions(objectName, block)) objectPermissions.Add(nested);
                    }
                    else objectPermissions.Add(new ObjectPermission(objectName, expression));
                    i = end - 1;
                }
                result.Add(new SecurityRole(roleName, permission, tablePermissions, objectPermissions));
            }
            catch (Exception ex) { warnings.Add($"角色 {Path.GetFileName(file)} 解析失败：{ex.Message}"); }
        }
        return result;
    }

    private static IEnumerable<CalculationGroup> ParseBimCalculationGroups(string root, List<string> warnings)
    {
        var result = new List<CalculationGroup>();
        var bim = Directory.EnumerateFiles(root, "model.bim", SearchOption.AllDirectories).FirstOrDefault();
        if (bim is null) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(bim), JsonOptions);
            var model = document.RootElement.TryGetProperty("model", out var m) ? m : document.RootElement;
            foreach (var table in ReadArray(model, "tables"))
            {
                if (!table.TryGetProperty("calculationGroup", out var group)) continue;
                var items = ReadArray(group, "calculationItems").Select(item => new CalculationItem(
                    GetString(item, "name") ?? "Calculation Item",
                    GetExpression(item) ?? string.Empty,
                    item.TryGetProperty("formatStringDefinition", out var format) ? GetExpression(format) : null,
                    item.TryGetProperty("ordinal", out var ordinal) && ordinal.TryGetInt32(out var ordinalValue) ? ordinalValue : null,
                    GetString(item, "description"))).ToList();
                result.Add(new CalculationGroup(GetString(table, "name") ?? "Calculation Group", (int)GetDouble(group, "precedence"), GetBool(table, "isHidden"), items));
            }
        }
        catch (Exception ex) { warnings.Add($"model.bim 计算组解析失败：{ex.Message}"); }
        return result;
    }

    private static List<SecurityRole> ParseBimRoles(string root, List<string> warnings)
    {
        var result = new List<SecurityRole>();
        var bim = Directory.EnumerateFiles(root, "model.bim", SearchOption.AllDirectories).FirstOrDefault();
        if (bim is null) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(bim), JsonOptions);
            var model = document.RootElement.TryGetProperty("model", out var m) ? m : document.RootElement;
            foreach (var role in ReadArray(model, "roles"))
            {
                var tablePermissions = ReadArray(role, "tablePermissions").Select(permission => new TablePermission(
                    GetString(permission, "name") ?? GetString(permission, "table") ?? "Table",
                    GetString(permission, "filterExpression") ?? string.Empty)).ToList();
                var objectPermissions = new List<ObjectPermission>();
                foreach (var permission in ReadArray(role, "tablePermissions"))
                foreach (var column in ReadArray(permission, "columnPermissions"))
                    objectPermissions.Add(new ObjectPermission($"{GetString(permission, "name")}[{GetString(column, "name")}]", GetString(column, "metadataPermission") ?? "none"));
                result.Add(new SecurityRole(GetString(role, "name") ?? "Role", GetString(role, "modelPermission") ?? "read", tablePermissions, objectPermissions));
            }
        }
        catch (Exception ex) { warnings.Add($"model.bim 角色解析失败：{ex.Message}"); }
        return result;
    }

    private static int FindBlockEnd(IReadOnlyList<string> lines, int start, int indent, int? upperBound = null)
    {
        var limit = upperBound ?? lines.Count;
        var i = start + 1;
        for (; i < limit; i++)
            if (!string.IsNullOrWhiteSpace(lines[i]) && LeadingWhitespace(lines[i]) <= indent) break;
        return i;
    }

    private static string? ReadNamedExpression(IReadOnlyList<string> block, string propertyName)
    {
        for (var i = 0; i < block.Count; i++)
        {
            var trimmed = block[i].Trim();
            if (!trimmed.StartsWith(propertyName, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = trimmed[propertyName.Length..].TrimStart();
            if (suffix.StartsWith(':') || suffix.StartsWith('=')) suffix = suffix[1..].Trim();
            var end = FindBlockEnd(block, i, LeadingWhitespace(block[i]));
            return ReadObjectExpression(suffix, block.Skip(i + 1).Take(end - i - 1).ToList());
        }
        return null;
    }

    private static string? ReadFormatStringExpression(IReadOnlyList<string> block)
    {
        var direct = ReadNamedExpression(block, "formatStringExpression");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        for (var i = 0; i < block.Count; i++)
        {
            var trimmed = block[i].Trim();
            if (!trimmed.StartsWith("formatStringDefinition", StringComparison.OrdinalIgnoreCase)) continue;
            var equals = FindUnquotedEquals(trimmed);
            var inline = equals >= 0 ? trimmed[(equals + 1)..].Trim() : null;
            var end = FindBlockEnd(block, i, LeadingWhitespace(block[i]));
            var nested = block.Skip(i + 1).Take(end - i - 1).ToList();
            return ReadObjectExpression(inline, nested) ?? ReadNamedExpression(nested, "expression");
        }
        return null;
    }

    private static IEnumerable<ObjectPermission> ParseNestedColumnPermissions(string table, IReadOnlyList<string> block)
    {
        for (var i = 0; i < block.Count; i++)
        {
            if (!TryParseTmdlDeclaration(block[i].Trim(), out var kind, out var column, out var permission) || kind != "columnpermission") continue;
            var end = FindBlockEnd(block, i, LeadingWhitespace(block[i]));
            var nested = block.Skip(i + 1).Take(end - i - 1).ToList();
            yield return new ObjectPermission($"{table}[{column}]", ReadObjectExpression(permission, nested) ?? "none");
            i = end - 1;
        }
    }

    private static (List<ReportBookmark> Bookmarks, List<BookmarkGroup> Groups) ParseBookmarks(string root, List<string> warnings)
    {
        var bookmarksRoot = Path.Combine(root, "definition", "bookmarks");
        if (!Directory.Exists(bookmarksRoot)) return (new(), new());

        var groups = new List<BookmarkGroup>();
        var indexFile = Path.Combine(bookmarksRoot, "bookmarks.json");
        if (File.Exists(indexFile))
        {
            try
            {
                using var index = JsonDocument.Parse(File.ReadAllText(indexFile), JsonOptions);
                var order = 0;
                foreach (var item in ReadArray(index.RootElement, "items"))
                {
                    if (!item.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) { order++; continue; }
                    var name = GetString(item, "name") ?? $"bookmark-group-{order}";
                    groups.Add(new BookmarkGroup(name, GetString(item, "displayName") ?? name,
                        children.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList(), order));
                    order++;
                }
            }
            catch (Exception ex) { warnings.Add($"书签分组索引解析失败：{ex.Message}"); }
        }

        var bookmarks = new List<ReportBookmark>();
        var fallbackCount = 0;
        foreach (var file in Directory.EnumerateFiles(bookmarksRoot, "*.bookmark.json", SearchOption.TopDirectoryOnly))
        {
            var raw = File.ReadAllText(file);
            try
            {
                using var document = JsonDocument.Parse(raw, JsonOptions);
                bookmarks.Add(ParseBookmark(document.RootElement, file));
            }
            catch
            {
                fallbackCount++;
                bookmarks.Add(ParseBookmarkFallback(raw, file));
            }
        }
        if (fallbackCount > 0) warnings.Add($"{fallbackCount} 个书签包含非标准 JSON，已使用容错模式提取基础信息。");

        var groupOrder = groups.SelectMany((group, groupIndex) => group.Children.Select((name, childIndex) => (name, order: groupIndex * 10000 + childIndex)))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Min(v => v.order), StringComparer.OrdinalIgnoreCase);
        return (bookmarks.OrderBy(x => groupOrder.TryGetValue(x.Name, out var order) ? order : int.MaxValue).ThenBy(x => x.DisplayName).ToList(), groups);
    }

    private static ReportBookmark ParseBookmark(JsonElement root, string file)
    {
        var name = GetString(root, "name") ?? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
        var options = root.TryGetProperty("options", out var optionValue) ? optionValue : default;
        var targets = ReadArray(options, "targetVisualNames").Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
        var suppressData = GetBool(options, "suppressData");
        var exploration = root.TryGetProperty("explorationState", out var explorationValue) ? explorationValue : default;
        var states = new Dictionary<string, BookmarkVisualState>(StringComparer.OrdinalIgnoreCase);
        var reportFilters = exploration.ValueKind == JsonValueKind.Object && exploration.TryGetProperty("filters", out var filters) ? CountFilters(filters) : 0;
        var visualFilters = 0;

        if (exploration.ValueKind == JsonValueKind.Object && exploration.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Object)
        {
            foreach (var section in sections.EnumerateObject())
            {
                if (section.Value.TryGetProperty("visualContainers", out var containers) && containers.ValueKind == JsonValueKind.Object)
                {
                    foreach (var container in containers.EnumerateObject())
                    {
                        var count = container.Value.TryGetProperty("filters", out var visualFilter) ? CountFilters(visualFilter) : 0;
                        visualFilters += count;
                        states[$"{section.Name}:{container.Name}"] = new BookmarkVisualState(section.Name, container.Name,
                            TryGetBool(container.Value, "isHidden"), FindFirstString(container.Value, "visualType"), count);
                    }
                }
                if (section.Value.TryGetProperty("visualContainerGroups", out var visualGroups) && visualGroups.ValueKind == JsonValueKind.Object)
                    CollectBookmarkVisibility(section.Name, visualGroups, states, false);
            }
        }

        foreach (var target in targets)
        {
            if (states.Values.Any(x => x.VisualName.Equals(target, StringComparison.OrdinalIgnoreCase))) continue;
            var page = GetString(exploration, "activeSection") ?? "";
            states[$"{page}:{target}"] = new BookmarkVisualState(page, target, null, null, 0);
        }
        return new ReportBookmark(name, GetString(root, "displayName") ?? name, GetString(exploration, "activeSection"),
            GetBool(options, "applyOnlyToTargetVisuals"), suppressData, targets, states.Values.ToList(), reportFilters, visualFilters, !suppressData, file);
    }

    private static void CollectBookmarkVisibility(string pageName, JsonElement items, Dictionary<string, BookmarkVisualState> states, bool parentHidden)
    {
        if (items.ValueKind != JsonValueKind.Object) return;
        foreach (var item in items.EnumerateObject())
        {
            var ownHidden = TryGetBool(item.Value, "isHidden");
            var effectiveHidden = parentHidden || ownHidden == true;
            states[$"{pageName}:{item.Name}"] = new BookmarkVisualState(pageName, item.Name, effectiveHidden, null, 0);
            if (item.Value.TryGetProperty("children", out var children)) CollectBookmarkVisibility(pageName, children, states, effectiveHidden);
        }
    }

    private static ReportBookmark ParseBookmarkFallback(string raw, string file)
    {
        var name = ExtractRawJsonString(raw, "name") ?? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
        var page = ExtractRawJsonString(raw, "activeSection");
        var targets = new List<string>();
        var match = Regex.Match(raw, "\\\"targetVisualNames\\\"\\s*:\\s*\\[(?<items>.*?)\\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (match.Success)
            foreach (Match value in Regex.Matches(match.Groups["items"].Value, "\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\""))
                targets.Add(UnescapeJsonString(value.Groups["value"].Value));
        var suppressData = ExtractRawJsonBool(raw, "suppressData");
        var states = targets.Select(target => new BookmarkVisualState(page ?? "", target, null, null, 0)).ToList();
        return new ReportBookmark(name, ExtractRawJsonString(raw, "displayName") ?? name, page,
            ExtractRawJsonBool(raw, "applyOnlyToTargetVisuals"), suppressData, targets, states, 0, 0, !suppressData, file);
    }

    private static int CountFilters(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.GetArrayLength(),
        JsonValueKind.Object => element.EnumerateObject().Count(),
        _ => 0
    };

    private static bool? TryGetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) ? result : null;
    }

    private static string? FindFirstString(JsonElement element, string propertyName)
    {
        string? result = null;
        Walk(element, (property, value) =>
        {
            if (result is null && property.Equals(propertyName, StringComparison.OrdinalIgnoreCase) && value.ValueKind == JsonValueKind.String) result = value.GetString();
        });
        return result;
    }

    private static string? ExtractRawJsonString(string raw, string property)
    {
        var match = Regex.Match(raw, $"\\\"{Regex.Escape(property)}\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.IgnoreCase);
        return match.Success ? UnescapeJsonString(match.Groups["value"].Value) : null;
    }

    private static bool ExtractRawJsonBool(string raw, string property)
    {
        var match = Regex.Match(raw, $"\\\"{Regex.Escape(property)}\\\"\\s*:\\s*(?<value>true|false)", RegexOptions.IgnoreCase);
        return match.Success && bool.Parse(match.Groups["value"].Value);
    }

    private static string UnescapeJsonString(string value)
    {
        try { return JsonSerializer.Deserialize<string>($"\"{value}\"") ?? value; }
        catch { return value; }
    }

    private static List<ReportPage> ParsePages(string root, List<string> warnings)
    {
        var pagesRoot = Directory.Exists(Path.Combine(root, "definition", "pages")) ? Path.Combine(root, "definition", "pages") : null;
        if (pagesRoot is null) return new List<ReportPage>();
        var pageFiles = Directory.EnumerateFiles(pagesRoot, "page.json", SearchOption.AllDirectories).ToList();
        var order = ReadPageOrder(Path.Combine(pagesRoot, "pages.json"));
        var pages = new List<ReportPage>();
        foreach (var pageFile in pageFiles)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(pageFile), JsonOptions);
                var page = document.RootElement;
                var pageName = GetString(page, "name") ?? Path.GetFileName(Path.GetDirectoryName(pageFile))!;
                var visualsRoot = Path.Combine(Path.GetDirectoryName(pageFile)!, "visuals");
                var visuals = Directory.Exists(visualsRoot)
                    ? Directory.EnumerateFiles(visualsRoot, "visual.json", SearchOption.AllDirectories).Select(ParseVisual).Where(v => v is not null).Cast<ReportVisual>().OrderBy(v => v.Z).ToList()
                    : new List<ReportVisual>();
                pages.Add(new ReportPage(pageName, GetString(page, "displayName") ?? pageName, GetDouble(page, "width", 1280), GetDouble(page, "height", 720), GetString(page, "visibility")?.Contains("Hidden", StringComparison.OrdinalIgnoreCase) == true, GetString(page, "displayOption"), visuals));
            }
            catch (Exception ex) { warnings.Add($"页面 {Path.GetFileName(Path.GetDirectoryName(pageFile))} 解析失败：{ex.Message}"); }
        }
        return pages.OrderBy(page => { var index = order.IndexOf(page.Name); return index < 0 ? int.MaxValue : index; }).ThenBy(page => page.DisplayName).ToList();
    }

    private static List<string> ReadPageOrder(string file)
    {
        if (!File.Exists(file)) return new();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
            if (document.RootElement.TryGetProperty("pageOrder", out var order)) return order.EnumerateArray().Select(item => item.GetString() ?? "").ToList();
        }
        catch { }
        return new();
    }

    private static ReportVisual? ParseVisual(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
        var root = document.RootElement;
        var name = GetString(root, "name") ?? Path.GetFileName(Path.GetDirectoryName(file))!;
        var position = root.TryGetProperty("position", out var p) ? p : default;
        var visual = root.TryGetProperty("visual", out var v) ? v : default;
        var type = visual.ValueKind == JsonValueKind.Object ? GetString(visual, "visualType") ?? "visual" : GetString(root, "visualType") ?? "visual";
        var title = ExtractVisualTitle(root) ?? FriendlyVisualType(type);
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFields(root, fields);
        return new ReportVisual(name, title, type, GetDouble(position, "x"), GetDouble(position, "y"), Math.Max(1, GetDouble(position, "width", 160)), Math.Max(1, GetDouble(position, "height", 90)), GetDouble(position, "z"), (int)GetDouble(position, "tabOrder"), GetBool(root, "isHidden") || GetString(root, "visibility")?.Contains("Hidden", StringComparison.OrdinalIgnoreCase) == true, fields.OrderBy(x => x).ToList(), file);
    }

    private static string? ExtractVisualTitle(JsonElement root)
    {
        string? found = null;
        Walk(root, (property, value) =>
        {
            if (found is null && (property.Equals("titleText", StringComparison.OrdinalIgnoreCase) || property.Equals("text", StringComparison.OrdinalIgnoreCase)) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text) && text.Length < 120) found = text;
            }
        });
        return found;
    }

    private static void CollectFields(JsonElement root, HashSet<string> fields)
    {
        Walk(root, (property, value) =>
        {
            if (value.ValueKind != JsonValueKind.Object) return;
            if (!property.Equals("Column", StringComparison.OrdinalIgnoreCase) && !property.Equals("Measure", StringComparison.OrdinalIgnoreCase) && !property.Equals("HierarchyLevel", StringComparison.OrdinalIgnoreCase)) return;
            var table = GetNestedString(value, "Expression", "SourceRef", "Entity") ?? GetString(value, "Entity");
            var name = GetString(value, "Property") ?? GetString(value, "Name");
            if (!string.IsNullOrWhiteSpace(name)) fields.Add(string.IsNullOrWhiteSpace(table) ? name : $"{table}[{name}]");
        });
    }

    private static void Walk(JsonElement element, Action<string, JsonElement> visitor)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) { visitor(property.Name, property.Value); Walk(property.Value, visitor); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Walk(item, visitor);
    }

    private static List<QualityIssue> AnalyzeQuality(IReadOnlyList<ModelTable> tables, IReadOnlyList<ModelRelationship> relationships, IReadOnlyList<CalculationGroup> calculationGroups, IReadOnlyList<SecurityRole> roles, IReadOnlyList<ReportPage> pages)
    {
        var issues = new List<QualityIssue>();
        foreach (var table in tables)
        {
            foreach (var measure in table.Measures.Where(m => string.IsNullOrWhiteSpace(m.Description)))
                issues.Add(new QualityIssue("MODEL-MEASURE-DESCRIPTION", "warning", "Model", "度量值缺少说明", "建议为度量值补充业务口径与计算说明。", $"{table.Name}[{measure.Name}]"));
            if (table.Columns.Count > 40)
                issues.Add(new QualityIssue("MODEL-WIDE-TABLE", "info", "Model", "表字段较多", $"该表包含 {table.Columns.Count} 个字段，建议检查是否可精简。", table.Name));
        }
        foreach (var relationship in relationships.Where(r => !r.IsActive))
            issues.Add(new QualityIssue("MODEL-INACTIVE-REL", "info", "Model", "存在非活动关系", "请确认度量值是否通过 USERELATIONSHIP 正确启用该关系。", relationship.Name));
        foreach (var item in calculationGroups.SelectMany(group => group.Items.Select(item => (group, item))).Where(x => string.IsNullOrWhiteSpace(x.item.Expression)))
            issues.Add(new QualityIssue("MODEL-CALC-ITEM-EXPRESSION", "error", "Model", "计算项缺少表达式", "计算项未检测到 DAX 表达式。", $"{item.group.Name}[{item.item.Name}]"));
        foreach (var role in roles.Where(role => role.TablePermissions.Count == 0 && role.ObjectPermissions.Count == 0))
            issues.Add(new QualityIssue("SECURITY-EMPTY-ROLE", "warning", "Security", "角色没有安全规则", "该角色拥有读取权限，但没有检测到 RLS 或 OLS 规则。", role.Name));
        foreach (var page in pages)
        {
            foreach (var visual in page.Visuals)
            {
                if (visual.X < 0 || visual.Y < 0 || visual.X + visual.Width > page.Width + 1 || visual.Y + visual.Height > page.Height + 1)
                    issues.Add(new QualityIssue("REPORT-OUTSIDE-CANVAS", "error", "Report", "视觉对象超出画布", $"位置 ({visual.X:0}, {visual.Y:0})，尺寸 {visual.Width:0} × {visual.Height:0}。", visual.Title, page.Name));
            }
            for (var i = 0; i < page.Visuals.Count; i++)
            for (var j = i + 1; j < page.Visuals.Count; j++)
            {
                var a = page.Visuals[i]; var b = page.Visuals[j];
                var overlap = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
                var minArea = Math.Min(a.Width * a.Height, b.Width * b.Height);
                if (minArea > 0 && overlap / minArea > .65 && a.Type != "shape" && b.Type != "shape")
                    issues.Add(new QualityIssue("REPORT-OVERLAP", "warning", "Report", "视觉对象大面积重叠", $"“{a.Title}”与“{b.Title}”重叠超过较小对象的 65%。", $"{a.Title} / {b.Title}", page.Name));
            }
        }
        return issues;
    }

    private static List<ModelDependency> BuildDependencies(IReadOnlyList<ModelTable> tables, IReadOnlyList<CalculationGroup> calculationGroups)
    {
        var tableMap = tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
        var measuresByName = tables.SelectMany(table => table.Measures.Select(measure => (table: table.Name, measure)))
            .GroupBy(x => x.measure.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var columnsByName = tables.SelectMany(table => table.Columns.Select(column => (table: table.Name, column)))
            .GroupBy(x => x.column.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<string, ModelDependency>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
        foreach (var measure in table.Measures)
            AddExpressionDependencies($"measure:{table.Name}:{measure.Name}", $"{table.Name}[{measure.Name}]", "measure", table.Name, measure.Expression);

        foreach (var group in calculationGroups)
        foreach (var item in group.Items)
        {
            AddExpressionDependencies($"calcitem:{group.Name}:{item.Name}", $"{group.Name}[{item.Name}]", "calculationItem", group.Name, item.Expression);
            if (!string.IsNullOrWhiteSpace(item.FormatStringExpression)) AddExpressionDependencies($"calcitem:{group.Name}:{item.Name}", $"{group.Name}[{item.Name}]", "calculationItem", group.Name, item.FormatStringExpression!);
        }
        return dependencies.Values.ToList();

        void AddExpressionDependencies(string sourceId, string sourceName, string sourceType, string sourceTable, string expression)
        {
            foreach (var reference in ExtractDaxReferences(expression))
            {
                string? targetTable = reference.Table;
                string? targetType = null;
                string? targetName = reference.Object;
                if (!string.IsNullOrWhiteSpace(targetTable) && tableMap.TryGetValue(targetTable, out var explicitTable))
                {
                    if (explicitTable.Measures.Any(m => m.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))) targetType = "measure";
                    else if (explicitTable.Columns.Any(c => c.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))) targetType = "column";
                }
                else
                {
                    if (measuresByName.TryGetValue(targetName, out var measureMatches))
                    {
                        var preferred = measureMatches.FirstOrDefault(x => x.table.Equals(sourceTable, StringComparison.OrdinalIgnoreCase));
                        var match = preferred.measure is not null ? preferred : measureMatches[0];
                        targetTable = match.table; targetType = "measure";
                    }
                    else if (columnsByName.TryGetValue(targetName, out var columnMatches) && columnMatches.Count == 1)
                    {
                        targetTable = columnMatches[0].table; targetType = "column";
                    }
                }
                if (targetType is null || string.IsNullOrWhiteSpace(targetTable)) continue;
                var targetId = $"{targetType}:{targetTable}:{targetName}";
                if (targetId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)) continue;
                var dependency = new ModelDependency(sourceId, sourceName, sourceType, targetId, $"{targetTable}[{targetName}]", targetType, reference.Raw);
                dependencies[$"{sourceId}>{targetId}"] = dependency;
            }
        }
    }

    private static IEnumerable<(string? Table, string Object, string Raw)> ExtractDaxReferences(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) yield break;
        var regex = new Regex(@"(?:(?<quoted>'(?:[^']|'')+')|(?<plain>[\p{L}\p{N}_.]+))?\s*\[(?<object>[^\]]+)\]", RegexOptions.Compiled);
        foreach (Match match in regex.Matches(expression))
        {
            var table = match.Groups["quoted"].Success ? Unquote(match.Groups["quoted"].Value) : match.Groups["plain"].Success ? match.Groups["plain"].Value : null;
            yield return (table, match.Groups["object"].Value.Trim(), match.Value.Trim());
        }
    }

    private static (string table, string column) ParseReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ("", "");
        var text = value.Trim();
        var bracket = text.LastIndexOf('[');
        if (bracket > 0 && text.EndsWith(']')) return (Unquote(text[..bracket].Trim()), text[(bracket + 1)..^1]);
        var dot = text.LastIndexOf('.');
        return dot > 0 ? (Unquote(text[..dot]), Unquote(text[(dot + 1)..])) : ("", Unquote(text));
    }

    private static string FriendlyVisualType(string type) => type switch
    {
        "card" or "cardVisual" => "卡片",
        "textbox" => "文本框",
        "slicer" => "切片器",
        "tableEx" => "表格",
        "matrix" => "矩阵",
        "lineChart" => "折线图",
        "barChart" or "clusteredBarChart" => "条形图",
        "columnChart" or "clusteredColumnChart" => "柱形图",
        "pieChart" => "饼图",
        "donutChart" => "环形图",
        "shape" => "形状",
        "image" => "图像",
        _ => type
    };

    private static int LeadingWhitespace(string value) => value.TakeWhile(char.IsWhiteSpace).Count();
    private static string? ReadPrecedingDocComment(string[] lines, int index)
    {
        var comments = new List<string>();
        for (var i = index - 1; i >= 0; i--)
        {
            var text = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(text) && comments.Count == 0) continue;
            if (!text.StartsWith("///")) break;
            comments.Insert(0, text[3..].Trim());
        }
        return comments.Count == 0 ? null : string.Join(" ", comments);
    }
    private static string Unquote(string value)
    {
        value = value.Trim();
        if ((value.StartsWith('\'') && value.EndsWith('\'')) || (value.StartsWith('"') && value.EndsWith('"'))) value = value[1..^1];
        return value.Replace("''", "'");
    }
    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;
    private static bool GetBool(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) && result);
    private static double GetDouble(JsonElement element, string name, double fallback = 0) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && (value.TryGetDouble(out var number) || value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)) ? number : fallback;
    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    private static string? GetNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path) if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
    private static string? GetExpression(JsonElement element)
    {
        if (!element.TryGetProperty("expression", out var expression)) return null;
        return expression.ValueKind switch
        {
            JsonValueKind.String => expression.GetString(),
            JsonValueKind.Array => string.Join(Environment.NewLine, expression.EnumerateArray().Select(item => item.GetString())),
            _ => expression.ToString()
        };
    }
}
