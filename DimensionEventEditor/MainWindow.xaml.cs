using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IoPath = System.IO.Path;

namespace DimensionEventEditor;

public partial class MainWindow : Window
{
    private const double NodeWidth = 340;
    private const double NodeMinHeight = 138;
    private const double NodeTitleHeight = 32;
    private const double NodeSituationTop = 40;
    private const double NodeSituationHeight = 44;
    private const double ChoiceStartY = 96;
    private const double ChoiceRowHeight = 42;
    private const double ChoiceRowGap = 8;
    private const double PinSize = 15;
    private const double RewardNodeWidth = 190;
    private const double RewardNodeHeight = 66;
    private const double BattleNodeWidth = 210;
    private const double BattleNodeHeight = 72;
    private const double ExitNodeWidth = 180;
    private const double ExitNodeHeight = 64;
    private const string DefaultBattleStageId = "s1_event_001_01";
    private const string PendingRewardPrefix = "pending_reward|";
    private const string PendingBattlePrefix = "pending_battle|";
    private const string PendingExitPrefix = "pending_exit|";
    private const string ChoiceExitPrefix = "choice_exit|";
    private const string GroupRewardPrefix = "group_reward|";
    private const string DefaultBackgroundImageRoot = @"D:\repos\dev\game\Resources\res\nexus";
    private static readonly string[] BackgroundImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];
    private const double GraphDefaultX = 4200;
    private const double GraphDefaultY = 2600;
    private const double LegacyLayoutMaxRight = 2200;
    private const double LegacyLayoutMaxBottom = 1800;
    private const double MinZoom = 0.35;
    private const double MaxZoom = 2.5;
    private static readonly Regex NodeIdRegex = new(@"\bs\d+_EVT_\d{3}(?:_[A-Za-z0-9]+)*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static double RoundCoord(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
    private static double RoundCanvasCoord(double value) => RoundCoord(Math.Max(0, value));

    private EventWorkbook? _workbook;
    private EventBaseRow? _selectedEvent;
    private ChoiceGroupRow? _selectedGroup;
    private EventChoiceRow? _selectedChoice;
    private string? _selectedObjectKey;
    private string? _dragLayoutKey;
    private Point _dragOffset;
    private bool _dragUndoPushed;
    private string? _linkGroupActionId;
    private string? _linkChoiceId;
    private string? _linkBranch;
    private Line? _linkPreviewLine;
    private readonly ScaleTransform _graphScale = new(1, 1);
    private double _zoom = 1.0;
    private AppSettings _settings = new();
    private bool _isPanning;
    private Point _panStart;
    private Point _rightDownGraphPoint;
    private double _panStartHorizontal;
    private double _panStartVertical;
    private bool _rightDragMoved;
    private bool _rightDownOnCanvas;
    private ChoiceGroupRow? _copiedGroup;
    private List<EventChoiceRow> _copiedChoices = [];
    private readonly List<string> _notiMessages = [];
    private readonly List<ConsoleLogEntry> _warningMessages = [];
    private readonly List<ConsoleLogEntry> _errorMessages = [];
    private readonly Stack<EventWorkbook> _undoStack = [];
    private readonly HashSet<string> _selectedNodeKeys = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Point> _multiDragStartPositions = [];
    private bool _isDraggingNodeSelection;
    private Point _multiDragStartMouse;
    private bool _isBoxSelecting;
    private Point _boxSelectStart;
    private Rectangle? _boxSelectVisual;
    private bool _centerGraphOnNextDraw;
    private bool _initialLoadStarted;
    private bool _isLoadingWorkbook;
    private bool _suppressEventSelectionChanged;
    private Process? _playerProcess;
    private bool _playerPaused;
    private bool _runtimeLocked;
    private string? _playerExePathInUse;
    private string? _runtimeRelicWorkbookPath;
    private readonly DispatcherTimer _playerWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    public MainWindow()
    {
        InitializeComponent();
        _settings = NexusPathResolver.LoadSettings();
        ApplyLayoutCache();
        GraphCanvas.LayoutTransform = _graphScale;
        EventList.PreviewMouseRightButtonDown += EventList_PreviewMouseRightButtonDown;
        _playerWatchTimer.Tick += PlayerWatchTimer_Tick;
        UpdateRuntimeUiState();
    }

    public void BeginInitialLoad()
    {
        if (_initialLoadStarted)
            return;

        _initialLoadStarted = true;
        _ = LoadInitialWorkbookAsync();
    }

    private void LoadInitialWorkbook()
    {
        _ = LoadInitialWorkbookAsync();
    }

    private async Task LoadInitialWorkbookAsync()
    {
        var path = NexusPathResolver.ResolveDefaultEventWorkbook();
        if (!string.IsNullOrWhiteSpace(path))
            await LoadWorkbookAsync(path, "Loading DB workbook...");
        else
        {
            Log("nexus_event 테이블을 찾지 못했습니다. Open DB로 파일을 선택하세요.");
            PromptOpenWorkbook();
        }
    }

    private void LoadWorkbook(string path)
    {
        _ = LoadWorkbookAsync(path, "Loading DB workbook...");
    }

    private async Task LoadWorkbookAsync(string path, string status)
    {
        if (_isLoadingWorkbook)
        {
            Log("Load skipped: another workbook load is already running.");
            return;
        }

        SplashWindow? busy = null;
        try
        {
            _isLoadingWorkbook = true;
            busy = ShowBusy(status);
            var workbook = await Task.Run(() => EventWorkbookService.Load(path));
            _workbook = workbook;
            _settings.EventWorkbookPath = path;
            _settings.ReposRoot = FindReposRoot(path);
            NexusPathResolver.SaveSettings(_settings);
            PathText.Text = path;
            _selectedEvent = _workbook.Events
                .OrderBy(e => e.Id)
                .FirstOrDefault(e => string.Equals(e.Id, _selectedEvent?.Id, StringComparison.OrdinalIgnoreCase))
                ?? _workbook.Events.OrderBy(e => e.Id).FirstOrDefault();
            _selectedGroup = null;
            _selectedChoice = null;
            _selectedObjectKey = null;
            _selectedNodeKeys.Clear();
            _undoStack.Clear();
            _centerGraphOnNextDraw = true;
            RefreshEventList();
            RefreshHierarchy();
            RefreshIssues();
            Log($"로드 완료: {_workbook.Events.Count} events / {_workbook.Groups.Count} groups / {_workbook.Choices.Count} choices");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            busy?.Close();
            _isLoadingWorkbook = false;
            Activate();
        }
    }

    private static string? FindReposRoot(string path)
    {
        var dir = IoPath.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (string.Equals(IoPath.GetFileName(dir), "repos", StringComparison.OrdinalIgnoreCase))
                return dir;
            dir = IoPath.GetDirectoryName(dir);
        }
        return null;
    }

    private void ApplyLayoutCache()
    {
        SetColumnWidth(LeftPaneColumn, _settings.LeftPaneWidth, 180, 520);
        SetColumnWidth(RightPaneColumn, _settings.RightPaneWidth, 520, 1500);
        SetColumnWidth(HierarchyPaneColumn, _settings.HierarchyPaneWidth, 140, 720);
        SetRowHeight(ConsoleRow, _settings.ConsoleHeight, 72, 760);
    }

    private static void SetColumnWidth(ColumnDefinition column, double value, double min, double max)
    {
        if (double.IsNaN(value) || value <= 0)
            return;
        column.Width = new GridLength(Math.Clamp(value, min, max));
    }

    private static void SetRowHeight(RowDefinition row, double value, double min, double max)
    {
        if (double.IsNaN(value) || value <= 0)
            return;
        row.Height = new GridLength(Math.Clamp(value, min, max));
    }

    private void SaveLayoutCache()
    {
        _settings.LeftPaneWidth = LeftPaneColumn.ActualWidth > 0 ? LeftPaneColumn.ActualWidth : LeftPaneColumn.Width.Value;
        _settings.RightPaneWidth = RightPaneColumn.ActualWidth > 0 ? RightPaneColumn.ActualWidth : RightPaneColumn.Width.Value;
        _settings.HierarchyPaneWidth = HierarchyPaneColumn.ActualWidth > 0 ? HierarchyPaneColumn.ActualWidth : HierarchyPaneColumn.Width.Value;
        _settings.ConsoleHeight = ConsoleRow.ActualHeight > 0 ? ConsoleRow.ActualHeight : ConsoleRow.Height.Value;
        NexusPathResolver.SaveSettings(_settings);
    }

    private void RefreshEventList()
    {
        if (_workbook is null)
            return;
        var selectedEventId = _selectedEvent?.Id;
        var query = SearchBox.Text?.Trim() ?? "";
        var events = _workbook.Events
            .Where(e => string.IsNullOrWhiteSpace(query)
                || e.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Memo.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Id)
            .ToList();
        _suppressEventSelectionChanged = true;
        try
        {
            EventList.ItemsSource = events;
            EventList.SelectedItem = !string.IsNullOrWhiteSpace(selectedEventId)
                ? events.FirstOrDefault(e => string.Equals(e.Id, selectedEventId, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        finally
        {
            _suppressEventSelectionChanged = false;
        }
    }

    private void RefreshIssues()
    {
        _warningMessages.Clear();
        _errorMessages.Clear();
        if (_workbook is null)
        {
            RefreshConsoleLists();
            return;
        }
        foreach (var issue in EventWorkbookService.Validate(_workbook))
            AddIssue(issue);
        foreach (var issue in CompileSelectedEventLogic())
            AddIssue(issue);
        RefreshConsoleLists();
    }

    private void AddIssue(ValidationIssue issue)
    {
        var text = issue.ToString();
        var entry = CreateConsoleEntry(
            issue.Severity == ValidationSeverity.Error ? "Error" : "Warning",
            text);
        if (issue.Severity == ValidationSeverity.Error)
            _errorMessages.Add(entry);
        else
            _warningMessages.Add(entry);
    }

    private void RefreshConsoleLists()
    {
        var filter = ConsoleSearchBox?.Text?.Trim() ?? "";
        static bool Matches(string message, string filter)
            => string.IsNullOrWhiteSpace(filter) || message.Contains(filter, StringComparison.OrdinalIgnoreCase);
        static bool MatchesEntry(ConsoleLogEntry entry, string filter)
            => Matches(entry.Message, filter)
               || (!string.IsNullOrWhiteSpace(entry.TargetId)
                   && entry.TargetId.Contains(filter, StringComparison.OrdinalIgnoreCase));

        NotiList.Items.Clear();
        WarningList.Items.Clear();
        ErrorList.Items.Clear();

        foreach (var message in _notiMessages.TakeLast(500).Where(m => Matches(m, filter)))
            NotiList.Items.Add(message);
        foreach (var entry in _warningMessages.Where(m => MatchesEntry(m, filter)))
            WarningList.Items.Add(CreateConsoleIssueListItem(entry));
        foreach (var entry in _errorMessages.Where(m => MatchesEntry(m, filter)))
            ErrorList.Items.Add(CreateConsoleIssueListItem(entry));

        ConsoleNotiCount.Text = _notiMessages.Count.ToString();
        ConsoleWarningCount.Text = _warningMessages.Count.ToString();
        ConsoleErrorCount.Text = _errorMessages.Count.ToString();
    }

    private ConsoleLogEntry CreateConsoleEntry(string severity, string message)
    {
        var (targetId, targetKind) = TryResolveConsoleTarget(message);
        return new ConsoleLogEntry
        {
            Severity = severity,
            Message = message,
            TargetId = targetId,
            TargetKind = targetKind
        };
    }

    private ListBoxItem CreateConsoleIssueListItem(ConsoleLogEntry entry)
    {
        var foreground = entry.Severity == "Error"
            ? new SolidColorBrush(Color.FromRgb(255, 128, 104))
            : new SolidColorBrush(Color.FromRgb(255, 204, 86));
        var item = new ListBoxItem
        {
            Content = entry.Message,
            Tag = entry,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
            Background = Brushes.Transparent,
            Cursor = entry.IsNavigable ? Cursors.Hand : Cursors.Arrow,
            ToolTip = entry.IsNavigable
                ? $"{entry.TargetKind}: {entry.TargetId}\nClick to focus the node."
                : "No matching node target was found."
        };
        item.PreviewMouseLeftButtonUp += ConsoleIssueListItem_PreviewMouseLeftButtonUp;

        var panel = new DockPanel { LastChildFill = true };
        var icon = new TextBlock
        {
            Text = "!",
            Foreground = foreground,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(icon, Dock.Left);
        panel.Children.Add(icon);

        panel.Children.Add(new TextBlock
        {
            Text = entry.Message,
            Foreground = new SolidColorBrush(Color.FromRgb(218, 218, 218)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        item.Content = panel;
        return item;
    }

    private void ConsoleIssueListItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { Tag: ConsoleLogEntry entry })
            FocusConsoleEntryTarget(entry);
    }

    private (string TargetId, string TargetKind) TryResolveConsoleTarget(string message)
    {
        if (_workbook is null)
            return ("", "");

        foreach (var candidate in NodeIdRegex.Matches(message)
                     .Cast<Match>()
                     .Select(m => m.Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_workbook.Choices.Any(c => string.Equals(c.Id, candidate, StringComparison.OrdinalIgnoreCase)))
                return (candidate, "choice");
            if (_workbook.Groups.Any(g => string.Equals(g.Id, candidate, StringComparison.OrdinalIgnoreCase)))
                return (candidate, "scene");
            if (_workbook.Events.Any(e => string.Equals(e.Id, candidate, StringComparison.OrdinalIgnoreCase)))
                return (candidate, "event");
        }

        return ("", "");
    }

    private void FocusConsoleEntryTarget(ConsoleLogEntry entry)
    {
        if (_workbook is null || !entry.IsNavigable)
            return;

        var targetId = entry.TargetId;
        var choice = _workbook.Choices.FirstOrDefault(c => string.Equals(c.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (choice is not null)
        {
            var group = _workbook.Groups.FirstOrDefault(g => string.Equals(g.Id, choice.GroupId, StringComparison.OrdinalIgnoreCase));
            var evt = group is null ? null : _workbook.Events.FirstOrDefault(e => string.Equals(e.Id, group.EventId, StringComparison.OrdinalIgnoreCase));
            if (group is null || evt is null)
                return;

            SelectEventForNavigation(evt);
            _selectedNodeKeys.Clear();
            _selectedNodeKeys.Add(group.Id);
            _selectedObjectKey = null;
            _selectedGroup = group;
            _selectedChoice = choice;
            DrawGraph();
            CenterGraphOnNode(group.Id);
            return;
        }

        var targetGroup = _workbook.Groups.FirstOrDefault(g => string.Equals(g.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (targetGroup is not null)
        {
            var evt = _workbook.Events.FirstOrDefault(e => string.Equals(e.Id, targetGroup.EventId, StringComparison.OrdinalIgnoreCase));
            if (evt is null)
                return;

            SelectEventForNavigation(evt);
            _selectedNodeKeys.Clear();
            _selectedNodeKeys.Add(targetGroup.Id);
            _selectedObjectKey = null;
            _selectedGroup = targetGroup;
            _selectedChoice = null;
            DrawGraph();
            CenterGraphOnNode(targetGroup.Id);
            return;
        }

        var targetEvent = _workbook.Events.FirstOrDefault(e => string.Equals(e.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (targetEvent is not null)
        {
            SelectEventForNavigation(targetEvent);
            _selectedNodeKeys.Clear();
            _selectedObjectKey = null;
            _selectedGroup = null;
            _selectedChoice = null;
            DrawGraph();
        }
    }

    private void SelectEventForNavigation(EventBaseRow evt)
    {
        var previousSuppress = _suppressEventSelectionChanged;
        _suppressEventSelectionChanged = true;
        try
        {
            _selectedEvent = evt;
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                SearchBox.Text = "";
            RefreshEventList();
            EventList.SelectedItem = evt;
            EventList.ScrollIntoView(evt);
        }
        finally
        {
            _suppressEventSelectionChanged = previousSuppress;
        }
    }

    private void CenterGraphOnNode(string layoutKey)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(layoutKey, out var layout))
            return;

        var width = Math.Max(layout.Width, layoutKey.StartsWith("reward|", StringComparison.OrdinalIgnoreCase) || layoutKey.StartsWith(GroupRewardPrefix, StringComparison.OrdinalIgnoreCase)
            ? RewardNodeWidth
            : layoutKey.StartsWith("battle|", StringComparison.OrdinalIgnoreCase)
                ? BattleNodeWidth
                : layoutKey.StartsWith("exit|", StringComparison.OrdinalIgnoreCase) || layoutKey.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase)
                    ? ExitNodeWidth
                    : NodeWidth);
        var height = Math.Max(layout.Height, layoutKey.StartsWith("reward|", StringComparison.OrdinalIgnoreCase) || layoutKey.StartsWith(GroupRewardPrefix, StringComparison.OrdinalIgnoreCase)
            ? RewardNodeHeight
            : layoutKey.StartsWith("battle|", StringComparison.OrdinalIgnoreCase)
                ? BattleNodeHeight
                : layoutKey.StartsWith("exit|", StringComparison.OrdinalIgnoreCase) || layoutKey.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase)
                    ? ExitNodeHeight
                    : NodeMinHeight);

        Dispatcher.BeginInvoke(() =>
        {
            var viewWidth = GraphScroll.ViewportWidth > 0 ? GraphScroll.ViewportWidth : GraphScroll.ActualWidth;
            var viewHeight = GraphScroll.ViewportHeight > 0 ? GraphScroll.ViewportHeight : GraphScroll.ActualHeight;
            if (viewWidth <= 0 || viewHeight <= 0)
                return;

            GraphScroll.ScrollToHorizontalOffset(Math.Max(0, (layout.X + width / 2) * _zoom - viewWidth / 2));
            GraphScroll.ScrollToVerticalOffset(Math.Max(0, (layout.Y + height / 2) * _zoom - viewHeight / 2));
        }, DispatcherPriority.Loaded);
    }

    private void DrawGraph()
    {
        GraphCanvas.Children.Clear();

        if (_workbook is null || _selectedEvent is null)
            return;

        var groups = _workbook.Groups.Where(g => g.EventId == _selectedEvent.Id).OrderBy(g => g.Id).ToList();
        EnsureLayouts(groups);
        NormalizeEventLayoutIfNeeded(groups);
        var groupSet = groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = _workbook.Choices.Where(c => groupSet.Contains(c.GroupId)).ToList();

        var choicesByGroup = choices
            .GroupBy(c => c.GroupId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Seq).ToList(), StringComparer.OrdinalIgnoreCase);
        var groupsById = groups.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var choice in choices)
        {
            DrawBranch(choice, "success", choice.SuccessRewardType, choice.SuccessRewardAmount, choice.SuccessNextGroupId, groupSet, groupsById, choicesByGroup);
            if (IsFailPinEnabled(choice))
                DrawBranch(choice, "fail", choice.FailRewardType, choice.FailRewardAmount, choice.FailNextGroupId, groupSet, groupsById, choicesByGroup);
        }

        foreach (var group in groups.Where(IsBattleGroup))
            DrawBattleNode(group);
        foreach (var group in groups.Where(IsGroupRewardTerminal))
            DrawGroupRewardNode(group);
        foreach (var group in groups.Where(g => IsExitGroup(g) && !IsGroupRewardTerminal(g)))
            DrawExitNode(group, choicesByGroup);
        DrawPendingObjectNodes();

        foreach (var group in groups)
            DrawNode(group, choicesByGroup.TryGetValue(group.Id, out var groupChoices) ? groupChoices : []);

        RefreshHierarchy();
        if (_selectedNodeKeys.Count > 1)
            BuildMultiSelectionInspector();
        else if (_selectedChoice is not null && _workbook.Choices.Contains(_selectedChoice))
            BuildChoiceInspector(_selectedChoice);
        else if (_selectedGroup is not null && _workbook.Groups.Contains(_selectedGroup))
            BuildGroupInspector(_selectedGroup);
        else
            BuildEventInspector();

        if (_centerGraphOnNextDraw)
        {
            _centerGraphOnNextDraw = false;
            CenterGraphOnGroups(groups);
        }
    }

    private void RefreshHierarchy()
    {
        HierarchyTree.Items.Clear();
        if (_workbook is null || _selectedEvent is null)
            return;

        var groups = _workbook.Groups.Where(g => g.EventId == _selectedEvent.Id).OrderBy(g => g.Id).ToList();
        foreach (var group in groups)
        {
            var isSelectedGroup = string.Equals(_selectedGroup?.Id, group.Id, StringComparison.OrdinalIgnoreCase);
            var groupItem = new TreeViewItem
            {
                Header = HierarchyHeader($"▣ {group.Id}  {TrimForHeader(group.Memo)}", GroupHierarchyDetail(group), isSelectedGroup),
                Tag = $"group|{group.Id}",
                IsExpanded = true
            };
            foreach (var choice in _workbook.Choices.Where(c => c.GroupId == group.Id).OrderBy(c => c.Seq))
            {
                var isSelectedChoice = string.Equals(_selectedChoice?.Id, choice.Id, StringComparison.OrdinalIgnoreCase);
                groupItem.Items.Add(new TreeViewItem
                {
                    Header = HierarchyHeader($"  ↳ {choice.Seq}. {TrimForHeader(choice.Memo)}", ChoiceHierarchyDetail(choice), isSelectedChoice),
                    Tag = $"choice|{choice.Id}"
                });
            }
            HierarchyTree.Items.Add(groupItem);
        }
    }

    private static StackPanel HierarchyHeader(string text, string detail, bool selected)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 2, 0, 3) };
        stack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = selected
                ? new SolidColorBrush(Color.FromRgb(255, 231, 142))
                : new SolidColorBrush(Color.FromRgb(220, 220, 220)),
            FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
        }
        return stack;
    }

    private static string GroupHierarchyDetail(ChoiceGroupRow group)
    {
        var parts = new List<string>
        {
            $"bg={BlankDash(group.Background)}",
            $"action={BlankDash(group.NextAction)}",
            $"tid={BlankDash(group.SituationTextTid)}"
        };
        if (!string.IsNullOrWhiteSpace(group.NpcId))
            parts.Add($"npc={group.NpcId}");
        if (!string.IsNullOrWhiteSpace(group.StageId))
            parts.Add($"stage={group.StageId}");
        return string.Join(" / ", parts);
    }

    private static string ChoiceHierarchyDetail(EventChoiceRow choice)
    {
        var parts = new List<string>
        {
            $"cost={DescribeCost(choice)}",
            $"rate={(choice.SuccessRate is null ? "-" : choice.SuccessRate)}",
            $"T={DescribeBranch(choice.SuccessRewardType, choice.SuccessRewardAmount, choice.SuccessNextGroupId)}",
            $"F={(IsFailPinEnabled(choice) ? DescribeBranch(choice.FailRewardType, choice.FailRewardAmount, choice.FailNextGroupId) : "inactive")}",
            $"tid={BlankDash(choice.ChoiceTextTid)}"
        };
        return string.Join(" / ", parts);
    }

    private static string DescribeCost(EventChoiceRow choice)
        => choice.CostType == "none" ? "none" : $"{choice.CostType}:{choice.CostAmount?.ToString() ?? "-"}";

    private static string DescribeBranch(string rewardType, int? amount, string nextGroupId)
    {
        var reward = rewardType == "none" ? "none" : $"{rewardType}:{amount?.ToString() ?? "-"}";
        var next = string.IsNullOrWhiteSpace(nextGroupId) ? "-" : nextGroupId;
        return $"{reward}-> {next}";
    }

    private static string BlankDash(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string TrimForHeader(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 32 ? text : text[..32] + "...";
    }

    private void EnsureLayouts(IReadOnlyList<ChoiceGroupRow> groups)
    {
        if (_workbook is null || _selectedEvent is null)
            return;

        var x = 80.0;
        var y = 80.0;
        var index = 0;
        foreach (var group in groups)
        {
            if (!_workbook.Layouts.ContainsKey(group.Id))
            {
                _workbook.Layouts[group.Id] = new NodeLayout
                {
                    EventId = _selectedEvent.Id,
                    GroupId = group.Id,
                    X = x + (index % 3) * 420,
                    Y = y + (index / 3) * 260,
                    Width = NodeWidth,
                    Height = NodeMinHeight
                };
            }
            else
            {
                _workbook.Layouts[group.Id].Width = NodeWidth;
                _workbook.Layouts[group.Id].Height = CalculateNodeHeight(
                    _workbook.Choices.Count(c => c.GroupId == group.Id));
            }
            index++;
        }
    }

    private void AutoLayoutSelectedEvent()
    {
        if (_workbook is null || _selectedEvent is null)
            return;

        var groups = _workbook.Groups
            .Where(g => string.Equals(g.EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Id)
            .ToList();
        if (groups.Count == 0)
            return;

        var groupSet = groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groupById = groups.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var choicesByGroup = _workbook.Choices
            .Where(c => groupSet.Contains(c.GroupId))
            .GroupBy(c => c.GroupId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Seq).ToList(), StringComparer.OrdinalIgnoreCase);
        var order = groups.Select((g, i) => new { g.Id, Index = i })
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var first = groupSet.Contains(_selectedEvent.FirstGroupId) ? _selectedEvent.FirstGroupId : groups[0].Id;
        var queue = new Queue<string>();
        depth[first] = 0;
        queue.Enqueue(first);
        while (queue.Count > 0)
        {
            var groupId = queue.Dequeue();
            var nextDepth = depth[groupId] + 1;
            if (!choicesByGroup.TryGetValue(groupId, out var choices))
                continue;
            foreach (var nextId in choices
                         .SelectMany(c => new[] { c.SuccessNextGroupId, c.FailNextGroupId })
                         .Where(n => !string.IsNullOrWhiteSpace(n) && groupSet.Contains(n)))
            {
                if (depth.ContainsKey(nextId))
                    continue;
                depth[nextId] = nextDepth;
                queue.Enqueue(nextId);
            }
        }

        var fallbackDepth = depth.Count == 0 ? 0 : depth.Values.Max() + 1;
        foreach (var group in groups.Where(g => !depth.ContainsKey(g.Id)))
            depth[group.Id] = fallbackDepth++;

        const double xGap = 560;
        const double yGap = 230;
        var occupied = new List<Rect>();
        foreach (var column in groups
                     .GroupBy(g => depth[g.Id])
                     .OrderBy(g => g.Key))
        {
            var rows = column.OrderBy(g => order[g.Id]).ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                var group = rows[i];
                var choiceCount = choicesByGroup.TryGetValue(group.Id, out var choices) ? choices.Count : 0;
                var height = CalculateNodeHeight(choiceCount);
                var x = GraphDefaultX + column.Key * xGap;
                var y = GraphDefaultY + i * yGap;
                _workbook.Layouts[group.Id] = new NodeLayout
                {
                    EventId = _selectedEvent.Id,
                    GroupId = group.Id,
                    X = x,
                    Y = y,
                    Width = NodeWidth,
                    Height = height
                };
                occupied.Add(new Rect(x, y, NodeWidth, height));
            }
        }

        foreach (var group in groups.OrderBy(g => depth[g.Id]).ThenBy(g => order[g.Id]))
        {
            if (!choicesByGroup.TryGetValue(group.Id, out var choices))
                choices = [];
            foreach (var choice in choices)
            {
                AutoLayoutReward(choice, "success", choice.SuccessRewardType, choice.SuccessRewardAmount, choicesByGroup, occupied);
                if (IsFailPinEnabled(choice))
                    AutoLayoutReward(choice, "fail", choice.FailRewardType, choice.FailRewardAmount, choicesByGroup, occupied);
            }

            if (!_workbook.Layouts.TryGetValue(group.Id, out var groupLayout))
                continue;
            var groupRect = new Rect(groupLayout.X, groupLayout.Y, groupLayout.Width, groupLayout.Height);
            if (IsBattleGroup(group))
            {
                var rect = PlaceObject(new Rect(groupRect.Right + 120, groupRect.Top + 18, BattleNodeWidth, BattleNodeHeight), occupied);
                _workbook.Layouts[BattleLayoutKey(group.Id)] = LayoutFromRect(group.EventId, BattleLayoutKey(group.Id), rect);
            }
            else
            {
                _workbook.Layouts.Remove(BattleLayoutKey(group.Id));
            }

            if (IsGroupRewardTerminal(group))
            {
                var rect = PlaceObject(new Rect(groupRect.Right + 120, groupRect.Top + Math.Max(0, groupRect.Height - RewardNodeHeight) / 2, RewardNodeWidth, RewardNodeHeight), occupied);
                _workbook.Layouts[GroupRewardLayoutKey(group.Id)] = LayoutFromRect(group.EventId, GroupRewardLayoutKey(group.Id), rect);
                _workbook.Layouts.Remove(ExitLayoutKey(group.Id));
            }
            else if (IsExitGroup(group))
            {
                var rect = PlaceObject(new Rect(groupRect.Right + 120, groupRect.Top + Math.Max(0, groupRect.Height - ExitNodeHeight) / 2, ExitNodeWidth, ExitNodeHeight), occupied);
                _workbook.Layouts[ExitLayoutKey(group.Id)] = LayoutFromRect(group.EventId, ExitLayoutKey(group.Id), rect);
                _workbook.Layouts.Remove(GroupRewardLayoutKey(group.Id));
            }
            else
            {
                _workbook.Layouts.Remove(ExitLayoutKey(group.Id));
                _workbook.Layouts.Remove(GroupRewardLayoutKey(group.Id));
            }
        }
    }

    private void AutoLayoutReward(
        EventChoiceRow choice,
        string branch,
        string rewardType,
        int? rewardAmount,
        Dictionary<string, List<EventChoiceRow>> choicesByGroup,
        List<Rect> occupied)
    {
        if (_workbook is null || rewardType == "none" || !_workbook.Layouts.TryGetValue(choice.GroupId, out var groupLayout))
            return;
        var from = GetChoicePinPoint(choice, branch, choicesByGroup);
        var x = groupLayout.X + groupLayout.Width + 120;
        var y = from.Y - RewardNodeHeight / 2 + (branch == "fail" ? 22 : -22);
        var rect = PlaceObject(new Rect(x, y, RewardNodeWidth, RewardNodeHeight), occupied);
        var key = RewardLayoutKey(choice.Id, branch);
        _workbook.Layouts[key] = LayoutFromRect(groupLayout.EventId, key, rect);
    }

    private static NodeLayout LayoutFromRect(string eventId, string key, Rect rect) => new()
    {
        EventId = eventId,
        GroupId = key,
        X = RoundCanvasCoord(rect.X),
        Y = RoundCanvasCoord(rect.Y),
        Width = RoundCoord(rect.Width),
        Height = RoundCoord(rect.Height)
    };

    private static Rect PlaceObject(Rect preferred, List<Rect> occupied)
    {
        var rect = preferred;
        var guard = 0;
        while (occupied.Any(o => Inflate(o, 18).IntersectsWith(rect)) && guard++ < 80)
            rect = new Rect(rect.X, rect.Y + rect.Height + 24, rect.Width, rect.Height);
        occupied.Add(rect);
        return rect;
    }

    private static Rect Inflate(Rect rect, double margin)
    {
        rect.Inflate(margin, margin);
        return rect;
    }

    private void NormalizeEventLayoutIfNeeded(IReadOnlyList<ChoiceGroupRow> groups)
    {
        if (_workbook is null || _selectedEvent is null || groups.Count == 0)
            return;

        var layouts = groups
            .Select(g => _workbook.Layouts.TryGetValue(g.Id, out var layout) ? layout : null)
            .Where(l => l is not null)
            .Cast<NodeLayout>()
            .ToList();
        if (layouts.Count == 0)
            return;

        var minX = layouts.Min(l => l.X);
        var minY = layouts.Min(l => l.Y);
        var maxX = layouts.Max(l => l.X + Math.Max(l.Width, NodeWidth));
        var maxY = layouts.Max(l => l.Y + Math.Max(l.Height, NodeMinHeight));
        if (maxX > LegacyLayoutMaxRight || maxY > LegacyLayoutMaxBottom)
            return;

        var dx = GraphDefaultX - minX;
        var dy = GraphDefaultY - minY;
        var groupIds = groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choiceIds = _workbook.Choices
            .Where(c => groupIds.Contains(c.GroupId))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var layout in layouts)
        {
            layout.X = RoundCanvasCoord(layout.X + dx);
            layout.Y = RoundCanvasCoord(layout.Y + dy);
        }

        foreach (var pair in _workbook.Layouts)
        {
            if (pair.Key.StartsWith("battle|", StringComparison.OrdinalIgnoreCase)
                && groupIds.Contains(pair.Key.Split('|').ElementAtOrDefault(1) ?? ""))
            {
                pair.Value.X = RoundCanvasCoord(pair.Value.X + dx);
                pair.Value.Y = RoundCanvasCoord(pair.Value.Y + dy);
            }
            else if (pair.Key.StartsWith("exit|", StringComparison.OrdinalIgnoreCase)
                     && groupIds.Contains(pair.Key.Split('|').ElementAtOrDefault(1) ?? ""))
            {
                pair.Value.X = RoundCanvasCoord(pair.Value.X + dx);
                pair.Value.Y = RoundCanvasCoord(pair.Value.Y + dy);
            }
            else if (pair.Key.StartsWith(GroupRewardPrefix, StringComparison.OrdinalIgnoreCase)
                     && groupIds.Contains(pair.Key.Split('|').ElementAtOrDefault(1) ?? ""))
            {
                pair.Value.X = RoundCanvasCoord(pair.Value.X + dx);
                pair.Value.Y = RoundCanvasCoord(pair.Value.Y + dy);
            }
            else if (pair.Key.StartsWith("reward|", StringComparison.OrdinalIgnoreCase)
                     && choiceIds.Contains(pair.Key.Split('|').ElementAtOrDefault(1) ?? ""))
            {
                pair.Value.X = RoundCanvasCoord(pair.Value.X + dx);
                pair.Value.Y = RoundCanvasCoord(pair.Value.Y + dy);
            }
            else if (pair.Key.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase)
                     && choiceIds.Contains(pair.Key.Split('|').ElementAtOrDefault(1) ?? ""))
            {
                pair.Value.X = RoundCanvasCoord(pair.Value.X + dx);
                pair.Value.Y = RoundCanvasCoord(pair.Value.Y + dy);
            }
            else if ((pair.Key.StartsWith(PendingRewardPrefix, StringComparison.OrdinalIgnoreCase)
                      || pair.Key.StartsWith(PendingBattlePrefix, StringComparison.OrdinalIgnoreCase)
                      || pair.Key.StartsWith(PendingExitPrefix, StringComparison.OrdinalIgnoreCase))
                     && string.Equals(pair.Value.EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase))
            {
                pair.Value.X = RoundCanvasCoord(pair.Value.X + dx);
                pair.Value.Y = RoundCanvasCoord(pair.Value.Y + dy);
            }
        }
    }

    private void CenterGraphOnGroups(IReadOnlyList<ChoiceGroupRow> groups)
    {
        if (_workbook is null || groups.Count == 0)
            return;

        var rects = groups
            .Where(g => _workbook.Layouts.ContainsKey(g.Id))
            .Select(g =>
            {
                var layout = _workbook.Layouts[g.Id];
                return new Rect(layout.X, layout.Y, Math.Max(layout.Width, NodeWidth), Math.Max(layout.Height, NodeMinHeight));
            })
            .ToList();
        if (rects.Count == 0)
            return;

        var bounds = rects[0];
        foreach (var rect in rects.Skip(1))
            bounds.Union(rect);

        Dispatcher.BeginInvoke(() =>
        {
            var viewWidth = GraphScroll.ViewportWidth > 0 ? GraphScroll.ViewportWidth : GraphScroll.ActualWidth;
            var viewHeight = GraphScroll.ViewportHeight > 0 ? GraphScroll.ViewportHeight : GraphScroll.ActualHeight;
            if (viewWidth <= 0 || viewHeight <= 0)
                return;
            GraphScroll.ScrollToHorizontalOffset(Math.Max(0, (bounds.Left + bounds.Width / 2) * _zoom - viewWidth / 2));
            GraphScroll.ScrollToVerticalOffset(Math.Max(0, (bounds.Top + bounds.Height / 2) * _zoom - viewHeight / 2));
        }, DispatcherPriority.Loaded);
    }

    private Point VisibleCanvasCenter()
    {
        var width = GraphScroll.ViewportWidth > 0 ? GraphScroll.ViewportWidth : GraphScroll.ActualWidth;
        var height = GraphScroll.ViewportHeight > 0 ? GraphScroll.ViewportHeight : GraphScroll.ActualHeight;
        return new Point(
            (GraphScroll.HorizontalOffset + width / 2) / Math.Max(0.01, _zoom),
            (GraphScroll.VerticalOffset + height / 2) / Math.Max(0.01, _zoom));
    }

    private void DrawNode(ChoiceGroupRow group, List<EventChoiceRow> choices)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(group.Id, out var layout))
            return;

        layout.Width = NodeWidth;
        layout.Height = CalculateNodeHeight(choices.Count);

        var selected = _selectedNodeKeys.Contains(group.Id)
            || string.Equals(_selectedGroup?.Id, group.Id, StringComparison.OrdinalIgnoreCase)
            || choices.Any(c => string.Equals(_selectedChoice?.Id, c.Id, StringComparison.OrdinalIgnoreCase));
        var isStartNode = string.Equals(_selectedEvent?.FirstGroupId, group.Id, StringComparison.OrdinalIgnoreCase);

        var root = new Grid
        {
            Width = layout.Width,
            Height = layout.Height,
            Tag = group.Id
        };
        root.MouseLeftButtonDown += Node_MouseLeftButtonDown;

        var border = new Border
        {
            Width = layout.Width,
            Height = layout.Height,
            Background = new SolidColorBrush(selected
                ? (isStartNode ? Color.FromRgb(37, 55, 76) : Color.FromRgb(52, 52, 52))
                : (isStartNode ? Color.FromRgb(30, 45, 65) : Color.FromRgb(42, 42, 42))),
            BorderBrush = new SolidColorBrush(selected
                ? Color.FromRgb(222, 202, 116)
                : (isStartNode ? Color.FromRgb(77, 163, 255) : Color.FromRgb(98, 98, 98))),
            BorderThickness = new Thickness(selected ? 2.5 : 1.4),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10),
            Tag = group.Id
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = group.Id,
            Foreground = new SolidColorBrush(Color.FromRgb(118, 188, 255)),
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Height = 24
        });
        stack.Children.Add(new TextBlock
        {
            Text = group.Memo,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Height = NodeSituationHeight,
            Margin = new Thickness(0, 5, 0, 10)
        });

        foreach (var choice in choices)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, ChoiceRowGap), Height = ChoiceRowHeight };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

            var button = new Button
            {
                Content = $"{choice.Seq}. {TrimForHeader(choice.Memo)}",
                Tag = choice.Id,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 12,
                Padding = new Thickness(6, 0, 4, 0),
                Background = string.Equals(_selectedChoice?.Id, choice.Id, StringComparison.OrdinalIgnoreCase)
                    ? new SolidColorBrush(Color.FromRgb(76, 76, 76))
                    : new SolidColorBrush(Color.FromRgb(235, 235, 235)),
                Foreground = string.Equals(_selectedChoice?.Id, choice.Id, StringComparison.OrdinalIgnoreCase)
                    ? Brushes.White
                    : Brushes.Black
            };
            button.Click += ChoiceButton_Click;
            Grid.SetColumn(button, 0);
            row.Children.Add(button);

            var pins = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            pins.Children.Add(ConnectorPin("T", choice.Id, "success", "#3cb878"));
            if (IsFailPinEnabled(choice))
                pins.Children.Add(ConnectorPin("F", choice.Id, "fail", "#c65a5a"));
            Grid.SetColumn(pins, 1);
            row.Children.Add(pins);

            stack.Children.Add(row);
        }

        border.Child = stack;
        root.Children.Add(border);

        var inputPin = PinShape("#dfe7ff", "IN");
        inputPin.Tag = $"pin|{group.Id}|input";
        Canvas.SetLeft(inputPin, -PinSize / 2);
        Canvas.SetTop(inputPin, NodeTitleHeight + 10);
        var overlay = new Canvas();
        overlay.Children.Add(inputPin);
        var actionPin = GroupExitPin(group.Id);
        Canvas.SetLeft(actionPin, layout.Width - PinSize / 2);
        Canvas.SetTop(actionPin, NodeTitleHeight + 10);
        overlay.Children.Add(actionPin);
        if (isStartNode)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(38, 116, 198)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(116, 190, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(7, 2, 7, 2),
                Child = new TextBlock
                {
                    Text = "START",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 10
                },
                IsHitTestVisible = false
            };
            Canvas.SetLeft(badge, layout.Width - 62);
            Canvas.SetTop(badge, 8);
            overlay.Children.Add(badge);
        }
        root.Children.Add(overlay);

        Canvas.SetLeft(root, layout.X);
        Canvas.SetTop(root, layout.Y);
        GraphCanvas.Children.Add(root);
    }

    private FrameworkElement ConnectorPin(string text, string choiceId, string branch, string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        var panel = new Grid
        {
            Width = 34,
            Height = 18,
            Margin = new Thickness(0, 1, 0, 1),
            ToolTip = branch == "success"
                ? "T 출력: 드래그해서 다음 장면 연결"
                : "F 출력: success_rate가 있을 때 실패/거짓 경로 연결"
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        Grid.SetColumn(label, 0);
        panel.Children.Add(label);

        var pin = new Ellipse
        {
            Width = PinSize,
            Height = PinSize,
            Fill = brush,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            Tag = $"{choiceId}|{branch}",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand
        };
        pin.PreviewMouseLeftButtonDown += Connector_MouseLeftButtonDown;
        pin.PreviewMouseRightButtonDown += Connector_MouseRightButtonDown;
        Grid.SetColumn(pin, 1);
        panel.Children.Add(pin);

        return panel;
    }

    private FrameworkElement GroupExitPin(string groupId)
    {
        var pin = new Ellipse
        {
            Width = PinSize,
            Height = PinSize,
            Fill = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            Tag = groupId,
            ToolTip = "터미널 출력: Battle / Reward / Exit 노드에 연결",
            Cursor = Cursors.Hand
        };
        pin.PreviewMouseLeftButtonDown += GroupActionConnector_MouseLeftButtonDown;
        pin.PreviewMouseRightButtonDown += GroupActionConnector_MouseRightButtonDown;
        return pin;
    }

    private static bool IsFailPinEnabled(EventChoiceRow choice) => choice.SuccessRate is not null;

    private static double CalculateNodeHeight(int choiceCount)
        => Math.Max(NodeMinHeight, ChoiceStartY + Math.Max(1, choiceCount) * (ChoiceRowHeight + ChoiceRowGap) + 8);

    private static Ellipse PinShape(string color, string tooltip)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        return new Ellipse
        {
            Width = PinSize,
            Height = PinSize,
            Fill = brush,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            ToolTip = tooltip,
            IsHitTestVisible = false
        };
    }

    private Point GetInputPinPoint(string groupId)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(groupId, out var layout))
            return new Point();
        return new Point(layout.X, layout.Y + NodeTitleHeight + 10 + PinSize / 2);
    }

    private Point GetGroupActionPinPoint(string groupId)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(groupId, out var layout))
            return new Point();
        return new Point(layout.X + layout.Width, layout.Y + NodeTitleHeight + 10 + PinSize / 2);
    }

    private Point GetChoicePinPoint(EventChoiceRow choice, string branch, Dictionary<string, List<EventChoiceRow>> choicesByGroup)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(choice.GroupId, out var layout))
            return new Point();

        var groupChoices = choicesByGroup.TryGetValue(choice.GroupId, out var list) ? list : [];
        var index = Math.Max(0, groupChoices.FindIndex(c => string.Equals(c.Id, choice.Id, StringComparison.OrdinalIgnoreCase)));
        var rowCenter = layout.Y + ChoiceStartY + index * (ChoiceRowHeight + ChoiceRowGap) + ChoiceRowHeight / 2;
        var hasFail = IsFailPinEnabled(choice);
        var y = branch == "fail" && hasFail
            ? rowCenter + 9
            : branch == "success" && hasFail
                ? rowCenter - 9
                : rowCenter;
        var x = layout.X + layout.Width + PinSize / 2;
        return new Point(x, y);
    }

    private void DrawBranch(
        EventChoiceRow choice,
        string branch,
        string rewardType,
        int? rewardAmount,
        string nextGroupId,
        HashSet<string> groupSet,
        Dictionary<string, ChoiceGroupRow> groupsById,
        Dictionary<string, List<EventChoiceRow>> choicesByGroup)
    {
        if (_workbook is null || !_workbook.Layouts.ContainsKey(choice.GroupId))
            return;

        var from = GetChoicePinPoint(choice, branch, choicesByGroup);
        var hasReward = rewardType != "none";
        var hasNext = !string.IsNullOrWhiteSpace(nextGroupId) && groupSet.Contains(nextGroupId);
        var exitRect = default(Rect);
        var isChoiceExit = false;
        var hasExit = !hasNext && TryGetExitNodeRectForBranch(choice, branch, groupsById, choicesByGroup, out exitRect, out isChoiceExit);
        if (!hasReward && !hasNext && !hasExit)
            return;

        var color = branch == "success" ? "#79d38a" : "#d37a7a";
        var label = branch == "success" ? "T" : "F";
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;

        if (hasReward)
        {
            var rewardRect = GetRewardNodeRect(choice, branch, choicesByGroup);
            var rewardIn = new Point(rewardRect.Left, rewardRect.Top + rewardRect.Height / 2);
            var rewardOut = new Point(rewardRect.Right, rewardRect.Top + rewardRect.Height / 2);
            DrawCurve(from, rewardIn, brush, label, GetGroupRect(choice.GroupId), rewardRect);
            DrawRewardNode(choice, branch, rewardType, rewardAmount, rewardRect);
            if (hasNext)
                DrawCurve(rewardOut, GetInputPinPoint(nextGroupId), brush, "", rewardRect, GetGroupRect(nextGroupId));
            else if (hasExit)
            {
                var exitIn = new Point(exitRect.Left, exitRect.Top + exitRect.Height / 2);
                DrawCurve(rewardOut, exitIn, brush, "", rewardRect, exitRect);
                if (isChoiceExit)
                    DrawChoiceExitNode(choice, branch, exitRect);
            }
            return;
        }

        if (hasNext)
            DrawCurve(from, GetInputPinPoint(nextGroupId), brush, label, GetGroupRect(choice.GroupId), GetGroupRect(nextGroupId));
        else if (hasExit)
        {
            var exitIn = new Point(exitRect.Left, exitRect.Top + exitRect.Height / 2);
            DrawCurve(from, exitIn, brush, label, GetGroupRect(choice.GroupId), exitRect);
            if (isChoiceExit)
                DrawChoiceExitNode(choice, branch, exitRect);
        }
    }

    private bool TryGetExitNodeRectForBranch(
        EventChoiceRow choice,
        string branch,
        Dictionary<string, ChoiceGroupRow> groupsById,
        Dictionary<string, List<EventChoiceRow>> choicesByGroup,
        out Rect rect,
        out bool isChoiceExit)
    {
        rect = default;
        isChoiceExit = false;
        if (_workbook is null)
            return false;

        var choiceExitKey = ChoiceExitLayoutKey(choice.Id, branch);
        if (_workbook.Layouts.ContainsKey(choiceExitKey))
        {
            rect = GetChoiceExitNodeRect(choice, branch, choicesByGroup);
            isChoiceExit = true;
            return true;
        }

        return false;
    }

    private Rect GetRewardNodeRect(EventChoiceRow choice, string branch, Dictionary<string, List<EventChoiceRow>> choicesByGroup)
    {
        var from = GetChoicePinPoint(choice, branch, choicesByGroup);
        var eventId = "";
        if (_workbook is not null)
            eventId = _workbook.Groups.FirstOrDefault(g => g.Id == choice.GroupId)?.EventId ?? "";
        var key = RewardLayoutKey(choice.Id, branch);
        var yOffset = branch == "success" ? -RewardNodeHeight - 8 : 8;
        return RectFromLayout(EnsureObjectLayout(key, eventId, from.X + 72, from.Y + yOffset, RewardNodeWidth, RewardNodeHeight));
    }

    private Rect GetChoiceExitNodeRect(EventChoiceRow choice, string branch, Dictionary<string, List<EventChoiceRow>> choicesByGroup)
    {
        var from = GetChoicePinPoint(choice, branch, choicesByGroup);
        var eventId = "";
        if (_workbook is not null)
            eventId = _workbook.Groups.FirstOrDefault(g => g.Id == choice.GroupId)?.EventId ?? "";
        var key = ChoiceExitLayoutKey(choice.Id, branch);
        var yOffset = branch == "success" ? -ExitNodeHeight - 8 : 8;
        return RectFromLayout(EnsureObjectLayout(key, eventId, from.X + 72, from.Y + yOffset, ExitNodeWidth, ExitNodeHeight));
    }

    private Rect? GetGroupRect(string groupId)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(groupId, out var layout))
            return null;
        return new Rect(layout.X, layout.Y, Math.Max(layout.Width, NodeWidth), Math.Max(layout.Height, NodeMinHeight));
    }

    private void DrawCurve(Point from, Point to, Brush brush, string label, Rect? sourceRect = null, Rect? targetRect = null)
    {
        var x1 = from.X;
        var y1 = from.Y;
        var x2 = to.X;
        var y2 = to.Y;
        var points = BuildOrthogonalRoute(from, to, sourceRect, targetRect);
        var figure = RoundedPolyline(points, 14);

        var path = new System.Windows.Shapes.Path
        {
            Stroke = brush,
            StrokeThickness = 2.1,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = new PathGeometry([figure])
        };
        GraphCanvas.Children.Add(path);
        Canvas.SetZIndex(path, -1);

        if (!string.IsNullOrWhiteSpace(label))
        {
            var text = new TextBlock { Text = label, Foreground = brush, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(text, (x1 + x2) / 2);
            Canvas.SetTop(text, (y1 + y2) / 2);
            GraphCanvas.Children.Add(text);
        }
    }

    private static List<Point> BuildOrthogonalRoute(Point from, Point to, Rect? sourceRect, Rect? targetRect)
    {
        const double margin = 42;
        var source = sourceRect ?? new Rect(from.X - 1, from.Y - 1, 2, 2);
        var target = targetRect ?? new Rect(to.X - 1, to.Y - 1, 2, 2);
        var horizontalGap = to.X - from.X;
        if (horizontalGap >= 8)
        {
            var bend = Math.Clamp(horizontalGap / 2, 12, 64);
            var midX = from.X + bend;
            return CompactRoute(new[]
            {
                from,
                new Point(midX, from.Y),
                new Point(midX, to.Y),
                to
            });
        }

        var exitX = Math.Max(from.X + margin, source.Right + margin);
        var enterX = target.Left - margin;
        var points = new List<Point> { from, new(exitX, from.Y) };

        if (to.X > from.X + margin * 2 && enterX > exitX + margin)
        {
            var midX = exitX + (enterX - exitX) / 2;
            points.Add(new Point(midX, from.Y));
            points.Add(new Point(midX, to.Y));
            points.Add(new Point(enterX, to.Y));
            points.Add(to);
            return CompactRoute(points);
        }

        var upperY = Math.Min(source.Top, target.Top) - margin;
        var lowerY = Math.Max(source.Bottom, target.Bottom) + margin;
        var corridorY = Math.Abs(from.Y - upperY) <= Math.Abs(from.Y - lowerY) ? upperY : lowerY;
        if (Math.Abs(to.Y - lowerY) < Math.Abs(to.Y - upperY))
            corridorY = lowerY;

        var leftSafeX = Math.Min(source.Left, target.Left) - margin;
        var routeEnterX = Math.Min(enterX, leftSafeX);
        points.Add(new Point(exitX, corridorY));
        points.Add(new Point(routeEnterX, corridorY));
        points.Add(new Point(routeEnterX, to.Y));
        points.Add(to);
        return CompactRoute(points);
    }

    private static List<Point> CompactRoute(IEnumerable<Point> points)
    {
        var compact = new List<Point>();
        foreach (var point in points)
        {
            if (compact.Count == 0 || Math.Abs(compact[^1].X - point.X) > 0.1 || Math.Abs(compact[^1].Y - point.Y) > 0.1)
                compact.Add(point);
        }
        return compact;
    }

    private static PathFigure RoundedPolyline(IReadOnlyList<Point> points, double radius)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false };
        for (var i = 1; i < points.Count; i++)
        {
            var current = points[i];
            if (i == points.Count - 1)
            {
                figure.Segments.Add(new LineSegment(current, true));
                continue;
            }

            var previous = points[i - 1];
            var next = points[i + 1];
            var v1 = current - previous;
            var v2 = next - current;
            var len1 = Math.Max(0.001, v1.Length);
            var len2 = Math.Max(0.001, v2.Length);
            var r = Math.Min(radius, Math.Min(len1, len2) / 2);
            var before = new Point(current.X - v1.X / len1 * r, current.Y - v1.Y / len1 * r);
            var after = new Point(current.X + v2.X / len2 * r, current.Y + v2.Y / len2 * r);
            figure.Segments.Add(new LineSegment(before, true));
            figure.Segments.Add(new QuadraticBezierSegment(current, after, true));
        }
        return figure;
    }

    private void DrawRewardNode(EventChoiceRow choice, string branch, string rewardType, int? rewardAmount, Rect rect)
    {
        var key = RewardLayoutKey(choice.Id, branch);
        var selected = _selectedNodeKeys.Contains(key)
            || string.Equals(_selectedObjectKey, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_selectedChoice?.Id, choice.Id, StringComparison.OrdinalIgnoreCase);
        var root = new Grid
        {
            Width = rect.Width,
            Height = rect.Height,
            Tag = key
        };
        root.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;

        var border = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            Background = new SolidColorBrush(Color.FromRgb(48, 43, 30)),
            BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(222, 202, 116) : Color.FromRgb(171, 138, 65)),
            BorderThickness = new Thickness(selected ? 2.0 : 1.2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 8, 18, 8),
            Tag = key,
            ToolTip = "보상 노드: 선택지 Inspector의 reward 칼럼으로 저장됩니다."
        };
        border.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;
        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = $"{(branch == "success" ? "T" : "F")} Reward",
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 221, 141)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 12
                },
                new TextBlock
                {
                    Text = $"{rewardType} x{rewardAmount?.ToString() ?? "-"}",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        root.Children.Add(border);

        var overlay = new Canvas();
        var input = PinShape("#dfe7ff", "Reward IN");
        Canvas.SetLeft(input, -PinSize / 2);
        Canvas.SetTop(input, rect.Height / 2 - PinSize / 2);
        overlay.Children.Add(input);

        root.Children.Add(overlay);

        Canvas.SetLeft(root, rect.Left);
        Canvas.SetTop(root, rect.Top);
        GraphCanvas.Children.Add(root);
    }

    private void DrawGroupRewardNode(ChoiceGroupRow group)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(group.Id, out var layout))
            return;

        var key = GroupRewardLayoutKey(group.Id);
        var rect = RectFromLayout(EnsureObjectLayout(
            key,
            group.EventId,
            layout.X + layout.Width + 84,
            layout.Y + Math.Max(0, layout.Height - RewardNodeHeight) / 2,
            RewardNodeWidth,
            RewardNodeHeight));
        var from = GetGroupActionPinPoint(group.Id);
        var to = new Point(rect.Left, rect.Top + rect.Height / 2);
        var brush = new SolidColorBrush(Color.FromRgb(214, 181, 88));
        DrawCurve(from, to, brush, "REWARD", GetGroupRect(group.Id), rect);

        var selected = _selectedNodeKeys.Contains(key)
            || string.Equals(_selectedObjectKey, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_selectedGroup?.Id, group.Id, StringComparison.OrdinalIgnoreCase);
        var root = new Grid
        {
            Width = rect.Width,
            Height = rect.Height,
            Tag = key
        };
        root.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;

        var border = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            Background = new SolidColorBrush(Color.FromRgb(48, 43, 30)),
            BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(222, 202, 116) : Color.FromRgb(171, 138, 65)),
            BorderThickness = new Thickness(selected ? 2.0 : 1.2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 8, 18, 8),
            Tag = key,
            ToolTip = "Reward node: connected scene exports as next_action=exit."
        };
        border.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;
        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Reward",
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 221, 141)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 13
                },
                new TextBlock
                {
                    Text = "event reward / exit",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        root.Children.Add(border);
        var overlay = new Canvas();
        var input = PinShape("#dfe7ff", "Reward IN");
        Canvas.SetLeft(input, -PinSize / 2);
        Canvas.SetTop(input, rect.Height / 2 - PinSize / 2);
        overlay.Children.Add(input);
        root.Children.Add(overlay);

        Canvas.SetLeft(root, rect.Left);
        Canvas.SetTop(root, rect.Top);
        GraphCanvas.Children.Add(root);
    }

    private void DrawPendingObjectNodes()
    {
        if (_workbook is null || _selectedEvent is null)
            return;

        foreach (var pair in _workbook.Layouts.Where(p => string.Equals(p.Value.EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (pair.Key.StartsWith(PendingRewardPrefix, StringComparison.OrdinalIgnoreCase))
            {
                DrawPendingObjectNode(pair.Key, "Reward", "장면 출력 핀으로 연결", pair.Value, "reward");
            }
            else if (pair.Key.StartsWith(PendingBattlePrefix, StringComparison.OrdinalIgnoreCase))
            {
                DrawPendingObjectNode(pair.Key, "Battle", "장면 출력 핀으로 연결", pair.Value, "battle");
            }
            else if (pair.Key.StartsWith(PendingExitPrefix, StringComparison.OrdinalIgnoreCase))
            {
                DrawPendingObjectNode(pair.Key, "Exit", "Set scene next_action=exit", pair.Value, "exit");
            }
        }
    }

    private void DrawPendingObjectNode(string key, string title, string body, NodeLayout layout, string kind)
    {
        var selected = _selectedNodeKeys.Contains(key) || string.Equals(_selectedObjectKey, key, StringComparison.OrdinalIgnoreCase);
        var root = new Grid
        {
            Width = layout.Width,
            Height = layout.Height,
            Tag = key
        };
        root.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;

        var border = new Border
        {
            Width = layout.Width,
            Height = layout.Height,
            Background = new SolidColorBrush(kind == "battle"
                ? Color.FromRgb(52, 34, 31)
                : kind == "exit"
                    ? Color.FromRgb(38, 38, 38)
                    : Color.FromRgb(48, 43, 30)),
            BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(222, 202, 116) : Color.FromRgb(120, 120, 120)),
            BorderThickness = new Thickness(selected ? 2.0 : 1.2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 8, 18, 8),
            Tag = key,
            ToolTip = "생성된 객체 노드입니다. 핀 연결 시 유효성 검사를 수행합니다."
        };
        border.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;
        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    Foreground = new SolidColorBrush(kind == "battle"
                        ? Color.FromRgb(255, 172, 144)
                        : kind == "exit"
                            ? Color.FromRgb(220, 220, 220)
                            : Color.FromRgb(255, 221, 141)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 13
                },
                new TextBlock
                {
                    Text = body,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
        root.Children.Add(border);

        var overlay = new Canvas();
        var input = PinShape("#dfe7ff", $"{title} IN");
        Canvas.SetLeft(input, -PinSize / 2);
        Canvas.SetTop(input, layout.Height / 2 - PinSize / 2);
        overlay.Children.Add(input);
        root.Children.Add(overlay);

        Canvas.SetLeft(root, layout.X);
        Canvas.SetTop(root, layout.Y);
        GraphCanvas.Children.Add(root);
    }

    private static bool IsBattleGroup(ChoiceGroupRow group)
        => string.Equals(group.NextAction, "battle", StringComparison.OrdinalIgnoreCase);

    private static bool IsExitGroup(ChoiceGroupRow group)
        => string.Equals(group.NextAction, "exit", StringComparison.OrdinalIgnoreCase);

    private bool IsGroupRewardTerminal(ChoiceGroupRow group)
        => IsExitGroup(group) && _workbook?.Layouts.ContainsKey(GroupRewardLayoutKey(group.Id)) == true;

    private bool HasIncomingChoice(string groupId)
        => _workbook?.Choices.Any(c =>
            string.Equals(c.SuccessNextGroupId, groupId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.FailNextGroupId, groupId, StringComparison.OrdinalIgnoreCase)) == true;

    private static string RewardLayoutKey(string choiceId, string branch) => $"reward|{choiceId}|{branch}";
    private static string GroupRewardLayoutKey(string groupId) => $"{GroupRewardPrefix}{groupId}";
    private static string BattleLayoutKey(string groupId) => $"battle|{groupId}";
    private static string ExitLayoutKey(string groupId) => $"exit|{groupId}";
    private static string ChoiceExitLayoutKey(string choiceId, string branch) => $"{ChoiceExitPrefix}{choiceId}|{branch}";
    private static string PendingRewardLayoutKey(string eventId, string branch) => $"{PendingRewardPrefix}{eventId}|{branch}|{Guid.NewGuid():N}";
    private static string PendingBattleLayoutKey(string eventId) => $"{PendingBattlePrefix}{eventId}|{Guid.NewGuid():N}";
    private static string PendingExitLayoutKey(string eventId) => $"{PendingExitPrefix}{eventId}|{Guid.NewGuid():N}";
    private static string PendingRewardBranch(string key)
    {
        var parts = key.Split('|');
        return parts.Length >= 3 ? parts[2] : "success";
    }

    private NodeLayout EnsureObjectLayout(string key, string eventId, double x, double y, double width, double height)
    {
        if (_workbook is null)
            return new NodeLayout { EventId = eventId, GroupId = key, X = x, Y = y, Width = width, Height = height };

        if (!_workbook.Layouts.TryGetValue(key, out var layout))
        {
            layout = new NodeLayout
            {
                EventId = eventId,
                GroupId = key,
                X = RoundCanvasCoord(x),
                Y = RoundCanvasCoord(y),
                Width = RoundCoord(width),
                Height = RoundCoord(height)
            };
            _workbook.Layouts[key] = layout;
        }
        layout.X = RoundCanvasCoord(layout.X);
        layout.Y = RoundCanvasCoord(layout.Y);
        layout.Width = RoundCoord(width);
        layout.Height = RoundCoord(height);
        return layout;
    }

    private static Rect RectFromLayout(NodeLayout layout) => new(layout.X, layout.Y, layout.Width, layout.Height);

    private bool HasOverlappingGroupExit(Rect rect, string eventId)
    {
        if (_workbook is null)
            return false;
        return _workbook.Layouts.Any(pair =>
            pair.Key.StartsWith("exit|", StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.Value.EventId, eventId, StringComparison.OrdinalIgnoreCase)
            && RectsNearlyEqual(RectFromLayout(pair.Value), rect));
    }

    private bool HasSelectedChoiceExitAt(Rect rect, string eventId)
    {
        if (_workbook is null)
            return false;
        return _workbook.Layouts.Any(pair =>
            pair.Key.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(_selectedObjectKey, pair.Key, StringComparison.OrdinalIgnoreCase) || _selectedNodeKeys.Contains(pair.Key))
            && string.Equals(pair.Value.EventId, eventId, StringComparison.OrdinalIgnoreCase)
            && RectsNearlyEqual(RectFromLayout(pair.Value), rect));
    }

    private static bool RectsNearlyEqual(Rect a, Rect b)
        => Math.Abs(a.Left - b.Left) < 0.5
           && Math.Abs(a.Top - b.Top) < 0.5
           && Math.Abs(a.Width - b.Width) < 1
           && Math.Abs(a.Height - b.Height) < 1;

    private void DrawBattleNode(ChoiceGroupRow group, bool linkedFromChoice = false)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(group.Id, out var layout))
            return;

        var key = BattleLayoutKey(group.Id);
        var rect = RectFromLayout(EnsureObjectLayout(
            key,
            group.EventId,
            layout.X + layout.Width + 84,
            layout.Y + 18,
            BattleNodeWidth,
            BattleNodeHeight));
        var brush = new SolidColorBrush(Color.FromRgb(214, 116, 86));
        if (!linkedFromChoice)
        {
            var from = GetGroupActionPinPoint(group.Id);
            var to = new Point(rect.Left, rect.Top + rect.Height / 2);
            DrawCurve(from, to, brush, "BATTLE", GetGroupRect(group.Id), rect);
        }

        var selected = _selectedNodeKeys.Contains(key)
            || string.Equals(_selectedObjectKey, key, StringComparison.OrdinalIgnoreCase)
            || HasSelectedChoiceExitAt(rect, group.EventId)
            || string.Equals(_selectedGroup?.Id, group.Id, StringComparison.OrdinalIgnoreCase);
        var root = new Grid
        {
            Width = rect.Width,
            Height = rect.Height,
            Tag = key
        };
        root.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;
        var border = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            Background = new SolidColorBrush(Color.FromRgb(52, 34, 31)),
            BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(222, 202, 116) : Color.FromRgb(204, 93, 76)),
            BorderThickness = new Thickness(selected ? 2.0 : 1.2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 8, 18, 8),
            Tag = key,
            ToolTip = "전투 노드: next_action=battle, stage_id로 export됩니다."
        };
        border.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;
        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Battle",
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 172, 144)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 13
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(group.StageId) ? "stage_id: -" : group.StageId,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        root.Children.Add(border);
        var overlay = new Canvas();
        var input = PinShape("#dfe7ff", "Battle IN");
        Canvas.SetLeft(input, -PinSize / 2);
        Canvas.SetTop(input, rect.Height / 2 - PinSize / 2);
        overlay.Children.Add(input);
        root.Children.Add(overlay);

        Canvas.SetLeft(root, rect.Left);
        Canvas.SetTop(root, rect.Top);
        GraphCanvas.Children.Add(root);
    }

    private void DrawExitNode(ChoiceGroupRow group, Dictionary<string, List<EventChoiceRow>> choicesByGroup)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(group.Id, out var layout))
            return;

        var key = ExitLayoutKey(group.Id);
        var rect = RectFromLayout(EnsureObjectLayout(
            key,
            group.EventId,
            layout.X + layout.Width + 84,
            layout.Y + Math.Max(0, layout.Height - ExitNodeHeight) / 2,
            ExitNodeWidth,
            ExitNodeHeight));
        var from = GetGroupActionPinPoint(group.Id);
        var to = new Point(rect.Left, rect.Top + rect.Height / 2);
        var brush = new SolidColorBrush(Color.FromRgb(160, 160, 160));
        DrawCurve(from, to, brush, "EXIT", GetGroupRect(group.Id), rect);

        var selected = _selectedNodeKeys.Contains(key)
            || string.Equals(_selectedObjectKey, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_selectedGroup?.Id, group.Id, StringComparison.OrdinalIgnoreCase);
        var root = new Grid
        {
            Width = rect.Width,
            Height = rect.Height,
            Tag = key
        };
        root.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;
        var border = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(222, 202, 116) : Color.FromRgb(135, 135, 135)),
            BorderThickness = new Thickness(selected ? 2.0 : 1.2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 8, 18, 8),
            Tag = key,
            ToolTip = "Exit node: next_action=exit"
        };
        border.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;
        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Exit",
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 13
                },
                new TextBlock
                {
                    Text = "event end",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        root.Children.Add(border);
        var overlay = new Canvas();
        var input = PinShape("#dfe7ff", "Exit IN");
        Canvas.SetLeft(input, -PinSize / 2);
        Canvas.SetTop(input, rect.Height / 2 - PinSize / 2);
        overlay.Children.Add(input);
        root.Children.Add(overlay);

        Canvas.SetLeft(root, rect.Left);
        Canvas.SetTop(root, rect.Top);
        GraphCanvas.Children.Add(root);
    }

    private void DrawChoiceExitNode(EventChoiceRow choice, string branch, Rect rect)
    {
        var key = ChoiceExitLayoutKey(choice.Id, branch);
        var eventId = _workbook?.Groups.FirstOrDefault(g => string.Equals(g.Id, choice.GroupId, StringComparison.OrdinalIgnoreCase))?.EventId ?? "";
        if (HasOverlappingGroupExit(rect, eventId))
            return;
        var selected = _selectedNodeKeys.Contains(key)
            || string.Equals(_selectedObjectKey, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_selectedChoice?.Id, choice.Id, StringComparison.OrdinalIgnoreCase);
        var root = new Grid
        {
            Width = rect.Width,
            Height = rect.Height,
            Tag = key
        };
        root.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;
        var border = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(222, 202, 116) : Color.FromRgb(135, 135, 135)),
            BorderThickness = new Thickness(selected ? 2.0 : 1.2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 8, 18, 8),
            Tag = key,
            ToolTip = "Choice exit node: this T/F result ends the event."
        };
        border.MouseLeftButtonDown += ObjectNode_MouseLeftButtonDown;
        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = $"{(branch == "success" ? "T" : "F")} Exit",
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 13
                },
                new TextBlock
                {
                    Text = "event end",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
        root.Children.Add(border);

        var overlay = new Canvas();
        var input = PinShape("#dfe7ff", "Exit IN");
        Canvas.SetLeft(input, -PinSize / 2);
        Canvas.SetTop(input, rect.Height / 2 - PinSize / 2);
        overlay.Children.Add(input);
        root.Children.Add(overlay);

        Canvas.SetLeft(root, rect.Left);
        Canvas.SetTop(root, rect.Top);
        GraphCanvas.Children.Add(root);
    }

    private void BuildEventInspector()
    {
        _selectedGroup = null;
        _selectedChoice = null;
        InspectorPanel.Children.Clear();
        ClearInspectorPreview();
        InspectorPanel.Children.Add(SectionTitle("이벤트"));
        if (_selectedEvent is null)
        {
            InspectorPanel.Children.Add(new TextBlock { Text = "이벤트를 선택하세요.", Foreground = Brushes.LightGray });
            return;
        }

        AddText("ID", _selectedEvent.Id, v => _selectedEvent.Id = v, readOnly: true);
        AddText("메모/이벤트명", _selectedEvent.Memo, v => { _selectedEvent.Memo = v; RefreshEventList(); });
        AddText("event_name TID", _selectedEvent.EventNameTid, v => _selectedEvent.EventNameTid = v);
        AddText("희귀도", _selectedEvent.Rarity, v => _selectedEvent.Rarity = v);
        AddText("가중치", _selectedEvent.Weight.ToString(), v => _selectedEvent.Weight = ParseUtil.NullableInt(v) ?? _selectedEvent.Weight);
        AddText("first_group_id", _selectedEvent.FirstGroupId, v => _selectedEvent.FirstGroupId = v);

        var normalizeTid = new Button { Content = "TID 자동 보정", Margin = new Thickness(0, 12, 0, 0), Height = 32 };
        normalizeTid.Click += (_, _) =>
        {
            EnsureGeneratedTids(force: true);
            DrawGraph();
            RefreshIssues();
        };
        InspectorPanel.Children.Add(normalizeTid);

        var delete = new Button
        {
            Content = "이벤트 삭제",
            Margin = new Thickness(0, 8, 0, 0),
            Height = 32,
            Background = new SolidColorBrush(Color.FromRgb(112, 45, 52)),
            Foreground = Brushes.White
        };
        delete.Click += (_, _) => DeleteEvent(_selectedEvent);
        InspectorPanel.Children.Add(delete);
    }

    private void BuildMultiSelectionInspector()
    {
        _selectedGroup = null;
        _selectedChoice = null;
        _selectedObjectKey = null;
        InspectorPanel.Children.Clear();
        ClearInspectorPreview();
        InspectorPanel.Children.Add(SectionTitle($"{_selectedNodeKeys.Count}개 노드 선택"));
        InspectorPanel.Children.Add(new TextBlock
        {
            Text = "선택한 노드는 드래그로 함께 이동할 수 있고 Del 키로 삭제할 수 있습니다.",
            Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 190)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12)
        });
    }

    private void BuildGroupInspector(ChoiceGroupRow group)
    {
        _selectedGroup = group;
        _selectedChoice = null;
        InspectorPanel.Children.Clear();
        ClearInspectorPreview();
        InspectorPanel.Children.Add(SectionTitle("장면 노드"));
        AddText("ID", group.Id, v => group.Id = v, readOnly: true);
        AddText("메모/상황문", group.Memo, v => { group.Memo = v; DrawGraph(); });
        AddText("situation_text TID", group.SituationTextTid, v => group.SituationTextTid = v);
        Action? refreshBackgroundPreview = null;
        AddBackgroundText(group, () => refreshBackgroundPreview?.Invoke());
        AddText("next_action", group.NextAction, v => { group.NextAction = v; DrawGraph(); });
        AddText("npc_id", group.NpcId, v => group.NpcId = v);
        AddText("stage_id", group.StageId, v => { group.StageId = v; DrawGraph(); });

        var battle = new Button
        {
            Content = IsBattleGroup(group) ? "전투 노드 제거" : "전투 노드 추가",
            Margin = new Thickness(0, 12, 0, 0),
            Height = 32
        };
        battle.Click += (_, _) =>
        {
            PushUndo();
            if (IsBattleGroup(group))
            {
                group.NextAction = "choice";
                group.StageId = "";
            }
            else
            {
                group.NextAction = "battle";
                if (string.IsNullOrWhiteSpace(group.StageId))
                    group.StageId = "STG_EVT_";
            }
            DrawGraph();
            RefreshIssues();
        };
        InspectorPanel.Children.Add(battle);

        var exit = new Button
        {
            Content = IsExitGroup(group) ? "Exit node remove" : "Exit node add",
            Margin = new Thickness(0, 8, 0, 0),
            Height = 32
        };
        exit.Click += (_, _) =>
        {
            PushUndo();
            if (IsExitGroup(group))
            {
                group.NextAction = "choice";
            }
            else
            {
                group.NextAction = "exit";
                group.StageId = "";
            }
            DrawGraph();
            RefreshIssues();
        };
        InspectorPanel.Children.Add(exit);
        InspectorPanel.Children.Remove(battle);
        InspectorPanel.Children.Remove(exit);

        var addChoice = new Button { Content = "선택지 추가", Margin = new Thickness(0, 8, 0, 0), Height = 32 };
        addChoice.Click += (_, _) => AddChoice(group);
        InspectorPanel.Children.Add(addChoice);

        var delete = new Button
        {
            Content = "장면 삭제",
            Margin = new Thickness(0, 8, 0, 0),
            Height = 32,
            Background = new SolidColorBrush(Color.FromRgb(112, 45, 52)),
            Foreground = Brushes.White
        };
        delete.Click += (_, _) => DeleteGroup(group);
        InspectorPanel.Children.Add(delete);
        refreshBackgroundPreview = AddBackgroundPreview(group);
    }

    private void BuildChoiceInspector(EventChoiceRow choice)
    {
        _selectedChoice = choice;
        _selectedGroup = _workbook?.Groups.FirstOrDefault(g => g.Id == choice.GroupId);
        InspectorPanel.Children.Clear();
        ClearInspectorPreview();
        InspectorPanel.Children.Add(SectionTitle("선택지"));
        AddText("ID", choice.Id, v => choice.Id = v, readOnly: true);
        AddText("메모/선택지 문구", choice.Memo, v => { choice.Memo = v; DrawGraph(); });
        AddText("choice_text TID", choice.ChoiceTextTid, v => choice.ChoiceTextTid = v);
        AddText("seq", choice.Seq.ToString(), v => choice.Seq = ParseUtil.NullableInt(v) ?? choice.Seq);
        AddText("비용 타입 cost_type", choice.CostType, v => choice.CostType = string.IsNullOrWhiteSpace(v) ? "none" : v);
        AddText("비용 수량 cost_amount", choice.CostAmount?.ToString() ?? "", v => choice.CostAmount = ParseUtil.NullableInt(v));
        AddText("성공 확률 success_rate", choice.SuccessRate?.ToString() ?? "", v =>
        {
            choice.SuccessRate = ParseUtil.NullableInt(v);
            DrawGraph();
        });
        InspectorPanel.Children.Add(new TextBlock
        {
            Text = "success_rate가 비어 있으면 F 핀은 비활성입니다.",
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        AddText("성공 보상 타입 success_reward_type", choice.SuccessRewardType, v =>
        {
            choice.SuccessRewardType = string.IsNullOrWhiteSpace(v) ? "none" : v;
            DrawGraph();
        });
        AddText("성공 보상 수량 success_reward_amount", choice.SuccessRewardAmount?.ToString() ?? "", v => choice.SuccessRewardAmount = ParseUtil.NullableInt(v));
        AddText("성공 다음 장면 success_next_group_id", choice.SuccessNextGroupId, v => choice.SuccessNextGroupId = v);
        AddText("실패 보상 타입 fail_reward_type", choice.FailRewardType, v =>
        {
            choice.FailRewardType = string.IsNullOrWhiteSpace(v) ? "none" : v;
            DrawGraph();
        });
        AddText("실패 보상 수량 fail_reward_amount", choice.FailRewardAmount?.ToString() ?? "", v => choice.FailRewardAmount = ParseUtil.NullableInt(v));
        AddText("실패 다음 장면 fail_next_group_id", choice.FailNextGroupId, v => choice.FailNextGroupId = v);

        var delete = new Button
        {
            Content = "선택지 삭제",
            Margin = new Thickness(0, 12, 0, 0),
            Height = 32,
            Background = new SolidColorBrush(Color.FromRgb(112, 45, 52)),
            Foreground = Brushes.White
        };
        delete.Click += (_, _) => DeleteChoice(choice);
        InspectorPanel.Children.Add(delete);
    }

    private void ClearInspectorPreview()
    {
        InspectorPreviewHost.Child = null;
        InspectorPreviewHost.Visibility = Visibility.Collapsed;
    }

    private TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 12)
    };

    private void AddText(string label, string value, Action<string> setter, bool readOnly = false)
    {
        AddTextEditor(label, value, setter, readOnly);
    }

    private void AddBackgroundText(ChoiceGroupRow group, Action? afterChanged = null)
    {
        TextEditHandle? handle = null;
        var picker = new Button
        {
            Content = "◎",
            Width = 30,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "background 이미지 선택",
            FontSize = 14,
            FontWeight = FontWeights.Bold
        };
        picker.Click += (_, _) =>
        {
            if (handle is null)
                return;

            var startRoot = !string.IsNullOrWhiteSpace(_settings.BackgroundImageRoot)
                ? _settings.BackgroundImageRoot
                : DefaultBackgroundImageRoot;
            var dialog = new BackgroundImagePickerWindow(DefaultBackgroundImageRoot, startRoot, handle.Box.Text)
            {
                Owner = this
            };
            var accepted = dialog.ShowDialog() == true;
            _settings.BackgroundImageRoot = dialog.CurrentRoot;
            if (!string.IsNullOrWhiteSpace(dialog.SelectedImagePath))
                _settings.LastBackgroundImagePath = dialog.SelectedImagePath;
            NexusPathResolver.SaveSettings(_settings);

            if (accepted && dialog.SelectedResourceKey is not null)
                handle.Apply(dialog.SelectedResourceKey, true);
        };

        handle = AddTextEditor("background", group.Background, v =>
        {
            group.Background = v;
            afterChanged?.Invoke();
        }, trailingElement: picker);
    }

    private Action AddBackgroundPreview(ChoiceGroupRow group)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var titleRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        var title = new TextBlock
        {
            Text = "Background Preview",
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(title, Dock.Left);
        titleRow.Children.Add(title);

        var keyText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleRow.Children.Add(keyText);
        panel.Children.Add(titleRow);

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var placeholder = new TextBlock
        {
            Text = "Preview not found",
            Foreground = new SolidColorBrush(Color.FromRgb(155, 155, 155)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14)
        };
        var previewGrid = new Grid();
        previewGrid.Children.Add(image);
        previewGrid.Children.Add(placeholder);
        panel.Children.Add(new Border
        {
            Height = 170,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(68, 68, 68)),
            BorderThickness = new Thickness(1),
            Child = previewGrid
        });

        var pathText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(145, 145, 145)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        panel.Children.Add(pathText);
        InspectorPreviewHost.Child = panel;
        InspectorPreviewHost.Visibility = Visibility.Visible;

        void Refresh()
        {
            var key = NormalizeBackgroundKey(group.Background);
            var resolved = ResolveBackgroundImagePath(group.Background);
            keyText.Text = key;
            pathText.Text = resolved ?? $"Image file not found: {key}";

            var source = resolved is not null ? LoadBackgroundPreviewBitmap(resolved, 420) : null;
            image.Source = source;
            image.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
            placeholder.Text = resolved is null
                ? $"Preview not found\n{key}"
                : $"Preview load failed\n{key}";
            placeholder.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        }

        Refresh();
        return Refresh;
    }

    private string? ResolveBackgroundImagePath(string background)
    {
        var key = NormalizeBackgroundKey(background);
        if (IoPath.IsPathRooted(key) && File.Exists(key))
            return key;

        var relative = key.Replace('/', IoPath.DirectorySeparatorChar).Replace('\\', IoPath.DirectorySeparatorChar);
        var roots = new[] { DefaultBackgroundImageRoot, _settings.BackgroundImageRoot }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var candidateBase = IoPath.Combine(root, relative);
            if (File.Exists(candidateBase))
                return candidateBase;

            if (IoPath.HasExtension(candidateBase))
                continue;

            foreach (var extension in BackgroundImageExtensions)
            {
                var candidate = candidateBase + extension;
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string NormalizeBackgroundKey(string background)
    {
        var key = background.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(key) ? "dimension_spiral" : key;
    }

    private static ImageSource? LoadBackgroundPreviewBitmap(string path, int decodePixelWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private TextEditHandle AddTextEditor(string label, string value, Action<string> setter, bool readOnly = false, UIElement? trailingElement = null)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 3)
        };
        header.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
            VerticalAlignment = VerticalAlignment.Center
        });
        var help = FindInspectorHelp(label);
        if (help is not null)
        {
            var info = new Button
            {
                Content = "i",
                Width = 18,
                Height = 18,
                MinHeight = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 11,
                ToolTip = help.Tooltip,
                Background = new SolidColorBrush(Color.FromRgb(54, 54, 54)),
                Foreground = new SolidColorBrush(Color.FromRgb(210, 220, 235))
            };
            info.Click += (_, _) => MessageBox.Show(this, help.Tooltip, $"{help.Table}.{help.Column}", MessageBoxButton.OK, MessageBoxImage.Information);
            header.Children.Add(info);
        }
        InspectorPanel.Children.Add(header);
        var current = value;
        TextBox? box = null;
        void ApplyBoxValue(string raw, bool pushUndo)
        {
            if (readOnly)
                return;
            var next = raw.Trim();
            if (string.Equals(current, next, StringComparison.Ordinal))
                return;
            if (pushUndo)
                PushUndo();
            setter(next);
            current = next;
            if (box is not null && !string.Equals(box.Text, next, StringComparison.Ordinal))
                box.Text = next;
            RefreshIssues();
            DrawGraph();
        }
        box = new TextBox { Text = value, IsReadOnly = readOnly, MinHeight = 28, TextWrapping = TextWrapping.Wrap };
        box.LostFocus += (_, _) => ApplyBoxValue(box.Text, pushUndo: true);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ApplyBoxValue(box.Text, pushUndo: true);
            }
        };
        if (trailingElement is null)
        {
            InspectorPanel.Children.Add(box);
        }
        else
        {
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(trailingElement, Dock.Right);
            row.Children.Add(trailingElement);
            row.Children.Add(box);
            InspectorPanel.Children.Add(row);
        }
        return new TextEditHandle(box, ApplyBoxValue);
    }

    private sealed class TextEditHandle
    {
        public TextEditHandle(TextBox box, Action<string, bool> apply)
        {
            Box = box;
            Apply = apply;
        }

        public TextBox Box { get; }
        public Action<string, bool> Apply { get; }
    }

    private ColumnHelp? FindInspectorHelp(string label)
    {
        if (_workbook is null)
            return null;
        var table = _selectedChoice is not null
            ? EventWorkbookService.ChoiceSheetName
            : _selectedGroup is not null
                ? EventWorkbookService.GroupSheetName
                : EventWorkbookService.BaseSheetName;
        var column = InferColumnName(label);
        if (string.IsNullOrWhiteSpace(column))
            return null;
        return _workbook.ColumnHelps.TryGetValue($"{table}.{column}", out var help) ? help : null;
    }

    private static string InferColumnName(string label)
    {
        var lower = label.ToLowerInvariant();
        var known = new[]
        {
            "event_name", "first_group_id", "floor_restriction", "diff_restriction",
            "situation_text", "next_action", "background", "npc_id", "stage_id",
            "choice_text", "cost_type", "cost_amount", "success_rate",
            "success_reward_type", "success_reward_amount", "success_next_group_id",
            "fail_reward_type", "fail_reward_amount", "fail_next_group_id",
            "rarity", "weight", "seq"
        };
        foreach (var column in known)
        {
            if (lower.Contains(column, StringComparison.OrdinalIgnoreCase))
                return column;
        }
        return lower.Trim() == "id" ? "id" : "";
    }

    private void AddChoice(ChoiceGroupRow group)
    {
        if (_workbook is null)
            return;
        PushUndo();
        var nextSeq = _workbook.Choices.Where(c => c.GroupId == group.Id).Select(c => c.Seq).DefaultIfEmpty(0).Max() + 1;
        var id = $"{group.Id}_C{nextSeq}";
        var choice = new EventChoiceRow
        {
            Id = id,
            Memo = "새 선택지",
            ExportId = EventWorkbookService.DefaultEventExportId,
            GroupId = group.Id,
            Seq = nextSeq,
            ChoiceTextTid = $"{id}_choice_text"
        };
        _workbook.Choices.Add(choice);
        RefreshHierarchy();
        DrawGraph();
        BuildChoiceInspector(choice);
    }

    private void AddGroup() => AddGroupAt(null);

    private void AddGroupAt(Point? canvasPoint)
    {
        if (_workbook is null || _selectedEvent is null)
            return;
        PushUndo();
        var id = NextGroupId(_selectedEvent.Id);
        var group = new ChoiceGroupRow
        {
            Id = id,
            EventId = _selectedEvent.Id,
            Memo = "새 장면",
            SituationTextTid = $"{id}_situation_text"
        };
        _workbook.Groups.Add(group);
        var point = canvasPoint ?? new Point(100, 100);
        _workbook.Layouts[group.Id] = new NodeLayout
        {
            EventId = _selectedEvent.Id,
            GroupId = group.Id,
            X = RoundCanvasCoord(point.X),
            Y = RoundCanvasCoord(point.Y),
            Width = NodeWidth,
            Height = NodeMinHeight
        };
        RefreshHierarchy();
        DrawGraph();
        BuildGroupInspector(group);
    }

    private void EnsureGeneratedTids(bool force)
    {
        if (_workbook is null)
            return;

        foreach (var evt in _workbook.Events)
        {
            var generated = $"{evt.Id}_name";
            if (force || string.IsNullOrWhiteSpace(evt.EventNameTid))
                evt.EventNameTid = generated;
        }

        foreach (var group in _workbook.Groups)
        {
            var generated = $"{group.Id}_situation_text";
            if (force || string.IsNullOrWhiteSpace(group.SituationTextTid))
                group.SituationTextTid = generated;
        }

        foreach (var choice in _workbook.Choices)
        {
            var generated = $"{choice.Id}_choice_text";
            if (force || string.IsNullOrWhiteSpace(choice.ChoiceTextTid))
                choice.ChoiceTextTid = generated;
        }
    }

    private void DeleteEvent(EventBaseRow evt)
    {
        if (_workbook is null)
            return;

        if (_workbook.Events.Count <= 1)
        {
            MessageBox.Show(this, "최소 1개의 이벤트가 필요합니다.", "Delete blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(this,
            $"이벤트와 포함된 장면/선택지를 모두 삭제합니다.\n\n{evt.Id}  {evt.Memo}",
            "이벤트 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
            return;

        PushUndo();
        var groupIds = _workbook.Groups
            .Where(g => g.EventId == evt.Id)
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choiceIds = _workbook.Choices
            .Where(c => groupIds.Contains(c.GroupId))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _workbook.Events.Remove(evt);
        _workbook.Groups.RemoveAll(g => groupIds.Contains(g.Id));
        _workbook.Choices.RemoveAll(c => groupIds.Contains(c.GroupId));
        foreach (var groupId in groupIds)
        {
            _workbook.Layouts.Remove(groupId);
            _workbook.Layouts.Remove(BattleLayoutKey(groupId));
            _workbook.Layouts.Remove(ExitLayoutKey(groupId));
            _workbook.Layouts.Remove(GroupRewardLayoutKey(groupId));
        }
        foreach (var key in _workbook.Layouts.Keys.Where(k =>
                     k.StartsWith("reward|", StringComparison.OrdinalIgnoreCase)
                     && k.Split('|').Length >= 3
                     && choiceIds.Contains(k.Split('|')[1])).ToList())
            _workbook.Layouts.Remove(key);
        foreach (var key in _workbook.Layouts.Keys.Where(k =>
                     k.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase)
                     && choiceIds.Contains(k.Split('|').ElementAtOrDefault(1) ?? "")).ToList())
            _workbook.Layouts.Remove(key);

        _selectedEvent = _workbook.Events.OrderBy(e => e.Id).FirstOrDefault();
        _selectedGroup = null;
        _selectedChoice = null;
        RefreshEventList();
        RefreshHierarchy();
        RefreshIssues();
        EventList.SelectedItem = _selectedEvent;
        DrawGraph();
    }

    private void DeleteGroup(ChoiceGroupRow group)
    {
        if (_workbook is null || _selectedEvent is null)
            return;

        var eventGroups = _workbook.Groups.Where(g => g.EventId == _selectedEvent.Id && g.Id != group.Id).ToList();
        if (eventGroups.Count == 0)
        {
            MessageBox.Show(this, "이벤트에는 최소 1개의 장면이 필요합니다.", "Delete blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(this,
            $"장면과 이 장면의 선택지를 삭제합니다.\n다른 선택지에서 이 장면으로 연결된 값은 비워집니다.\n\n{group.Id}",
            "장면 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
            return;

        PushUndo();
        var removedChoiceIds = _workbook.Choices
            .Where(c => c.GroupId == group.Id)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _workbook.Groups.Remove(group);
        _workbook.Choices.RemoveAll(c => c.GroupId == group.Id);
        _workbook.Layouts.Remove(group.Id);
        _workbook.Layouts.Remove(BattleLayoutKey(group.Id));
        _workbook.Layouts.Remove(ExitLayoutKey(group.Id));
        _workbook.Layouts.Remove(GroupRewardLayoutKey(group.Id));
        foreach (var key in _workbook.Layouts.Keys.Where(k =>
                     k.StartsWith("reward|", StringComparison.OrdinalIgnoreCase)
                     && k.Split('|').Length >= 3
                     && removedChoiceIds.Contains(k.Split('|')[1])).ToList())
            _workbook.Layouts.Remove(key);
        foreach (var key in _workbook.Layouts.Keys.Where(k =>
                     k.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase)
                     && removedChoiceIds.Contains(k.Split('|').ElementAtOrDefault(1) ?? "")).ToList())
            _workbook.Layouts.Remove(key);
        foreach (var choice in _workbook.Choices)
        {
            if (choice.SuccessNextGroupId == group.Id)
                choice.SuccessNextGroupId = "";
            if (choice.FailNextGroupId == group.Id)
                choice.FailNextGroupId = "";
        }
        if (_selectedEvent.FirstGroupId == group.Id)
            _selectedEvent.FirstGroupId = eventGroups[0].Id;
        _selectedGroup = null;
        RefreshHierarchy();
        DrawGraph();
        RefreshIssues();
    }

    private void DeleteChoice(EventChoiceRow choice)
    {
        if (_workbook is null)
            return;
        var result = MessageBox.Show(this, $"선택지를 삭제합니다.\n\n{choice.Id}", "선택지 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
            return;
        PushUndo();
        _workbook.Choices.Remove(choice);
        _workbook.Layouts.Remove(RewardLayoutKey(choice.Id, "success"));
        _workbook.Layouts.Remove(RewardLayoutKey(choice.Id, "fail"));
        _workbook.Layouts.Remove(ChoiceExitLayoutKey(choice.Id, "success"));
        _workbook.Layouts.Remove(ChoiceExitLayoutKey(choice.Id, "fail"));
        _selectedChoice = null;
        RefreshHierarchy();
        DrawGraph();
        RefreshIssues();
    }

    private void DeleteSelected()
    {
        if (_workbook is null)
            return;

        if (_selectedNodeKeys.Count > 0)
        {
            DeleteSelectedNodes();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_selectedObjectKey))
        {
            PushUndo();
            if (_selectedObjectKey.StartsWith(PendingRewardPrefix, StringComparison.OrdinalIgnoreCase)
                || _selectedObjectKey.StartsWith(PendingBattlePrefix, StringComparison.OrdinalIgnoreCase)
                || _selectedObjectKey.StartsWith(PendingExitPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _workbook.Layouts.Remove(_selectedObjectKey);
                _selectedObjectKey = null;
            }
            else if (_selectedObjectKey.StartsWith("reward|", StringComparison.OrdinalIgnoreCase))
            {
                var parts = _selectedObjectKey.Split('|');
                if (parts.Length >= 3)
                {
                    var choice = _workbook.Choices.FirstOrDefault(c => c.Id == parts[1]);
                    if (choice is not null && parts[2] == "success")
                    {
                        choice.SuccessRewardType = "none";
                        choice.SuccessRewardAmount = null;
                    }
                    else if (choice is not null && parts[2] == "fail")
                    {
                        choice.FailRewardType = "none";
                        choice.FailRewardAmount = null;
                    }
                }
                _workbook.Layouts.Remove(_selectedObjectKey);
                _selectedObjectKey = null;
            }
            else if (_selectedObjectKey.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _workbook.Layouts.Remove(_selectedObjectKey);
                _selectedObjectKey = null;
            }
            else if (_selectedObjectKey.StartsWith("battle|", StringComparison.OrdinalIgnoreCase))
            {
                var groupId = _selectedObjectKey.Split('|').ElementAtOrDefault(1) ?? "";
                var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
                if (group is not null)
                {
                    group.NextAction = "choice";
                    group.StageId = "";
                }
                _workbook.Layouts.Remove(_selectedObjectKey);
                _selectedObjectKey = null;
            }
            else if (_selectedObjectKey.StartsWith(GroupRewardPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var groupId = _selectedObjectKey.Split('|').ElementAtOrDefault(1) ?? "";
                var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
                if (group is not null)
                {
                    group.NextAction = "choice";
                    group.StageId = "";
                }
                _workbook.Layouts.Remove(_selectedObjectKey);
                _selectedObjectKey = null;
            }
            else if (_selectedObjectKey.StartsWith("exit|", StringComparison.OrdinalIgnoreCase))
            {
                var groupId = _selectedObjectKey.Split('|').ElementAtOrDefault(1) ?? "";
                var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
                if (group is not null)
                    group.NextAction = "choice";
                _workbook.Layouts.Remove(_selectedObjectKey);
                _selectedObjectKey = null;
            }
            DrawGraph();
            RefreshIssues();
            return;
        }

        if (_selectedChoice is not null)
        {
            DeleteChoice(_selectedChoice);
            return;
        }

        if (_selectedGroup is not null)
            DeleteGroup(_selectedGroup);
    }

    private void DeleteSelectedNodes()
    {
        if (_workbook is null || _selectedEvent is null || _selectedNodeKeys.Count == 0)
            return;

        var selected = _selectedNodeKeys.ToList();
        var groupIds = selected
            .Where(k => !k.Contains('|') && _workbook.Groups.Any(g => g.Id == k && g.EventId == _selectedEvent.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentGroupIds = _workbook.Groups
            .Where(g => g.EventId == _selectedEvent.Id)
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groupIds.Count >= currentGroupIds.Count)
        {
            MessageBox.Show(this, "이벤트에는 최소 1개의 장면 노드가 필요합니다.", "Delete blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(this, $"선택한 노드 {selected.Count}개를 삭제합니다.", "노드 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
            return;

        PushUndo();

        foreach (var key in selected.Where(k => k.StartsWith("reward|", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = key.Split('|');
            if (parts.Length >= 3)
            {
                var choice = _workbook.Choices.FirstOrDefault(c => c.Id == parts[1]);
                if (choice is not null && parts[2] == "success")
                {
                    choice.SuccessRewardType = "none";
                    choice.SuccessRewardAmount = null;
                }
                else if (choice is not null && parts[2] == "fail")
                {
                    choice.FailRewardType = "none";
                    choice.FailRewardAmount = null;
                }
            }
            _workbook.Layouts.Remove(key);
        }

        foreach (var key in selected.Where(k =>
                     k.StartsWith(PendingRewardPrefix, StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith(PendingBattlePrefix, StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith(PendingExitPrefix, StringComparison.OrdinalIgnoreCase)))
            _workbook.Layouts.Remove(key);

        foreach (var key in selected.Where(k => k.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase)))
            _workbook.Layouts.Remove(key);

        foreach (var key in selected.Where(k => k.StartsWith("battle|", StringComparison.OrdinalIgnoreCase)))
        {
            var groupId = key.Split('|').ElementAtOrDefault(1) ?? "";
            var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group is not null)
            {
                group.NextAction = "choice";
                group.StageId = "";
            }
            _workbook.Layouts.Remove(key);
        }

        foreach (var key in selected.Where(k => k.StartsWith(GroupRewardPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var groupId = key.Split('|').ElementAtOrDefault(1) ?? "";
            var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group is not null)
            {
                group.NextAction = "choice";
                group.StageId = "";
            }
            _workbook.Layouts.Remove(key);
        }

        foreach (var key in selected.Where(k => k.StartsWith("exit|", StringComparison.OrdinalIgnoreCase)))
        {
            var groupId = key.Split('|').ElementAtOrDefault(1) ?? "";
            var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group is not null)
                group.NextAction = "choice";
            _workbook.Layouts.Remove(key);
        }

        var removedChoiceIds = _workbook.Choices
            .Where(c => groupIds.Contains(c.GroupId))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _workbook.Groups.RemoveAll(g => groupIds.Contains(g.Id));
        _workbook.Choices.RemoveAll(c => groupIds.Contains(c.GroupId));
        foreach (var groupId in groupIds)
        {
            _workbook.Layouts.Remove(groupId);
            _workbook.Layouts.Remove(BattleLayoutKey(groupId));
            _workbook.Layouts.Remove(ExitLayoutKey(groupId));
            _workbook.Layouts.Remove(GroupRewardLayoutKey(groupId));
        }
        foreach (var key in _workbook.Layouts.Keys.Where(k =>
                     k.StartsWith("reward|", StringComparison.OrdinalIgnoreCase)
                     && removedChoiceIds.Contains(k.Split('|').ElementAtOrDefault(1) ?? "")).ToList())
            _workbook.Layouts.Remove(key);
        foreach (var key in _workbook.Layouts.Keys.Where(k =>
                     k.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase)
                     && removedChoiceIds.Contains(k.Split('|').ElementAtOrDefault(1) ?? "")).ToList())
            _workbook.Layouts.Remove(key);
        foreach (var choice in _workbook.Choices)
        {
            if (groupIds.Contains(choice.SuccessNextGroupId))
                choice.SuccessNextGroupId = "";
            if (groupIds.Contains(choice.FailNextGroupId))
                choice.FailNextGroupId = "";
        }
        if (groupIds.Contains(_selectedEvent.FirstGroupId))
            _selectedEvent.FirstGroupId = _workbook.Groups.First(g => g.EventId == _selectedEvent.Id).Id;

        _selectedNodeKeys.Clear();
        _selectedGroup = null;
        _selectedChoice = null;
        _selectedObjectKey = null;
        RefreshHierarchy();
        DrawGraph();
        RefreshIssues();
    }

    private List<ValidationIssue> CompileSelectedEventLogic()
    {
        var issues = new List<ValidationIssue>();
        if (_workbook is null || _selectedEvent is null)
            return issues;

        var groups = _workbook.Groups.Where(g => g.EventId == _selectedEvent.Id).ToList();
        var groupSet = groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0)
        {
            issues.Add(FlowError($"{_selectedEvent.Id}: 장면 노드가 없습니다."));
            return issues;
        }

        if (string.IsNullOrWhiteSpace(_selectedEvent.FirstGroupId) || !groupSet.Contains(_selectedEvent.FirstGroupId))
        {
            issues.Add(FlowError($"{_selectedEvent.Id}: 시작 장면 first_group_id가 유효하지 않습니다. ({_selectedEvent.FirstGroupId})"));
            return issues;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(_selectedEvent.FirstGroupId);

        while (queue.Count > 0)
        {
            var groupId = queue.Dequeue();
            if (!visited.Add(groupId))
                continue;

            var group = groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));
            if (group is null)
                continue;

            var choices = _workbook.Choices.Where(c => c.GroupId == group.Id).OrderBy(c => c.Seq).ToList();
            if (IsBattleGroup(group))
            {
                if (string.IsNullOrWhiteSpace(group.StageId))
                    issues.Add(FlowError($"{group.Id}: next_action=battle인데 stage_id가 없습니다."));
                if (choices.Count > 0)
                    issues.Add(FlowWarning($"{group.Id}: battle 장면에 선택지가 남아 있습니다. export는 battle/stage_id를 우선 사용합니다."));
                continue;
            }
            else if (!string.IsNullOrWhiteSpace(group.StageId))
            {
                issues.Add(FlowWarning($"{group.Id}: stage_id가 있지만 next_action이 battle이 아닙니다. ({group.NextAction})"));
            }

            var groupEndsWithExit = string.Equals(group.NextAction, "exit", StringComparison.OrdinalIgnoreCase);
            if (groupEndsWithExit && choices.Count == 0)
                continue;

            if (string.Equals(group.NextAction, "choice", StringComparison.OrdinalIgnoreCase) && choices.Count == 0)
                issues.Add(FlowWarning($"{group.Id}: choice 장면인데 선택지가 없습니다."));

            foreach (var choice in choices)
            {
                if (choice.SuccessRate is < 0 or > 100)
                    issues.Add(FlowError($"{choice.Id}: success_rate는 0~100 범위여야 합니다. ({choice.SuccessRate})"));

                if (!string.IsNullOrWhiteSpace(choice.SuccessNextGroupId))
                {
                    if (!groupSet.Contains(choice.SuccessNextGroupId))
                        issues.Add(FlowError($"{choice.Id}: T 경로 대상 장면이 없습니다. ({choice.SuccessNextGroupId})"));
                    else
                        queue.Enqueue(choice.SuccessNextGroupId);
                }
                else if (choice.SuccessRewardType == "none")
                {
                    var hasChoiceExit = _workbook.Layouts.ContainsKey(ChoiceExitLayoutKey(choice.Id, "success"));
                    if (!groupEndsWithExit && !hasChoiceExit)
                        issues.Add(FlowWarning($"{choice.Id}: T 경로에 다음 장면도 보상도 없습니다."));
                }

                var hasFailConfig = !string.IsNullOrWhiteSpace(choice.FailNextGroupId)
                    || choice.FailRewardType != "none"
                    || choice.FailRewardAmount is not null;
                if (!IsFailPinEnabled(choice))
                {
                    if (hasFailConfig)
                        issues.Add(FlowError($"{choice.Id}: success_rate가 없어 F 핀이 비활성인데 F 결과가 설정되어 있습니다."));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(choice.FailNextGroupId))
                {
                    if (!groupSet.Contains(choice.FailNextGroupId))
                        issues.Add(FlowError($"{choice.Id}: F 경로 대상 장면이 없습니다. ({choice.FailNextGroupId})"));
                    else
                        queue.Enqueue(choice.FailNextGroupId);
                }
                else if (choice.FailRewardType == "none")
                {
                    var hasChoiceExit = _workbook.Layouts.ContainsKey(ChoiceExitLayoutKey(choice.Id, "fail"));
                    if (!groupEndsWithExit && !hasChoiceExit)
                        issues.Add(FlowWarning($"{choice.Id}: success_rate가 있어 F 핀이 활성인데 F 결과가 비어 있습니다."));
                }
            }
        }

        foreach (var unreachable in groups.Where(g => !visited.Contains(g.Id)).OrderBy(g => g.Id))
            issues.Add(FlowWarning($"{unreachable.Id}: 시작 장면에서 도달할 수 없습니다."));

        return issues;
    }

    private static ValidationIssue FlowError(string message) => new()
    {
        Severity = ValidationSeverity.Error,
        Message = $"컴파일: {message}"
    };

    private static ValidationIssue FlowWarning(string message) => new()
    {
        Severity = ValidationSeverity.Warning,
        Message = $"컴파일: {message}"
    };

    private void RestoreSelectionAfterModelChange()
    {
        if (_workbook is null)
            return;

        if (_selectedEvent is null || !_workbook.Events.Contains(_selectedEvent))
            _selectedEvent = _workbook.Events.OrderBy(e => e.Id).FirstOrDefault();

        if (_selectedGroup is not null && !_workbook.Groups.Contains(_selectedGroup))
            _selectedGroup = null;

        if (_selectedChoice is not null && !_workbook.Choices.Contains(_selectedChoice))
            _selectedChoice = null;

        if (_selectedEvent is not null && _selectedGroup is not null && _selectedGroup.EventId != _selectedEvent.Id)
            _selectedGroup = null;

        if (_selectedChoice is not null)
        {
            var group = _workbook.Groups.FirstOrDefault(g => g.Id == _selectedChoice.GroupId);
            if (group is null || (_selectedEvent is not null && group.EventId != _selectedEvent.Id))
                _selectedChoice = null;
        }
    }

    private void CopySelectedNode()
    {
        if (_workbook is null)
            return;

        var group = _selectedGroup;
        if (group is null && _selectedChoice is not null)
            group = _workbook.Groups.FirstOrDefault(g => g.Id == _selectedChoice.GroupId);
        if (group is null)
            return;

        _copiedGroup = CloneGroupForClipboard(group);
        _copiedChoices = _workbook.Choices
            .Where(c => c.GroupId == group.Id)
            .OrderBy(c => c.Seq)
            .Select(CloneChoiceForClipboard)
            .ToList();
        Log($"COPY NODE: {group.Id}");
    }

    private void PasteCopiedNode()
    {
        if (_workbook is null || _selectedEvent is null || _copiedGroup is null)
            return;

        PushUndo();
        var id = NextGroupId(_selectedEvent.Id);
        var group = CloneGroupForClipboard(_copiedGroup);
        group.Id = id;
        group.EventId = _selectedEvent.Id;
        group.SituationTextTid = $"{id}_situation_text";
        _workbook.Groups.Add(group);

        foreach (var source in _copiedChoices.OrderBy(c => c.Seq))
        {
            var choiceId = $"{id}_C{source.Seq}";
            var choice = CloneChoiceForClipboard(source);
            choice.Id = choiceId;
            choice.GroupId = id;
            choice.ChoiceTextTid = $"{choiceId}_choice_text";
            choice.SuccessNextGroupId = "";
            choice.FailNextGroupId = "";
            _workbook.Choices.Add(choice);
        }

        var basePoint = new Point(120, 120);
        if (_selectedGroup is not null && _workbook.Layouts.TryGetValue(_selectedGroup.Id, out var selectedLayout))
            basePoint = new Point(selectedLayout.X + 46, selectedLayout.Y + 46);
        else if (_copiedGroup is not null && _workbook.Layouts.TryGetValue(_copiedGroup.Id, out var copiedLayout))
            basePoint = new Point(copiedLayout.X + 46, copiedLayout.Y + 46);

        _workbook.Layouts[id] = new NodeLayout
        {
            EventId = _selectedEvent.Id,
            GroupId = id,
            X = RoundCanvasCoord(basePoint.X),
            Y = RoundCanvasCoord(basePoint.Y),
            Width = NodeWidth,
            Height = CalculateNodeHeight(_copiedChoices.Count)
        };

        _selectedGroup = group;
        _selectedChoice = null;
        RefreshHierarchy();
        DrawGraph();
        RefreshIssues();
        Log($"PASTE NODE: {id} / 연결 제거됨");
    }

    private string NextGroupId(string eventId)
    {
        if (_workbook is null)
            return $"{eventId}_G1";
        var next = _workbook.Groups
            .Where(g => g.EventId == eventId && g.Id.Contains("_G", StringComparison.OrdinalIgnoreCase))
            .Select(g =>
            {
                var last = g.Id.Split("_G").LastOrDefault();
                return int.TryParse(last, out var n) ? n : 0;
            })
            .DefaultIfEmpty(0)
            .Max() + 1;
        return $"{eventId}_G{next}";
    }

    private static ChoiceGroupRow CloneGroupForClipboard(ChoiceGroupRow source) => new()
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

    private static EventChoiceRow CloneChoiceForClipboard(EventChoiceRow source) => new()
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

    private void PushUndo()
    {
        if (_workbook is null)
            return;
        _undoStack.Push(CloneWorkbook(_workbook));
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
            return;
        var eventId = _selectedEvent?.Id;
        var groupId = _selectedGroup?.Id;
        var choiceId = _selectedChoice?.Id;
        var objectKey = _selectedObjectKey;
        _workbook = _undoStack.Pop();
        _selectedEvent = !string.IsNullOrWhiteSpace(eventId)
            ? _workbook.Events.FirstOrDefault(e => string.Equals(e.Id, eventId, StringComparison.OrdinalIgnoreCase))
            : null;
        _selectedGroup = !string.IsNullOrWhiteSpace(groupId)
            ? _workbook.Groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase))
            : null;
        _selectedChoice = !string.IsNullOrWhiteSpace(choiceId)
            ? _workbook.Choices.FirstOrDefault(c => string.Equals(c.Id, choiceId, StringComparison.OrdinalIgnoreCase))
            : null;
        _selectedObjectKey = !string.IsNullOrWhiteSpace(objectKey) && _workbook.Layouts.ContainsKey(objectKey)
            ? objectKey
            : null;
        RestoreSelectionAfterModelChange();
        RefreshEventList();
        RefreshHierarchy();
        DrawGraph();
        RefreshIssues();
        Log("UNDO");
    }

    private static EventWorkbook CloneWorkbook(EventWorkbook source)
    {
        var clone = new EventWorkbook { SourcePath = source.SourcePath };
        foreach (var e in source.Events)
            clone.Events.Add(new EventBaseRow { Id = e.Id, Memo = e.Memo, ExportId = e.ExportId, EventNameTid = e.EventNameTid, Rarity = e.Rarity, FloorRestriction = e.FloorRestriction, DiffRestriction = e.DiffRestriction, Weight = e.Weight, FirstGroupId = e.FirstGroupId });
        foreach (var g in source.Groups)
            clone.Groups.Add(new ChoiceGroupRow { Id = g.Id, Memo = g.Memo, ExportId = g.ExportId, EventId = g.EventId, Background = g.Background, NpcId = g.NpcId, SituationTextTid = g.SituationTextTid, NextAction = g.NextAction, StageId = g.StageId });
        foreach (var c in source.Choices)
            clone.Choices.Add(CloneChoiceForClipboard(c));
        foreach (var t in source.TextEntries)
            clone.TextEntries.Add(new TextEntry { ExportId = t.ExportId, Tid = t.Tid, Text = t.Text, Comment = t.Comment });
        foreach (var pair in source.Layouts)
            clone.Layouts[pair.Key] = new NodeLayout { EventId = pair.Value.EventId, GroupId = pair.Value.GroupId, X = pair.Value.X, Y = pair.Value.Y, Width = pair.Value.Width, Height = pair.Value.Height };
        foreach (var pair in source.ColumnHelps)
            clone.ColumnHelps[pair.Key] = pair.Value;
        return clone;
    }

    private void Log(string message)
    {
        var line = $"INFO: {message}";
        _notiMessages.Add(line);
        RefreshConsoleLists();
        ConsoleTabs.SelectedIndex = 0;
    }

    private void ConsoleSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshConsoleLists();
    }

    private void ConsoleClearButton_Click(object sender, RoutedEventArgs e)
    {
        _notiMessages.Clear();
        RefreshConsoleLists();
    }

    private SplashWindow ShowBusy(string status)
    {
        var busy = new SplashWindow(status)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Topmost = false
        };
        busy.Show();
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        return busy;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        PromptOpenWorkbook();
    }

    private void PromptOpenWorkbook()
    {
        var dialog = new OpenFileDialog
        {
            Title = "nexus_event 차원 탐사 이벤트.xlsx 선택",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists("D:\\repos\\design\\DB\\alpha") ? "D:\\repos\\design\\DB\\alpha" : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true)
        {
            if (!NexusPathResolver.LooksLikeEventWorkbook(dialog.FileName))
            {
                MessageBox.Show(this, "nexus_event 테이블 구조가 아닙니다.", "Invalid workbook", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LoadWorkbook(dialog.FileName);
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null || string.IsNullOrWhiteSpace(_workbook.SourcePath))
            LoadInitialWorkbook();
        else
            LoadWorkbook(_workbook.SourcePath);
    }

    private void AutoLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null || _selectedEvent is null)
            return;
        PushUndo();
        AutoLayoutSelectedEvent();
        _centerGraphOnNextDraw = true;
        DrawGraph();
        RefreshIssues();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeLocked || IsPlayerRunning())
        {
            StopPlayer();
            return;
        }

        StartSelectedEventPlayer();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPlayerRunning() || _playerProcess is null)
            return;

        try
        {
            if (_playerPaused)
            {
                NtResumeProcess(_playerProcess.Handle);
                _playerPaused = false;
                Log("Player resumed.");
            }
            else
            {
                NtSuspendProcess(_playerProcess.Handle);
                _playerPaused = true;
                Log("Player paused.");
            }
            UpdateRuntimeUiState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Pause failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayer();

    private void StartSelectedEventPlayer()
    {
        if (_workbook is null || _selectedEvent is null)
            return;

        EnsureGeneratedTids(force: false);
        RefreshIssues();
        var errors = EventWorkbookService.Validate(_workbook)
            .Concat(CompileSelectedEventLogic())
            .Count(i => i.Severity == ValidationSeverity.Error);
        if (errors > 0)
        {
            Log($"COMPILE FAILED: {_selectedEvent.Id} / 오류 {errors}건");
            MessageBox.Show(this, $"로직 오류 {errors}건이 있습니다. Console / Validation을 확인하세요.", "Compile failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var playerExe = ResolvePlayerExeOrPrompt();
        if (string.IsNullOrWhiteSpace(playerExe))
            return;

        try
        {
            var runtimeWorkbook = CreateRuntimeWorkbook();
            WritePlayerLaunchRequest(playerExe, runtimeWorkbook, _runtimeRelicWorkbookPath, _selectedEvent.Id);

            var startInfo = new ProcessStartInfo
            {
                FileName = playerExe,
                WorkingDirectory = IoPath.GetDirectoryName(playerExe) ?? "",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("--event-id");
            startInfo.ArgumentList.Add(_selectedEvent.Id);
            startInfo.ArgumentList.Add("--excel");
            startInfo.ArgumentList.Add(runtimeWorkbook);
            if (!string.IsNullOrWhiteSpace(_runtimeRelicWorkbookPath))
            {
                startInfo.ArgumentList.Add("--relic");
                startInfo.ArgumentList.Add(_runtimeRelicWorkbookPath);
            }
            startInfo.ArgumentList.Add("--from-editor");

            var process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("플레이어 프로세스를 시작하지 못했습니다.");

            _playerProcess = process;
            _playerExePathInUse = playerExe;
            _runtimeLocked = true;
            _playerPaused = false;
            _playerProcess.EnableRaisingEvents = true;
            _playerProcess.Exited += (_, _) => Dispatcher.BeginInvoke(() => OnPlayerExited("event"));
            _playerWatchTimer.Start();
            Keyboard.ClearFocus();
            Log($"PLAYER START: {_selectedEvent.Id} / {IoPath.GetFileName(playerExe)}");
            UpdateRuntimeUiState();
        }
        catch (IOException)
        {
            MessageBox.Show(this, "엑셀을 종료해주세요.", "Runtime workbook failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, "엑셀을 종료해주세요.", "Runtime workbook failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Player launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? ResolvePlayerExeOrPrompt()
    {
        var resolved = NexusPathResolver.ResolveDefaultPlayerExe();
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        var dialog = new OpenFileDialog
        {
            Title = "Dimension Event Player 위치 선택",
            Filter = $"{NexusPathResolver.PlayerExeName}|{NexusPathResolver.PlayerExeName}|Executable (*.exe)|*.exe|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(IoPath.GetDirectoryName(_settings.PlayerExePath ?? "") ?? "")
                ? IoPath.GetDirectoryName(_settings.PlayerExePath!)!
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true)
            return null;

        if (!NexusPathResolver.LooksLikePlayerExe(dialog.FileName))
        {
            MessageBox.Show(this, "플레이어 경로가 잘못되었습니다. exe와 pck가 같은 폴더에 있어야 합니다.", "Invalid player", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        _settings.PlayerExePath = dialog.FileName;
        NexusPathResolver.SaveSettings(_settings);
        return dialog.FileName;
    }

    private string CreateRuntimeWorkbook()
    {
        if (_workbook is null || _selectedEvent is null)
            throw new InvalidOperationException("선택된 이벤트가 없습니다.");

        var runtimeDir = IoPath.Combine(IoPath.GetTempPath(), "DimensionEventEditor", "Runtime");
        Directory.CreateDirectory(runtimeDir);
        var output = IoPath.Combine(runtimeDir, $"nexus_event_runtime_{_selectedEvent.Id}.xlsx");
        if (File.Exists(output))
            File.Delete(output);
        EventWorkbookService.SaveAs(_workbook, output, createBackup: false);
        _runtimeRelicWorkbookPath = CopyRuntimeRelicWorkbook(runtimeDir);
        return output;
    }

    private string? CopyRuntimeRelicWorkbook(string runtimeDir)
    {
        if (_workbook is null)
            return null;

        var sourceDir = IoPath.GetDirectoryName(_workbook.SourcePath);
        var relicSource = "";
        if (!string.IsNullOrWhiteSpace(sourceDir) && Directory.Exists(sourceDir))
        {
            relicSource = Directory.EnumerateFiles(sourceDir, "nexus_relic*.xlsx")
                .FirstOrDefault(p => !IoPath.GetFileName(p).StartsWith("~$", StringComparison.OrdinalIgnoreCase)) ?? "";
        }

        if (string.IsNullOrWhiteSpace(relicSource))
        {
            var fallback = IoPath.Combine("D:\\repos\\design\\DB\\alpha", "nexus_relic 차원 탐사 유물.xlsx");
            if (File.Exists(fallback))
                relicSource = fallback;
        }

        if (string.IsNullOrWhiteSpace(relicSource) || !File.Exists(relicSource))
            return null;

        var destination = IoPath.Combine(runtimeDir, "nexus_relic 차원 탐사 유물.xlsx");
        if (File.Exists(destination))
            File.Delete(destination);
        File.Copy(relicSource, destination, overwrite: true);
        return destination;
    }

    private void WritePlayerLaunchRequest(string playerExe, string runtimeWorkbook, string? relicWorkbook, string eventId)
    {
        var playerDir = IoPath.GetDirectoryName(playerExe);
        if (string.IsNullOrWhiteSpace(playerDir))
            return;
        var dataDir = IoPath.Combine(playerDir, "data");
        Directory.CreateDirectory(dataDir);
        var json = JsonSerializer.Serialize(new
        {
            event_id = eventId,
            eventId,
            excel = runtimeWorkbook,
            relic = relicWorkbook ?? "",
            workbook_path = runtimeWorkbook,
            relic_workbook_path = relicWorkbook ?? "",
            source_workbook_path = _workbook?.SourcePath ?? "",
            requested_at = DateTimeOffset.Now.ToString("O")
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(IoPath.Combine(dataDir, "editor_launch.json"), json);
    }

    private bool IsPlayerRunning()
    {
        try
        {
            return _playerProcess is not null && !_playerProcess.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void StopPlayer()
    {
        try
        {
            if (_playerProcess is not null && !_playerProcess.HasExited)
            {
                if (_playerPaused)
                {
                    try { NtResumeProcess(_playerProcess.Handle); } catch { }
                }
                _playerProcess.Kill(entireProcessTree: true);
                _playerProcess.WaitForExit(2000);
            }
            KillKnownPlayerProcesses();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Stop failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            OnPlayerExited("stop");
        }
    }

    private void KillKnownPlayerProcesses()
    {
        var expectedPath = _playerExePathInUse;
        var expectedName = IoPath.GetFileNameWithoutExtension(expectedPath ?? NexusPathResolver.PlayerExeName);
        foreach (var process in Process.GetProcessesByName(expectedName))
        {
            if (process.Id == Environment.ProcessId)
                continue;

            try
            {
                if (!string.IsNullOrWhiteSpace(expectedPath))
                {
                    string? modulePath = null;
                    try { modulePath = process.MainModule?.FileName; } catch { }
                    if (!string.Equals(modulePath, expectedPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
            }
            catch
            {
                // Best effort cleanup. UI normalization must still proceed.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void PlayerWatchTimer_Tick(object? sender, EventArgs e)
    {
        if (_playerProcess is null)
        {
            _playerWatchTimer.Stop();
            UpdateRuntimeUiState();
            return;
        }

        try
        {
            if (_playerProcess.HasExited)
                OnPlayerExited("watchdog");
        }
        catch
        {
            OnPlayerExited("watchdog");
        }
    }

    private void OnPlayerExited(string reason)
    {
        var hadProcess = _playerProcess is not null;
        if (_playerProcess is not null)
        {
            _playerProcess.Dispose();
            _playerProcess = null;
        }
        _playerPaused = false;
        _runtimeLocked = false;
        _playerExePathInUse = null;
        _playerWatchTimer.Stop();
        UpdateRuntimeUiState();
        if (hadProcess)
            Log($"PLAYER STOP ({reason})");
    }

    private void UpdateRuntimeUiState()
    {
        var locked = _runtimeLocked;
        var running = locked && IsPlayerRunning();
        EditorSurface.IsHitTestVisible = !locked;
        OpenButton.IsEnabled = !locked;
        ReloadButton.IsEnabled = !locked;
        AutoLayoutButton.IsEnabled = !locked;
        PreviewButton.IsEnabled = !locked;
        PlayButton.IsEnabled = true;
        PlayButton.Background = new SolidColorBrush(locked ? Color.FromRgb(42, 91, 148) : Color.FromRgb(58, 58, 58));
        PlayButton.BorderBrush = new SolidColorBrush(locked ? Color.FromRgb(72, 126, 184) : Color.FromRgb(86, 86, 86));
        PauseButton.IsEnabled = running;
        PauseButton.Content = _playerPaused ? "▶" : "Ⅱ";
        StopButton.IsEnabled = locked;
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null)
            return;

        EnsureGeneratedTids(force: false);
        RefreshIssues();
        var errors = EventWorkbookService.Validate(_workbook).Where(i => i.Severity == ValidationSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, "오류가 있어 Export 할 수 없습니다.", "Validation failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var preview = new PreviewWindow(
            () => EventWorkbookService.BuildDiff(_workbook),
            entry =>
            {
                if (EventWorkbookService.RevertDiff(_workbook, entry))
                {
                    RestoreSelectionAfterModelChange();
                    RefreshEventList();
                    RefreshHierarchy();
                    RefreshIssues();
                    DrawGraph();
                }
            },
            BuildPreviewGraph,
            _settings.LastExportId);
        if (preview.ShowDialog() == true)
        {
            try
            {
                foreach (var uncheckedEntry in preview.UncheckedEntries)
                    EventWorkbookService.RevertDiff(_workbook, uncheckedEntry);
                var exportId = preview.ExportId;
                if (!string.IsNullOrWhiteSpace(preview.ExportId))
                {
                    EventWorkbookService.ApplyExportId(_workbook, preview.CheckedEntries, exportId);
                    _settings.LastExportId = exportId;
                    NexusPathResolver.SaveSettings(_settings);
                }
                var busy = ShowBusy("Exporting workbook...");
                try
                {
                    EventWorkbookService.SaveAs(_workbook, _workbook.SourcePath, createBackup: true);
                }
                finally
                {
                    busy.Close();
                }
                LoadWorkbook(_workbook.SourcePath);
                MessageBox.Show(this, "Export 완료", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (IOException)
            {
                MessageBox.Show(this, "엑셀을 종료해주세요.", "File locked", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show(this, "엑셀을 종료해주세요.", "File locked", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private EventGraphPreview? BuildPreviewGraph(string eventId, bool before)
    {
        if (_workbook is null || string.IsNullOrWhiteSpace(eventId))
            return null;

        var source = before && File.Exists(_workbook.SourcePath)
            ? EventWorkbookService.Load(_workbook.SourcePath)
            : _workbook;
        var evt = source.Events.FirstOrDefault(e => string.Equals(e.Id, eventId, StringComparison.OrdinalIgnoreCase));
        if (evt is null)
            return null;

        var graph = new EventGraphPreview
        {
            EventId = evt.Id,
            EventName = evt.Memo
        };
        var groups = source.Groups.Where(g => string.Equals(g.EventId, evt.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Id).ToList();
        if (groups.Count == 0)
            return graph;

        var groupSet = groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choicesByGroup = source.Choices
            .Where(c => groupSet.Contains(c.GroupId))
            .GroupBy(c => c.GroupId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Seq).ToList(), StringComparer.OrdinalIgnoreCase);

        var rawLayouts = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var group in groups)
        {
            var choices = choicesByGroup.TryGetValue(group.Id, out var list) ? list : [];
            if (source.Layouts.TryGetValue(group.Id, out var layout))
                rawLayouts[group.Id] = new Rect(layout.X, layout.Y, Math.Max(layout.Width, NodeWidth), Math.Max(layout.Height, CalculateNodeHeight(choices.Count)));
            else
                rawLayouts[group.Id] = new Rect(80 + (index % 3) * 420, 80 + (index / 3) * 260, NodeWidth, CalculateNodeHeight(choices.Count));
            index++;
        }

        foreach (var choice in choicesByGroup.Values.SelectMany(x => x))
        {
            AddPreviewRewardLayout(source, graph, rawLayouts, groups, choice, "success", choice.SuccessRewardType, choice.SuccessRewardAmount, choice.SuccessNextGroupId);
            AddPreviewChoiceExitLayout(source, rawLayouts, choice, "success");
            if (IsFailPinEnabled(choice))
            {
                AddPreviewRewardLayout(source, graph, rawLayouts, groups, choice, "fail", choice.FailRewardType, choice.FailRewardAmount, choice.FailNextGroupId);
                AddPreviewChoiceExitLayout(source, rawLayouts, choice, "fail");
            }
        }
        foreach (var group in groups.Where(IsBattleGroup))
        {
            var key = BattleLayoutKey(group.Id);
            var groupRect = rawLayouts[group.Id];
            rawLayouts[key] = source.Layouts.TryGetValue(key, out var layout)
                ? new Rect(layout.X, layout.Y, Math.Max(layout.Width, BattleNodeWidth), Math.Max(layout.Height, BattleNodeHeight))
                : new Rect(groupRect.Right + 84, groupRect.Top + 18, BattleNodeWidth, BattleNodeHeight);
        }
        foreach (var group in groups.Where(g => IsExitGroup(g) && source.Layouts.ContainsKey(GroupRewardLayoutKey(g.Id))))
        {
            var key = GroupRewardLayoutKey(group.Id);
            var groupRect = rawLayouts[group.Id];
            rawLayouts[key] = source.Layouts.TryGetValue(key, out var layout)
                ? new Rect(layout.X, layout.Y, Math.Max(layout.Width, RewardNodeWidth), Math.Max(layout.Height, RewardNodeHeight))
                : new Rect(groupRect.Right + 84, groupRect.Top + Math.Max(0, groupRect.Height - RewardNodeHeight) / 2, RewardNodeWidth, RewardNodeHeight);
        }
        foreach (var group in groups.Where(g => IsExitGroup(g) && !source.Layouts.ContainsKey(GroupRewardLayoutKey(g.Id))))
        {
            var key = ExitLayoutKey(group.Id);
            var groupRect = rawLayouts[group.Id];
            rawLayouts[key] = source.Layouts.TryGetValue(key, out var layout)
                ? new Rect(layout.X, layout.Y, Math.Max(layout.Width, ExitNodeWidth), Math.Max(layout.Height, ExitNodeHeight))
                : new Rect(groupRect.Right + 84, groupRect.Top + Math.Max(0, groupRect.Height - ExitNodeHeight) / 2, ExitNodeWidth, ExitNodeHeight);
        }

        var bounds = rawLayouts.Values.First();
        foreach (var rect in rawLayouts.Values.Skip(1))
            bounds.Union(rect);
        var dx = 40 - bounds.Left;
        var dy = 40 - bounds.Top;

        foreach (var group in groups)
        {
            var rect = rawLayouts[group.Id];
            var choices = choicesByGroup.TryGetValue(group.Id, out var list) ? list : [];
            var node = new EventGraphPreviewNode
            {
                Id = group.Id,
                Kind = "scene",
                Title = group.Id,
                Body = group.Memo,
                X = rect.X + dx,
                Y = rect.Y + dy,
                Width = rect.Width,
                Height = rect.Height
            };
            foreach (var choice in choices)
            {
                node.Rows.Add($"{choice.Seq}. {choice.Memo}");
                node.RowIds.Add(choice.Id);
            }
            graph.Nodes.Add(node);
        }

        foreach (var choice in choicesByGroup.Values.SelectMany(x => x))
        {
            AddPreviewRewardNode(source, graph, rawLayouts, dx, dy, choice, "success", choice.SuccessRewardType, choice.SuccessRewardAmount);
            AddPreviewChoiceExitNode(graph, rawLayouts, dx, dy, choice, "success");
            if (IsFailPinEnabled(choice))
            {
                AddPreviewRewardNode(source, graph, rawLayouts, dx, dy, choice, "fail", choice.FailRewardType, choice.FailRewardAmount);
                AddPreviewChoiceExitNode(graph, rawLayouts, dx, dy, choice, "fail");
            }
        }
        foreach (var group in groups.Where(IsBattleGroup))
        {
            var key = BattleLayoutKey(group.Id);
            if (!rawLayouts.TryGetValue(key, out var rect))
                continue;
            graph.Nodes.Add(new EventGraphPreviewNode
            {
                Id = key,
                Kind = "battle",
                Title = "Battle",
                Body = string.IsNullOrWhiteSpace(group.StageId) ? "stage_id: -" : group.StageId,
                X = rect.X + dx,
                Y = rect.Y + dy,
                Width = rect.Width,
                Height = rect.Height
            });
            graph.Links.Add(new EventGraphPreviewLink { FromId = group.Id, ToId = key, Label = "BATTLE" });
        }
        foreach (var group in groups.Where(g => IsExitGroup(g) && source.Layouts.ContainsKey(GroupRewardLayoutKey(g.Id))))
        {
            var key = GroupRewardLayoutKey(group.Id);
            if (!rawLayouts.TryGetValue(key, out var rect))
                continue;
            graph.Nodes.Add(new EventGraphPreviewNode
            {
                Id = key,
                Kind = "reward",
                Title = "Reward",
                Body = "event reward / exit",
                X = rect.X + dx,
                Y = rect.Y + dy,
                Width = rect.Width,
                Height = rect.Height
            });
            graph.Links.Add(new EventGraphPreviewLink { FromId = group.Id, ToId = key, Label = "REWARD" });
        }
        foreach (var group in groups.Where(g => IsExitGroup(g) && !source.Layouts.ContainsKey(GroupRewardLayoutKey(g.Id))))
        {
            var key = ExitLayoutKey(group.Id);
            if (!rawLayouts.TryGetValue(key, out var rect))
                continue;
            graph.Nodes.Add(new EventGraphPreviewNode
            {
                Id = key,
                Kind = "exit",
                Title = "Exit",
                Body = "event end",
                X = rect.X + dx,
                Y = rect.Y + dy,
                Width = rect.Width,
                Height = rect.Height
            });
            graph.Links.Add(new EventGraphPreviewLink { FromId = group.Id, ToId = key, Label = "EXIT" });
        }

        foreach (var choice in choicesByGroup.Values.SelectMany(x => x))
        {
            AddPreviewLinks(graph, choice.GroupId, choice, "success", choice.SuccessRewardType, choice.SuccessNextGroupId);
            if (IsFailPinEnabled(choice))
                AddPreviewLinks(graph, choice.GroupId, choice, "fail", choice.FailRewardType, choice.FailNextGroupId);
        }

        return graph;
    }

    private static void AddPreviewRewardLayout(EventWorkbook source, EventGraphPreview graph, Dictionary<string, Rect> layouts, List<ChoiceGroupRow> groups, EventChoiceRow choice, string branch, string rewardType, int? rewardAmount, string nextGroupId)
    {
        if (rewardType == "none")
            return;
        var key = RewardLayoutKey(choice.Id, branch);
        if (source.Layouts.TryGetValue(key, out var layout))
        {
            layouts[key] = new Rect(layout.X, layout.Y, Math.Max(layout.Width, RewardNodeWidth), Math.Max(layout.Height, RewardNodeHeight));
            return;
        }
        if (!layouts.TryGetValue(choice.GroupId, out var groupRect))
            return;
        var yOffset = branch == "success" ? -RewardNodeHeight - 8 : 8;
        layouts[key] = new Rect(groupRect.Right + 72, groupRect.Top + yOffset, RewardNodeWidth, RewardNodeHeight);
    }

    private static void AddPreviewChoiceExitLayout(EventWorkbook source, Dictionary<string, Rect> layouts, EventChoiceRow choice, string branch)
    {
        var key = ChoiceExitLayoutKey(choice.Id, branch);
        if (source.Layouts.TryGetValue(key, out var layout))
            layouts[key] = new Rect(layout.X, layout.Y, Math.Max(layout.Width, ExitNodeWidth), Math.Max(layout.Height, ExitNodeHeight));
    }

    private static void AddPreviewRewardNode(EventWorkbook source, EventGraphPreview graph, Dictionary<string, Rect> layouts, double dx, double dy, EventChoiceRow choice, string branch, string rewardType, int? rewardAmount)
    {
        if (rewardType == "none")
            return;
        var key = RewardLayoutKey(choice.Id, branch);
        if (!layouts.TryGetValue(key, out var rect))
            return;
        graph.Nodes.Add(new EventGraphPreviewNode
        {
            Id = key,
            Kind = "reward",
            Title = $"{(branch == "success" ? "T" : "F")} Reward",
            Body = $"{rewardType} x{rewardAmount?.ToString() ?? "-"}",
            X = rect.X + dx,
            Y = rect.Y + dy,
            Width = rect.Width,
            Height = rect.Height
        });
    }

    private static void AddPreviewChoiceExitNode(EventGraphPreview graph, Dictionary<string, Rect> layouts, double dx, double dy, EventChoiceRow choice, string branch)
    {
        var key = ChoiceExitLayoutKey(choice.Id, branch);
        if (!layouts.TryGetValue(key, out var rect))
            return;
        graph.Nodes.Add(new EventGraphPreviewNode
        {
            Id = key,
            Kind = "exit",
            Title = $"{(branch == "success" ? "T" : "F")} Exit",
            Body = "event end",
            X = rect.X + dx,
            Y = rect.Y + dy,
            Width = rect.Width,
            Height = rect.Height
        });
    }

    private static void AddPreviewLinks(EventGraphPreview graph, string groupId, EventChoiceRow choice, string branch, string rewardType, string nextGroupId)
    {
        var label = branch == "success" ? "T" : "F";
        var rewardKey = RewardLayoutKey(choice.Id, branch);
        var choiceExitKey = ChoiceExitLayoutKey(choice.Id, branch);
        var exitKey = graph.Nodes.Any(n => string.Equals(n.Id, choiceExitKey, StringComparison.OrdinalIgnoreCase))
            ? choiceExitKey
            : "";
        var hasExit = !string.IsNullOrWhiteSpace(exitKey);
        if (rewardType != "none" && graph.Nodes.Any(n => string.Equals(n.Id, rewardKey, StringComparison.OrdinalIgnoreCase)))
        {
            graph.Links.Add(new EventGraphPreviewLink { FromId = groupId, ToId = rewardKey, Label = label });
            if (!string.IsNullOrWhiteSpace(nextGroupId))
                graph.Links.Add(new EventGraphPreviewLink { FromId = rewardKey, ToId = nextGroupId, Label = "" });
            else if (hasExit)
                graph.Links.Add(new EventGraphPreviewLink { FromId = rewardKey, ToId = exitKey, Label = "" });
            return;
        }
        if (!string.IsNullOrWhiteSpace(nextGroupId))
            graph.Links.Add(new EventGraphPreviewLink { FromId = groupId, ToId = nextGroupId, Label = label });
        else if (hasExit)
            graph.Links.Add(new EventGraphPreviewLink { FromId = groupId, ToId = exitKey, Label = label });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshEventList();

    private void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEventSelectionChanged)
            return;

        _selectedEvent = EventList.SelectedItem as EventBaseRow;
        _selectedGroup = null;
        _selectedChoice = null;
        _selectedObjectKey = null;
        _selectedNodeKeys.Clear();
        _centerGraphOnNextDraw = true;
        DrawGraph();
    }

    private void EventList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (item?.DataContext is not EventBaseRow evt)
            return;

        EventList.SelectedItem = evt;
        var menu = new ContextMenu();
        var delete = new MenuItem { Header = "이벤트 삭제" };
        delete.Click += (_, _) => DeleteEvent(evt);
        menu.Items.Add(delete);
        item.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void AddEventButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null)
            return;
        var id = EventWorkbookService.NextEventId(_workbook);
        var firstGroupId = $"{id}_G1";
        var evt = new EventBaseRow
        {
            Id = id,
            Memo = "새 이벤트",
            EventNameTid = $"{id}_name",
            FirstGroupId = firstGroupId
        };
        var group = new ChoiceGroupRow
        {
            Id = firstGroupId,
            EventId = id,
            Memo = "새 이벤트 상황",
            SituationTextTid = $"{firstGroupId}_situation_text"
        };
        _workbook.Events.Add(evt);
        _workbook.Groups.Add(group);
        _workbook.Layouts[group.Id] = new NodeLayout { EventId = id, GroupId = group.Id, X = 80, Y = 80 };
        _selectedEvent = evt;
        RefreshEventList();
        RefreshHierarchy();
        EventList.SelectedItem = evt;
        DrawGraph();
    }

    private void AddGroupButton_Click(object sender, RoutedEventArgs e) => AddGroup();

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string groupId || _workbook is null)
            return;
        var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is null)
            return;
        if (_selectedNodeKeys.Count > 1 && _selectedNodeKeys.Contains(groupId))
        {
            BeginLayoutDrag(groupId, e);
            e.Handled = true;
            return;
        }
        _selectedNodeKeys.Clear();
        BuildGroupInspector(group);
        _selectedObjectKey = null;
        RefreshHierarchy();
        DrawGraph();
        BeginLayoutDrag(groupId, e);
        e.Handled = true;
    }

    private void ChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string choiceId } && _workbook is not null)
        {
            var choice = _workbook.Choices.FirstOrDefault(c => c.Id == choiceId);
            if (choice is not null)
            {
                _selectedNodeKeys.Clear();
                _selectedObjectKey = null;
                BuildChoiceInspector(choice);
                RefreshHierarchy();
                DrawGraph();
            }
        }
    }

    private void RewardNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string choiceId } || _workbook is null)
            return;
        var choice = _workbook.Choices.FirstOrDefault(c => c.Id == choiceId);
        if (choice is null)
            return;
        _selectedNodeKeys.Clear();
        BuildChoiceInspector(choice);
        RefreshHierarchy();
        DrawGraph();
        e.Handled = true;
    }

    private void BattleNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string groupId } || _workbook is null)
            return;
        var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is null)
            return;
        _selectedNodeKeys.Clear();
        BuildGroupInspector(group);
        RefreshHierarchy();
        DrawGraph();
        e.Handled = true;
    }

    private void ObjectNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || _workbook is null)
            return;

        if (_selectedNodeKeys.Count > 1 && _selectedNodeKeys.Contains(key))
        {
            BeginLayoutDrag(key, e);
            e.Handled = true;
            return;
        }
        _selectedNodeKeys.Clear();
        _selectedObjectKey = key;
        if (key.StartsWith(PendingRewardPrefix, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(PendingBattlePrefix, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(PendingExitPrefix, StringComparison.OrdinalIgnoreCase))
        {
            BuildPendingObjectInspector(key);
        }
        else if (key.StartsWith("reward|", StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Split('|');
            if (parts.Length >= 3)
            {
                var choice = _workbook.Choices.FirstOrDefault(c => c.Id == parts[1]);
                if (choice is not null)
                {
                    _selectedChoice = choice;
                    _selectedGroup = _workbook.Groups.FirstOrDefault(g => g.Id == choice.GroupId);
                    BuildChoiceInspector(choice);
                }
            }
        }
        else if (key.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Split('|');
            if (parts.Length >= 3)
            {
                var choice = _workbook.Choices.FirstOrDefault(c => c.Id == parts[1]);
                if (choice is not null)
                {
                    _selectedChoice = choice;
                    _selectedGroup = _workbook.Groups.FirstOrDefault(g => g.Id == choice.GroupId);
                    BuildChoiceInspector(choice);
                }
            }
        }
        else if (key.StartsWith("battle|", StringComparison.OrdinalIgnoreCase))
        {
            var groupId = key.Split('|').ElementAtOrDefault(1) ?? "";
            var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group is not null)
            {
                _selectedGroup = group;
                _selectedChoice = null;
                BuildGroupInspector(group);
            }
        }
        else if (key.StartsWith(GroupRewardPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var groupId = key.Split('|').ElementAtOrDefault(1) ?? "";
            var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group is not null)
            {
                _selectedGroup = group;
                _selectedChoice = null;
                BuildGroupInspector(group);
            }
        }
        else if (key.StartsWith("exit|", StringComparison.OrdinalIgnoreCase))
        {
            var groupId = key.Split('|').ElementAtOrDefault(1) ?? "";
            var group = _workbook.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group is not null)
            {
                _selectedGroup = group;
                _selectedChoice = null;
                BuildGroupInspector(group);
            }
        }

        BeginLayoutDrag(key, e);
        RefreshHierarchy();
        e.Handled = true;
    }

    private void BuildPendingObjectInspector(string key)
    {
        InspectorPanel.Children.Clear();
        ClearInspectorPreview();
        var isReward = key.StartsWith(PendingRewardPrefix, StringComparison.OrdinalIgnoreCase);
        var isBattle = key.StartsWith(PendingBattlePrefix, StringComparison.OrdinalIgnoreCase);
        InspectorPanel.Children.Add(SectionTitle(isReward ? "보상 노드" : isBattle ? "전투 노드" : "Exit 노드"));
        InspectorPanel.Children.Add(new TextBlock
        {
            Text = isReward
                ? "장면 노드의 오른쪽 출력 핀에서 연결하면 해당 장면은 보상을 받고 종료됩니다. next_action은 exit로 저장됩니다."
                : isBattle
                    ? "장면 노드의 오른쪽 출력 핀에서 연결하면 해당 장면의 next_action이 battle로 저장됩니다. stage_id는 장면 인스펙터에서 입력합니다."
                    : "장면 노드의 오른쪽 출력 핀에서 연결하면 해당 장면의 next_action이 exit로 저장됩니다.",
            Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 190)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12)
        });
        var delete = new Button
        {
            Content = "노드 삭제",
            Height = 32,
            Background = new SolidColorBrush(Color.FromRgb(112, 45, 52)),
            Foreground = Brushes.White
        };
        delete.Click += (_, _) => DeleteSelected();
        InspectorPanel.Children.Add(delete);
    }

    private void BeginLayoutDrag(string layoutKey, MouseButtonEventArgs e)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(layoutKey, out var layout))
            return;

        var mouse = e.GetPosition(GraphCanvas);
        _dragLayoutKey = layoutKey;
        _dragOffset = new Point(mouse.X - layout.X, mouse.Y - layout.Y);
        _dragUndoPushed = false;
        _isDraggingNodeSelection = _selectedNodeKeys.Count > 1 && _selectedNodeKeys.Contains(layoutKey);
        _multiDragStartMouse = mouse;
        _multiDragStartPositions = _isDraggingNodeSelection
            ? _selectedNodeKeys
                .Where(k => _workbook.Layouts.ContainsKey(k))
                .ToDictionary(k => k, k => new Point(_workbook.Layouts[k].X, _workbook.Layouts[k].Y), StringComparer.OrdinalIgnoreCase)
            : [];
        Mouse.Capture(GraphCanvas);
    }

    private void Connector_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } element)
            return;
        var parts = tag.Split('|');
        if (parts.Length != 2)
            return;
        _linkGroupActionId = null;
        _linkChoiceId = parts[0];
        _linkBranch = parts[1];
        var start = element.TranslatePoint(
            new Point(element.ActualWidth / 2, element.ActualHeight / 2),
            GraphCanvas);
        _linkPreviewLine = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = start.X,
            Y2 = start.Y,
            Stroke = _linkBranch == "success" ? Brushes.LightGreen : Brushes.IndianRed,
            StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        };
        GraphCanvas.Children.Add(_linkPreviewLine);
        Mouse.Capture(GraphCanvas);
        e.Handled = true;
    }

    private void GroupActionConnector_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string groupId } element)
            return;
        _linkChoiceId = null;
        _linkBranch = null;
        _linkGroupActionId = groupId;
        var start = element.TranslatePoint(
            new Point(element.ActualWidth / 2, element.ActualHeight / 2),
            GraphCanvas);
        _linkPreviewLine = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = start.X,
            Y2 = start.Y,
            Stroke = new SolidColorBrush(Color.FromRgb(214, 116, 86)),
            StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        };
        GraphCanvas.Children.Add(_linkPreviewLine);
        Mouse.Capture(GraphCanvas);
        e.Handled = true;
    }

    private void GroupActionConnector_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string groupId } || _workbook is null)
            return;
        var group = _workbook.Groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));
        if (group is null)
            return;

        var menu = new ContextMenu();
        var clear = new MenuItem { Header = "next_action 연결 해제" };
        clear.Click += (_, _) =>
        {
            PushUndo();
            group.NextAction = "choice";
            group.StageId = "";
            _workbook.Layouts.Remove(BattleLayoutKey(group.Id));
            _workbook.Layouts.Remove(ExitLayoutKey(group.Id));
            _workbook.Layouts.Remove(GroupRewardLayoutKey(group.Id));
            _selectedGroup = group;
            BuildGroupInspector(group);
            DrawGraph();
            RefreshIssues();
        };
        menu.Items.Add(clear);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Connector_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || _workbook is null)
            return;
        var parts = tag.Split('|');
        if (parts.Length != 2)
            return;
        var choice = _workbook.Choices.FirstOrDefault(c => c.Id == parts[0]);
        if (choice is null)
            return;

        var menu = new ContextMenu();
        var clear = new MenuItem { Header = "연결 해제" };
        clear.Click += (_, _) =>
        {
            PushUndo();
            if (parts[1] == "success")
            {
                choice.SuccessNextGroupId = "";
                _workbook.Layouts.Remove(ChoiceExitLayoutKey(choice.Id, "success"));
            }
            else
            {
                choice.FailNextGroupId = "";
                _workbook.Layouts.Remove(ChoiceExitLayoutKey(choice.Id, "fail"));
            }
            BuildChoiceInspector(choice);
            DrawGraph();
            RefreshIssues();
        };
        menu.Items.Add(clear);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void GraphCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Source == GraphCanvas)
        {
            _isBoxSelecting = true;
            _boxSelectStart = e.GetPosition(GraphCanvas);
            _boxSelectVisual = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(88, 148, 255)),
                StrokeThickness = 1.4,
                Fill = new SolidColorBrush(Color.FromArgb(42, 88, 148, 255)),
                IsHitTestVisible = false
            };
            GraphCanvas.Children.Add(_boxSelectVisual);
            Canvas.SetZIndex(_boxSelectVisual, 1000);
            Mouse.Capture(GraphCanvas);
            e.Handled = true;
        }
    }

    private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            var point = e.GetPosition(GraphScroll);
            if (Math.Abs(point.X - _panStart.X) > 4 || Math.Abs(point.Y - _panStart.Y) > 4)
                _rightDragMoved = true;
            if (_rightDragMoved)
            {
                GraphScroll.ScrollToHorizontalOffset(_panStartHorizontal - (point.X - _panStart.X));
                GraphScroll.ScrollToVerticalOffset(_panStartVertical - (point.Y - _panStart.Y));
            }
            return;
        }

        if (_linkPreviewLine is not null)
        {
            var linkMouse = e.GetPosition(GraphCanvas);
            _linkPreviewLine.X2 = linkMouse.X;
            _linkPreviewLine.Y2 = linkMouse.Y;
            return;
        }

        if (_isBoxSelecting && _boxSelectVisual is not null)
        {
            var point = e.GetPosition(GraphCanvas);
            var rect = RectFromPoints(_boxSelectStart, point);
            Canvas.SetLeft(_boxSelectVisual, rect.Left);
            Canvas.SetTop(_boxSelectVisual, rect.Top);
            _boxSelectVisual.Width = rect.Width;
            _boxSelectVisual.Height = rect.Height;
            return;
        }

        if (_dragLayoutKey is null || _workbook is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var mouse = e.GetPosition(GraphCanvas);
        if (_isDraggingNodeSelection)
        {
            var delta = mouse - _multiDragStartMouse;
            if (!_dragUndoPushed && (Math.Abs(delta.X) > 0.1 || Math.Abs(delta.Y) > 0.1))
            {
                PushUndo();
                _dragUndoPushed = true;
            }
            foreach (var (key, start) in _multiDragStartPositions)
            {
                if (_workbook.Layouts.TryGetValue(key, out var selectedLayout))
                {
                    selectedLayout.X = RoundCanvasCoord(start.X + delta.X);
                    selectedLayout.Y = RoundCanvasCoord(start.Y + delta.Y);
                }
            }
            DrawGraph();
        }
        else if (_workbook.Layouts.TryGetValue(_dragLayoutKey, out var layout))
        {
            var newX = RoundCanvasCoord(mouse.X - _dragOffset.X);
            var newY = RoundCanvasCoord(mouse.Y - _dragOffset.Y);
            if (!_dragUndoPushed && (Math.Abs(newX - layout.X) > 0.1 || Math.Abs(newY - layout.Y) > 0.1))
            {
                PushUndo();
                _dragUndoPushed = true;
            }
            layout.X = newX;
            layout.Y = newY;
            DrawGraph();
        }
    }

    private void GraphCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_linkPreviewLine is not null)
        {
            FinishLinkDrag(e.GetPosition(GraphCanvas));
            return;
        }

        if (_isBoxSelecting)
        {
            FinishBoxSelection(e.GetPosition(GraphCanvas));
            return;
        }

        _dragLayoutKey = null;
        _isDraggingNodeSelection = false;
        _dragUndoPushed = false;
        Mouse.Capture(null);
    }

    private static Rect RectFromPoints(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void FinishBoxSelection(Point end)
    {
        if (_boxSelectVisual is not null)
            GraphCanvas.Children.Remove(_boxSelectVisual);
        _boxSelectVisual = null;
        _isBoxSelecting = false;
        Mouse.Capture(null);

        var rect = RectFromPoints(_boxSelectStart, end);
        _selectedNodeKeys.Clear();
        if (rect.Width < 6 && rect.Height < 6)
        {
            _selectedGroup = null;
            _selectedChoice = null;
            _selectedObjectKey = null;
            BuildEventInspector();
            DrawGraph();
            return;
        }

        foreach (var key in CurrentEventLayoutKeys())
        {
            if (!_workbook!.Layouts.TryGetValue(key, out var layout))
                continue;
            var nodeRect = new Rect(layout.X, layout.Y, Math.Max(layout.Width, 24), Math.Max(layout.Height, 24));
            if (rect.IntersectsWith(nodeRect))
                _selectedNodeKeys.Add(key);
        }

        _selectedGroup = null;
        _selectedChoice = null;
        _selectedObjectKey = null;
        DrawGraph();
    }

    private IEnumerable<string> CurrentEventLayoutKeys()
    {
        if (_workbook is null || _selectedEvent is null)
            yield break;

        var groupIds = _workbook.Groups
            .Where(g => g.EventId == _selectedEvent.Id)
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choiceIds = _workbook.Choices
            .Where(c => groupIds.Contains(c.GroupId))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var groupId in groupIds)
            yield return groupId;
        foreach (var key in _workbook.Layouts.Keys)
        {
            if (key.StartsWith("battle|", StringComparison.OrdinalIgnoreCase)
                && groupIds.Contains(key.Split('|').ElementAtOrDefault(1) ?? ""))
                yield return key;
            else if (key.StartsWith("exit|", StringComparison.OrdinalIgnoreCase)
                     && groupIds.Contains(key.Split('|').ElementAtOrDefault(1) ?? ""))
                yield return key;
            else if (key.StartsWith(GroupRewardPrefix, StringComparison.OrdinalIgnoreCase)
                     && groupIds.Contains(key.Split('|').ElementAtOrDefault(1) ?? ""))
                yield return key;
            else if (key.StartsWith("reward|", StringComparison.OrdinalIgnoreCase)
                     && choiceIds.Contains(key.Split('|').ElementAtOrDefault(1) ?? ""))
                yield return key;
            else if (key.StartsWith(ChoiceExitPrefix, StringComparison.OrdinalIgnoreCase)
                     && choiceIds.Contains(key.Split('|').ElementAtOrDefault(1) ?? ""))
                yield return key;
            else if ((key.StartsWith(PendingRewardPrefix, StringComparison.OrdinalIgnoreCase)
                      || key.StartsWith(PendingBattlePrefix, StringComparison.OrdinalIgnoreCase)
                      || key.StartsWith(PendingExitPrefix, StringComparison.OrdinalIgnoreCase))
                     && string.Equals(_workbook.Layouts[key].EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase))
                yield return key;
        }
    }

    private void GraphCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStart = e.GetPosition(GraphScroll);
        _rightDownGraphPoint = e.GetPosition(GraphCanvas);
        _panStartHorizontal = GraphScroll.HorizontalOffset;
        _panStartVertical = GraphScroll.VerticalOffset;
        _rightDragMoved = false;
        _rightDownOnCanvas = e.OriginalSource == GraphCanvas;
        Mouse.Capture(GraphCanvas);
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void GraphCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var showContextMenu = _rightDownOnCanvas && !_rightDragMoved;
        _isPanning = false;
        Mouse.Capture(null);
        Cursor = Cursors.Arrow;
        if (showContextMenu)
            ShowGraphContextMenu(_rightDownGraphPoint);
        e.Handled = true;
    }

    private void ShowGraphContextMenu(Point canvasPoint)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = GraphCanvas,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };
        var addGroup = new MenuItem { Header = "장면 노드 추가" };
        addGroup.Click += (_, _) => AddGroupAt(canvasPoint);
        menu.Items.Add(addGroup);

        menu.Items.Add(new Separator());
        var addReward = new MenuItem { Header = "보상 노드 추가" };
        addReward.Click += (_, _) => AddRewardNodeFromMenu(canvasPoint);
        menu.Items.Add(addReward);

        var addBattle = new MenuItem { Header = "전투 노드 추가" };
        addBattle.Click += (_, _) => AddBattleNodeFromMenu(canvasPoint);
        menu.Items.Add(addBattle);
        var addExit = new MenuItem { Header = "Exit node add" };
        addExit.Click += (_, _) => AddExitNodeFromMenu(canvasPoint);
        menu.Items.Add(addExit);
        menu.IsOpen = true;
    }

    private void AddRewardNodeFromMenu(Point canvasPoint)
    {
        if (_workbook is null || _selectedEvent is null)
            return;

        PushUndo();
        var key = PendingRewardLayoutKey(_selectedEvent.Id, "terminal");
        _workbook.Layouts[key] = new NodeLayout
        {
            EventId = _selectedEvent.Id,
            GroupId = key,
            X = RoundCanvasCoord(canvasPoint.X),
            Y = RoundCanvasCoord(canvasPoint.Y),
            Width = RewardNodeWidth,
            Height = RewardNodeHeight
        };
        _selectedObjectKey = key;
        DrawGraph();
        RefreshIssues();
    }

    private void AddBattleNodeFromMenu(Point canvasPoint)
    {
        if (_workbook is null || _selectedEvent is null)
            return;

        PushUndo();
        var key = PendingBattleLayoutKey(_selectedEvent.Id);
        _workbook.Layouts[key] = new NodeLayout
        {
            EventId = _selectedEvent.Id,
            GroupId = key,
            X = RoundCanvasCoord(canvasPoint.X),
            Y = RoundCanvasCoord(canvasPoint.Y),
            Width = BattleNodeWidth,
            Height = BattleNodeHeight
        };
        _selectedObjectKey = key;
        DrawGraph();
        RefreshIssues();
    }

    private void AddExitNodeFromMenu(Point canvasPoint)
    {
        if (_workbook is null || _selectedEvent is null)
            return;

        PushUndo();
        var key = PendingExitLayoutKey(_selectedEvent.Id);
        _workbook.Layouts[key] = new NodeLayout
        {
            EventId = _selectedEvent.Id,
            GroupId = key,
            X = RoundCanvasCoord(canvasPoint.X),
            Y = RoundCanvasCoord(canvasPoint.Y),
            Width = ExitNodeWidth,
            Height = ExitNodeHeight
        };
        _selectedObjectKey = key;

        DrawGraph();
        RefreshIssues();
    }

    private void GraphCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var oldZoom = _zoom;
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(_zoom - oldZoom) < 0.001)
            return;

        var cursor = e.GetPosition(GraphScroll);
        var graphPoint = e.GetPosition(GraphCanvas);
        _graphScale.ScaleX = _zoom;
        _graphScale.ScaleY = _zoom;
        GraphCanvas.UpdateLayout();

        GraphScroll.ScrollToHorizontalOffset(graphPoint.X * _zoom - cursor.X);
        GraphScroll.ScrollToVerticalOffset(graphPoint.Y * _zoom - cursor.Y);
        e.Handled = true;
    }

    private void FinishLinkDrag(Point point)
    {
        if (_workbook is not null && _linkGroupActionId is not null)
        {
            FinishGroupActionLink(point);
        }
        else if (_workbook is not null && _linkChoiceId is not null && _linkBranch is not null)
        {
            var choice = _workbook.Choices.FirstOrDefault(c => c.Id == _linkChoiceId);
            var pendingRewardKey = FindPendingRewardAt(point);
            var pendingBattleKey = FindPendingBattleAt(point);
            var pendingExitKey = FindPendingExitAt(point);
            var existingBattleKey = FindExistingBattleAt(point);
            var existingExitKey = FindExistingExitAt(point);
            var existingRewardKey = FindExistingGroupRewardAt(point);
            var targetGroupId = FindGroupAt(point);
            if (targetGroupId is not null && choice is not null)
            {
                PushUndo();
                if (_linkBranch == "success")
                {
                    choice.SuccessNextGroupId = targetGroupId;
                    _workbook.Layouts.Remove(ChoiceExitLayoutKey(choice.Id, "success"));
                }
                else
                {
                    choice.FailNextGroupId = targetGroupId;
                    _workbook.Layouts.Remove(ChoiceExitLayoutKey(choice.Id, "fail"));
                }
                BuildChoiceInspector(choice);
            }
            else if (choice is not null
                     && (pendingRewardKey is not null
                         || pendingBattleKey is not null
                         || pendingExitKey is not null
                         || existingRewardKey is not null
                         || existingBattleKey is not null
                         || existingExitKey is not null))
            {
                MessageBox.Show(this, "선택지 T/F 핀은 장면 노드에만 연결합니다. Battle, Exit, Reward는 장면 노드의 출력 핀에서 연결하세요.", "연결 불가한 노드입니다", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (choice is not null)
            {
                MessageBox.Show(this, "장면 노드의 입력 핀에 연결해야 합니다.", "연결 불가한 노드입니다", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        if (_linkPreviewLine is not null)
            GraphCanvas.Children.Remove(_linkPreviewLine);
        _linkPreviewLine = null;
        _linkGroupActionId = null;
        _linkChoiceId = null;
        _linkBranch = null;
        Mouse.Capture(null);
        DrawGraph();
        RefreshIssues();
    }

    private void FinishGroupActionLink(Point point)
    {
        if (_workbook is null || _linkGroupActionId is null)
            return;
        var group = _workbook.Groups.FirstOrDefault(g => string.Equals(g.Id, _linkGroupActionId, StringComparison.OrdinalIgnoreCase));
        if (group is null)
            return;

        var pendingRewardKey = FindPendingRewardAt(point);
        var pendingBattleKey = FindPendingBattleAt(point);
        var pendingExitKey = FindPendingExitAt(point);
        var existingRewardKey = FindExistingGroupRewardAt(point);
        var existingBattleKey = FindExistingBattleAt(point);
        var existingExitKey = FindExistingExitAt(point);

        if (pendingBattleKey is not null || existingBattleKey is not null)
        {
            PushUndo();
            AttachBattleToGroup(group, pendingBattleKey ?? existingBattleKey!);
            BuildGroupInspector(group);
        }
        else if (pendingRewardKey is not null || existingRewardKey is not null)
        {
            PushUndo();
            AttachRewardToGroup(group, pendingRewardKey ?? existingRewardKey!);
            BuildGroupInspector(group);
        }
        else if (pendingExitKey is not null || existingExitKey is not null)
        {
            PushUndo();
            AttachExitToGroup(group, pendingExitKey ?? existingExitKey!);
            BuildGroupInspector(group);
        }
        else
        {
            MessageBox.Show(this, "장면 노드의 출력 핀은 Battle, Exit, Reward 노드에 연결할 수 있습니다.", "연결 불가한 노드입니다", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AttachPendingReward(EventChoiceRow choice, string branch, string pendingKey)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(pendingKey, out var pending))
            return;

        if (branch == "success")
        {
            choice.SuccessRewardType = choice.SuccessRewardType == "none" ? "gold" : choice.SuccessRewardType;
            choice.SuccessRewardAmount ??= 1;
        }
        else
        {
            choice.FailRewardType = choice.FailRewardType == "none" ? "gold" : choice.FailRewardType;
            choice.FailRewardAmount ??= 1;
        }

        var key = RewardLayoutKey(choice.Id, branch);
        _workbook.Layouts.Remove(pendingKey);
        _workbook.Layouts[key] = new NodeLayout
        {
            EventId = pending.EventId,
            GroupId = key,
            X = RoundCanvasCoord(pending.X),
            Y = RoundCanvasCoord(pending.Y),
            Width = RewardNodeWidth,
            Height = RewardNodeHeight
        };
        _selectedObjectKey = key;
    }

    private void AttachPendingChoiceExit(EventChoiceRow choice, string branch, string pendingKey)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(pendingKey, out var pending))
            return;

        if (branch == "success")
            choice.SuccessNextGroupId = "";
        else
            choice.FailNextGroupId = "";

        var key = ChoiceExitLayoutKey(choice.Id, branch);
        _workbook.Layouts.Remove(pendingKey);
        _workbook.Layouts[key] = new NodeLayout
        {
            EventId = pending.EventId,
            GroupId = key,
            X = RoundCanvasCoord(pending.X),
            Y = RoundCanvasCoord(pending.Y),
            Width = ExitNodeWidth,
            Height = ExitNodeHeight
        };
        _selectedObjectKey = key;
    }

    private void AttachChoiceToExistingExit(EventChoiceRow choice, string branch, string exitKey)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(exitKey, out var exitLayout))
            return;

        if (branch == "success")
            choice.SuccessNextGroupId = "";
        else
            choice.FailNextGroupId = "";

        var key = ChoiceExitLayoutKey(choice.Id, branch);
        _workbook.Layouts[key] = new NodeLayout
        {
            EventId = exitLayout.EventId,
            GroupId = key,
            X = RoundCanvasCoord(exitLayout.X),
            Y = RoundCanvasCoord(exitLayout.Y),
            Width = ExitNodeWidth,
            Height = ExitNodeHeight
        };
        _selectedObjectKey = key;
    }

    private void AttachPendingChoiceBattle(EventChoiceRow choice, string branch, string pendingKey)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(pendingKey, out var pending))
            return;

        var sourceGroup = _workbook.Groups.FirstOrDefault(g => string.Equals(g.Id, choice.GroupId, StringComparison.OrdinalIgnoreCase));
        if (sourceGroup is null)
            return;

        var id = NextGroupId(sourceGroup.EventId);
        var battleGroup = new ChoiceGroupRow
        {
            Id = id,
            EventId = sourceGroup.EventId,
            Memo = "전투가 시작된다.",
            ExportId = EventWorkbookService.DefaultEventExportId,
            Background = sourceGroup.Background,
            NpcId = sourceGroup.NpcId,
            SituationTextTid = $"{id}_situation_text",
            NextAction = "battle",
            StageId = DefaultBattleStageId
        };
        _workbook.Groups.Add(battleGroup);
        SetChoiceBranchNextGroup(choice, branch, battleGroup.Id);

        _workbook.Layouts.Remove(pendingKey);
        _workbook.Layouts[battleGroup.Id] = new NodeLayout
        {
            EventId = battleGroup.EventId,
            GroupId = battleGroup.Id,
            X = RoundCanvasCoord(pending.X),
            Y = RoundCanvasCoord(pending.Y),
            Width = NodeWidth,
            Height = NodeMinHeight
        };
        var battleKey = BattleLayoutKey(battleGroup.Id);
        _workbook.Layouts[battleKey] = new NodeLayout
        {
            EventId = battleGroup.EventId,
            GroupId = battleKey,
            X = RoundCanvasCoord(pending.X),
            Y = RoundCanvasCoord(pending.Y),
            Width = BattleNodeWidth,
            Height = BattleNodeHeight
        };
        _selectedObjectKey = battleKey;
        _selectedGroup = battleGroup;
        _selectedChoice = null;
    }

    private void AttachChoiceToExistingBattle(EventChoiceRow choice, string branch, string battleKey)
    {
        if (_workbook is null)
            return;

        var groupId = battleKey.Split('|').ElementAtOrDefault(1) ?? "";
        var group = _workbook.Groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));
        if (group is null || !IsBattleGroup(group))
            return;

        SetChoiceBranchNextGroup(choice, branch, group.Id);
        _selectedObjectKey = BattleLayoutKey(group.Id);
        _selectedGroup = group;
        _selectedChoice = null;
    }

    private void SetChoiceBranchNextGroup(EventChoiceRow choice, string branch, string nextGroupId)
    {
        if (_workbook is null)
            return;

        if (branch == "success")
        {
            choice.SuccessNextGroupId = nextGroupId;
            _workbook.Layouts.Remove(ChoiceExitLayoutKey(choice.Id, "success"));
        }
        else
        {
            choice.FailNextGroupId = nextGroupId;
            _workbook.Layouts.Remove(ChoiceExitLayoutKey(choice.Id, "fail"));
        }
    }

    private void AttachBattleToGroup(ChoiceGroupRow group, string sourceKey)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(sourceKey, out var sourceLayout))
            return;

        group.NextAction = "battle";
        if (string.IsNullOrWhiteSpace(group.StageId))
            group.StageId = DefaultBattleStageId;
        _workbook.Layouts.Remove(sourceKey);
        _workbook.Layouts.Remove(ExitLayoutKey(group.Id));
        _workbook.Layouts.Remove(GroupRewardLayoutKey(group.Id));
        var key = BattleLayoutKey(group.Id);
        _workbook.Layouts[key] = new NodeLayout
        {
            EventId = group.EventId,
            GroupId = key,
            X = RoundCanvasCoord(sourceLayout.X),
            Y = RoundCanvasCoord(sourceLayout.Y),
            Width = BattleNodeWidth,
            Height = BattleNodeHeight
        };
        _selectedObjectKey = key;
        _selectedGroup = group;
        _selectedChoice = null;
    }

    private void AttachRewardToGroup(ChoiceGroupRow group, string sourceKey)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(sourceKey, out var sourceLayout))
            return;

        group.NextAction = "exit";
        group.StageId = "";
        _workbook.Layouts.Remove(sourceKey);
        _workbook.Layouts.Remove(BattleLayoutKey(group.Id));
        _workbook.Layouts.Remove(ExitLayoutKey(group.Id));
        var key = GroupRewardLayoutKey(group.Id);
        _workbook.Layouts[key] = new NodeLayout
        {
            EventId = group.EventId,
            GroupId = key,
            X = RoundCanvasCoord(sourceLayout.X),
            Y = RoundCanvasCoord(sourceLayout.Y),
            Width = RewardNodeWidth,
            Height = RewardNodeHeight
        };
        _selectedObjectKey = key;
        _selectedGroup = group;
        _selectedChoice = null;
    }

    private void AttachExitToGroup(ChoiceGroupRow group, string sourceKey)
    {
        if (_workbook is null || !_workbook.Layouts.TryGetValue(sourceKey, out var sourceLayout))
            return;

        group.NextAction = "exit";
        group.StageId = "";
        _workbook.Layouts.Remove(sourceKey);
        _workbook.Layouts.Remove(BattleLayoutKey(group.Id));
        _workbook.Layouts.Remove(GroupRewardLayoutKey(group.Id));
        var key = ExitLayoutKey(group.Id);
        _workbook.Layouts[key] = new NodeLayout
        {
            EventId = group.EventId,
            GroupId = key,
            X = RoundCanvasCoord(sourceLayout.X),
            Y = RoundCanvasCoord(sourceLayout.Y),
            Width = ExitNodeWidth,
            Height = ExitNodeHeight
        };
        _selectedObjectKey = key;
        _selectedGroup = group;
        _selectedChoice = null;
    }

    private string? FindGroupAt(Point point)
    {
        if (_workbook is null || _selectedEvent is null)
            return null;
        var groups = _workbook.Groups.Where(g => g.EventId == _selectedEvent.Id).ToList();
        foreach (var group in groups)
        {
            var pin = GetInputPinPoint(group.Id);
            if ((pin - point).Length <= 34)
                return group.Id;
        }

        foreach (var group in groups)
        {
            if (!_workbook.Layouts.TryGetValue(group.Id, out var layout))
                continue;
            var rect = new Rect(layout.X, layout.Y, layout.Width, Math.Max(layout.Height, 160));
            if (rect.Contains(point))
                return group.Id;
        }
        return null;
    }

    private string? FindExistingExitAt(Point point)
    {
        if (_workbook is null || _selectedEvent is null)
            return null;
        foreach (var pair in _workbook.Layouts.Where(p =>
                     p.Key.StartsWith("exit|", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(p.Value.EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var rect = new Rect(pair.Value.X, pair.Value.Y, pair.Value.Width, pair.Value.Height);
            var pin = new Point(rect.Left, rect.Top + rect.Height / 2);
            if ((pin - point).Length <= 34 || rect.Contains(point))
                return pair.Key;
        }
        return null;
    }

    private string? FindExistingBattleAt(Point point)
    {
        if (_workbook is null || _selectedEvent is null)
            return null;
        foreach (var pair in _workbook.Layouts.Where(p =>
                     p.Key.StartsWith("battle|", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(p.Value.EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var rect = new Rect(pair.Value.X, pair.Value.Y, pair.Value.Width, pair.Value.Height);
            var pin = new Point(rect.Left, rect.Top + rect.Height / 2);
            if ((pin - point).Length <= 34 || rect.Contains(point))
                return pair.Key;
        }
        return null;
    }

    private string? FindExistingGroupRewardAt(Point point)
    {
        if (_workbook is null || _selectedEvent is null)
            return null;
        foreach (var pair in _workbook.Layouts.Where(p =>
                     p.Key.StartsWith(GroupRewardPrefix, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(p.Value.EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var rect = new Rect(pair.Value.X, pair.Value.Y, pair.Value.Width, pair.Value.Height);
            var pin = new Point(rect.Left, rect.Top + rect.Height / 2);
            if ((pin - point).Length <= 34 || rect.Contains(point))
                return pair.Key;
        }
        return null;
    }

    private string? FindPendingRewardAt(Point point)
    {
        if (_workbook is null || _selectedEvent is null)
            return null;
        foreach (var pair in _workbook.Layouts.Where(p =>
                     p.Key.StartsWith(PendingRewardPrefix, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(p.Value.EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var pin = new Point(pair.Value.X, pair.Value.Y + pair.Value.Height / 2);
            if ((pin - point).Length <= 34)
                return pair.Key;
        }
        return null;
    }

    private string? FindPendingBattleAt(Point point)
    {
        if (_workbook is null || _selectedEvent is null)
            return null;
        foreach (var pair in _workbook.Layouts.Where(p =>
                     p.Key.StartsWith(PendingBattlePrefix, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(p.Value.EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var pin = new Point(pair.Value.X, pair.Value.Y + pair.Value.Height / 2);
            if ((pin - point).Length <= 34)
                return pair.Key;
        }
        return null;
    }

    private string? FindPendingExitAt(Point point)
    {
        if (_workbook is null || _selectedEvent is null)
            return null;
        foreach (var pair in _workbook.Layouts.Where(p =>
                     p.Key.StartsWith(PendingExitPrefix, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(p.Value.EventId, _selectedEvent.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var pin = new Point(pair.Value.X, pair.Value.Y + pair.Value.Height / 2);
            var rect = new Rect(pair.Value.X, pair.Value.Y, pair.Value.Width, pair.Value.Height);
            if ((pin - point).Length <= 34 || rect.Contains(point))
                return pair.Key;
        }
        return null;
    }

    private void HierarchyTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_workbook is null || e.NewValue is not TreeViewItem { Tag: string tag })
            return;
        var parts = tag.Split('|');
        if (parts.Length != 2)
            return;
        if (parts[0] == "group")
        {
            var group = _workbook.Groups.FirstOrDefault(g => g.Id == parts[1]);
            if (group is not null)
            {
                _selectedObjectKey = null;
                BuildGroupInspector(group);
                DrawGraph();
            }
        }
        else if (parts[0] == "choice")
        {
            var choice = _workbook.Choices.FirstOrDefault(c => c.Id == parts[1]);
            if (choice is not null)
            {
                _selectedObjectKey = null;
                BuildChoiceInspector(choice);
                DrawGraph();
            }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_runtimeLocked)
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
            return;

        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        if (e.Key == Key.C)
        {
            CopySelectedNode();
            e.Handled = true;
        }
        else if (e.Key == Key.V)
        {
            PasteCopiedNode();
            e.Handled = true;
        }
        else if (e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
    }

    private void LayoutSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SaveLayoutCache();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (IsPlayerRunning())
            StopPlayer();
        SaveLayoutCache();
    }
}
