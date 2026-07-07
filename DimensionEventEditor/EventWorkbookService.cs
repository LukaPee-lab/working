using System.Globalization;
using System.IO;
using ClosedXML.Excel;

namespace DimensionEventEditor;

public static class EventWorkbookService
{
    public const string BaseSheetName = "nexus_event_base";
    public const string GroupSheetName = "nexus_event_choice_group";
    public const string ChoiceSheetName = "nexus_event_choice";
    public const string TextSheetName = "텍스트";
    public const string LayoutSheetName = "이벤트툴_레이아웃";
    public const string DefaultEventExportId = "manmo2429_175126";
    public const string DefaultTextExportId = "251023.lbh9517_142227";

    public static EventWorkbook Load(string path)
    {
        using var workbook = OpenWorkbookSnapshot(path);
        var model = new EventWorkbook { SourcePath = path };
        LoadBase(workbook.Worksheet(BaseSheetName), model);
        LoadGroups(workbook.Worksheet(GroupSheetName), model);
        LoadChoices(workbook.Worksheet(ChoiceSheetName), model);
        if (workbook.Worksheets.TryGetWorksheet(TextSheetName, out var textSheet))
            LoadText(textSheet, model);
        if (TryGetLayoutWorksheet(workbook, out var layoutSheet))
            LoadLayout(layoutSheet, model);
        if (workbook.Worksheets.TryGetWorksheet("매뉴얼", out var manualSheet))
            LoadManual(manualSheet, model);
        return model;
    }

    public static XLWorkbook OpenWorkbookSnapshot(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var copy = new MemoryStream();
        source.CopyTo(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    public static List<ValidationIssue> Validate(EventWorkbook workbook)
    {
        var issues = new List<ValidationIssue>();
        var eventIds = workbook.Events.Select(e => e.Id).Where(NotBlank).ToList();
        var groupIds = workbook.Groups.Select(g => g.Id).Where(NotBlank).ToList();
        var choiceIds = workbook.Choices.Select(c => c.Id).Where(NotBlank).ToList();
        var eventSet = eventIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groupSet = groupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groupById = workbook.Groups.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        AddDuplicates(issues, "event", eventIds);
        AddDuplicates(issues, "group", groupIds);
        AddDuplicates(issues, "choice", choiceIds);

        foreach (var evt in workbook.Events)
        {
            if (Blank(evt.Id))
                issues.Add(Error("event id가 비어 있습니다."));
            if (Blank(evt.Memo))
                issues.Add(Error($"{evt.Id}: 이벤트명 메모가 비어 있습니다."));
            if (Blank(evt.EventNameTid))
                issues.Add(Error($"{evt.Id}: event_name TID가 비어 있습니다."));
            if (Blank(evt.FirstGroupId) || !groupSet.Contains(evt.FirstGroupId))
                issues.Add(Error($"{evt.Id}: first_group_id가 존재하지 않습니다. ({evt.FirstGroupId})"));
        }

        foreach (var group in workbook.Groups)
        {
            if (Blank(group.Id))
                issues.Add(Error("group id가 비어 있습니다."));
            if (!eventSet.Contains(group.EventId))
                issues.Add(Error($"{group.Id}: event_id가 base에 없습니다. ({group.EventId})"));
            if (Blank(group.Memo))
                issues.Add(Error($"{group.Id}: 상황 메모가 비어 있습니다."));
            if (Blank(group.SituationTextTid))
                issues.Add(Error($"{group.Id}: situation_text TID가 비어 있습니다."));
            if (group.NextAction == "choice" && workbook.Choices.All(c => !Same(c.GroupId, group.Id)))
                issues.Add(Warning($"{group.Id}: next_action=choice지만 선택지가 없습니다."));
            if (Same(group.NextAction, "battle") && Blank(group.StageId))
                issues.Add(Error($"{group.Id}: next_action=battle인데 stage_id가 없습니다."));
            if (!Same(group.NextAction, "battle") && NotBlank(group.StageId))
                issues.Add(Warning($"{group.Id}: stage_id가 있지만 next_action이 battle이 아닙니다. ({group.NextAction})"));
        }

        foreach (var choice in workbook.Choices)
        {
            if (!groupSet.Contains(choice.GroupId))
                issues.Add(Error($"{choice.Id}: group_id가 존재하지 않습니다. ({choice.GroupId})"));
            if (Blank(choice.Memo))
                issues.Add(Error($"{choice.Id}: 선택지 메모가 비어 있습니다."));
            if (Blank(choice.ChoiceTextTid))
                issues.Add(Error($"{choice.Id}: choice_text TID가 비어 있습니다."));
            if (!Blank(choice.SuccessNextGroupId) && !groupSet.Contains(choice.SuccessNextGroupId))
                issues.Add(Error($"{choice.Id}: success_next_group_id가 존재하지 않습니다. ({choice.SuccessNextGroupId})"));
            if (!Blank(choice.FailNextGroupId) && !groupSet.Contains(choice.FailNextGroupId))
                issues.Add(Error($"{choice.Id}: fail_next_group_id가 존재하지 않습니다. ({choice.FailNextGroupId})"));
            if (choice.CostType != "none" && choice.CostAmount is null)
                issues.Add(Error($"{choice.Id}: cost_type이 {choice.CostType}인데 cost_amount가 없습니다."));
            if (choice.SuccessRewardType != "none" && choice.SuccessRewardAmount is null)
                issues.Add(Error($"{choice.Id}: success_reward_type이 {choice.SuccessRewardType}인데 amount가 없습니다."));
            if (choice.FailRewardType != "none" && choice.FailRewardAmount is null)
                issues.Add(Error($"{choice.Id}: fail_reward_type이 {choice.FailRewardType}인데 amount가 없습니다."));
            var groupEndsWithExit = groupById.TryGetValue(choice.GroupId, out var group)
                && Same(group.NextAction, "exit");
            if (Blank(choice.SuccessNextGroupId) && choice.SuccessRewardType == "none" && !groupEndsWithExit && !HasChoiceExit(workbook, choice.Id, "success"))
                issues.Add(Warning($"{choice.Id}: 성공 보상도 다음 group도 없습니다."));
        }

        foreach (var duplicateTid in workbook.TextEntries.Where(t => NotBlank(t.Tid))
                     .GroupBy(t => t.Tid, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            issues.Add(Warning($"텍스트 시트에 TID가 중복됩니다: {duplicateTid}"));
        }

        return issues;
    }

    public static List<DiffEntry> BuildDiff(EventWorkbook edited)
    {
        var original = File.Exists(edited.SourcePath) ? Load(edited.SourcePath) : new EventWorkbook();
        var diff = new List<DiffEntry>();
        CompareRows(diff, BaseSheetName, SnapshotBase(original), SnapshotBase(edited));
        CompareRows(diff, GroupSheetName, SnapshotGroups(original), SnapshotGroups(edited));
        CompareRows(diff, ChoiceSheetName, SnapshotChoices(original), SnapshotChoices(edited));
        CompareRows(diff, TextSheetName, SnapshotText(GenerateTextEntries(original)), SnapshotText(GenerateTextEntries(edited)));
        var existingTextTids = original.TextEntries.Select(t => t.Tid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingTextCount = GenerateTextEntries(edited).Count(t => !existingTextTids.Contains(t.Tid));
        if (missingTextCount > 0)
        {
            diff.Add(new DiffEntry
            {
                Sheet = TextSheetName,
                Key = "missing generated TIDs",
                ChangeType = "SYNC",
                Detail = $"텍스트 시트에 없는 DB 파생 TID {missingTextCount}건을 보강",
                BeforeValue = $"현재 텍스트 시트에 없는 DB 파생 TID: {missingTextCount}건",
                AfterValue = "Export 시 누락 TID를 텍스트 시트에 추가",
                CanRevert = false
            });
        }
        CompareRows(diff, LayoutSheetName, SnapshotLayout(original), SnapshotLayout(edited));
        return diff;
    }

    public static bool RevertDiff(EventWorkbook edited, DiffEntry entry)
    {
        if (!entry.CanRevert || !File.Exists(edited.SourcePath))
            return false;

        var original = Load(edited.SourcePath);
        return entry.Sheet switch
        {
            BaseSheetName => RevertBaseRow(edited, original, entry),
            GroupSheetName => RevertGroupRow(edited, original, entry),
            ChoiceSheetName => RevertChoiceRow(edited, original, entry),
            LayoutSheetName => RevertLayoutRow(edited, original, entry),
            _ => false
        };
    }

    public static void ApplyExportId(EventWorkbook workbook, IEnumerable<DiffEntry> entries, string exportId)
    {
        if (Blank(exportId))
            return;

        foreach (var entry in entries)
        {
            if (entry.Sheet == BaseSheetName)
            {
                var row = workbook.Events.FirstOrDefault(e => Same(e.Id, entry.Key));
                if (row is not null)
                    row.ExportId = exportId;
            }
            else if (entry.Sheet == GroupSheetName)
            {
                var row = workbook.Groups.FirstOrDefault(g => Same(g.Id, entry.Key));
                if (row is not null)
                    row.ExportId = exportId;
            }
            else if (entry.Sheet == ChoiceSheetName)
            {
                var row = workbook.Choices.FirstOrDefault(c => Same(c.Id, entry.Key));
                if (row is not null)
                    row.ExportId = exportId;
            }
        }
    }

    public static void SaveAs(EventWorkbook model, string outputPath, bool createBackup)
    {
        if (createBackup && File.Exists(outputPath))
        {
            var backup = Path.Combine(Path.GetDirectoryName(outputPath) ?? "",
                $"{Path.GetFileNameWithoutExtension(outputPath)}.backup_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(outputPath)}");
            File.Copy(outputPath, backup, overwrite: false);
        }

        using var workbook = File.Exists(model.SourcePath) ? OpenWorkbookSnapshot(model.SourcePath) : new XLWorkbook();
        WriteBase(EnsureSheet(workbook, BaseSheetName), model);
        WriteGroups(EnsureSheet(workbook, GroupSheetName), model);
        WriteChoices(EnsureSheet(workbook, ChoiceSheetName), model);
        WriteText(EnsureSheet(workbook, TextSheetName), model);
        WriteLayout(NormalizeLayoutSheet(workbook), model);
        workbook.SaveAs(outputPath);
    }

    public static string NextEventId(EventWorkbook workbook)
    {
        var max = workbook.Events
            .Select(e => e.Id)
            .Select(id => id.StartsWith("s1_EVT_", StringComparison.OrdinalIgnoreCase) && int.TryParse(id[7..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"s1_EVT_{max + 1:000}";
    }

    public static List<TextEntry> GenerateTextEntries(EventWorkbook workbook)
    {
        var entries = new List<TextEntry>();
        entries.AddRange(workbook.Events.Select(e => new TextEntry
        {
            ExportId = DefaultTextExportId,
            Tid = e.EventNameTid,
            Text = e.Memo,
            Comment = $"이벤트명: {e.Id}"
        }));
        entries.AddRange(workbook.Groups.Select(g => new TextEntry
        {
            ExportId = DefaultTextExportId,
            Tid = g.SituationTextTid,
            Text = g.Memo,
            Comment = $"상황 설명: {g.Id}"
        }));
        entries.AddRange(workbook.Choices.Select(c => new TextEntry
        {
            ExportId = DefaultTextExportId,
            Tid = c.ChoiceTextTid,
            Text = c.Memo,
            Comment = $"선택지: {c.Id}"
        }));
        return entries.Where(e => NotBlank(e.Tid)).ToList();
    }

    private static void LoadBase(IXLWorksheet ws, EventWorkbook model)
    {
        for (var row = 4; row <= LastRow(ws); row++)
        {
            var id = Cell(ws, row, 1);
            if (Blank(id))
                continue;
            model.Events.Add(new EventBaseRow
            {
                Id = id,
                Memo = Cell(ws, row, 2),
                ExportId = Default(Cell(ws, row, 3), DefaultEventExportId),
                EventNameTid = Cell(ws, row, 4),
                Rarity = Default(Cell(ws, row, 5), "Common"),
                FloorRestriction = Cell(ws, row, 6),
                DiffRestriction = Cell(ws, row, 7),
                Weight = Int(Cell(ws, row, 8), 10),
                FirstGroupId = Cell(ws, row, 9)
            });
        }
    }

    private static void LoadGroups(IXLWorksheet ws, EventWorkbook model)
    {
        for (var row = 4; row <= LastRow(ws); row++)
        {
            var id = Cell(ws, row, 1);
            if (Blank(id))
                continue;
            model.Groups.Add(new ChoiceGroupRow
            {
                Id = id,
                Memo = Cell(ws, row, 2),
                ExportId = Default(Cell(ws, row, 3), DefaultEventExportId),
                EventId = Cell(ws, row, 4),
                Background = Default(Cell(ws, row, 5), "dimension_spiral"),
                NpcId = Cell(ws, row, 6),
                SituationTextTid = Cell(ws, row, 7),
                NextAction = Default(Cell(ws, row, 8), "choice"),
                StageId = Cell(ws, row, 9)
            });
        }
    }

    private static void LoadChoices(IXLWorksheet ws, EventWorkbook model)
    {
        for (var row = 4; row <= LastRow(ws); row++)
        {
            var id = Cell(ws, row, 1);
            if (Blank(id))
                continue;
            model.Choices.Add(new EventChoiceRow
            {
                Id = id,
                Memo = Cell(ws, row, 2),
                ExportId = Default(Cell(ws, row, 3), DefaultEventExportId),
                GroupId = Cell(ws, row, 4),
                Seq = Int(Cell(ws, row, 5), 1),
                ChoiceTextTid = Cell(ws, row, 6),
                CostType = Default(Cell(ws, row, 7), "none"),
                CostAmount = ParseUtil.NullableInt(Cell(ws, row, 8)),
                SuccessRate = ParseUtil.NullableInt(Cell(ws, row, 9)),
                SuccessRewardType = Default(Cell(ws, row, 10), "none"),
                SuccessRewardAmount = ParseUtil.NullableInt(Cell(ws, row, 11)),
                SuccessNextGroupId = Cell(ws, row, 12),
                FailRewardType = Default(Cell(ws, row, 13), "none"),
                FailRewardAmount = ParseUtil.NullableInt(Cell(ws, row, 14)),
                FailNextGroupId = Cell(ws, row, 15)
            });
        }
    }

    private static void LoadText(IXLWorksheet ws, EventWorkbook model)
    {
        var tidCol = 1;
        var textCol = 2;
        var commentCol = 3;
        var exportIdCol = 4;
        var h1 = Cell(ws, 1, 1);
        var h2 = Cell(ws, 1, 2);
        if (h1.Equals("ExportID", StringComparison.OrdinalIgnoreCase) && h2.Equals("TID", StringComparison.OrdinalIgnoreCase))
        {
            exportIdCol = 1;
            tidCol = 2;
            textCol = 3;
            commentCol = 4;
        }

        for (var row = 2; row <= LastRow(ws); row++)
        {
            var tid = Cell(ws, row, tidCol);
            var text = Cell(ws, row, textCol);
            var comment = Cell(ws, row, commentCol);
            var exportId = Cell(ws, row, exportIdCol);
            if (Blank(tid))
                continue;
            model.TextEntries.Add(new TextEntry { ExportId = exportId, Tid = tid, Text = text, Comment = comment });
        }
    }

    private static void LoadManual(IXLWorksheet ws, EventWorkbook model)
    {
        LoadManualSection(ws, model, BaseSheetName, 17, 25);
        LoadManualSection(ws, model, GroupSheetName, 31, 39);
        LoadManualSection(ws, model, ChoiceSheetName, 45, 59);
    }

    private static void LoadManualSection(IXLWorksheet ws, EventWorkbook model, string table, int startRow, int endRow)
    {
        for (var row = startRow; row <= endRow; row++)
        {
            var column = Cell(ws, row, 2);
            if (Blank(column))
                continue;
            var help = new ColumnHelp
            {
                Table = table,
                Column = column,
                Type = Cell(ws, row, 3),
                Required = Cell(ws, row, 4),
                Description = Cell(ws, row, 5),
                Example = Cell(ws, row, 6)
            };
            model.ColumnHelps[$"{table}.{column}"] = help;
        }
    }

    private static void LoadLayout(IXLWorksheet ws, EventWorkbook model)
    {
        for (var row = 2; row <= LastRow(ws); row++)
        {
            var key = Cell(ws, row, 3);
            if (Blank(key))
                continue;
            model.Layouts[key] = new NodeLayout
            {
                EventId = Cell(ws, row, 2),
                GroupId = key,
                X = Double(Cell(ws, row, 4), 0),
                Y = Double(Cell(ws, row, 5), 0),
                Width = Double(Cell(ws, row, 6), 260),
                Height = Double(Cell(ws, row, 7), 132)
            };
        }
    }

    private static void WriteBase(IXLWorksheet ws, EventWorkbook model)
    {
        EnsureBaseHeaders(ws);
        ClearData(ws, 4);
        var row = 4;
        foreach (var e in model.Events.OrderBy(e => e.Id))
        {
            ws.Cell(row, 1).Value = e.Id;
            ws.Cell(row, 2).Value = e.Memo;
            ws.Cell(row, 3).Value = e.ExportId;
            ws.Cell(row, 4).Value = e.EventNameTid;
            ws.Cell(row, 5).Value = e.Rarity;
            ws.Cell(row, 6).Value = e.FloorRestriction;
            ws.Cell(row, 7).Value = e.DiffRestriction;
            ws.Cell(row, 8).Value = e.Weight;
            ws.Cell(row, 9).Value = e.FirstGroupId;
            row++;
        }
    }

    private static void WriteGroups(IXLWorksheet ws, EventWorkbook model)
    {
        EnsureGroupHeaders(ws);
        ClearData(ws, 4);
        var row = 4;
        foreach (var g in model.Groups.OrderBy(g => g.EventId).ThenBy(g => g.Id))
        {
            ws.Cell(row, 1).Value = g.Id;
            ws.Cell(row, 2).Value = g.Memo;
            ws.Cell(row, 3).Value = g.ExportId;
            ws.Cell(row, 4).Value = g.EventId;
            ws.Cell(row, 5).Value = g.Background;
            ws.Cell(row, 6).Value = g.NpcId;
            ws.Cell(row, 7).Value = g.SituationTextTid;
            ws.Cell(row, 8).Value = g.NextAction;
            ws.Cell(row, 9).Value = g.StageId;
            row++;
        }
    }

    private static void WriteChoices(IXLWorksheet ws, EventWorkbook model)
    {
        EnsureChoiceHeaders(ws);
        ClearData(ws, 4);
        var row = 4;
        foreach (var c in model.Choices.OrderBy(c => c.GroupId).ThenBy(c => c.Seq).ThenBy(c => c.Id))
        {
            ws.Cell(row, 1).Value = c.Id;
            ws.Cell(row, 2).Value = c.Memo;
            ws.Cell(row, 3).Value = c.ExportId;
            ws.Cell(row, 4).Value = c.GroupId;
            ws.Cell(row, 5).Value = c.Seq;
            ws.Cell(row, 6).Value = c.ChoiceTextTid;
            ws.Cell(row, 7).Value = c.CostType;
            SetNullable(ws.Cell(row, 8), c.CostAmount);
            SetNullable(ws.Cell(row, 9), c.SuccessRate);
            ws.Cell(row, 10).Value = c.SuccessRewardType;
            SetNullable(ws.Cell(row, 11), c.SuccessRewardAmount);
            ws.Cell(row, 12).Value = c.SuccessNextGroupId;
            ws.Cell(row, 13).Value = c.FailRewardType;
            SetNullable(ws.Cell(row, 14), c.FailRewardAmount);
            ws.Cell(row, 15).Value = c.FailNextGroupId;
            row++;
        }
    }

    private static void WriteText(IXLWorksheet ws, EventWorkbook model)
    {
        ws.Cell(1, 1).Value = "TID";
        ws.Cell(1, 2).Value = "Text";
        ws.Cell(1, 3).Value = "Comment";
        ws.Cell(1, 4).Value = "ExportID";

        var generated = GenerateTextEntries(model)
            .GroupBy(e => e.Tid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var tidToRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var row = 2; row <= LastRow(ws); row++)
        {
            var tid = Cell(ws, row, 1);
            if (NotBlank(tid) && !tidToRow.ContainsKey(tid))
                tidToRow[tid] = row;
        }

        var nextRow = Math.Max(LastRow(ws) + 1, 2);
        foreach (var entry in generated.Values.OrderBy(e => e.Tid))
        {
            if (!tidToRow.TryGetValue(entry.Tid, out var row))
            {
                row = nextRow++;
                tidToRow[entry.Tid] = row;
            }
            ws.Cell(row, 1).Value = entry.Tid;
            ws.Cell(row, 2).Value = entry.Text;
            ws.Cell(row, 3).Value = entry.Comment;
            ws.Cell(row, 4).Value = DefaultTextExportId;
        }
    }

    private static void WriteLayout(IXLWorksheet ws, EventWorkbook model)
    {
        ws.Cell(1, 1).Value = "schema_version";
        ws.Cell(1, 2).Value = "event_id";
        ws.Cell(1, 3).Value = "group_id";
        ws.Cell(1, 4).Value = "x";
        ws.Cell(1, 5).Value = "y";
        ws.Cell(1, 6).Value = "width";
        ws.Cell(1, 7).Value = "height";
        ws.Cell(1, 8).Value = "updated_at";
        ClearData(ws, 2);
        var row = 2;
        foreach (var layout in model.Layouts.Values.OrderBy(l => l.EventId).ThenBy(l => l.GroupId))
        {
            ws.Cell(row, 1).Value = 1;
            ws.Cell(row, 2).Value = layout.EventId;
            ws.Cell(row, 3).Value = layout.GroupId;
            ws.Cell(row, 4).Value = RoundLayout(layout.X);
            ws.Cell(row, 5).Value = RoundLayout(layout.Y);
            ws.Cell(row, 6).Value = RoundLayout(layout.Width);
            ws.Cell(row, 7).Value = RoundLayout(layout.Height);
            ws.Cell(row, 8).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            row++;
        }
        ws.Visibility = XLWorksheetVisibility.Visible;
    }

    private static void CompareRows(List<DiffEntry> diff, string sheet, Dictionary<string, string> before, Dictionary<string, string> after)
    {
        foreach (var removed in before.Keys.Except(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diff.Add(new DiffEntry
            {
                Sheet = sheet,
                Key = removed,
                ChangeType = "DELETE",
                Detail = "원본 행이 삭제됨",
                BeforeValue = before[removed],
                AfterValue = "",
                CanRevert = sheet != TextSheetName
            });
        }
        foreach (var added in after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diff.Add(new DiffEntry
            {
                Sheet = sheet,
                Key = added,
                ChangeType = "ADD",
                Detail = after[added],
                BeforeValue = "",
                AfterValue = after[added],
                CanRevert = sheet != TextSheetName
            });
        }
        foreach (var key in before.Keys.Intersect(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(before[key], after[key], StringComparison.Ordinal))
            {
                diff.Add(new DiffEntry
                {
                    Sheet = sheet,
                    Key = key,
                    ChangeType = "MODIFY",
                    Detail = after[key],
                    BeforeValue = before[key],
                    AfterValue = after[key],
                    CanRevert = sheet != TextSheetName
                });
            }
        }
    }

    private static Dictionary<string, string> SnapshotBase(EventWorkbook workbook) =>
        workbook.Events.ToDictionary(e => e.Id, e => string.Join("|", e.Memo, e.EventNameTid, e.Rarity, e.FloorRestriction, e.DiffRestriction, e.Weight, e.FirstGroupId), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> SnapshotGroups(EventWorkbook workbook) =>
        workbook.Groups.ToDictionary(g => g.Id, g => string.Join("|", g.Memo, g.EventId, g.Background, g.NpcId, g.SituationTextTid, g.NextAction, g.StageId), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> SnapshotChoices(EventWorkbook workbook) =>
        workbook.Choices.ToDictionary(c => c.Id, c => string.Join("|", c.Memo, c.GroupId, c.Seq, c.ChoiceTextTid, c.CostType, c.CostAmount, c.SuccessRate, c.SuccessRewardType, c.SuccessRewardAmount, c.SuccessNextGroupId, c.FailRewardType, c.FailRewardAmount, c.FailNextGroupId), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> SnapshotText(IEnumerable<TextEntry> entries) =>
        entries.Where(e => NotBlank(e.Tid)).GroupBy(e => e.Tid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join("|", g.Last().ExportId, g.Last().Text, g.Last().Comment), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> SnapshotLayout(EventWorkbook workbook) =>
        workbook.Layouts.Values.ToDictionary(l => l.GroupId, l => string.Join("|", l.EventId, RoundLayout(l.X), RoundLayout(l.Y), RoundLayout(l.Width), RoundLayout(l.Height)), StringComparer.OrdinalIgnoreCase);

    private static double RoundLayout(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static bool HasChoiceExit(EventWorkbook workbook, string choiceId, string branch)
        => workbook.Layouts.ContainsKey($"choice_exit|{choiceId}|{branch}");

    private static bool RevertBaseRow(EventWorkbook edited, EventWorkbook original, DiffEntry entry)
    {
        if (entry.ChangeType == "ADD")
        {
            return edited.Events.RemoveAll(e => Same(e.Id, entry.Key)) > 0;
        }

        var source = original.Events.FirstOrDefault(e => Same(e.Id, entry.Key));
        if (source is null)
            return false;

        var target = edited.Events.FirstOrDefault(e => Same(e.Id, entry.Key));
        if (target is null)
            edited.Events.Add(Clone(source));
        else
            Copy(source, target);
        return true;
    }

    private static bool RevertGroupRow(EventWorkbook edited, EventWorkbook original, DiffEntry entry)
    {
        if (entry.ChangeType == "ADD")
        {
            var removed = edited.Groups.RemoveAll(g => Same(g.Id, entry.Key)) > 0;
            edited.Choices.RemoveAll(c => Same(c.GroupId, entry.Key));
            edited.Layouts.Remove(entry.Key);
            return removed;
        }

        var source = original.Groups.FirstOrDefault(g => Same(g.Id, entry.Key));
        if (source is null)
            return false;

        var target = edited.Groups.FirstOrDefault(g => Same(g.Id, entry.Key));
        if (target is null)
            edited.Groups.Add(Clone(source));
        else
            Copy(source, target);
        return true;
    }

    private static bool RevertChoiceRow(EventWorkbook edited, EventWorkbook original, DiffEntry entry)
    {
        if (entry.ChangeType == "ADD")
        {
            return edited.Choices.RemoveAll(c => Same(c.Id, entry.Key)) > 0;
        }

        var source = original.Choices.FirstOrDefault(c => Same(c.Id, entry.Key));
        if (source is null)
            return false;

        var target = edited.Choices.FirstOrDefault(c => Same(c.Id, entry.Key));
        if (target is null)
            edited.Choices.Add(Clone(source));
        else
            Copy(source, target);
        return true;
    }

    private static bool RevertLayoutRow(EventWorkbook edited, EventWorkbook original, DiffEntry entry)
    {
        if (entry.ChangeType == "ADD")
            return edited.Layouts.Remove(entry.Key);

        if (!original.Layouts.TryGetValue(entry.Key, out var source))
            return false;

        edited.Layouts[entry.Key] = Clone(source);
        return true;
    }

    private static EventBaseRow Clone(EventBaseRow source) => new()
    {
        Id = source.Id,
        Memo = source.Memo,
        ExportId = source.ExportId,
        EventNameTid = source.EventNameTid,
        Rarity = source.Rarity,
        FloorRestriction = source.FloorRestriction,
        DiffRestriction = source.DiffRestriction,
        Weight = source.Weight,
        FirstGroupId = source.FirstGroupId
    };

    private static ChoiceGroupRow Clone(ChoiceGroupRow source) => new()
    {
        Id = source.Id,
        Memo = source.Memo,
        ExportId = source.ExportId,
        EventId = source.EventId,
        Background = source.Background,
        NpcId = source.NpcId,
        SituationTextTid = source.SituationTextTid,
        NextAction = source.NextAction,
        StageId = source.StageId
    };

    private static EventChoiceRow Clone(EventChoiceRow source) => new()
    {
        Id = source.Id,
        Memo = source.Memo,
        ExportId = source.ExportId,
        GroupId = source.GroupId,
        Seq = source.Seq,
        ChoiceTextTid = source.ChoiceTextTid,
        CostType = source.CostType,
        CostAmount = source.CostAmount,
        SuccessRate = source.SuccessRate,
        SuccessRewardType = source.SuccessRewardType,
        SuccessRewardAmount = source.SuccessRewardAmount,
        SuccessNextGroupId = source.SuccessNextGroupId,
        FailRewardType = source.FailRewardType,
        FailRewardAmount = source.FailRewardAmount,
        FailNextGroupId = source.FailNextGroupId
    };

    private static NodeLayout Clone(NodeLayout source) => new()
    {
        EventId = source.EventId,
        GroupId = source.GroupId,
        X = source.X,
        Y = source.Y,
        Width = source.Width,
        Height = source.Height
    };

    private static void Copy(EventBaseRow source, EventBaseRow target)
    {
        target.Memo = source.Memo;
        target.ExportId = source.ExportId;
        target.EventNameTid = source.EventNameTid;
        target.Rarity = source.Rarity;
        target.FloorRestriction = source.FloorRestriction;
        target.DiffRestriction = source.DiffRestriction;
        target.Weight = source.Weight;
        target.FirstGroupId = source.FirstGroupId;
    }

    private static void Copy(ChoiceGroupRow source, ChoiceGroupRow target)
    {
        target.Memo = source.Memo;
        target.ExportId = source.ExportId;
        target.EventId = source.EventId;
        target.Background = source.Background;
        target.NpcId = source.NpcId;
        target.SituationTextTid = source.SituationTextTid;
        target.NextAction = source.NextAction;
        target.StageId = source.StageId;
    }

    private static void Copy(EventChoiceRow source, EventChoiceRow target)
    {
        target.Memo = source.Memo;
        target.ExportId = source.ExportId;
        target.GroupId = source.GroupId;
        target.Seq = source.Seq;
        target.ChoiceTextTid = source.ChoiceTextTid;
        target.CostType = source.CostType;
        target.CostAmount = source.CostAmount;
        target.SuccessRate = source.SuccessRate;
        target.SuccessRewardType = source.SuccessRewardType;
        target.SuccessRewardAmount = source.SuccessRewardAmount;
        target.SuccessNextGroupId = source.SuccessNextGroupId;
        target.FailRewardType = source.FailRewardType;
        target.FailRewardAmount = source.FailRewardAmount;
        target.FailNextGroupId = source.FailNextGroupId;
    }

    private static void AddDuplicates(List<ValidationIssue> issues, string label, IEnumerable<string> ids)
    {
        foreach (var duplicate in ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(Error($"{label} id가 중복됩니다: {duplicate.Key}"));
    }

    private static IXLWorksheet EnsureSheet(XLWorkbook workbook, string name) =>
        workbook.Worksheets.TryGetWorksheet(name, out var ws) ? ws : workbook.AddWorksheet(name);

    private static bool TryGetLayoutWorksheet(XLWorkbook workbook, out IXLWorksheet worksheet)
    {
        if (workbook.Worksheets.TryGetWorksheet(LayoutSheetName, out worksheet!))
            return true;

        worksheet = workbook.Worksheets
            .FirstOrDefault(ws => NormalizeSheetName(ws.Name).Equals(LayoutSheetName, StringComparison.OrdinalIgnoreCase))!;
        return worksheet is not null;
    }

    private static IXLWorksheet NormalizeLayoutSheet(XLWorkbook workbook)
    {
        var candidates = workbook.Worksheets
            .Where(ws => NormalizeSheetName(ws.Name).Equals(LayoutSheetName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        IXLWorksheet? target = null;
        if (workbook.Worksheets.TryGetWorksheet(LayoutSheetName, out var exact))
            target = exact;
        else if (candidates.Count > 0)
        {
            target = candidates[0];
            target.Name = LayoutSheetName;
        }
        else
        {
            target = workbook.AddWorksheet(LayoutSheetName);
        }

        foreach (var stale in candidates.Where(ws => !string.Equals(ws.Name, LayoutSheetName, StringComparison.Ordinal)).ToList())
            stale.Delete();

        return target;
    }

    private static string NormalizeSheetName(string name)
    {
        var normalized = name.Trim().Trim('\'');
        while (normalized.StartsWith("-", StringComparison.Ordinal) || normalized.StartsWith("/", StringComparison.Ordinal))
            normalized = normalized[1..].TrimStart();
        return normalized;
    }

    private static void EnsureBaseHeaders(IXLWorksheet ws)
    {
        ws.Cell(3, 1).Value = "id";
        ws.Cell(3, 3).Value = "export_id";
        ws.Cell(3, 4).Value = "event_name";
        ws.Cell(3, 5).Value = "rarity";
        ws.Cell(3, 6).Value = "floor_restriction";
        ws.Cell(3, 7).Value = "diff_restriction";
        ws.Cell(3, 8).Value = "weight";
        ws.Cell(3, 9).Value = "first_group_id";
    }

    private static void EnsureGroupHeaders(IXLWorksheet ws)
    {
        ws.Cell(3, 1).Value = "id";
        ws.Cell(3, 3).Value = "export_id";
        ws.Cell(3, 4).Value = "event_id";
        ws.Cell(3, 5).Value = "background";
        ws.Cell(3, 6).Value = "npc_id";
        ws.Cell(3, 7).Value = "situation_text";
        ws.Cell(3, 8).Value = "next_action";
        ws.Cell(3, 9).Value = "stage_id";
    }

    private static void EnsureChoiceHeaders(IXLWorksheet ws)
    {
        ws.Cell(3, 1).Value = "id";
        ws.Cell(3, 3).Value = "export_id";
        ws.Cell(3, 4).Value = "group_id";
        ws.Cell(3, 5).Value = "seq";
        ws.Cell(3, 6).Value = "choice_text";
        ws.Cell(3, 7).Value = "cost_type";
        ws.Cell(3, 8).Value = "cost_amount";
        ws.Cell(3, 9).Value = "success_rate";
        ws.Cell(3, 10).Value = "success_reward_type";
        ws.Cell(3, 11).Value = "success_reward_amount";
        ws.Cell(3, 12).Value = "success_next_group_id";
        ws.Cell(3, 13).Value = "fail_reward_type";
        ws.Cell(3, 14).Value = "fail_reward_amount";
        ws.Cell(3, 15).Value = "fail_next_group_id";
    }

    private static void ClearData(IXLWorksheet ws, int startRow)
    {
        var last = LastRow(ws);
        if (last >= startRow)
            ws.Rows(startRow, last).Delete();
    }

    private static int LastRow(IXLWorksheet ws) => ws.LastRowUsed()?.RowNumber() ?? 1;
    private static string Cell(IXLWorksheet ws, int row, int col) => ws.Cell(row, col).GetString().Trim();
    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
    private static bool NotBlank(string? value) => !Blank(value);
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Default(string value, string fallback) => Blank(value) ? fallback : value;
    private static int Int(string value, int fallback) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static double Double(string value, double fallback) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static ValidationIssue Error(string message) => new() { Severity = ValidationSeverity.Error, Message = message };
    private static ValidationIssue Warning(string message) => new() { Severity = ValidationSeverity.Warning, Message = message };

    private static void SetNullable(IXLCell cell, int? value)
    {
        if (value.HasValue)
            cell.Value = value.Value;
        else
            cell.Clear(XLClearOptions.Contents);
    }
}
