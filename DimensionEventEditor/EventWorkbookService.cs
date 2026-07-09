using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace DimensionEventEditor;

public static class EventWorkbookService
{
    private const string SpreadsheetMainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string BaseSheetName = "nexus_event_base";
    public const string GroupSheetName = "nexus_event_choice_group";
    public const string ChoiceSheetName = "nexus_event_choice";
    public const string TextSheetName = "텍스트";
    public const string LayoutSheetName = "이벤트툴_레이아웃";
    public const string DefaultEventExportId = "manmo2429_175126";
    public const string DefaultTextExportId = "251023.lbh9517_142227";
    public const string BattleResultChoiceSuffix = "_battle_result";

    private static readonly string[] EventInfoSheetNames =
    [
        "이벤트별 요약",
        "보상 정보",
        "비용 정보",
        "이벤트 난이도",
        "전투 정보",
        "밸런스 체크"
    ];

    private static readonly string[] ObsoleteEventInfoSheetNames =
    [
        "흐름 정보"
    ];

    private static readonly string[] ExitMemoVariants =
    [
        "{0}의 여운이 잦아들고 차원 탐사가 마무리된다.",
        "{0}에서 얻은 단서를 정리한 뒤 균열 밖으로 물러난다.",
        "{0}의 흔적이 희미해지며 일행은 다음 탐사를 준비한다.",
        "{0}을 뒤로하고 불안정한 차원의 문이 조용히 닫힌다.",
        "{0}의 기척이 사라지고 주변 공간이 원래의 고요를 되찾는다.",
        "{0}의 마지막 파동을 확인하고 탐사를 종료한다.",
        "{0}의 결말을 마음에 새긴 채 차원의 틈을 빠져나온다.",
        "{0}의 잔상이 흩어지고 이번 조사는 여기서 끝난다.",
        "{0}을 둘러싼 이상 현상이 가라앉으며 발걸음을 돌린다.",
        "{0}의 기록을 남기고 낯선 공간에서 벗어난다."
    ];

    public static EventWorkbook Load(string path, bool normalizeExitTerminals = true)
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
        if (normalizeExitTerminals)
            NormalizeExitTerminals(model);
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
        var groupById = UniqueById(workbook.Groups, g => g.Id);

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
            if (!workbook.Groups.Any(g => Same(g.EventId, evt.Id) && Same(g.NextAction, "exit")))
                issues.Add(Error($"{evt.Id}: 이벤트 종료용 next_action=exit 장면이 없습니다."));
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
            if (Same(group.NextAction, "choice") && workbook.Choices.All(c => !Same(c.GroupId, group.Id)))
                issues.Add(Warning($"{group.Id}: next_action=choice지만 선택지가 없습니다."));
            if (Same(group.NextAction, "exit") && workbook.Choices.Any(c => Same(c.GroupId, group.Id)))
                issues.Add(Warning($"{group.Id}: next_action=exit 장면에 선택지가 남아 있습니다."));
            if (Same(group.NextAction, "battle") && Blank(group.StageId))
                issues.Add(Error($"{group.Id}: next_action=battle인데 stage_id가 없습니다."));
            if (Same(group.NextAction, "battle"))
            {
                var resultChoice = FindBattleResultChoice(workbook, group);
                if (resultChoice is null)
                    issues.Add(Error($"{group.Id}: battle 결과용 선택지 row가 없습니다. ({BattleResultChoiceId(group.Id)})"));
                else if (Same(resultChoice.SuccessRewardType, "none"))
                    issues.Add(Warning($"{group.Id}: battle 성공 보상이 없습니다."));
            }
            if (!Same(group.NextAction, "battle") && NotBlank(group.StageId))
                issues.Add(Warning($"{group.Id}: stage_id가 있지만 next_action이 battle이 아닙니다. ({group.NextAction})"));
        }

        foreach (var evt in workbook.Events)
        {
            var eventGroups = workbook.Groups
                .Where(g => Same(g.EventId, evt.Id))
                .ToList();
            var battleGroupIds = eventGroups
                .Where(g => Same(g.NextAction, "battle"))
                .Select(g => g.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (battleGroupIds.Count == 0)
                continue;

            var sourceGroupIds = workbook.Choices
                .Where(c => groupById.TryGetValue(c.GroupId, out var owner)
                            && Same(owner.EventId, evt.Id)
                            && !IsBattleResultChoice(c, owner)
                            && (battleGroupIds.Contains(c.SuccessNextGroupId) || battleGroupIds.Contains(c.FailNextGroupId)))
                .Select(c => c.GroupId)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceGroupId in sourceGroupIds)
            {
                if (!HasPlainEscapeChoice(workbook, sourceGroupId, groupById))
                    issues.Add(Error($"{sourceGroupId}: battle 진입 전 도망 선택지가 없습니다."));
            }
        }

        foreach (var choice in workbook.Choices)
        {
            if (Blank(choice.Id))
                issues.Add(Error("choice id가 비어 있습니다."));
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
            if (!Same(choice.CostType, "none") && choice.CostAmount is null)
                issues.Add(Error($"{choice.Id}: cost_type이 {choice.CostType}인데 cost_amount가 없습니다."));
            if (!Same(choice.SuccessRewardType, "none") && choice.SuccessRewardAmount is null)
                issues.Add(Error($"{choice.Id}: success_reward_type이 {choice.SuccessRewardType}인데 amount가 없습니다."));
            if (!Same(choice.FailRewardType, "none") && choice.FailRewardAmount is null)
                issues.Add(Error($"{choice.Id}: fail_reward_type이 {choice.FailRewardType}인데 amount가 없습니다."));
            var groupEndsWithExit = groupById.TryGetValue(choice.GroupId, out var group)
                && Same(group.NextAction, "exit");
            var isBattleResult = group is not null && IsBattleResultChoice(choice, group);
            if (Blank(choice.SuccessNextGroupId) && !groupEndsWithExit)
                issues.Add(Error($"{choice.Id}: 성공 경로가 exit 장면에 연결되지 않았습니다."));
            if ((choice.SuccessRate is not null || isBattleResult) && Blank(choice.FailNextGroupId) && !groupEndsWithExit)
                issues.Add(Error($"{choice.Id}: 실패 경로가 exit 장면에 연결되지 않았습니다."));
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
        NormalizeExitTerminals(edited);
        var original = File.Exists(edited.SourcePath) ? Load(edited.SourcePath, normalizeExitTerminals: false) : new EventWorkbook();
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
        return RevertDiff(edited, original, entry);
    }

    public static int RevertDiffs(EventWorkbook edited, IEnumerable<DiffEntry> entries)
    {
        var revertable = entries
            .Where(e => e.CanRevert)
            .ToList();
        if (revertable.Count == 0 || !File.Exists(edited.SourcePath))
            return 0;

        var original = Load(edited.SourcePath);
        var reverted = 0;
        foreach (var entry in revertable)
        {
            if (RevertDiff(edited, original, entry))
                reverted++;
        }
        return reverted;
    }

    private static bool RevertDiff(EventWorkbook edited, EventWorkbook original, DiffEntry entry)
    {
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

    public static void SaveAs(EventWorkbook model, string outputPath, bool createBackup, string? textExportId = null)
    {
        NormalizeExitTerminals(model);

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
        WriteText(EnsureSheet(workbook, TextSheetName), model, textExportId);
        WriteEventInfoSheets(workbook, model);
        WriteLayout(NormalizeLayoutSheet(workbook), model);
        NormalizeManualSheetIdentityExamples(workbook);
        ArrangeWorksheetsForExport(workbook);
        workbook.SaveAs(outputPath);
        FixWorksheetDimensions(outputPath, model);
    }

    public static int NormalizeExitTerminals(EventWorkbook workbook)
    {
        if (HasInvalidIdentityKeys(workbook))
            return 0;

        var changed = 0;
        changed += NormalizeIdentityCasing(workbook);
        changed += NormalizeExitActionGroups(workbook);
        changed += EnsureReferencedGroups(workbook);
        changed += EnsureBattleResultChoices(workbook);
        changed += NormalizeExitGroupIds(workbook);

        changed += EnsureExitMemos(workbook);
        return changed;
    }

    public static string NextEventId(EventWorkbook workbook)
    {
        var max = workbook.Events
            .Select(e => e.Id)
            .Select(id => id.StartsWith("s1_evt_", StringComparison.OrdinalIgnoreCase) && int.TryParse(id[7..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"s1_evt_{max + 1:000}";
    }

    public static List<TextEntry> GenerateTextEntries(EventWorkbook workbook, string? textExportId = null)
    {
        var overrideTextExportId = textExportId?.Trim();
        var existingTexts = workbook.TextEntries
            .Where(e => NotBlank(e.Tid))
            .GroupBy(e => e.Tid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key!, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        string ExportIdFor(string tid, string text)
        {
            existingTexts.TryGetValue(tid, out var existing);
            if (!string.IsNullOrWhiteSpace(existing?.ExportId)
                && string.Equals(existing.Text, text, StringComparison.Ordinal))
            {
                return existing.ExportId;
            }

            if (!string.IsNullOrWhiteSpace(overrideTextExportId))
                return overrideTextExportId;
            return !string.IsNullOrWhiteSpace(existing?.ExportId)
                ? existing.ExportId
                : DefaultTextExportId;
        }

        var entries = new List<TextEntry>();
        entries.AddRange(workbook.Events.Select(e => new TextEntry
        {
            ExportId = ExportIdFor(e.EventNameTid, e.Memo),
            Tid = e.EventNameTid,
            Text = e.Memo,
            Comment = $"이벤트명: {e.Id}"
        }));
        entries.AddRange(workbook.Groups.Select(g => new TextEntry
        {
            ExportId = ExportIdFor(g.SituationTextTid, g.Memo),
            Tid = g.SituationTextTid,
            Text = g.Memo,
            Comment = $"상황 설명: {g.Id}"
        }));
        entries.AddRange(workbook.Choices.Select(c => new TextEntry
        {
            ExportId = ExportIdFor(c.ChoiceTextTid, c.Memo),
            Tid = c.ChoiceTextTid,
            Text = c.Memo,
            Comment = $"선택지: {c.Id}"
        }));
        return entries.Where(e => NotBlank(e.Tid)).ToList();
    }

    public static EventInfoReport BuildEventInfoReport(EventWorkbook workbook)
    {
        var report = new EventInfoReport();
        var events = workbook.Events.Where(e => NotBlank(e.Id)).OrderBy(e => e.Id).ToList();
        var groupsById = UniqueById(workbook.Groups, g => g.Id);
        var eventById = UniqueById(workbook.Events, e => e.Id);
        var groupsByEvent = workbook.Groups
            .Where(g => NotBlank(g.EventId))
            .GroupBy(g => g.EventId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id).ToList(), StringComparer.OrdinalIgnoreCase);
        var choicesByEvent = workbook.Choices
            .Where(c => groupsById.ContainsKey(c.GroupId))
            .GroupBy(c => groupsById[c.GroupId].EventId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.GroupId).ThenBy(c => c.Seq).ThenBy(c => c.Id).ToList(), StringComparer.OrdinalIgnoreCase);

        var rewardBranches = BuildRewardBranches(workbook, eventById, groupsById);
        var costBranches = BuildCostBranches(workbook, eventById, groupsById);
        var visibleChoicesByEvent = choicesByEvent.ToDictionary(
            p => p.Key,
            p => p.Value.Where(c => !IsBattleResultChoice(c, groupsById[c.GroupId])).ToList(),
            StringComparer.OrdinalIgnoreCase);
        AddReportLocations(report, workbook, eventById, rewardBranches, costBranches);

        BuildEventSummaryTable(report, events, groupsByEvent, choicesByEvent, visibleChoicesByEvent, rewardBranches, costBranches);
        BuildRewardInfoTable(report, rewardBranches);
        BuildCostInfoTable(report, costBranches);
        BuildDifficultyInfoTable(report, events, groupsByEvent, choicesByEvent, visibleChoicesByEvent, rewardBranches, costBranches);
        BuildBattleInfoTable(report, workbook, eventById);
        BuildBalanceCheckTable(report, events, groupsByEvent, choicesByEvent, rewardBranches, costBranches);
        return report;
    }

    private static void BuildEventSummaryTable(
        EventInfoReport report,
        List<EventBaseRow> events,
        Dictionary<string, List<ChoiceGroupRow>> groupsByEvent,
        Dictionary<string, List<EventChoiceRow>> choicesByEvent,
        Dictionary<string, List<EventChoiceRow>> visibleChoicesByEvent,
        List<RewardBranchInfo> rewardBranches,
        List<CostBranchInfo> costBranches)
    {
        var table = CreateInfoTable(report, "이벤트별 요약", "이벤트 단위로 보상, 비용, 전투, 분기 밀도를 한 번에 보는 표입니다.",
            "event_id", "event_name", "rarity", "weight", "scene_cnt", "choice_cnt", "reward_branch_cnt",
            "reward_summary", "cost_summary", "battle_cnt", "chance_choice_cnt", "exit_scene_cnt");

        foreach (var evt in events)
        {
            var groups = GetList(groupsByEvent, evt.Id);
            var choices = GetList(choicesByEvent, evt.Id);
            var visibleChoices = GetList(visibleChoicesByEvent, evt.Id);
            var eventRewards = rewardBranches.Where(r => Same(r.EventId, evt.Id)).ToList();
            var eventCosts = costBranches.Where(c => Same(c.EventId, evt.Id)).ToList();
            table.Rows.Add(
            [
                evt.Id,
                evt.Memo,
                evt.Rarity,
                evt.Weight,
                groups.Count,
                visibleChoices.Count,
                eventRewards.Count,
                SummarizeTypedAmounts(eventRewards.Select(r => (r.Type, r.Amount))),
                SummarizeTypedAmounts(eventCosts.Select(c => (c.Type, c.Amount))),
                groups.Count(g => Same(g.NextAction, "battle")),
                visibleChoices.Count(c => c.SuccessRate.HasValue),
                groups.Count(g => Same(g.NextAction, "exit"))
            ]);
        }
    }

    private static void BuildRewardInfoTable(EventInfoReport report, List<RewardBranchInfo> rewardBranches)
    {
        var table = CreateInfoTable(report, "보상 정보", "보상 타입별 지급 횟수와 총량입니다. T/F 및 battle 결과 보상도 모두 포함합니다.",
            "reward_id(type)", "cnt", "amount_total", "amount_avg", "event_cnt", "choice_cnt", "events");

        foreach (var group in rewardBranches
                     .GroupBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key))
        {
            var total = group.Sum(r => r.Amount);
            table.Rows.Add(
            [
                group.Key,
                group.Count(),
                total,
                group.Any() ? Math.Round(total / (double)group.Count(), 2) : 0,
                group.Select(r => r.EventId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                group.Select(r => r.ChoiceId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                JoinLimited(group.Select(r => $"{r.EventId} {r.EventName}").Distinct(StringComparer.OrdinalIgnoreCase), 12)
            ]);
        }
    }

    private static void BuildCostInfoTable(EventInfoReport report, List<CostBranchInfo> costBranches)
    {
        var table = CreateInfoTable(report, "비용 정보", "선택지 비용 타입별 사용 횟수와 총량입니다.",
            "cost_type", "cnt", "amount_total", "amount_avg", "event_cnt", "choice_cnt", "events");

        foreach (var group in costBranches
                     .GroupBy(c => c.Type, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key))
        {
            var total = group.Sum(c => c.Amount);
            table.Rows.Add(
            [
                group.Key,
                group.Count(),
                total,
                group.Any() ? Math.Round(total / (double)group.Count(), 2) : 0,
                group.Select(c => c.EventId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                group.Select(c => c.ChoiceId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                JoinLimited(group.Select(c => $"{c.EventId} {c.EventName}").Distinct(StringComparer.OrdinalIgnoreCase), 12)
            ]);
        }
    }

    private static void BuildDifficultyInfoTable(
        EventInfoReport report,
        List<EventBaseRow> events,
        Dictionary<string, List<ChoiceGroupRow>> groupsByEvent,
        Dictionary<string, List<EventChoiceRow>> choicesByEvent,
        Dictionary<string, List<EventChoiceRow>> visibleChoicesByEvent,
        List<RewardBranchInfo> rewardBranches,
        List<CostBranchInfo> costBranches)
    {
        var table = CreateInfoTable(report, "이벤트 난이도", "이벤트 구조 복잡도와 플레이 부담을 보기 위한 지표입니다. 실제 전투 난이도와는 별도입니다.",
            "event_id", "event_name", "rarity", "floor_restriction", "diff_restriction", "weight",
            "scene_cnt", "choice_cnt", "branch_cnt", "battle_cnt", "chance_choice_cnt", "cost_choice_cnt",
            "reward_branch_cnt", "complexity_score", "complexity_rank");

        foreach (var evt in events)
        {
            var groups = GetList(groupsByEvent, evt.Id);
            var choices = GetList(choicesByEvent, evt.Id);
            var visibleChoices = GetList(visibleChoicesByEvent, evt.Id);
            var rewardCount = rewardBranches.Count(r => Same(r.EventId, evt.Id));
            var costChoiceCount = costBranches.Where(c => Same(c.EventId, evt.Id)).Select(c => c.ChoiceId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var battleCount = groups.Count(g => Same(g.NextAction, "battle"));
            var chanceCount = visibleChoices.Count(c => c.SuccessRate.HasValue);
            var branchCount = visibleChoices.Sum(c => 1 + (c.SuccessRate.HasValue || NotBlank(c.FailNextGroupId) ? 1 : 0));
            var score = groups.Count
                        + visibleChoices.Count * 0.8
                        + branchCount * 0.25
                        + battleCount * 2.0
                        + chanceCount * 1.5
                        + costChoiceCount * 0.7
                        + rewardCount * 0.35;
            table.Rows.Add(
            [
                evt.Id,
                evt.Memo,
                evt.Rarity,
                evt.FloorRestriction,
                evt.DiffRestriction,
                evt.Weight,
                groups.Count,
                visibleChoices.Count,
                branchCount,
                battleCount,
                chanceCount,
                costChoiceCount,
                rewardCount,
                Math.Round(score, 2),
                score >= 16 ? "높음" : score >= 9 ? "보통" : "낮음"
            ]);
        }
    }

    private static void BuildBattleInfoTable(EventInfoReport report, EventWorkbook workbook, Dictionary<string, EventBaseRow> eventById)
    {
        var table = CreateInfoTable(report, "전투 정보", "전투 진입 장면과 stage_id 사용량입니다.",
            "stage_id", "battle_scene_cnt", "event_cnt", "events", "battle_groups");

        var battleGroups = workbook.Groups.Where(g => Same(g.NextAction, "battle")).ToList();
        foreach (var group in battleGroups
                     .GroupBy(g => Default(g.StageId, "(stage_id 없음)"), StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key))
        {
            table.Rows.Add(
            [
                group.Key,
                group.Count(),
                group.Select(g => g.EventId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                JoinLimited(group.Select(g => EventLabel(g.EventId, eventById)).Distinct(StringComparer.OrdinalIgnoreCase), 12),
                JoinLimited(group.Select(g => g.Id), 16)
            ]);
        }
    }

    private static void AddReportLocations(
        EventInfoReport report,
        EventWorkbook workbook,
        Dictionary<string, EventBaseRow> eventById,
        List<RewardBranchInfo> rewardBranches,
        List<CostBranchInfo> costBranches)
    {
        foreach (var reward in rewardBranches)
        {
            report.Locations.Add(new EventInfoLocation
            {
                TableName = "보상 정보",
                Key = reward.Type,
                EventId = reward.EventId,
                EventName = reward.EventName,
                GroupId = reward.GroupId,
                ChoiceId = reward.ChoiceId,
                Branch = reward.Branch,
                NodeKey = RewardLayoutKeyForReport(reward.ChoiceId, reward.Branch),
                Kind = "reward",
                Label = $"{reward.EventId} {reward.EventName}",
                Detail = $"{reward.Type} {reward.Amount} / {reward.GroupId} / {reward.ChoiceId} / {reward.Branch}"
            });
        }

        foreach (var cost in costBranches)
        {
            report.Locations.Add(new EventInfoLocation
            {
                TableName = "비용 정보",
                Key = cost.Type,
                EventId = cost.EventId,
                EventName = cost.EventName,
                GroupId = cost.GroupId,
                ChoiceId = cost.ChoiceId,
                Branch = "choice",
                NodeKey = cost.GroupId,
                Kind = "choice",
                Label = $"{cost.EventId} {cost.EventName}",
                Detail = $"{cost.Type} {cost.Amount} / {cost.GroupId} / {cost.ChoiceId}"
            });
        }

        foreach (var group in workbook.Groups.Where(g => Same(g.NextAction, "battle")))
        {
            var evt = eventById.GetValueOrDefault(group.EventId);
            var eventName = evt?.Memo ?? "";
            report.Locations.Add(new EventInfoLocation
            {
                TableName = "전투 정보",
                Key = Default(group.StageId, "(stage_id 없음)"),
                EventId = group.EventId,
                EventName = eventName,
                GroupId = group.Id,
                NodeKey = BattleLayoutKeyForReport(group.Id),
                Kind = "battle",
                Label = $"{group.EventId} {eventName}",
                Detail = $"{group.Id} / stage={Default(group.StageId, "-")}"
            });
        }
    }

    private static void BuildBalanceCheckTable(
        EventInfoReport report,
        List<EventBaseRow> events,
        Dictionary<string, List<ChoiceGroupRow>> groupsByEvent,
        Dictionary<string, List<EventChoiceRow>> choicesByEvent,
        List<RewardBranchInfo> rewardBranches,
        List<CostBranchInfo> costBranches)
    {
        var table = CreateInfoTable(report, "밸런스 체크", "보상/비용/전투/확률 분기가 어느 이벤트에 몰려 있는지 빠르게 보는 체크 표입니다.",
            "check", "event_cnt", "events");

        void AddCheck(string label, IEnumerable<EventBaseRow> matched)
        {
            var list = matched.OrderBy(e => e.Id).ToList();
            table.Rows.Add([label, list.Count, JoinLimited(list.Select(e => $"{e.Id} {e.Memo}"), 20)]);
            foreach (var evt in list)
            {
                var groupId = evt.FirstGroupId;
                report.Locations.Add(new EventInfoLocation
                {
                    TableName = table.Name,
                    Key = label,
                    EventId = evt.Id,
                    EventName = evt.Memo,
                    GroupId = groupId,
                    NodeKey = groupId,
                    Kind = "event",
                    Label = $"{evt.Id} {evt.Memo}",
                    Detail = label
                });
            }
        }

        AddCheck("보상 없는 이벤트", events.Where(e => rewardBranches.All(r => !Same(r.EventId, e.Id))));
        AddCheck("보상 5회 이상 이벤트", events.Where(e => rewardBranches.Count(r => Same(r.EventId, e.Id)) >= 5));
        AddCheck("유물 보상 포함 이벤트", events.Where(e => rewardBranches.Any(r => Same(r.EventId, e.Id) && ContainsAny(r.Type, "relic", "유물"))));
        AddCheck("골드 보상 포함 이벤트", events.Where(e => rewardBranches.Any(r => Same(r.EventId, e.Id) && ContainsAny(r.Type, "gold", "골드"))));
        AddCheck("횃불/소모품 보상 포함 이벤트", events.Where(e => rewardBranches.Any(r => Same(r.EventId, e.Id) && ContainsAny(r.Type, "torch", "item", "횃불"))));
        AddCheck("비용 선택지 포함 이벤트", events.Where(e => costBranches.Any(c => Same(c.EventId, e.Id))));
        AddCheck("확률 분기 포함 이벤트", events.Where(e => GetList(choicesByEvent, e.Id).Any(c => c.SuccessRate.HasValue)));
        AddCheck("전투 포함 이벤트", events.Where(e => GetList(groupsByEvent, e.Id).Any(g => Same(g.NextAction, "battle"))));
        AddCheck("장면 8개 이상 이벤트", events.Where(e => GetList(groupsByEvent, e.Id).Count >= 8));
    }

    private static List<RewardBranchInfo> BuildRewardBranches(EventWorkbook workbook, Dictionary<string, EventBaseRow> eventById, Dictionary<string, ChoiceGroupRow> groupsById)
    {
        var result = new List<RewardBranchInfo>();
        foreach (var choice in workbook.Choices)
        {
            if (!groupsById.TryGetValue(choice.GroupId, out var group) || !eventById.TryGetValue(group.EventId, out var evt))
                continue;
            AddReward(result, evt, group, choice, "success", choice.SuccessRewardType, choice.SuccessRewardAmount);
            AddReward(result, evt, group, choice, "fail", choice.FailRewardType, choice.FailRewardAmount);
        }

        return result;
    }

    private static void AddReward(List<RewardBranchInfo> result, EventBaseRow evt, ChoiceGroupRow group, EventChoiceRow choice, string branch, string type, int? amount)
    {
        if (Blank(type) || Same(type, "none"))
            return;
        result.Add(new RewardBranchInfo(evt.Id, evt.Memo, group.Id, choice.Id, branch, type, amount ?? 0));
    }

    private static List<CostBranchInfo> BuildCostBranches(EventWorkbook workbook, Dictionary<string, EventBaseRow> eventById, Dictionary<string, ChoiceGroupRow> groupsById)
    {
        var result = new List<CostBranchInfo>();
        foreach (var choice in workbook.Choices)
        {
            if (Blank(choice.CostType) || Same(choice.CostType, "none"))
                continue;
            if (!groupsById.TryGetValue(choice.GroupId, out var group) || !eventById.TryGetValue(group.EventId, out var evt))
                continue;
            result.Add(new CostBranchInfo(evt.Id, evt.Memo, group.Id, choice.Id, choice.CostType, choice.CostAmount ?? 0));
        }

        return result;
    }

    private static EventInfoTable CreateInfoTable(EventInfoReport report, string name, string description, params string[] columns)
    {
        var table = new EventInfoTable { Name = name, Description = description };
        table.Columns.AddRange(columns);
        report.Tables.Add(table);
        return table;
    }

    private static List<T> GetList<T>(Dictionary<string, List<T>> source, string key)
        => source.TryGetValue(key, out var value) ? value : [];

    private static string SummarizeTypedAmounts(IEnumerable<(string Type, int Amount)> values)
    {
        var items = values
            .Where(v => NotBlank(v.Type))
            .GroupBy(v => v.Type, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => $"{g.Key} x{g.Count()} / {g.Sum(v => v.Amount)}")
            .ToList();
        return items.Count == 0 ? "-" : string.Join(", ", items);
    }

    private static string EventLabel(string eventId, Dictionary<string, EventBaseRow> eventById)
        => eventById.TryGetValue(eventId, out var evt) ? $"{evt.Id} {evt.Memo}" : eventId;

    private static string JoinLimited(IEnumerable<string> values, int limit)
    {
        var list = values.Where(NotBlank).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit + 1).ToList();
        if (list.Count == 0)
            return "-";
        if (list.Count > limit)
            return string.Join(", ", list.Take(limit)) + " ...";
        return string.Join(", ", list);
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string RewardLayoutKeyForReport(string choiceId, string branch) => $"reward|{choiceId}|{branch}";

    private static string BattleLayoutKeyForReport(string groupId) => $"battle|{groupId}";

    private sealed record RewardBranchInfo(string EventId, string EventName, string GroupId, string ChoiceId, string Branch, string Type, int Amount);

    private sealed record CostBranchInfo(string EventId, string EventName, string GroupId, string ChoiceId, string Type, int Amount);

    private static int NormalizeIdentityCasing(EventWorkbook workbook)
    {
        var changed = 0;

        void Lower(string value, Action<string> set)
        {
            if (Blank(value))
                return;
            var lowered = value.ToLowerInvariant();
            if (string.Equals(value, lowered, StringComparison.Ordinal))
                return;
            set(lowered);
            changed++;
        }

        foreach (var evt in workbook.Events)
        {
            Lower(evt.Id, v => evt.Id = v);
            Lower(evt.EventNameTid, v => evt.EventNameTid = v);
            Lower(evt.FirstGroupId, v => evt.FirstGroupId = v);
        }

        foreach (var group in workbook.Groups)
        {
            Lower(group.Id, v => group.Id = v);
            Lower(group.EventId, v => group.EventId = v);
            Lower(group.SituationTextTid, v => group.SituationTextTid = v);
        }

        foreach (var choice in workbook.Choices)
        {
            Lower(choice.Id, v => choice.Id = v);
            Lower(choice.GroupId, v => choice.GroupId = v);
            Lower(choice.ChoiceTextTid, v => choice.ChoiceTextTid = v);
            Lower(choice.SuccessNextGroupId, v => choice.SuccessNextGroupId = v);
            Lower(choice.FailNextGroupId, v => choice.FailNextGroupId = v);
        }

        foreach (var text in workbook.TextEntries)
            Lower(text.Tid, v => text.Tid = v);

        if (workbook.Layouts.Count == 0)
            return changed;

        var loweredLayouts = new Dictionary<string, NodeLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, layout) in workbook.Layouts.ToList())
        {
            Lower(layout.EventId, v => layout.EventId = v);
            Lower(layout.GroupId, v => layout.GroupId = v);

            var loweredKey = key.ToLowerInvariant();
            if (!string.Equals(key, loweredKey, StringComparison.Ordinal))
                changed++;
            loweredLayouts[loweredKey] = layout;
        }

        workbook.Layouts.Clear();
        foreach (var (key, layout) in loweredLayouts)
            workbook.Layouts[key] = layout;

        return changed;
    }

    private static int RemoveDeprecatedTerminalLayouts(EventWorkbook workbook)
    {
        var staleKeys = workbook.Layouts.Keys
            .Where(IsDeprecatedTerminalLayout)
            .ToList();
        foreach (var key in staleKeys)
            workbook.Layouts.Remove(key);
        return staleKeys.Count;
    }

    private static bool IsDeprecatedTerminalLayout(string key)
        => key.StartsWith("exit|", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("choice_exit|", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("pending_exit|", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("group_reward|", StringComparison.OrdinalIgnoreCase);

    private static int NormalizeExitActionGroups(EventWorkbook workbook)
    {
        var changed = 0;
        var groupsWithChoices = workbook.Choices
            .Where(c => NotBlank(c.GroupId))
            .Select(c => c.GroupId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in workbook.Groups.Where(g => Same(g.NextAction, "exit") && groupsWithChoices.Contains(g.Id)))
        {
            group.NextAction = "choice";
            if (NotBlank(group.StageId))
                group.StageId = "";
            changed++;
        }

        return changed;
    }

    public static string BattleResultChoiceId(string groupId) => $"{groupId}{BattleResultChoiceSuffix}";

    private static EventChoiceRow? FindBattleResultChoice(EventWorkbook workbook, ChoiceGroupRow group)
        => workbook.Choices.FirstOrDefault(c => Same(c.Id, BattleResultChoiceId(group.Id)) && Same(c.GroupId, group.Id));

    private static bool IsBattleResultChoice(EventChoiceRow choice, ChoiceGroupRow group)
        => Same(group.NextAction, "battle")
           && Same(choice.GroupId, group.Id)
           && Same(choice.Id, BattleResultChoiceId(group.Id));

    private static int EnsureBattleResultChoices(EventWorkbook workbook)
    {
        var changed = 0;
        foreach (var group in workbook.Groups.Where(g => Same(g.NextAction, "battle")).OrderBy(g => g.Id))
        {
            var id = BattleResultChoiceId(group.Id);
            var choice = workbook.Choices.FirstOrDefault(c => Same(c.Id, id));
            if (choice is null)
            {
                var nextSeq = workbook.Choices
                    .Where(c => Same(c.GroupId, group.Id))
                    .Select(c => c.Seq)
                    .DefaultIfEmpty(0)
                    .Max() + 1;
                choice = new EventChoiceRow
                {
                    Id = id,
                    Memo = "전투 결과",
                    ExportId = DefaultEventExportId,
                    GroupId = group.Id,
                    Seq = Math.Max(1, nextSeq),
                    ChoiceTextTid = $"{id}_choice_text",
                    CostType = "none",
                    SuccessRewardType = "relic",
                    SuccessRewardAmount = 1,
                    FailRewardType = "none"
                };
                workbook.Choices.Add(choice);
                changed++;
            }

            changed += EnsureBattleResultDefaults(choice, group);
        }

        return changed;
    }

    private static int EnsureBattleResultDefaults(EventChoiceRow choice, ChoiceGroupRow group)
    {
        var changed = 0;
        if (!Same(choice.GroupId, group.Id))
        {
            choice.GroupId = group.Id;
            changed++;
        }
        if (Blank(choice.Memo))
        {
            choice.Memo = "전투 결과";
            changed++;
        }
        if (Blank(choice.ExportId))
        {
            choice.ExportId = DefaultEventExportId;
            changed++;
        }
        if (choice.Seq <= 0)
        {
            choice.Seq = 1;
            changed++;
        }
        if (Blank(choice.ChoiceTextTid))
        {
            choice.ChoiceTextTid = $"{choice.Id}_choice_text";
            changed++;
        }
        if (Blank(choice.CostType))
        {
            choice.CostType = "none";
            changed++;
        }
        if (Blank(choice.SuccessRewardType) || (Same(choice.SuccessRewardType, "none") && choice.SuccessRewardAmount is null))
        {
            choice.SuccessRewardType = "relic";
            choice.SuccessRewardAmount = 1;
            changed++;
        }
        else if (!Same(choice.SuccessRewardType, "none") && choice.SuccessRewardAmount is null)
        {
            choice.SuccessRewardAmount = 1;
            changed++;
        }
        if (Blank(choice.FailRewardType))
        {
            choice.FailRewardType = "none";
            changed++;
        }

        return changed;
    }

    private static int EnsurePreBattleEscapeChoices(
        EventWorkbook workbook,
        EventBaseRow evt,
        List<ChoiceGroupRow> eventGroups,
        Dictionary<string, ChoiceGroupRow> groupsById)
    {
        var battleGroupIds = eventGroups
            .Where(g => Same(g.NextAction, "battle"))
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (battleGroupIds.Count == 0)
            return 0;

        var sourceGroupIds = workbook.Choices
            .Where(c => groupsById.TryGetValue(c.GroupId, out var owner)
                        && Same(owner.EventId, evt.Id)
                        && !IsBattleResultChoice(c, owner)
                        && (battleGroupIds.Contains(c.SuccessNextGroupId) || battleGroupIds.Contains(c.FailNextGroupId)))
            .Select(c => c.GroupId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceGroupIds.Count == 0)
            return 0;

        var changed = 0;
        var exitGroup = EnsureExitGroup(workbook, evt, eventGroups);
        groupsById[exitGroup.Id] = exitGroup;

        foreach (var sourceGroupId in sourceGroupIds)
        {
            if (!groupsById.TryGetValue(sourceGroupId, out var sourceGroup))
                continue;
            if (Same(sourceGroup.NextAction, "battle") || Same(sourceGroup.NextAction, "exit"))
                continue;
            if (HasPlainEscapeChoice(workbook, sourceGroup.Id, groupsById))
                continue;

            if (!Same(sourceGroup.NextAction, "choice"))
            {
                sourceGroup.NextAction = "choice";
                sourceGroup.StageId = "";
                changed++;
            }

            var nextSeq = workbook.Choices
                .Where(c => Same(c.GroupId, sourceGroup.Id))
                .Select(c => c.Seq)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var id = NextChoiceId(workbook, sourceGroup.Id);
            workbook.Choices.Add(new EventChoiceRow
            {
                Id = id,
                Memo = "무시하고 지나친다.",
                ExportId = DefaultEventExportId,
                GroupId = sourceGroup.Id,
                Seq = nextSeq,
                ChoiceTextTid = $"{id}_choice_text",
                CostType = "none",
                SuccessRewardType = "none",
                SuccessNextGroupId = exitGroup.Id,
                FailRewardType = "none"
            });
            changed++;
        }

        return changed;
    }

    private static bool HasPlainEscapeChoice(
        EventWorkbook workbook,
        string sourceGroupId,
        Dictionary<string, ChoiceGroupRow> groupsById)
        => workbook.Choices.Any(c =>
            Same(c.GroupId, sourceGroupId)
            && Same(c.CostType, "none")
            && c.CostAmount is null
            && Same(c.SuccessRewardType, "none")
            && c.SuccessRewardAmount is null
            && Same(c.FailRewardType, "none")
            && c.FailRewardAmount is null
            && NotBlank(c.SuccessNextGroupId)
            && groupsById.TryGetValue(c.SuccessNextGroupId, out var target)
            && Same(target.NextAction, "exit"));

    private static string NextChoiceId(EventWorkbook workbook, string groupId)
    {
        for (var seq = workbook.Choices
                     .Where(c => Same(c.GroupId, groupId))
                     .Select(c => c.Seq)
                     .DefaultIfEmpty(0)
                     .Max() + 1;
             ; seq++)
        {
            var id = $"{groupId}_c{seq}";
            if (workbook.Choices.All(c => !Same(c.Id, id)))
                return id;
        }
    }

    private static int EnsureReferencedGroups(EventWorkbook workbook)
    {
        var eventIds = workbook.Events.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingGroupIds = workbook.Groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in workbook.Events.Where(e => NotBlank(e.FirstGroupId)))
            referenced.Add(evt.FirstGroupId);
        foreach (var choice in workbook.Choices)
        {
            if (NotBlank(choice.GroupId))
                referenced.Add(choice.GroupId);
            if (NotBlank(choice.SuccessNextGroupId))
                referenced.Add(choice.SuccessNextGroupId);
            if (NotBlank(choice.FailNextGroupId))
                referenced.Add(choice.FailNextGroupId);
        }

        var added = 0;
        foreach (var groupId in referenced.OrderBy(x => x))
        {
            if (existingGroupIds.Contains(groupId))
                continue;

            var eventId = InferEventIdFromGroupId(groupId);
            if (Blank(eventId) || !eventIds.Contains(eventId))
                continue;

            var template = workbook.Groups
                .Where(g => Same(g.EventId, eventId))
                .OrderBy(g => g.Id)
                .FirstOrDefault();
            var hasChoices = workbook.Choices.Any(c => Same(c.GroupId, groupId));
            var isExit = groupId.Contains("_exit", StringComparison.OrdinalIgnoreCase) || !hasChoices;
            var evt = workbook.Events.FirstOrDefault(e => Same(e.Id, eventId));
            var group = new ChoiceGroupRow
            {
                Id = groupId,
                Memo = isExit ? ExitMemoForEvent(evt, eventId) : "장면 내용을 입력하세요.",
                ExportId = DefaultEventExportId,
                EventId = eventId,
                Background = template?.Background ?? "dimension_spiral",
                NpcId = template?.NpcId ?? "",
                SituationTextTid = $"{groupId}_situation_text",
                NextAction = isExit ? "exit" : "choice",
                StageId = ""
            };
            workbook.Groups.Add(group);
            existingGroupIds.Add(groupId);
            added++;
        }

        return added;
    }

    private static string InferEventIdFromGroupId(string groupId)
    {
        var match = Regex.Match(groupId, @"^s\d+_evt_\d{3}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : "";
    }

    private static ChoiceGroupRow EnsureExitGroup(EventWorkbook workbook, EventBaseRow evt, List<ChoiceGroupRow> eventGroups)
    {
        var existing = ExistingExitGroup(workbook, evt.Id, eventGroups);
        if (existing is not null)
            return existing;

        var template = eventGroups
            .OrderByDescending(g => workbook.Choices.Any(c => Same(c.GroupId, g.Id) && (Blank(c.SuccessNextGroupId) || (c.SuccessRate is not null && Blank(c.FailNextGroupId)))))
            .ThenBy(g => g.Id)
            .First();
        var id = NextExitGroupId(workbook, evt.Id);
        var exitGroup = new ChoiceGroupRow
        {
            Id = id,
            Memo = ExitMemoForEvent(evt, evt.Id),
            ExportId = EventWorkbookService.DefaultEventExportId,
            EventId = evt.Id,
            Background = Default(template.Background, "dimension_spiral"),
            NpcId = template.NpcId,
            SituationTextTid = $"{id}_situation_text",
            NextAction = "exit",
            StageId = ""
        };
        workbook.Groups.Add(exitGroup);
        eventGroups.Add(exitGroup);

        if (workbook.Layouts.TryGetValue(template.Id, out var sourceLayout))
        {
            workbook.Layouts[id] = new NodeLayout
            {
                EventId = evt.Id,
                GroupId = id,
                X = sourceLayout.X + 520,
                Y = sourceLayout.Y,
                Width = 340,
                Height = 138
            };
        }

        return exitGroup;
    }

    private static int EnsureExitMemos(EventWorkbook workbook)
    {
        var changed = 0;
        foreach (var group in workbook.Groups.Where(g => Same(g.NextAction, "exit")))
        {
            if (!IsGeneratedExitMemo(group.Memo))
                continue;
            var evt = workbook.Events.FirstOrDefault(e => Same(e.Id, group.EventId));
            var memo = ExitMemoForEvent(evt, group.EventId);
            if (Same(group.Memo, memo))
                continue;
            group.Memo = memo;
            changed++;
        }
        return changed;
    }

    private static bool IsGeneratedExitMemo(string memo)
    {
        if (Blank(memo))
            return true;
        var trimmed = memo.Trim();
        return Same(trimmed, "이벤트가 종료된다.")
               || Same(trimmed, "이벤트가 종료됩니다.")
               || Same(trimmed, "event end")
               || Same(trimmed, "이벤트 종료");
    }

    private static string ExitMemoForEvent(EventBaseRow? evt, string eventId)
    {
        var title = evt is not null && NotBlank(evt.Memo) ? evt.Memo.Trim() : eventId;
        if (Blank(title))
            title = "이번 사건";
        var index = StableIndex(evt?.Id ?? eventId, ExitMemoVariants.Length);
        return string.Format(CultureInfo.InvariantCulture, ExitMemoVariants[index], title);
    }

    private static int StableIndex(string key, int count)
    {
        if (count <= 0)
            return 0;
        unchecked
        {
            var hash = 23;
            foreach (var ch in key)
                hash = hash * 31 + ch;
            var positive = hash == int.MinValue ? 0 : Math.Abs(hash);
            return positive % count;
        }
    }

    private static int NormalizeExitGroupIds(EventWorkbook workbook)
    {
        var changed = 0;
        var usedIds = workbook.Groups
            .Select(g => g.Id)
            .Where(NotBlank)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in workbook.Events.OrderBy(e => e.Id))
        {
            var exitGroups = workbook.Groups
                .Where(g => Same(g.EventId, evt.Id) && Same(g.NextAction, "exit"))
                .OrderBy(g => g.Id)
                .ToList();

            foreach (var group in exitGroups)
            {
                if (IsCanonicalExitGroupId(group.Id, evt.Id))
                    continue;

                var oldId = group.Id;
                usedIds.Remove(oldId);
                var newId = NextExitGroupId(usedIds, evt.Id);
                RenameGroupId(workbook, oldId, newId);
                usedIds.Add(newId);
                changed++;
            }
        }

        return changed;
    }

    private static bool IsCanonicalExitGroupId(string groupId, string eventId)
        => Regex.IsMatch(groupId, $"^{Regex.Escape(eventId)}_g999_exit(?:_\\d+)?$", RegexOptions.IgnoreCase);

    private static void RenameGroupId(EventWorkbook workbook, string oldId, string newId)
    {
        if (Blank(oldId) || Blank(newId) || Same(oldId, newId))
            return;

        foreach (var group in workbook.Groups.Where(g => Same(g.Id, oldId)))
        {
            group.Id = newId;
            if (Blank(group.SituationTextTid) || Same(group.SituationTextTid, $"{oldId}_situation_text"))
                group.SituationTextTid = $"{newId}_situation_text";
        }

        foreach (var evt in workbook.Events.Where(e => Same(e.FirstGroupId, oldId)))
            evt.FirstGroupId = newId;

        foreach (var choice in workbook.Choices)
        {
            if (Same(choice.GroupId, oldId))
                choice.GroupId = newId;
            if (Same(choice.SuccessNextGroupId, oldId))
                choice.SuccessNextGroupId = newId;
            if (Same(choice.FailNextGroupId, oldId))
                choice.FailNextGroupId = newId;
        }

        foreach (var text in workbook.TextEntries.Where(t => Same(t.Tid, $"{oldId}_situation_text")))
            text.Tid = $"{newId}_situation_text";

        RenameLayoutKey(workbook, oldId, newId);
        RenameLayoutKey(workbook, $"battle|{oldId}", $"battle|{newId}");
        RenameLayoutKey(workbook, $"exit|{oldId}", $"exit|{newId}");
        RenameLayoutKey(workbook, $"group_reward|{oldId}", $"group_reward|{newId}");
        foreach (var layout in workbook.Layouts.Values.Where(l => Same(l.GroupId, oldId)))
            layout.GroupId = newId;
    }

    private static void RenameLayoutKey(EventWorkbook workbook, string oldKey, string newKey)
    {
        if (!workbook.Layouts.TryGetValue(oldKey, out var layout) || workbook.Layouts.ContainsKey(newKey))
            return;

        workbook.Layouts.Remove(oldKey);
        layout.GroupId = newKey;
        workbook.Layouts[newKey] = layout;
    }

    public static string NextExitGroupId(EventWorkbook workbook, string eventId)
    {
        var existing = workbook.Groups
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NextExitGroupId(existing, eventId);
    }

    private static string NextExitGroupId(HashSet<string> existing, string eventId)
    {
        var candidate = CanonicalExitGroupId(eventId);
        if (!existing.Contains(candidate))
            return candidate;

        for (var i = 2; ; i++)
        {
            candidate = $"{eventId}_g999_exit_{i}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    public static string CanonicalExitGroupId(string eventId) => $"{eventId}_g999_exit";

    private static ChoiceGroupRow? ExistingExitGroup(EventWorkbook workbook, string eventId, List<ChoiceGroupRow> eventGroups)
    {
        var canonicalId = CanonicalExitGroupId(eventId);
        return eventGroups
                   .Where(g => Same(g.NextAction, "exit")
                               && !workbook.Choices.Any(c => Same(c.GroupId, g.Id)))
                   .OrderByDescending(g => Same(g.Id, canonicalId))
                   .ThenBy(g => g.Id)
                   .FirstOrDefault()
               ?? eventGroups
                   .Where(g => Same(g.NextAction, "exit"))
                   .OrderByDescending(g => Same(g.Id, canonicalId))
                   .ThenBy(g => g.Id)
                   .FirstOrDefault();
    }

    private static void FixWorksheetDimensions(string outputPath, EventWorkbook model)
    {
        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update);
        var sheetPaths = GetWorksheetPaths(archive);
        var dimensions = new Dictionary<string, (int LastRow, int LastColumn)>(StringComparer.OrdinalIgnoreCase)
        {
            [BaseSheetName] = (Math.Max(3, model.Events.Count + 3), 9),
            [GroupSheetName] = (Math.Max(3, model.Groups.Count + 3), 9),
            [ChoiceSheetName] = (Math.Max(3, model.Choices.Count + 3), 15),
            [TextSheetName] = (Math.Max(1, GenerateTextEntries(model).Count + 1), 4),
            [LayoutSheetName] = (Math.Max(1, model.Layouts.Count + 1), 10)
        };
        foreach (var table in BuildEventInfoReport(model).Tables)
            dimensions[table.Name] = (Math.Max(4, table.Rows.Count + 4), Math.Max(1, table.Columns.Count));

        foreach (var (sheetName, size) in dimensions)
        {
            if (!sheetPaths.TryGetValue(sheetName, out var entryName))
                continue;
            SetWorksheetDimension(archive, entryName, $"A1:{ColumnName(size.LastColumn)}{size.LastRow}");
        }

        NormalizeLegacyExcelReaderNamespaces(archive);
    }

    private static Dictionary<string, string> GetWorksheetPaths(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbookEntry is null || relsEntry is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        XDocument workbook;
        XDocument rels;
        using (var stream = workbookEntry.Open())
            workbook = XDocument.Load(stream);
        using (var stream = relsEntry.Open())
            rels = XDocument.Load(stream);

        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

        var targets = rels.Root?
            .Elements(packageRelationships + "Relationship")
            .Where(e => e.Attribute("Id") is not null && e.Attribute("Target") is not null)
            .ToDictionary(e => e.Attribute("Id")!.Value, e => e.Attribute("Target")!.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Root?.Element(spreadsheet + "sheets")?.Elements(spreadsheet + "sheet") ?? [])
        {
            var name = sheet.Attribute("name")?.Value;
            var rid = sheet.Attribute(relationships + "id")?.Value;
            if (Blank(name) || Blank(rid) || !targets.TryGetValue(rid!, out var target))
                continue;
            var entryName = target.TrimStart('/');
            if (!entryName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                entryName = "xl/" + entryName;
            result[name!] = entryName;
        }

        return result;
    }

    private static void SetWorksheetDimension(ZipArchive archive, string entryName, string dimension)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
            return;

        string xml;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            xml = reader.ReadToEnd();

        xml = Regex.Replace(
            xml,
            @"(<(?:\w+:)?dimension\s+ref="")[^""]+(""\s*/>)",
            $"$1{dimension}$2",
            RegexOptions.IgnoreCase);

        entry.Delete();
        var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: xml.StartsWith('\ufeff')));
        writer.Write(xml.TrimStart('\ufeff'));
    }

    private static void NormalizeLegacyExcelReaderNamespaces(ZipArchive archive)
    {
        RewriteZipTextEntry(archive, "xl/workbook.xml", NormalizeSpreadsheetMainNamespacePrefix);
        foreach (var entryName in archive.Entries
                     .Select(e => e.FullName)
                     .Where(name => name.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                    && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            RewriteZipTextEntry(archive, entryName, NormalizeSpreadsheetMainNamespacePrefix);
        }
    }

    private static string NormalizeSpreadsheetMainNamespacePrefix(string xml)
    {
        if (!xml.Contains($"xmlns:x=\"{SpreadsheetMainNamespace}\"", StringComparison.Ordinal)
            || !xml.Contains("<x:", StringComparison.Ordinal))
        {
            return xml;
        }

        var hasDefaultNamespace = xml.Contains($"xmlns=\"{SpreadsheetMainNamespace}\"", StringComparison.Ordinal);
        var normalized = Regex.Replace(
            xml,
            $@"\sxmlns:x=""{Regex.Escape(SpreadsheetMainNamespace)}""",
            hasDefaultNamespace ? "" : $" xmlns=\"{SpreadsheetMainNamespace}\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        normalized = Regex.Replace(
            normalized,
            @"(<\/?)x:([A-Za-z_][\w.\-]*)",
            "$1$2",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        return normalized;
    }

    private static void RewriteZipTextEntry(ZipArchive archive, string entryName, Func<string, string> transform)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
            return;

        string xml;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            xml = reader.ReadToEnd();

        var transformed = transform(xml);
        if (string.Equals(xml, transformed, StringComparison.Ordinal))
            return;

        entry.Delete();
        var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: xml.StartsWith('\ufeff')));
        writer.Write(transformed.TrimStart('\ufeff'));
    }

    private static string ColumnName(int column)
    {
        var name = "";
        while (column > 0)
        {
            column--;
            name = (char)('A' + column % 26) + name;
            column /= 26;
        }
        return name;
    }

    private static void LoadBase(IXLWorksheet ws, EventWorkbook model)
    {
        for (var row = 4; row <= LastRow(ws); row++)
        {
            var id = Cell(ws, row, 1);
            if (Blank(id) && !HasDataInColumns(ws, row, 2, 9))
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
            if (Blank(id) && !HasDataInColumns(ws, row, 2, 9))
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
            if (Blank(id) && !HasDataInColumns(ws, row, 2, 15))
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
                Description = NormalizeIdentityExampleText(Cell(ws, row, 5)),
                Example = NormalizeIdentityExampleText(Cell(ws, row, 6))
            };
            model.ColumnHelps[$"{table}.{column}"] = help;
        }
    }

    private static void NormalizeManualSheetIdentityExamples(XLWorkbook workbook)
    {
        if (!workbook.Worksheets.TryGetWorksheet("매뉴얼", out var ws))
            return;

        foreach (var cell in ws.CellsUsed())
        {
            var text = cell.GetString();
            if (Blank(text))
                continue;
            var normalized = NormalizeIdentityExampleText(text);
            if (!string.Equals(text, normalized, StringComparison.Ordinal))
                cell.Value = normalized;
        }
    }

    private static string NormalizeIdentityExampleText(string text)
    {
        if (Blank(text))
            return text;

        var normalized = text
            .Replace("s1_EVT_", "s1_evt_", StringComparison.OrdinalIgnoreCase)
            .Replace("_BATTLE_RESULT", "_battle_result", StringComparison.OrdinalIgnoreCase)
            .Replace("_G999_EXIT", "_g999_exit", StringComparison.OrdinalIgnoreCase)
            .Replace("_EXIT", "_exit", StringComparison.OrdinalIgnoreCase);

        normalized = Regex.Replace(normalized, @"\bEVT_(\d{3})", "evt_$1", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"_G(\d+)", "_g$1", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"_C(\d+)", "_c$1", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"_S(\d+)", "_s$1", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"_B(\d+)", "_b$1", RegexOptions.IgnoreCase);
        return normalized;
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
                Height = Double(Cell(ws, row, 7), 132),
                PayloadType = Cell(ws, row, 9),
                PayloadAmount = ParseUtil.NullableInt(Cell(ws, row, 10))
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

    private static void WriteText(IXLWorksheet ws, EventWorkbook model, string? textExportId)
    {
        ws.Cell(1, 1).Value = "TID";
        ws.Cell(1, 2).Value = "Text";
        ws.Cell(1, 3).Value = "Comment";
        ws.Cell(1, 4).Value = "ExportID";

        var generated = GenerateTextEntries(model, textExportId)
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
            ws.Cell(row, 4).Value = entry.ExportId;
        }
    }

    private static void WriteEventInfoSheets(XLWorkbook workbook, EventWorkbook model)
    {
        foreach (var sheetName in ObsoleteEventInfoSheetNames)
        {
            if (workbook.Worksheets.TryGetWorksheet(sheetName, out var obsoleteSheet))
                obsoleteSheet.Delete();
        }

        var report = BuildEventInfoReport(model);
        foreach (var table in report.Tables)
            WriteEventInfoSheet(EnsureSheet(workbook, table.Name), table);
    }

    private static void WriteEventInfoSheet(IXLWorksheet ws, EventInfoTable table)
    {
        ws.Clear();
        ws.Cell(1, 1).Value = table.Name;
        ws.Cell(2, 1).Value = table.Description;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#666666");

        for (var col = 0; col < table.Columns.Count; col++)
        {
            var cell = ws.Cell(4, col + 1);
            cell.Value = table.Columns[col];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2f5597");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        var row = 5;
        foreach (var dataRow in table.Rows)
        {
            for (var col = 0; col < table.Columns.Count; col++)
            {
                var value = col < dataRow.Count ? dataRow[col] : null;
                SetInfoCell(ws.Cell(row, col + 1), value);
            }
            row++;
        }

        var lastRow = Math.Max(4, row - 1);
        if (table.Columns.Count > 0)
        {
            var range = ws.Range(4, 1, lastRow, table.Columns.Count);
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#d9e2f3");
            range.Style.Border.InsideBorderColor = XLColor.FromHtml("#d9e2f3");
            range.SetAutoFilter();
            ws.SheetView.FreezeRows(4);
            ws.Columns(1, table.Columns.Count).AdjustToContents();
            foreach (var column in ws.Columns(1, table.Columns.Count))
            {
                if (column.Width > 55)
                    column.Width = 55;
            }
        }
    }

    private static void SetInfoCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                cell.Clear(XLClearOptions.Contents);
                break;
            case int intValue:
                cell.Value = intValue;
                break;
            case long longValue:
                cell.Value = longValue;
                break;
            case double doubleValue:
                cell.Value = doubleValue;
                break;
            case decimal decimalValue:
                cell.Value = decimalValue;
                break;
            default:
                cell.Value = value.ToString() ?? "";
                break;
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
        ws.Cell(1, 9).Value = "payload_type";
        ws.Cell(1, 10).Value = "payload_amount";
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
            ws.Cell(row, 9).Value = layout.PayloadType;
            SetNullable(ws.Cell(row, 10), layout.PayloadAmount);
            row++;
        }
        ws.Visibility = XLWorksheetVisibility.Visible;
    }

    private static void ArrangeWorksheetsForExport(XLWorkbook workbook)
    {
        var position = 1;
        MoveWorksheet(workbook, BaseSheetName, ref position);
        MoveWorksheet(workbook, GroupSheetName, ref position);
        MoveWorksheet(workbook, ChoiceSheetName, ref position);
        MoveWorksheet(workbook, TextSheetName, ref position);
        MoveWorksheet(workbook, "매뉴얼", ref position);
        foreach (var sheetName in EventInfoSheetNames)
            MoveWorksheet(workbook, sheetName, ref position);
        if (workbook.Worksheets.TryGetWorksheet(LayoutSheetName, out var layout))
            layout.Position = workbook.Worksheets.Count;
    }

    private static void MoveWorksheet(XLWorkbook workbook, string sheetName, ref int position)
    {
        if (!workbook.Worksheets.TryGetWorksheet(sheetName, out var ws))
            return;
        ws.Position = position;
        position++;
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
        workbook.Events.Where(e => NotBlank(e.Id))
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var e = g.Last();
                return string.Join("|", e.Memo, e.EventNameTid, e.Rarity, e.FloorRestriction, e.DiffRestriction, e.Weight, e.FirstGroupId);
            }, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> SnapshotGroups(EventWorkbook workbook) =>
        workbook.Groups.Where(g => NotBlank(g.Id))
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var row = g.Last();
                return string.Join("|", row.Memo, row.EventId, row.Background, row.NpcId, row.SituationTextTid, row.NextAction, row.StageId);
            }, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> SnapshotChoices(EventWorkbook workbook) =>
        workbook.Choices.Where(c => NotBlank(c.Id))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var c = g.Last();
                return string.Join("|", c.Memo, c.GroupId, c.Seq, c.ChoiceTextTid, c.CostType, c.CostAmount, c.SuccessRate, c.SuccessRewardType, c.SuccessRewardAmount, c.SuccessNextGroupId, c.FailRewardType, c.FailRewardAmount, c.FailNextGroupId);
            }, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> SnapshotText(IEnumerable<TextEntry> entries) =>
        entries.Where(e => NotBlank(e.Tid)).GroupBy(e => e.Tid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join("|", g.Last().ExportId, g.Last().Text, g.Last().Comment), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> SnapshotLayout(EventWorkbook workbook) =>
        workbook.Layouts.Values.Where(l => NotBlank(l.GroupId))
            .GroupBy(l => l.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var layout = g.Last();
                return string.Join("|", layout.EventId, RoundLayout(layout.X), RoundLayout(layout.Y), RoundLayout(layout.Width), RoundLayout(layout.Height), layout.PayloadType, layout.PayloadAmount);
            }, StringComparer.OrdinalIgnoreCase);

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
        Height = source.Height,
        PayloadType = source.PayloadType,
        PayloadAmount = source.PayloadAmount
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

    private static Dictionary<string, T> UniqueById<T>(IEnumerable<T> rows, Func<T, string?> keySelector) =>
        rows.Select(row => (Row: row, Key: keySelector(row)))
            .Where(pair => NotBlank(pair.Key))
            .GroupBy(pair => pair.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Row, StringComparer.OrdinalIgnoreCase);

    private static bool HasInvalidIdentityKeys(EventWorkbook workbook) =>
        HasBlankOrDuplicateKeys(workbook.Events.Select(e => e.Id))
        || HasBlankOrDuplicateKeys(workbook.Groups.Select(g => g.Id))
        || HasBlankOrDuplicateKeys(workbook.Choices.Select(c => c.Id));

    private static bool HasBlankOrDuplicateKeys(IEnumerable<string?> keys)
    {
        var materialized = keys.ToList();
        return materialized.Any(Blank)
               || materialized.Where(NotBlank)
                   .GroupBy(k => k!, StringComparer.OrdinalIgnoreCase)
                   .Any(g => g.Count() > 1);
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

    private static bool HasDataInColumns(IXLWorksheet ws, int row, int startColumn, int endColumn)
    {
        for (var col = startColumn; col <= endColumn; col++)
        {
            if (NotBlank(Cell(ws, row, col)))
                return true;
        }

        return false;
    }

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
