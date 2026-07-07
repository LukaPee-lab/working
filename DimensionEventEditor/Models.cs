using System.Globalization;

namespace DimensionEventEditor;

public sealed class EventWorkbook
{
    public string SourcePath { get; set; } = "";
    public List<EventBaseRow> Events { get; } = [];
    public List<ChoiceGroupRow> Groups { get; } = [];
    public List<EventChoiceRow> Choices { get; } = [];
    public List<TextEntry> TextEntries { get; } = [];
    public Dictionary<string, NodeLayout> Layouts { get; } = [];
    public Dictionary<string, ColumnHelp> ColumnHelps { get; } = [];
}

public sealed class EventBaseRow
{
    public string Id { get; set; } = "";
    public string Memo { get; set; } = "";
    public string ExportId { get; set; } = EventWorkbookService.DefaultEventExportId;
    public string EventNameTid { get; set; } = "";
    public string Rarity { get; set; } = "Common";
    public string FloorRestriction { get; set; } = "";
    public string DiffRestriction { get; set; } = "";
    public int Weight { get; set; } = 10;
    public string FirstGroupId { get; set; } = "";

    public override string ToString() => $"{Id}  {Memo}";
}

public sealed class ChoiceGroupRow
{
    public string Id { get; set; } = "";
    public string Memo { get; set; } = "";
    public string ExportId { get; set; } = EventWorkbookService.DefaultEventExportId;
    public string EventId { get; set; } = "";
    public string Background { get; set; } = "dimension_spiral";
    public string NpcId { get; set; } = "";
    public string SituationTextTid { get; set; } = "";
    public string NextAction { get; set; } = "choice";
    public string StageId { get; set; } = "";
}

public sealed class EventChoiceRow
{
    public string Id { get; set; } = "";
    public string Memo { get; set; } = "";
    public string ExportId { get; set; } = EventWorkbookService.DefaultEventExportId;
    public string GroupId { get; set; } = "";
    public int Seq { get; set; } = 1;
    public string ChoiceTextTid { get; set; } = "";
    public string CostType { get; set; } = "none";
    public int? CostAmount { get; set; }
    public int? SuccessRate { get; set; }
    public string SuccessRewardType { get; set; } = "none";
    public int? SuccessRewardAmount { get; set; }
    public string SuccessNextGroupId { get; set; } = "";
    public string FailRewardType { get; set; } = "none";
    public int? FailRewardAmount { get; set; }
    public string FailNextGroupId { get; set; } = "";
}

public sealed class TextEntry
{
    public string ExportId { get; set; } = EventWorkbookService.DefaultTextExportId;
    public string Tid { get; set; } = "";
    public string Text { get; set; } = "";
    public string Comment { get; set; } = "";
}

public sealed class NodeLayout
{
    public string EventId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 260;
    public double Height { get; set; } = 132;
}

public sealed class EventGraphPreview
{
    public string EventId { get; set; } = "";
    public string EventName { get; set; } = "";
    public List<EventGraphPreviewNode> Nodes { get; } = [];
    public List<EventGraphPreviewLink> Links { get; } = [];
}

public sealed class EventGraphPreviewNode
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "scene";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Rows { get; } = [];
    public List<string> RowIds { get; } = [];
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 260;
    public double Height { get; set; } = 120;
    public bool Changed { get; set; }
}

public sealed class EventGraphPreviewLink
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Changed { get; set; }
}

public sealed class ColumnHelp
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public string Type { get; set; } = "";
    public string Required { get; set; } = "";
    public string Description { get; set; } = "";
    public string Example { get; set; } = "";

    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(Description))
                lines.Add(Description);
            if (!string.IsNullOrWhiteSpace(Type) || !string.IsNullOrWhiteSpace(Required))
                lines.Add($"type: {Type} / required: {Required}");
            if (!string.IsNullOrWhiteSpace(Example))
                lines.Add($"example: {Example}");
            return string.Join(Environment.NewLine, lines);
        }
    }
}

public sealed class DiffEntry
{
    public string Sheet { get; set; } = "";
    public string Key { get; set; } = "";
    public string ChangeType { get; set; } = "";
    public string Detail { get; set; } = "";
    public string BeforeValue { get; set; } = "";
    public string AfterValue { get; set; } = "";
    public bool CanRevert { get; set; } = true;

    public override string ToString() => $"[{Sheet}] {ChangeType} {Key}";
}

public enum ValidationSeverity
{
    Error,
    Warning
}

public sealed class ValidationIssue
{
    public ValidationSeverity Severity { get; set; }
    public string Message { get; set; } = "";

    public override string ToString() => $"{Severity}: {Message}";
}

public sealed class ConsoleLogEntry
{
    public string Severity { get; set; } = "Info";
    public string Message { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string TargetKind { get; set; } = "";

    public bool IsNavigable => !string.IsNullOrWhiteSpace(TargetId);

    public override string ToString() => Message;
}

public sealed class AppSettings
{
    public string? EventWorkbookPath { get; set; }
    public string? ReposRoot { get; set; }
    public string? PlayerExePath { get; set; }
    public string? BackgroundImageRoot { get; set; }
    public string? LastBackgroundImagePath { get; set; }
    public double LeftPaneWidth { get; set; } = 250;
    public double RightPaneWidth { get; set; } = 640;
    public double HierarchyPaneWidth { get; set; } = 270;
    public double ConsoleHeight { get; set; } = 150;
    public string LastExportId { get; set; } = EventWorkbookService.DefaultEventExportId;
}

public static class ParseUtil
{
    public static int? NullableInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
