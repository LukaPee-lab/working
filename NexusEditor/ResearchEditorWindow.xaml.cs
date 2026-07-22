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
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace NexusEditor;

public partial class ResearchEditorWindow : Window
{
    private const double NodeWidth = 128;
    private const double NodeHeight = 140;
    private const double PinDiameter = 14;
    private const double PinDropRadius = 26;
    private const double ConnectorWidth = 124;
    private const double ColumnSpacing = NodeWidth + ConnectorWidth;
    private const double GraphLeft = 86;
    private const double LaneSpacing = 158;
    private const double RowStep = LaneSpacing / 2;
    private const double GraphHeight = 1480;
    private const double LaneTop = GraphHeight / 2 - NodeHeight / 2;
    private static readonly int[] StructuredRows = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly Color[] PermissionColors =
    [
        Color.FromRgb(43, 82, 106),
        Color.FromRgb(61, 86, 77),
        Color.FromRgb(92, 75, 45),
        Color.FromRgb(88, 62, 89),
        Color.FromRgb(102, 63, 63),
        Color.FromRgb(54, 78, 112),
        Color.FromRgb(70, 91, 55),
        Color.FromRgb(105, 76, 50)
    ];

    private readonly Action<WorkspaceMode>? _workspaceSwitch;
    private readonly Dictionary<string, Border> _nodeVisuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ellipse> _inputPinVisuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ellipse> _outputPinVisuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Border> _stepBandVisuals = [];
    private readonly Dictionary<int, Border> _stepHeaderVisuals = [];
    private readonly HashSet<string> _selectedNodeIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<int> _selectedStepColumns = [];
    private readonly SortedSet<int> _stepMarqueeBaseSelection = [];
    private readonly Stack<UndoState> _undoStack = [];
    private readonly List<ResearchConsoleEntry> _consoleEntries = [];
    private readonly List<ResearchNodeClipboardItem> _nodeClipboard = [];
    private readonly Dictionary<string, ImageSource?> _researchAssetCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(BitmapSource Source, Int32Rect Crop), BitmapSource> _nineSliceCropCache = [];
    private readonly HashSet<string> _reportedMissingAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _validationTimer;
    private long _consoleSequence;

    private ResearchEditorSettings _settings;
    private ResearchWorkbookPaths? _paths;
    private ResearchWorkbookContext? _workbook;
    private ResearchCategoryRow? _selectedCategory;
    private ResearchNodeRow? _primaryNode;
    private ResearchStepRangeSnapshot? _stepClipboard;
    private string? _researchImageRoot;
    private bool _isLoading;
    private bool _suppressCategorySelection;
    private bool _suppressInspectorCommit;
    private bool _allowClose;
    private int _permissionFilter;
    private bool _quickNodePopupRequested;
    private bool _quickEditorShowsEffect;
    private bool _updatingPermissionFilters;

    private bool _isPanning;
    private bool _panGestureMoved;
    private Point _panScreenStart;
    private double _panXStart;
    private double _panYStart;
    private ResearchNodeRow? _pendingContextNode;
    private Border? _pendingContextCard;
    private ResearchPinTag? _pendingContextPin;
    private string? _linkDragSourceIdentity;
    private ShapePath? _linkPreviewPath;
    private DispatcherOperation? _pendingHierarchyRefresh;
    private ResearchSlotTag? _pendingEmptySlot;
    private Point _pendingEmptySlotStartScreen;
    private bool _pendingEmptySlotAdditive;
    private string? _pendingNodeDragAnchorIdentity;
    private readonly Dictionary<string, int> _pendingNodeDragRows = new(StringComparer.OrdinalIgnoreCase);
    private Point _pendingNodeDragStartScreen;
    private int _pendingNodeDragDeltaRow;
    private bool _nodeDragMoved;
    private bool _isStepMarqueeSelecting;
    private bool _stepMarqueeMoved;
    private bool _stepMarqueeAdditive;
    private int? _stepMarqueeClickedColumn;
    private Point _stepMarqueeStartScreen;
    private Point _stepMarqueeStartWorld;

    public ResearchEditorWindow(Action<WorkspaceMode>? workspaceSwitch = null)
    {
        InitializeComponent();
        _validationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _validationTimer.Tick += (_, _) =>
        {
            _validationTimer.Stop();
            RunValidation(logSuccess: false);
        };
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
            _selectedStepColumns.Clear();
            _primaryNode = null;
            _stepClipboard = null;
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
        CloseQuickNodePopup();
        _selectedCategory = category;
        _settings.LastCategory = category?.Category;
        _selectedNodeIdentities.Clear();
        _selectedStepColumns.Clear();
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
        CancelLinkDrag(releaseCapture: true);
        CloseQuickNodePopup(clearRequest: false);
        BandCanvas.Children.Clear();
        LinkCanvas.Children.Clear();
        NodeCanvas.Children.Clear();
        _nodeVisuals.Clear();
        _inputPinVisuals.Clear();
        _outputPinVisuals.Clear();
        _stepBandVisuals.Clear();
        _stepHeaderVisuals.Clear();

        if (_workbook is null || _selectedCategory is null)
        {
            SceneStatusText.Text = "Select a category";
            RefreshPermissionFilterButtons();
            return;
        }

        RefreshPermissionFilterButtons();

        var categoryNodes = _workbook.Nodes
            .Where(node => Same(node.Category, _selectedCategory.Category))
            .OrderBy(node => node.Column)
            .ThenBy(node => node.Row)
            .ToList();
        var nodes = VisibleNodes().ToList();
        ResizeGraphSurface(categoryNodes);
        DrawStructuredBands(nodes);
        DrawStepBands(nodes);
        DrawPermissionRegions(categoryNodes);
        DrawStepHeaders(nodes);
        DrawAppendStepButton(categoryNodes);
        DrawEmptySlotTargets(categoryNodes, nodes);
        foreach (var node in nodes)
            CreateNodeVisual(node);
        DrawAllLinks();
        ApplySelectionVisuals();
        RefreshQuickNodePopup();

        var allCount = _workbook.Nodes.Count(node => Same(node.Category, _selectedCategory.Category));
        SceneStatusText.Text = $"{_selectedCategory.Category} / {nodes.Count:N0} of {allCount:N0} nodes / Wheel: Zoom / RMB: Pan";
        if (fitGraph)
            Dispatcher.BeginInvoke(FitOpeningView);
    }

    private void ResizeGraphSurface(IReadOnlyCollection<ResearchNodeRow> nodes)
    {
        var maxColumn = nodes.Select(node => node.Column).DefaultIfEmpty(4).Max();
        var width = Math.Max(1600, WorldX(maxColumn) + NodeWidth + 250);
        GraphCanvas.Width = width;
        GraphCanvas.Height = GraphHeight;
        BandCanvas.Width = width;
        BandCanvas.Height = GraphHeight;
        LinkCanvas.Width = width;
        LinkCanvas.Height = GraphHeight;
        NodeCanvas.Width = width;
        NodeCanvas.Height = GraphHeight;
    }

    private void DrawStructuredBands(IReadOnlyCollection<ResearchNodeRow> nodes)
    {
        for (var laneIndex = 0; laneIndex < StructuredRows.Length; laneIndex++)
        {
            var band = new Border
            {
                Width = GraphCanvas.Width,
                Height = RowStep,
                Background = new SolidColorBrush(laneIndex % 2 == 0
                    ? Color.FromRgb(30, 30, 30)
                    : Color.FromRgb(27, 27, 27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(43, 43, 43)),
                BorderThickness = new Thickness(0, 1, 0, 1),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"ROW {StructuredRows[laneIndex]}",
                    Foreground = new SolidColorBrush(Color.FromRgb(104, 104, 104)),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(14, 7, 0, 0)
                }
            };
            Canvas.SetLeft(band, 0);
            Canvas.SetTop(band, WorldY(StructuredRows[laneIndex]) + NodeHeight / 2 - RowStep / 2);
            BandCanvas.Children.Add(band);
        }

    }

    private void DrawStepBands(IReadOnlyCollection<ResearchNodeRow> nodes)
    {
        foreach (var column in nodes.Select(node => node.Column).Distinct().OrderBy(value => value))
        {
            var band = new Border
            {
                Width = NodeWidth + 18,
                Height = GraphHeight - 48,
                Background = StepBandBrush(column, selected: false, copied: false),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 76, 76, 76)),
                BorderThickness = new Thickness(1, 0, 1, 0),
                Tag = new ResearchStepTag(column),
                ToolTip = $"STEP {column:000} block - click to select, Ctrl+C to copy"
            };
            band.MouseLeftButtonDown += StepBlock_MouseLeftButtonDown;
            Canvas.SetLeft(band, WorldX(column) - 9);
            Canvas.SetTop(band, 43);
            Panel.SetZIndex(band, 2);
            BandCanvas.Children.Add(band);
            _stepBandVisuals[column] = band;
        }
    }

    private static Brush StepBandBrush(int column, bool selected, bool copied)
    {
        if (selected)
            return new SolidColorBrush(Color.FromArgb(76, 51, 112, 142));
        if (copied)
            return new SolidColorBrush(Color.FromArgb(58, 137, 105, 42));
        return new SolidColorBrush(column % 2 == 0
            ? Color.FromArgb(34, 78, 91, 99)
            : Color.FromArgb(20, 58, 66, 72));
    }

    private void DrawPermissionRegions(IReadOnlyCollection<ResearchNodeRow> nodes)
    {
        if (_selectedCategory is null || nodes.Count == 0)
            return;

        var columns = nodes
            .GroupBy(node => node.Column)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                Column = group.Key,
                Permission = group.GroupBy(node => node.NodePermission ?? 0)
                    .OrderByDescending(permissionGroup => permissionGroup.Count())
                    .ThenBy(permissionGroup => permissionGroup.Key)
                    .First().Key
            })
            .ToList();
        var regions = new List<(int Permission, int FirstColumn, int LastColumn)>();
        foreach (var column in columns)
        {
            if (regions.Count > 0
                && regions[^1].Permission == column.Permission
                && regions[^1].LastColumn + 1 == column.Column)
            {
                var current = regions[^1];
                regions[^1] = (current.Permission, current.FirstColumn, column.Column);
            }
            else
            {
                regions.Add((column.Permission, column.Column, column.Column));
            }
        }

        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            var width = WorldX(region.LastColumn) - WorldX(region.FirstColumn) + NodeWidth;
            var strip = new Border
            {
                Width = width,
                Height = 32,
                Background = NodeHeaderBrush(region.Permission),
                BorderBrush = new SolidColorBrush(Color.FromRgb(96, 96, 96)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Opacity = 0.9
            };
            var content = new Grid { Margin = new Thickness(8, 0, 4, 0) };
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(new TextBlock
            {
                Text = region.Permission > 0 ? $"PERMISSION {region.Permission}" : "PERMISSION NOT SET",
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });

            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (index < regions.Count - 1 && regions[index + 1].Permission == region.Permission + 1)
            {
                controls.Children.Add(CreatePermissionButton("◀", "현재 구역의 마지막 STEP을 다음 권한 구역으로 옮깁니다.",
                    () => ChangePermissionBoundary(region.Permission, -1)));
                controls.Children.Add(CreatePermissionButton("▶", "다음 권한 구역의 첫 STEP을 현재 구역으로 가져옵니다.",
                    () => ChangePermissionBoundary(region.Permission, 1)));
            }
            if (index == regions.Count - 1 && region.Permission > 1)
            {
                controls.Children.Add(CreatePermissionButton("−", "마지막 권한 구역을 앞 구역에 합칩니다.", RemovePermissionRegion));
            }
            Grid.SetColumn(controls, 1);
            content.Children.Add(controls);
            strip.Child = content;
            Canvas.SetLeft(strip, WorldX(region.FirstColumn));
            Canvas.SetTop(strip, 8);
            BandCanvas.Children.Add(strip);
        }

        var lastColumn = columns.Max(column => column.Column);
        var addButton = new Button
        {
            Content = "+ 권한 구역",
            Width = 108,
            Height = 32,
            Padding = new Thickness(5, 2, 5, 2),
            FontSize = 10.5,
            ToolTip = "마지막 권한 구역의 끝 STEP을 떼어 새 권한 구역을 만듭니다."
        };
        addButton.Click += (_, _) => AddPermissionRegion();
        Canvas.SetLeft(addButton, WorldX(lastColumn) + NodeWidth + 12);
        Canvas.SetTop(addButton, 8);
        BandCanvas.Children.Add(addButton);
    }

    private static Button CreatePermissionButton(string text, string toolTip, Action action)
    {
        var button = new Button
        {
            Content = text,
            Width = 25,
            Height = 23,
            MinHeight = 23,
            Padding = new Thickness(0),
            Margin = new Thickness(3, 0, 0, 0),
            FontSize = 11,
            ToolTip = toolTip
        };
        button.Click += (_, eventArgs) =>
        {
            action();
            eventArgs.Handled = true;
        };
        return button;
    }

    private void DrawStepHeaders(IReadOnlyCollection<ResearchNodeRow> nodes)
    {
        foreach (var columnGroup in nodes.GroupBy(node => node.Column).OrderBy(group => group.Key))
        {
            var column = columnGroup.Key;
            var count = columnGroup.Count();
            var permission = columnGroup.Select(node => node.NodePermission).FirstOrDefault(value => value is not null) ?? 0;
            var header = new Border
            {
                Width = NodeWidth,
                Height = 58,
                Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(73, 73, 73)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Tag = new ResearchStepTag(column),
                ToolTip = $"STEP {column:000} block - click to select, Ctrl+C to copy"
            };
            header.MouseLeftButtonDown += StepBlock_MouseLeftButtonDown;
            var root = new Grid { Margin = new Thickness(6, 4, 6, 5) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            header.Child = root;

            var label = new Grid();
            label.ColumnDefinitions.Add(new ColumnDefinition());
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            label.Children.Add(new TextBlock
            {
                Text = $"STEP {column:000}",
                Foreground = new SolidColorBrush(Color.FromRgb(190, 210, 224)),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            var permissionText = new TextBlock
            {
                Text = $"P{permission}",
                Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)),
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(permissionText, 1);
            label.Children.Add(permissionText);
            root.Children.Add(label);

            var counter = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            counter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            counter.ColumnDefinitions.Add(new ColumnDefinition());
            counter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            counter.Children.Add(CreateStepCountButton("-", column, -1, count > 1));
            var countText = new TextBlock
            {
                Text = $"{count} NODES",
                Foreground = Brushes.White,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(countText, 1);
            counter.Children.Add(countText);
            var increase = CreateStepCountButton("+", column, 1,
                count < ResearchWorkbookService.MaxNodesPerStep);
            Grid.SetColumn(increase, 2);
            counter.Children.Add(increase);
            Grid.SetRow(counter, 1);
            root.Children.Add(counter);

            Canvas.SetLeft(header, WorldX(column));
            Canvas.SetTop(header, 48);
            Panel.SetZIndex(header, 20);
            NodeCanvas.Children.Add(header);
            _stepHeaderVisuals[column] = header;
        }
    }

    private void DrawAppendStepButton(IReadOnlyCollection<ResearchNodeRow> nodes)
    {
        if (_selectedCategory is null || nodes.Count == 0)
            return;

        var lastColumn = nodes.Max(node => node.Column);
        var button = new Button
        {
            Content = "+ STEP",
            Width = 94,
            Height = 58,
            Padding = new Thickness(7, 3, 7, 3),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            ToolTip = "맨 끝에 연구 STEP을 하나 추가합니다."
        };
        button.Click += AppendStepButton_Click;
        Canvas.SetLeft(button, WorldX(lastColumn) + NodeWidth + 20);
        Canvas.SetTop(button, 48);
        Panel.SetZIndex(button, 20);
        NodeCanvas.Children.Add(button);
    }

    private Button CreateStepCountButton(string text, int column, int delta, bool enabled)
    {
        var button = new Button
        {
            Content = text,
            Tag = (column, delta),
            IsEnabled = enabled,
            Width = 26,
            Height = 22,
            MinHeight = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(1),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            ToolTip = delta > 0 ? "Add one node to this step" : "Remove one node from this step"
        };
        button.Click += StepCountButton_Click;
        return button;
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
        card.MouseRightButtonDown += Node_MouseRightButtonDown;

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

        var grid = new Grid { Margin = new Thickness(6, 5, 6, 5) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(21) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
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
                Width = 76,
                Height = 76,
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
            Text = $"x{node.ActiveStep ?? 0}",
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 1)
        };
        Grid.SetRow(count, 2);
        grid.Children.Add(count);

        Canvas.SetLeft(card, WorldX(node.Column));
        Canvas.SetTop(card, WorldY(node.Row));
        Panel.SetZIndex(card, 10);
        NodeCanvas.Children.Add(card);
        _nodeVisuals[node.RowIdentity] = card;
        CreateNodePin(node, isOutput: false);
        CreateNodePin(node, isOutput: true);
    }

    private void CreateNodePin(ResearchNodeRow node, bool isOutput)
    {
        var pin = new Ellipse
        {
            Width = PinDiameter,
            Height = PinDiameter,
            Fill = new SolidColorBrush(isOutput
                ? Color.FromRgb(70, 196, 132)
                : Color.FromRgb(218, 228, 250)),
            Stroke = new SolidColorBrush(Color.FromRgb(19, 22, 27)),
            StrokeThickness = 1.5,
            Cursor = Cursors.Cross,
            Tag = new ResearchPinTag(node.RowIdentity, isOutput),
            ToolTip = isOutput
                ? "출력 핀: 다음 STEP의 입력 핀으로 드래그하여 연결 / 우클릭하여 출력 연결 해제"
                : "입력 핀: 이전 STEP의 출력 핀을 여기에 연결 / 우클릭하여 입력 연결 해제"
        };
        pin.MouseLeftButtonDown += Pin_MouseLeftButtonDown;
        pin.MouseRightButtonDown += Pin_MouseRightButtonDown;
        Canvas.SetLeft(pin, WorldX(node.Column) + (isOutput ? NodeWidth : 0) - PinDiameter / 2);
        Canvas.SetTop(pin, WorldY(node.Row) + NodeHeight / 2 - PinDiameter / 2);
        Panel.SetZIndex(pin, 30);
        NodeCanvas.Children.Add(pin);
        (isOutput ? _outputPinVisuals : _inputPinVisuals)[node.RowIdentity] = pin;
    }

    private void DrawEmptySlotTargets(
        IReadOnlyCollection<ResearchNodeRow> allCategoryNodes,
        IReadOnlyCollection<ResearchNodeRow> visibleNodes)
    {
        var occupied = allCategoryNodes
            .Select(node => (node.Column, node.Row))
            .ToHashSet();
        var countsByColumn = allCategoryNodes
            .GroupBy(node => node.Column)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var column in visibleNodes.Select(node => node.Column).Distinct().OrderBy(value => value))
        {
            if (countsByColumn.GetValueOrDefault(column) >= ResearchWorkbookService.MaxNodesPerStep)
                continue;
            foreach (var row in StructuredRows)
            {
                if (occupied.Contains((column, row)))
                    continue;

                var hint = new TextBlock
                {
                    Text = "+ 연구 노드",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 181, 204)),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0
                };
                var slot = new Border
                {
                    Width = NodeWidth,
                    Height = NodeHeight,
                    Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Tag = new ResearchSlotTag(column, row),
                    ToolTip = $"STEP {column:000} / row {row}: 좌클릭하여 연구 노드 추가 / 드래그하여 STEP 선택",
                    Child = hint
                };
                slot.MouseEnter += (_, _) =>
                {
                    slot.BorderBrush = new SolidColorBrush(Color.FromRgb(66, 112, 138));
                    slot.Background = new SolidColorBrush(Color.FromArgb(34, 50, 87, 108));
                    hint.Opacity = 0.85;
                };
                slot.MouseLeave += (_, _) =>
                {
                    slot.BorderBrush = Brushes.Transparent;
                    slot.Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
                    hint.Opacity = 0;
                };
                slot.MouseLeftButtonDown += EmptySlot_MouseLeftButtonDown;
                Canvas.SetLeft(slot, WorldX(column));
                Canvas.SetTop(slot, WorldY(row));
                Panel.SetZIndex(slot, 4);
                NodeCanvas.Children.Add(slot);
            }
        }
    }

    private void RefreshEmptySlotTargets()
    {
        if (_workbook is null || _selectedCategory is null)
            return;

        var oldSlots = NodeCanvas.Children
            .OfType<FrameworkElement>()
            .Where(element => element.Tag is ResearchSlotTag)
            .ToList();
        foreach (var slot in oldSlots)
            NodeCanvas.Children.Remove(slot);

        var categoryNodes = _workbook.Nodes
            .Where(node => Same(node.Category, _selectedCategory.Category))
            .ToList();
        DrawEmptySlotTargets(categoryNodes, VisibleNodes().ToList());
    }

    private void RefreshStepHeaderCount(int column)
    {
        if (_workbook is null || _selectedCategory is null
            || !_stepHeaderVisuals.TryGetValue(column, out var header)
            || header.Child is not Grid root)
        {
            return;
        }

        var count = VisibleNodes().Count(node => node.Column == column);
        var counter = root.Children
            .OfType<Grid>()
            .FirstOrDefault(element => Grid.GetRow(element) == 1);
        if (counter is null)
            return;

        var countText = counter.Children.OfType<TextBlock>().FirstOrDefault();
        if (countText is not null)
            countText.Text = $"{count} NODES";
        foreach (var button in counter.Children.OfType<Button>())
        {
            if (button.Tag is not ValueTuple<int, int> change)
                continue;
            button.IsEnabled = change.Item2 > 0
                ? count < ResearchWorkbookService.MaxNodesPerStep
                : count > 1;
        }
    }

    private void RemoveNodeVisual(string rowIdentity)
    {
        if (_nodeVisuals.Remove(rowIdentity, out var card))
            NodeCanvas.Children.Remove(card);
        if (_inputPinVisuals.Remove(rowIdentity, out var inputPin))
            NodeCanvas.Children.Remove(inputPin);
        if (_outputPinVisuals.Remove(rowIdentity, out var outputPin))
            NodeCanvas.Children.Remove(outputPin);
    }

    private void UpdateSceneStatus()
    {
        if (_workbook is null || _selectedCategory is null)
            return;
        var visibleCount = VisibleNodes().Count();
        var allCount = _workbook.Nodes.Count(node => Same(node.Category, _selectedCategory.Category));
        SceneStatusText.Text = $"{_selectedCategory.Category} / {visibleCount:N0} of {allCount:N0} nodes / Wheel: Zoom / RMB: Pan";
    }

    private void RefreshResearchAssetRoot()
    {
        _researchAssetCache.Clear();
        _nineSliceCropCache.Clear();
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
                var connector = CreateResourceConnector(source, target);
                if (connector is null)
                    continue;
                connector.Tag = new ResearchLinkTag(source.Id, target.RowIdentity);
                var menu = new ContextMenu();
                var disconnect = new MenuItem { Header = "조건 연결 해제" };
                disconnect.Click += (_, _) => DisconnectLink(source, target);
                menu.Items.Add(disconnect);
                connector.ContextMenu = menu;
                LinkCanvas.Children.Add(connector);
            }
        }
    }

    private FrameworkElement? CreateResourceConnector(ResearchNodeRow source, ResearchNodeRow target)
    {
        if (!_nodeVisuals.ContainsKey(source.RowIdentity) || !_nodeVisuals.ContainsKey(target.RowIdentity))
            return null;
        var from = GetOutputPoint(source);
        var to = GetInputPoint(target);
        var rowDistance = Math.Abs(Array.IndexOf(StructuredRows, source.Row) - Array.IndexOf(StructuredRows, target.Row));
        var assetKey = to.Y switch
        {
            _ when Math.Abs(to.Y - from.Y) < 1 => "roadmap_line_s_5",
            _ when to.Y > from.Y && rowDistance <= 1 => "roadmap_line_s_1",
            _ when to.Y < from.Y && rowDistance <= 1 => "roadmap_line_s_2",
            _ when to.Y > from.Y => "roadmap_line_l_2",
            _ => "roadmap_line_l_1"
        };
        var sourceImage = LoadResearchAsset(assetKey, reportMissing: true);
        if (sourceImage is null)
            return null;

        var verticalDistance = Math.Abs(to.Y - from.Y);
        var width = Math.Max(ConnectorWidth, to.X - from.X);
        var height = Math.Max(12, verticalDistance + 12);
        FrameworkElement connector;
        if (sourceImage is BitmapSource bitmapSource)
        {
            connector = assetKey == "roadmap_line_s_5"
                ? CreateHorizontalNineSlice(bitmapSource, width)
                : CreateBentNineSlice(bitmapSource, assetKey, width, height);
        }
        else
        {
            connector = new Image
            {
                Source = sourceImage,
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };
        }
        connector.Cursor = Cursors.Hand;
        connector.ToolTip = $"{source.Id} -> {target.Id}\n우클릭하여 조건 연결 해제";
        Canvas.SetLeft(connector, from.X);
        Canvas.SetTop(connector, Math.Min(from.Y, to.Y) - 6);
        return connector;
    }

    private FrameworkElement CreateHorizontalNineSlice(BitmapSource source, double width)
    {
        var cap = Math.Min(8, Math.Max(1, source.PixelWidth / 3));
        var grid = new Grid
        {
            Width = width,
            Height = source.PixelHeight,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            ClipToBounds = true
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cap) });
        AddNineSliceImage(grid, source, new Int32Rect(0, 0, cap, source.PixelHeight), 0, 0);
        AddNineSliceImage(grid, source, new Int32Rect(cap, 0, source.PixelWidth - cap * 2, source.PixelHeight), 0, 1);
        AddNineSliceImage(grid, source, new Int32Rect(source.PixelWidth - cap, 0, cap, source.PixelHeight), 0, 2);
        return grid;
    }

    private FrameworkElement CreateBentNineSlice(BitmapSource source, string assetKey, double width, double height)
    {
        var longConnector = assetKey.Contains("_l_", StringComparison.OrdinalIgnoreCase);
        var spineStart = Math.Min(source.PixelWidth - 2, longConnector ? 56 : 49);
        var spineWidth = Math.Min(source.PixelWidth - spineStart, longConnector ? 12 : 11);
        var rightWidth = source.PixelWidth - spineStart - spineWidth;
        var cap = Math.Min(12, Math.Max(1, source.PixelHeight / 3));
        var middleHeight = source.PixelHeight - cap * 2;

        var grid = new Grid
        {
            Width = width,
            Height = height,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            ClipToBounds = true
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(1, spineStart), GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(spineWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(1, rightWidth), GridUnitType.Star)
        });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cap) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cap) });

        var xCuts = new[] { 0, spineStart, spineStart + spineWidth, source.PixelWidth };
        var yCuts = new[] { 0, cap, cap + middleHeight, source.PixelHeight };
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var crop = new Int32Rect(
                    xCuts[column],
                    yCuts[row],
                    xCuts[column + 1] - xCuts[column],
                    yCuts[row + 1] - yCuts[row]);
                AddNineSliceImage(grid, source, crop, row, column);
            }
        }
        return grid;
    }

    private void AddNineSliceImage(Grid grid, BitmapSource source, Int32Rect crop, int row, int column)
    {
        if (crop.Width <= 0 || crop.Height <= 0)
            return;
        if (!_nineSliceCropCache.TryGetValue((source, crop), out var cropped))
        {
            cropped = new CroppedBitmap(source, crop);
            cropped.Freeze();
            _nineSliceCropCache[(source, crop)] = cropped;
        }
        var image = new Image
        {
            Source = cropped,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            IsHitTestVisible = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Grid.SetRow(image, row);
        Grid.SetColumn(image, column);
        grid.Children.Add(image);
    }

    private Point GetInputPoint(ResearchNodeRow node) => new(
        Canvas.GetLeft(_nodeVisuals[node.RowIdentity]),
        Canvas.GetTop(_nodeVisuals[node.RowIdentity]) + NodeHeight / 2);

    private Point GetOutputPoint(ResearchNodeRow node) => new(
        Canvas.GetLeft(_nodeVisuals[node.RowIdentity]) + NodeWidth,
        Canvas.GetTop(_nodeVisuals[node.RowIdentity]) + NodeHeight / 2);

    private static double WorldX(int column) => GraphLeft + Math.Max(0, column - 1) * ColumnSpacing;

    private static double WorldY(int row) =>
        LaneTop - (row / 2.0 - ResearchWorkbookService.CenterResearchRow / 2.0) * LaneSpacing;

    private int[] GetLaneChoices()
        => StructuredRows;

    private static Brush NodeHeaderBrush(int? permission)
    {
        var color = permission is >= 1
            ? PermissionColors[(permission.Value - 1) % PermissionColors.Length]
            : Color.FromRgb(58, 58, 58);
        return new SolidColorBrush(color);
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static double PositiveOrDefault(double first, double second, double fallback) =>
        first > 1 ? first : second > 1 ? second : fallback;

    private sealed record ResearchLinkTag(string SourceNodeId, string TargetRowIdentity);
    private sealed record ResearchStepTag(int Column);
    private sealed record ResearchSlotTag(int Column, int Row);
    private sealed record ResearchPinTag(string NodeRowIdentity, bool IsOutput);
    private sealed record UndoState(
        ResearchWorkbookContext Workbook,
        string? Category,
        string[] SelectedNodeIds,
        int[] SelectedStepColumns);
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
        if (_updatingPermissionFilters)
            return;
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
            var visibleColumns = VisibleNodes().Select(node => node.Column).ToHashSet();
            _selectedStepColumns.IntersectWith(visibleColumns);
            RenderGraph(fitGraph: true);
            PopulateHierarchy();
            RenderInspector();
        }
    }

    private void RefreshPermissionFilterButtons()
    {
        if (PermissionFilterPanel is null || PermissionAll is null)
            return;

        _updatingPermissionFilters = true;
        try
        {
            while (PermissionFilterPanel.Children.Count > 2)
                PermissionFilterPanel.Children.RemoveAt(PermissionFilterPanel.Children.Count - 1);

            var permissions = _workbook is null || _selectedCategory is null
                ? []
                : _workbook.Nodes
                    .Where(node => Same(node.Category, _selectedCategory.Category) && node.NodePermission is >= 1)
                    .Select(node => node.NodePermission!.Value)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray();

            if (_permissionFilter > 0 && !permissions.Contains(_permissionFilter))
            {
                _permissionFilter = 0;
                _settings.PermissionFilter = 0;
            }

            PermissionAll.IsChecked = _permissionFilter == 0;
            foreach (var permission in permissions)
            {
                var button = new RadioButton
                {
                    GroupName = "Permission",
                    Content = permission.ToString(CultureInfo.InvariantCulture),
                    Tag = permission.ToString(CultureInfo.InvariantCulture),
                    Style = (Style)FindResource("FilterToggle"),
                    IsChecked = _permissionFilter == permission
                };
                button.Checked += PermissionFilter_Checked;
                PermissionFilterPanel.Children.Add(button);
            }
        }
        finally
        {
            _updatingPermissionFilters = false;
        }
    }

    private void StepCountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null || _selectedCategory is null
            || sender is not Button { Tag: ValueTuple<int, int> change })
            return;

        var category = _selectedCategory.Category;
        var currentCount = _workbook.Nodes.Count(node => Same(node.Category, category) && node.Column == change.Item1);
        var desiredCount = Math.Clamp(currentCount + change.Item2, 1,
            ResearchWorkbookService.MaxNodesPerStep);
        if (desiredCount == currentCount)
            return;

        PushUndo();
        var result = ResearchWorkbookService.TrySetColumnNodeCountInPlace(
            _workbook, category, change.Item1, desiredCount);
        if (!result.Success)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Error, result.Message);
            return;
        }

        _selectedCategory = _workbook.FindCategory(category);
        var liveIdentities = _workbook.Nodes.Select(node => node.RowIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedNodeIdentities.IntersectWith(liveIdentities);
        _primaryNode = _workbook.Nodes.FirstOrDefault(node => _selectedNodeIdentities.Contains(node.RowIdentity));
        RenderGraph();
        ScheduleHierarchyRefresh();
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info, result.Message);
        e.Handled = true;
    }

    private void AppendStepButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null || _selectedCategory is null)
            return;

        var category = _selectedCategory.Category;
        PushUndo();
        var result = ResearchWorkbookService.TryAppendStep(_workbook, category);
        if (!result.Success)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Error, result.Message, category);
            return;
        }

        var newColumn = _workbook.Nodes
            .Where(node => Same(node.Category, category))
            .Select(node => node.Column)
            .DefaultIfEmpty()
            .Max();
        _permissionFilter = 0;
        _settings.PermissionFilter = 0;
        _selectedNodeIdentities.Clear();
        _selectedStepColumns.Clear();
        _selectedStepColumns.Add(newColumn);
        _primaryNode = null;
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info, result.Message, category);
        e.Handled = true;
    }

    private void ChangePermissionBoundary(int permission, int direction) =>
        ApplyPermissionMutation(() => ResearchWorkbookService.TryShiftPermissionBoundary(
            _workbook!, _selectedCategory!.Category, permission, direction));

    private void AddPermissionRegion() =>
        ApplyPermissionMutation(() => ResearchWorkbookService.TryAddPermissionRegion(
            _workbook!, _selectedCategory!.Category));

    private void RemovePermissionRegion() =>
        ApplyPermissionMutation(() => ResearchWorkbookService.TryRemovePermissionRegion(
            _workbook!, _selectedCategory!.Category));

    private void ApplyPermissionMutation(Func<ResearchMutationResult> mutation)
    {
        if (_workbook is null || _selectedCategory is null)
            return;
        var category = _selectedCategory.Category;
        PushUndo();
        var result = mutation();
        if (!result.Success)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Error, result.Message, category);
            return;
        }

        _selectedCategory = _workbook.FindCategory(category);
        var visibleIdentities = VisibleNodes()
            .Select(node => node.RowIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedNodeIdentities.IntersectWith(visibleIdentities);
        _primaryNode = _workbook.Nodes.FirstOrDefault(node => _selectedNodeIdentities.Contains(node.RowIdentity));
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info, result.Message, category);
    }

    private void StepBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ResearchStepTag step })
            return;
        BeginStepMarqueeSelection(e, step.Column);
        e.Handled = true;
    }

    private void SelectStepBlock(int column, bool updateHierarchy)
    {
        if (!VisibleNodes().Any(node => node.Column == column))
            return;
        SelectStepBlocks([column], updateHierarchy);
    }

    private void SelectStepBlocks(IEnumerable<int> columns, bool updateHierarchy)
    {
        var visibleColumns = VisibleNodes().Select(node => node.Column).ToHashSet();
        var selectedColumns = columns.Where(visibleColumns.Contains).Distinct().OrderBy(value => value).ToArray();
        SceneViewport.Focus();
        _selectedNodeIdentities.Clear();
        _primaryNode = null;
        _selectedStepColumns.Clear();
        foreach (var column in selectedColumns)
            _selectedStepColumns.Add(column);
        CloseQuickNodePopup();
        ApplySelectionVisuals();
        RenderInspector();
        if (updateHierarchy && selectedColumns.Length == 1)
            SelectHierarchyStep(selectedColumns[0]);
    }

    private void EmptySlot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ResearchSlotTag slot })
            return;
        if (e.ChangedButton != MouseButton.Left)
            return;

        SceneViewport.Focus();
        CloseQuickNodePopup();
        _pendingEmptySlot = slot;
        _pendingEmptySlotStartScreen = e.GetPosition(SceneViewport);
        _pendingEmptySlotAdditive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        SceneViewport.CaptureMouse();
        e.Handled = true;
    }

    private void CreateNodeAtSlot(int column, int row)
    {
        if (_workbook is null || _selectedCategory is null)
            return;
        var category = _selectedCategory.Category;
        if (_workbook.Nodes.Any(node => Same(node.Category, category) && node.Column == column && node.Row == row))
        {
            Log(ResearchConsoleSeverity.Warning, $"STEP {column:000} / row {row}은 이미 사용 중입니다.", category);
            return;
        }
        if (_workbook.Nodes.Count(node => Same(node.Category, category) && node.Column == column)
            >= ResearchWorkbookService.MaxNodesPerStep)
        {
            Log(ResearchConsoleSeverity.Warning,
                $"STEP {column:000}에는 연구 노드를 최대 {ResearchWorkbookService.MaxNodesPerStep}개만 추가할 수 있습니다.",
                category);
            return;
        }

        var categoryNodes = _workbook.Nodes
            .Where(node => Same(node.Category, category))
            .ToList();
        var template = categoryNodes
            .OrderBy(node => Math.Abs(node.Column - column))
            .ThenBy(node => Math.Abs(node.Row - row))
            .FirstOrDefault();
        var permission = _permissionFilter > 0
            ? _permissionFilter
            : categoryNodes.Where(node => node.Column == column)
                .Select(node => node.NodePermission)
                .FirstOrDefault(value => value is not null)
              ?? template?.NodePermission
              ?? 1;

        PushUndo();
        try
        {
            var created = ResearchWorkbookService.CreateNode(
                _workbook,
                category,
                column,
                row,
                template?.ExportId,
                template?.ThemeId ?? "s1");
            created.Node.NodePermission = permission;
            _selectedNodeIdentities.Clear();
            _selectedNodeIdentities.Add(created.Node.RowIdentity);
            _selectedStepColumns.Clear();
            _primaryNode = created.Node;
            _quickNodePopupRequested = true;
            RefreshEmptySlotTargets();
            RefreshStepHeaderCount(column);
            CreateNodeVisual(created.Node);
            ApplySelectionVisuals();
            RefreshQuickNodePopup();
            UpdateSceneStatus();
            ScheduleHierarchyRefresh();
            RenderInspector();
            ScheduleValidation();
            UpdateDirtyState();
            Log(ResearchConsoleSeverity.Info,
                $"연구 노드를 추가했습니다: {created.Node.Id} (STEP {column:000} / row {row})",
                created.Node.Id);
        }
        catch (Exception ex)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Error, $"연구 노드를 추가하지 못했습니다: {ex.Message}", category);
            ThemedMessageBox.Show(ex.Message, "연구 노드 추가", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ResearchNodeRow node } card)
            return;
        if (e.ChangedButton != MouseButton.Left)
            return;

        SelectNodeFromScene(node, card, respectControlModifier: true, showQuickEditor: false);
        if (!_selectedNodeIdentities.Contains(node.RowIdentity))
        {
            e.Handled = true;
            return;
        }

        _pendingNodeDragAnchorIdentity = node.RowIdentity;
        _pendingNodeDragRows.Clear();
        foreach (var selected in VisibleNodes().Where(candidate =>
                     _selectedNodeIdentities.Contains(candidate.RowIdentity)))
        {
            _pendingNodeDragRows[selected.RowIdentity] = selected.Row;
        }
        _pendingNodeDragStartScreen = e.GetPosition(SceneViewport);
        _pendingNodeDragDeltaRow = 0;
        _nodeDragMoved = false;
        SceneViewport.CaptureMouse();
        e.Handled = true;
    }

    private void SelectNodeFromScene(
        ResearchNodeRow node,
        Border card,
        bool respectControlModifier,
        bool showQuickEditor)
    {
        SceneViewport.Focus();
        _selectedStepColumns.Clear();
        if (respectControlModifier && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
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
        if (showQuickEditor && _selectedNodeIdentities.Count == 1 && _primaryNode is not null)
            ShowQuickNodePopup(_primaryNode, card);
        else
            CloseQuickNodePopup();
    }

    private void Pin_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_workbook is null
            || sender is not Ellipse { Tag: ResearchPinTag pin }
            || !_nodeVisuals.TryGetValue(pin.NodeRowIdentity, out var card))
            return;
        var node = _workbook.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, pin.NodeRowIdentity));
        if (node is null)
            return;

        if (!pin.IsOutput)
        {
            SelectNodeFromScene(node, card, respectControlModifier: false, showQuickEditor: true);
            e.Handled = true;
            return;
        }

        SelectNodeFromScene(node, card, respectControlModifier: false, showQuickEditor: false);
        CancelRightPointerGesture(releaseCapture: false);
        CancelLinkDrag(releaseCapture: false);
        _linkDragSourceIdentity = node.RowIdentity;
        _linkPreviewPath = new ShapePath
        {
            Stroke = new SolidColorBrush(Color.FromRgb(104, 213, 151)),
            StrokeThickness = 3,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_linkPreviewPath, 100);
        LinkCanvas.Children.Add(_linkPreviewPath);
        UpdateLinkPreview(e.GetPosition(SceneViewport));
        SceneViewport.CaptureMouse();
        e.Handled = true;
    }

    private void Pin_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse { Tag: ResearchPinTag pin })
            _pendingContextPin = pin;
    }

    private void Node_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ResearchNodeRow node } card)
            return;

        _pendingContextNode = node;
        _pendingContextCard = card;
    }

    private void ShowNodeContextMenu(ResearchNodeRow node, Border card)
    {
        SceneViewport.Focus();
        _selectedStepColumns.Clear();
        if (!_selectedNodeIdentities.Contains(node.RowIdentity))
        {
            _selectedNodeIdentities.Clear();
            _selectedNodeIdentities.Add(node.RowIdentity);
        }
        _primaryNode = node;
        CloseQuickNodePopup();
        ApplySelectionVisuals();
        RenderInspector();
        SelectHierarchyNode(node.RowIdentity);

        var count = _selectedNodeIdentities.Count;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var delete = new MenuItem
        {
            Header = count > 1 ? $"선택한 연구 노드 {count:N0}개 삭제" : "연구 노드 삭제"
        };
        delete.Click += (_, _) => DeleteSelectedNodes();
        menu.Items.Add(delete);
        card.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ShowPinContextMenu(ResearchPinTag pin)
    {
        if (_workbook is null)
            return;
        var node = _workbook.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, pin.NodeRowIdentity));
        var visual = (pin.IsOutput ? _outputPinVisuals : _inputPinVisuals).GetValueOrDefault(pin.NodeRowIdentity);
        if (node is null || visual is null || !_nodeVisuals.TryGetValue(node.RowIdentity, out var card))
            return;

        SelectNodeFromScene(node, card, respectControlModifier: false, showQuickEditor: false);
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var disconnect = new MenuItem
        {
            Header = pin.IsOutput ? "이 출력 핀의 연결 모두 해제" : "이 입력 핀의 연결 모두 해제"
        };
        disconnect.Click += (_, _) => DisconnectPinConnections(node.RowIdentity, pin.IsOutput);
        menu.Items.Add(disconnect);
        visual.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void DisconnectPinConnections(string nodeRowIdentity, bool isOutput)
    {
        if (_workbook is null)
            return;
        var node = _workbook.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, nodeRowIdentity));
        if (node is null)
            return;

        var targets = isOutput
            ? _workbook.Nodes.Where(candidate => candidate.Conditions.Any(value => Same(value, node.Id))).ToList()
            : [node];
        var sourceIds = isOutput
            ? new[] { node.Id }
            : node.Conditions.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sourceIds.Length == 0 || !targets.Any(target => sourceIds.Any(sourceId =>
                target.Conditions.Any(value => Same(value, sourceId)))))
        {
            Log(ResearchConsoleSeverity.Info, "해제할 핀 연결이 없습니다.", node.Id);
            return;
        }

        PushUndo();
        var changed = false;
        foreach (var target in targets)
        foreach (var sourceId in sourceIds)
            changed |= ResearchWorkbookService.DisconnectCondition(target, sourceId);
        if (!changed)
        {
            UndoWithoutRender();
            return;
        }

        RenderGraph();
        ScheduleHierarchyRefresh();
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info,
            isOutput ? "출력 핀의 연결을 해제했습니다." : "입력 핀의 연결을 해제했습니다.", node.Id);
    }

    private void UpdateLinkPreview(Point screenPoint)
    {
        if (_workbook is null || _linkPreviewPath is null || _linkDragSourceIdentity is null)
            return;
        var source = _workbook.Nodes.FirstOrDefault(node => Same(node.RowIdentity, _linkDragSourceIdentity));
        if (source is null || !_outputPinVisuals.ContainsKey(source.RowIdentity))
            return;
        var from = GetOutputPoint(source);
        var to = ScreenToWorld(screenPoint);
        var bendX = from.X + Math.Max(24, (to.X - from.X) / 2);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(from, isFilled: false, isClosed: false);
            context.LineTo(new Point(bendX, from.Y), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(bendX, to.Y), isStroked: true, isSmoothJoin: false);
            context.LineTo(to, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        _linkPreviewPath.Data = geometry;
    }

    private ResearchNodeRow? FindInputPinTarget(Point screenPoint, string sourceIdentity)
    {
        if (_workbook is null)
            return null;
        ResearchNodeRow? closest = null;
        var closestDistanceSquared = PinDropRadius * PinDropRadius;
        foreach (var (identity, visual) in _inputPinVisuals)
        {
            if (Same(identity, sourceIdentity))
                continue;
            var center = visual.TranslatePoint(new Point(PinDiameter / 2, PinDiameter / 2), SceneViewport);
            var delta = center - screenPoint;
            var distanceSquared = delta.X * delta.X + delta.Y * delta.Y;
            if (distanceSquared > closestDistanceSquared)
                continue;
            closestDistanceSquared = distanceSquared;
            closest = _workbook.Nodes.FirstOrDefault(node => Same(node.RowIdentity, identity));
        }
        return closest;
    }

    private void CompleteLinkDrag(Point screenPoint)
    {
        if (_workbook is null || _linkDragSourceIdentity is null)
        {
            CancelLinkDrag(releaseCapture: true);
            return;
        }
        var sourceIdentity = _linkDragSourceIdentity;
        var target = FindInputPinTarget(screenPoint, sourceIdentity);
        CancelLinkDrag(releaseCapture: true);
        if (target is null)
            return;

        var source = _workbook.Nodes.FirstOrDefault(node => Same(node.RowIdentity, sourceIdentity));
        if (source is null)
            return;
        PushUndo();
        var result = ResearchWorkbookService.TryConnectCondition(_workbook, source.RowIdentity, target.RowIdentity);
        if (!result.Success)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Warning, result.Message, source.Id);
        }
        else
        {
            Log(ResearchConsoleSeverity.Info, result.Message, target.Id);
            if (!string.IsNullOrWhiteSpace(result.WarningMessage))
                Log(ResearchConsoleSeverity.Warning, result.WarningMessage, target.Id);
        }

        RenderGraph();
        ScheduleHierarchyRefresh();
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
    }

    private void CancelLinkDrag(bool releaseCapture)
    {
        if (_linkPreviewPath is not null)
            LinkCanvas.Children.Remove(_linkPreviewPath);
        _linkPreviewPath = null;
        _linkDragSourceIdentity = null;
        if (releaseCapture && SceneViewport.IsMouseCaptured)
            SceneViewport.ReleaseMouseCapture();
    }

    private void ShowQuickNodePopup(ResearchNodeRow node, Border card)
    {
        if (QuickNodePopup.Tag is not string previousIdentity || !Same(previousIdentity, node.RowIdentity))
            _quickEditorShowsEffect = false;
        _quickNodePopupRequested = true;
        QuickNodePopup.IsOpen = false;
        QuickNodePopup.Tag = node.RowIdentity;
        QuickNodePopup.PlacementTarget = card;

        var cardPosition = card.TranslatePoint(new Point(0, 0), SceneViewport);
        var placeRight = cardPosition.X + NodeWidth + 400 <= SceneViewport.ActualWidth;
        QuickNodePopup.Placement = placeRight ? PlacementMode.Right : PlacementMode.Left;
        QuickNodePopup.HorizontalOffset = placeRight ? 8 : -8;
        QuickNodePopup.VerticalOffset = -6;

        PopulateQuickEditorFields(node);
        UpdateQuickEditorTab();
        QuickNodePopup.IsOpen = true;
    }

    private void PopulateQuickEditorFields(ResearchNodeRow node)
    {
        QuickNodeIdText.Text = node.Id;
        QuickNodeImageBox.Text = node.Image;
        QuickNodeActiveItemBox.Text = node.ActiveItemId;
        QuickNodeActiveValueBox.Text = node.ActiveItemValue;
        QuickNodeActiveStepBox.Text = node.ActiveStep?.ToString(CultureInfo.InvariantCulture) ?? "";
        QuickNodeTotalText.Text = $"필요 수량 합계: {node.TotalRequiredHelper?.ToString("G", CultureInfo.InvariantCulture) ?? "0"}";
        PopulateQuickEffectFields(node);
    }

    private void PopulateQuickEffectFields(ResearchNodeRow node)
    {
        var effect = _workbook?.FindEffect(node);
        var hasEffect = effect is not null;
        QuickEffectMissingPanel.Visibility = hasEffect ? Visibility.Collapsed : Visibility.Visible;
        QuickEffectFieldsPanel.Visibility = hasEffect ? Visibility.Visible : Visibility.Collapsed;

        QuickEffectIdBox.Text = effect?.Id ?? "";
        QuickEffectExportIdBox.Text = effect?.ExportId ?? "";
        QuickEffectParentEffectBox.Text = effect?.ParentEffect ?? "";
        QuickEffectGroupMemoBox.Text = effect?.GroupMemo ?? "";
        QuickEffectMemoBox.Text = effect?.Memo ?? "";
        QuickEffectTypeBox.Text = effect?.Type ?? "";
        QuickEffectConditionBox.Text = effect?.Condition ?? "";
        QuickEffectValueBox.Text = effect?.ValueText ?? "";
        QuickEffectTestBox.Text = effect?.Test ?? "";
        QuickEffectExtraBox.Text = effect?.Extra ?? "";
    }

    private void UpdateQuickEditorTab()
    {
        QuickNodeSettingsTab.IsChecked = !_quickEditorShowsEffect;
        QuickEffectSettingsTab.IsChecked = _quickEditorShowsEffect;
        QuickNodeSettingsPanel.Visibility = _quickEditorShowsEffect ? Visibility.Collapsed : Visibility.Visible;
        QuickEffectSettingsPanel.Visibility = _quickEditorShowsEffect ? Visibility.Visible : Visibility.Collapsed;
        QuickEditorApplyButton.IsEnabled = !_quickEditorShowsEffect
                                           || QuickEffectFieldsPanel.Visibility == Visibility.Visible;
    }

    private void RefreshQuickNodePopup()
    {
        if (!_quickNodePopupRequested)
        {
            CloseQuickNodePopup(clearRequest: false);
            return;
        }

        if (_primaryNode is null || _selectedNodeIdentities.Count != 1)
        {
            CloseQuickNodePopup();
            return;
        }

        if (!_nodeVisuals.ContainsKey(_primaryNode.RowIdentity))
        {
            CloseQuickNodePopup(clearRequest: false);
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_quickNodePopupRequested
                && _primaryNode is not null
                && _selectedNodeIdentities.Count == 1
                && _nodeVisuals.TryGetValue(_primaryNode.RowIdentity, out var currentCard))
            {
                ShowQuickNodePopup(_primaryNode, currentCard);
            }
        }));
    }

    private void CloseQuickNodePopup(bool clearRequest = true)
    {
        QuickNodePopup.IsOpen = false;
        QuickNodePopup.PlacementTarget = null;
        QuickNodePopup.Tag = null;
        if (clearRequest)
        {
            _quickNodePopupRequested = false;
            _quickEditorShowsEffect = false;
        }
    }

    private void QuickNodeCloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseQuickNodePopup();
        e.Handled = true;
    }

    private void QuickEditorTab_Click(object sender, RoutedEventArgs e)
    {
        _quickEditorShowsEffect = ReferenceEquals(sender, QuickEffectSettingsTab);
        UpdateQuickEditorTab();
        e.Handled = true;
    }

    private void QuickEffectCreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null || QuickNodePopup.Tag is not string rowIdentity)
            return;
        var node = _workbook.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, rowIdentity));
        if (node is null)
        {
            CloseQuickNodePopup();
            return;
        }

        CreateMissingEffect(node);
        PopulateQuickEffectFields(node);
        UpdateQuickEditorTab();
        ScheduleValidation();
        Log(ResearchConsoleSeverity.Info, $"연결 효과 행을 생성했습니다: {node.NexusEffectId}", node.Id);
        e.Handled = true;
    }

    private void QuickNodeApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null || QuickNodePopup.Tag is not string rowIdentity)
            return;
        var node = _workbook.Nodes.FirstOrDefault(candidate => Same(candidate.RowIdentity, rowIdentity));
        if (node is null)
        {
            CloseQuickNodePopup();
            return;
        }

        if (_quickEditorShowsEffect)
        {
            ApplyQuickEffect(node, e);
            return;
        }

        int? activeStep;
        try
        {
            activeStep = ParseNullableInt(QuickNodeActiveStepBox.Text, "active_step");
            if (activeStep is < 0)
                throw new InvalidOperationException("active_step은 0 이상이어야 합니다.");
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(ex.Message, "빠른 노드 편집", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var image = QuickNodeImageBox.Text.Trim();
        var activeItem = QuickNodeActiveItemBox.Text.Trim();
        var activeValues = QuickNodeActiveValueBox.Text.Trim();
        if (Same(node.Image, image)
            && Same(node.ActiveItemId, activeItem)
            && Same(node.ActiveItemValue, activeValues)
            && node.ActiveStep == activeStep)
        {
            CloseQuickNodePopup();
            return;
        }

        PushUndo();
        node.Image = image;
        node.ActiveItemId = activeItem;
        node.ActiveItemValue = activeValues;
        node.ActiveStep = activeStep;
        node.TotalRequiredHelper = ResearchWorkbookService.CalculateActiveItemTotal(activeValues);
        _primaryNode = node;
        _selectedNodeIdentities.Clear();
        _selectedNodeIdentities.Add(node.RowIdentity);
        _quickNodePopupRequested = true;
        RenderGraph();
        ScheduleHierarchyRefresh();
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info, $"빠른 노드 편집을 적용했습니다: {node.Id}", node.Id);
        e.Handled = true;
    }

    private void ApplyQuickEffect(ResearchNodeRow node, RoutedEventArgs e)
    {
        if (_workbook?.FindEffect(node) is not { } effect)
        {
            ThemedMessageBox.Show("연결된 nexus_effect 행이 없습니다. 먼저 효과 행을 생성해 주세요.",
                "빠른 효과 편집", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var exportId = QuickEffectExportIdBox.Text.Trim();
        var parentEffect = QuickEffectParentEffectBox.Text.Trim();
        var groupMemo = QuickEffectGroupMemoBox.Text.Trim();
        var memo = QuickEffectMemoBox.Text.Trim();
        var type = QuickEffectTypeBox.Text.Trim();
        var condition = QuickEffectConditionBox.Text.Trim();
        var value = QuickEffectValueBox.Text.Trim();
        var test = QuickEffectTestBox.Text.Trim();
        var extra = QuickEffectExtraBox.Text.Trim();

        if (string.Equals(effect.ExportId, exportId, StringComparison.Ordinal)
            && string.Equals(effect.ParentEffect, parentEffect, StringComparison.Ordinal)
            && string.Equals(effect.GroupMemo, groupMemo, StringComparison.Ordinal)
            && string.Equals(effect.Memo, memo, StringComparison.Ordinal)
            && string.Equals(effect.Type, type, StringComparison.Ordinal)
            && string.Equals(effect.Condition, condition, StringComparison.Ordinal)
            && string.Equals(effect.ValueText, value, StringComparison.Ordinal)
            && string.Equals(effect.Test, test, StringComparison.Ordinal)
            && string.Equals(effect.Extra, extra, StringComparison.Ordinal))
        {
            CloseQuickNodePopup();
            return;
        }

        PushUndo();
        effect.ExportId = exportId;
        effect.ParentEffect = parentEffect;
        effect.GroupMemo = groupMemo;
        effect.Memo = memo;
        effect.Type = type;
        effect.Condition = condition;
        effect.ValueText = value;
        effect.Test = test;
        effect.Extra = extra;
        _primaryNode = node;
        _selectedNodeIdentities.Clear();
        _selectedNodeIdentities.Add(node.RowIdentity);
        _quickNodePopupRequested = true;
        PopulateQuickEffectFields(node);
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info, $"빠른 효과 편집을 적용했습니다: {effect.Id}", node.Id);
        e.Handled = true;
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
        ScheduleHierarchyRefresh();
        RenderInspector();
        ScheduleValidation();
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
        _panGestureMoved = false;
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

        var wasDrag = _panGestureMoved;
        var contextPin = _pendingContextPin;
        var contextNode = _pendingContextNode;
        var contextCard = _pendingContextCard;
        _isPanning = false;
        _panGestureMoved = false;
        _pendingContextPin = null;
        _pendingContextNode = null;
        _pendingContextCard = null;
        if (SceneViewport.IsMouseCaptured)
            SceneViewport.ReleaseMouseCapture();
        if (wasDrag)
            SaveViewSettings();
        else if (contextPin is not null)
            ShowPinContextMenu(contextPin);
        else if (contextNode is not null && contextCard is not null)
            ShowNodeContextMenu(contextNode, contextCard);
        e.Handled = true;
    }

    private void SceneViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_linkDragSourceIdentity is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                CancelLinkDrag(releaseCapture: true);
            else
                UpdateLinkPreview(e.GetPosition(SceneViewport));
            e.Handled = true;
            return;
        }
        if (_pendingNodeDragAnchorIdentity is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CancelNodeRowDrag(restorePreview: true, releaseCapture: true);
                return;
            }

            var current = e.GetPosition(SceneViewport);
            if (!_nodeDragMoved && !HasExceededDragThreshold(_pendingNodeDragStartScreen, current))
            {
                e.Handled = true;
                return;
            }
            if (!_nodeDragMoved)
            {
                _nodeDragMoved = true;
                CloseQuickNodePopup();
            }

            var scale = Math.Max(0.01, GraphScale.ScaleY);
            var rawDelta = -(current.Y - _pendingNodeDragStartScreen.Y) / scale / RowStep;
            var deltaRow = (int)Math.Round(rawDelta, MidpointRounding.AwayFromZero);
            var minRow = _pendingNodeDragRows.Values.DefaultIfEmpty(ResearchWorkbookService.MinResearchRow).Min();
            var maxRow = _pendingNodeDragRows.Values.DefaultIfEmpty(ResearchWorkbookService.MaxResearchRow).Max();
            deltaRow = Math.Clamp(
                deltaRow,
                ResearchWorkbookService.MinResearchRow - minRow,
                ResearchWorkbookService.MaxResearchRow - maxRow);
            if (deltaRow != _pendingNodeDragDeltaRow)
            {
                _pendingNodeDragDeltaRow = deltaRow;
                PreviewNodeRowDrag(deltaRow);
            }
            e.Handled = true;
            return;
        }
        if (_pendingEmptySlot is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            var slotCurrent = e.GetPosition(SceneViewport);
            if (HasExceededDragThreshold(_pendingEmptySlotStartScreen, slotCurrent))
            {
                var slot = _pendingEmptySlot;
                _pendingEmptySlot = null;
                BeginStepMarqueeSelection(
                    _pendingEmptySlotStartScreen,
                    slot.Column,
                    _pendingEmptySlotAdditive);
                UpdateStepMarqueeSelection(slotCurrent);
            }
            e.Handled = true;
            return;
        }
        if (_isStepMarqueeSelecting && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateStepMarqueeSelection(e.GetPosition(SceneViewport));
            e.Handled = true;
            return;
        }
        if (!_isPanning)
            return;
        if (e.RightButton != MouseButtonState.Pressed)
        {
            CancelRightPointerGesture(releaseCapture: true);
            return;
        }
        var panCurrent = e.GetPosition(SceneViewport);
        if (!_panGestureMoved && !HasExceededDragThreshold(_panScreenStart, panCurrent))
        {
            e.Handled = true;
            return;
        }
        if (!_panGestureMoved)
        {
            _panGestureMoved = true;
            CloseQuickNodePopup();
        }
        var delta = panCurrent - _panScreenStart;
        GraphTranslate.X = _panXStart + delta.X;
        GraphTranslate.Y = _panYStart + delta.Y;
        e.Handled = true;
    }

    private void SceneViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Border or TextBlock or Image or Button)
            return;
        BeginStepMarqueeSelection(e, clickedColumn: null);
        e.Handled = true;
    }

    private void BeginStepMarqueeSelection(MouseButtonEventArgs e, int? clickedColumn)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        BeginStepMarqueeSelection(
            e.GetPosition(SceneViewport),
            clickedColumn,
            (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    private void BeginStepMarqueeSelection(Point startScreen, int? clickedColumn, bool additive)
    {
        SceneViewport.Focus();
        CloseQuickNodePopup();
        _isStepMarqueeSelecting = true;
        _stepMarqueeMoved = false;
        _stepMarqueeAdditive = additive;
        _stepMarqueeClickedColumn = clickedColumn;
        _stepMarqueeStartScreen = startScreen;
        _stepMarqueeStartWorld = ScreenToWorld(_stepMarqueeStartScreen);
        _stepMarqueeBaseSelection.Clear();
        if (_stepMarqueeAdditive)
            _stepMarqueeBaseSelection.UnionWith(_selectedStepColumns);
        _selectedNodeIdentities.Clear();
        _primaryNode = null;
        StepSelectionMarquee.Visibility = Visibility.Collapsed;
        SceneViewport.CaptureMouse();
    }

    private void UpdateStepMarqueeSelection(Point currentScreen)
    {
        if (!_isStepMarqueeSelecting)
            return;
        if (!_stepMarqueeMoved && !HasExceededDragThreshold(_stepMarqueeStartScreen, currentScreen))
            return;

        _stepMarqueeMoved = true;
        var currentWorld = ScreenToWorld(currentScreen);
        var selectionRect = NormalizedRect(_stepMarqueeStartWorld, currentWorld);
        Canvas.SetLeft(StepSelectionMarquee, selectionRect.Left);
        Canvas.SetTop(StepSelectionMarquee, selectionRect.Top);
        StepSelectionMarquee.Width = selectionRect.Width;
        StepSelectionMarquee.Height = selectionRect.Height;
        StepSelectionMarquee.Visibility = Visibility.Visible;

        var intersected = _stepBandVisuals.Keys
            .Where(column => new Rect(WorldX(column) - 9, 43, NodeWidth + 18, GraphHeight - 48)
                .IntersectsWith(selectionRect))
            .ToArray();
        _selectedStepColumns.Clear();
        _selectedStepColumns.UnionWith(_stepMarqueeBaseSelection);
        _selectedStepColumns.UnionWith(intersected);
        ApplySelectionVisuals();
    }

    private void SceneViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_linkDragSourceIdentity is not null)
        {
            CompleteLinkDrag(e.GetPosition(SceneViewport));
            e.Handled = true;
            return;
        }
        if (_pendingNodeDragAnchorIdentity is not null)
        {
            CompleteNodeRowDrag();
            e.Handled = true;
            return;
        }
        if (_pendingEmptySlot is not null)
        {
            var slot = _pendingEmptySlot;
            _pendingEmptySlot = null;
            if (SceneViewport.IsMouseCaptured)
                SceneViewport.ReleaseMouseCapture();
            CreateNodeAtSlot(slot.Column, slot.Row);
            e.Handled = true;
            return;
        }
        if (!_isStepMarqueeSelecting)
            return;

        if (!_stepMarqueeMoved)
        {
            _selectedStepColumns.Clear();
            _selectedStepColumns.UnionWith(_stepMarqueeBaseSelection);
            if (_stepMarqueeClickedColumn is int clickedColumn)
            {
                if (_stepMarqueeAdditive && !_selectedStepColumns.Add(clickedColumn))
                    _selectedStepColumns.Remove(clickedColumn);
                else if (!_stepMarqueeAdditive)
                    _selectedStepColumns.Add(clickedColumn);
            }
        }

        _isStepMarqueeSelecting = false;
        StepSelectionMarquee.Visibility = Visibility.Collapsed;
        SceneViewport.ReleaseMouseCapture();
        ApplySelectionVisuals();
        RenderInspector();
        if (_selectedStepColumns.Count == 1)
            SelectHierarchyStep(_selectedStepColumns.Min);
        e.Handled = true;
    }

    private void SceneViewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SceneViewport))
            return;
        CancelLinkDrag(releaseCapture: false);
        CancelNodeRowDrag(restorePreview: true, releaseCapture: false);
        CancelRightPointerGesture(releaseCapture: false);
        _pendingEmptySlot = null;
        if (_isStepMarqueeSelecting)
        {
            _isStepMarqueeSelecting = false;
            StepSelectionMarquee.Visibility = Visibility.Collapsed;
        }
    }

    private void PreviewNodeRowDrag(int deltaRow)
    {
        foreach (var (identity, originalRow) in _pendingNodeDragRows)
        {
            var targetRow = originalRow + deltaRow;
            if (_nodeVisuals.TryGetValue(identity, out var card))
                Canvas.SetTop(card, WorldY(targetRow));
            if (_inputPinVisuals.TryGetValue(identity, out var inputPin))
                Canvas.SetTop(inputPin, WorldY(targetRow) + NodeHeight / 2 - PinDiameter / 2);
            if (_outputPinVisuals.TryGetValue(identity, out var outputPin))
                Canvas.SetTop(outputPin, WorldY(targetRow) + NodeHeight / 2 - PinDiameter / 2);
        }
        DrawAllLinks();
    }

    private void CompleteNodeRowDrag()
    {
        if (_workbook is null || _pendingNodeDragAnchorIdentity is null)
        {
            CancelNodeRowDrag(restorePreview: true, releaseCapture: true);
            return;
        }

        var anchorIdentity = _pendingNodeDragAnchorIdentity;
        var identities = _pendingNodeDragRows.Keys.ToArray();
        var deltaRow = _pendingNodeDragDeltaRow;
        var wasDrag = _nodeDragMoved;
        CancelNodeRowDrag(restorePreview: false, releaseCapture: true);

        if (!wasDrag || deltaRow == 0)
        {
            var clickedNode = _workbook.Nodes.FirstOrDefault(node => Same(node.RowIdentity, anchorIdentity));
            if (clickedNode is not null && _nodeVisuals.TryGetValue(anchorIdentity, out var clickedCard))
                SelectNodeFromScene(clickedNode, clickedCard, respectControlModifier: false, showQuickEditor: true);
            return;
        }

        PushUndo();
        var result = ResearchWorkbookService.TryMoveNodesInPlace(
            _workbook,
            identities,
            deltaColumn: 0,
            deltaRow: deltaRow);
        if (!result.Success)
        {
            UndoWithoutRender();
            RenderGraph();
            RenderInspector();
            Log(ResearchConsoleSeverity.Warning, result.Message, anchorIdentity);
            return;
        }

        var category = _selectedCategory?.Category;
        _selectedCategory = _workbook.FindCategory(category);
        _primaryNode = _workbook.Nodes.FirstOrDefault(node => Same(node.RowIdentity, anchorIdentity));
        RenderGraph();
        ScheduleHierarchyRefresh();
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info, result.Message, _primaryNode?.Id ?? anchorIdentity);
    }

    private void CancelNodeRowDrag(bool restorePreview, bool releaseCapture)
    {
        if (restorePreview && _nodeDragMoved && _pendingNodeDragRows.Count > 0)
            PreviewNodeRowDrag(0);

        _pendingNodeDragAnchorIdentity = null;
        _pendingNodeDragRows.Clear();
        _pendingNodeDragDeltaRow = 0;
        _nodeDragMoved = false;
        if (releaseCapture && SceneViewport.IsMouseCaptured)
            SceneViewport.ReleaseMouseCapture();
    }

    private void CancelRightPointerGesture(bool releaseCapture)
    {
        _isPanning = false;
        _panGestureMoved = false;
        _pendingContextPin = null;
        _pendingContextNode = null;
        _pendingContextCard = null;
        if (releaseCapture && SceneViewport.IsMouseCaptured)
            SceneViewport.ReleaseMouseCapture();
    }

    private static bool HasExceededDragThreshold(Point start, Point current) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private Point ScreenToWorld(Point point) => new(
        (point.X - GraphTranslate.X) / GraphScale.ScaleX,
        (point.Y - GraphTranslate.Y) / GraphScale.ScaleY);

    private static Rect NormalizedRect(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Max(1, Math.Abs(second.X - first.X)),
        Math.Max(1, Math.Abs(second.Y - first.Y)));

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

        foreach (var (column, visual) in _stepBandVisuals)
        {
            var selected = _selectedStepColumns.Contains(column);
            var copied = IsCopiedStep(column);
            visual.Background = StepBandBrush(column, selected, copied);
            visual.BorderBrush = selected
                ? new SolidColorBrush(Color.FromRgb(82, 170, 211))
                : copied
                    ? new SolidColorBrush(Color.FromRgb(222, 183, 79))
                    : new SolidColorBrush(Color.FromArgb(90, 76, 76, 76));
            visual.BorderThickness = selected || copied
                ? new Thickness(2, 0, 2, 0)
                : new Thickness(1, 0, 1, 0);
        }

        foreach (var (column, visual) in _stepHeaderVisuals)
        {
            var selected = _selectedStepColumns.Contains(column);
            var copied = IsCopiedStep(column);
            visual.Background = new SolidColorBrush(selected
                ? Color.FromRgb(45, 79, 96)
                : copied
                    ? Color.FromRgb(78, 67, 39)
                    : Color.FromRgb(42, 42, 42));
            visual.BorderBrush = selected
                ? new SolidColorBrush(Color.FromRgb(82, 170, 211))
                : copied
                    ? new SolidColorBrush(Color.FromRgb(222, 183, 79))
                    : new SolidColorBrush(Color.FromRgb(73, 73, 73));
            visual.BorderThickness = selected || copied ? new Thickness(2) : new Thickness(1);
            visual.ToolTip = selected && _stepClipboard is not null && !copied
                ? $"STEP {column:000} paste target - press Ctrl+V"
                : copied
                    ? $"STEP {column:000} copied source"
                    : $"STEP {column:000} block - click to select, Ctrl+C to copy";
        }
    }

    private bool IsCopiedStep(int column) =>
        _stepClipboard is not null
        && _selectedCategory is not null
        && Same(_stepClipboard.SourceCategory, _selectedCategory.Category)
        && _stepClipboard.Blocks.Any(block => block.SourceColumn == column);

    private void FitOpeningView()
    {
        var nodes = VisibleNodes().ToList();
        if (nodes.Count == 0 || SceneViewport.ActualWidth < 20 || SceneViewport.ActualHeight < 20)
            return;
        var firstColumn = nodes.Min(node => node.Column);
        FitNodes(nodes.Where(node => node.Column <= firstColumn + 3).ToList(), maxScale: 1.0);
    }

    private void FitAll()
    {
        var nodes = VisibleNodes().ToList();
        if (nodes.Count == 0 || SceneViewport.ActualWidth < 20 || SceneViewport.ActualHeight < 20)
            return;
        FitNodes(nodes, maxScale: 1.25);
    }

    private void FitNodes(IReadOnlyCollection<ResearchNodeRow> nodes, double maxScale)
    {
        var left = nodes.Min(node => WorldX(node.Column));
        var right = nodes.Max(node => WorldX(node.Column)) + NodeWidth;
        var top = nodes.Min(node => WorldY(node.Row)) - 24;
        var bottom = nodes.Max(node => WorldY(node.Row)) + NodeHeight;
        var scale = Math.Clamp(Math.Min((SceneViewport.ActualWidth - 70) / Math.Max(1, right - left),
                                        (SceneViewport.ActualHeight - 70) / Math.Max(1, bottom - top)), 0.18, maxScale);
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
            ThemedMessageBox.Show("내보낼 연구 변경 내용이 없습니다.", "연구 데이터 내보내기", MessageBoxButton.OK, MessageBoxImage.Information);
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
            _undoStack.Push(new UndoState(
                rebasedState,
                state.Category,
                state.SelectedNodeIds,
                state.SelectedStepColumns));
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
            ThemedMessageBox.Show("연구 DB 검증 오류를 먼저 해결하세요.", "연구 데이터 내보내기", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        catch (Exception ex)
        {
            var message = BuildResearchExportFailureMessage(ex);
            Log(ResearchConsoleSeverity.Error, message);
            ThemedMessageBox.Show(message, "연구 데이터 내보내기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private string BuildResearchExportFailureMessage(Exception exception)
    {
        if (IsFileLockException(exception))
        {
            var fileName = _paths is null
                ? "연구 DB 엑셀 파일"
                : $"'{Path.GetFileName(_paths.OutSystemPath)}' 파일";
            return $"다른 프로그램에서 {fileName}을 사용 중이라 내보낼 수 없습니다.\n\n" +
                   "Excel에서 해당 파일을 닫은 뒤 다시 시도해 주세요.";
        }

        return $"연구 데이터를 내보내지 못했습니다.\n\n오류 내용: {exception.Message}";
    }

    private static bool IsFileLockException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is IOException && (current.HResult & 0xFFFF) is 32 or 33)
                return true;
        }

        return false;
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

    private void ScheduleValidation()
    {
        _validationTimer.Stop();
        _validationTimer.Start();
    }

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
            foreach (var stepGroup in permissionGroup.GroupBy(node => node.Column).OrderBy(group => group.Key))
            {
                var step = new TreeViewItem
                {
                    Header = $"STEP {stepGroup.Key:000}  ({stepGroup.Count()} nodes)",
                    Tag = new ResearchStepTag(stepGroup.Key),
                    IsExpanded = stepGroup.Key == permissionGroup.Min(node => node.Column)
                };
                permission.Items.Add(step);
                foreach (var node in stepGroup.OrderBy(node => node.Row))
                {
                    step.Items.Add(new TreeViewItem
                    {
                        Header = $"row {node.Row}  {node.Id}",
                        Tag = node
                    });
                }
            }
        }
    }

    private void ScheduleHierarchyRefresh()
    {
        if (_pendingHierarchyRefresh is { Status: DispatcherOperationStatus.Pending })
            return;
        _pendingHierarchyRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pendingHierarchyRefresh = null;
            PopulateHierarchy();
        }));
    }

    private void HierarchyTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (HierarchyTree.SelectedItem is not TreeViewItem item)
            return;
        if (item.Tag is ResearchCategoryRow category)
        {
            _selectedNodeIdentities.Clear();
            _selectedStepColumns.Clear();
            _primaryNode = null;
            _selectedCategory = category;
            ApplySelectionVisuals();
            RenderInspector();
            return;
        }
        if (item.Tag is ResearchStepTag step)
        {
            SelectStepBlock(step.Column, updateHierarchy: false);
            return;
        }
        if (item.Tag is not ResearchNodeRow node)
            return;
        _selectedNodeIdentities.Clear();
        _selectedNodeIdentities.Add(node.RowIdentity);
        _selectedStepColumns.Clear();
        _primaryNode = node;
        ApplySelectionVisuals();
        RenderInspector();
    }

    private void SelectHierarchyStep(int column)
    {
        foreach (var root in HierarchyTree.Items.OfType<TreeViewItem>())
        {
            if (SelectHierarchyStepRecursive(root, column))
                return;
        }
    }

    private static bool SelectHierarchyStepRecursive(TreeViewItem parent, int column)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is ResearchStepTag step && step.Column == column)
            {
                item.IsSelected = true;
                item.BringIntoView();
                parent.IsExpanded = true;
                return true;
            }
            if (SelectHierarchyStepRecursive(item, column))
            {
                item.IsExpanded = true;
                parent.IsExpanded = true;
                return true;
            }
        }
        return false;
    }

    private void SelectHierarchyNode(string rowIdentity)
    {
        foreach (var root in HierarchyTree.Items.OfType<TreeViewItem>())
        {
            if (SelectHierarchyNodeRecursive(root, rowIdentity))
                return;
        }
    }

    private static bool SelectHierarchyNodeRecursive(TreeViewItem parent, string rowIdentity)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is ResearchNodeRow node && Same(node.RowIdentity, rowIdentity))
            {
                item.IsSelected = true;
                item.BringIntoView();
                return true;
            }
            if (SelectHierarchyNodeRecursive(item, rowIdentity))
            {
                item.IsExpanded = true;
                parent.IsExpanded = true;
                return true;
            }
        }
        return false;
    }

    private void RenderInspector()
    {
        _suppressInspectorCommit = true;
        InspectorPanel.Children.Clear();
        NavigatorPanel.Children.Clear();
        if (_selectedStepColumns.Count == 1)
        {
            var selectedStep = _selectedStepColumns.Min;
            RenderStepInspector(selectedStep);
            _suppressInspectorCommit = false;
            return;
        }
        if (_selectedStepColumns.Count > 1)
        {
            var nodeCount = VisibleNodes().Count(node => _selectedStepColumns.Contains(node.Column));
            AddInspectorTitle($"{_selectedStepColumns.Count:N0}개 STEP 블록 선택");
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = $"STEP {_selectedStepColumns.Min:000}~{_selectedStepColumns.Max:000}, 연구 노드 {nodeCount:N0}개가 선택되었습니다. Ctrl+C로 묶어서 복사할 수 있습니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 165)),
                TextWrapping = TextWrapping.Wrap
            });
            _suppressInspectorCommit = false;
            return;
        }
        if (_selectedNodeIdentities.Count > 1)
        {
            AddInspectorTitle($"{_selectedNodeIdentities.Count:N0} nodes selected");
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = "Delete로 선택한 노드를 함께 삭제할 수 있습니다. 배치는 STEP 상단의 노드 수 버튼으로 조정합니다.",
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

    private void RenderNavigator()
    {
        if (_selectedStepColumns.Count == 1)
        {
            var selectedStep = _selectedStepColumns.Min;
            var nodes = VisibleNodes().Where(node => node.Column == selectedStep).ToList();
            AddPanelTitle(NavigatorPanel, $"STEP {selectedStep:000}");
            AddReadOnlyField(NavigatorPanel, "nodes", nodes.Count.ToString(CultureInfo.InvariantCulture),
                "Number of nodes in this STEP block.");
            AddReadOnlyField(NavigatorPanel, "permission",
                nodes.Select(node => node.NodePermission).FirstOrDefault(value => value is not null)?.ToString(CultureInfo.InvariantCulture) ?? "",
                "Permission region assigned to this STEP block.");
            NavigatorPanel.Children.Add(new TextBlock
            {
                Text = _stepClipboard is null
                    ? "Press Ctrl+C to copy this STEP block."
                    : IsCopiedStep(selectedStep)
                        ? "Copied source. Select another STEP and press Ctrl+V."
                        : "Paste target. Press Ctrl+V to replace this STEP block's content.",
                Foreground = new SolidColorBrush(Color.FromRgb(171, 185, 194)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }
        if (_selectedStepColumns.Count > 1)
        {
            AddPanelTitle(NavigatorPanel, $"{_selectedStepColumns.Count:N0} STEP blocks");
            NavigatorPanel.Children.Add(new TextBlock
            {
                Text = $"STEP {_selectedStepColumns.Min:000}~{_selectedStepColumns.Max:000} 선택. Ctrl+C로 함께 복사하세요.",
                Foreground = new SolidColorBrush(Color.FromRgb(171, 185, 194)),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        if (_selectedNodeIdentities.Count > 1)
        {
            AddPanelTitle(NavigatorPanel, $"{_selectedNodeIdentities.Count:N0} nodes");
            NavigatorPanel.Children.Add(new TextBlock
            {
                Text = "노드 하나를 선택하면 자주 쓰는 값을 바로 수정할 수 있습니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 165)),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        if (_primaryNode is not { } node)
        {
            AddPanelTitle(NavigatorPanel, "Quick Edit");
            NavigatorPanel.Children.Add(new TextBlock
            {
                Text = "Scene에서 연구 노드를 선택하세요.",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        AddPanelTitle(NavigatorPanel, "Quick Node");
        AddReadOnlyField(NavigatorPanel, "selected node", node.Id,
            "현재 선택한 연구 노드 ID입니다.");
        AddEditableField(NavigatorPanel, "image", node.Image, value => node.Image = value,
            "연구 노드에 표시할 이미지 리소스 키입니다.");
        AddEditableField(NavigatorPanel, "active_item_id", node.ActiveItemId, value => node.ActiveItemId = value,
            "노드 활성화에 필요한 아이템 ID입니다.");
        AddEditableField(NavigatorPanel, "active_item_value", node.ActiveItemValue, value =>
        {
            node.ActiveItemValue = value;
            node.TotalRequiredHelper = ResearchWorkbookService.CalculateActiveItemTotal(value);
        }, "단계별 필요 수량입니다. 여러 값은 세미콜론으로 구분합니다.");
        AddEditableField(NavigatorPanel, "active_step", node.ActiveStep?.ToString(CultureInfo.InvariantCulture) ?? "", value =>
            node.ActiveStep = ParseNullableInt(value, "active_step"), "활성화 단계 수입니다.");
        AddReadOnlyField(NavigatorPanel, "total required",
            node.TotalRequiredHelper?.ToString("G", CultureInfo.InvariantCulture) ?? "",
            "active_item_value에 입력한 수량의 합계입니다.");
    }

    private void RenderStepInspector(int column)
    {
        var nodes = VisibleNodes().Where(node => node.Column == column).OrderBy(node => node.Row).ToList();
        AddInspectorTitle($"STEP {column:000} Block");
        AddReadOnlyField("node count", nodes.Count.ToString(CultureInfo.InvariantCulture),
            "Use the minus and plus buttons in the STEP header to set 1 to 3 nodes.");
        AddReadOnlyField("rows", string.Join(", ", nodes.Select(node => node.Row)),
            "Rows occupied by this STEP block.");
        AddReadOnlyField("permission",
            nodes.Select(node => node.NodePermission).FirstOrDefault(value => value is not null)?.ToString(CultureInfo.InvariantCulture) ?? "",
            "Permission region assigned to this STEP block.");
        InspectorPanel.Children.Add(new TextBlock
        {
            Text = _stepClipboard is null
                ? "Ctrl+C copies the whole STEP. Select another shaded STEP and press Ctrl+V to paste."
                : IsCopiedStep(column)
                    ? "This is the copied source STEP."
                    : "This is the paste target. Ctrl+V keeps its position, permission and links while replacing node content.",
            Foreground = new SolidColorBrush(Color.FromRgb(174, 184, 190)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        });
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
            if (permission is < 1)
                throw new InvalidOperationException("node_permission은 1 이상이어야 합니다.");
            node.NodePermission = permission;
        }, "연구 권한 구역 번호입니다. 1 이상의 값을 사용하며 상단 필터와 permission band에 반영됩니다.");
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
        AddReadOnlyField("column / STEP", node.Column.ToString(CultureInfo.InvariantCulture),
            "Scene 상단 STEP 번호입니다. STEP의 +/- 버튼으로 구조를 조정합니다.");
        AddReadOnlyField("row / SLOT", node.Row.ToString(CultureInfo.InvariantCulture),
            $"Scene의 고정 슬롯입니다. 사용할 수 있는 row는 {string.Join(", ", StructuredRows)}입니다.");
        RenderPreviousStepConditions(node);
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

    private void RenderPreviousStepConditions(ResearchNodeRow node)
    {
        AddInspectorSection("Previous STEP Conditions");
        if (_workbook is null || node.Column <= 1)
        {
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = "첫 STEP은 선행 조건이 없습니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                Margin = new Thickness(0, 0, 0, 8)
            });
            return;
        }

        var previousNodes = _workbook.Nodes
            .Where(candidate => Same(candidate.Category, node.Category) && candidate.Column == node.Column - 1)
            .OrderBy(candidate => candidate.Row)
            .ToList();
        if (previousNodes.Count == 0)
        {
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = "바로 전 STEP에 연결할 노드가 없습니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(235, 105, 105)),
                Margin = new Thickness(0, 0, 0, 8)
            });
            return;
        }

        foreach (var prerequisite in previousNodes)
        {
            var checkBox = new CheckBox
            {
                Content = $"row {prerequisite.Row}  {prerequisite.Id}",
                IsChecked = node.Conditions.Any(value => Same(value, prerequisite.Id)),
                Foreground = new SolidColorBrush(Color.FromRgb(218, 218, 218)),
                Margin = new Thickness(2, 3, 0, 5),
                Padding = new Thickness(3),
                ToolTip = "체크하면 이 노드를 선행 조건으로 연결합니다."
            };
            checkBox.Checked += (_, _) => SetPreviousStepCondition(node.RowIdentity, prerequisite.RowIdentity, connect: true);
            checkBox.Unchecked += (_, _) => SetPreviousStepCondition(node.RowIdentity, prerequisite.RowIdentity, connect: false);
            InspectorPanel.Children.Add(checkBox);
        }

        var previousIds = previousNodes.Select(candidate => candidate.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unresolved = node.Conditions
            .Where(value => !string.IsNullOrWhiteSpace(value) && !previousIds.Contains(value))
            .ToList();
        if (unresolved.Count > 0)
        {
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = $"다른 STEP을 참조하는 기존 값: {string.Join(", ", unresolved)}",
                Foreground = new SolidColorBrush(Color.FromRgb(235, 177, 92)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 5, 0, 7)
            });
        }
    }

    private void SetPreviousStepCondition(string targetIdentity, string prerequisiteIdentity, bool connect)
    {
        if (_workbook is null || _suppressInspectorCommit)
            return;
        var target = _workbook.Nodes.FirstOrDefault(node => Same(node.RowIdentity, targetIdentity));
        var prerequisite = _workbook.Nodes.FirstOrDefault(node => Same(node.RowIdentity, prerequisiteIdentity));
        if (target is null || prerequisite is null)
            return;

        PushUndo();
        ResearchMutationResult result;
        if (connect)
        {
            result = ResearchWorkbookService.TryConnectCondition(_workbook, prerequisite.RowIdentity, target.RowIdentity);
        }
        else
        {
            var changed = ResearchWorkbookService.DisconnectCondition(target, prerequisite.Id);
            result = changed
                ? new ResearchMutationResult { Success = true, Message = "선행 조건 연결을 해제했습니다." }
                : ResearchMutationResult.Failed("해제할 선행 조건 연결이 없습니다.");
        }

        if (!result.Success)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Warning, result.Message, target.Id);
        }
        else if (!string.IsNullOrWhiteSpace(result.WarningMessage))
        {
            Log(ResearchConsoleSeverity.Warning, result.WarningMessage, target.Id);
        }
        RenderGraph();
        PopulateHierarchy();
        _primaryNode = _workbook.Nodes.FirstOrDefault(node => Same(node.RowIdentity, targetIdentity));
        RenderInspector();
        UpdateDirtyState();
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

    private void AddInspectorTitle(string title) => AddPanelTitle(InspectorPanel, title);

    private static void AddPanelTitle(Panel panel, string title)
    {
        panel.Children.Add(new TextBlock
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
        => AddReadOnlyField(InspectorPanel, label, value, help);

    private void AddReadOnlyField(Panel panel, string label, string value, string help)
    {
        AddFieldLabel(panel, label, help);
        panel.Children.Add(new TextBox
        {
            Text = value,
            IsReadOnly = true,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            Margin = new Thickness(0, 0, 0, 8)
        });
    }

    private void AddEditableField(string label, string value, Action<string> apply, string help, bool multiline = false)
        => AddEditableField(InspectorPanel, label, value, apply, help, multiline);

    private void AddEditableField(Panel panel, string label, string value, Action<string> apply, string help, bool multiline = false)
    {
        AddFieldLabel(panel, label, help);
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
        panel.Children.Add(box);
    }

    private void AddFieldLabel(string label, string help)
        => AddFieldLabel(InspectorPanel, label, help);

    private void AddFieldLabel(Panel panel, string label, string help)
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
        panel.Children.Add(row);
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
        SetPaneVisible(tag, false);
        e.Handled = true;
    }

    private void PaneMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } item)
            return;
        SetPaneVisible(tag, item.IsChecked);
    }

    private void SetPaneVisible(string tag, bool visible)
    {
        if (!visible)
            RememberPaneSize(tag);

        switch (tag)
        {
            case "category":
                CategoryPaneMenuItem.IsChecked = visible;
                break;
            case "scene":
                ScenePaneMenuItem.IsChecked = visible;
                break;
            case "hierarchy":
                HierarchyPaneMenuItem.IsChecked = visible;
                break;
            case "inspector":
                InspectorPaneMenuItem.IsChecked = visible;
                break;
            case "console":
                ConsolePaneMenuItem.IsChecked = visible;
                break;
            default:
                return;
        }

        ApplyPaneVisibility();
        SaveLayoutSettings();
    }

    private void RememberPaneSize(string tag)
    {
        switch (tag)
        {
            case "category":
                _settings.CategoryPaneWidth = PositiveOrDefault(
                    CategoryColumn.ActualWidth, CategoryColumn.Width.Value, _settings.CategoryPaneWidth);
                break;
            case "hierarchy":
                _settings.HierarchyPaneWidth = PositiveOrDefault(
                    HierarchyColumn.ActualWidth, HierarchyColumn.Width.Value, _settings.HierarchyPaneWidth);
                goto case "right";
            case "inspector":
            case "right":
                _settings.RightPaneWidth = PositiveOrDefault(
                    RightColumn.ActualWidth, RightColumn.Width.Value, _settings.RightPaneWidth);
                break;
            case "console":
                _settings.ConsoleHeight = PositiveOrDefault(
                    ConsoleRow.ActualHeight, ConsoleRow.Height.Value, _settings.ConsoleHeight);
                break;
        }
    }

    private void ApplyPaneVisibility()
    {
        var showCategory = CategoryPaneMenuItem.IsChecked;
        var showScene = ScenePaneMenuItem.IsChecked;
        var showHierarchy = HierarchyPaneMenuItem.IsChecked;
        var showInspector = InspectorPaneMenuItem.IsChecked;
        var showConsole = ConsolePaneMenuItem.IsChecked;

        CategoryPane.Visibility = showCategory ? Visibility.Visible : Visibility.Collapsed;
        CategoryColumn.MinWidth = showCategory ? 170 : 0;
        CategoryColumn.Width = showCategory
            ? new GridLength(Math.Clamp(_settings.CategoryPaneWidth, 170, 520))
            : new GridLength(0);

        ScenePane.Visibility = showScene ? Visibility.Visible : Visibility.Collapsed;
        SceneColumn.MinWidth = showScene ? 220 : 0;
        SceneColumn.Width = showScene ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        var showCategorySceneSplitter = showCategory && showScene;
        CategorySplitter.Visibility = showCategorySceneSplitter ? Visibility.Visible : Visibility.Collapsed;
        CategorySplitterColumn.Width = showCategorySceneSplitter ? new GridLength(5) : new GridLength(0);

        ConsolePane.Visibility = showConsole ? Visibility.Visible : Visibility.Collapsed;
        ConsoleSplitter.Visibility = showConsole ? Visibility.Visible : Visibility.Collapsed;
        ConsoleSplitterRow.Height = showConsole ? new GridLength(7) : new GridLength(0);
        ConsoleRow.MinHeight = showConsole ? 130 : 0;
        ConsoleRow.Height = showConsole
            ? new GridLength(Math.Clamp(_settings.ConsoleHeight, 130, 520))
            : new GridLength(0);

        var showRight = showHierarchy || showInspector;
        RightPane.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        var showRightSplitter = showRight && (showCategory || showScene);
        RightPaneSplitter.Visibility = showRightSplitter ? Visibility.Visible : Visibility.Collapsed;
        RightPaneSplitterColumn.Width = showRightSplitter ? new GridLength(5) : new GridLength(0);
        var rightMinimum = showHierarchy && showInspector ? 520 : 260;
        RightColumn.MinWidth = showRight ? rightMinimum : 0;
        RightColumn.Width = showRight
            ? new GridLength(Math.Clamp(_settings.RightPaneWidth, rightMinimum, 1500))
            : new GridLength(0);

        HierarchyPane.Visibility = showHierarchy ? Visibility.Visible : Visibility.Collapsed;
        HierarchyColumn.MinWidth = showHierarchy ? 140 : 0;
        HierarchyColumn.Width = showHierarchy
            ? new GridLength(Math.Clamp(_settings.HierarchyPaneWidth, 140, 720))
            : new GridLength(0);

        InspectorPane.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.MinWidth = showInspector ? 120 : 0;
        InspectorColumn.Width = showInspector ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        var showInnerSplitter = showHierarchy && showInspector;
        HierarchyInspectorSplitter.Visibility = showInnerSplitter ? Visibility.Visible : Visibility.Collapsed;
        HierarchyInspectorSplitterColumn.Width = showInnerSplitter ? new GridLength(5) : new GridLength(0);
    }

    private void LayoutSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => SaveLayoutSettings();

    private void RestoreLayout()
    {
        NavigatorColumn.Width = new GridLength(0);
        NavigatorPane.Visibility = Visibility.Collapsed;
        NavigatorSplitter.Visibility = Visibility.Collapsed;
        ApplyPaneVisibility();
        GraphScale.ScaleX = GraphScale.ScaleY = Math.Clamp(_settings.Zoom, 0.18, 2.4);
        GraphTranslate.X = _settings.PanX;
        GraphTranslate.Y = _settings.PanY;
        _permissionFilter = Math.Max(0, _settings.PermissionFilter);
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
        if (CategoryPane.Visibility == Visibility.Visible && CategoryColumn.ActualWidth > 20)
            _settings.CategoryPaneWidth = CategoryColumn.ActualWidth;
        if (RightPane.Visibility == Visibility.Visible && RightColumn.ActualWidth > 20)
            _settings.RightPaneWidth = RightColumn.ActualWidth;
        if (HierarchyPane.Visibility == Visibility.Visible && HierarchyColumn.ActualWidth > 20)
            _settings.HierarchyPaneWidth = HierarchyColumn.ActualWidth;
        if (ConsolePane.Visibility == Visibility.Visible && ConsoleRow.ActualHeight >= 130)
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
            DeleteCurrentSelection();
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
        if (_allowClose)
            return;

        if (!HasUnsavedChanges)
        {
            if (ThemedMessageBox.Show(
                    this,
                    "프로그램을 종료하시겠습니까?",
                    "프로그램 종료",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
            return;
        }

        var result = WorkspaceSwitchDialog.RequestExit(
            this,
            "저장하지 않은 연구 변경이 있습니다. 변경 내용을 어떻게 처리할지 선택하세요.");
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
        var snapshot = _workbook.DeepClone(includeOriginalSnapshot: false);
        snapshot.OriginalSnapshot = _workbook.OriginalSnapshot;
        _undoStack.Push(new UndoState(
            snapshot,
            _selectedCategory?.Category,
            _selectedNodeIdentities.ToArray(),
            _selectedStepColumns.ToArray()));
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
        PopulateCategories(_selectedCategory?.Category);
        _selectedNodeIdentities.Clear();
        foreach (var identity in state.SelectedNodeIds)
            _selectedNodeIdentities.Add(identity);
        _primaryNode = _workbook.Nodes.FirstOrDefault(node => _selectedNodeIdentities.Contains(node.RowIdentity));
        _selectedStepColumns.Clear();
        _selectedStepColumns.UnionWith(state.SelectedStepColumns);
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
        _selectedStepColumns.Clear();
        _selectedStepColumns.UnionWith(state.SelectedStepColumns);
    }

    private void CopySelectedNodes()
    {
        if (_workbook is null || _selectedCategory is null)
            return;
        if (_selectedStepColumns.Count > 0)
        {
            var snapshot = ResearchWorkbookService.CaptureStepBlocks(
                _workbook, _selectedCategory.Category, _selectedStepColumns);
            if (snapshot.StepCount == 0 || snapshot.NodeCount == 0)
                return;
            _stepClipboard = snapshot;
            _nodeClipboard.Clear();
            ApplySelectionVisuals();
            RenderInspector();
            Log(ResearchConsoleSeverity.Info,
                $"STEP 블록 {snapshot.StepCount:N0}개를 복사했습니다. ({snapshot.NodeCount:N0}개 노드)",
                _selectedCategory.Category);
            return;
        }

        _stepClipboard = null;
        _nodeClipboard.Clear();
        foreach (var node in _workbook.Nodes.Where(node => _selectedNodeIdentities.Contains(node.RowIdentity)))
            _nodeClipboard.Add(new ResearchNodeClipboardItem(node.DeepClone(), _workbook.FindEffect(node)?.DeepClone()));
        ApplySelectionVisuals();
        if (_nodeClipboard.Count > 0)
            Log(ResearchConsoleSeverity.Info, $"연구 노드 {_nodeClipboard.Count:N0}개를 복사했습니다.");
    }

    private void PasteNodes()
    {
        if (_workbook is null || _selectedCategory is null)
            return;
        if (_stepClipboard is not null)
        {
            PasteStepBlock();
            return;
        }
        if (_nodeClipboard.Count == 0)
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

    private void PasteStepBlock()
    {
        if (_workbook is null || _selectedCategory is null || _stepClipboard is null)
            return;
        if (_selectedStepColumns.Count == 0)
        {
            Log(ResearchConsoleSeverity.Warning, "붙여넣을 STEP 또는 시작 STEP을 먼저 선택하세요.");
            return;
        }

        var sourceBlocks = _stepClipboard.Blocks.OrderBy(block => block.SourceColumn).ToArray();
        int[] targetColumns;
        if (_selectedStepColumns.Count == 1)
        {
            targetColumns = Enumerable.Range(_selectedStepColumns.Min, sourceBlocks.Length).ToArray();
        }
        else if (_selectedStepColumns.Count == sourceBlocks.Length)
        {
            targetColumns = _selectedStepColumns.ToArray();
        }
        else
        {
            Log(ResearchConsoleSeverity.Warning,
                $"복사한 STEP은 {sourceBlocks.Length:N0}개입니다. 붙여넣을 STEP {sourceBlocks.Length:N0}개 또는 시작 STEP 하나를 선택하세요.");
            return;
        }

        var availableColumns = _workbook.Nodes
            .Where(node => Same(node.Category, _selectedCategory.Category))
            .Select(node => node.Column)
            .ToHashSet();
        if (targetColumns.Any(column => !availableColumns.Contains(column)))
        {
            Log(ResearchConsoleSeverity.Warning, "붙여넣을 범위에 아직 생성되지 않은 STEP이 있습니다. 우측 + STEP으로 먼저 추가하세요.");
            return;
        }
        if (Same(_stepClipboard.SourceCategory, _selectedCategory.Category)
            && sourceBlocks.Select(block => block.SourceColumn).SequenceEqual(targetColumns))
        {
            Log(ResearchConsoleSeverity.Warning, "복사한 STEP 묶음과 붙여넣을 STEP 묶음이 같습니다.");
            return;
        }

        var category = _selectedCategory.Category;
        PushUndo();
        for (var index = 0; index < sourceBlocks.Length; index++)
        {
            var result = ResearchWorkbookService.TryPasteStepBlock(
                _workbook, sourceBlocks[index], category, targetColumns[index]);
            if (!result.Success)
            {
                UndoWithoutRender();
                Log(ResearchConsoleSeverity.Error, result.Message, category);
                return;
            }
        }

        _selectedCategory = _workbook.FindCategory(category);
        _selectedNodeIdentities.Clear();
        _primaryNode = null;
        _selectedStepColumns.Clear();
        _selectedStepColumns.UnionWith(targetColumns);
        RenderGraph();
        PopulateHierarchy();
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info,
            $"STEP 블록 {sourceBlocks.Length:N0}개를 STEP {targetColumns.Min():000}~{targetColumns.Max():000}에 붙여넣었습니다.",
            category);
    }

    private void DeleteCurrentSelection()
    {
        if (_workbook is null)
            return;

        if (_selectedNodeIdentities.Count > 0)
        {
            DeleteSelectedNodes();
            return;
        }

        if (_selectedStepColumns.Count == 0)
            return;

        var identities = VisibleNodes()
            .Where(node => _selectedStepColumns.Contains(node.Column))
            .Select(node => node.RowIdentity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (identities.Length == 0)
            return;

        DeleteNodes(
            identities,
            $"선택한 STEP {_selectedStepColumns.Count:N0}개의 연구 노드 {identities.Length:N0}개를 삭제할까요? 참조 중인 조건선도 함께 제거됩니다.");
    }

    private void DeleteSelectedNodes() =>
        DeleteNodes(
            _selectedNodeIdentities.ToArray(),
            $"선택한 연구 노드 {_selectedNodeIdentities.Count:N0}개를 삭제할까요? 참조 중인 조건선도 함께 제거됩니다.");

    private void DeleteNodes(IReadOnlyCollection<string> identities, string confirmation)
    {
        if (_workbook is null || identities.Count == 0)
            return;
        if (ThemedMessageBox.Show(confirmation,
                "연구 노드 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        CloseQuickNodePopup();
        var identitySet = identities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affectedColumns = _workbook.Nodes
            .Where(node => identitySet.Contains(node.RowIdentity))
            .Select(node => node.Column)
            .Distinct()
            .ToArray();
        PushUndo();
        var result = ResearchWorkbookService.TryDeleteNodesInPlace(
            _workbook,
            identities,
            removeConditionReferences: true);
        if (!result.Success)
        {
            UndoWithoutRender();
            Log(ResearchConsoleSeverity.Error, result.Message);
            return;
        }
        _selectedNodeIdentities.Clear();
        _selectedStepColumns.Clear();
        _primaryNode = null;
        var requiresFullRender = affectedColumns.Any(column =>
            !VisibleNodes().Any(node => node.Column == column));
        if (requiresFullRender)
        {
            RenderGraph();
        }
        else
        {
            foreach (var identity in identities)
                RemoveNodeVisual(identity);
            DrawAllLinks();
            RefreshEmptySlotTargets();
            foreach (var column in affectedColumns)
                RefreshStepHeaderCount(column);
            ApplySelectionVisuals();
            UpdateSceneStatus();
        }
        ScheduleHierarchyRefresh();
        RenderInspector();
        ScheduleValidation();
        UpdateDirtyState();
        Log(ResearchConsoleSeverity.Info, result.Message);
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
