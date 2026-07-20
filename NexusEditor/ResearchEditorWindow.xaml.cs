using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace NexusEditor;

public partial class ResearchEditorWindow : Window
{
    private const double NodeWidth = 146;
    private const double NodeHeight = 162;
    private const double ColumnSpacing = 188;
    private const double GraphLeft = 110;
    private const double LaneTop = 78;
    private const double LaneSpacing = 236;

    private readonly Action<WorkspaceMode>? _workspaceSwitch;
    private readonly Dictionary<string, Border> _nodeVisuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ellipse> _inputPins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ellipse> _outputPins = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedNodeIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<UndoState> _undoStack = [];
    private readonly List<ResearchConsoleEntry> _consoleEntries = [];
    private readonly List<ResearchNodeClipboardItem> _nodeClipboard = [];
    private readonly Dictionary<string, ImageSource?> _researchAssetCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedMissingAssets = new(StringComparer.OrdinalIgnoreCase);
    private long _consoleSequence;

    private ResearchEditorSettings _settings;
    private ResearchWorkbookPaths? _paths;
    private ResearchWorkbookContext? _workbook;
    private ResearchCategoryRow? _selectedCategory;
    private ResearchNodeRow? _primaryNode;
    private string? _researchImageRoot;
    private bool _isLoading;
    private bool _suppressCategorySelection;
    private bool _suppressInspectorCommit;
    private bool _allowClose;
    private int _permissionFilter;

    private bool _isPanning;
    private Point _panScreenStart;
    private double _panXStart;
    private double _panYStart;
    private bool _rightMouseMoved;

    private bool _isNodeDragging;
    private Point _nodeDragStartWorld;
    private readonly Dictionary<string, Point> _nodeDragOrigins = new(StringComparer.OrdinalIgnoreCase);
    private Border? _dragCaptureVisual;

    private bool _isLinkDragging;
    private ResearchNodeRow? _linkSource;
    private ShapePath? _temporaryLink;

    public ResearchEditorWindow(Action<WorkspaceMode>? workspaceSwitch = null)
    {
        InitializeComponent();
        _workspaceSwitch = workspaceSwitch;
        _settings = ResearchPathResolver.LoadSettings();
        RefreshResearchAssetRoot();
        RestoreLayout();
    }

    public bool HasUnsavedChanges => _workbook is not null && ResearchWorkbookService.BuildDiff(_workbook).Count > 0;

    public async Task<bool> BeginInitialLoadAsync()
    {
        if (_workbook is not null)
            return true;
        if (_isLoading)
            return false;

        var paths = ResearchPathResolver.ResolveDefault();
        if (paths is null)
        {
            var preferences = new ResearchPreferencesWindow(null) { Owner = this };
            if (preferences.ShowDialog() != true || preferences.SelectedPaths is null)
            {
                Log(ResearchConsoleSeverity.Error, "연구 DB 경로가 설정되지 않았습니다.");
                return false;
            }
            paths = preferences.SelectedPaths;
        }

        return await LoadAsync(paths);
    }

    private async Task<bool> LoadAsync(ResearchWorkbookPaths paths)
    {
        _isLoading = true;
        IsEnabled = false;
        try
        {
            SceneStatusText.Text = "Loading research DB...";
            var loaded = await Task.Run(() => ResearchWorkbookService.Load(paths.OutSystemPath, paths.EffectPath));
            _paths = paths;
            _workbook = loaded;
            _undoStack.Clear();
            _selectedNodeIdentities.Clear();
            _primaryNode = null;
            RefreshResearchAssetRoot();
            ResearchPathResolver.Remember(paths);
            WorkbookPathText.Text = $"{Path.GetFileName(paths.OutSystemPath)} + {Path.GetFileName(paths.EffectPath)}";
            PopulateCategories();
            RunValidation(logSuccess: true);
            UpdateDirtyState();
            return true;
        }
        catch (Exception ex)
        {
            Log(ResearchConsoleSeverity.Error, $"연구 DB를 열지 못했습니다: {ex.Message}");
            ThemedMessageBox.Show(ex.ToString(), "Research DB load failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            IsEnabled = true;
            _isLoading = false;
        }
    }

    private void PopulateCategories(string? selectCategory = null)
    {
        if (_workbook is null)
            return;

        var search = CategorySearchBox.Text.Trim();
        var items = _workbook.Categories
            .Where(category => search.Length == 0
                               || category.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                               || category.CategoryKey.Contains(search, StringComparison.OrdinalIgnoreCase)
                               || category.NodeName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(category => category.Index ?? int.MaxValue)
            .ThenBy(category => category.Category, StringComparer.OrdinalIgnoreCase)
            .Select(category => new ResearchCategoryListItem(category))
            .ToList();

        var desired = selectCategory ?? _selectedCategory?.Category ?? _settings.LastCategory;
        _suppressCategorySelection = true;
        CategoryList.ItemsSource = items;
        CategoryList.SelectedItem = items.FirstOrDefault(item => Same(item.Row.Category, desired)) ?? items.FirstOrDefault();
        _suppressCategorySelection = false;
        if (CategoryList.SelectedItem is ResearchCategoryListItem selected)
            SelectCategory(selected.Row, fitGraph: true);
        else
            SelectCategory(null, fitGraph: false);
    }

    private void SelectCategory(ResearchCategoryRow? category, bool fitGraph)
    {
        _selectedCategory = category;
        _settings.LastCategory = category?.Category;
        _selectedNodeIdentities.Clear();
        _primaryNode = null;
        RenderGraph(fitGraph);
        PopulateHierarchy();
        RenderInspector();
    }

    private IEnumerable<ResearchNodeRow> VisibleNodes()
    {
        if (_workbook is null || _selectedCategory is null)
            return [];
        return _workbook.Nodes
            .Where(node => Same(node.Category, _selectedCategory.Category))
            .Where(node => _permissionFilter == 0 || node.NodePermission == _permissionFilter)
            .OrderBy(node => node.Column)
            .ThenBy(node => node.Row);
    }

    private void RenderGraph(bool fitGraph = false)
    {
        BandCanvas.Children.Clear();
        LinkCanvas.Children.Clear();
        NodeCanvas.Children.Clear();
        _nodeVisuals.Clear();
        _inputPins.Clear();
        _outputPins.Clear();

        if (_workbook is null || _selectedCategory is null)
        {
            SceneStatusText.Text = "Select a category";
            return;
        }

        var nodes = VisibleNodes().ToList();
        DrawPermissionBands(nodes);
        foreach (var node in nodes)
            CreateNodeVisual(node);
        DrawAllLinks();
        ApplySelectionVisuals();

        var allCount = _workbook.Nodes.Count(node => Same(node.Category, _selectedCategory.Category));
        SceneStatusText.Text = $"{_selectedCategory.Category} / {nodes.Count:N0} of {allCount:N0} nodes / Wheel: Zoom / RMB: Pan";
        if (fitGraph)
            Dispatcher.BeginInvoke(FitAll);
    }

    private void DrawPermissionBands(IReadOnlyCollection<ResearchNodeRow> nodes)
    {
        for (var permission = 1; permission <= 5; permission++)
        {
            var group = nodes.Where(node => node.NodePermission == permission).ToList();
            if (group.Count == 0)
                continue;
            var minColumn = group.Min(node => node.Column);
            var maxColumn = group.Max(node => node.Column);
            var left = WorldX(minColumn) - 28;
            var width = WorldX(maxColumn) - left + NodeWidth + 56;
            var band = new Border
            {
                Width = width,
                Height = 1010,
                Background = new SolidColorBrush(Color.FromArgb(permission % 2 == 0 ? (byte)18 : (byte)10, 70, 145, 200)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 70, 145, 200)),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"PERMISSION {permission}",
                    Foreground = new SolidColorBrush(Color.FromArgb(140, 135, 196, 235)),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(12, 8, 0, 0)
                }
            };
            Canvas.SetLeft(band, left);
            Canvas.SetTop(band, 30);
            BandCanvas.Children.Add(band);
        }
    }

    private void CreateNodeVisual(ResearchNodeRow node)
    {
        var card = new Border
        {
            Width = NodeWidth,
            Height = NodeHeight,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(91, 91, 91)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(3),
            Tag = node,
            ToolTip = $"{node.Id}\n{node.NodeEffectDesc}\nimage: {node.Image}\nactive_step: {node.ActiveStep ?? 0}"
        };
        card.MouseLeftButtonDown += Node_MouseLeftButtonDown;
        card.MouseMove += Node_MouseMove;
        card.MouseLeftButtonUp += Node_MouseLeftButtonUp;

        var root = new Grid();
        card.Child = root;
        var frameSource = LoadResearchAsset("nexus1_bg_research_slot", reportMissing: false);
        if (frameSource is not null)
        {
            root.Children.Add(new Image
            {
                Source = frameSource,
                Stretch = Stretch.Fill,
                Opacity = 0.82,
                IsHitTestVisible = false
            });
        }

        var grid = new Grid { Margin = new Thickness(7, 6, 7, 7) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(23) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        Panel.SetZIndex(grid, 1);
        root.Children.Add(grid);

        var header = new Border
        {
            Background = NodeHeaderBrush(node.NodePermission),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 2, 5, 2),
            Opacity = 0.94
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Child = headerGrid;
        headerGrid.Children.Add(new TextBlock
        {
            Text = node.Id,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 9.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        var permission = new TextBlock
        {
            Text = $"P{node.NodePermission ?? 0}",
            Foreground = new SolidColorBrush(Color.FromRgb(215, 235, 248)),
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(permission, 1);
        headerGrid.Children.Add(permission);
        grid.Children.Add(header);

        var iconHost = new Grid { Margin = new Thickness(10, 4, 10, 0) };
        var iconSource = LoadResearchAsset(node.Image);
        if (iconSource is not null)
        {
            iconHost.Children.Add(new Image
            {
                Source = iconSource,
                Width = 82,
                Height = 82,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            });
        }
        else
        {
            iconHost.Children.Add(new TextBlock
            {
                Text = "?",
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                FontSize = 38,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        Grid.SetRow(iconHost, 1);
        grid.Children.Add(iconHost);

        var count = new TextBlock
        {
            Text = $"×{node.ActiveStep ?? 0}",
            Foreground = Brushes.White,
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(count, 2);
        grid.Children.Add(count);

        var input = CreatePin(node, isOutput: false);
        var output = CreatePin(node, isOutput: true);
        root.Children.Add(input);
        root.Children.Add(output);

        Canvas.SetLeft(card, WorldX(node.Column));
        Canvas.SetTop(card, WorldY(node.Row));
        NodeCanvas.Children.Add(card);
        _nodeVisuals[node.RowIdentity] = card;
        _inputPins[node.RowIdentity] = input;
        _outputPins[node.RowIdentity] = output;
    }

    private void RefreshResearchAssetRoot()
    {
        _researchAssetCache.Clear();
        _reportedMissingAssets.Clear();
        var devRoot = NexusPathResolver.ResolveDefaultDevRoot(NexusPathResolver.LoadSettings());
        var candidate = string.IsNullOrWhiteSpace(devRoot)
            ? null
            : Path.Combine(devRoot, "game", "Resources", "res", "img");
        _researchImageRoot = candidate is not null && Directory.Exists(candidate) ? candidate : null;
    }

    private ImageSource? LoadResearchAsset(string? assetKey, bool reportMissing = true)
    {
        var key = NormalizeResearchAssetKey(assetKey);
        if (key is null)
        {
            if (reportMissing)
                ReportMissingAssetOnce(assetKey ?? "(blank)", "연구 노드 image 값이 비어 있거나 올바른 파일명이 아닙니다.");
            return LoadFallbackAsset();
        }

        if (_researchAssetCache.TryGetValue(key, out var cached))
            return cached;
        if (_researchImageRoot is null)
        {
            if (reportMissing)
                ReportMissingAssetOnce("__image_root__", "DEV의 game\\Resources\\res\\img 경로를 찾지 못했습니다. Preference의 Client Path를 확인하세요.");
            _researchAssetCache[key] = null;
            return reportMissing ? LoadFallbackAsset() : null;
        }

        var path = Path.Combine(_researchImageRoot, key + ".png");
        var image = LoadBitmap(path);
        if (image is null && reportMissing)
            ReportMissingAssetOnce(key, $"연구 노드 아이콘을 찾지 못했습니다: {key}.png");
        _researchAssetCache[key] = image;
        return image ?? (reportMissing ? LoadFallbackAsset() : null);
    }

    private ImageSource? LoadFallbackAsset()
    {
        const string fallbackKey = "icon_stat_def";
        if (_researchAssetCache.TryGetValue(fallbackKey, out var cached))
            return cached;
        if (_researchImageRoot is null)
            return null;
        var image = LoadBitmap(Path.Combine(_researchImageRoot, fallbackKey + ".png"));
        _researchAssetCache[fallbackKey] = image;
        return image;
    }

    private static string? NormalizeResearchAssetKey(string? assetKey)
    {
        var trimmed = assetKey?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !Same(Path.GetFileName(trimmed), trimmed))
            return null;
        var key = Path.GetFileNameWithoutExtension(trimmed);
        return key.Length > 0 && key.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            ? key
            : null;
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void ReportMissingAssetOnce(string key, string message)
    {
        if (_reportedMissingAssets.Add(key))
            Log(ResearchConsoleSeverity.Warning, message);
    }

    private Ellipse CreatePin(ResearchNodeRow node, bool isOutput)
    {
        var pin = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = isOutput
                ? new SolidColorBrush(Color.FromRgb(81, 199, 137))
                : new SolidColorBrush(Color.FromRgb(216, 226, 255)),
            Stroke = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
            StrokeThickness = 1.5,
            HorizontalAlignment = isOutput ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isOutput ? new Thickness(0, 0, -7, 0) : new Thickness(-7, 0, 0, 0),
            Tag = new ResearchPinTag(node, isOutput),
            Cursor = isOutput ? Cursors.Cross : Cursors.Arrow,
            ToolTip = isOutput ? "Drag to another node input" : "Condition input"
        };
        Panel.SetZIndex(pin, 10);
        if (isOutput)
            pin.MouseLeftButtonDown += OutputPin_MouseLeftButtonDown;
        return pin;
    }

    private void DrawAllLinks()
    {
        if (_workbook is null)
            return;
        LinkCanvas.Children.Clear();
        var visible = VisibleNodes().ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var target in visible.Values)
        {
            foreach (var sourceId in target.Conditions.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (!visible.TryGetValue(sourceId, out var source))
                    continue;
                var path = CreateLinkPath(source, target, temporary: false);
                path.Tag = new ResearchLinkTag(source.Id, target.RowIdentity);
                var menu = new ContextMenu();
                var disconnect = new MenuItem { Header = "Disconnect condition" };
                disconnect.Click += (_, _) => DisconnectLink(source, target);
                menu.Items.Add(disconnect);
                path.ContextMenu = menu;
                LinkCanvas.Children.Add(path);
            }
        }
    }

    private ShapePath CreateLinkPath(ResearchNodeRow source, ResearchNodeRow target, bool temporary)
    {
        var from = GetOutputPoint(source);
        var to = GetInputPoint(target);
        return new ShapePath
        {
            Data = BuildOrthogonalGeometry(from, to),
            Stroke = temporary
                ? new SolidColorBrush(Color.FromArgb(190, 242, 210, 91))
                : new SolidColorBrush(Color.FromRgb(101, 210, 149)),
            StrokeThickness = temporary ? 2.4 : 2.0,
            Fill = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = temporary ? null : $"{source.Id} -> {target.Id}\nRight click to disconnect"
        };
    }

    private static Geometry BuildOrthogonalGeometry(Point from, Point to)
    {
        const double radius = 12;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(from, false, false);

        if (to.X >= from.X + 48)
        {
            var middle = (from.X + to.X) / 2;
            var direction = Math.Sign(to.Y - from.Y);
            var r = Math.Min(radius, Math.Abs(to.Y - from.Y) / 2);
            context.LineTo(new Point(middle - r, from.Y), true, false);
            if (r > 0)
                context.ArcTo(new Point(middle, from.Y + direction * r), new Size(r, r), 0, false, direction > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            context.LineTo(new Point(middle, to.Y - direction * r), true, false);
            if (r > 0)
                context.ArcTo(new Point(middle + r, to.Y), new Size(r, r), 0, false, direction > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true, false);
            context.LineTo(to, true, false);
        }
        else
        {
            var routeX = Math.Max(from.X, to.X) + 44;
            var routeY = Math.Min(from.Y, to.Y) - 42;
            context.LineTo(new Point(routeX, from.Y), true, false);
            context.LineTo(new Point(routeX, routeY), true, false);
            context.LineTo(new Point(to.X - 34, routeY), true, false);
            context.LineTo(new Point(to.X - 34, to.Y), true, false);
            context.LineTo(to, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private Point GetInputPoint(ResearchNodeRow node) => new(
        Canvas.GetLeft(_nodeVisuals[node.RowIdentity]),
        Canvas.GetTop(_nodeVisuals[node.RowIdentity]) + NodeHeight / 2);

    private Point GetOutputPoint(ResearchNodeRow node) => new(
        Canvas.GetLeft(_nodeVisuals[node.RowIdentity]) + NodeWidth,
        Canvas.GetTop(_nodeVisuals[node.RowIdentity]) + NodeHeight / 2);

    private static double WorldX(int column) => GraphLeft + Math.Max(0, column - 1) * ColumnSpacing;

    private double WorldY(int row)
    {
        var lanes = GetLaneChoices();
        var laneIndex = Array.IndexOf(lanes, row);
        if (laneIndex >= 0)
            return LaneTop + laneIndex * LaneSpacing;
        var step = lanes.Length > 1 ? Math.Max(1d, lanes.Zip(lanes.Skip(1), (left, right) => right - left).Average()) : 2d;
        return LaneTop + Math.Max(0, (row - lanes[0]) / step) * LaneSpacing;
    }

    private int NearestLane(double top)
    {
        var choices = GetLaneChoices();
        return choices.MinBy(row => Math.Abs(WorldY(row) - top));
    }

    private int[] GetLaneChoices()
    {
        var categoryRows = _workbook?.Nodes
            .Where(node => _selectedCategory is not null && Same(node.Category, _selectedCategory.Category))
            .Select(node => node.Row)
            .Distinct()
            .OrderBy(row => row)
            .ToArray() ?? [];
        if (categoryRows.Length >= 2)
            return categoryRows;

        var commonRows = _workbook?.Nodes
            .Where(node => node.Row > 0)
            .GroupBy(node => node.Row)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(3)
            .Select(group => group.Key)
            .OrderBy(row => row)
            .ToArray() ?? [];
        return commonRows.Length > 0 ? commonRows : [3, 5, 7];
    }

    private static Brush NodeHeaderBrush(int? permission)
    {
        var color = permission switch
        {
            1 => Color.FromRgb(43, 82, 106),
            2 => Color.FromRgb(61, 86, 77),
            3 => Color.FromRgb(92, 75, 45),
            4 => Color.FromRgb(88, 62, 89),
            5 => Color.FromRgb(102, 63, 63),
            _ => Color.FromRgb(58, 58, 58)
        };
        return new SolidColorBrush(color);
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed record ResearchPinTag(ResearchNodeRow Node, bool IsOutput);
    private sealed record ResearchLinkTag(string SourceNodeId, string TargetRowIdentity);
    private sealed record UndoState(ResearchWorkbookContext Workbook, string? Category, string[] SelectedNodeIds);
    private sealed record ResearchNodeClipboardItem(ResearchNodeRow Node, ResearchEffectRow? Effect);

    private sealed class ResearchCategoryListItem
    {
        public ResearchCategoryListItem(ResearchCategoryRow row) => Row = row;
        public ResearchCategoryRow Row { get; }
        public override string ToString() => $"{Row.Index,2}. {Row.Category}   {Row.NodeName}";
    }

    private enum ResearchConsoleSeverity { Info, Warning, Error }

    private sealed class ResearchConsoleEntry
    {
        public ResearchConsoleSeverity Severity { get; init; }
        public string Message { get; init; } = "";
        public string TargetId { get; init; } = "";
        public bool IsValidation { get; init; }
        public long Sequence { get; init; }
        public override string ToString() => $"{Severity.ToString().ToUpperInvariant()}: {Message}";
    }
}

public partial class ResearchEditorWindow
{
    private void CategorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_workbook is not null)
            PopulateCategories();
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCategorySelection)
            return;
        SelectCategory((CategoryList.SelectedItem as ResearchCategoryListItem)?.Row, fitGraph: true);
    }

    private void CategoryList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ResearchCategoryListItem categoryItem)
            return;
        item.IsSelected = true;
        var menu = new ContextMenu();
        var delete = new MenuItem { Header = "Delete category" };
        delete.Click += (_, _) => DeleteSelectedCategory();
        menu.Items.Add(delete);
        item.ContextMenu = menu;
        item.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void AddCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null)
            return;
        var nextIndex = Math.Max(1, _workbook.Categories.Select(category => category.Index ?? 0).DefaultIfEmpty().Max() + 1);
        var key = $"category_{nextIndex}";
        PushUndo();
        try
        {
            var category = ResearchWorkbookService.CreateCategory(_workbook, key, nextIndex);
            PopulateCategories(category.Category);
            UpdateDirtyState();
            Log(ResearchConsoleSeverity.Info, $"카테고리를 추가했습니다: {category.Category}", category.Category);
        }
        catch (Exception ex)
        {
            UndoWithoutRender();
            ThemedMessageBox.Show(ex.Message, "Add category", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteSelectedCategory()
    {
        if (_workbook is null || _selectedCategory is null)
            return;
        var contained = _workbook.Nodes.Count(node => Same(node.Category, _selectedCategory.Category));
        var message = contained == 0
            ? $"{_selectedCategory.Category} 카테고리를 삭제할까요?"
            : $"{_selectedCategory.Category} 카테고리와 안의 노드 {contained:N0}개를 함께 삭제할까요?";
        if (ThemedMessageBox.Show(message, "Delete category", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        PushUndo();
        var result = ResearchWorkbookService.TryDeleteCategory(_workbook, _selectedCategory.RowIdentity, deleteContainedNodes: true);
        if (!result.Success)
        {
            UndoWithoutRender();
            ThemedMessageBox.Show(result.Message, "Delete category", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _selectedCategory = null;
        PopulateCategories();
        UpdateDirtyState();
    }

    private void PermissionFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var permission))
            return;
        _permissionFilter = permission;
        _settings.PermissionFilter = permission;
        if (IsLoaded)
        {
            var visibleIdentities = VisibleNodes()
                .Select(node => node.RowIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _selectedNodeIdentities.IntersectWith(visibleIdentities);
            if (_primaryNode is not null && !visibleIdentities.Contains(_primaryNode.RowIdentity))
                _primaryNode = null;
            RenderGraph(fitGraph: true);
            PopulateHierarchy();
            RenderInspector();
        }
    }

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ResearchNodeRow node } card)
            return;
        if (e.OriginalSource is Ellipse)
            return;

        SceneViewport.Focus();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (!_selectedNodeIdentities.Add(node.RowIdentity))
                _selectedNodeIdentities.Remove(node.RowIdentity);
        }
        else if (!_selectedNodeIdentities.Contains(node.RowIdentity))
        {
            _selectedNodeIdentities.Clear();
            _selectedNodeIdentities.Add(node.RowIdentity);
        }

        _primaryNode = _selectedNodeIdentities.Contains(node.RowIdentity) ? node : null;
        ApplySelectionVisuals();
        RenderInspector();
        SelectHierarchyNode(node.RowIdentity);

        if (_primaryNode is null)
            return;
        _isNodeDragging = true;
        _nodeDragStartWorld = e.GetPosition(NodeCanvas);
        _nodeDragOrigins.Clear();
        foreach (var identity in _selectedNodeIdentities)
        {
            if (!_nodeVisuals.TryGetValue(identity, out var visual))
                continue;
            _nodeDragOrigins[identity] = new Point(Canvas.GetLeft(visual), Canvas.GetTop(visual));
        }
        _dragCaptureVisual = card;
        card.CaptureMouse();
        e.Handled = true;
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isNodeDragging || e.LeftButton != MouseButtonState.Pressed)
            return;
        var current = e.GetPosition(NodeCanvas);
        var delta = current - _nodeDragStartWorld;
        foreach (var (identity, origin) in _nodeDragOrigins)
        {
            if (!_nodeVisuals.TryGetValue(identity, out var visual))
                continue;
            Canvas.SetLeft(visual, Math.Max(10, origin.X + delta.X));
            Canvas.SetTop(visual, Math.Clamp(origin.Y + delta.Y, 35, GraphCanvas.Height - NodeHeight - 20));
        }
        DrawAllLinks();
        e.Handled = true;
    }

    private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isNodeDragging)
            return;
        _dragCaptureVisual?.ReleaseMouseCapture();
        _dragCaptureVisual = null;
        _isNodeDragging = false;

        if (_workbook is null || _primaryNode is null || !_nodeVisuals.TryGetValue(_primaryNode.RowIdentity, out var primaryVisual))
            return;
        var origin = _nodeDragOrigins.GetValueOrDefault(_primaryNode.RowIdentity);
        var currentLeft = Canvas.GetLeft(primaryVisual);
        var currentTop = Canvas.GetTop(primaryVisual);
        var oldColumn = _primaryNode.Column;
        var oldRow = _primaryNode.Row;
        var newColumn = Math.Max(1, (int)Math.Round((currentLeft - GraphLeft) / ColumnSpacing) + 1);
        var newRow = NearestLane(currentTop);
        var deltaColumn = newColumn - oldColumn;
        var deltaRow = newRow - oldRow;
        if (Math.Abs(currentLeft - origin.X) < 1 && Math.Abs(currentTop - origin.Y) < 1 || deltaColumn == 0 && deltaRow == 0)
        {
            RenderGraph();
            return;
        }

        PushUndo();
        var moveResult = ResearchWorkbookService.TryMoveNodes(
            _workbook, _selectedNodeIdentities, deltaColumn, deltaRow);
        if (!moveResult.Success)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Warning, moveResult.Message, _primaryNode.Id);
        }
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        UpdateDirtyState();
        e.Handled = true;
    }

    private void OutputPin_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Ellipse { Tag: ResearchPinTag { IsOutput: true } pin })
            return;
        _isLinkDragging = true;
        _linkSource = pin.Node;
        _temporaryLink = new ShapePath
        {
            Stroke = new SolidColorBrush(Color.FromRgb(241, 205, 82)),
            StrokeThickness = 2.4,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        LinkCanvas.Children.Add(_temporaryLink);
        SceneViewport.CaptureMouse();
        UpdateTemporaryLink(e.GetPosition(GraphCanvas));
        e.Handled = true;
    }

    private void SceneViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isLinkDragging)
            return;
        var hit = SceneViewport.InputHitTest(e.GetPosition(SceneViewport)) as DependencyObject;
        var input = FindInputPin(hit) ?? FindNearestInputPin(e.GetPosition(SceneViewport));
        var source = _linkSource;
        EndLinkDrag();
        if (_workbook is null || source is null || input?.Tag is not ResearchPinTag { IsOutput: false } target)
        {
            Log(ResearchConsoleSeverity.Warning, "연결할 노드의 왼쪽 INPUT 핀에 선을 놓으세요.");
            return;
        }

        PushUndo();
        var result = ResearchWorkbookService.TryConnectCondition(_workbook, source.RowIdentity, target.Node.RowIdentity);
        if (!result.Success)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Warning, result.Message, target.Node.Id);
        }
        else if (!string.IsNullOrWhiteSpace(result.WarningMessage))
        {
            Log(ResearchConsoleSeverity.Warning, result.WarningMessage, source.Id);
        }
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        UpdateDirtyState();
        e.Handled = true;
    }

    private void UpdateTemporaryLink(Point worldPoint)
    {
        if (_temporaryLink is null || _linkSource is null || !_nodeVisuals.ContainsKey(_linkSource.RowIdentity))
            return;
        _temporaryLink.Data = BuildOrthogonalGeometry(GetOutputPoint(_linkSource), worldPoint);
    }

    private void EndLinkDrag()
    {
        SceneViewport.ReleaseMouseCapture();
        if (_temporaryLink is not null)
            LinkCanvas.Children.Remove(_temporaryLink);
        _temporaryLink = null;
        _linkSource = null;
        _isLinkDragging = false;
    }

    private void DisconnectLink(ResearchNodeRow source, ResearchNodeRow target)
    {
        if (_workbook is null)
            return;
        PushUndo();
        if (!ResearchWorkbookService.DisconnectCondition(target, source.Id))
        {
            UndoWithoutRender();
            return;
        }
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        UpdateDirtyState();
    }

    private void SceneViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var oldScale = GraphScale.ScaleX;
        var newScale = Math.Clamp(oldScale * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.18, 2.4);
        var mouse = e.GetPosition(SceneViewport);
        var worldX = (mouse.X - GraphTranslate.X) / oldScale;
        var worldY = (mouse.Y - GraphTranslate.Y) / oldScale;
        GraphScale.ScaleX = GraphScale.ScaleY = newScale;
        GraphTranslate.X = mouse.X - worldX * newScale;
        GraphTranslate.Y = mouse.Y - worldY * newScale;
        SaveViewSettings();
        e.Handled = true;
    }

    private void SceneViewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _rightMouseMoved = false;
        _panScreenStart = e.GetPosition(SceneViewport);
        _panXStart = GraphTranslate.X;
        _panYStart = GraphTranslate.Y;
        SceneViewport.CaptureMouse();
        e.Handled = true;
    }

    private void SceneViewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning)
            return;
        _isPanning = false;
        SceneViewport.ReleaseMouseCapture();
        SaveViewSettings();
        if (!_rightMouseMoved)
            OpenSceneContextMenu(e.GetPosition(NodeCanvas));
        e.Handled = true;
    }

    private void SceneViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isLinkDragging)
        {
            UpdateTemporaryLink(e.GetPosition(GraphCanvas));
            return;
        }
        if (!_isPanning || e.RightButton != MouseButtonState.Pressed)
            return;
        var current = e.GetPosition(SceneViewport);
        var delta = current - _panScreenStart;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 3)
            _rightMouseMoved = true;
        GraphTranslate.X = _panXStart + delta.X;
        GraphTranslate.Y = _panYStart + delta.Y;
        e.Handled = true;
    }

    private void SceneViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Border or Ellipse or TextBlock)
            return;
        _selectedNodeIdentities.Clear();
        _primaryNode = null;
        ApplySelectionVisuals();
        RenderInspector();
        e.Handled = true;
    }

    private void OpenSceneContextMenu(Point worldPoint)
    {
        var menu = new ContextMenu();
        var add = new MenuItem { Header = "Add research node" };
        add.Click += (_, _) => AddNodeAt(worldPoint);
        menu.Items.Add(add);
        menu.IsOpen = true;
    }

    private void AddNodeAt(Point worldPoint)
    {
        if (_workbook is null || _selectedCategory is null)
            return;
        var column = Math.Max(1, (int)Math.Round((worldPoint.X - GraphLeft) / ColumnSpacing) + 1);
        var row = NearestLane(worldPoint.Y);
        while (_workbook.Nodes.Any(node => Same(node.Category, _selectedCategory.Category) && node.Column == column && node.Row == row))
            column++;
        PushUndo();
        try
        {
            var created = ResearchWorkbookService.CreateNode(_workbook, _selectedCategory.Category, column, row);
            _selectedNodeIdentities.Clear();
            _selectedNodeIdentities.Add(created.Node.RowIdentity);
            _primaryNode = created.Node;
            RenderGraph();
            PopulateHierarchy();
            RenderInspector();
            UpdateDirtyState();
        }
        catch (Exception ex)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Error, ex.Message);
        }
    }

    private void ApplySelectionVisuals()
    {
        foreach (var (identity, visual) in _nodeVisuals)
        {
            var selected = _selectedNodeIdentities.Contains(identity);
            visual.BorderBrush = selected
                ? new SolidColorBrush(Color.FromRgb(241, 207, 86))
                : new SolidColorBrush(Color.FromRgb(91, 91, 91));
            visual.BorderThickness = selected ? new Thickness(2.2) : new Thickness(1.2);
        }
    }

    private void FitAll()
    {
        var nodes = VisibleNodes().ToList();
        if (nodes.Count == 0 || SceneViewport.ActualWidth < 20 || SceneViewport.ActualHeight < 20)
            return;
        var left = nodes.Min(node => WorldX(node.Column));
        var right = nodes.Max(node => WorldX(node.Column)) + NodeWidth;
        var top = nodes.Min(node => WorldY(node.Row));
        var bottom = nodes.Max(node => WorldY(node.Row)) + NodeHeight;
        var scale = Math.Clamp(Math.Min((SceneViewport.ActualWidth - 70) / Math.Max(1, right - left),
                                        (SceneViewport.ActualHeight - 70) / Math.Max(1, bottom - top)), 0.18, 1.25);
        GraphScale.ScaleX = GraphScale.ScaleY = scale;
        GraphTranslate.X = (SceneViewport.ActualWidth - (right - left) * scale) / 2 - left * scale;
        GraphTranslate.Y = (SceneViewport.ActualHeight - (bottom - top) * scale) / 2 - top * scale;
        SaveViewSettings();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T value)
                return value;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static Ellipse? FindInputPin(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is Ellipse { Tag: ResearchPinTag { IsOutput: false } } pin)
                return pin;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private Ellipse? FindNearestInputPin(Point viewportPoint)
    {
        const double snapRadius = 24;
        Ellipse? nearest = null;
        var nearestDistanceSquared = snapRadius * snapRadius;
        foreach (var pin in _inputPins.Values)
        {
            if (!pin.IsVisible)
                continue;
            try
            {
                var origin = pin.TransformToAncestor(SceneViewport).Transform(new Point(0, 0));
                var center = new Point(origin.X + pin.ActualWidth / 2, origin.Y + pin.ActualHeight / 2);
                var dx = center.X - viewportPoint.X;
                var dy = center.Y - viewportPoint.Y;
                var distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > nearestDistanceSquared)
                    continue;
                nearestDistanceSquared = distanceSquared;
                nearest = pin;
            }
            catch (InvalidOperationException)
            {
                // The graph may have been rebuilt between mouse events.
            }
        }
        return nearest;
    }
}

public partial class ResearchEditorWindow
{
    private void ExportPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null || _paths is null)
            return;
        var diff = ResearchWorkbookService.BuildDiff(_workbook);
        if (diff.Count == 0)
        {
            ThemedMessageBox.Show("Export할 연구 변경이 없습니다.", "Research Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = new ResearchExportPreviewWindow(
            () => _workbook is null ? [] : ResearchWorkbookService.BuildDiff(_workbook),
            entries =>
            {
                if (_workbook is null)
                    return;
                PushUndo();
                ResearchWorkbookService.RevertRelatedDiffs(_workbook, entries);
                RefreshAfterExternalMutation();
            },
            ExportSelectedDiffs,
            string.IsNullOrWhiteSpace(_settings.LastExportId)
                ? ResearchWorkbookService.DefaultResearchExportId
                : _settings.LastExportId)
        {
            Owner = this
        };
        preview.ShowDialog();
        if (!string.IsNullOrWhiteSpace(preview.ExportId))
        {
            _settings.LastExportId = preview.ExportId;
            ResearchPathResolver.SaveSettings(_settings);
        }
        RefreshAfterExternalMutation();
    }

    private ResearchSaveResult ExportSelectedDiffs(IReadOnlyCollection<ResearchDiffEntry> selected, string exportId)
    {
        if (_workbook is null || _paths is null)
            throw new InvalidOperationException("연구 DB가 열려 있지 않습니다.");
        var selectedEntries = selected.ToList();
        var categoryIdentity = _selectedCategory?.RowIdentity;
        var nodeIdentity = _primaryNode?.RowIdentity;
        var exportContext = ResearchWorkbookService.CreateExportContext(_workbook, selectedEntries);
        var selectedIdentities = ResearchWorkbookService.BuildDiff(exportContext)
            .Select(entry => entry.RowIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new ResearchSaveOptions
        {
            CreateBackup = true,
            ValidateBeforeSave = true,
            ChangedRowsExportId = exportId,
            ExportRowIdentities = selectedIdentities
        };
        var result = ResearchWorkbookService.SaveAtomic(exportContext, options);
        var savedReload = ResearchWorkbookService.Load(_paths.OutSystemPath, _paths.EffectPath);
        var currentBeforeRebase = _workbook;
        var undoStates = _undoStack.Reverse().ToList();
        _workbook = ResearchWorkbookService.RebaseAfterExport(
            currentBeforeRebase, savedReload, exportContext, selectedEntries);
        _undoStack.Clear();
        foreach (var state in undoStates)
        {
            var rebasedState = ResearchWorkbookService.RebaseAfterExport(
                state.Workbook, savedReload, exportContext, selectedEntries);
            _undoStack.Push(new UndoState(rebasedState, state.Category, state.SelectedNodeIds));
        }
        _settings.LastExportId = exportId;
        ResearchPathResolver.SaveSettings(_settings);
        RefreshSelectedObjects(categoryIdentity, nodeIdentity);
        RefreshAfterExternalMutation();
        return result;
    }

    private bool ExportDirect()
    {
        if (_workbook is null)
            return true;
        var diff = ResearchWorkbookService.BuildDiff(_workbook);
        if (diff.Count == 0)
            return true;
        try
        {
            ExportSelectedDiffs(diff, string.IsNullOrWhiteSpace(_settings.LastExportId)
                ? ResearchWorkbookService.DefaultResearchExportId
                : _settings.LastExportId);
            return true;
        }
        catch (ResearchWorkbookValidationException ex)
        {
            foreach (var issue in ex.Issues)
                Log(issue.Severity == ResearchValidationSeverity.Error ? ResearchConsoleSeverity.Error : ResearchConsoleSeverity.Warning,
                    issue.Message, issue.TargetId);
            ThemedMessageBox.Show("연구 DB 검증 오류를 먼저 해결하세요.", "Research Export", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        catch (Exception ex)
        {
            Log(ResearchConsoleSeverity.Error, ex.Message);
            ThemedMessageBox.Show(ex.Message, "Research Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RefreshAfterExternalMutation()
    {
        PopulateCategories(_selectedCategory?.Category);
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        UpdateDirtyState();
    }
}

public partial class ResearchEditorWindow
{
    private void ValidateMenuItem_Click(object sender, RoutedEventArgs e) => RunValidation(logSuccess: true);

    private IReadOnlyList<ResearchValidationIssue> RunValidation(bool logSuccess)
    {
        if (_workbook is null)
            return [];
        var issues = ResearchWorkbookService.Validate(_workbook)
            .OrderBy(issue => issue.Severity == ResearchValidationSeverity.Error ? 0 : 1)
            .ThenBy(issue => issue.Sheet)
            .ThenBy(issue => issue.TargetId)
            .ToList();
        _consoleEntries.RemoveAll(entry => entry.IsValidation);
        foreach (var issue in issues)
        {
            _consoleEntries.Add(new ResearchConsoleEntry
            {
                Severity = issue.Severity == ResearchValidationSeverity.Error
                    ? ResearchConsoleSeverity.Error
                    : ResearchConsoleSeverity.Warning,
                Message = $"[{issue.Code}] {issue.Message}",
                TargetId = issue.TargetId,
                IsValidation = true,
                Sequence = ++_consoleSequence
            });
        }
        if (issues.Count == 0 && logSuccess)
        {
            _consoleEntries.Add(new ResearchConsoleEntry
            {
                Severity = ResearchConsoleSeverity.Info,
                Message = $"연구 DB 검증 완료: {_workbook.Categories.Count:N0} categories / {_workbook.Nodes.Count:N0} nodes / {_workbook.Effects.Count:N0} effects",
                IsValidation = true,
                Sequence = ++_consoleSequence
            });
        }
        TrimConsoleEntries();
        RefreshConsole();
        return issues;
    }

    private void Log(ResearchConsoleSeverity severity, string message, string? targetId = null)
    {
        _consoleEntries.Add(new ResearchConsoleEntry
        {
            Severity = severity,
            Message = message,
            TargetId = targetId ?? "",
            Sequence = ++_consoleSequence
        });
        TrimConsoleEntries();
        RefreshConsole();
    }

    private void TrimConsoleEntries()
    {
        const int maxEntries = 10000;
        if (_consoleEntries.Count > maxEntries)
            _consoleEntries.RemoveRange(0, _consoleEntries.Count - maxEntries);
    }

    private void RefreshConsole()
    {
        var search = ConsoleSearchBox.Text.Trim();
        ConsoleList.ItemsSource = _consoleEntries
            .Where(entry => search.Length == 0
                            || entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || entry.TargetId.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Severity == ResearchConsoleSeverity.Error ? 0 : entry.Severity == ResearchConsoleSeverity.Warning ? 1 : 2)
            .ThenByDescending(entry => entry.Sequence)
            .ToList();
    }

    private void ConsoleSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshConsole();

    private void ClearConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        _consoleEntries.Clear();
        RefreshConsole();
    }

    private void ConsoleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ConsoleList.SelectedItem is not ResearchConsoleEntry entry || string.IsNullOrWhiteSpace(entry.TargetId) || _workbook is null)
            return;
        var node = _workbook.FindNode(entry.TargetId)
                   ?? _workbook.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, entry.TargetId));
        if (node is not null)
        {
            var category = _workbook.FindCategory(node.Category);
            if (category is not null && !Same(_selectedCategory?.Category, category.Category))
            {
                _selectedCategory = category;
                PopulateCategories(category.Category);
            }
            _selectedNodeIdentities.Clear();
            _selectedNodeIdentities.Add(node.RowIdentity);
            _primaryNode = node;
            RenderGraph();
            PopulateHierarchy();
            RenderInspector();
            FocusSelectedNode();
            return;
        }
        var categoryTarget = _workbook.FindCategory(entry.TargetId);
        if (categoryTarget is not null)
            PopulateCategories(categoryTarget.Category);
    }

    private void UpdateDirtyState()
    {
        if (_workbook is null)
        {
            DirtyText.Text = "";
            return;
        }
        var count = ResearchWorkbookService.BuildDiff(_workbook).Count;
        DirtyText.Text = count == 0 ? "" : $"● {count:N0} changes";
        Title = count == 0 ? "Nexus Editor - Research" : "Nexus Editor - Research *";
    }

    private void RevertDiffEntry(ResearchDiffEntry entry)
    {
        if (_workbook?.OriginalSnapshot is null)
            return;
        PushUndo();
        switch (entry.Sheet)
        {
            case ResearchWorkbookService.CategorySheetName:
                RevertRow(_workbook.Categories, _workbook.OriginalSnapshot.Categories, entry,
                    row => row.RowIdentity, row => row.DeepClone());
                break;
            case ResearchWorkbookService.NodeSheetName:
                RevertRow(_workbook.Nodes, _workbook.OriginalSnapshot.Nodes, entry,
                    row => row.RowIdentity, row => row.DeepClone());
                break;
            case ResearchWorkbookService.EffectSheetName:
                RevertRow(_workbook.Effects, _workbook.OriginalSnapshot.Effects, entry,
                    row => row.RowIdentity, row => row.DeepClone());
                break;
        }
        RefreshSelectedObjects(_selectedCategory?.RowIdentity, _primaryNode?.RowIdentity);
        PopulateCategories(_selectedCategory?.Category);
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        UpdateDirtyState();
    }

    private static void RevertRow<TRow>(
        List<TRow> current,
        List<TRow> original,
        ResearchDiffEntry entry,
        Func<TRow, string> identity,
        Func<TRow, TRow> clone)
    {
        var currentIndex = current.FindIndex(row => Same(identity(row), entry.RowIdentity));
        var originalRow = original.FirstOrDefault(row => Same(identity(row), entry.RowIdentity));
        if (entry.ChangeType == ResearchDiffChangeType.Add)
        {
            if (currentIndex >= 0)
                current.RemoveAt(currentIndex);
            return;
        }
        if (originalRow is null)
            return;
        if (currentIndex >= 0)
            current[currentIndex] = clone(originalRow);
        else
            current.Add(clone(originalRow));
    }
}

public partial class ResearchEditorWindow
{
    private void PopulateHierarchy()
    {
        HierarchyTree.Items.Clear();
        if (_selectedCategory is null)
            return;

        var root = new TreeViewItem
        {
            Header = $"{_selectedCategory.Category} ({VisibleNodes().Count():N0})",
            Tag = _selectedCategory,
            IsExpanded = true
        };
        HierarchyTree.Items.Add(root);
        foreach (var permissionGroup in VisibleNodes().GroupBy(node => node.NodePermission ?? 0).OrderBy(group => group.Key))
        {
            var permission = new TreeViewItem
            {
                Header = $"Permission {permissionGroup.Key} ({permissionGroup.Count():N0})",
                IsExpanded = true
            };
            root.Items.Add(permission);
            foreach (var node in permissionGroup.OrderBy(node => node.Column).ThenBy(node => node.Row))
            {
                permission.Items.Add(new TreeViewItem
                {
                    Header = $"[{node.Column},{node.Row}] {node.Id}",
                    Tag = node
                });
            }
        }
    }

    private void HierarchyTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (HierarchyTree.SelectedItem is not TreeViewItem item)
            return;
        if (item.Tag is ResearchCategoryRow category)
        {
            _selectedNodeIdentities.Clear();
            _primaryNode = null;
            _selectedCategory = category;
            ApplySelectionVisuals();
            RenderInspector();
            return;
        }
        if (item.Tag is not ResearchNodeRow node)
            return;
        _selectedNodeIdentities.Clear();
        _selectedNodeIdentities.Add(node.RowIdentity);
        _primaryNode = node;
        ApplySelectionVisuals();
        RenderInspector();
        FocusSelectedNode();
    }

    private void SelectHierarchyNode(string rowIdentity)
    {
        foreach (var root in HierarchyTree.Items.OfType<TreeViewItem>())
        foreach (var permission in root.Items.OfType<TreeViewItem>())
        foreach (var item in permission.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is ResearchNodeRow node && Same(node.RowIdentity, rowIdentity))
            {
                item.IsSelected = true;
                return;
            }
        }
    }

    private void RenderInspector()
    {
        _suppressInspectorCommit = true;
        InspectorPanel.Children.Clear();
        if (_selectedNodeIdentities.Count > 1)
        {
            AddInspectorTitle($"{_selectedNodeIdentities.Count:N0} nodes selected");
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = "Delete로 함께 삭제하거나 노드를 끌어 같은 간격으로 이동할 수 있습니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 165)),
                TextWrapping = TextWrapping.Wrap
            });
            _suppressInspectorCommit = false;
            return;
        }
        if (_primaryNode is not null)
            RenderNodeInspector(_primaryNode);
        else if (_selectedCategory is not null)
            RenderCategoryInspector(_selectedCategory);
        else
            InspectorPanel.Children.Add(new TextBlock { Text = "Select a category or node", Foreground = Brushes.Gray });
        _suppressInspectorCommit = false;
    }

    private void RenderCategoryInspector(ResearchCategoryRow category)
    {
        AddInspectorTitle("Node Category");
        AddReadOnlyField("category", category.Category, "nexus_node_category.category. Helper Prefix와 Category Key로 자동 생성됩니다.");
        AddEditableField("export_id", category.ExportId, value => category.ExportId = value,
            "변경된 행을 추적하는 Export ID입니다.");
        AddEditableField("helper prefix", category.HelperPrefix, value =>
        {
            category.HelperPrefix = value;
            var result = ResearchWorkbookService.TryRenameCategory(_workbook!, category.RowIdentity, category.CategoryKey);
            EnsureMutation(result);
            RefreshSelectedObjects(category.RowIdentity, null);
        }, "category 식별자 앞부분입니다. 바꾸면 이 카테고리의 노드 ID와 효과 ID, 조건 참조도 함께 바뀝니다.");
        AddEditableField("category key", category.CategoryKey, value =>
        {
            var result = ResearchWorkbookService.TryRenameCategory(_workbook!, category.RowIdentity, value);
            EnsureMutation(result);
            RefreshSelectedObjects(category.RowIdentity, null);
        }, "소문자 영문, 숫자, 밑줄을 사용합니다. 변경 시 연결된 모든 ID를 함께 갱신합니다.");
        AddEditableField("index", category.Index?.ToString(CultureInfo.InvariantCulture) ?? "", value =>
            category.Index = ParseNullableInt(value, "index"), "카테고리 정렬 순서입니다.");
        AddReadOnlyField("node_name", category.NodeName, "category key에서 자동 생성되는 로컬라이징 TID입니다.");

        var delete = CreateInspectorButton("Delete Category", new SolidColorBrush(Color.FromRgb(118, 48, 52)));
        delete.Click += (_, _) => DeleteSelectedCategory();
        InspectorPanel.Children.Add(delete);
    }

    private void RenderNodeInspector(ResearchNodeRow node)
    {
        AddInspectorTitle("Research Node");
        AddReadOnlyField("id", node.Id, "theme_id, category, column, row로 자동 생성됩니다.");
        AddEditableField("export_id", node.ExportId, value => node.ExportId = value,
            "이 노드 행의 Export ID입니다.");
        AddEditableField("theme_id", node.ThemeId, value =>
        {
            node.ThemeId = value;
            EnsureMutation(ResearchWorkbookService.TryMoveNode(_workbook!, node.RowIdentity, node.Column, node.Row));
            RefreshSelectedObjects(null, node.RowIdentity);
        }, "노드 ID와 효과 ID의 접두어입니다. 변경 시 참조를 함께 갱신합니다.");
        AddEditableField("category", node.Category, value =>
            EnsureMutation(ResearchWorkbookService.TryChangeNodeCategory(_workbook!, node.RowIdentity, value)),
            $"연결할 nexus_node_category ID입니다. 현재 카테고리: {string.Join(", ", _workbook!.Categories.Select(row => row.Category))}");
        AddReadOnlyField("category_name", node.CategoryName, "카테고리 이름 TID이며 수식으로 관리됩니다.");
        AddReadOnlyField("node_effect_desc", node.NodeEffectDesc, "노드 효과 설명 TID이며 수식으로 관리됩니다.");
        AddEditableField("image", node.Image, value => node.Image = value,
            "연구 노드에 표시할 이미지 리소스 키입니다.");
        AddEditableField("node_permission", node.NodePermission?.ToString(CultureInfo.InvariantCulture) ?? "", value =>
        {
            var permission = ParseNullableInt(value, "node_permission");
            if (permission is < 1 or > 5)
                throw new InvalidOperationException("node_permission은 1~5여야 합니다.");
            node.NodePermission = permission;
        }, "연구 단계입니다. 1~5를 사용하며 상단 필터와 permission band에 반영됩니다.");
        AddEditableField("active_item_id", node.ActiveItemId, value => node.ActiveItemId = value,
            "노드 활성화에 필요한 아이템 ID입니다.");
        AddEditableField("active_item_value", node.ActiveItemValue, value =>
        {
            node.ActiveItemValue = value;
            node.TotalRequiredHelper = ResearchWorkbookService.CalculateActiveItemTotal(value);
        },
            "단계별 필요 수량입니다. 여러 값은 세미콜론으로 구분합니다.");
        AddReadOnlyField("total required", node.TotalRequiredHelper?.ToString("G", CultureInfo.InvariantCulture) ?? "",
            "active_item_value의 합계를 계산하는 보조 칼럼입니다.");
        AddEditableField("active_step", node.ActiveStep?.ToString(CultureInfo.InvariantCulture) ?? "", value =>
            node.ActiveStep = ParseNullableInt(value, "active_step"), "활성화 단계 수입니다.");
        AddEditableField("column", node.Column.ToString(CultureInfo.InvariantCulture), value =>
        {
            var column = ParseRequiredInt(value, "column");
            EnsureMutation(ResearchWorkbookService.TryMoveNode(_workbook!, node.RowIdentity, column, node.Row));
            RefreshSelectedObjects(null, node.RowIdentity);
        }, "그래프의 X 위치입니다. 이동하면 노드 및 효과 ID와 모든 참조가 함께 바뀝니다.");
        AddEditableField("row", node.Row.ToString(CultureInfo.InvariantCulture), value =>
        {
            var row = ParseRequiredInt(value, "row");
            EnsureMutation(ResearchWorkbookService.TryMoveNode(_workbook!, node.RowIdentity, node.Column, row));
            RefreshSelectedObjects(null, node.RowIdentity);
        }, $"그래프의 Y lane입니다. 현재 DB lane은 {string.Join(", ", GetLaneChoices())}입니다.");

        for (var index = 0; index < 5; index++)
        {
            var conditionIndex = index;
            AddEditableField($"condition_node_{index + 1}", node.Conditions[index], value =>
            {
                node.SetCondition(conditionIndex, value);
                EnsureNoConditionErrors(node);
            }, "이 노드보다 앞서 열려 있어야 하는 연구 노드 ID입니다. 최대 5개입니다.");
        }
        AddReadOnlyField("nexus_effect_id", node.NexusEffectId, "대응 nexus_effect ID이며 노드 좌표에 따라 자동 생성됩니다.");

        var effect = _workbook?.FindEffect(node);
        AddInspectorSection("Linked Effect");
        if (effect is null)
        {
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = "연결된 nexus_effect 행이 없습니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(235, 105, 105)),
                Margin = new Thickness(0, 0, 0, 8)
            });
            var addEffect = CreateInspectorButton("Create Missing Effect", new SolidColorBrush(Color.FromRgb(56, 86, 63)));
            addEffect.Click += (_, _) => CreateMissingEffect(node);
            InspectorPanel.Children.Add(addEffect);
        }
        else
        {
            RenderEffectInspector(effect);
        }

        var delete = CreateInspectorButton("Delete Node", new SolidColorBrush(Color.FromRgb(118, 48, 52)));
        delete.Click += (_, _) => DeleteSelectedNodes();
        InspectorPanel.Children.Add(delete);
    }

    private void RenderEffectInspector(ResearchEffectRow effect)
    {
        AddReadOnlyField("effect id", effect.Id, "연구 노드 ID와 같은 좌표 규칙으로 자동 생성됩니다.");
        AddEditableField("effect export_id", effect.ExportId, value => effect.ExportId = value, "nexus_effect 행의 Export ID입니다.");
        AddEditableField("parent_effect", effect.ParentEffect, value => effect.ParentEffect = value, "상위 효과 ID입니다.");
        AddEditableField("group memo", effect.GroupMemo, value => effect.GroupMemo = value, "효과 그룹을 설명하는 메모입니다.");
        AddEditableField("memo", effect.Memo, value => effect.Memo = value, "효과의 용도와 좌표를 설명하는 메모입니다.", multiline: true);
        AddEditableField("type", effect.Type, value => effect.Type = value, "효과 처리 타입입니다. 기존 값 또는 엔진이 지원하는 값을 입력합니다.");
        AddEditableField("condition", effect.Condition, value => effect.Condition = value, "효과 적용 조건입니다.");
        AddEditableField("value", effect.ValueText, value => effect.ValueText = value, "효과 값입니다. 원본 셀 형식을 가능한 한 유지합니다.");
        AddEditableField("effect column 9", effect.Test, value => effect.Test = value, "nexus_effect의 9번째 데이터 칼럼입니다.");
        AddEditableField("effect column 10", effect.Extra, value => effect.Extra = value, "nexus_effect의 10번째 데이터 칼럼입니다.");
    }

    private void CreateMissingEffect(ResearchNodeRow node)
    {
        if (_workbook is null)
            return;
        PushUndo();
        var effect = new ResearchEffectRow
        {
            RowIdentity = $"effect:new:{Guid.NewGuid():N}",
            LinkedNodeRowIdentity = node.RowIdentity
        };
        effect.Id = node.NexusEffectId;
        effect.ExportId = node.ExportId;
        effect.GroupMemo = $"연구 {node.Category}";
        effect.Memo = $"연구 노드 효과 ({node.Category}/node {node.Column},{node.Row})";
        _workbook.Effects.Add(effect);
        RenderInspector();
        UpdateDirtyState();
    }

    private void AddInspectorTitle(string title)
    {
        InspectorPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = Brushes.White
        });
    }

    private void AddInspectorSection(string title)
    {
        InspectorPanel.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(68, 68, 68)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 13, 0, 8),
            Padding = new Thickness(0, 9, 0, 0),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(205, 205, 205))
            }
        });
    }

    private void AddReadOnlyField(string label, string value, string help)
    {
        AddFieldLabel(label, help);
        InspectorPanel.Children.Add(new TextBox
        {
            Text = value,
            IsReadOnly = true,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            Margin = new Thickness(0, 0, 0, 8)
        });
    }

    private void AddEditableField(string label, string value, Action<string> apply, string help, bool multiline = false)
    {
        AddFieldLabel(label, help);
        var box = new TextBox
        {
            Text = value,
            Margin = new Thickness(0, 0, 0, 8),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 58 : 28,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Tag = value
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (_suppressInspectorCommit || Equals(box.Tag, box.Text))
                return;
            CommitInspectorEdit(() => apply(box.Text));
        };
        InspectorPanel.Children.Add(box);
    }

    private void AddFieldLabel(string label, string help)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(185, 185, 185)) });
        var info = new Button
        {
            Content = "i",
            Width = 18,
            Height = 18,
            MinHeight = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(5, 0, 0, 0),
            ToolTip = help,
            FontSize = 10
        };
        info.Click += (_, _) => ThemedMessageBox.Show(help, label, MessageBoxButton.OK, MessageBoxImage.Information);
        row.Children.Add(info);
        InspectorPanel.Children.Add(row);
    }

    private static Button CreateInspectorButton(string text, Brush background) => new()
    {
        Content = text,
        Height = 32,
        Margin = new Thickness(0, 10, 0, 0),
        Background = background,
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(98, 98, 98))
    };

    private void CommitInspectorEdit(Action apply)
    {
        if (_workbook is null)
            return;
        var categoryIdentity = _selectedCategory?.RowIdentity;
        var nodeIdentity = _primaryNode?.RowIdentity;
        PushUndo();
        try
        {
            apply();
            RefreshSelectedObjects(categoryIdentity, nodeIdentity);
            PopulateCategories(_selectedCategory?.Category);
            RenderGraph();
            PopulateHierarchy();
            RenderInspector();
            UpdateDirtyState();
        }
        catch (Exception ex)
        {
            UndoWithoutRender();
            RefreshSelectedObjects(categoryIdentity, nodeIdentity);
            PopulateCategories(_selectedCategory?.Category);
            RenderGraph();
            PopulateHierarchy();
            RenderInspector();
            Log(ResearchConsoleSeverity.Error, ex.Message, _primaryNode?.Id ?? _selectedCategory?.Category ?? "");
        }
    }

    private void RefreshSelectedObjects(string? categoryRowIdentity, string? nodeRowIdentity)
    {
        if (_workbook is null)
            return;
        if (!string.IsNullOrWhiteSpace(categoryRowIdentity))
            _selectedCategory = _workbook.Categories.FirstOrDefault(category => Same(category.RowIdentity, categoryRowIdentity));
        if (!string.IsNullOrWhiteSpace(nodeRowIdentity))
        {
            _primaryNode = _workbook.Nodes.FirstOrDefault(node => Same(node.RowIdentity, nodeRowIdentity));
            if (_primaryNode is not null)
                _selectedCategory = _workbook.FindCategory(_primaryNode.Category);
        }
    }

    private void EnsureNoConditionErrors(ResearchNodeRow node)
    {
        if (_workbook is null)
            return;
        var error = ResearchWorkbookService.Validate(_workbook).FirstOrDefault(issue =>
            issue.Severity == ResearchValidationSeverity.Error
            && issue.Code.StartsWith("condition_", StringComparison.OrdinalIgnoreCase)
            && (Same(issue.RowIdentity, node.RowIdentity)
                || issue.Code.Equals("condition_cycle", StringComparison.OrdinalIgnoreCase)
                && issue.Message.Contains(node.Id, StringComparison.OrdinalIgnoreCase)));
        if (error is not null)
            throw new InvalidOperationException(error.Message);
    }

    private static void EnsureMutation(ResearchMutationResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
    }

    private static int ParseRequiredInt(string value, string field)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidOperationException($"{field}에는 1 이상의 정수를 입력하세요.");
        return parsed;
    }

    private static int? ParseNullableInt(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidOperationException($"{field}에는 정수를 입력하세요.");
        return parsed;
    }
}

public partial class ResearchEditorWindow
{
    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_paths is null)
        {
            await BeginInitialLoadAsync();
            return;
        }
        if (HasUnsavedChanges
            && ThemedMessageBox.Show("저장하지 않은 연구 변경을 버리고 DB를 다시 읽을까요?", "Reload research DB", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await LoadAsync(_paths);
    }

    private void FitAllButton_Click(object sender, RoutedEventArgs e) => FitAll();

    private async void PreferenceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ResearchPreferencesWindow(_paths) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPaths is null)
            return;

        var selectedPaths = dialog.SelectedPaths;
        if (_paths is not null && SamePath(_paths.OutSystemPath, selectedPaths.OutSystemPath)
                               && SamePath(_paths.EffectPath, selectedPaths.EffectPath))
            return;

        if (HasUnsavedChanges)
        {
            var decision = WorkspaceSwitchDialog.Request(this, "연구 DB 경로를 바꾸면 저장하지 않은 변경을 현재 DB에서 떠나게 됩니다.");
            if (decision == WorkspaceSwitchResult.Cancel)
                return;
            if (decision == WorkspaceSwitchResult.ExportAndSwitch && !ExportDirect())
                return;
        }

        await LoadAsync(selectedPaths);
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void EventEditorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveWorkspace())
            return;
        _workspaceSwitch?.Invoke(WorkspaceMode.Event);
    }

    public void PrepareForWorkspaceClose() => _allowClose = true;

    private bool CanLeaveWorkspace()
    {
        if (!HasUnsavedChanges)
            return true;
        var result = WorkspaceSwitchDialog.Request(this, "연구 편집기에 저장하지 않은 변경이 있습니다.");
        return result switch
        {
            WorkspaceSwitchResult.DiscardChanges => true,
            WorkspaceSwitchResult.ExportAndSwitch => ExportDirect(),
            _ => false
        };
    }

    private void ClosePaneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;
        var menuItem = tag switch
        {
            "category" => CategoryPaneMenuItem,
            "scene" => ScenePaneMenuItem,
            "hierarchy" => HierarchyPaneMenuItem,
            "inspector" => InspectorPaneMenuItem,
            "console" => ConsolePaneMenuItem,
            _ => null
        };
        if (menuItem is null)
            return;
        menuItem.IsChecked = false;
        PaneMenuItem_Click(menuItem, e);
        e.Handled = true;
    }

    private void PaneMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } item)
            return;
        switch (tag)
        {
            case "category":
                CategoryColumn.Width = item.IsChecked ? new GridLength(Math.Max(170, _settings.CategoryPaneWidth)) : new GridLength(0);
                CategoryPane.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                CategorySplitter.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                break;
            case "scene":
                ScenePane.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                break;
            case "hierarchy":
                HierarchyColumn.Width = item.IsChecked ? new GridLength(Math.Max(130, _settings.HierarchyPaneWidth)) : new GridLength(0);
                HierarchyPane.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                break;
            case "inspector":
                InspectorPane.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                break;
            case "console":
                ConsoleRow.Height = item.IsChecked ? new GridLength(Math.Max(72, _settings.ConsoleHeight)) : new GridLength(0);
                ConsolePane.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                ConsoleSplitter.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
        SaveLayoutSettings();
    }

    private void LayoutSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => SaveLayoutSettings();

    private void RestoreLayout()
    {
        CategoryColumn.Width = new GridLength(Math.Max(170, _settings.CategoryPaneWidth));
        RightColumn.Width = new GridLength(Math.Max(280, _settings.RightPaneWidth));
        HierarchyColumn.Width = new GridLength(Math.Max(130, _settings.HierarchyPaneWidth));
        ConsoleRow.Height = new GridLength(Math.Max(72, _settings.ConsoleHeight));
        GraphScale.ScaleX = GraphScale.ScaleY = Math.Clamp(_settings.Zoom, 0.18, 2.4);
        GraphTranslate.X = _settings.PanX;
        GraphTranslate.Y = _settings.PanY;
        _permissionFilter = Math.Clamp(_settings.PermissionFilter, 0, 5);
    }

    private void SaveViewSettings()
    {
        _settings.Zoom = GraphScale.ScaleX;
        _settings.PanX = GraphTranslate.X;
        _settings.PanY = GraphTranslate.Y;
        ResearchPathResolver.SaveSettings(_settings);
    }

    private void SaveLayoutSettings()
    {
        if (CategoryColumn.ActualWidth > 20)
            _settings.CategoryPaneWidth = CategoryColumn.ActualWidth;
        if (RightColumn.ActualWidth > 20)
            _settings.RightPaneWidth = RightColumn.ActualWidth;
        if (HierarchyColumn.ActualWidth > 20)
            _settings.HierarchyPaneWidth = HierarchyColumn.ActualWidth;
        if (ConsoleRow.ActualHeight > 20)
            _settings.ConsoleHeight = ConsoleRow.ActualHeight;
        SaveViewSettings();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase or PasswordBox
            || Keyboard.FocusedElement is TextBoxBase or PasswordBox
            || Keyboard.FocusedElement is ComboBox { IsEditable: true })
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Z)
            {
                Undo();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C)
            {
                CopySelectedNodes();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.V)
            {
                PasteNodes();
                e.Handled = true;
                return;
            }
        }
        if (e.Key is Key.Delete or Key.Back)
        {
            DeleteSelectedNodes();
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            FocusSelectedNode();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveLayoutSettings();
        if (_allowClose || !HasUnsavedChanges)
            return;
        var result = WorkspaceSwitchDialog.Request(this, "연구 편집기에 저장하지 않은 변경이 있습니다.");
        if (result == WorkspaceSwitchResult.Cancel)
        {
            e.Cancel = true;
            return;
        }
        if (result == WorkspaceSwitchResult.ExportAndSwitch && !ExportDirect())
            e.Cancel = true;
    }

    private void PushUndo()
    {
        if (_workbook is null)
            return;
        _undoStack.Push(new UndoState(
            _workbook.DeepClone(includeOriginalSnapshot: true),
            _selectedCategory?.Category,
            _selectedNodeIdentities.ToArray()));
        while (_undoStack.Count > 80)
        {
            var retained = _undoStack.Take(80).Reverse().ToArray();
            _undoStack.Clear();
            foreach (var state in retained)
                _undoStack.Push(state);
        }
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
            return;
        var state = _undoStack.Pop();
        _workbook = state.Workbook;
        _selectedCategory = _workbook.FindCategory(state.Category);
        _selectedNodeIdentities.Clear();
        foreach (var identity in state.SelectedNodeIds)
            _selectedNodeIdentities.Add(identity);
        _primaryNode = _workbook.Nodes.FirstOrDefault(node => _selectedNodeIdentities.Contains(node.RowIdentity));
        PopulateCategories(_selectedCategory?.Category);
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info, "Undo");
    }

    private void UndoWithoutRender()
    {
        if (_undoStack.Count == 0)
            return;
        var state = _undoStack.Pop();
        _workbook = state.Workbook;
        _selectedCategory = _workbook.FindCategory(state.Category);
        _selectedNodeIdentities.Clear();
        foreach (var identity in state.SelectedNodeIds)
            _selectedNodeIdentities.Add(identity);
        _primaryNode = _workbook.Nodes.FirstOrDefault(node => _selectedNodeIdentities.Contains(node.RowIdentity));
    }

    private void CopySelectedNodes()
    {
        if (_workbook is null)
            return;
        _nodeClipboard.Clear();
        foreach (var node in _workbook.Nodes.Where(node => _selectedNodeIdentities.Contains(node.RowIdentity)))
            _nodeClipboard.Add(new ResearchNodeClipboardItem(node.DeepClone(), _workbook.FindEffect(node)?.DeepClone()));
        if (_nodeClipboard.Count > 0)
            Log(ResearchConsoleSeverity.Info, $"연구 노드 {_nodeClipboard.Count:N0}개를 복사했습니다.");
    }

    private void PasteNodes()
    {
        if (_workbook is null || _selectedCategory is null || _nodeClipboard.Count == 0)
            return;
        PushUndo();
        var createdIdentities = new List<string>();
        try
        {
            foreach (var item in _nodeClipboard)
            {
                var column = item.Node.Column + 1;
                var row = item.Node.Row;
                while (_workbook.Nodes.Any(node => Same(node.Category, _selectedCategory.Category) && node.Column == column && node.Row == row))
                    column++;
                var created = ResearchWorkbookService.CreateNode(_workbook, _selectedCategory.Category, column, row, item.Node.ExportId, item.Node.ThemeId);
                CopyEditableNodeFields(item.Node, created.Node);
                created.Node.Column = column;
                created.Node.Row = row;
                created.Node.Id = ResearchWorkbookService.GenerateNodeId(created.Node.ThemeId, created.Node.Category, column, row);
                created.Node.NexusEffectId = ResearchWorkbookService.GenerateEffectId(created.Node.ThemeId, created.Node.Category, column, row);
                ClearConditions(created.Node);
                if (item.Effect is not null)
                    CopyEffectFields(item.Effect, created.Effect, created.Node.NexusEffectId);
                createdIdentities.Add(created.Node.RowIdentity);
            }
        }
        catch (Exception ex)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Error, $"붙여넣지 못했습니다: {ex.Message}");
            return;
        }
        _selectedNodeIdentities.Clear();
        foreach (var identity in createdIdentities)
            _selectedNodeIdentities.Add(identity);
        _primaryNode = _workbook.Nodes.FirstOrDefault(node => createdIdentities.Contains(node.RowIdentity));
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        UpdateDirtyState();
    }

    private void DeleteSelectedNodes()
    {
        if (_workbook is null || _selectedNodeIdentities.Count == 0)
            return;
        if (ThemedMessageBox.Show($"선택한 연구 노드 {_selectedNodeIdentities.Count:N0}개를 삭제할까요? 참조 중인 조건선도 함께 제거됩니다.",
                "Delete research nodes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        PushUndo();
        foreach (var identity in _selectedNodeIdentities.ToArray())
        {
            var result = ResearchWorkbookService.TryDeleteNode(_workbook, identity, removeConditionReferences: true);
            if (!result.Success)
            {
                UndoWithoutRender();
                Log(ResearchConsoleSeverity.Error, result.Message);
                return;
            }
        }
        _selectedNodeIdentities.Clear();
        _primaryNode = null;
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        UpdateDirtyState();
    }

    private static void ClearConditions(ResearchNodeRow node)
    {
        for (var index = 0; index < 5; index++)
            node.SetCondition(index, "");
    }

    private static void CopyEditableNodeFields(ResearchNodeRow source, ResearchNodeRow target)
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

    private static void CopyEffectFields(ResearchEffectRow source, ResearchEffectRow target, string newId)
    {
        target.Id = newId;
        target.ExportId = source.ExportId;
        target.ParentEffect = source.ParentEffect;
        target.Type = source.Type;
        target.Condition = source.Condition;
        target.ValueCell = source.ValueCell.DeepClone();
        target.TestCell = source.TestCell.DeepClone();
        target.ExtraCell = source.ExtraCell.DeepClone();
    }

    private void FocusSelectedNode()
    {
        if (_primaryNode is null || !_nodeVisuals.TryGetValue(_primaryNode.RowIdentity, out var visual))
            return;
        var scale = Math.Clamp(GraphScale.ScaleX, 0.65, 1.25);
        GraphScale.ScaleX = GraphScale.ScaleY = scale;
        GraphTranslate.X = SceneViewport.ActualWidth / 2 - (Canvas.GetLeft(visual) + NodeWidth / 2) * scale;
        GraphTranslate.Y = SceneViewport.ActualHeight / 2 - (Canvas.GetTop(visual) + NodeHeight / 2) * scale;
        PulseNode(visual);
        SaveViewSettings();
    }

    private static void PulseNode(Border visual)
    {
        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0.45,
            Duration = TimeSpan.FromMilliseconds(350),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2)
        };
        visual.BeginAnimation(OpacityProperty, animation);
    }
}
