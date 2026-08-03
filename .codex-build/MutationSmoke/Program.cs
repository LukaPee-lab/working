using NexusEditor;

var outSystemPath = @"D:\repos\design\DB\alpha\nexus_out_system 차원_탐사_아웃시스템.xlsx";
var effectPath = @"D:\repos\design\DB\alpha\nexus_effect 차원 탐사 효과.xlsx";
var context = ResearchWorkbookService.Load(outSystemPath, effectPath);

var deleteContext = context.DeepClone(includeOriginalSnapshot: false);
var selected = deleteContext.Nodes.Take(2).ToArray();
var selectedIdentities = selected.Select(node => node.RowIdentity).ToArray();
var removedNodeIds = selected.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
var linkedEffects = selected.Select(deleteContext.FindEffect).Where(effect => effect is not null).ToArray();
var beforeNodeCount = deleteContext.Nodes.Count;
var beforeEffectCount = deleteContext.Effects.Count;
var deleteResult = ResearchWorkbookService.TryDeleteNodesInPlace(
    deleteContext,
    selectedIdentities,
    removeConditionReferences: true);
if (!deleteResult.Success
    || deleteContext.Nodes.Count != beforeNodeCount - selected.Length
    || deleteContext.Effects.Count != beforeEffectCount - linkedEffects.Length
    || deleteContext.Nodes.Any(node => node.Conditions.Any(removedNodeIds.Contains)))
{
    throw new InvalidOperationException("Batch delete invariant failed.");
}

var moveContext = context.DeepClone(includeOriginalSnapshot: false);
var node = moveContext.Nodes.First();
var occupiedRows = moveContext.Nodes
    .Where(candidate => candidate.Category.Equals(node.Category, StringComparison.OrdinalIgnoreCase)
                        && candidate.Column == node.Column)
    .Select(candidate => candidate.Row)
    .ToHashSet();
var targetRow = Enumerable.Range(
        ResearchWorkbookService.MinResearchRow,
        ResearchWorkbookService.MaxResearchRow - ResearchWorkbookService.MinResearchRow + 1)
    .First(row => !occupiedRows.Contains(row));
var oldId = node.Id;
var rowIdentity = node.RowIdentity;
var moveResult = ResearchWorkbookService.TryMoveNodesInPlace(
    moveContext,
    [rowIdentity],
    deltaColumn: 0,
    deltaRow: targetRow - node.Row);
var moved = moveContext.Nodes.Single(candidate => candidate.RowIdentity == rowIdentity);
if (!moveResult.Success
    || moved.Row != targetRow
    || moved.Id.Equals(oldId, StringComparison.OrdinalIgnoreCase)
    || moveContext.Nodes.Any(candidate => candidate.Conditions.Any(
        condition => condition.Equals(oldId, StringComparison.OrdinalIgnoreCase))))
{
    throw new InvalidOperationException("Row move invariant failed.");
}

Console.WriteLine($"BATCH_DELETE_OK nodes={beforeNodeCount}->{deleteContext.Nodes.Count} effects={beforeEffectCount}->{deleteContext.Effects.Count}");
Console.WriteLine($"ROW_MOVE_OK {oldId}->{moved.Id} row={targetRow}");
