using System.IO;
using System.Text;
using System.Windows;

namespace NexusEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--qa", StringComparison.OrdinalIgnoreCase))
        {
            RunQaMode(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--research-qa", StringComparison.OrdinalIgnoreCase))
        {
            RunResearchQaMode(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--dev-probe", StringComparison.OrdinalIgnoreCase))
        {
            RunDevProbeMode();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--dev-run-event", StringComparison.OrdinalIgnoreCase))
        {
            RunDevEventMode(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--dev-read-console", StringComparison.OrdinalIgnoreCase))
        {
            RunDevConsoleReadMode(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--sound-probe", StringComparison.OrdinalIgnoreCase))
        {
            RunSoundProbeMode(e.Args);
            Shutdown();
            return;
        }

        var splash = new SplashWindow("Opening Nexus Editor...");
        splash.Show();
        _ = StartEditorAsync(splash);
    }

    private async Task StartEditorAsync(SplashWindow splash)
    {
        try
        {
            await Task.Delay(850);
            splash.SetStatus("Choose a Nexus workspace...");
            var eventPath = NexusPathResolver.ResolveDefaultEventWorkbook();
            var researchPaths = ResearchPathResolver.ResolveDefault();
            NexusHubWindow? hub = null;
            hub = new NexusHubWindow(
                File.Exists(eventPath)
                    ? WorkspaceCardState.Ready(Path.GetFileName(eventPath))
                    : WorkspaceCardState.Unavailable("Event DB 경로를 먼저 설정하세요."),
                researchPaths is not null
                    ? WorkspaceCardState.Ready($"{Path.GetFileName(researchPaths.OutSystemPath)} + effect")
                    : WorkspaceCardState.Unavailable("Research DB 경로를 먼저 설정하세요."),
                mode => OpenWorkspace(mode, hub));
            MainWindow = hub;
            hub.Show();
            splash.Close();
        }
        catch (Exception ex)
        {
            splash.Close();
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "NexusEditor_startup_error.txt"), ex.ToString());
            }
            catch
            {
                // Ignore diagnostic logging failures.
            }
            ThemedMessageBox.Show(ex.ToString(), "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static void RunQaMode(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // WinExe builds may run without an attached console.
        }
        var source = args.Length > 1 ? args[1] : NexusPathResolver.ResolveDefaultEventWorkbook();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            Console.Error.WriteLine("QA failed: nexus_event workbook not found.");
            Environment.ExitCode = 2;
            return;
        }

        var output = args.Length > 2
            ? args[2]
            : Path.Combine(Path.GetDirectoryName(source) ?? Environment.CurrentDirectory, "nexus_event_qa_export.xlsx");
        var workbook = EventWorkbookService.Load(source);
        var issues = EventWorkbookService.Validate(workbook);
        foreach (var issue in issues)
            Console.WriteLine($"{issue.Severity}: {issue.Message}");

        var errorCount = issues.Count(i => i.Severity == ValidationSeverity.Error);
        if (errorCount > 0)
        {
            Console.Error.WriteLine($"QA failed: {errorCount} validation errors.");
            Environment.ExitCode = 3;
            return;
        }

        var diff = EventWorkbookService.BuildDiff(workbook);
        Console.WriteLine($"QA diff entries: {diff.Count}");
        foreach (var group in diff.GroupBy(d => d.Sheet).OrderBy(g => g.Key))
            Console.WriteLine($"QA diff {group.Key}: {group.Count()}");

        EventWorkbookService.SaveAs(workbook, output, createBackup: false);
        var reloaded = EventWorkbookService.Load(output);
        var reloadIssues = EventWorkbookService.Validate(reloaded);
        var reloadErrorCount = reloadIssues.Count(i => i.Severity == ValidationSeverity.Error);

        Console.WriteLine($"QA loaded: {workbook.Events.Count} events, {workbook.Groups.Count} groups, {workbook.Choices.Count} choices.");
        Console.WriteLine($"QA exported: {output}");
        Console.WriteLine($"QA reload errors: {reloadErrorCount}");
        Environment.ExitCode = reloadErrorCount == 0 ? 0 : 4;
    }

    private static void RunResearchQaMode(string[] args)
    {
        TryEnableUtf8Console();
        var defaults = ResearchPathResolver.ResolveDefault();
        var outSystemPath = args.Length > 1 ? args[1] : defaults?.OutSystemPath;
        var effectPath = args.Length > 2 ? args[2] : defaults?.EffectPath;
        if (string.IsNullOrWhiteSpace(outSystemPath) || !File.Exists(outSystemPath)
            || string.IsNullOrWhiteSpace(effectPath) || !File.Exists(effectPath))
        {
            Console.Error.WriteLine("Research QA failed: nexus_node/nexus_effect workbook pair not found.");
            Environment.ExitCode = 10;
            return;
        }

        try
        {
            var workbook = ResearchWorkbookService.Load(outSystemPath, effectPath);
            var issues = ResearchWorkbookService.Validate(workbook);
            foreach (var issue in issues.Take(20))
                Console.WriteLine($"{issue.Severity}: {issue.Message}");

            var errorCount = issues.Count(issue => issue.Severity == ResearchValidationSeverity.Error);
            Console.WriteLine($"Research QA loaded: {workbook.Categories.Count} categories, {workbook.Nodes.Count} nodes, {workbook.Effects.Count} effects.");
            Console.WriteLine($"Research QA validation errors: {errorCount}");
            if (errorCount > 20)
                Console.WriteLine($"Research QA validation: {errorCount - 20:N0} additional pre-existing errors omitted.");

            var qaArtifactDirectory = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3])
                ? Path.GetFullPath(args[3])
                : null;
            var roundTrip = ResearchWorkbookService.TestRoundTrip(
                workbook,
                qaArtifactDirectory,
                keepArtifacts: qaArtifactDirectory is not null);
            if (qaArtifactDirectory is not null)
                Console.WriteLine($"Research QA artifacts: {qaArtifactDirectory}");
            Console.WriteLine($"Research QA baseline/reload errors: {roundTrip.BaselineValidationErrorCount}/{roundTrip.ReloadValidationErrorCount}");
            Console.WriteLine($"Research QA first-pass differences: {roundTrip.FirstPassDifferences.Count}");
            Console.WriteLine($"Research QA second-pass differences: {roundTrip.SecondPassDifferences.Count}");
            Console.WriteLine($"Research QA newly introduced errors: {roundTrip.NewValidationIssues.Count}");
            foreach (var difference in roundTrip.FirstPassDifferences.Take(20))
                Console.WriteLine($"First pass: {difference}");
            foreach (var difference in roundTrip.SecondPassDifferences.Take(20))
                Console.WriteLine($"Second pass: {difference}");
            if (!roundTrip.Success)
            {
                Environment.ExitCode = 12;
                return;
            }

            var mutationFailures = RunResearchMutationQa(workbook);
            foreach (var failure in mutationFailures)
                Console.WriteLine($"Mutation QA: {failure}");
            Console.WriteLine($"Research QA mutation failures: {mutationFailures.Count}");
            Environment.ExitCode = mutationFailures.Count == 0 ? 0 : 14;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Research QA failed: {ex}");
            Environment.ExitCode = 13;
        }
    }

    private static List<string> RunResearchMutationQa(ResearchWorkbookContext source)
    {
        var failures = new List<string>();
        var linkedEffectCount = source.Nodes.Count(node => source.FindEffect(node) is not null);
        if (linkedEffectCount != source.Nodes.Count)
            failures.Add($"기존 연구 노드-효과 연결 실패: {linkedEffectCount}/{source.Nodes.Count}");
        if (Math.Abs(ResearchWorkbookService.CalculateActiveItemTotal("1;2;3") - 6d) > 0.0000001d)
            failures.Add("active_item_value 합산 helper 계산 실패");
        try
        {
            _ = ResearchWorkbookService.CalculateActiveItemTotal("1;invalid;3");
            failures.Add("잘못된 active_item_value 숫자 형식이 허용됨");
        }
        catch (InvalidOperationException)
        {
        }

        var themeTest = source.DeepClone();
        var themeNode = themeTest.Nodes.FirstOrDefault();
        if (themeNode is not null)
        {
            themeNode.ThemeId = "s2";
            var themeResult = ResearchWorkbookService.TryMoveNode(
                themeTest, themeNode.RowIdentity, themeNode.Column, themeNode.Row);
            var changedThemeNode = themeTest.Nodes.First(row => row.RowIdentity == themeNode.RowIdentity);
            var themeEffect = themeTest.FindEffect(changedThemeNode);
            if (!themeResult.Success
                || !changedThemeNode.Id.StartsWith("s2_node_", StringComparison.Ordinal)
                || themeEffect is null
                || !string.Equals(themeEffect.Id, changedThemeNode.NexusEffectId, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("theme_id 변경 시 node/effect ID 연쇄 갱신 실패");
            }
            else
            {
                var themeRoundTrip = ResearchWorkbookService.TestRoundTrip(themeTest);
                if (!themeRoundTrip.Success)
                    failures.Add("s1 이외 theme 연구 효과의 저장/재로드 실패");
            }
        }

        var categoryMoveTest = source.DeepClone();
        var nextCategoryIndex = categoryMoveTest.Categories.Select(row => row.Index ?? 0).DefaultIfEmpty().Max() + 1;
        var fromCategory = ResearchWorkbookService.CreateCategory(categoryMoveTest, "qa_move_from", nextCategoryIndex);
        var toCategory = ResearchWorkbookService.CreateCategory(categoryMoveTest, "qa_move_to", nextCategoryIndex + 1);
        var categoryMovePair = ResearchWorkbookService.CreateNode(categoryMoveTest, fromCategory.Category, 201, 3);
        categoryMovePair.Node.Image = "qa_category_move_icon";
        categoryMovePair.Effect.Type = "cs_add";
        categoryMovePair.Effect.Condition = "range=self";
        categoryMovePair.Effect.ValueText = "1";
        var categoryMove = ResearchWorkbookService.TryChangeNodeCategory(
            categoryMoveTest, categoryMovePair.Node.RowIdentity, toCategory.Category);
        var movedCategoryNode = categoryMoveTest.Nodes.First(row => row.RowIdentity == categoryMovePair.Node.RowIdentity);
        var movedCategoryEffect = categoryMoveTest.FindEffect(movedCategoryNode);
        if (!categoryMove.Success
            || !string.Equals(movedCategoryNode.Category, toCategory.Category, StringComparison.OrdinalIgnoreCase)
            || movedCategoryEffect is null
            || !string.Equals(movedCategoryEffect.Id, movedCategoryNode.NexusEffectId, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("개별 노드 category 변경 시 node/effect 연쇄 갱신 실패");
        }
        else
        {
            var categoryRoundTrip = ResearchWorkbookService.TestRoundTrip(categoryMoveTest);
            if (!categoryRoundTrip.Success)
                failures.Add("개별 노드 category 변경의 저장/재로드 실패");
        }

        var exportSelectionTest = source.DeepClone();
        var selectionCategories = exportSelectionTest.Categories.Take(2).ToList();
        if (selectionCategories.Count == 2)
        {
            selectionCategories[0].ExportId += "_qa_first";
            selectionCategories[1].ExportId += "_qa_second";
            var selectionDiff = ResearchWorkbookService.BuildDiff(exportSelectionTest)
                .Where(entry => entry.Sheet == ResearchWorkbookService.CategorySheetName)
                .ToList();
            var selectedContext = ResearchWorkbookService.CreateExportContext(exportSelectionTest, selectionDiff.Take(1));
            var selectedDiff = ResearchWorkbookService.BuildDiff(selectedContext);
            if (selectedDiff.Count != 1
                || !string.Equals(selectedDiff[0].RowIdentity, selectionDiff[0].RowIdentity, StringComparison.OrdinalIgnoreCase))
                failures.Add("Export Preview 선택 행 분리 실패");
            if (ResearchWorkbookService.BuildDiff(exportSelectionTest).Count < 2)
                failures.Add("Export Preview 선택 행 분리 중 원본 편집 상태 손상");
            var simulatedSaved = selectedContext.DeepClone(includeOriginalSnapshot: false);
            simulatedSaved.OriginalSnapshot = simulatedSaved.DeepClone(includeOriginalSnapshot: false);
            var rebased = ResearchWorkbookService.RebaseAfterExport(
                exportSelectionTest, simulatedSaved, selectedContext, selectionDiff.Take(1));
            var pendingAfterRebase = ResearchWorkbookService.BuildDiff(rebased);
            if (pendingAfterRebase.Count != 1
                || !string.Equals(pendingAfterRebase[0].RowIdentity, selectionDiff[1].RowIdentity, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("일부 Export 뒤 체크하지 않은 편집 상태 보존 실패");
            }
        }

        var topologyTest = source.DeepClone();
        var topologyCategoryIndex = topologyTest.Categories.Select(row => row.Index ?? 0).DefaultIfEmpty().Max() + 1;
        var topologyCategory = ResearchWorkbookService.CreateCategory(topologyTest, "qa_topology", topologyCategoryIndex);
        var branchSource = ResearchWorkbookService.CreateNode(topologyTest, topologyCategory.Category, 201, 4).Node;
        var branchTargets = new[] { 2, 4, 6, 8 }
            .Select(row => ResearchWorkbookService.CreateNode(topologyTest, topologyCategory.Category, 202, row).Node)
            .ToList();
        var branchResults = branchTargets.Take(3)
            .Select(target => ResearchWorkbookService.TryConnectCondition(topologyTest, branchSource.RowIdentity, target.RowIdentity))
            .ToList();
        if (branchResults.Any(result => !result.Success)
            || string.IsNullOrWhiteSpace(branchResults[^1].WarningMessage))
            failures.Add("1-3 연구 분기의 경고 허용 규칙 실패");
        if (ResearchWorkbookService.TryConnectCondition(topologyTest, branchSource.RowIdentity, branchTargets[3].RowIdentity).Success)
            failures.Add("연구 노드의 네 번째 후행 연결 차단 실패");
        var distantTarget = ResearchWorkbookService.CreateNode(topologyTest, topologyCategory.Category, 203, 4).Node;
        var distantResult = ResearchWorkbookService.TryConnectCondition(topologyTest, branchSource.RowIdentity, distantTarget.RowIdentity);
        if (distantResult.Success || !distantResult.Message.Contains("바로 다음", StringComparison.Ordinal))
            failures.Add("인접하지 않은 column 간 연구 노드 연결 차단 실패");

        var mergeSources = new[] { 2, 4, 6, 8 }
            .Select(row => ResearchWorkbookService.CreateNode(topologyTest, topologyCategory.Category, 204, row).Node)
            .ToList();
        var mergeTarget = ResearchWorkbookService.CreateNode(topologyTest, topologyCategory.Category, 205, 4).Node;
        var mergeResults = mergeSources.Take(3)
            .Select(prerequisite => ResearchWorkbookService.TryConnectCondition(topologyTest, prerequisite.RowIdentity, mergeTarget.RowIdentity))
            .ToList();
        if (mergeResults.Any(result => !result.Success)
            || string.IsNullOrWhiteSpace(mergeResults[^1].WarningMessage))
            failures.Add("3-1 연구 합류의 경고 허용 규칙 실패");
        if (ResearchWorkbookService.TryConnectCondition(topologyTest, mergeSources[3].RowIdentity, mergeTarget.RowIdentity).Success)
            failures.Add("연구 노드의 네 번째 선행 연결 차단 실패");

        var structuredTest = source.DeepClone();
        var structuredCategoryIndex = structuredTest.Categories.Select(row => row.Index ?? 0).DefaultIfEmpty().Max() + 1;
        var structuredCategory = ResearchWorkbookService.CreateCategory(structuredTest, "qa_structured", structuredCategoryIndex);
        var structuredStart = ResearchWorkbookService.CreateNode(structuredTest, structuredCategory.Category, 301, 4);
        var structuredNext = ResearchWorkbookService.CreateNode(structuredTest, structuredCategory.Category, 302, 4);
        foreach (var pair in new[] { structuredStart, structuredNext })
        {
            pair.Node.Image = "qa_node_icon";
            pair.Effect.Type = "cs_add";
            pair.Effect.Condition = "range=self";
            pair.Effect.ValueText = "1";
        }
        var expandStructured = ResearchWorkbookService.TrySetColumnNodeCount(
            structuredTest, structuredCategory.Category, 301, 3);
        var expandedNodes = structuredTest.Nodes
            .Where(node => node.Category == structuredCategory.Category && node.Column == 301)
            .OrderBy(node => node.Row)
            .ToList();
        var expandedTarget = structuredTest.Nodes.Single(node =>
            node.Category == structuredCategory.Category && node.Column == 302);
        if (!expandStructured.Success
            || !expandedNodes.Select(node => node.Row).SequenceEqual(new[] { 2, 4, 6 })
            || expandedTarget.Conditions.Count(value => !string.IsNullOrWhiteSpace(value)) != 3
            || expandedNodes.Any(node => structuredTest.FindEffect(node) is null))
        {
            failures.Add("단계 노드 수 1->3 구조 변경 또는 효과/조건 연동 실패");
        }
        var reduceStructured = ResearchWorkbookService.TrySetColumnNodeCount(
            structuredTest, structuredCategory.Category, 301, 2);
        var reducedNodes = structuredTest.Nodes
            .Where(node => node.Category == structuredCategory.Category && node.Column == 301)
            .OrderBy(node => node.Row)
            .ToList();
        expandedTarget = structuredTest.Nodes.Single(node =>
            node.Category == structuredCategory.Category && node.Column == 302);
        if (!reduceStructured.Success
            || !reducedNodes.Select(node => node.Row).SequenceEqual(new[] { 2, 6 })
            || expandedTarget.Conditions.Count(value => !string.IsNullOrWhiteSpace(value)) != 2
            || expandedTarget.Conditions.Where(value => !string.IsNullOrWhiteSpace(value))
                .Any(value => structuredTest.FindNode(value) is null))
        {
            failures.Add("단계 노드 수 3->2 구조 변경 또는 조건 정리 실패");
        }

        var exactSlot = ResearchWorkbookService.CreateNode(
            structuredTest, structuredCategory.Category, 301, 4);
        exactSlot.Node.Image = "qa_exact_slot_icon";
        exactSlot.Effect.Type = "cs_add";
        exactSlot.Effect.Condition = "range=self";
        exactSlot.Effect.ValueText = "1";
        var exactEffectIdentity = exactSlot.Effect.RowIdentity;
        var deleteExactSlot = ResearchWorkbookService.TryDeleteNode(
            structuredTest, exactSlot.Node.RowIdentity, removeConditionReferences: true);
        if (!deleteExactSlot.Success
            || structuredTest.Nodes.Any(node => node.RowIdentity == exactSlot.Node.RowIdentity)
            || structuredTest.Effects.Any(effect => effect.RowIdentity == exactEffectIdentity)
            || !structuredTest.Nodes
                .Where(node => node.Category == structuredCategory.Category && node.Column == 301)
                .Select(node => node.Row)
                .OrderBy(row => row)
                .SequenceEqual(new[] { 2, 6 }))
        {
            failures.Add("빈 슬롯의 정확한 좌표 생성 또는 노드/효과 단독 삭제 실패");
        }

        for (var index = 0; index < reducedNodes.Count; index++)
        {
            var sourceNode = reducedNodes[index];
            sourceNode.Image = $"qa_step_copy_{sourceNode.Row}";
            sourceNode.ActiveItemId = $"qa_item_{sourceNode.Row}";
            sourceNode.ActiveItemValue = (index + 2).ToString();
            sourceNode.ActiveStep = index + 2;
            if (structuredTest.FindEffect(sourceNode) is { } sourceEffect)
            {
                sourceEffect.Type = $"qa_copy_type_{sourceNode.Row}";
                sourceEffect.ValueText = (index + 10).ToString();
            }
        }
        expandedTarget.NodePermission = 4;
        var stepSnapshot = ResearchWorkbookService.CaptureStepBlock(
            structuredTest, structuredCategory.Category, 301);
        var pasteStep = ResearchWorkbookService.TryPasteStepBlock(
            structuredTest, stepSnapshot, structuredCategory.Category, 302);
        var pastedNodes = structuredTest.Nodes
            .Where(node => node.Category == structuredCategory.Category && node.Column == 302)
            .OrderBy(node => node.Row)
            .ToList();
        var pastedPrerequisites = pastedNodes
            .SelectMany(node => node.Conditions)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (!pasteStep.Success
            || !pastedNodes.Select(node => node.Row).SequenceEqual(new[] { 2, 6 })
            || !pastedNodes.Select(node => node.Image).SequenceEqual(new[] { "qa_step_copy_2", "qa_step_copy_6" })
            || pastedNodes.Any(node => node.NodePermission != 4)
            || pastedNodes.Any(node => structuredTest.FindEffect(node) is null)
            || !pastedNodes.Select(node => structuredTest.FindEffect(node)!.Type)
                .SequenceEqual(new[] { "qa_copy_type_2", "qa_copy_type_6" })
            || pastedPrerequisites.Count != 2
            || pastedPrerequisites.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2
            || pastedPrerequisites.Any(value => structuredTest.FindNode(value) is null))
        {
            failures.Add("STEP 블록 복사/붙여넣기 또는 좌표·권한·연결 보존 실패");
        }

        var appendFirst = ResearchWorkbookService.TryAppendStep(
            structuredTest, structuredCategory.Category);
        var appendSecond = ResearchWorkbookService.TryAppendStep(
            structuredTest, structuredCategory.Category);
        var appendedNodes = structuredTest.Nodes
            .Where(node => node.Category == structuredCategory.Category && node.Column is 303 or 304)
            .OrderBy(node => node.Column)
            .ToList();
        var rangeSnapshot = ResearchWorkbookService.CaptureStepBlocks(
            structuredTest, structuredCategory.Category, new[] { 301, 302 });
        var pasteRangeFirst = ResearchMutationResult.Failed("STEP 묶음 캡처 실패");
        var pasteRangeSecond = ResearchMutationResult.Failed("STEP 묶음 캡처 실패");
        if (rangeSnapshot.Blocks.Count == 2)
        {
            pasteRangeFirst = ResearchWorkbookService.TryPasteStepBlock(
                structuredTest, rangeSnapshot.Blocks[0], structuredCategory.Category, 303);
            pasteRangeSecond = ResearchWorkbookService.TryPasteStepBlock(
                structuredTest, rangeSnapshot.Blocks[1], structuredCategory.Category, 304);
        }
        var pastedRangeNodes = structuredTest.Nodes
            .Where(node => node.Category == structuredCategory.Category && node.Column is 303 or 304)
            .OrderBy(node => node.Column)
            .ThenBy(node => node.Row)
            .ToList();
        if (!appendFirst.Success
            || !appendSecond.Success
            || appendedNodes.Count != 2
            || appendedNodes.Any(node => structuredTest.FindEffect(node) is null)
            || appendedNodes.Any(node => !string.Equals(
                node.Id,
                ResearchWorkbookService.GenerateNodeId(node.ThemeId, node.Category, node.Column, node.Row),
                StringComparison.OrdinalIgnoreCase))
            || appendedNodes.Any(node => node.Column > 301
                && !node.Conditions.Any(value => !string.IsNullOrWhiteSpace(value)))
            || rangeSnapshot.StepCount != 2
            || rangeSnapshot.NodeCount != 4
            || !pasteRangeFirst.Success
            || !pasteRangeSecond.Success
            || pastedRangeNodes.Count != 4
            || pastedRangeNodes.Any(node => structuredTest.FindEffect(node) is null))
        {
            failures.Add("STEP 연속 추가 또는 다중 STEP 묶음 복사/붙여넣기 실패");
        }

        var permissionTest = source.DeepClone();
        var permissionCategoryIndex = permissionTest.Categories.Select(row => row.Index ?? 0).DefaultIfEmpty().Max() + 1;
        var permissionCategory = ResearchWorkbookService.CreateCategory(permissionTest, "qa_permission", permissionCategoryIndex);
        for (var column = 401; column <= 406; column++)
        {
            var pair = ResearchWorkbookService.CreateNode(permissionTest, permissionCategory.Category, column, 4);
            pair.Node.Image = "qa_permission_icon";
            pair.Node.NodePermission = 1;
            pair.Effect.Type = "cs_add";
            pair.Effect.Condition = "range=self";
            pair.Effect.ValueText = "1";
        }
        var addPermission = ResearchWorkbookService.TryAddPermissionRegion(permissionTest, permissionCategory.Category);
        var shiftPermissionLeft = ResearchWorkbookService.TryShiftPermissionBoundary(permissionTest, permissionCategory.Category, 1, -1);
        var permissionTwoColumns = permissionTest.Nodes
            .Where(node => node.Category == permissionCategory.Category && node.NodePermission == 2)
            .Select(node => node.Column)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        var shiftPermissionRight = ResearchWorkbookService.TryShiftPermissionBoundary(permissionTest, permissionCategory.Category, 1, 1);
        var removePermission = ResearchWorkbookService.TryRemovePermissionRegion(permissionTest, permissionCategory.Category);
        if (!addPermission.Success
            || !shiftPermissionLeft.Success
            || !permissionTwoColumns.SequenceEqual(new[] { 405, 406 })
            || !shiftPermissionRight.Success
            || !removePermission.Success
            || permissionTest.Nodes.Where(node => node.Category == permissionCategory.Category)
                .Any(node => node.NodePermission != 1))
        {
            failures.Add("권한 구역 추가/경계 이동/삭제의 node_permission 일괄 변경 실패");
        }


        var permissionColumns = permissionTest.Nodes
            .Where(node => node.Category == permissionCategory.Category)
            .GroupBy(node => node.Column)
            .OrderBy(group => group.Key)
            .ToList();
        for (var index = 0; index < permissionColumns.Count; index++)
        {
            var permission = index >= permissionColumns.Count - 2 ? 5 : index + 1;
            foreach (var node in permissionColumns[index])
                node.NodePermission = permission;
        }
        var addPermissionSix = ResearchWorkbookService.TryAddPermissionRegion(
            permissionTest, permissionCategory.Category);
        var permissionSixNodes = permissionTest.Nodes
            .Where(node => node.Category == permissionCategory.Category && node.NodePermission == 6)
            .ToList();
        var permissionSixValidation = ResearchWorkbookService.Validate(permissionTest)
            .Where(issue => issue.Code == "node_permission_range")
            .ToList();
        if (!addPermissionSix.Success || permissionSixNodes.Count == 0 || permissionSixValidation.Count > 0)
            failures.Add("PERMISSION 6 이상 구역 생성 또는 검증 상한 해제 실패");

        var test = source.DeepClone();
        var categoryIndex = test.Categories.Select(row => row.Index ?? 0).DefaultIfEmpty().Max() + 1;
        var category = ResearchWorkbookService.CreateCategory(test, "qa_editor", categoryIndex);
        var first = ResearchWorkbookService.CreateNode(test, category.Category, 101, 3);
        var second = ResearchWorkbookService.CreateNode(test, category.Category, 102, 3);
        foreach (var pair in new[] { first, second })
        {
            pair.Node.Image = "qa_node_icon";
            pair.Effect.Type = "cs_add";
            pair.Effect.Condition = "range=self";
            pair.Effect.ValueText = "1";
        }

        var connected = ResearchWorkbookService.TryConnectCondition(
            test, first.Node.RowIdentity, second.Node.RowIdentity);
        if (!connected.Success || !second.Node.Conditions.Any(value =>
                string.Equals(value, first.Node.Id, StringComparison.OrdinalIgnoreCase)))
            failures.Add("노드 연결 또는 condition_node 갱신 실패");

        var multiMove = ResearchWorkbookService.TryMoveNodes(
            test,
            new[] { first.Node.RowIdentity, second.Node.RowIdentity },
            deltaColumn: 1,
            deltaRow: 0);
        var multiMovedFirst = test.Nodes.First(row => row.RowIdentity == first.Node.RowIdentity);
        var multiMovedSecond = test.Nodes.First(row => row.RowIdentity == second.Node.RowIdentity);
        if (!multiMove.Success
            || multiMovedFirst.Column != 102 || multiMovedFirst.Row != 3
            || multiMovedSecond.Column != 103 || multiMovedSecond.Row != 3
            || !multiMovedSecond.Conditions.Any(value =>
                string.Equals(value, multiMovedFirst.Id, StringComparison.OrdinalIgnoreCase)))
            failures.Add("다중 노드 이동의 선택 노드 간 자리 교차 또는 참조 연쇄 갱신 실패");

        var oldFirstId = multiMovedFirst.Id;
        var moved = ResearchWorkbookService.TryMoveNode(test, first.Node.RowIdentity, 102, 5);
        var movedFirst = test.Nodes.First(row => row.RowIdentity == first.Node.RowIdentity);
        var movedSecond = test.Nodes.First(row => row.RowIdentity == second.Node.RowIdentity);
        if (!moved.Success || movedFirst.Id == oldFirstId
            || !movedSecond.Conditions.Any(value => string.Equals(value, movedFirst.Id, StringComparison.OrdinalIgnoreCase))
            || test.FindEffect(movedFirst) is not { } movedEffect
            || !string.Equals(movedEffect.Id, movedFirst.NexusEffectId, StringComparison.OrdinalIgnoreCase))
            failures.Add("좌표 이동 시 node/effect/condition ID 연쇄 갱신 실패");

        var renamed = ResearchWorkbookService.TryRenameCategory(test, category.RowIdentity, "qa_editor_renamed");
        var renamedCategory = test.Categories.First(row => row.RowIdentity == category.RowIdentity);
        var renamedNodes = test.Nodes.Where(row => row.Category == renamedCategory.Category).ToList();
        if (!renamed.Success || renamedNodes.Count != 2
            || renamedNodes.Any(node => test.FindEffect(node) is null)
            || renamedNodes.SelectMany(node => node.Conditions).Any(value =>
                !string.IsNullOrWhiteSpace(value) && test.FindNode(value) is null))
            failures.Add("카테고리 변경 시 node/effect/condition 연쇄 갱신 실패");

        var revertTest = test.DeepClone();
        var effectDiff = ResearchWorkbookService.BuildDiff(revertTest)
            .FirstOrDefault(entry => entry.Sheet == ResearchWorkbookService.EffectSheetName
                                     && entry.ChangeType == ResearchDiffChangeType.Add);
        if (effectDiff is null)
        {
            failures.Add("연쇄 리버트 QA용 효과 변경을 찾지 못함");
        }
        else
        {
            ResearchWorkbookService.RevertRelatedDiffs(revertTest, new[] { effectDiff });
            if (ResearchWorkbookService.BuildDiff(revertTest).Count != 0)
                failures.Add("카테고리/노드/효과 연쇄 변경의 묶음 리버트 실패");
        }

        var newIssues = ResearchWorkbookService.Validate(test)
            .Where(issue => issue.Severity == ResearchValidationSeverity.Error
                            && issue.RowIdentity.Contains(":new:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (newIssues.Count > 0)
            failures.Add($"신규 연구 데이터 검증 오류 {newIssues.Count}건: {newIssues[0].Message}");

        var roundTrip = ResearchWorkbookService.TestRoundTrip(test);
        if (!roundTrip.Success)
            failures.Add($"신규 연구 데이터 왕복 실패: first={roundTrip.FirstPassDifferences.Count}, second={roundTrip.SecondPassDifferences.Count}, newErrors={roundTrip.NewValidationIssues.Count}");

        var deletion = test.DeepClone();
        var firstToDelete = deletion.Nodes.First(row => row.Column == 102 && row.Category == renamedCategory.Category);
        if (ResearchWorkbookService.TryDeleteNode(deletion, firstToDelete.RowIdentity).Success)
            failures.Add("참조 중인 선행 노드가 보호되지 않음");
        if (!ResearchWorkbookService.TryDeleteNode(deletion, firstToDelete.RowIdentity, removeConditionReferences: true).Success)
            failures.Add("연쇄 참조 제거를 포함한 노드 삭제 실패");
        if (ResearchWorkbookService.TryDeleteCategory(deletion, category.RowIdentity).Success)
            failures.Add("노드가 남은 카테고리가 보호되지 않음");
        if (!ResearchWorkbookService.TryDeleteCategory(deletion, category.RowIdentity, deleteContainedNodes: true).Success)
            failures.Add("노드/효과를 포함한 카테고리 연쇄 삭제 실패");

        RunResearchExternalConflictQa(source, failures);

        return failures;
    }

    private static void RunResearchExternalConflictQa(
        ResearchWorkbookContext source,
        ICollection<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "NexusEditor", "ResearchConflictQa", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outCopy = Path.Combine(root, Path.GetFileName(source.SourceOutSystemPath));
        var effectCopy = Path.Combine(root, Path.GetFileName(source.SourceEffectPath));
        try
        {
            File.Copy(source.SourceOutSystemPath, outCopy);
            File.Copy(source.SourceEffectPath, effectCopy);
            var loaded = ResearchWorkbookService.Load(outCopy, effectCopy);
            using (var stream = new FileStream(outCopy, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                stream.WriteByte(0);
            try
            {
                ResearchWorkbookService.SaveAtomic(loaded, new ResearchSaveOptions { CreateBackup = false });
                failures.Add("외부 DB 변경 충돌 차단 실패");
            }
            catch (IOException ex) when (ex.Message.Contains("외부에서 변경", StringComparison.Ordinal))
            {
                // Expected: an external edit must be reloaded before export.
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static async Task PrepareClickSoundCacheAsync(SplashWindow splash)
    {
        var settings = NexusPathResolver.LoadSettings();
        var devRoot = NexusPathResolver.ResolveDefaultDevRoot(settings);
        if (devRoot is null)
            return;

        var cacheRoot = NexusPathResolver.ResolveSoundCacheRoot(settings);
        settings.SoundCacheRoot = cacheRoot;
        var service = new ClickSoundCatalogService(devRoot, cacheRoot);
        try
        {
            splash.SetStatus("Indexing FMOD sound banks...");
            var scanProgress = new Progress<ClickSoundScanProgress>(item =>
            {
                splash.SetStatus($"Indexing FMOD banks {item.Processed}/{item.Total}: {item.CurrentBank}");
                splash.SetProgress(item.Processed, item.Total);
            });
            var catalog = await service.LoadCatalogAsync(scanProgress, CancellationToken.None);
            if (catalog.Sounds.Count == 0)
                return;
            var missing = catalog.Sounds.Where(sound => !service.IsCached(sound)).ToList();
            if (missing.Count == 0)
                return;

            if (string.IsNullOrWhiteSpace(settings.SoundCacheMode))
            {
                var answer = ThemedMessageBox.Show(
                    $"FMOD 사운드 {catalog.Sounds.Count:N0}개를 미리 WAV 캐시로 추출할까요?\n\n" +
                    "예: 지금 로딩 화면에서 모두 추출한 뒤 에디터를 엽니다. 디스크 용량과 시간이 많이 필요할 수 있습니다.\n" +
                    "아니요: 목록은 모두 표시하고, 재생한 음원만 그때 캐시합니다.\n\n" +
                    $"캐시 위치: {cacheRoot}\nDEV/SVN 폴더에는 파일을 만들지 않습니다.",
                    "Click Sound Cache", MessageBoxButton.YesNo, MessageBoxImage.Question);
                settings.SoundCacheMode = answer == MessageBoxResult.Yes ? "precache" : "lazy";
                NexusPathResolver.SaveSettings(settings);
            }

            if (!string.Equals(settings.SoundCacheMode, "precache", StringComparison.OrdinalIgnoreCase))
                return;

            splash.SetStatus($"Preparing sound cache 0/{missing.Count}...");
            splash.SetProgress(0, missing.Count);
            var cacheProgress = new Progress<ClickSoundCacheProgress>(item =>
            {
                splash.SetStatus($"Preparing sound cache {item.Processed}/{item.Total}: {item.CurrentSound}");
                splash.SetProgress(item.Processed, item.Total);
            });
            await service.PrecacheAsync(missing, cacheProgress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            splash.SetStatus($"Sound cache skipped: {ex.Message}");
            await Task.Delay(1200);
        }
        finally
        {
            NexusPathResolver.SaveSettings(settings);
            splash.SetProgress(0, 0);
        }
    }

    public async void OpenWorkspace(WorkspaceMode mode, Window? previous = null)
    {
        if (previous is not null)
            previous.IsEnabled = false;
        var splash = new SplashWindow(mode == WorkspaceMode.Event
            ? "Opening Event Editor..."
            : "Opening Research Editor...");
        CopyWindowPlacement(previous, splash);
        splash.Show();
        try
        {
            Window next;
            if (mode == WorkspaceMode.Event)
            {
                await PrepareClickSoundCacheAsync(splash);
                var eventWindow = new MainWindow();
                next = eventWindow;
                CopyWindowPlacement(previous, eventWindow);
                eventWindow.WindowState = WindowState.Maximized;
                MainWindow = eventWindow;
                eventWindow.Show();
                eventWindow.BeginInitialLoad();
            }
            else
            {
                ResearchEditorWindow? researchWindow = null;
                researchWindow = new ResearchEditorWindow(target => OpenWorkspace(target, researchWindow));
                next = researchWindow;
                CopyWindowPlacement(previous, researchWindow);
                researchWindow.WindowState = WindowState.Maximized;
                MainWindow = researchWindow;
                researchWindow.Show();
                if (!await researchWindow.BeginInitialLoadAsync())
                {
                    splash.Close();
                    researchWindow.PrepareForWorkspaceClose();
                    researchWindow.Close();
                    if (previous is not null)
                    {
                        previous.IsEnabled = true;
                        if (previous is NexusHubWindow hub)
                            hub.ResetSelection();
                        previous.Show();
                        previous.Activate();
                        MainWindow = previous;
                    }
                    return;
                }
            }

            splash.Close();
            if (previous is not null && previous != next)
            {
                if (previous is ResearchEditorWindow researchEditor)
                    researchEditor.PrepareForWorkspaceClose();
                previous.Close();
            }
        }
        catch (Exception ex)
        {
            splash.Close();
            ThemedMessageBox.Show(ex.ToString(), "Workspace startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            if (previous is not null)
            {
                previous.IsEnabled = true;
                if (previous is NexusHubWindow hub)
                    hub.ResetSelection();
                previous.Show();
                previous.Activate();
                MainWindow = previous;
            }
        }
    }

    private static void CopyWindowPlacement(Window? source, Window target)
    {
        if (source is null)
            return;
        target.WindowStartupLocation = WindowStartupLocation.Manual;
        if (source.WindowState == WindowState.Normal)
        {
            target.Left = source.Left;
            target.Top = source.Top;
            target.Width = source.Width;
            target.Height = source.Height;
        }
        target.WindowState = source.WindowState;
    }

    private static void RunDevProbeMode()
    {
        TryEnableUtf8Console();
        var instances = FindConfiguredDevInstances();
        foreach (var instance in instances)
        {
            Console.WriteLine($"PID={instance.ProcessId} title={instance.GameWindowTitle} console={instance.HasConsole} input=0x{instance.ConsoleInputHandle.ToInt64():X} output=0x{instance.ConsoleOutputHandle.ToInt64():X} path={instance.ExecutablePath}");
        }
        Console.WriteLine($"DEV instances: {instances.Count}");
        Environment.ExitCode = instances.Any(instance => instance.HasConsole) ? 0 : 5;
    }

    private static void RunDevEventMode(string[] args)
    {
        TryEnableUtf8Console();
        if (args.Length < 3 || !int.TryParse(args[1], out var processId))
        {
            Console.Error.WriteLine("Usage: --dev-run-event <pid> <event-id>");
            Environment.ExitCode = 6;
            return;
        }

        var instance = FindConfiguredDevInstances().FirstOrDefault(candidate => candidate.ProcessId == processId);
        if (instance is null)
        {
            Console.Error.WriteLine($"DEV PID {processId} not found.");
            Environment.ExitCode = 7;
            return;
        }

        var result = EpicSevenDevClientService.SendRunEvent(instance, args[2]);
        Console.WriteLine(result.Message);
        Environment.ExitCode = result.Success ? 0 : 8;
    }

    private static void RunDevConsoleReadMode(string[] args)
    {
        TryEnableUtf8Console();
        if (args.Length < 2 || !int.TryParse(args[1], out var processId))
        {
            Console.Error.WriteLine("Usage: --dev-read-console <pid>");
            Environment.ExitCode = 9;
            return;
        }

        var instance = FindConfiguredDevInstances().FirstOrDefault(candidate => candidate.ProcessId == processId);
        if (instance is null)
        {
            Console.Error.WriteLine($"DEV PID {processId} not found.");
            Environment.ExitCode = 10;
            return;
        }

        var text = EpicSevenDevClientService.ReadConsoleText(instance);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(20))
            Console.WriteLine(line);
        Console.WriteLine($"DEV console chars: {text.Length}");
        Environment.ExitCode = text.Length > 0 ? 0 : 11;
    }

    private static IReadOnlyList<EpicSevenDevInstance> FindConfiguredDevInstances()
    {
        var settings = NexusPathResolver.LoadSettings();
        var devRoot = NexusPathResolver.ResolveDefaultDevRoot(settings);
        return EpicSevenDevClientService.FindInstances(devRoot);
    }

    private static void RunSoundProbeMode(string[] args)
    {
        TryEnableUtf8Console();
        var settings = NexusPathResolver.LoadSettings();
        var devRoot = args.Length > 1 ? NexusPathResolver.FindDevRoot(args[1]) : NexusPathResolver.ResolveDefaultDevRoot(settings);
        if (devRoot is null)
        {
            Console.Error.WriteLine("Sound probe failed: DEV root not found.");
            Environment.ExitCode = 12;
            return;
        }
        var cacheRoot = args.Length > 2 ? Path.GetFullPath(args[2]) : NexusPathResolver.ResolveSoundCacheRoot(settings);
        try
        {
            var service = new ClickSoundCatalogService(devRoot, cacheRoot);
            var catalog = service.LoadCatalogAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"Sound probe: {catalog.Sounds.Count} sounds / signature={catalog.Signature}");
            Console.WriteLine($"Sound cache: {cacheRoot}");
            Console.WriteLine($"Decoder ready: {service.DecoderAvailable}");
            foreach (var sound in catalog.Sounds.Take(3))
                Console.WriteLine($"{sound.FmodPath}\t{sound.RelativeBankPath}\tsubsong={sound.Subsong}");
            if (args.Skip(3).Any(value => string.Equals(value, "--decode-first", StringComparison.OrdinalIgnoreCase)))
            {
                var first = catalog.Sounds.First();
                var wav = service.EnsureDecodedAsync(first, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"Decoded: {first.FmodPath} -> {wav} ({new FileInfo(wav).Length:N0} bytes)");
            }
            Environment.ExitCode = catalog.Sounds.Count > 0 && service.DecoderAvailable ? 0 : 13;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sound probe failed: {ex}");
            Environment.ExitCode = 14;
        }
    }

    private static void TryEnableUtf8Console()
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { }
    }
}
