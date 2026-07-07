using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DimensionEventEditor;

public sealed class PreviewWindow : Window
{
    private readonly Func<IReadOnlyList<DiffEntry>> _loadDiff;
    private readonly Action<IReadOnlyList<DiffEntry>> _revert;
    private readonly Func<string, bool, EventGraphPreview?> _loadGraphPreview;
    private readonly TextBlock _title;
    private readonly ListBox _list;
    private readonly ScrollViewer _beforePreview;
    private readonly ScrollViewer _afterPreview;
    private readonly TextBox _beforeDiff;
    private readonly TextBox _afterDiff;
    private readonly Button _revertButton;
    private readonly TextBox _exportIdBox;
    private readonly Dictionary<ScrollViewer, ScaleTransform> _previewScales = [];
    private readonly Dictionary<ScrollViewer, Point> _previewPanStarts = [];
    private readonly Dictionary<ScrollViewer, Point> _previewPanOffsets = [];
    private const double PreviewMinZoom = 0.35;
    private const double PreviewMaxZoom = 2.5;

    private sealed class DiffGroup
    {
        public string Key { get; init; } = "";
        public List<DiffEntry> Entries { get; init; } = [];
        public bool CanRevert => Entries.Any(e => e.CanRevert);
        public override string ToString() => $"{Key}  ({Entries.Count} changes)";
    }

    public PreviewWindow(Func<IReadOnlyList<DiffEntry>> loadDiff, Action<IReadOnlyList<DiffEntry>> revert, Func<string, bool, EventGraphPreview?> loadGraphPreview, string exportId)
    {
        _loadDiff = loadDiff;
        _revert = revert;
        _loadGraphPreview = loadGraphPreview;

        Title = "Export Preview";
        Width = 1480;
        Height = 900;
        Background = new SolidColorBrush(Color.FromRgb(27, 27, 27));
        Foreground = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PreviewKeyDown += PreviewWindow_PreviewKeyDown;

        var root = new DockPanel { Margin = new Thickness(14) };
        _title = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(_title, Dock.Top);
        root.Children.Add(_title);

        var exportPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        exportPanel.Children.Add(new TextBlock
        {
            Text = "Export ID",
            Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 190)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _exportIdBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(exportId) ? EventWorkbookService.DefaultEventExportId : exportId,
            Width = 260,
            Height = 28,
            Background = new SolidColorBrush(Color.FromRgb(43, 43, 43)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85))
        };
        exportPanel.Children.Add(_exportIdBox);
        DockPanel.SetDock(exportPanel, Dock.Top);
        root.Children.Add(exportPanel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _revertButton = new Button { Content = "Revert Event", Width = 130, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var apply = new Button { Content = "Apply Checked", Width = 120, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 96, Height = 34 };
        _revertButton.Click += (_, _) => RevertSelected();
        apply.Click += (_, _) => { DialogResult = true; Close(); };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(_revertButton);
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _list = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(31, 31, 31)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(17, 17, 17)),
            Margin = new Thickness(0, 0, 12, 0),
            SelectionMode = SelectionMode.Extended
        };
        _list.SelectionChanged += (_, _) => ShowSelected();
        Grid.SetColumn(_list, 0);
        body.Children.Add(_list);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var previewGrid = TwoColumnSection("Before Node Preview", "After Node Preview");
        _beforePreview = PreviewBox();
        _afterPreview = PreviewBox();
        AddBoxes(previewGrid, _beforePreview, _afterPreview);
        Grid.SetRow(previewGrid, 0);
        right.Children.Add(previewGrid);

        var splitter = new GridSplitter
        {
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
            ShowsPreview = true
        };
        Grid.SetRow(splitter, 1);
        right.Children.Add(splitter);

        var diffGrid = TwoColumnSection("Before Diff", "After Diff");
        _beforeDiff = DiffBox();
        _afterDiff = DiffBox();
        AddBoxes(diffGrid, _beforeDiff, _afterDiff);
        Grid.SetRow(diffGrid, 2);
        right.Children.Add(diffGrid);

        root.Children.Add(body);
        Content = root;

        ReloadDiff();
    }

    private void PreviewWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        DialogResult = false;
        Close();
    }

    public IReadOnlyList<DiffEntry> UncheckedEntries => _list.Items
        .OfType<ListBoxItem>()
        .Where(i => i.Tag is DiffGroup && FindCheckBox(i)?.IsChecked != true)
        .SelectMany(i => ((DiffGroup)i.Tag).Entries)
        .ToList();

    public IReadOnlyList<DiffEntry> CheckedEntries => _list.Items
        .OfType<ListBoxItem>()
        .Where(i => i.Tag is DiffGroup && FindCheckBox(i)?.IsChecked == true)
        .SelectMany(i => ((DiffGroup)i.Tag).Entries)
        .ToList();

    public string ExportId => _exportIdBox.Text.Trim();

    private static Grid TwoColumnSection(string leftHeader, string rightHeader)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Header(leftHeader, 0));
        grid.Children.Add(Header(rightHeader, 1));
        return grid;
    }

    private static void AddBoxes(Grid grid, UIElement left, UIElement right)
    {
        Grid.SetColumn(left, 0);
        Grid.SetRow(left, 1);
        Grid.SetColumn(right, 1);
        Grid.SetRow(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
    }

    private static TextBlock Header(string text, int column)
    {
        var header = new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(222, 222, 222)),
            Margin = new Thickness(0, 0, 8, 8)
        };
        Grid.SetColumn(header, column);
        return header;
    }

    private static TextBox DiffBox() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = new SolidColorBrush(Color.FromRgb(31, 31, 31)),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(17, 17, 17)),
        Margin = new Thickness(0, 0, 8, 0),
        Padding = new Thickness(10)
    };

    private ScrollViewer PreviewBox()
    {
        var viewer = new ScrollViewer
        {
            Background = new SolidColorBrush(Color.FromRgb(31, 31, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(17, 17, 17)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        viewer.PreviewMouseWheel += PreviewBox_MouseWheel;
        viewer.PreviewMouseRightButtonDown += PreviewBox_MouseRightButtonDown;
        viewer.PreviewMouseRightButtonUp += PreviewBox_MouseRightButtonUp;
        viewer.PreviewMouseMove += PreviewBox_MouseMove;
        return viewer;
    }

    private void ReloadDiff()
    {
        var selectedKey = SelectedGroup()?.Key;
        var groups = _loadDiff()
            .GroupBy(EventGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DiffGroup
            {
                Key = g.Key,
                Entries = g.OrderBy(DiffPriority).ThenBy(d => d.Sheet).ThenBy(d => d.Key).ToList()
            })
            .OrderBy(g => GroupPriority(g.Key))
            .ThenBy(g => g.Key)
            .ToList();

        _title.Text = $"변경 내역 {groups.Sum(g => g.Entries.Count)}건 / 이벤트 묶음 {groups.Count}개";
        _list.Items.Clear();
        foreach (var group in groups)
            _list.Items.Add(CreateGroupItem(group));

        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            foreach (var item in _list.Items.OfType<ListBoxItem>())
            {
                if (item.Tag is DiffGroup group && string.Equals(group.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    _list.SelectedItem = item;
                    break;
                }
            }
        }

        if (_list.SelectedItem is null && _list.Items.Count > 0)
            _list.SelectedIndex = 0;
        ShowSelected();
    }

    private static string EventGroupKey(DiffEntry entry)
    {
        var text = $"{entry.Key} {entry.Detail} {entry.BeforeValue} {entry.AfterValue}";
        var match = Regex.Match(text, @"s1_EVT_\d{3}", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Value;
        if (entry.Sheet == EventWorkbookService.LayoutSheetName)
            return "Layout";
        if (entry.Sheet == EventWorkbookService.TextSheetName)
            return "Text";
        return "Other";
    }

    private static int GroupPriority(string key)
    {
        if (key == "Text") return 80;
        if (key == "Layout") return 90;
        if (key == "Other") return 99;
        return 10;
    }

    private static int DiffPriority(DiffEntry entry)
    {
        if (entry.Sheet == EventWorkbookService.LayoutSheetName)
            return 90;
        if (entry.Sheet == EventWorkbookService.TextSheetName)
            return 50;
        return 10;
    }

    private ListBoxItem CreateGroupItem(DiffGroup group)
    {
        var check = new CheckBox
        {
            Tag = group,
            IsChecked = true,
            IsEnabled = group.CanRevert,
            Focusable = false,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        check.PreviewMouseLeftButtonDown += DiffCheckBox_PreviewMouseLeftButtonDown;

        var summary = string.Join(" / ", group.Entries
            .GroupBy(e => e.Sheet)
            .Select(g => $"{ShortSheet(g.Key)} {g.Count()}")
            .Take(4));
        var text = new StackPanel { Orientation = Orientation.Vertical };
        var title = GroupTitle(group);
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = summary,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(4, 4, 4, 4) };
        DockPanel.SetDock(check, Dock.Left);
        panel.Children.Add(check);
        panel.Children.Add(text);

        return new ListBoxItem
        {
            Content = panel,
            Tag = group,
            Padding = new Thickness(0)
        };
    }

    private string GroupTitle(DiffGroup group)
    {
        var graph = _loadGraphPreview(group.Key, false) ?? _loadGraphPreview(group.Key, true);
        if (graph is null || string.IsNullOrWhiteSpace(graph.EventName))
            return group.Key;
        return $"{graph.EventId} / {graph.EventName}";
    }

    private static string ShortSheet(string sheet) => sheet switch
    {
        EventWorkbookService.BaseSheetName => "base",
        EventWorkbookService.GroupSheetName => "group",
        EventWorkbookService.ChoiceSheetName => "choice",
        EventWorkbookService.TextSheetName => "text",
        EventWorkbookService.LayoutSheetName => "layout",
        _ => sheet
    };

    private DiffGroup? SelectedGroup()
    {
        if (_list.SelectedItem is ListBoxItem { Tag: DiffGroup group })
            return group;
        return null;
    }

    private void ShowSelected()
    {
        var selected = _list.SelectedItems.OfType<ListBoxItem>().Select(i => i.Tag).OfType<DiffGroup>().ToList();
        var group = selected.FirstOrDefault();
        if (group is null)
        {
            _beforePreview.Content = null;
            _afterPreview.Content = null;
            _beforeDiff.Text = "";
            _afterDiff.Text = "";
            _revertButton.IsEnabled = false;
            return;
        }

        SetPreviewContent(_beforePreview, RenderGraphPreview(group, before: true));
        SetPreviewContent(_afterPreview, RenderGraphPreview(group, before: false));
        _beforeDiff.Text = BuildDiffText(group, before: true);
        _afterDiff.Text = BuildDiffText(group, before: false);
        _revertButton.IsEnabled = selected.Any(g => g.CanRevert);
        _revertButton.Content = selected.Count > 1 ? $"Revert {selected.Count} Events" : "Revert Event";
    }

    private void SetPreviewContent(ScrollViewer viewer, Canvas canvas)
    {
        var scale = new ScaleTransform(1, 1);
        canvas.LayoutTransform = scale;
        viewer.Content = canvas;
        _previewScales[viewer] = scale;
        viewer.ScrollToHorizontalOffset(0);
        viewer.ScrollToVerticalOffset(0);
    }

    private void PreviewBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !_previewScales.TryGetValue(viewer, out var scale))
            return;

        var oldZoom = scale.ScaleX;
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        var newZoom = Math.Clamp(oldZoom * factor, PreviewMinZoom, PreviewMaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.001)
            return;

        var cursor = e.GetPosition(viewer);
        var beforeX = viewer.HorizontalOffset + cursor.X;
        var beforeY = viewer.VerticalOffset + cursor.Y;
        scale.ScaleX = newZoom;
        scale.ScaleY = newZoom;
        if (viewer.Content is FrameworkElement element)
            element.UpdateLayout();
        var ratio = newZoom / oldZoom;
        viewer.ScrollToHorizontalOffset(beforeX * ratio - cursor.X);
        viewer.ScrollToVerticalOffset(beforeY * ratio - cursor.Y);
        e.Handled = true;
    }

    private void PreviewBox_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
            return;
        _previewPanStarts[viewer] = e.GetPosition(viewer);
        _previewPanOffsets[viewer] = new Point(viewer.HorizontalOffset, viewer.VerticalOffset);
        viewer.CaptureMouse();
        viewer.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void PreviewBox_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
            return;
        _previewPanStarts.Remove(viewer);
        _previewPanOffsets.Remove(viewer);
        viewer.ReleaseMouseCapture();
        viewer.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void PreviewBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !_previewPanStarts.TryGetValue(viewer, out var start) || !_previewPanOffsets.TryGetValue(viewer, out var offset))
            return;
        if (e.RightButton != MouseButtonState.Pressed)
            return;
        var current = e.GetPosition(viewer);
        viewer.ScrollToHorizontalOffset(offset.X - (current.X - start.X));
        viewer.ScrollToVerticalOffset(offset.Y - (current.Y - start.Y));
        e.Handled = true;
    }

    private Canvas RenderGraphPreview(DiffGroup group, bool before)
    {
        var graph = _loadGraphPreview(group.Key, before);
        const double headerHeight = 48;
        var canvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromRgb(31, 31, 31)),
            MinWidth = 720,
            MinHeight = 360
        };
        if (graph is null || graph.Nodes.Count == 0)
        {
            canvas.Children.Add(new TextBlock
            {
                Text = "노드 변경 없음",
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                Margin = new Thickness(16)
            });
            return canvas;
        }

        var changedText = string.Join("\n", group.Entries.Select(e => $"{e.Key}\n{e.Detail}\n{e.BeforeValue}\n{e.AfterValue}"));
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        canvas.Width = Math.Max(720, graph.Nodes.Max(n => n.X + n.Width) + 80);
        canvas.Height = Math.Max(360, graph.Nodes.Max(n => n.Y + n.Height) + 80 + headerHeight);

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(graph.EventName)
                ? graph.EventId
                : $"{graph.EventId} / {graph.EventName}",
            Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
            FontSize = 16,
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(title, 16);
        Canvas.SetTop(title, 12);
        canvas.Children.Add(title);

        foreach (var link in graph.Links)
        {
            if (!nodes.TryGetValue(link.FromId, out var from) || !nodes.TryGetValue(link.ToId, out var to))
                continue;
            var line = RoutedLine(from, to, headerHeight);
            line.Stroke = new SolidColorBrush(Color.FromRgb(112, 190, 126));
            line.StrokeThickness = LinkChanged(link, changedText) ? 3.0 : 1.8;
            canvas.Children.Add(line);
            if (!string.IsNullOrWhiteSpace(link.Label))
            {
                var label = new TextBlock
                {
                    Text = link.Label,
                    Foreground = Brushes.LightGreen,
                    FontWeight = FontWeights.Bold
                };
                Canvas.SetLeft(label, (from.X + from.Width + to.X) / 2);
                Canvas.SetTop(label, (from.Y + to.Y) / 2 + headerHeight);
                canvas.Children.Add(label);
            }
        }

        foreach (var node in graph.Nodes)
            canvas.Children.Add(RenderNode(node, changedText, headerHeight));
        return canvas;
    }

    private static Path RoutedLine(EventGraphPreviewNode fromNode, EventGraphPreviewNode toNode, double yOffset)
    {
        var fromRect = new Rect(fromNode.X, fromNode.Y + yOffset, fromNode.Width, fromNode.Height);
        var toRect = new Rect(toNode.X, toNode.Y + yOffset, toNode.Width, toNode.Height);
        var from = new Point(fromRect.Right, fromRect.Top + fromRect.Height / 2);
        var to = new Point(toRect.Left, toRect.Top + toRect.Height / 2);
        var points = BuildPreviewRoute(from, to, fromRect, toRect);
        var figure = new PathFigure { StartPoint = points[0] };
        foreach (var point in points.Skip(1))
            figure.Segments.Add(new LineSegment(point, true));
        return new Path { Data = new PathGeometry([figure]) };
    }

    private static List<Point> BuildPreviewRoute(Point from, Point to, Rect source, Rect target)
    {
        const double margin = 34;
        var horizontalGap = to.X - from.X;
        if (horizontalGap >= 8)
        {
            var bend = Math.Clamp(horizontalGap / 2, 12, 64);
            var midX = from.X + bend;
            return [from, new Point(midX, from.Y), new Point(midX, to.Y), to];
        }

        var exitX = source.Right + margin;
        var enterX = target.Left - margin;
        var points = new List<Point> { from, new(exitX, from.Y) };
        var corridorY = from.Y <= to.Y
            ? Math.Min(source.Top, target.Top) - margin
            : Math.Max(source.Bottom, target.Bottom) + margin;
        var routeEnterX = Math.Min(enterX, Math.Min(source.Left, target.Left) - margin);
        points.Add(new Point(exitX, corridorY));
        points.Add(new Point(routeEnterX, corridorY));
        points.Add(new Point(routeEnterX, to.Y));
        points.Add(to);
        return points;
    }

    private static Border RenderNode(EventGraphPreviewNode node, string changedText, double yOffset)
    {
        var changed = NodeChanged(node, changedText);
        var border = new Border
        {
            Width = node.Width,
            Height = node.Height,
            Background = new SolidColorBrush(changed ? Color.FromRgb(66, 58, 35) : Color.FromRgb(42, 42, 42)),
            BorderBrush = new SolidColorBrush(changed ? Color.FromRgb(239, 210, 93) : NodeBorder(node.Kind)),
            BorderThickness = new Thickness(changed ? 2.5 : 1.3),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = node.Title,
            Foreground = new SolidColorBrush(Color.FromRgb(118, 188, 255)),
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = node.Body,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 52,
            Margin = new Thickness(0, 6, 0, 8)
        });
        for (var i = 0; i < Math.Min(node.Rows.Count, 5); i++)
        {
            var row = node.Rows[i];
            var rowId = i < node.RowIds.Count ? node.RowIds[i] : "";
            var rowChanged = RowChanged(rowId, row, changedText);
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(rowChanged ? Color.FromRgb(255, 230, 138) : Color.FromRgb(232, 232, 232)),
                BorderBrush = new SolidColorBrush(rowChanged ? Color.FromRgb(235, 190, 70) : Color.FromRgb(232, 232, 232)),
                BorderThickness = new Thickness(rowChanged ? 1 : 0),
                Margin = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = row,
                    Foreground = Brushes.Black,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            });
        }
        border.Child = stack;
        Canvas.SetLeft(border, node.X);
        Canvas.SetTop(border, node.Y + yOffset);
        return border;
    }

    private static Color NodeBorder(string kind) => kind switch
    {
        "reward" => Color.FromRgb(171, 138, 65),
        "battle" => Color.FromRgb(204, 93, 76),
        _ => Color.FromRgb(98, 98, 98)
    };

    private static bool NodeChanged(EventGraphPreviewNode node, string changedText)
        => changedText.Contains(node.Id, StringComparison.OrdinalIgnoreCase);

    private static bool RowChanged(string rowId, string rowText, string changedText)
        => (!string.IsNullOrWhiteSpace(rowId) && changedText.Contains(rowId, StringComparison.OrdinalIgnoreCase))
           || (!string.IsNullOrWhiteSpace(rowText) && changedText.Contains(rowText, StringComparison.OrdinalIgnoreCase));

    private static bool LinkChanged(EventGraphPreviewLink link, string changedText)
        => changedText.Contains(link.FromId, StringComparison.OrdinalIgnoreCase)
           || changedText.Contains(link.ToId, StringComparison.OrdinalIgnoreCase);

    private static string BuildDiffText(DiffGroup group, bool before)
    {
        return string.Join("\n\n", group.Entries.Select(e =>
        {
            var value = before ? e.BeforeValue : e.AfterValue;
            if (string.IsNullOrWhiteSpace(value)) value = "(empty)";
            return $"[{ShortSheet(e.Sheet)}] {e.ChangeType} {e.Key}\n{value}";
        }));
    }

    private void RevertSelected()
    {
        var selected = _list.SelectedItems
            .OfType<ListBoxItem>()
            .Select(i => i.Tag)
            .OfType<DiffGroup>()
            .SelectMany(g => g.Entries)
            .Where(d => d.CanRevert)
            .ToList();
        if (selected.Count == 0)
            return;

        _revert(selected);
        ReloadDiff();
    }

    private void DiffCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox clicked)
            return;

        var newValue = clicked.IsChecked != true;
        var selectedItems = _list.SelectedItems
            .OfType<ListBoxItem>()
            .Where(i => i.Tag is DiffGroup g && g.CanRevert)
            .ToList();
        var clickedGroup = clicked.Tag as DiffGroup;
        if (clickedGroup is null)
        {
            e.Handled = true;
            return;
        }

        if (selectedItems.Any(i => ReferenceEquals(i.Tag, clickedGroup)))
        {
            foreach (var item in selectedItems)
            {
                var check = FindCheckBox(item);
                if (check is not null)
                    check.IsChecked = newValue;
            }
        }
        else
        {
            clicked.IsChecked = newValue;
        }

        e.Handled = true;
    }

    private static CheckBox? FindCheckBox(ListBoxItem item)
    {
        if (item.Content is DockPanel panel)
            return panel.Children.OfType<CheckBox>().FirstOrDefault();
        return null;
    }
}
