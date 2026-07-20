using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace NexusEditor;

public static class ResearchWorkbookService
{
    public const string CategorySheetName = "nexus_node_category";
    public const string NodeSheetName = "nexus_node";
    public const string EffectSheetName = "nexus_effect";
    public const string DefaultResearchExportId = "lbh9517_175126";

    private const int FirstDataRow = 4;
    private const int CategoryColumnCount = 6;
    private const int NodeColumnCount = 20;
    private const int EffectColumnCount = 10;

    private static readonly Regex InvisibleFormatCharacters = new(@"\p{Cf}", RegexOptions.Compiled);
    private static readonly Regex IdentifierPattern = new(@"^[a-z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex EffectMemoCoordinatePattern = new(
        @"(?<prefix>\bnode\s+)\d+\s*,\s*\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EffectIdCoordinatePattern = new(
        @"^(?<theme>[a-z0-9_]+)_node_eff_(?<category>.+)_(?<column>\d+)_(?<row>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ResearchWorkbookContext Load(string outSystemPath, string effectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outSystemPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectPath);

        using var outSystemWorkbook = OpenWorkbookSnapshot(outSystemPath);
        using var effectWorkbook = OpenWorkbookSnapshot(effectPath);

        if (!outSystemWorkbook.Worksheets.TryGetWorksheet(CategorySheetName, out var categorySheet))
            throw new InvalidDataException($"{CategorySheetName} 시트를 찾을 수 없습니다: {outSystemPath}");
        if (!outSystemWorkbook.Worksheets.TryGetWorksheet(NodeSheetName, out var nodeSheet))
            throw new InvalidDataException($"{NodeSheetName} 시트를 찾을 수 없습니다: {outSystemPath}");
        if (!effectWorkbook.Worksheets.TryGetWorksheet(EffectSheetName, out var effectSheet))
            throw new InvalidDataException($"{EffectSheetName} 시트를 찾을 수 없습니다: {effectPath}");

        var context = new ResearchWorkbookContext
        {
            SourceOutSystemPath = Path.GetFullPath(outSystemPath),
            SourceEffectPath = Path.GetFullPath(effectPath),
            SourceOutSystemSignature = ComputeFileSignature(outSystemPath),
            SourceEffectSignature = ComputeFileSignature(effectPath)
        };

        LoadCategories(categorySheet, context);
        LoadNodes(nodeSheet, context);
        LoadEffects(effectSheet, context);
        Sanitize(context);
        AssociateNodeEffects(context);
        context.OriginalSnapshot = context.DeepClone(includeOriginalSnapshot: false);
        return context;
    }

    public static XLWorkbook OpenWorkbookSnapshot(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var copy = new MemoryStream();
        source.CopyTo(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    public static string GenerateCategoryId(string? helperPrefix, string? categoryKey)
    {
        var parts = new[] { SanitizeIdentifierPart(helperPrefix), SanitizeIdentifierPart(categoryKey) }
            .Where(part => part.Length > 0);
        return string.Join("_", parts);
    }

    public static string GenerateCategoryNodeName(string? categoryKey) =>
        $"s1_node_{SanitizeIdentifierPart(categoryKey)}_name";

    public static string GenerateNodeId(string? themeId, string? category, int column, int row) =>
        $"{SanitizeIdentifierPart(themeId)}_node_{SanitizeIdentifierPart(category)}_{column}_{row}";

    public static string GenerateNodeEffectDescription(string? category) =>
        $"s1_node_{SanitizeIdentifierPart(category)}_desc";

    public static string GenerateEffectId(string? themeId, string? category, int column, int row) =>
        $"{SanitizeIdentifierPart(themeId)}_node_eff_{SanitizeIdentifierPart(category)}_{column}_{row}";

    public static double CalculateActiveItemTotal(string? activeItemValue)
    {
        var values = (activeItemValue ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var total = 0d;
        foreach (var value in values)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                throw new InvalidOperationException($"active_item_value에는 숫자만 입력하세요. ({value})");
            total += parsed;
        }
        return total;
    }

    public static int Sanitize(ResearchWorkbookContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var changed = 0;

        foreach (var category in context.Categories)
        {
            changed += SanitizeField(category.Category, value => category.Category = value);
            changed += SanitizeField(category.ExportId, value => category.ExportId = value);
            changed += SanitizeField(category.HelperPrefix, value => category.HelperPrefix = value);
            changed += SanitizeField(category.CategoryKey, value => category.CategoryKey = value);
            changed += SanitizeField(category.NodeName, value => category.NodeName = value);
            changed += SanitizeField(category.CategoryFormulaA1, value => category.CategoryFormulaA1 = value);
            changed += SanitizeField(category.NodeNameFormulaA1, value => category.NodeNameFormulaA1 = value);
        }

        foreach (var node in context.Nodes)
        {
            changed += SanitizeField(node.Id, value => node.Id = value);
            changed += SanitizeField(node.ExportId, value => node.ExportId = value);
            changed += SanitizeField(node.ThemeId, value => node.ThemeId = value);
            changed += SanitizeField(node.Category, value => node.Category = value);
            changed += SanitizeField(node.CategoryName, value => node.CategoryName = value);
            changed += SanitizeField(node.NodeEffectDesc, value => node.NodeEffectDesc = value);
            changed += SanitizeField(node.Image, value => node.Image = value);
            changed += SanitizeField(node.ActiveItemId, value => node.ActiveItemId = value);
            changed += SanitizeField(node.ActiveItemValue, value => node.ActiveItemValue = value);
            for (var index = 0; index < 5; index++)
            {
                var conditionIndex = index;
                changed += SanitizeField(node.Conditions[index], value => node.SetCondition(conditionIndex, value));
            }
            changed += SanitizeField(node.NexusEffectId, value => node.NexusEffectId = value);
            changed += SanitizeField(node.IdFormulaA1, value => node.IdFormulaA1 = value);
            changed += SanitizeField(node.CategoryNameFormulaA1, value => node.CategoryNameFormulaA1 = value);
            changed += SanitizeField(node.NodeEffectDescFormulaA1, value => node.NodeEffectDescFormulaA1 = value);
            changed += SanitizeField(node.NexusEffectIdFormulaA1, value => node.NexusEffectIdFormulaA1 = value);
        }

        foreach (var effect in context.Effects)
        {
            for (var index = 0; index < EffectColumnCount; index++)
            {
                var cell = effect.GetCell(index);
                var sanitizedText = SanitizeText(cell.Text);
                var sanitizedFormula = SanitizeText(cell.FormulaA1);
                if (cell.Text == sanitizedText && cell.FormulaA1 == sanitizedFormula)
                    continue;

                var replacement = cell.DeepClone();
                replacement.Text = sanitizedText;
                replacement.FormulaA1 = sanitizedFormula;
                effect.SetCell(index, replacement);
                changed++;
            }
        }

        return changed;
    }

    public static List<ResearchValidationIssue> Validate(ResearchWorkbookContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<ResearchValidationIssue>();
        var categoriesById = UniqueRows(context.Categories, row => row.Category);
        var nodesById = UniqueRows(context.Nodes, row => row.Id);

        AddDuplicateIssues(issues, CategorySheetName, "category_duplicate", "카테고리 ID",
            context.Categories, row => row.Category, row => row.RowIdentity);
        AddDuplicateIssues(issues, CategorySheetName, "category_index_duplicate", "카테고리 index",
            context.Categories.Where(row => row.Index.HasValue), row => row.Index?.ToString(CultureInfo.InvariantCulture), row => row.RowIdentity);
        AddDuplicateIssues(issues, NodeSheetName, "node_id_duplicate", "연구 노드 ID",
            context.Nodes, row => row.Id, row => row.RowIdentity);
        AddDuplicateIssues(issues, EffectSheetName, "effect_id_duplicate", "연구 효과 ID",
            context.Effects, row => row.Id, row => row.RowIdentity);

        foreach (var duplicateCoordinate in context.Nodes
                     .GroupBy(node => $"{node.Category}\u001f{node.Column}\u001f{node.Row}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var node in duplicateCoordinate)
            {
                issues.Add(Error("coordinate_duplicate",
                    $"{node.Id}: 같은 카테고리의 좌표 ({node.Column}, {node.Row})가 중복됩니다.",
                    NodeSheetName, node.RowIdentity, node.Id, "column,row"));
            }
        }

        foreach (var category in context.Categories)
        {
            var expectedCategory = GenerateCategoryId(category.HelperPrefix, category.CategoryKey);
            if (string.IsNullOrWhiteSpace(category.Category))
                issues.Add(Error("category_blank", "카테고리 ID가 비어 있습니다.", CategorySheetName, category.RowIdentity, "", "category"));
            else if (!Same(category.Category, expectedCategory))
                issues.Add(Error("category_generated_mismatch",
                    $"{category.Category}: helper C/D로 생성한 ID는 {expectedCategory}입니다.",
                    CategorySheetName, category.RowIdentity, category.Category, "category"));

            if (!IdentifierPattern.IsMatch(category.Category))
                issues.Add(Error("category_format", $"{category.Category}: 카테고리 ID는 소문자 영문, 숫자, 밑줄만 사용할 수 있습니다.",
                    CategorySheetName, category.RowIdentity, category.Category, "category"));
            if (!Same(category.NodeName, GenerateCategoryNodeName(category.CategoryKey)))
                issues.Add(Error("category_node_name_mismatch", $"{category.Category}: node_name 생성값이 올바르지 않습니다.",
                    CategorySheetName, category.RowIdentity, category.Category, "node_name"));
            if (!SameFormula(category.CategoryFormulaA1, CategoryFormula(category.SourceRowNumber)))
                issues.Add(Error("category_formula", $"{category.Category}: category 수식이 예상 형식이 아닙니다.",
                    CategorySheetName, category.RowIdentity, category.Category, "category"));
            if (!SameFormula(category.NodeNameFormulaA1, CategoryNodeNameFormula(category.SourceRowNumber)))
                issues.Add(Error("category_node_name_formula", $"{category.Category}: node_name 수식이 예상 형식이 아닙니다.",
                    CategorySheetName, category.RowIdentity, category.Category, "node_name"));
        }

        var linkedEffectRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var legacyEffectMismatches = new List<(ResearchNodeRow Node, ResearchEffectRow Effect)>();
        foreach (var node in context.Nodes)
        {
            var expectedId = GenerateNodeId(node.ThemeId, node.Category, node.Column, node.Row);
            var expectedEffectId = GenerateEffectId(node.ThemeId, node.Category, node.Column, node.Row);

            if (!categoriesById.ContainsKey(node.Category))
                issues.Add(Error("missing_category", $"{node.Id}: category가 nexus_node_category에 없습니다. ({node.Category})",
                    NodeSheetName, node.RowIdentity, node.Id, "category"));
            if (!Same(node.Id, expectedId))
                issues.Add(Error("node_generated_id", $"{node.Id}: 현재 필드로 생성한 ID는 {expectedId}입니다.",
                    NodeSheetName, node.RowIdentity, node.Id, "id"));
            if (!Same(node.NexusEffectId, expectedEffectId))
                issues.Add(Error("node_generated_effect_id", $"{node.Id}: nexus_effect_id 생성값은 {expectedEffectId}입니다.",
                    NodeSheetName, node.RowIdentity, node.Id, "nexus_effect_id"));
            if (!Same(node.CategoryName, $"s1_node_{SanitizeIdentifierPart(node.Category)}_name"))
                issues.Add(Error("node_category_name", $"{node.Id}: category_name 생성값이 올바르지 않습니다.",
                    NodeSheetName, node.RowIdentity, node.Id, "category_name"));
            if (!Same(node.NodeEffectDesc, GenerateNodeEffectDescription(node.Category)))
                issues.Add(Error("node_effect_desc", $"{node.Id}: node_effect_desc 생성값이 올바르지 않습니다.",
                    NodeSheetName, node.RowIdentity, node.Id, "node_effect_desc"));
            if (node.Column <= 0 || node.Row <= 0)
                issues.Add(Error("coordinate_invalid", $"{node.Id}: column과 row는 1 이상이어야 합니다.",
                    NodeSheetName, node.RowIdentity, node.Id, "column,row"));
            if (string.IsNullOrWhiteSpace(node.Image))
                issues.Add(Error("image_blank", $"{node.Id}: image가 비어 있습니다.",
                    NodeSheetName, node.RowIdentity, node.Id, "image"));

            ValidateNodeFormulas(issues, node);
            ValidateActivationCost(issues, node);
            ValidateConditions(issues, node, nodesById);

            var linkedEffect = context.FindEffect(node);
            if (linkedEffect is null)
            {
                issues.Add(Error("node_effect_missing",
                    $"{node.Id}: 정확히 하나의 연구 효과가 필요합니다. ({node.NexusEffectId})",
                    NodeSheetName, node.RowIdentity, node.Id, "nexus_effect_id"));
            }
            else
            {
                linkedEffectRows.Add(linkedEffect.RowIdentity);
                if (!Same(linkedEffect.Id, node.NexusEffectId))
                    legacyEffectMismatches.Add((node, linkedEffect));
            }
        }

        foreach (var mismatchGroup in legacyEffectMismatches
                     .GroupBy(item => item.Node.Category, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = mismatchGroup.First();
            issues.Add(Warning("node_effect_id_mismatch",
                $"{mismatchGroup.Key}: 레거시 연구 효과 {mismatchGroup.Count():N0}개를 좌표 순서로 연결했습니다. "
                + $"노드를 이동하거나 카테고리를 바꾸면 해당 효과 ID가 현재 노드 좌표 규칙으로 정규화됩니다. "
                + $"예: {first.Node.Id} -> {first.Effect.Id}",
                NodeSheetName, first.Node.RowIdentity, first.Node.Id, "nexus_effect_id"));
        }

        foreach (var effect in context.Effects)
        {
            if (!linkedEffectRows.Contains(effect.RowIdentity))
                issues.Add(Error("orphan_effect", $"{effect.Id}: 대응하는 nexus_node가 없습니다.",
                    EffectSheetName, effect.RowIdentity, effect.Id, "id"));
        }

        AddTopologyIssues(issues, context.Nodes, nodesById);
        AddCycleIssues(issues, context.Nodes, nodesById);
        return issues;
    }

    private static void ValidateNodeFormulas(List<ResearchValidationIssue> issues, ResearchNodeRow node)
    {
        var row = node.SourceRowNumber;
        if (!SameFormula(node.IdFormulaA1, NodeIdFormula(row)))
            issues.Add(Error("node_id_formula", $"{node.Id}: id 수식이 예상 형식이 아닙니다.", NodeSheetName, node.RowIdentity, node.Id, "id"));
        if (!SameFormula(node.CategoryNameFormulaA1, NodeCategoryNameFormula(row)))
            issues.Add(Error("node_category_name_formula", $"{node.Id}: category_name 수식이 예상 형식이 아닙니다.", NodeSheetName, node.RowIdentity, node.Id, "category_name"));
        if (!SameFormula(node.NodeEffectDescFormulaA1, NodeEffectDescriptionFormula(row)))
            issues.Add(Error("node_effect_desc_formula", $"{node.Id}: node_effect_desc 수식이 예상 형식이 아닙니다.", NodeSheetName, node.RowIdentity, node.Id, "node_effect_desc"));
        if (!SameFormula(node.NexusEffectIdFormulaA1, NodeEffectIdFormula(row)))
            issues.Add(Error("node_effect_id_formula", $"{node.Id}: nexus_effect_id 수식이 예상 형식이 아닙니다.", NodeSheetName, node.RowIdentity, node.Id, "nexus_effect_id"));
    }

    private static void ValidateActivationCost(List<ResearchValidationIssue> issues, ResearchNodeRow node)
    {
        var values = node.ActiveItemValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if ((node.ActiveStep ?? 0) != values.Length)
        {
            issues.Add(Error("active_step_count", $"{node.Id}: active_item_value 개수({values.Length})와 active_step({node.ActiveStep ?? 0})이 다릅니다.",
                NodeSheetName, node.RowIdentity, node.Id, "active_item_value,active_step"));
        }

        double total;
        try
        {
            total = CalculateActiveItemTotal(node.ActiveItemValue);
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(Error("active_item_value_format", $"{node.Id}: {ex.Message}",
                NodeSheetName, node.RowIdentity, node.Id, "active_item_value"));
            return;
        }

        if (!node.TotalRequiredHelper.HasValue || Math.Abs(node.TotalRequiredHelper.Value - total) > 0.0000001d)
        {
            issues.Add(Error("active_total_helper", $"{node.Id}: 전체 필요 개수 helper는 {total.ToString("G17", CultureInfo.InvariantCulture)}이어야 합니다.",
                NodeSheetName, node.RowIdentity, node.Id, "K"));
        }
    }

    private static void ValidateConditions(
        List<ResearchValidationIssue> issues,
        ResearchNodeRow node,
        Dictionary<string, List<ResearchNodeRow>> nodesById)
    {
        var conditions = node.Conditions.Where(NotBlank).ToList();
        if (conditions.Count > 5)
            issues.Add(Error("condition_max5", $"{node.Id}: 선행 조건은 최대 5개입니다.", NodeSheetName, node.RowIdentity, node.Id, "condition_node_1~5"));

        foreach (var duplicate in conditions.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            issues.Add(Error("condition_duplicate", $"{node.Id}: 같은 선행 조건이 중복됩니다. ({duplicate.Key})",
                NodeSheetName, node.RowIdentity, node.Id, "condition_node_1~5"));
        }

        foreach (var condition in conditions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Same(condition, node.Id))
            {
                issues.Add(Error("condition_self", $"{node.Id}: 자기 자신을 선행 조건으로 사용할 수 없습니다.",
                    NodeSheetName, node.RowIdentity, node.Id, "condition_node_1~5"));
                continue;
            }

            if (!nodesById.TryGetValue(condition, out var prerequisiteRows) || prerequisiteRows.Count == 0)
            {
                issues.Add(Error("condition_missing", $"{node.Id}: 선행 조건 노드가 존재하지 않습니다. ({condition})",
                    NodeSheetName, node.RowIdentity, node.Id, "condition_node_1~5"));
                continue;
            }

            var prerequisite = prerequisiteRows[0];
            if (!Same(prerequisite.Category, node.Category))
                issues.Add(Error("condition_cross_category", $"{node.Id}: 다른 카테고리 노드를 선행 조건으로 사용할 수 없습니다. ({condition})",
                    NodeSheetName, node.RowIdentity, node.Id, "condition_node_1~5"));
            if (prerequisite.Column >= node.Column)
                issues.Add(Error("condition_non_backward", $"{node.Id}: 선행 조건은 더 앞 column에 있어야 합니다. ({condition})",
                    NodeSheetName, node.RowIdentity, node.Id, "condition_node_1~5"));
            else if (prerequisite.Column + 1 != node.Column)
                issues.Add(Error("condition_non_adjacent", $"{node.Id}: 바로 이전 column의 노드만 선행 조건으로 연결할 수 있습니다. ({condition})",
                    NodeSheetName, node.RowIdentity, node.Id, "condition_node_1~5"));
        }
    }

    private static void AddTopologyIssues(
        List<ResearchValidationIssue> issues,
        IReadOnlyCollection<ResearchNodeRow> nodes,
        Dictionary<string, List<ResearchNodeRow>> nodesById)
    {
        var outgoing = new Dictionary<string, List<ResearchNodeRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in nodes)
        {
            foreach (var condition in target.Conditions.Where(NotBlank).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!nodesById.TryGetValue(condition, out var sourceRows) || sourceRows.Count == 0)
                    continue;
                var source = sourceRows[0];
                if (!Same(source.Category, target.Category) || source.Column + 1 != target.Column)
                    continue;
                if (!outgoing.TryGetValue(source.Id, out var targets))
                {
                    targets = [];
                    outgoing[source.Id] = targets;
                }
                if (targets.All(candidate => !Same(candidate.RowIdentity, target.RowIdentity)))
                    targets.Add(target);
            }
        }

        var exactMergeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in nodes.Where(node => NotBlank(node.Id)))
        {
            if (!outgoing.TryGetValue(source.Id, out var targets))
                continue;
            if (targets.Count > 3)
            {
                issues.Add(Error("condition_fanout_max3",
                    $"{source.Id}: 후행 노드가 {targets.Count}개입니다. 연구 구조는 최대 3갈래까지만 편집할 수 있습니다.",
                    NodeSheetName, source.RowIdentity, source.Id, "condition_node_1~5"));
                continue;
            }
            if (targets.Count != 3)
                continue;

            HashSet<string>? commonTargets = null;
            foreach (var child in targets)
            {
                var nextIds = outgoing.TryGetValue(child.Id, out var nextNodes)
                    ? nextNodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (commonTargets is null)
                    commonTargets = nextIds;
                else
                    commonTargets.IntersectWith(nextIds);
            }

            var mergeTargetId = commonTargets?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(mergeTargetId))
                exactMergeTargets.Add(mergeTargetId);
            var shape = string.IsNullOrWhiteSpace(mergeTargetId) ? "1-3 분기" : $"1-3-1 분기 ({mergeTargetId}로 합류)";
            issues.Add(Warning("condition_branch_1_3_1",
                $"{source.Id}: {shape} 구조입니다. 현재 시스템 기준은 1-2-1이므로 연결을 확인하세요.",
                NodeSheetName, source.RowIdentity, source.Id, "condition_node_1~5"));
        }

        foreach (var target in nodes)
        {
            var incomingCount = target.Conditions
                .Where(NotBlank)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(condition => nodesById.TryGetValue(condition, out var sourceRows)
                                    && sourceRows.Count > 0
                                    && Same(sourceRows[0].Category, target.Category)
                                    && sourceRows[0].Column + 1 == target.Column);
            if (incomingCount > 3)
            {
                issues.Add(Error("condition_fanin_max3",
                    $"{target.Id}: 선행 노드가 {incomingCount}개입니다. 연구 구조는 최대 3갈래까지만 편집할 수 있습니다.",
                    NodeSheetName, target.RowIdentity, target.Id, "condition_node_1~5"));
            }
            else if (incomingCount == 3 && !exactMergeTargets.Contains(target.Id))
            {
                issues.Add(Warning("condition_merge_3_1",
                    $"{target.Id}: 3-1 합류 구조입니다. 현재 시스템 기준은 1-2-1이므로 연결을 확인하세요.",
                    NodeSheetName, target.RowIdentity, target.Id, "condition_node_1~5"));
            }
        }
    }

    private static void AddCycleIssues(
        List<ResearchValidationIssue> issues,
        IReadOnlyCollection<ResearchNodeRow> nodes,
        Dictionary<string, List<ResearchNodeRow>> nodesById)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ResearchNodeRow node)
        {
            if (state.TryGetValue(node.Id, out var currentState))
            {
                if (currentState == 2)
                    return;
                if (currentState == 1)
                {
                    var start = stack.FindIndex(id => Same(id, node.Id));
                    var cycle = start >= 0 ? stack.Skip(start).Append(node.Id).ToList() : [node.Id];
                    var key = string.Join(" -> ", cycle);
                    if (reported.Add(key))
                        issues.Add(Error("condition_cycle", $"연구 노드 선행 조건이 순환합니다: {key}", NodeSheetName, node.RowIdentity, node.Id, "condition_node_1~5"));
                }
                return;
            }

            state[node.Id] = 1;
            stack.Add(node.Id);
            foreach (var condition in node.Conditions.Where(NotBlank))
            {
                if (nodesById.TryGetValue(condition, out var candidates) && candidates.Count > 0)
                    Visit(candidates[0]);
            }
            stack.RemoveAt(stack.Count - 1);
            state[node.Id] = 2;
        }

        foreach (var node in nodes)
            Visit(node);
    }

    private static void LoadCategories(IXLWorksheet sheet, ResearchWorkbookContext context)
    {
        for (var row = FirstDataRow; row <= LastRow(sheet); row++)
        {
            if (!HasData(sheet, row, CategoryColumnCount))
                continue;

            var helperPrefix = CellText(sheet.Cell(row, 3));
            var categoryKey = CellText(sheet.Cell(row, 4));
            context.Categories.Add(new ResearchCategoryRow
            {
                RowIdentity = $"category:{row}",
                SourceRowNumber = row,
                Category = ValueOrGenerated(CellText(sheet.Cell(row, 1)), GenerateCategoryId(helperPrefix, categoryKey)),
                ExportId = CellText(sheet.Cell(row, 2)),
                HelperPrefix = helperPrefix,
                CategoryKey = categoryKey,
                Index = NullableInt(sheet.Cell(row, 5)),
                NodeName = ValueOrGenerated(CellText(sheet.Cell(row, 6)), GenerateCategoryNodeName(categoryKey)),
                CategoryFormulaA1 = Formula(sheet.Cell(row, 1)),
                NodeNameFormulaA1 = Formula(sheet.Cell(row, 6))
            });
        }
    }

    private static void LoadNodes(IXLWorksheet sheet, ResearchWorkbookContext context)
    {
        for (var row = FirstDataRow; row <= LastRow(sheet); row++)
        {
            if (!HasData(sheet, row, NodeColumnCount))
                continue;

            var themeId = CellText(sheet.Cell(row, 3));
            var category = CellText(sheet.Cell(row, 4));
            var column = NullableInt(sheet.Cell(row, 13)) ?? 0;
            var nodeRow = NullableInt(sheet.Cell(row, 14)) ?? 0;
            context.Nodes.Add(new ResearchNodeRow
            {
                RowIdentity = $"node:{row}",
                SourceRowNumber = row,
                Id = GenerateNodeId(themeId, category, column, nodeRow),
                ExportId = CellText(sheet.Cell(row, 2)),
                ThemeId = themeId,
                Category = category,
                CategoryName = $"s1_node_{SanitizeIdentifierPart(category)}_name",
                NodeEffectDesc = GenerateNodeEffectDescription(category),
                Image = CellText(sheet.Cell(row, 7)),
                NodePermission = NullableInt(sheet.Cell(row, 8)),
                ActiveItemId = CellText(sheet.Cell(row, 9)),
                ActiveItemValue = CellText(sheet.Cell(row, 10)),
                TotalRequiredHelper = NullableDouble(sheet.Cell(row, 11)),
                ActiveStep = NullableInt(sheet.Cell(row, 12)),
                Column = column,
                Row = nodeRow,
                ConditionNode1 = CellText(sheet.Cell(row, 15)),
                ConditionNode2 = CellText(sheet.Cell(row, 16)),
                ConditionNode3 = CellText(sheet.Cell(row, 17)),
                ConditionNode4 = CellText(sheet.Cell(row, 18)),
                ConditionNode5 = CellText(sheet.Cell(row, 19)),
                NexusEffectId = GenerateEffectId(themeId, category, column, nodeRow),
                IdFormulaA1 = Formula(sheet.Cell(row, 1)),
                CategoryNameFormulaA1 = Formula(sheet.Cell(row, 5)),
                NodeEffectDescFormulaA1 = Formula(sheet.Cell(row, 6)),
                NexusEffectIdFormulaA1 = Formula(sheet.Cell(row, 20))
            });
        }
    }

    private static void LoadEffects(IXLWorksheet sheet, ResearchWorkbookContext context)
    {
        for (var row = FirstDataRow; row <= LastRow(sheet); row++)
        {
            var id = CellText(sheet.Cell(row, 1));
            if (!EffectIdCoordinatePattern.IsMatch(id))
                continue;

            var effect = new ResearchEffectRow
            {
                RowIdentity = $"effect:{row}",
                SourceRowNumber = row
            };
            for (var column = 1; column <= EffectColumnCount; column++)
                effect.SetCell(column - 1, CaptureCell(sheet.Cell(row, column)));
            context.Effects.Add(effect);
        }
    }

    public static void AssociateNodeEffects(ResearchWorkbookContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var effect in context.Effects)
            effect.LinkedNodeRowIdentity = "";

        var linkedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in context.Nodes)
        {
            var exact = context.Effects.FirstOrDefault(effect =>
                string.IsNullOrWhiteSpace(effect.LinkedNodeRowIdentity)
                && Same(effect.Id, node.NexusEffectId));
            if (exact is null)
                continue;
            exact.LinkedNodeRowIdentity = node.RowIdentity;
            linkedNodes.Add(node.RowIdentity);
        }

        var remainingNodes = context.Nodes
            .Where(node => !linkedNodes.Contains(node.RowIdentity))
            .GroupBy(NodeEffectGroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(node => node.Row).ThenBy(node => node.SourceRowNumber).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var remainingEffects = context.Effects
            .Where(effect => string.IsNullOrWhiteSpace(effect.LinkedNodeRowIdentity))
            .Select(effect => (Effect: effect, Coordinate: ParseEffectCoordinate(effect.Id)))
            .Where(item => item.Coordinate is not null)
            .GroupBy(item => item.Coordinate!.Value.GroupKey, StringComparer.OrdinalIgnoreCase);

        foreach (var effectGroup in remainingEffects)
        {
            if (!remainingNodes.TryGetValue(effectGroup.Key, out var nodes))
                continue;
            var effects = effectGroup
                .OrderBy(item => item.Coordinate!.Value.Row)
                .ThenBy(item => item.Effect.SourceRowNumber)
                .Select(item => item.Effect)
                .ToList();
            for (var index = 0; index < Math.Min(nodes.Count, effects.Count); index++)
                effects[index].LinkedNodeRowIdentity = nodes[index].RowIdentity;
        }
    }

    private static string NodeEffectGroupKey(ResearchNodeRow node) =>
        $"{node.ThemeId}\u001f{node.Category}\u001f{node.Column}";

    private static (string GroupKey, int Row)? ParseEffectCoordinate(string? effectId)
    {
        var match = EffectIdCoordinatePattern.Match(effectId ?? "");
        if (!match.Success
            || !int.TryParse(match.Groups["column"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var column)
            || !int.TryParse(match.Groups["row"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var row))
        {
            return null;
        }

        var key = $"{match.Groups["theme"].Value}\u001f{match.Groups["category"].Value}\u001f{column}";
        return (key, row);
    }

    public static List<ResearchDiffEntry> BuildDiff(ResearchWorkbookContext edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        var original = edited.OriginalSnapshot ?? new ResearchWorkbookContext();
        return BuildDiff(edited, original);
    }

    public static List<ResearchDiffEntry> BuildDiff(
        ResearchWorkbookContext edited,
        ResearchWorkbookContext original)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(original);
        var diff = new List<ResearchDiffEntry>();

        CompareRows(diff, CategorySheetName,
            original.Categories.Select(SnapshotCategory),
            edited.Categories.Select(SnapshotCategory));
        CompareRows(diff, NodeSheetName,
            original.Nodes.Select(SnapshotNode),
            edited.Nodes.Select(SnapshotNode));
        CompareRows(diff, EffectSheetName,
            original.Effects.Select(SnapshotEffect),
            edited.Effects.Select(SnapshotEffect));
        return diff;
    }

    public static bool RevertDiff(ResearchWorkbookContext context, ResearchDiffEntry entry)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entry);
        var original = context.OriginalSnapshot
            ?? throw new InvalidOperationException("원본 스냅샷이 없어 변경을 되돌릴 수 없습니다.");

        return entry.Sheet switch
        {
            CategorySheetName => RevertRow(
                context.Categories,
                original.Categories,
                entry.RowIdentity,
                row => row.RowIdentity,
                row => row.SourceRowNumber,
                row => row.DeepClone()),
            NodeSheetName => RevertRow(
                context.Nodes,
                original.Nodes,
                entry.RowIdentity,
                row => row.RowIdentity,
                row => row.SourceRowNumber,
                row => row.DeepClone()),
            EffectSheetName => RevertRow(
                context.Effects,
                original.Effects,
                entry.RowIdentity,
                row => row.RowIdentity,
                row => row.SourceRowNumber,
                row => row.DeepClone()),
            _ => false
        };
    }

    public static int RevertDiffs(
        ResearchWorkbookContext context,
        IEnumerable<ResearchDiffEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entries);
        var reverted = 0;
        foreach (var entry in entries
                     .GroupBy(item => $"{item.Sheet}\u001f{item.RowIdentity}", StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            if (RevertDiff(context, entry))
                reverted++;
        }
        return reverted;
    }

    public static int RevertRelatedDiffs(
        ResearchWorkbookContext context,
        IEnumerable<ResearchDiffEntry> selectedEntries)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selectedEntries);

        var allDiff = BuildDiff(context);
        var selectedKeys = selectedEntries
            .Select(DiffIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedKeys.Count == 0)
            return 0;

        ExpandStructuralSelection(context, allDiff, selectedKeys);
        var selected = allDiff.Where(entry => selectedKeys.Contains(DiffIdentity(entry))).ToList();
        var cascadeColumns = BuildCascadeRevertColumns(context, selected);
        var reverted = RevertDiffs(context, selected);
        foreach (var entry in allDiff.Where(entry => !selectedKeys.Contains(DiffIdentity(entry))))
        {
            if (!cascadeColumns.TryGetValue(DiffIdentity(entry), out var columns))
                continue;
            if (entry.ChangeType != ResearchDiffChangeType.Modify)
            {
                if (RevertDiff(context, entry))
                    reverted++;
                continue;
            }
            if (RevertColumns(context, entry, columns))
                reverted++;
        }
        return reverted;
    }

    private static void ExpandStructuralSelection(
        ResearchWorkbookContext context,
        IReadOnlyCollection<ResearchDiffEntry> allDiff,
        HashSet<string> selectedKeys)
    {
        var original = context.OriginalSnapshot!;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var entry in allDiff.Where(item => selectedKeys.Contains(DiffIdentity(item))).ToList())
            {
                if (entry.ChangeType == ResearchDiffChangeType.Modify)
                    continue;
                if (Same(entry.Sheet, EffectSheetName))
                {
                    var effect = context.Effects.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity))
                                 ?? original.Effects.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
                    if (effect is not null && NotBlank(effect.LinkedNodeRowIdentity))
                    {
                        var nodeEntry = allDiff.FirstOrDefault(candidate =>
                            Same(candidate.Sheet, NodeSheetName)
                            && Same(candidate.RowIdentity, effect.LinkedNodeRowIdentity)
                            && candidate.ChangeType != ResearchDiffChangeType.Modify);
                        if (nodeEntry is not null)
                            changed |= selectedKeys.Add(DiffIdentity(nodeEntry));
                    }
                }
                else if (Same(entry.Sheet, NodeSheetName))
                {
                    var node = context.Nodes.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity))
                               ?? original.Nodes.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
                    if (node is null)
                        continue;
                    foreach (var effect in new[]
                             {
                                 context.Nodes.Contains(node) ? context.FindEffect(node) : null,
                                 original.Nodes.Contains(node) ? original.FindEffect(node) : null
                             }.Where(effect => effect is not null).Cast<ResearchEffectRow>())
                    {
                        var effectEntry = allDiff.FirstOrDefault(candidate =>
                            Same(candidate.Sheet, EffectSheetName)
                            && Same(candidate.RowIdentity, effect.RowIdentity)
                            && candidate.ChangeType != ResearchDiffChangeType.Modify);
                        if (effectEntry is not null)
                            changed |= selectedKeys.Add(DiffIdentity(effectEntry));
                    }
                    var category = context.FindCategory(node.Category) ?? original.FindCategory(node.Category);
                    if (category is not null)
                    {
                        var categoryEntry = allDiff.FirstOrDefault(candidate =>
                            Same(candidate.Sheet, CategorySheetName)
                            && Same(candidate.RowIdentity, category.RowIdentity)
                            && candidate.ChangeType != ResearchDiffChangeType.Modify);
                        if (categoryEntry is not null)
                            changed |= selectedKeys.Add(DiffIdentity(categoryEntry));
                    }
                }
            }
        }
    }

    private static Dictionary<string, HashSet<string>> BuildCascadeRevertColumns(
        ResearchWorkbookContext context,
        IReadOnlyCollection<ResearchDiffEntry> selected)
    {
        var original = context.OriginalSnapshot
            ?? throw new InvalidOperationException("원본 스냅샷이 없어 연쇄 변경을 되돌릴 수 없습니다.");
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void Add(string sheet, string rowIdentity, params string[] columns)
        {
            var key = $"{sheet}\u001f{rowIdentity}";
            if (!result.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                result[key] = set;
            }
            set.UnionWith(columns);
        }

        void AddDependentConditions(IEnumerable<string> nodeIds)
        {
            var ids = nodeIds.Where(NotBlank).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dependant in original.Nodes.Concat(context.Nodes)
                         .Where(node => node.Conditions.Any(ids.Contains)))
            {
                Add(NodeSheetName, dependant.RowIdentity,
                    "condition_node_1", "condition_node_2", "condition_node_3", "condition_node_4", "condition_node_5");
            }
        }

        foreach (var entry in selected)
        {
            if (Same(entry.Sheet, CategorySheetName))
            {
                var before = original.Categories.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
                var after = context.Categories.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
                var categoryIds = new[] { before?.Category, after?.Category }.Where(NotBlank).Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var nodes = original.Nodes.Concat(context.Nodes)
                    .Where(node => categoryIds.Contains(node.Category))
                    .GroupBy(node => node.RowIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                foreach (var node in nodes)
                {
                    Add(NodeSheetName, node.RowIdentity,
                        "id", "category", "category_name", "node_effect_desc", "nexus_effect_id");
                    var originalNode = original.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, node.RowIdentity));
                    var currentNode = context.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, node.RowIdentity));
                    foreach (var effect in new[]
                             {
                                 originalNode is null ? null : original.FindEffect(originalNode),
                                 currentNode is null ? null : context.FindEffect(currentNode)
                             }.Where(effect => effect is not null).Cast<ResearchEffectRow>())
                    {
                        Add(EffectSheetName, effect.RowIdentity, "column_1", "column_4", "column_5");
                    }
                }
                AddDependentConditions(nodes.Select(node => node.Id));
            }
            else if (Same(entry.Sheet, NodeSheetName))
            {
                var before = original.Nodes.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
                var after = context.Nodes.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
                AddDependentConditions(new[] { before?.Id, after?.Id }.Where(NotBlank).Cast<string>());
                foreach (var effect in new[]
                         {
                             before is null ? null : original.FindEffect(before),
                             after is null ? null : context.FindEffect(after)
                         }.Where(effect => effect is not null).Cast<ResearchEffectRow>())
                {
                    Add(EffectSheetName, effect.RowIdentity, "column_1", "column_5");
                }
            }
        }
        return result;
    }

    private static bool RevertColumns(
        ResearchWorkbookContext context,
        ResearchDiffEntry entry,
        IReadOnlySet<string> columns)
    {
        var original = context.OriginalSnapshot!;
        if (Same(entry.Sheet, NodeSheetName))
        {
            var current = context.Nodes.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
            var before = original.Nodes.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
            if (current is null || before is null)
                return false;
            if (columns.Contains("id")) current.Id = before.Id;
            if (columns.Contains("category")) current.Category = before.Category;
            if (columns.Contains("category_name")) current.CategoryName = before.CategoryName;
            if (columns.Contains("node_effect_desc")) current.NodeEffectDesc = before.NodeEffectDesc;
            if (columns.Contains("nexus_effect_id")) current.NexusEffectId = before.NexusEffectId;
            for (var index = 0; index < 5; index++)
            {
                if (columns.Contains($"condition_node_{index + 1}"))
                    current.SetCondition(index, before.Conditions[index]);
            }
            return true;
        }
        if (Same(entry.Sheet, EffectSheetName))
        {
            var current = context.Effects.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
            var before = original.Effects.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
            if (current is null || before is null)
                return false;
            for (var index = 0; index < EffectColumnCount; index++)
            {
                if (columns.Contains($"column_{index + 1}"))
                    current.SetCell(index, before.GetCell(index));
            }
            current.LinkedNodeRowIdentity = before.LinkedNodeRowIdentity;
            return true;
        }
        return false;
    }

    public static ResearchWorkbookContext CreateExportContext(
        ResearchWorkbookContext edited,
        IEnumerable<ResearchDiffEntry> selectedEntries)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(selectedEntries);

        var selected = selectedEntries
            .Select(DiffIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exportContext = edited.DeepClone();
        var uncheckedEntries = BuildDiff(exportContext)
            .Where(entry => !selected.Contains(DiffIdentity(entry)))
            .ToList();
        RevertDiffs(exportContext, uncheckedEntries);
        return exportContext;
    }

    public static ResearchWorkbookContext RebaseAfterExport(
        ResearchWorkbookContext historicalState,
        ResearchWorkbookContext savedReload,
        ResearchWorkbookContext exportedState,
        IEnumerable<ResearchDiffEntry> selectedEntries)
    {
        ArgumentNullException.ThrowIfNull(historicalState);
        ArgumentNullException.ThrowIfNull(savedReload);
        ArgumentNullException.ThrowIfNull(exportedState);
        ArgumentNullException.ThrowIfNull(selectedEntries);

        var baseline = savedReload.DeepClone(includeOriginalSnapshot: false);
        TransferRowIdentities(baseline, exportedState);
        AssociateNodeEffects(baseline);

        var selectedKeys = selectedEntries.Select(DiffIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingEntries = BuildDiff(historicalState)
            .Where(entry => !selectedKeys.Contains(DiffIdentity(entry)))
            .ToList();
        var rebased = baseline.DeepClone(includeOriginalSnapshot: false);
        foreach (var entry in pendingEntries)
            ApplyRowState(rebased, historicalState, entry);
        RefreshSequentialMetadata(rebased);
        AssociateNodeEffects(rebased);
        rebased.OriginalSnapshot = baseline.DeepClone(includeOriginalSnapshot: false);
        return rebased;
    }

    private static void TransferRowIdentities(
        ResearchWorkbookContext target,
        ResearchWorkbookContext reference)
    {
        TransferRowIdentities(target.Categories, reference.Categories,
            row => row.Category, row => row.RowIdentity, (row, identity) => row.RowIdentity = identity);
        TransferRowIdentities(target.Nodes, reference.Nodes,
            row => row.Id, row => row.RowIdentity, (row, identity) => row.RowIdentity = identity);
        TransferRowIdentities(target.Effects, reference.Effects,
            row => row.Id, row => row.RowIdentity, (row, identity) => row.RowIdentity = identity);
    }

    private static void TransferRowIdentities<T>(
        IEnumerable<T> targetRows,
        IEnumerable<T> referenceRows,
        Func<T, string> key,
        Func<T, string> identity,
        Action<T, string> assignIdentity)
    {
        var identitiesByKey = referenceRows
            .Where(row => NotBlank(key(row)))
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => identity(group.Single()), StringComparer.OrdinalIgnoreCase);
        foreach (var row in targetRows)
        {
            if (identitiesByKey.TryGetValue(key(row), out var rowIdentity))
                assignIdentity(row, rowIdentity);
        }
    }

    private static void ApplyRowState(
        ResearchWorkbookContext target,
        ResearchWorkbookContext source,
        ResearchDiffEntry entry)
    {
        switch (entry.Sheet)
        {
            case CategorySheetName:
                ApplyRowState(target.Categories, source.Categories, entry,
                    row => row.RowIdentity, row => row.DeepClone());
                break;
            case NodeSheetName:
                ApplyRowState(target.Nodes, source.Nodes, entry,
                    row => row.RowIdentity, row => row.DeepClone());
                break;
            case EffectSheetName:
                ApplyRowState(target.Effects, source.Effects, entry,
                    row => row.RowIdentity, row => row.DeepClone());
                break;
        }
    }

    private static void ApplyRowState<T>(
        List<T> targetRows,
        IReadOnlyCollection<T> sourceRows,
        ResearchDiffEntry entry,
        Func<T, string> identity,
        Func<T, T> clone)
    {
        var targetIndex = targetRows.FindIndex(row => Same(identity(row), entry.RowIdentity));
        var sourceRow = sourceRows.FirstOrDefault(row => Same(identity(row), entry.RowIdentity));
        if (sourceRow is null)
        {
            if (targetIndex >= 0)
                targetRows.RemoveAt(targetIndex);
            return;
        }
        if (targetIndex >= 0)
            targetRows[targetIndex] = clone(sourceRow);
        else
            targetRows.Add(clone(sourceRow));
    }

    private static void RefreshSequentialMetadata(ResearchWorkbookContext context)
    {
        for (var index = 0; index < context.Categories.Count; index++)
        {
            var row = context.Categories[index];
            row.SourceRowNumber = FirstDataRow + index;
            row.CategoryFormulaA1 = CategoryFormula(row.SourceRowNumber);
            row.NodeNameFormulaA1 = CategoryNodeNameFormula(row.SourceRowNumber);
        }
        for (var index = 0; index < context.Nodes.Count; index++)
        {
            var row = context.Nodes[index];
            row.SourceRowNumber = FirstDataRow + index;
            row.IdFormulaA1 = NodeIdFormula(row.SourceRowNumber);
            row.CategoryNameFormulaA1 = NodeCategoryNameFormula(row.SourceRowNumber);
            row.NodeEffectDescFormulaA1 = NodeEffectDescriptionFormula(row.SourceRowNumber);
            row.NexusEffectIdFormulaA1 = NodeEffectIdFormula(row.SourceRowNumber);
        }
    }

    public static int ApplyExportId(
        ResearchWorkbookContext context,
        IEnumerable<ResearchDiffEntry> entries,
        string exportId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entries);
        var sanitizedExportId = SanitizeText(exportId).Trim();
        if (sanitizedExportId.Length == 0)
            return 0;

        var identities = entries
            .Where(entry => entry.ChangeType != ResearchDiffChangeType.Delete)
            .Select(entry => entry.RowIdentity)
            .Where(NotBlank)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = 0;

        foreach (var category in context.Categories.Where(row => identities.Contains(row.RowIdentity)))
        {
            if (category.ExportId == sanitizedExportId)
                continue;
            category.ExportId = sanitizedExportId;
            changed++;
        }
        foreach (var node in context.Nodes.Where(row => identities.Contains(row.RowIdentity)))
        {
            if (node.ExportId == sanitizedExportId)
                continue;
            node.ExportId = sanitizedExportId;
            changed++;
        }
        foreach (var effect in context.Effects.Where(row => identities.Contains(row.RowIdentity)))
        {
            if (effect.ExportId == sanitizedExportId)
                continue;
            effect.ExportId = sanitizedExportId;
            changed++;
        }

        return changed;
    }

    public static ResearchCategoryRow CreateCategory(
        ResearchWorkbookContext context,
        string categoryKey,
        int index,
        string? exportId = null,
        string? helperPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = SanitizeIdentifierPart(categoryKey);
        if (key.Length == 0 || !IdentifierPattern.IsMatch(key))
            throw new ArgumentException("카테고리 key는 소문자 영문, 숫자, 밑줄만 사용할 수 있습니다.", nameof(categoryKey));

        var prefix = SanitizeIdentifierPart(helperPrefix);
        var generated = GenerateCategoryId(prefix, key);
        if (context.Categories.Any(row => Same(row.Category, generated)))
            throw new InvalidOperationException($"이미 존재하는 카테고리입니다: {generated}");
        if (context.Categories.Any(row => row.Index == index))
            throw new InvalidOperationException($"이미 사용 중인 category index입니다: {index}");

        var sourceRow = NextSourceRow(context.Categories.Select(row => row.SourceRowNumber));
        var row = new ResearchCategoryRow
        {
            RowIdentity = $"category:new:{Guid.NewGuid():N}",
            SourceRowNumber = sourceRow,
            Category = generated,
            ExportId = SanitizeText(exportId ?? MostCommonExportId(context.Categories.Select(item => item.ExportId))),
            HelperPrefix = prefix,
            CategoryKey = key,
            Index = index,
            NodeName = GenerateCategoryNodeName(key),
            CategoryFormulaA1 = CategoryFormula(sourceRow),
            NodeNameFormulaA1 = CategoryNodeNameFormula(sourceRow)
        };
        context.Categories.Add(row);
        return row;
    }

    public static ResearchMutationResult TryDeleteCategory(
        ResearchWorkbookContext context,
        string rowIdentity,
        bool deleteContainedNodes = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var category = context.Categories.FirstOrDefault(row => Same(row.RowIdentity, rowIdentity));
        if (category is null)
            return ResearchMutationResult.Failed("삭제할 카테고리를 찾지 못했습니다.");

        var contained = context.Nodes.Where(node => Same(node.Category, category.Category)).ToList();
        if (contained.Count > 0 && !deleteContainedNodes)
            return ResearchMutationResult.Failed($"카테고리에 연구 노드 {contained.Count}개가 남아 있습니다.");

        var result = new ResearchMutationResult { Success = true, Message = "카테고리를 삭제했습니다." };
        result.AffectedRowIdentities.Add(category.RowIdentity);
        if (contained.Count > 0)
        {
            var nodeIds = contained.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var linkedEffects = contained.Select(context.FindEffect)
                .Where(effect => effect is not null)
                .Cast<ResearchEffectRow>()
                .GroupBy(effect => effect.RowIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            foreach (var node in context.Nodes)
                RemoveConditionReferences(node, nodeIds);
            foreach (var node in contained)
                result.AffectedRowIdentities.Add(node.RowIdentity);
            foreach (var effect in linkedEffects)
            {
                result.AffectedRowIdentities.Add(effect.RowIdentity);
                context.Effects.Remove(effect);
            }
            context.Nodes.RemoveAll(node => nodeIds.Contains(node.Id));
        }
        context.Categories.Remove(category);
        return result;
    }

    public static (ResearchNodeRow Node, ResearchEffectRow Effect) CreateNode(
        ResearchWorkbookContext context,
        string category,
        int column,
        int row,
        string? exportId = null,
        string themeId = "s1")
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalizedCategory = SanitizeIdentifierPart(category);
        var normalizedTheme = SanitizeIdentifierPart(themeId);
        if (context.FindCategory(normalizedCategory) is null)
            throw new InvalidOperationException($"존재하지 않는 카테고리입니다: {normalizedCategory}");
        if (column <= 0 || row <= 0)
            throw new ArgumentOutOfRangeException(nameof(column), "column과 row는 1 이상이어야 합니다.");
        if (context.Nodes.Any(node => Same(node.Category, normalizedCategory) && node.Column == column && node.Row == row))
            throw new InvalidOperationException($"이미 사용 중인 좌표입니다: {normalizedCategory} ({column}, {row})");

        var sourceRow = NextSourceRow(context.Nodes.Select(node => node.SourceRowNumber));
        var effectiveExportId = SanitizeText(exportId ?? MostCommonExportId(context.Nodes.Select(node => node.ExportId)));
        var node = new ResearchNodeRow
        {
            RowIdentity = $"node:new:{Guid.NewGuid():N}",
            SourceRowNumber = sourceRow,
            ThemeId = normalizedTheme,
            Category = normalizedCategory,
            Column = column,
            Row = row,
            Id = GenerateNodeId(normalizedTheme, normalizedCategory, column, row),
            ExportId = effectiveExportId,
            CategoryName = $"s1_node_{normalizedCategory}_name",
            NodeEffectDesc = GenerateNodeEffectDescription(normalizedCategory),
            NexusEffectId = GenerateEffectId(normalizedTheme, normalizedCategory, column, row),
            NodePermission = 1,
            ActiveStep = 0,
            TotalRequiredHelper = 0,
            IdFormulaA1 = NodeIdFormula(sourceRow),
            CategoryNameFormulaA1 = NodeCategoryNameFormula(sourceRow),
            NodeEffectDescFormulaA1 = NodeEffectDescriptionFormula(sourceRow),
            NexusEffectIdFormulaA1 = NodeEffectIdFormula(sourceRow)
        };

        var effect = new ResearchEffectRow
        {
            RowIdentity = $"effect:new:{Guid.NewGuid():N}",
            SourceRowNumber = 0,
            LinkedNodeRowIdentity = node.RowIdentity
        };
        effect.Id = node.NexusEffectId;
        effect.ExportId = effectiveExportId;
        effect.GroupMemo = $"연구소:{normalizedCategory}";
        effect.Memo = $"연구 노드 효과 (연구소/{normalizedCategory}/tier 1/node {column},{row})";

        context.Nodes.Add(node);
        context.Effects.Add(effect);
        return (node, effect);
    }

    public static ResearchMutationResult TryDeleteNode(
        ResearchWorkbookContext context,
        string rowIdentity,
        bool removeConditionReferences = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var node = context.Nodes.FirstOrDefault(row => Same(row.RowIdentity, rowIdentity));
        if (node is null)
            return ResearchMutationResult.Failed("삭제할 연구 노드를 찾지 못했습니다.");

        var dependants = context.Nodes
            .Where(candidate => candidate.Conditions.Any(condition => Same(condition, node.Id)))
            .ToList();
        if (dependants.Count > 0 && !removeConditionReferences)
            return ResearchMutationResult.Failed($"후행 연구 노드 {dependants.Count}개가 이 노드를 참조하고 있습니다.");

        var result = new ResearchMutationResult { Success = true, Message = "연구 노드와 대응 효과를 삭제했습니다." };
        result.AffectedRowIdentities.Add(node.RowIdentity);
        foreach (var dependant in dependants)
        {
            RemoveConditionReferences(dependant, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.Id });
            result.AffectedRowIdentities.Add(dependant.RowIdentity);
        }

        var linkedEffect = context.FindEffect(node);
        if (linkedEffect is not null)
        {
            result.AffectedRowIdentities.Add(linkedEffect.RowIdentity);
            context.Effects.Remove(linkedEffect);
        }
        context.Nodes.Remove(node);
        return result;
    }

    public static ResearchMutationResult TryRenameCategory(
        ResearchWorkbookContext context,
        string categoryRowIdentity,
        string newCategoryKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = SanitizeIdentifierPart(newCategoryKey);
        if (key.Length == 0 || !IdentifierPattern.IsMatch(key))
            return ResearchMutationResult.Failed("카테고리 key는 소문자 영문, 숫자, 밑줄만 사용할 수 있습니다.");

        var candidate = context.DeepClone(includeOriginalSnapshot: false);
        var category = candidate.Categories.FirstOrDefault(row => Same(row.RowIdentity, categoryRowIdentity));
        if (category is null)
            return ResearchMutationResult.Failed("변경할 카테고리를 찾지 못했습니다.");

        var oldCategory = category.Category;
        var generatedCategory = GenerateCategoryId(category.HelperPrefix, key);
        if (candidate.Categories.Any(row => !Same(row.RowIdentity, category.RowIdentity) && Same(row.Category, generatedCategory)))
            return ResearchMutationResult.Failed($"이미 존재하는 카테고리입니다: {generatedCategory}");

        category.CategoryKey = key;
        category.Category = generatedCategory;
        category.NodeName = GenerateCategoryNodeName(key);

        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { category.RowIdentity };
        foreach (var node in candidate.Nodes.Where(node => Same(node.Category, oldCategory)))
        {
            var oldId = node.Id;
            var linkedEffect = candidate.FindEffect(node);
            node.Category = generatedCategory;
            node.Id = GenerateNodeId(node.ThemeId, generatedCategory, node.Column, node.Row);
            node.CategoryName = $"s1_node_{generatedCategory}_name";
            node.NodeEffectDesc = GenerateNodeEffectDescription(generatedCategory);
            node.NexusEffectId = GenerateEffectId(node.ThemeId, generatedCategory, node.Column, node.Row);
            idMap[oldId] = node.Id;
            affected.Add(node.RowIdentity);
            if (linkedEffect is not null)
            {
                linkedEffect.Id = node.NexusEffectId;
                linkedEffect.LinkedNodeRowIdentity = node.RowIdentity;
                linkedEffect.GroupMemo = ReplaceOrdinalIgnoreCase(linkedEffect.GroupMemo, $"연구소:{oldCategory}", $"연구소:{generatedCategory}");
                linkedEffect.Memo = ReplaceOrdinalIgnoreCase(linkedEffect.Memo, $"/{oldCategory}/", $"/{generatedCategory}/");
                affected.Add(linkedEffect.RowIdentity);
            }
        }

        CascadeConditionIds(candidate.Nodes, idMap, affected);

        if (HasDuplicateGeneratedKeys(candidate, out var duplicateMessage))
            return ResearchMutationResult.Failed(duplicateMessage);

        context.ReplaceDataFrom(candidate);
        var result = new ResearchMutationResult { Success = true, Message = $"카테고리를 {generatedCategory}(으)로 변경했습니다." };
        result.AffectedRowIdentities.AddRange(affected);
        return result;
    }

    public static ResearchMutationResult TryMoveNode(
        ResearchWorkbookContext context,
        string nodeRowIdentity,
        int newColumn,
        int newRow)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (newColumn <= 0 || newRow <= 0)
            return ResearchMutationResult.Failed("column과 row는 1 이상이어야 합니다.");

        var candidate = context.DeepClone(includeOriginalSnapshot: false);
        var node = candidate.Nodes.FirstOrDefault(row => Same(row.RowIdentity, nodeRowIdentity));
        if (node is null)
            return ResearchMutationResult.Failed("이동할 연구 노드를 찾지 못했습니다.");
        if (candidate.Nodes.Any(row => !Same(row.RowIdentity, node.RowIdentity)
                                       && Same(row.Category, node.Category)
                                       && row.Column == newColumn
                                       && row.Row == newRow))
        {
            return ResearchMutationResult.Failed($"이미 사용 중인 좌표입니다: {node.Category} ({newColumn}, {newRow})");
        }

        var oldId = node.Id;
        var linkedEffect = candidate.FindEffect(node);
        node.Column = newColumn;
        node.Row = newRow;
        node.Id = GenerateNodeId(node.ThemeId, node.Category, newColumn, newRow);
        node.NexusEffectId = GenerateEffectId(node.ThemeId, node.Category, newColumn, newRow);

        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.RowIdentity };
        CascadeConditionIds(candidate.Nodes,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldId] = node.Id }, affected);
        if (linkedEffect is not null)
        {
            linkedEffect.Id = node.NexusEffectId;
            linkedEffect.LinkedNodeRowIdentity = node.RowIdentity;
            linkedEffect.Memo = EffectMemoCoordinatePattern.Replace(linkedEffect.Memo, $"${{prefix}}{newColumn},{newRow}");
            affected.Add(linkedEffect.RowIdentity);
        }

        if (HasDuplicateGeneratedKeys(candidate, out var duplicateMessage))
            return ResearchMutationResult.Failed(duplicateMessage);

        context.ReplaceDataFrom(candidate);
        var result = new ResearchMutationResult { Success = true, Message = $"노드를 ({newColumn}, {newRow})(으)로 이동했습니다." };
        result.AffectedRowIdentities.AddRange(affected);
        return result;
    }

    public static ResearchMutationResult TryChangeNodeCategory(
        ResearchWorkbookContext context,
        string nodeRowIdentity,
        string newCategory)
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalizedCategory = SanitizeIdentifierPart(newCategory);
        if (normalizedCategory.Length == 0 || !IdentifierPattern.IsMatch(normalizedCategory))
            return ResearchMutationResult.Failed("category는 소문자 영문, 숫자, 밑줄만 사용할 수 있습니다.");
        if (context.FindCategory(normalizedCategory) is null)
            return ResearchMutationResult.Failed($"존재하지 않는 카테고리입니다: {normalizedCategory}");

        var candidate = context.DeepClone(includeOriginalSnapshot: false);
        var node = candidate.Nodes.FirstOrDefault(row => Same(row.RowIdentity, nodeRowIdentity));
        if (node is null)
            return ResearchMutationResult.Failed("카테고리를 바꿀 연구 노드를 찾지 못했습니다.");
        if (Same(node.Category, normalizedCategory))
            return new ResearchMutationResult { Success = true, Message = "노드 카테고리가 바뀌지 않았습니다." };
        if (candidate.Nodes.Any(row => !Same(row.RowIdentity, node.RowIdentity)
                                       && Same(row.Category, normalizedCategory)
                                       && row.Column == node.Column
                                       && row.Row == node.Row))
        {
            return ResearchMutationResult.Failed(
                $"대상 카테고리에 이미 사용 중인 좌표가 있습니다: {normalizedCategory} ({node.Column}, {node.Row})");
        }

        var oldCategory = node.Category;
        var oldId = node.Id;
        var linkedEffect = candidate.FindEffect(node);
        node.Category = normalizedCategory;
        node.Id = GenerateNodeId(node.ThemeId, normalizedCategory, node.Column, node.Row);
        node.CategoryName = $"s1_node_{normalizedCategory}_name";
        node.NodeEffectDesc = GenerateNodeEffectDescription(normalizedCategory);
        node.NexusEffectId = GenerateEffectId(node.ThemeId, normalizedCategory, node.Column, node.Row);

        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.RowIdentity };
        CascadeConditionIds(candidate.Nodes,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldId] = node.Id }, affected);
        if (linkedEffect is not null)
        {
            linkedEffect.Id = node.NexusEffectId;
            linkedEffect.LinkedNodeRowIdentity = node.RowIdentity;
            linkedEffect.GroupMemo = ReplaceOrdinalIgnoreCase(
                linkedEffect.GroupMemo, $"연구소:{oldCategory}", $"연구소:{normalizedCategory}");
            linkedEffect.Memo = ReplaceOrdinalIgnoreCase(
                linkedEffect.Memo, $"/{oldCategory}/", $"/{normalizedCategory}/");
            affected.Add(linkedEffect.RowIdentity);
        }

        if (HasDuplicateGeneratedKeys(candidate, out var duplicateMessage))
            return ResearchMutationResult.Failed(duplicateMessage);

        context.ReplaceDataFrom(candidate);
        var result = new ResearchMutationResult
        {
            Success = true,
            Message = $"노드 카테고리를 {normalizedCategory}(으)로 변경했습니다."
        };
        result.AffectedRowIdentities.AddRange(affected);
        return result;
    }

    public static ResearchMutationResult TryMoveNodes(
        ResearchWorkbookContext context,
        IEnumerable<string> nodeRowIdentities,
        int deltaColumn,
        int deltaRow)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nodeRowIdentities);

        var identities = nodeRowIdentities
            .Where(NotBlank)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (identities.Count == 0)
            return ResearchMutationResult.Failed("이동할 연구 노드를 선택하세요.");
        if (deltaColumn == 0 && deltaRow == 0)
            return new ResearchMutationResult { Success = true, Message = "노드 위치가 바뀌지 않았습니다." };

        var candidate = context.DeepClone(includeOriginalSnapshot: false);
        var selected = candidate.Nodes.Where(node => identities.Contains(node.RowIdentity)).ToList();
        if (selected.Count != identities.Count)
            return ResearchMutationResult.Failed("이동할 연구 노드 일부를 찾지 못했습니다.");

        var targets = selected.Select(node => new
        {
            Node = node,
            Column = node.Column + deltaColumn,
            Row = node.Row + deltaRow
        }).ToList();
        if (targets.Any(target => target.Column <= 0 || target.Row <= 0))
            return ResearchMutationResult.Failed("column과 row는 1 이상이어야 합니다.");

        var duplicateTarget = targets
            .GroupBy(target => $"{target.Node.Category}\u001f{target.Column}\u001f{target.Row}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget is not null)
            return ResearchMutationResult.Failed("선택한 노드들의 이동 좌표가 서로 겹칩니다.");

        foreach (var target in targets)
        {
            if (candidate.Nodes.Any(node => !identities.Contains(node.RowIdentity)
                                            && Same(node.Category, target.Node.Category)
                                            && node.Column == target.Column
                                            && node.Row == target.Row))
            {
                return ResearchMutationResult.Failed(
                    $"이미 사용 중인 좌표입니다: {target.Node.Category} ({target.Column}, {target.Row})");
            }
        }

        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var effectUpdates = new Dictionary<string, (string NewId, int Column, int Row)>(StringComparer.OrdinalIgnoreCase);
        var affected = new HashSet<string>(identities, StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var oldId = target.Node.Id;
            var linkedEffect = candidate.FindEffect(target.Node);
            target.Node.Column = target.Column;
            target.Node.Row = target.Row;
            target.Node.Id = GenerateNodeId(target.Node.ThemeId, target.Node.Category, target.Column, target.Row);
            target.Node.NexusEffectId = GenerateEffectId(target.Node.ThemeId, target.Node.Category, target.Column, target.Row);
            idMap[oldId] = target.Node.Id;
            if (linkedEffect is not null)
                effectUpdates[linkedEffect.RowIdentity] = (target.Node.NexusEffectId, target.Column, target.Row);
        }

        CascadeConditionIds(candidate.Nodes, idMap, affected);
        foreach (var effect in candidate.Effects)
        {
            if (!effectUpdates.TryGetValue(effect.RowIdentity, out var replacement))
                continue;
            effect.Id = replacement.NewId;
            effect.LinkedNodeRowIdentity = candidate.Nodes.First(node =>
                Same(node.NexusEffectId, replacement.NewId)).RowIdentity;
            effect.Memo = EffectMemoCoordinatePattern.Replace(effect.Memo,
                $"${{prefix}}{replacement.Column},{replacement.Row}");
            affected.Add(effect.RowIdentity);
        }

        if (HasDuplicateGeneratedKeys(candidate, out var duplicateMessage))
            return ResearchMutationResult.Failed(duplicateMessage);

        context.ReplaceDataFrom(candidate);
        var result = new ResearchMutationResult
        {
            Success = true,
            Message = $"연구 노드 {selected.Count:N0}개를 이동했습니다."
        };
        result.AffectedRowIdentities.AddRange(affected);
        return result;
    }

    public static ResearchMutationResult TryConnectCondition(
        ResearchWorkbookContext context,
        string prerequisiteNodeIdentity,
        string targetNodeIdentity)
    {
        ArgumentNullException.ThrowIfNull(context);
        var prerequisite = context.Nodes.FirstOrDefault(row => Same(row.RowIdentity, prerequisiteNodeIdentity));
        var target = context.Nodes.FirstOrDefault(row => Same(row.RowIdentity, targetNodeIdentity));
        if (prerequisite is null || target is null)
            return ResearchMutationResult.Failed("연결할 연구 노드를 찾지 못했습니다.");
        if (Same(prerequisite.Id, target.Id))
            return ResearchMutationResult.Failed("자기 자신을 선행 조건으로 연결할 수 없습니다.");
        if (!Same(prerequisite.Category, target.Category))
            return ResearchMutationResult.Failed("다른 카테고리의 연구 노드는 연결할 수 없습니다.");
        if (prerequisite.Column >= target.Column)
            return ResearchMutationResult.Failed("선행 조건은 대상보다 앞 column에 있어야 합니다.");
        if (prerequisite.Column + 1 != target.Column)
            return ResearchMutationResult.Failed("바로 다음 column의 연구 노드에만 연결할 수 있습니다.");
        if (target.Conditions.Any(condition => Same(condition, prerequisite.Id)))
            return ResearchMutationResult.Failed("이미 연결된 선행 조건입니다.");

        var outgoingCount = context.Nodes.Count(candidate =>
            Same(candidate.Category, prerequisite.Category)
            && candidate.Conditions.Any(condition => Same(condition, prerequisite.Id)));
        if (outgoingCount >= 3)
            return ResearchMutationResult.Failed("한 연구 노드에서는 최대 3개의 다음 노드로만 연결할 수 있습니다.");

        var incomingCount = target.Conditions.Count(NotBlank);
        if (incomingCount >= 3)
            return ResearchMutationResult.Failed("한 연구 노드에는 최대 3개의 선행 노드만 연결할 수 있습니다.");

        var index = target.Conditions.ToList().FindIndex(string.IsNullOrWhiteSpace);
        if (index < 0)
            return ResearchMutationResult.Failed("선행 조건은 최대 5개까지 연결할 수 있습니다.");

        var undo = target.Conditions[index];
        target.SetCondition(index, prerequisite.Id);
        if (HasCycle(context.Nodes))
        {
            target.SetCondition(index, undo);
            return ResearchMutationResult.Failed("이 연결은 순환 구조를 만듭니다.");
        }

        var warnings = new List<string>();
        if (outgoingCount + 1 == 3)
            warnings.Add($"{prerequisite.Id}: 1-3 분기입니다. 현재 시스템 기준은 1-2-1이므로 구조를 확인하세요.");
        if (incomingCount + 1 == 3)
            warnings.Add($"{target.Id}: 3-1 합류입니다. 현재 시스템 기준은 1-2-1이므로 구조를 확인하세요.");
        var result = new ResearchMutationResult
        {
            Success = true,
            Message = "선행 조건을 연결했습니다.",
            WarningMessage = string.Join(" ", warnings.Distinct(StringComparer.OrdinalIgnoreCase))
        };
        result.AffectedRowIdentities.Add(target.RowIdentity);
        return result;
    }

    public static ResearchMutationResult TrySetColumnNodeCount(
        ResearchWorkbookContext context,
        string category,
        int column,
        int desiredCount)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (column <= 0)
            return ResearchMutationResult.Failed("column은 1 이상이어야 합니다.");
        if (desiredCount is < 1 or > 3)
            return ResearchMutationResult.Failed("한 단계에는 연구 노드를 1~3개만 배치할 수 있습니다.");

        var candidate = context.DeepClone(includeOriginalSnapshot: false);
        var categoryRow = candidate.FindCategory(category);
        if (categoryRow is null)
            return ResearchMutationResult.Failed($"연구 카테고리를 찾지 못했습니다: {category}");

        var existing = candidate.Nodes
            .Where(node => Same(node.Category, categoryRow.Category) && node.Column == column)
            .OrderBy(node => node.Row)
            .ToList();
        if (existing.Count == 0)
            return ResearchMutationResult.Failed($"STEP {column}에 기준이 될 연구 노드가 없습니다.");

        var desiredRows = desiredCount switch
        {
            1 => new[] { 4 },
            2 => new[] { 2, 6 },
            _ => new[] { 2, 4, 6 }
        };
        if (existing.Count == desiredCount
            && existing.Select(node => node.Row).SequenceEqual(desiredRows))
        {
            return new ResearchMutationResult
            {
                Success = true,
                Message = $"STEP {column}의 노드 수가 이미 {desiredCount}개입니다."
            };
        }

        var templateNode = existing
            .OrderBy(node => Math.Abs(node.Row - 4))
            .ThenBy(node => node.Row)
            .First()
            .DeepClone();
        var templateEffect = candidate.FindEffect(existing.First(node => Same(node.RowIdentity, templateNode.RowIdentity)))?.DeepClone();
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var survivors = new List<ResearchNodeRow>();
        foreach (var row in desiredRows)
        {
            var exact = existing.FirstOrDefault(node => node.Row == row && !survivors.Contains(node));
            if (exact is not null)
                survivors.Add(exact);
        }
        foreach (var node in existing)
        {
            if (survivors.Count >= desiredCount)
                break;
            if (!survivors.Contains(node))
                survivors.Add(node);
        }

        foreach (var node in existing.Where(node => !survivors.Contains(node)).ToList())
        {
            var deletion = TryDeleteNode(candidate, node.RowIdentity, removeConditionReferences: true);
            if (!deletion.Success)
                return deletion;
            foreach (var identity in deletion.AffectedRowIdentities)
                affected.Add(identity);
        }

        var occupiedRows = survivors.Select(node => node.Row).ToHashSet();
        var missingRows = desiredRows.Where(row => !occupiedRows.Contains(row)).ToList();
        var movable = survivors.Where(node => !desiredRows.Contains(node.Row)).ToList();
        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Math.Min(movable.Count, missingRows.Count); index++)
        {
            var node = movable[index];
            var oldId = node.Id;
            var newRow = missingRows[index];
            node.Row = newRow;
            node.Id = GenerateNodeId(node.ThemeId, node.Category, node.Column, newRow);
            node.NexusEffectId = GenerateEffectId(node.ThemeId, node.Category, node.Column, newRow);
            idMap[oldId] = node.Id;
            affected.Add(node.RowIdentity);
            if (candidate.FindEffect(node) is { } effect)
            {
                effect.Id = node.NexusEffectId;
                effect.LinkedNodeRowIdentity = node.RowIdentity;
                effect.Memo = EffectMemoCoordinatePattern.Replace(effect.Memo, $"${{prefix}}{node.Column},{newRow}");
                affected.Add(effect.RowIdentity);
            }
        }
        if (idMap.Count > 0)
            CascadeConditionIds(candidate.Nodes, idMap, affected);

        var remainingRows = desiredRows
            .Where(row => !candidate.Nodes.Any(node => Same(node.Category, categoryRow.Category)
                                                       && node.Column == column
                                                       && node.Row == row))
            .ToList();
        foreach (var row in remainingRows)
        {
            var created = CreateNode(candidate, categoryRow.Category, column, row, templateNode.ExportId, templateNode.ThemeId);
            CopyNodePayload(templateNode, created.Node);
            created.Node.Column = column;
            created.Node.Row = row;
            created.Node.Id = GenerateNodeId(created.Node.ThemeId, created.Node.Category, column, row);
            created.Node.NexusEffectId = GenerateEffectId(created.Node.ThemeId, created.Node.Category, column, row);
            ClearNodeConditions(created.Node);
            if (templateEffect is not null)
                CopyEffectPayload(templateEffect, created.Effect, created.Node);
            affected.Add(created.Node.RowIdentity);
            affected.Add(created.Effect.RowIdentity);
        }

        RebuildCanonicalBoundary(candidate, categoryRow.Category, column, affected);
        RebuildCanonicalBoundary(candidate, categoryRow.Category, column + 1, affected);

        if (HasDuplicateGeneratedKeys(candidate, out var duplicateMessage))
            return ResearchMutationResult.Failed(duplicateMessage);

        context.ReplaceDataFrom(candidate);
        var result = new ResearchMutationResult
        {
            Success = true,
            Message = $"STEP {column}을 연구 노드 {desiredCount}개 구조로 변경했습니다."
        };
        result.AffectedRowIdentities.AddRange(affected);
        return result;
    }

    private static void RebuildCanonicalBoundary(
        ResearchWorkbookContext context,
        string category,
        int targetColumn,
        ISet<string> affected)
    {
        if (targetColumn <= 1)
            return;
        var sources = context.Nodes
            .Where(node => Same(node.Category, category) && node.Column == targetColumn - 1)
            .OrderBy(node => node.Row)
            .ToList();
        var targets = context.Nodes
            .Where(node => Same(node.Category, category) && node.Column == targetColumn)
            .OrderBy(node => node.Row)
            .ToList();
        if (sources.Count == 0 || targets.Count == 0)
            return;

        var edges = new HashSet<(string SourceIdentity, string TargetIdentity)>();
        foreach (var target in targets)
        {
            var source = sources
                .OrderBy(candidate => Math.Abs(candidate.Row - target.Row))
                .ThenBy(candidate => candidate.Row)
                .First();
            edges.Add((source.RowIdentity, target.RowIdentity));
        }
        foreach (var source in sources)
        {
            if (edges.Any(edge => Same(edge.SourceIdentity, source.RowIdentity)))
                continue;
            var target = targets
                .OrderBy(candidate => Math.Abs(candidate.Row - source.Row))
                .ThenBy(candidate => edges.Count(edge => Same(edge.TargetIdentity, candidate.RowIdentity)))
                .ThenBy(candidate => candidate.Row)
                .First();
            edges.Add((source.RowIdentity, target.RowIdentity));
        }

        var sourceByIdentity = sources.ToDictionary(node => node.RowIdentity, StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            ClearNodeConditions(target);
            var prerequisiteIds = edges
                .Where(edge => Same(edge.TargetIdentity, target.RowIdentity))
                .Select(edge => sourceByIdentity[edge.SourceIdentity].Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
            for (var index = 0; index < prerequisiteIds.Count; index++)
                target.SetCondition(index, prerequisiteIds[index]);
            affected.Add(target.RowIdentity);
        }
    }

    private static void ClearNodeConditions(ResearchNodeRow node)
    {
        for (var index = 0; index < 5; index++)
            node.SetCondition(index, "");
    }

    private static void CopyNodePayload(ResearchNodeRow source, ResearchNodeRow target)
    {
        target.ExportId = source.ExportId;
        target.ThemeId = source.ThemeId;
        target.Image = source.Image;
        target.NodePermission = source.NodePermission;
        target.ActiveItemId = source.ActiveItemId;
        target.ActiveItemValue = source.ActiveItemValue;
        target.TotalRequiredHelper = source.TotalRequiredHelper;
        target.ActiveStep = source.ActiveStep;
    }

    private static void CopyEffectPayload(ResearchEffectRow source, ResearchEffectRow target, ResearchNodeRow node)
    {
        target.Id = node.NexusEffectId;
        target.ExportId = source.ExportId;
        target.ParentEffect = source.ParentEffect;
        target.GroupMemo = source.GroupMemo;
        target.Memo = EffectMemoCoordinatePattern.Replace(source.Memo, $"${{prefix}}{node.Column},{node.Row}");
        target.Type = source.Type;
        target.Condition = source.Condition;
        target.ValueCell = source.ValueCell.DeepClone();
        target.TestCell = source.TestCell.DeepClone();
        target.ExtraCell = source.ExtraCell.DeepClone();
        target.LinkedNodeRowIdentity = node.RowIdentity;
    }

    public static bool DisconnectCondition(ResearchNodeRow target, string prerequisiteNodeId)
    {
        ArgumentNullException.ThrowIfNull(target);
        var remaining = target.Conditions.Where(condition => NotBlank(condition) && !Same(condition, prerequisiteNodeId)).ToList();
        if (remaining.Count == target.Conditions.Count(NotBlank))
            return false;
        for (var index = 0; index < 5; index++)
            target.SetCondition(index, index < remaining.Count ? remaining[index] : "");
        return true;
    }

    public static ResearchSaveResult SaveAtomic(
        ResearchWorkbookContext context,
        ResearchSaveOptions? options = null) =>
        SaveAs(context, context.SourceOutSystemPath, context.SourceEffectPath, options);

    public static ResearchSaveResult SaveAs(
        ResearchWorkbookContext context,
        string outSystemOutputPath,
        string effectOutputPath,
        ResearchSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(outSystemOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectOutputPath);
        if (context.OriginalSnapshot is null)
            throw new InvalidOperationException("Load로 생성한 ResearchWorkbookContext만 저장할 수 있습니다.");
        if (!File.Exists(context.SourceOutSystemPath))
            throw new FileNotFoundException("원본 아웃시스템 워크북을 찾을 수 없습니다.", context.SourceOutSystemPath);
        if (!File.Exists(context.SourceEffectPath))
            throw new FileNotFoundException("원본 효과 워크북을 찾을 수 없습니다.", context.SourceEffectPath);

        options ??= new ResearchSaveOptions();
        var working = context.DeepClone();
        Sanitize(working);
        var initialDiff = BuildDiff(working);
        if (!string.IsNullOrWhiteSpace(options.ChangedRowsExportId))
        {
            var selected = options.ExportRowIdentities is null
                ? initialDiff
                : initialDiff.Where(entry => options.ExportRowIdentities.Contains(entry.RowIdentity, StringComparer.OrdinalIgnoreCase)).ToList();
            ApplyExportId(working, selected, options.ChangedRowsExportId!);
        }

        var validation = Validate(working);
        var baselineValidation = Validate(working.OriginalSnapshot!);
        var newValidationErrors = FindNewValidationErrors(validation, baselineValidation, useRowIdentity: true);
        if (options.ValidateBeforeSave && newValidationErrors.Count > 0)
            throw new ResearchWorkbookValidationException(newValidationErrors);

        var outPath = Path.GetFullPath(outSystemOutputPath);
        var effectPath = Path.GetFullPath(effectOutputPath);
        if (SamePath(outPath, effectPath))
            throw new ArgumentException("아웃시스템과 효과 워크북의 저장 경로가 같을 수 없습니다.");
        if (SamePath(outPath, context.SourceOutSystemPath))
            EnsureSourceUnchanged(context.SourceOutSystemPath, context.SourceOutSystemSignature, "아웃시스템 DB");
        if (SamePath(effectPath, context.SourceEffectPath))
            EnsureSourceUnchanged(context.SourceEffectPath, context.SourceEffectSignature, "효과 DB");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? throw new InvalidOperationException("아웃시스템 출력 폴더가 없습니다."));
        Directory.CreateDirectory(Path.GetDirectoryName(effectPath) ?? throw new InvalidOperationException("효과 출력 폴더가 없습니다."));

        var outTemp = TemporaryWorkbookPath(outPath);
        var effectTemp = TemporaryWorkbookPath(effectPath);
        try
        {
            WriteOutSystemWorkbook(working, outTemp);
            WriteEffectWorkbook(working, effectTemp);
            VerifySavedPair(working, outTemp, effectTemp, options.ValidateBeforeSave);

            var commit = CommitWorkbookPair(outTemp, outPath, effectTemp, effectPath, options.CreateBackup);
            var result = new ResearchSaveResult
            {
                OutSystemPath = outPath,
                EffectPath = effectPath,
                OutSystemBackupPath = commit.OutBackup,
                EffectBackupPath = commit.EffectBackup,
                ChangedRowCount = initialDiff.Count
            };
            result.ValidationIssues.AddRange(validation);
            result.NewValidationIssues.AddRange(newValidationErrors);
            return result;
        }
        finally
        {
            TryDelete(outTemp);
            TryDelete(effectTemp);
        }
    }

    public static ResearchRoundTripResult TestRoundTrip(
        ResearchWorkbookContext context,
        string? workingDirectory = null,
        bool keepArtifacts = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ownsDirectory = string.IsNullOrWhiteSpace(workingDirectory);
        var root = ownsDirectory
            ? Path.Combine(Path.GetTempPath(), "NexusEditor", "ResearchRoundTrip", Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(workingDirectory!);
        var firstDirectory = Path.Combine(root, "pass1");
        var secondDirectory = Path.Combine(root, "pass2");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);

        var firstOut = Path.Combine(firstDirectory, Path.GetFileName(context.SourceOutSystemPath));
        var firstEffect = Path.Combine(firstDirectory, Path.GetFileName(context.SourceEffectPath));
        var secondOut = Path.Combine(secondDirectory, Path.GetFileName(context.SourceOutSystemPath));
        var secondEffect = Path.Combine(secondDirectory, Path.GetFileName(context.SourceEffectPath));
        var result = new ResearchRoundTripResult();

        try
        {
            var baselineValidation = Validate(context);
            result.BaselineValidationErrorCount = baselineValidation.Count(issue => issue.Severity == ResearchValidationSeverity.Error);
            SaveAs(context, firstOut, firstEffect, new ResearchSaveOptions { CreateBackup = false });
            var firstReload = Load(firstOut, firstEffect);
            result.FirstPassDifferences.AddRange(ComparePersistedData(context, firstReload));

            SaveAs(firstReload, secondOut, secondEffect, new ResearchSaveOptions { CreateBackup = false });
            var secondReload = Load(secondOut, secondEffect);
            result.SecondPassDifferences.AddRange(ComparePersistedData(firstReload, secondReload));
            var reloadValidation = Validate(secondReload);
            result.ReloadValidationErrorCount = reloadValidation.Count(issue => issue.Severity == ResearchValidationSeverity.Error);
            result.ValidationIssues.AddRange(reloadValidation);
            result.NewValidationIssues.AddRange(
                FindNewValidationErrors(reloadValidation, baselineValidation, useRowIdentity: false));
            return result;
        }
        finally
        {
            if (!keepArtifacts && ownsDirectory)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                    // QA artifacts are best-effort cleanup only.
                }
            }
        }
    }

    private static void WriteOutSystemWorkbook(ResearchWorkbookContext context, string outputPath)
    {
        using (var workbook = OpenWorkbookSnapshot(context.SourceOutSystemPath))
        {
            if (!workbook.Worksheets.TryGetWorksheet(CategorySheetName, out var categorySheet))
                throw new InvalidDataException($"{CategorySheetName} 시트를 찾을 수 없습니다.");
            if (!workbook.Worksheets.TryGetWorksheet(NodeSheetName, out var nodeSheet))
                throw new InvalidDataException($"{NodeSheetName} 시트를 찾을 수 없습니다.");

            RewriteCategories(categorySheet, context.Categories);
            RewriteNodes(nodeSheet, context.Nodes);
            workbook.SaveAs(outputPath);
        }
        WriteFormulaCaches(outputPath, context);
    }

    private static void WriteEffectWorkbook(ResearchWorkbookContext context, string outputPath)
    {
        using var workbook = OpenWorkbookSnapshot(context.SourceEffectPath);
        if (!workbook.Worksheets.TryGetWorksheet(EffectSheetName, out var effectSheet))
            throw new InvalidDataException($"{EffectSheetName} 시트를 찾을 수 없습니다.");

        RewriteResearchEffects(effectSheet, context);
        workbook.SaveAs(outputPath);
    }

    private static void RewriteCategories(IXLWorksheet sheet, IReadOnlyList<ResearchCategoryRow> categories)
    {
        var originalLastRow = LastRow(sheet);
        var templateRow = Math.Max(FirstDataRow, Math.Min(originalLastRow, FirstDataRow));
        EnsureSequentialCapacity(sheet, categories.Count, CategoryColumnCount, originalLastRow, templateRow);

        for (var index = 0; index < categories.Count; index++)
        {
            var row = FirstDataRow + index;
            var category = categories[index];
            SetFormula(sheet.Cell(row, 1), CategoryFormula(row));
            SetText(sheet.Cell(row, 2), category.ExportId);
            SetText(sheet.Cell(row, 3), category.HelperPrefix);
            SetText(sheet.Cell(row, 4), category.CategoryKey);
            SetNullableInt(sheet.Cell(row, 5), category.Index);
            SetFormula(sheet.Cell(row, 6), CategoryNodeNameFormula(row));
        }

        DeleteTrailingRows(sheet, FirstDataRow + categories.Count, originalLastRow);
    }

    private static void RewriteNodes(IXLWorksheet sheet, IReadOnlyList<ResearchNodeRow> nodes)
    {
        var originalLastRow = LastRow(sheet);
        var templateRow = Math.Max(FirstDataRow, Math.Min(originalLastRow, FirstDataRow));
        EnsureSequentialCapacity(sheet, nodes.Count, NodeColumnCount, originalLastRow, templateRow);

        for (var index = 0; index < nodes.Count; index++)
        {
            var row = FirstDataRow + index;
            var node = nodes[index];
            SetFormula(sheet.Cell(row, 1), NodeIdFormula(row));
            SetText(sheet.Cell(row, 2), node.ExportId);
            SetText(sheet.Cell(row, 3), node.ThemeId);
            SetText(sheet.Cell(row, 4), node.Category);
            SetFormula(sheet.Cell(row, 5), NodeCategoryNameFormula(row));
            SetFormula(sheet.Cell(row, 6), NodeEffectDescriptionFormula(row));
            SetText(sheet.Cell(row, 7), node.Image);
            SetNullableInt(sheet.Cell(row, 8), node.NodePermission);
            SetText(sheet.Cell(row, 9), node.ActiveItemId);
            SetText(sheet.Cell(row, 10), node.ActiveItemValue);
            SetNullableDouble(sheet.Cell(row, 11), node.TotalRequiredHelper);
            SetNullableInt(sheet.Cell(row, 12), node.ActiveStep);
            SetInt(sheet.Cell(row, 13), node.Column);
            SetInt(sheet.Cell(row, 14), node.Row);
            for (var condition = 0; condition < 5; condition++)
                SetText(sheet.Cell(row, 15 + condition), node.Conditions[condition]);
            SetFormula(sheet.Cell(row, 20), NodeEffectIdFormula(row));
        }

        DeleteTrailingRows(sheet, FirstDataRow + nodes.Count, originalLastRow);
    }

    private static void RewriteResearchEffects(IXLWorksheet sheet, ResearchWorkbookContext context)
    {
        var originalEffects = context.OriginalSnapshot?.Effects ?? [];
        var currentByIdentity = context.Effects.ToDictionary(effect => effect.RowIdentity, StringComparer.OrdinalIgnoreCase);
        var originalByIdentity = originalEffects.ToDictionary(effect => effect.RowIdentity, StringComparer.OrdinalIgnoreCase);

        foreach (var original in originalEffects)
        {
            if (currentByIdentity.TryGetValue(original.RowIdentity, out var current))
                WriteEffectRow(sheet, original.SourceRowNumber, current);
        }

        var deletedRows = originalEffects
            .Where(original => !currentByIdentity.ContainsKey(original.RowIdentity))
            .Select(original => original.SourceRowNumber)
            .Where(row => row >= FirstDataRow)
            .Distinct()
            .OrderByDescending(row => row)
            .ToList();
        foreach (var row in deletedRows)
            sheet.Range(row, 1, row, EffectColumnCount).Clear(XLClearOptions.Contents);

        var added = context.Effects
            .Where(effect => !originalByIdentity.ContainsKey(effect.RowIdentity))
            .ToList();
        var templateRow = originalEffects.FirstOrDefault()?.SourceRowNumber ?? Math.Max(FirstDataRow, LastRow(sheet));
        var reusableRows = new Queue<int>(deletedRows.OrderBy(row => row));
        foreach (var effect in added)
        {
            var targetRow = reusableRows.Count > 0 ? reusableRows.Dequeue() : LastRow(sheet) + 1;
            if (targetRow > LastRow(sheet))
                CopyRowTemplate(sheet, templateRow, targetRow, EffectColumnCount);
            WriteEffectRow(sheet, targetRow, effect);
        }
    }

    private static void WriteEffectRow(IXLWorksheet sheet, int row, ResearchEffectRow effect)
    {
        if (row < FirstDataRow)
            throw new InvalidDataException($"연구 효과의 원본 행 번호가 잘못되었습니다: {effect.Id}");
        for (var column = 1; column <= EffectColumnCount; column++)
            WriteCellValue(sheet.Cell(row, column), effect.GetCell(column - 1));
    }

    private static void VerifySavedPair(
        ResearchWorkbookContext expected,
        string outSystemPath,
        string effectPath,
        bool validate)
    {
        VerifyFormulaCaches(outSystemPath, expected);
        var reloaded = Load(outSystemPath, effectPath);
        if (reloaded.Categories.Count != expected.Categories.Count
            || reloaded.Nodes.Count != expected.Nodes.Count
            || reloaded.Effects.Count != expected.Effects.Count)
        {
            throw new InvalidDataException(
                $"저장 후 행 수가 달라졌습니다. category {expected.Categories.Count}/{reloaded.Categories.Count}, " +
                $"node {expected.Nodes.Count}/{reloaded.Nodes.Count}, effect {expected.Effects.Count}/{reloaded.Effects.Count}");
        }

        var persistenceDifferences = ComparePersistedData(expected, reloaded);
        if (persistenceDifferences.Count > 0)
        {
            throw new InvalidDataException(
                "저장 후 데이터가 원본 모델과 다릅니다." + Environment.NewLine
                + string.Join(Environment.NewLine, persistenceDifferences.Take(20)));
        }

        if (!validate)
            return;
        var expectedIssues = Validate(expected);
        var reloadedIssues = Validate(reloaded);
        var newIssues = FindNewValidationErrors(reloadedIssues, expectedIssues, useRowIdentity: false);
        if (newIssues.Count > 0)
            throw new ResearchWorkbookValidationException(newIssues, "저장 과정에서 새로운 검증 오류가 발생했습니다.");
    }

    private static void WriteFormulaCaches(string workbookPath, ResearchWorkbookContext context)
    {
        using var archive = ZipFile.Open(workbookPath, ZipArchiveMode.Update);
        var categoryValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < context.Categories.Count; index++)
        {
            var row = FirstDataRow + index;
            categoryValues[$"A{row}"] = context.Categories[index].Category;
            categoryValues[$"F{row}"] = context.Categories[index].NodeName;
        }
        SetFormulaCaches(archive, CategorySheetName, categoryValues);

        var nodeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < context.Nodes.Count; index++)
        {
            var row = FirstDataRow + index;
            var node = context.Nodes[index];
            nodeValues[$"A{row}"] = node.Id;
            nodeValues[$"E{row}"] = node.CategoryName;
            nodeValues[$"F{row}"] = node.NodeEffectDesc;
            nodeValues[$"T{row}"] = node.NexusEffectId;
        }
        SetFormulaCaches(archive, NodeSheetName, nodeValues);
    }

    private static void VerifyFormulaCaches(string workbookPath, ResearchWorkbookContext context)
    {
        using var archive = ZipFile.OpenRead(workbookPath);
        var categoryValues = ReadFormulaCaches(archive, CategorySheetName);
        for (var index = 0; index < context.Categories.Count; index++)
        {
            var row = FirstDataRow + index;
            EnsureFormulaCache(categoryValues, $"A{row}", context.Categories[index].Category, CategorySheetName);
            EnsureFormulaCache(categoryValues, $"F{row}", context.Categories[index].NodeName, CategorySheetName);
        }

        var nodeValues = ReadFormulaCaches(archive, NodeSheetName);
        for (var index = 0; index < context.Nodes.Count; index++)
        {
            var row = FirstDataRow + index;
            var node = context.Nodes[index];
            EnsureFormulaCache(nodeValues, $"A{row}", node.Id, NodeSheetName);
            EnsureFormulaCache(nodeValues, $"E{row}", node.CategoryName, NodeSheetName);
            EnsureFormulaCache(nodeValues, $"F{row}", node.NodeEffectDesc, NodeSheetName);
            EnsureFormulaCache(nodeValues, $"T{row}", node.NexusEffectId, NodeSheetName);
        }
    }

    private static void EnsureFormulaCache(
        IReadOnlyDictionary<string, string> values,
        string address,
        string expected,
        string sheet)
    {
        if (!values.TryGetValue(address, out var actual)
            || !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{sheet}!{address} 수식 캐시값이 올바르지 않습니다. expected={expected}, actual={actual ?? "<missing>"}");
        }
    }

    private static void SetFormulaCaches(
        ZipArchive archive,
        string sheetName,
        IReadOnlyDictionary<string, string> values)
    {
        var entryPath = ResolveWorksheetEntryPath(archive, sheetName);
        var entry = archive.GetEntry(entryPath)
            ?? throw new InvalidDataException($"워크시트 XML을 찾을 수 없습니다: {sheetName}");
        var document = LoadXml(entry);
        var spreadsheet = document.Root?.Name.Namespace
            ?? throw new InvalidDataException($"워크시트 XML이 비어 있습니다: {sheetName}");
        var cells = document.Descendants(spreadsheet + "c")
            .Where(cell => cell.Attribute("r") is not null)
            .ToDictionary(cell => cell.Attribute("r")!.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var (address, value) in values)
        {
            if (!cells.TryGetValue(address, out var cell) || cell.Element(spreadsheet + "f") is null)
                throw new InvalidDataException($"수식 셀을 찾을 수 없습니다: {sheetName}!{address}");
            cell.SetAttributeValue("t", "str");
            var cached = cell.Element(spreadsheet + "v");
            if (cached is null)
            {
                cached = new XElement(spreadsheet + "v");
                cell.Element(spreadsheet + "f")!.AddAfterSelf(cached);
            }
            cached.Value = value;
        }

        ReplaceXmlEntry(archive, entryPath, document);
    }

    private static Dictionary<string, string> ReadFormulaCaches(ZipArchive archive, string sheetName)
    {
        var entryPath = ResolveWorksheetEntryPath(archive, sheetName);
        var entry = archive.GetEntry(entryPath)
            ?? throw new InvalidDataException($"워크시트 XML을 찾을 수 없습니다: {sheetName}");
        var document = LoadXml(entry);
        var spreadsheet = document.Root?.Name.Namespace
            ?? throw new InvalidDataException($"워크시트 XML이 비어 있습니다: {sheetName}");
        return document.Descendants(spreadsheet + "c")
            .Where(cell => cell.Attribute("r") is not null && cell.Element(spreadsheet + "f") is not null)
            .ToDictionary(
                cell => cell.Attribute("r")!.Value,
                cell => cell.Element(spreadsheet + "v")?.Value ?? "",
                StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveWorksheetEntryPath(ZipArchive archive, string sheetName)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("xl/workbook.xml을 찾을 수 없습니다.");
        var workbook = LoadXml(workbookEntry);
        var spreadsheet = workbook.Root?.Name.Namespace
            ?? throw new InvalidDataException("workbook.xml이 비어 있습니다.");
        var sheet = workbook.Descendants(spreadsheet + "sheet")
            .FirstOrDefault(item => string.Equals(item.Attribute("name")?.Value, sheetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"워크시트를 찾을 수 없습니다: {sheetName}");
        var relationshipId = sheet.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value
            ?? throw new InvalidDataException($"워크시트 관계 ID가 없습니다: {sheetName}");

        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidDataException("workbook.xml.rels를 찾을 수 없습니다.");
        var relationships = LoadXml(relationshipsEntry);
        var package = relationships.Root?.Name.Namespace
            ?? throw new InvalidDataException("workbook.xml.rels가 비어 있습니다.");
        var target = relationships.Descendants(package + "Relationship")
            .FirstOrDefault(item => string.Equals(item.Attribute("Id")?.Value, relationshipId, StringComparison.Ordinal))
            ?.Attribute("Target")?.Value
            ?? throw new InvalidDataException($"워크시트 관계 경로가 없습니다: {sheetName}");

        return new Uri(new Uri("https://nexus.local/xl/workbook.xml"), target)
            .AbsolutePath.TrimStart('/');
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, System.Xml.Linq.LoadOptions.PreserveWhitespace);
    }

    private static void ReplaceXmlEntry(ZipArchive archive, string entryPath, XDocument document)
    {
        archive.GetEntry(entryPath)?.Delete();
        var replacement = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = replacement.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = false
        });
        document.Save(writer);
    }

    private static List<ResearchValidationIssue> FindNewValidationErrors(
        IEnumerable<ResearchValidationIssue> current,
        IEnumerable<ResearchValidationIssue> baseline,
        bool useRowIdentity)
    {
        var available = baseline
            .Where(issue => issue.Severity == ResearchValidationSeverity.Error)
            .GroupBy(issue => ValidationFingerprint(issue, useRowIdentity), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var added = new List<ResearchValidationIssue>();
        foreach (var issue in current.Where(issue => issue.Severity == ResearchValidationSeverity.Error))
        {
            var key = ValidationFingerprint(issue, useRowIdentity);
            if (available.TryGetValue(key, out var count) && count > 0)
            {
                available[key] = count - 1;
                continue;
            }
            added.Add(issue);
        }
        return added;
    }

    private static string ValidationFingerprint(ResearchValidationIssue issue, bool useRowIdentity) =>
        string.Join("\u001f", issue.Code, issue.Sheet,
            useRowIdentity ? issue.RowIdentity : issue.TargetId,
            issue.Column);

    private static CommitResult CommitWorkbookPair(
        string outTemp,
        string outTarget,
        string effectTemp,
        string effectTarget,
        bool keepBackups)
    {
        PreflightWritable(outTarget);
        PreflightWritable(effectTarget);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var outExisted = File.Exists(outTarget);
        var effectExisted = File.Exists(effectTarget);
        var outRollback = outExisted ? AvailableBackupPath(outTarget, stamp, keepBackups ? "backup" : "rollback") : null;
        var effectRollback = effectExisted ? AvailableBackupPath(effectTarget, stamp, keepBackups ? "backup" : "rollback") : null;
        if (outExisted)
            File.Copy(outTarget, outRollback!, overwrite: false);
        if (effectExisted)
            File.Copy(effectTarget, effectRollback!, overwrite: false);

        var outCommitted = false;
        var effectCommitted = false;
        var commitSucceeded = false;
        var rollbackSucceeded = false;
        try
        {
            ReplaceTarget(outTemp, outTarget, outExisted);
            outCommitted = true;
            ReplaceTarget(effectTemp, effectTarget, effectExisted);
            effectCommitted = true;
            commitSucceeded = true;
        }
        catch (Exception commitError)
        {
            var rollbackErrors = new List<Exception>();
            if (effectCommitted)
            {
                try { RestoreTarget(effectTarget, effectRollback, effectExisted); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            if (outCommitted)
            {
                try { RestoreTarget(outTarget, outRollback, outExisted); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            rollbackSucceeded = rollbackErrors.Count == 0;
            if (!rollbackSucceeded)
            {
                throw new AggregateException(
                    "연구 DB 저장과 자동 복구가 모두 실패했습니다. backup/rollback 파일을 보존했습니다.",
                    new[] { commitError }.Concat(rollbackErrors));
            }
            throw;
        }
        finally
        {
            if (!keepBackups && (commitSucceeded || rollbackSucceeded))
            {
                TryDelete(outRollback);
                TryDelete(effectRollback);
            }
        }

        return new CommitResult(
            keepBackups ? outRollback : null,
            keepBackups ? effectRollback : null);
    }

    private static void ReplaceTarget(string tempPath, string targetPath, bool targetExists)
    {
        if (targetExists)
            File.Replace(tempPath, targetPath, null, ignoreMetadataErrors: true);
        else
            File.Move(tempPath, targetPath);
    }

    private static void RestoreTarget(string targetPath, string? rollbackPath, bool targetExisted)
    {
        if (!targetExisted)
        {
            TryDelete(targetPath);
            return;
        }
        if (rollbackPath is null || !File.Exists(rollbackPath))
            throw new IOException($"롤백 파일을 찾을 수 없습니다: {targetPath}");
        File.Copy(rollbackPath, targetPath, overwrite: true);
    }

    private static void PreflightWritable(string path)
    {
        if (!File.Exists(path))
            return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void EnsureSourceUnchanged(string path, string expectedSignature, string label)
    {
        if (string.IsNullOrWhiteSpace(expectedSignature))
            return;
        var currentSignature = ComputeFileSignature(path);
        if (!string.Equals(currentSignature, expectedSignature, StringComparison.Ordinal))
        {
            throw new IOException(
                $"{label}가 에디터에서 연 뒤 외부에서 변경되었습니다. Reload한 뒤 다시 Export하세요.\n{path}");
        }
    }

    private static string ComputeFileSignature(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void RewriteSourceRowNumbersAfterSequentialSave(ResearchWorkbookContext context)
    {
        for (var index = 0; index < context.Categories.Count; index++)
            context.Categories[index].SourceRowNumber = FirstDataRow + index;
        for (var index = 0; index < context.Nodes.Count; index++)
            context.Nodes[index].SourceRowNumber = FirstDataRow + index;
    }

    private static void EnsureSequentialCapacity(
        IXLWorksheet sheet,
        int rowCount,
        int columnCount,
        int originalLastRow,
        int templateRow)
    {
        var requiredLastRow = FirstDataRow + rowCount - 1;
        for (var row = Math.Max(originalLastRow + 1, FirstDataRow); row <= requiredLastRow; row++)
            CopyRowTemplate(sheet, templateRow, row, columnCount);
    }

    private static void CopyRowTemplate(IXLWorksheet sheet, int templateRow, int targetRow, int columnCount)
    {
        if (targetRow == templateRow || templateRow < FirstDataRow || templateRow > LastRow(sheet))
            return;
        sheet.Cell(targetRow, 1).CopyFrom(sheet.Range(templateRow, 1, templateRow, columnCount));
        sheet.Row(targetRow).Height = sheet.Row(templateRow).Height;
    }

    private static void DeleteTrailingRows(IXLWorksheet sheet, int firstDeleteRow, int originalLastRow)
    {
        if (firstDeleteRow <= originalLastRow)
            sheet.Rows(firstDeleteRow, originalLastRow).Delete();
    }

    private static void WriteCellValue(IXLCell cell, ResearchCellValue value)
    {
        cell.Clear(XLClearOptions.Contents);
        if (!string.IsNullOrWhiteSpace(value.NumberFormat))
            cell.Style.NumberFormat.Format = value.NumberFormat;
        if (value.HasFormula)
        {
            cell.FormulaA1 = NormalizeFormula(value.FormulaA1);
            return;
        }

        switch (value.Kind)
        {
            case ResearchCellValueKind.Blank:
                return;
            case ResearchCellValueKind.Text:
                cell.Value = SanitizeText(value.Text);
                return;
            case ResearchCellValueKind.Number:
                cell.Value = value.Number;
                return;
            case ResearchCellValueKind.Boolean:
                cell.Value = value.Boolean;
                return;
            case ResearchCellValueKind.DateTime:
                cell.Value = value.DateTime;
                return;
            case ResearchCellValueKind.TimeSpan:
                cell.Value = value.TimeSpan;
                return;
            case ResearchCellValueKind.Error:
                if (Enum.TryParse<XLError>(value.Text, ignoreCase: true, out var error))
                    cell.Value = error;
                else
                    cell.Value = value.Text;
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(value.Kind));
        }
    }

    private static ResearchCellValue CaptureCell(IXLCell cell)
    {
        var formula = Formula(cell);
        var value = formula.Length > 0 ? cell.CachedValue : cell.Value;
        var captured = value.Type switch
        {
            XLDataType.Blank => ResearchCellValue.Blank(cell.Style.NumberFormat.Format),
            XLDataType.Text => ResearchCellValue.FromText(SanitizeText(value.GetText()), cell.Style.NumberFormat.Format),
            XLDataType.Number => ResearchCellValue.FromNumber(value.GetNumber(), cell.Style.NumberFormat.Format),
            XLDataType.Boolean => new ResearchCellValue
            {
                Kind = ResearchCellValueKind.Boolean,
                Boolean = value.GetBoolean(),
                NumberFormat = cell.Style.NumberFormat.Format
            },
            XLDataType.DateTime => new ResearchCellValue
            {
                Kind = ResearchCellValueKind.DateTime,
                DateTime = value.GetDateTime(),
                NumberFormat = cell.Style.NumberFormat.Format
            },
            XLDataType.TimeSpan => new ResearchCellValue
            {
                Kind = ResearchCellValueKind.TimeSpan,
                TimeSpan = value.GetTimeSpan(),
                NumberFormat = cell.Style.NumberFormat.Format
            },
            XLDataType.Error => new ResearchCellValue
            {
                Kind = ResearchCellValueKind.Error,
                Text = value.GetError().ToString(),
                NumberFormat = cell.Style.NumberFormat.Format
            },
            _ => ResearchCellValue.FromText(SanitizeText(value.ToString(CultureInfo.InvariantCulture)), cell.Style.NumberFormat.Format)
        };
        captured.FormulaA1 = formula;
        return captured;
    }

    private static RowSnapshot SnapshotCategory(ResearchCategoryRow row) => new(
        row.RowIdentity,
        row.Category,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["category"] = row.Category,
            ["export_id"] = row.ExportId,
            ["helper_c"] = row.HelperPrefix,
            ["helper_d"] = row.CategoryKey,
            ["index"] = Canonical(row.Index),
            ["node_name"] = row.NodeName,
            ["category_formula"] = NormalizeFormula(row.CategoryFormulaA1),
            ["node_name_formula"] = NormalizeFormula(row.NodeNameFormulaA1)
        });

    private static RowSnapshot SnapshotNode(ResearchNodeRow row) => new(
        row.RowIdentity,
        row.Id,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = row.Id,
            ["export_id"] = row.ExportId,
            ["theme_id"] = row.ThemeId,
            ["category"] = row.Category,
            ["category_name"] = row.CategoryName,
            ["node_effect_desc"] = row.NodeEffectDesc,
            ["image"] = row.Image,
            ["node_permission"] = Canonical(row.NodePermission),
            ["active_item_id"] = row.ActiveItemId,
            ["active_item_value"] = row.ActiveItemValue,
            ["total"] = Canonical(row.TotalRequiredHelper),
            ["active_step"] = Canonical(row.ActiveStep),
            ["column"] = Canonical(row.Column),
            ["row"] = Canonical(row.Row),
            ["condition_node_1"] = row.ConditionNode1,
            ["condition_node_2"] = row.ConditionNode2,
            ["condition_node_3"] = row.ConditionNode3,
            ["condition_node_4"] = row.ConditionNode4,
            ["condition_node_5"] = row.ConditionNode5,
            ["nexus_effect_id"] = row.NexusEffectId,
            ["id_formula"] = NormalizeFormula(row.IdFormulaA1),
            ["category_name_formula"] = NormalizeFormula(row.CategoryNameFormulaA1),
            ["node_effect_desc_formula"] = NormalizeFormula(row.NodeEffectDescFormulaA1),
            ["nexus_effect_id_formula"] = NormalizeFormula(row.NexusEffectIdFormulaA1)
        });

    private static RowSnapshot SnapshotEffect(ResearchEffectRow row)
    {
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < EffectColumnCount; index++)
            columns[$"column_{index + 1}"] = Canonical(row.GetCell(index));
        return new RowSnapshot(row.RowIdentity, row.Id, columns);
    }

    private static RowSnapshot SnapshotCategoryForPersistence(ResearchCategoryRow row)
    {
        var snapshot = SnapshotCategory(row);
        return WithoutFormulaColumns(snapshot);
    }

    private static RowSnapshot SnapshotNodeForPersistence(ResearchNodeRow row)
    {
        var snapshot = SnapshotNode(row);
        return WithoutFormulaColumns(snapshot);
    }

    private static RowSnapshot WithoutFormulaColumns(RowSnapshot snapshot) => new(
        snapshot.Identity,
        snapshot.Key,
        snapshot.Columns
            .Where(pair => !pair.Key.EndsWith("_formula", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static void CompareRows(
        List<ResearchDiffEntry> target,
        string sheet,
        IEnumerable<RowSnapshot> originalRows,
        IEnumerable<RowSnapshot> editedRows)
    {
        var original = originalRows
            .GroupBy(row => row.Identity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var edited = editedRows
            .GroupBy(row => row.Identity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var identity in original.Keys.Union(edited.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            original.TryGetValue(identity, out var before);
            edited.TryGetValue(identity, out var after);
            if (before is null)
            {
                var added = NewDiff(sheet, identity, ResearchDiffChangeType.Add, null, after!);
                added.ChangedColumns.AddRange(after!.Columns.Keys);
                target.Add(added);
                continue;
            }
            if (after is null)
            {
                var deleted = NewDiff(sheet, identity, ResearchDiffChangeType.Delete, before, null);
                deleted.ChangedColumns.AddRange(before.Columns.Keys);
                target.Add(deleted);
                continue;
            }

            var changedColumns = before.Columns.Keys.Union(after.Columns.Keys, StringComparer.Ordinal)
                .Where(column => !before.Columns.TryGetValue(column, out var oldValue)
                                 || !after.Columns.TryGetValue(column, out var newValue)
                                 || !string.Equals(oldValue, newValue, StringComparison.Ordinal))
                .OrderBy(column => column, StringComparer.Ordinal)
                .ToList();
            if (changedColumns.Count == 0)
                continue;

            var modified = NewDiff(sheet, identity, ResearchDiffChangeType.Modify, before, after);
            modified.ChangedColumns.AddRange(changedColumns);
            target.Add(modified);
        }
    }

    private static ResearchDiffEntry NewDiff(
        string sheet,
        string identity,
        ResearchDiffChangeType changeType,
        RowSnapshot? before,
        RowSnapshot? after) => new()
    {
        Sheet = sheet,
        RowIdentity = identity,
        Key = after?.Key ?? before?.Key ?? "",
        OriginalKey = before?.Key ?? "",
        ChangeType = changeType,
        BeforeValue = SnapshotText(before),
        AfterValue = SnapshotText(after)
    };

    private static string DiffIdentity(ResearchDiffEntry entry) =>
        $"{entry.Sheet}\u001f{entry.RowIdentity}";

    private static List<HashSet<string>> BuildCascadeDiffGroups(
        ResearchWorkbookContext context,
        IReadOnlyCollection<ResearchDiffEntry> allDiff)
    {
        var original = context.OriginalSnapshot
            ?? throw new InvalidOperationException("원본 스냅샷이 없어 연쇄 변경을 되돌릴 수 없습니다.");
        var groups = new List<HashSet<string>>();

        foreach (var entry in allDiff.Where(item => Same(item.Sheet, CategorySheetName)))
        {
            var before = original.Categories.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
            var after = context.Categories.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
            if (before is not null && after is not null && Same(before.Category, after.Category))
                continue;

            var categoryIds = new[] { before?.Category, after?.Category }
                .Where(NotBlank)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var relatedNodes = original.Nodes.Concat(context.Nodes)
                .Where(node => categoryIds.Contains(node.Category))
                .ToList();
            groups.Add(BuildNodeCascadeGroup(allDiff, original, context, relatedNodes, entry));
        }

        foreach (var entry in allDiff.Where(item => Same(item.Sheet, NodeSheetName)))
        {
            var before = original.Nodes.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
            var after = context.Nodes.FirstOrDefault(row => Same(row.RowIdentity, entry.RowIdentity));
            if (before is not null && after is not null && Same(before.Id, after.Id))
                continue;
            groups.Add(BuildNodeCascadeGroup(allDiff, original, context,
                new[] { before, after }.Where(node => node is not null).Cast<ResearchNodeRow>(), entry));
        }

        return groups;
    }

    private static HashSet<string> BuildNodeCascadeGroup(
        IReadOnlyCollection<ResearchDiffEntry> allDiff,
        ResearchWorkbookContext original,
        ResearchWorkbookContext current,
        IEnumerable<ResearchNodeRow> seedNodes,
        ResearchDiffEntry seedEntry)
    {
        var nodes = seedNodes.ToList();
        var nodeRowIdentities = nodes.Select(node => node.RowIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodeIds = nodes.Select(node => node.Id).Where(NotBlank)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dependentNodeRows = original.Nodes.Concat(current.Nodes)
            .Where(node => node.Conditions.Any(condition => nodeIds.Contains(condition)))
            .Select(node => node.RowIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectRows = nodes
            .SelectMany(node => new[]
            {
                original.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, node.RowIdentity)) is { } originalNode
                    ? original.FindEffect(originalNode)
                    : null,
                current.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, node.RowIdentity)) is { } currentNode
                    ? current.FindEffect(currentNode)
                    : null
            })
            .Where(effect => effect is not null)
            .Cast<ResearchEffectRow>()
            .Select(effect => effect.RowIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            DiffIdentity(seedEntry)
        };
        foreach (var entry in allDiff)
        {
            if (Same(entry.Sheet, NodeSheetName)
                && (nodeRowIdentities.Contains(entry.RowIdentity) || dependentNodeRows.Contains(entry.RowIdentity))
                || Same(entry.Sheet, EffectSheetName) && effectRows.Contains(entry.RowIdentity))
            {
                group.Add(DiffIdentity(entry));
            }
        }
        return group;
    }

    private static string SnapshotText(RowSnapshot? snapshot) => snapshot is null
        ? ""
        : string.Join(" | ", snapshot.Columns.Select(pair => $"{pair.Key}={pair.Value}"));

    private static bool RevertRow<T>(
        List<T> currentRows,
        IReadOnlyCollection<T> originalRows,
        string rowIdentity,
        Func<T, string> identity,
        Func<T, int> sourceRow,
        Func<T, T> clone)
    {
        var currentIndex = currentRows.FindIndex(row => Same(identity(row), rowIdentity));
        var original = originalRows.FirstOrDefault(row => Same(identity(row), rowIdentity));
        if (original is null)
        {
            if (currentIndex < 0)
                return false;
            currentRows.RemoveAt(currentIndex);
            return true;
        }

        if (currentIndex >= 0)
            currentRows[currentIndex] = clone(original);
        else
            currentRows.Add(clone(original));

        currentRows.Sort((left, right) => SortableSourceRow(sourceRow(left)).CompareTo(SortableSourceRow(sourceRow(right))));
        return true;
    }

    private static int SortableSourceRow(int row) => row > 0 ? row : int.MaxValue;

    private static Dictionary<string, List<T>> UniqueRows<T>(IEnumerable<T> rows, Func<T, string> keySelector) =>
        rows.GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

    private static void AddDuplicateIssues<T>(
        List<ResearchValidationIssue> issues,
        string sheet,
        string code,
        string label,
        IEnumerable<T> rows,
        Func<T, string?> keySelector,
        Func<T, string> identitySelector)
    {
        foreach (var group in rows
                     .Select(row => (Row: row, Key: SanitizeText(keySelector(row)).Trim()))
                     .Where(item => item.Key.Length > 0)
                     .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var item in group)
            {
                issues.Add(Error(code, $"{label}가 중복됩니다: {group.Key}", sheet,
                    identitySelector(item.Row), group.Key, label));
            }
        }
    }

    private static ResearchValidationIssue Error(
        string code,
        string message,
        string sheet,
        string rowIdentity,
        string targetId,
        string column) => new()
    {
        Severity = ResearchValidationSeverity.Error,
        Code = code,
        Message = message,
        Sheet = sheet,
        RowIdentity = rowIdentity,
        TargetId = targetId,
        Column = column
    };

    private static ResearchValidationIssue Warning(
        string code,
        string message,
        string sheet,
        string rowIdentity,
        string targetId,
        string column) => new()
    {
        Severity = ResearchValidationSeverity.Warning,
        Code = code,
        Message = message,
        Sheet = sheet,
        RowIdentity = rowIdentity,
        TargetId = targetId,
        Column = column
    };

    private static bool HasDuplicateGeneratedKeys(ResearchWorkbookContext context, out string message)
    {
        var duplicateCategory = context.Categories.GroupBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => NotBlank(group.Key) && group.Count() > 1);
        if (duplicateCategory is not null)
        {
            message = $"카테고리 ID가 중복됩니다: {duplicateCategory.Key}";
            return true;
        }

        var duplicateNode = context.Nodes.GroupBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => NotBlank(group.Key) && group.Count() > 1);
        if (duplicateNode is not null)
        {
            message = $"연구 노드 ID가 중복됩니다: {duplicateNode.Key}";
            return true;
        }

        var duplicateCoordinate = context.Nodes
            .GroupBy(row => $"{row.Category}\u001f{row.Column}\u001f{row.Row}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCoordinate is not null)
        {
            var node = duplicateCoordinate.First();
            message = $"연구 노드 좌표가 중복됩니다: {node.Category} ({node.Column}, {node.Row})";
            return true;
        }

        var duplicateEffect = context.Effects.GroupBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => NotBlank(group.Key) && group.Count() > 1);
        if (duplicateEffect is not null)
        {
            message = $"연구 효과 ID가 중복됩니다: {duplicateEffect.Key}";
            return true;
        }

        message = "";
        return false;
    }

    private static bool HasCycle(IReadOnlyCollection<ResearchNodeRow> nodes)
    {
        var byId = nodes.Where(node => NotBlank(node.Id))
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        bool Visit(ResearchNodeRow node)
        {
            if (state.TryGetValue(node.Id, out var value))
                return value == 1;
            state[node.Id] = 1;
            foreach (var condition in node.Conditions.Where(NotBlank))
            {
                if (byId.TryGetValue(condition, out var prerequisite) && Visit(prerequisite))
                    return true;
            }
            state[node.Id] = 2;
            return false;
        }

        return nodes.Any(Visit);
    }

    private static void CascadeConditionIds(
        IEnumerable<ResearchNodeRow> nodes,
        IReadOnlyDictionary<string, string> idMap,
        ISet<string> affected)
    {
        foreach (var node in nodes)
        {
            var changed = false;
            for (var index = 0; index < node.Conditions.Count; index++)
            {
                if (!idMap.TryGetValue(node.Conditions[index], out var replacement))
                    continue;
                node.SetCondition(index, replacement);
                changed = true;
            }
            if (changed)
                affected.Add(node.RowIdentity);
        }
    }

    private static void RemoveConditionReferences(ResearchNodeRow node, ISet<string> removedIds)
    {
        var remaining = node.Conditions
            .Where(value => NotBlank(value) && !removedIds.Contains(value))
            .ToList();
        for (var index = 0; index < 5; index++)
            node.SetCondition(index, index < remaining.Count ? remaining[index] : "");
    }

    private static string ReplaceOrdinalIgnoreCase(string source, string oldValue, string newValue)
    {
        if (source.Length == 0 || oldValue.Length == 0)
            return source;
        return Regex.Replace(source, Regex.Escape(oldValue), _ => newValue, RegexOptions.IgnoreCase);
    }

    private static int NextSourceRow(IEnumerable<int> sourceRows) =>
        Math.Max(FirstDataRow - 1, sourceRows.DefaultIfEmpty(FirstDataRow - 1).Max()) + 1;

    private static string MostCommonExportId(IEnumerable<string> exportIds) =>
        exportIds.Where(NotBlank)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? DefaultResearchExportId;

    private static string CategoryFormula(int row) => $"_xlfn.TEXTJOIN(\"_\",TRUE,C{row},D{row})";
    private static string CategoryNodeNameFormula(int row) => $"\"s1_node_\"&D{row}&\"_name\"";
    private static string NodeIdFormula(int row) => $"_xlfn.TEXTJOIN(\"_\",TRUE,C{row},\"node\",D{row},M{row},N{row})";
    private static string NodeCategoryNameFormula(int row) => $"\"s1_node_\"&D{row}&\"_name\"";
    private static string NodeEffectDescriptionFormula(int row) => $"\"s1_node_\"&D{row}&\"_desc\"";
    private static string NodeEffectIdFormula(int row) => $"_xlfn.TEXTJOIN(\"_\",TRUE,C{row},\"node_eff\",D{row},M{row},N{row})";

    private static bool SameFormula(string? left, string? right) =>
        string.Equals(NormalizeFormula(left), NormalizeFormula(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFormula(string? formula)
    {
        var value = SanitizeText(formula).Trim();
        if (value.StartsWith("=", StringComparison.Ordinal))
            value = value[1..];
        return Regex.Replace(value, @"\s+", "");
    }

    private static string SanitizeIdentifierPart(string? value)
    {
        var sanitized = SanitizeText(value).Trim().ToLowerInvariant();
        sanitized = Regex.Replace(sanitized, @"[^a-z0-9_]+", "_");
        sanitized = Regex.Replace(sanitized, @"_+", "_");
        return sanitized.Trim('_');
    }

    private static string SanitizeText(string? value) =>
        InvisibleFormatCharacters.Replace(value ?? "", "").Replace("\0", "", StringComparison.Ordinal);

    private static int SanitizeField(string? value, Action<string> setter)
    {
        var original = value ?? "";
        var sanitized = SanitizeText(original);
        if (string.Equals(original, sanitized, StringComparison.Ordinal))
            return 0;
        setter(sanitized);
        return 1;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    private static int LastRow(IXLWorksheet sheet) =>
        sheet.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? FirstDataRow - 1;

    private static bool HasData(IXLWorksheet sheet, int row, int columnCount)
    {
        for (var column = 1; column <= columnCount; column++)
        {
            var cell = sheet.Cell(row, column);
            if (cell.HasFormula || !cell.IsEmpty())
                return true;
        }
        return false;
    }

    private static string CellText(IXLCell cell)
    {
        var value = cell.HasFormula ? cell.CachedValue : cell.Value;
        return SanitizeText(value.Type switch
        {
            XLDataType.Blank => "",
            XLDataType.Text => value.GetText(),
            XLDataType.Number => value.GetNumber().ToString("G17", CultureInfo.InvariantCulture),
            XLDataType.Boolean => value.GetBoolean() ? "TRUE" : "FALSE",
            XLDataType.DateTime => value.GetDateTime().ToString("O", CultureInfo.InvariantCulture),
            XLDataType.TimeSpan => value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),
            XLDataType.Error => value.GetError().ToString(),
            _ => value.ToString(CultureInfo.InvariantCulture)
        });
    }

    private static string Formula(IXLCell cell) => SanitizeText(cell.FormulaA1);

    private static int? NullableInt(IXLCell cell)
    {
        var value = cell.HasFormula ? cell.CachedValue : cell.Value;
        if (value.Type == XLDataType.Number)
            return checked((int)Math.Round(value.GetNumber(), MidpointRounding.AwayFromZero));
        return int.TryParse(CellText(cell), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? NullableDouble(IXLCell cell)
    {
        var value = cell.HasFormula ? cell.CachedValue : cell.Value;
        if (value.Type == XLDataType.Number)
            return value.GetNumber();
        return double.TryParse(CellText(cell), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string ValueOrGenerated(string value, string generated) => NotBlank(value) ? value : generated;

    private static void SetText(IXLCell cell, string? value)
    {
        cell.Clear(XLClearOptions.Contents);
        var sanitized = SanitizeText(value);
        if (sanitized.Length > 0)
            cell.Value = sanitized;
    }

    private static void SetFormula(IXLCell cell, string formula)
    {
        cell.Clear(XLClearOptions.Contents);
        cell.FormulaA1 = NormalizeFormula(formula);
    }

    private static void SetNullableInt(IXLCell cell, int? value)
    {
        cell.Clear(XLClearOptions.Contents);
        if (value.HasValue)
            cell.Value = value.Value;
    }

    private static void SetNullableDouble(IXLCell cell, double? value)
    {
        cell.Clear(XLClearOptions.Contents);
        if (value.HasValue)
            cell.Value = value.Value;
    }

    private static void SetInt(IXLCell cell, int value)
    {
        cell.Clear(XLClearOptions.Contents);
        cell.Value = value;
    }

    private static string TemporaryWorkbookPath(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath)
                        ?? throw new InvalidOperationException($"출력 폴더가 없습니다: {targetPath}");
        var name = Path.GetFileNameWithoutExtension(targetPath);
        return Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.tmp.xlsx");
    }

    private static string AvailableBackupPath(string targetPath, string stamp, string kind)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var extension = Path.GetExtension(targetPath);
        var candidate = Path.Combine(directory, $"{name}.{kind}.{stamp}{extension}");
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(directory, $"{name}.{kind}.{stamp}.{suffix}{extension}");
        return candidate;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cleanup must not hide the original save exception.
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string Canonical(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Canonical(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Canonical(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "";

    private static string Canonical(ResearchCellValue value) => string.Join("\u001f",
        ((int)value.Kind).ToString(CultureInfo.InvariantCulture),
        value.DisplayText,
        NormalizeFormula(value.FormulaA1),
        value.NumberFormat);

    private static List<string> ComparePersistedData(
        ResearchWorkbookContext expected,
        ResearchWorkbookContext actual)
    {
        var differences = new List<string>();
        ComparePersistedRows(differences, CategorySheetName,
            expected.Categories.Select(SnapshotCategoryForPersistence),
            actual.Categories.Select(SnapshotCategoryForPersistence));
        ComparePersistedRows(differences, NodeSheetName,
            expected.Nodes.Select(SnapshotNodeForPersistence),
            actual.Nodes.Select(SnapshotNodeForPersistence));
        ComparePersistedRows(differences, EffectSheetName,
            expected.Effects.Select(SnapshotEffect),
            actual.Effects.Select(SnapshotEffect));
        return differences;
    }

    private static void ComparePersistedRows(
        List<string> differences,
        string sheet,
        IEnumerable<RowSnapshot> expectedRows,
        IEnumerable<RowSnapshot> actualRows)
    {
        var expected = expectedRows.GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var actual = actualRows.GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in expected.Keys.Union(actual.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!expected.TryGetValue(key, out var before))
            {
                differences.Add($"[{sheet}] 예상하지 않은 행: {key}");
                continue;
            }
            if (!actual.TryGetValue(key, out var after))
            {
                differences.Add($"[{sheet}] 저장 후 누락된 행: {key}");
                continue;
            }
            foreach (var column in before.Columns.Keys.Union(after.Columns.Keys, StringComparer.Ordinal))
            {
                before.Columns.TryGetValue(column, out var beforeValue);
                after.Columns.TryGetValue(column, out var afterValue);
                if (!string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
                    differences.Add($"[{sheet}] {key}.{column}: '{beforeValue}' -> '{afterValue}'");
            }
        }
    }

    private sealed record RowSnapshot(
        string Identity,
        string Key,
        IReadOnlyDictionary<string, string> Columns);

    private sealed record CommitResult(string? OutBackup, string? EffectBackup);
}
