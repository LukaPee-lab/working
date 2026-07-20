using System.Globalization;
using System.IO;

namespace NexusEditor;

public enum ResearchCellValueKind
{
    Blank,
    Text,
    Number,
    Boolean,
    DateTime,
    TimeSpan,
    Error
}

public sealed class ResearchCellValue
{
    public ResearchCellValueKind Kind { get; set; }
    public string Text { get; set; } = "";
    public double Number { get; set; }
    public bool Boolean { get; set; }
    public DateTime DateTime { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public string FormulaA1 { get; set; } = "";
    public string NumberFormat { get; set; } = "General";

    public bool HasFormula => !string.IsNullOrWhiteSpace(FormulaA1);

    public string DisplayText => Kind switch
    {
        ResearchCellValueKind.Blank => "",
        ResearchCellValueKind.Text or ResearchCellValueKind.Error => Text,
        ResearchCellValueKind.Number => Number.ToString("G17", CultureInfo.InvariantCulture),
        ResearchCellValueKind.Boolean => Boolean ? "TRUE" : "FALSE",
        ResearchCellValueKind.DateTime => DateTime.ToString("O", CultureInfo.InvariantCulture),
        ResearchCellValueKind.TimeSpan => TimeSpan.ToString("c", CultureInfo.InvariantCulture),
        _ => Text
    };

    public ResearchCellValue DeepClone() => new()
    {
        Kind = Kind,
        Text = Text,
        Number = Number,
        Boolean = Boolean,
        DateTime = DateTime,
        TimeSpan = TimeSpan,
        FormulaA1 = FormulaA1,
        NumberFormat = NumberFormat
    };

    public static ResearchCellValue Blank(string? numberFormat = null) => new()
    {
        Kind = ResearchCellValueKind.Blank,
        NumberFormat = string.IsNullOrWhiteSpace(numberFormat) ? "General" : numberFormat
    };

    public static ResearchCellValue FromText(string? value, string? numberFormat = null) => new()
    {
        Kind = string.IsNullOrEmpty(value) ? ResearchCellValueKind.Blank : ResearchCellValueKind.Text,
        Text = value ?? "",
        NumberFormat = string.IsNullOrWhiteSpace(numberFormat) ? "General" : numberFormat
    };

    public static ResearchCellValue FromNumber(double value, string? numberFormat = null) => new()
    {
        Kind = ResearchCellValueKind.Number,
        Number = value,
        NumberFormat = string.IsNullOrWhiteSpace(numberFormat) ? "General" : numberFormat
    };

    public static ResearchCellValue FromEditedText(string? value, ResearchCellValue? previous = null)
    {
        var text = value ?? "";
        var numberFormat = previous?.NumberFormat;
        if (text.Length == 0)
            return Blank(numberFormat);

        if (previous?.Kind == ResearchCellValueKind.Number
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return FromNumber(number, numberFormat);
        }

        if (previous?.Kind == ResearchCellValueKind.Boolean && bool.TryParse(text, out var boolean))
        {
            return new ResearchCellValue
            {
                Kind = ResearchCellValueKind.Boolean,
                Boolean = boolean,
                NumberFormat = string.IsNullOrWhiteSpace(numberFormat) ? "General" : numberFormat
            };
        }

        if (previous?.Kind == ResearchCellValueKind.DateTime
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
        {
            return new ResearchCellValue
            {
                Kind = ResearchCellValueKind.DateTime,
                DateTime = dateTime,
                NumberFormat = string.IsNullOrWhiteSpace(numberFormat) ? "General" : numberFormat
            };
        }

        if (previous?.Kind == ResearchCellValueKind.TimeSpan
            && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var timeSpan))
        {
            return new ResearchCellValue
            {
                Kind = ResearchCellValueKind.TimeSpan,
                TimeSpan = timeSpan,
                NumberFormat = string.IsNullOrWhiteSpace(numberFormat) ? "General" : numberFormat
            };
        }

        return FromText(text, numberFormat);
    }
}

public sealed class ResearchCategoryRow
{
    public string RowIdentity { get; set; } = Guid.NewGuid().ToString("N");
    public int SourceRowNumber { get; set; }
    public string Category { get; set; } = "";
    public string ExportId { get; set; } = "";
    public string HelperPrefix { get; set; } = "";
    public string CategoryKey { get; set; } = "";
    public int? Index { get; set; }
    public string NodeName { get; set; } = "";
    public string CategoryFormulaA1 { get; set; } = "";
    public string NodeNameFormulaA1 { get; set; } = "";

    public ResearchCategoryRow DeepClone() => new()
    {
        RowIdentity = RowIdentity,
        SourceRowNumber = SourceRowNumber,
        Category = Category,
        ExportId = ExportId,
        HelperPrefix = HelperPrefix,
        CategoryKey = CategoryKey,
        Index = Index,
        NodeName = NodeName,
        CategoryFormulaA1 = CategoryFormulaA1,
        NodeNameFormulaA1 = NodeNameFormulaA1
    };
}

public sealed class ResearchNodeRow
{
    public string RowIdentity { get; set; } = Guid.NewGuid().ToString("N");
    public int SourceRowNumber { get; set; }
    public string Id { get; set; } = "";
    public string ExportId { get; set; } = "";
    public string ThemeId { get; set; } = "s1";
    public string Category { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string NodeEffectDesc { get; set; } = "";
    public string Image { get; set; } = "";
    public int? NodePermission { get; set; }
    public string ActiveItemId { get; set; } = "";
    public string ActiveItemValue { get; set; } = "";
    public double? TotalRequiredHelper { get; set; }
    public int? ActiveStep { get; set; }
    public int Column { get; set; }
    public int Row { get; set; }
    public string ConditionNode1 { get; set; } = "";
    public string ConditionNode2 { get; set; } = "";
    public string ConditionNode3 { get; set; } = "";
    public string ConditionNode4 { get; set; } = "";
    public string ConditionNode5 { get; set; } = "";
    public string NexusEffectId { get; set; } = "";
    public string IdFormulaA1 { get; set; } = "";
    public string CategoryNameFormulaA1 { get; set; } = "";
    public string NodeEffectDescFormulaA1 { get; set; } = "";
    public string NexusEffectIdFormulaA1 { get; set; } = "";

    public IReadOnlyList<string> Conditions =>
        [ConditionNode1, ConditionNode2, ConditionNode3, ConditionNode4, ConditionNode5];

    public void SetCondition(int index, string? value)
    {
        var sanitized = value ?? "";
        switch (index)
        {
            case 0: ConditionNode1 = sanitized; break;
            case 1: ConditionNode2 = sanitized; break;
            case 2: ConditionNode3 = sanitized; break;
            case 3: ConditionNode4 = sanitized; break;
            case 4: ConditionNode5 = sanitized; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public ResearchNodeRow DeepClone() => new()
    {
        RowIdentity = RowIdentity,
        SourceRowNumber = SourceRowNumber,
        Id = Id,
        ExportId = ExportId,
        ThemeId = ThemeId,
        Category = Category,
        CategoryName = CategoryName,
        NodeEffectDesc = NodeEffectDesc,
        Image = Image,
        NodePermission = NodePermission,
        ActiveItemId = ActiveItemId,
        ActiveItemValue = ActiveItemValue,
        TotalRequiredHelper = TotalRequiredHelper,
        ActiveStep = ActiveStep,
        Column = Column,
        Row = Row,
        ConditionNode1 = ConditionNode1,
        ConditionNode2 = ConditionNode2,
        ConditionNode3 = ConditionNode3,
        ConditionNode4 = ConditionNode4,
        ConditionNode5 = ConditionNode5,
        NexusEffectId = NexusEffectId,
        IdFormulaA1 = IdFormulaA1,
        CategoryNameFormulaA1 = CategoryNameFormulaA1,
        NodeEffectDescFormulaA1 = NodeEffectDescFormulaA1,
        NexusEffectIdFormulaA1 = NexusEffectIdFormulaA1
    };
}

public sealed class ResearchEffectRow
{
    private ResearchCellValue[] _cells = Enumerable.Range(0, 10)
        .Select(_ => ResearchCellValue.Blank())
        .ToArray();

    public string RowIdentity { get; set; } = Guid.NewGuid().ToString("N");
    public int SourceRowNumber { get; set; }
    // Runtime-only association used to preserve legacy DB pairs whose effect ID coordinate is offset.
    public string LinkedNodeRowIdentity { get; set; } = "";
    public IReadOnlyList<ResearchCellValue> Cells => _cells;

    public string Id { get => GetText(0); set => SetText(0, value); }
    public string ExportId { get => GetText(1); set => SetText(1, value); }
    public string ParentEffect { get => GetText(2); set => SetText(2, value); }
    public string GroupMemo { get => GetText(3); set => SetText(3, value); }
    public string Memo { get => GetText(4); set => SetText(4, value); }
    public string Type { get => GetText(5); set => SetText(5, value); }
    public string Condition { get => GetText(6); set => SetText(6, value); }
    public ResearchCellValue ValueCell { get => _cells[7]; set => _cells[7] = value?.DeepClone() ?? ResearchCellValue.Blank(); }
    public string ValueText { get => GetText(7); set => _cells[7] = ResearchCellValue.FromEditedText(value, _cells[7]); }
    public ResearchCellValue TestCell { get => _cells[8]; set => _cells[8] = value?.DeepClone() ?? ResearchCellValue.Blank(); }
    public string Test { get => GetText(8); set => _cells[8] = ResearchCellValue.FromEditedText(value, _cells[8]); }
    public ResearchCellValue ExtraCell { get => _cells[9]; set => _cells[9] = value?.DeepClone() ?? ResearchCellValue.Blank(); }
    public string Extra { get => GetText(9); set => _cells[9] = ResearchCellValue.FromEditedText(value, _cells[9]); }

    public ResearchCellValue GetCell(int zeroBasedColumn) => _cells[zeroBasedColumn];

    public void SetCell(int zeroBasedColumn, ResearchCellValue value)
    {
        if (zeroBasedColumn is < 0 or >= 10)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedColumn));
        _cells[zeroBasedColumn] = value?.DeepClone() ?? ResearchCellValue.Blank();
    }

    public ResearchEffectRow DeepClone()
    {
        var clone = new ResearchEffectRow
        {
            RowIdentity = RowIdentity,
            SourceRowNumber = SourceRowNumber,
            LinkedNodeRowIdentity = LinkedNodeRowIdentity
        };
        clone._cells = _cells.Select(cell => cell.DeepClone()).ToArray();
        return clone;
    }

    private string GetText(int index) => _cells[index].DisplayText;

    private void SetText(int index, string? value) =>
        _cells[index] = ResearchCellValue.FromText(value, _cells[index].NumberFormat);
}

public sealed class ResearchWorkbookContext
{
    public string SourceOutSystemPath { get; set; } = "";
    public string SourceEffectPath { get; set; } = "";
    public string SourceOutSystemSignature { get; set; } = "";
    public string SourceEffectSignature { get; set; } = "";
    public List<ResearchCategoryRow> Categories { get; } = [];
    public List<ResearchNodeRow> Nodes { get; } = [];
    public List<ResearchEffectRow> Effects { get; } = [];
    public ResearchWorkbookContext? OriginalSnapshot { get; internal set; }

    public ResearchWorkbookContext DeepClone(bool includeOriginalSnapshot = true)
    {
        var clone = new ResearchWorkbookContext
        {
            SourceOutSystemPath = SourceOutSystemPath,
            SourceEffectPath = SourceEffectPath,
            SourceOutSystemSignature = SourceOutSystemSignature,
            SourceEffectSignature = SourceEffectSignature
        };
        clone.Categories.AddRange(Categories.Select(row => row.DeepClone()));
        clone.Nodes.AddRange(Nodes.Select(row => row.DeepClone()));
        clone.Effects.AddRange(Effects.Select(row => row.DeepClone()));
        if (includeOriginalSnapshot && OriginalSnapshot is not null)
            clone.OriginalSnapshot = OriginalSnapshot.DeepClone(includeOriginalSnapshot: false);
        return clone;
    }

    public void ReplaceDataFrom(ResearchWorkbookContext source, bool copyOriginalSnapshot = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        SourceOutSystemPath = source.SourceOutSystemPath;
        SourceEffectPath = source.SourceEffectPath;
        SourceOutSystemSignature = source.SourceOutSystemSignature;
        SourceEffectSignature = source.SourceEffectSignature;
        Categories.Clear();
        Categories.AddRange(source.Categories.Select(row => row.DeepClone()));
        Nodes.Clear();
        Nodes.AddRange(source.Nodes.Select(row => row.DeepClone()));
        Effects.Clear();
        Effects.AddRange(source.Effects.Select(row => row.DeepClone()));
        if (copyOriginalSnapshot)
            OriginalSnapshot = source.OriginalSnapshot?.DeepClone(includeOriginalSnapshot: false);
    }

    public ResearchCategoryRow? FindCategory(string? category) =>
        Categories.FirstOrDefault(row => string.Equals(row.Category, category, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(row.CategoryKey, category, StringComparison.OrdinalIgnoreCase));

    public ResearchNodeRow? FindNode(string? nodeId) =>
        Nodes.FirstOrDefault(row => string.Equals(row.Id, nodeId, StringComparison.OrdinalIgnoreCase));

    public ResearchEffectRow? FindEffect(string? effectId) =>
        Effects.FirstOrDefault(row => string.Equals(row.Id, effectId, StringComparison.OrdinalIgnoreCase));

    public ResearchEffectRow? FindEffect(ResearchNodeRow node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var exact = Effects.FirstOrDefault(effect =>
            string.Equals(effect.Id, node.NexusEffectId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(effect.LinkedNodeRowIdentity)
                || string.Equals(effect.LinkedNodeRowIdentity, node.RowIdentity, StringComparison.OrdinalIgnoreCase)));
        return exact ?? Effects.FirstOrDefault(effect =>
            string.Equals(effect.LinkedNodeRowIdentity, node.RowIdentity, StringComparison.OrdinalIgnoreCase));
    }
}

public enum ResearchValidationSeverity
{
    Error,
    Warning
}

public sealed class ResearchValidationIssue
{
    public ResearchValidationSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string Sheet { get; set; } = "";
    public string RowIdentity { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Column { get; set; } = "";

    public override string ToString() => $"{Severity}: {Message}";
}

public enum ResearchDiffChangeType
{
    Add,
    Modify,
    Delete
}

public sealed class ResearchDiffEntry
{
    public string Sheet { get; set; } = "";
    public string RowIdentity { get; set; } = "";
    public string Key { get; set; } = "";
    public string OriginalKey { get; set; } = "";
    public ResearchDiffChangeType ChangeType { get; set; }
    public List<string> ChangedColumns { get; } = [];
    public string BeforeValue { get; set; } = "";
    public string AfterValue { get; set; } = "";

    public override string ToString() => $"[{Sheet}] {ChangeType} {Key}";
}

public sealed class ResearchMutationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public List<string> AffectedRowIdentities { get; } = [];

    public static ResearchMutationResult Failed(string message) => new() { Success = false, Message = message };
}

public sealed class ResearchSaveOptions
{
    public bool CreateBackup { get; set; } = true;
    public bool ValidateBeforeSave { get; set; } = true;
    public string? ChangedRowsExportId { get; set; }
    public IReadOnlyCollection<string>? ExportRowIdentities { get; set; }
}

public sealed class ResearchSaveResult
{
    public string OutSystemPath { get; init; } = "";
    public string EffectPath { get; init; } = "";
    public string? OutSystemBackupPath { get; init; }
    public string? EffectBackupPath { get; init; }
    public int ChangedRowCount { get; init; }
    public List<ResearchValidationIssue> ValidationIssues { get; } = [];
    public List<ResearchValidationIssue> NewValidationIssues { get; } = [];
}

public sealed class ResearchRoundTripResult
{
    public bool Success => FirstPassDifferences.Count == 0
                           && SecondPassDifferences.Count == 0
                           && NewValidationIssues.Count == 0;
    public List<string> FirstPassDifferences { get; } = [];
    public List<string> SecondPassDifferences { get; } = [];
    public List<ResearchValidationIssue> ValidationIssues { get; } = [];
    public List<ResearchValidationIssue> NewValidationIssues { get; } = [];
    public int BaselineValidationErrorCount { get; set; }
    public int ReloadValidationErrorCount { get; set; }
}

public sealed class ResearchWorkbookValidationException : IOException
{
    public ResearchWorkbookValidationException(
        IEnumerable<ResearchValidationIssue> issues,
        string message = "연구 노드 워크북 검증에 실패했습니다.")
        : base(BuildMessage(message, issues))
    {
        Issues = issues.ToList().AsReadOnly();
    }

    public IReadOnlyList<ResearchValidationIssue> Issues { get; }

    private static string BuildMessage(string message, IEnumerable<ResearchValidationIssue> issues)
    {
        var materialized = issues.ToList();
        var details = string.Join(Environment.NewLine, materialized.Take(20).Select(issue => issue.ToString()));
        return materialized.Count == 0 ? message : $"{message}{Environment.NewLine}{details}";
    }
}
