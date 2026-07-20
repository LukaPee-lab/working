using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IoPath = System.IO.Path;

namespace NexusEditor;

public sealed class BackgroundImagePickerWindow : Window
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"
    };

    private readonly string _homeRoot;
    private readonly string _initialValue;
    private readonly TextBox _pathBox;
    private readonly TextBox _searchBox;
    private readonly TextBlock _statusText;
    private readonly TextBlock _selectedText;
    private readonly Image _preview;
    private readonly WrapPanel _itemsPanel;
    private readonly List<BackgroundImageItem> _allItems = [];
    private readonly Dictionary<BackgroundImageItem, Button> _tileButtons = [];
    private BackgroundImageItem? _selectedItem;

    public BackgroundImagePickerWindow(string homeRoot, string? startRoot, string? currentValue)
    {
        _homeRoot = homeRoot;
        CurrentRoot = Directory.Exists(startRoot) ? startRoot! : homeRoot;
        _initialValue = currentValue?.Trim() ?? "";

        Title = "Select Background";
        Width = 920;
        Height = 650;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush(34, 34, 34);
        Foreground = Brush(220, 220, 220);

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var home = Button("Home", 64);
        home.ToolTip = _homeRoot;
        Grid.SetColumn(home, 0);
        toolbar.Children.Add(home);

        _pathBox = TextBox(CurrentRoot);
        _pathBox.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(_pathBox, 1);
        toolbar.Children.Add(_pathBox);
        home.Click += (_, _) =>
        {
            CurrentRoot = _homeRoot;
            _pathBox.Text = CurrentRoot;
            LoadImages();
        };

        var load = Button("Load", 64);
        load.Click += (_, _) => LoadPathFromTextBox();
        Grid.SetColumn(load, 2);
        toolbar.Children.Add(load);

        _searchBox = TextBox("");
        _searchBox.Margin = new Thickness(6, 0, 0, 0);
        _searchBox.ToolTip = "Search";
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        Grid.SetColumn(_searchBox, 3);
        toolbar.Children.Add(_searchBox);

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        Grid.SetRow(contentGrid, 1);
        root.Children.Add(contentGrid);

        _itemsPanel = new WrapPanel { Margin = new Thickness(4) };
        var scroll = new ScrollViewer
        {
            Content = _itemsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brush(42, 42, 42)
        };
        Grid.SetColumn(scroll, 0);
        contentGrid.Children.Add(scroll);

        var previewPanel = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(previewPanel, 1);
        contentGrid.Children.Add(previewPanel);

        previewPanel.Children.Add(new TextBlock
        {
            Text = "Preview",
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var previewBorder = new Border
        {
            Height = 210,
            Background = Brush(20, 20, 20),
            BorderBrush = Brush(70, 70, 70),
            BorderThickness = new Thickness(1),
            Child = _preview = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6) }
        };
        previewPanel.Children.Add(previewBorder);

        _selectedText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = Brush(210, 210, 210)
        };
        previewPanel.Children.Add(_selectedText);

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = Brush(160, 160, 160)
        };
        previewPanel.Children.Add(_statusText);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        var cancel = Button("Cancel", 90);
        cancel.Click += (_, _) => DialogResult = false;
        DockPanel.SetDock(cancel, Dock.Right);
        bottom.Children.Add(cancel);

        var select = Button("Select", 90);
        select.Margin = new Thickness(0, 0, 8, 0);
        select.Click += (_, _) => AcceptSelected();
        DockPanel.SetDock(select, Dock.Right);
        bottom.Children.Add(select);

        var hint = new TextBlock
        {
            Text = "Double click selects. The background value is saved as a resource key relative to the nexus folder.",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(150, 150, 150)
        };
        bottom.Children.Add(hint);

        Loaded += (_, _) => LoadImages();
    }

    public string CurrentRoot { get; private set; }
    public string? SelectedResourceKey { get; private set; }
    public string? SelectedImagePath { get; private set; }

    private void LoadPathFromTextBox()
    {
        var path = _pathBox.Text.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            ThemedMessageBox.Show(this, "Image folder path is invalid.", "Invalid path", MessageBoxButton.OK, MessageBoxImage.Warning);
            _pathBox.Text = CurrentRoot;
            return;
        }

        CurrentRoot = path;
        LoadImages();
    }

    private void LoadImages()
    {
        _allItems.Clear();
        _tileButtons.Clear();
        _itemsPanel.Children.Clear();
        _preview.Source = null;
        _selectedItem = null;
        SelectedResourceKey = null;
        SelectedImagePath = null;

        _allItems.Add(BackgroundImageItem.None());
        if (!Directory.Exists(CurrentRoot))
        {
            _statusText.Text = $"Folder not found: {CurrentRoot}";
            ApplyFilter();
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(CurrentRoot, "*.*", SearchOption.AllDirectories)
                .Where(path => SupportedExtensions.Contains(IoPath.GetExtension(path)))
                .OrderBy(path => GetResourceKey(path), StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var key = GetResourceKey(file);
                _allItems.Add(new BackgroundImageItem(file, key, IoPath.GetFileNameWithoutExtension(file), LoadBitmap(file, 112)));
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
        }

        _statusText.Text = $"{Math.Max(0, _allItems.Count - 1)} images loaded from {CurrentRoot}";
        ApplyFilter();
        SelectInitialValue();
    }

    private void ApplyFilter()
    {
        _tileButtons.Clear();
        _itemsPanel.Children.Clear();
        var query = _searchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allItems
            : _allItems.Where(item =>
                item.ResourceKey.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var item in filtered)
        {
            var tile = CreateTile(item);
            _tileButtons[item] = tile;
            _itemsPanel.Children.Add(tile);
        }
        UpdateTileSelection();
    }

    private Button CreateTile(BackgroundImageItem item)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        if (item.Thumbnail is not null)
        {
            stack.Children.Add(new Image
            {
                Source = item.Thumbnail,
                Width = 112,
                Height = 82,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(4, 4, 4, 2)
            });
        }
        else
        {
            stack.Children.Add(new Border
            {
                Width = 112,
                Height = 82,
                Margin = new Thickness(4, 4, 4, 2),
                Background = Brush(28, 28, 28),
                BorderBrush = Brush(75, 75, 75),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = item.IsNone ? "None" : "No Preview",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush(170, 170, 170)
                }
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = item.DisplayName,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(225, 225, 225),
            MaxHeight = 40,
            Margin = new Thickness(4, 2, 4, 4)
        });

        var button = new Button
        {
            Content = stack,
            Tag = item,
            Width = 134,
            Height = 136,
            Margin = new Thickness(4),
            Padding = new Thickness(0),
            Background = Brush(55, 55, 55),
            BorderBrush = Brush(72, 72, 72),
            BorderThickness = new Thickness(1)
        };
        button.Click += (_, _) => SelectItem(item);
        button.MouseDoubleClick += (_, _) => AcceptSelected();
        return button;
    }

    private void SelectInitialValue()
    {
        if (string.IsNullOrWhiteSpace(_initialValue))
            return;

        var item = _allItems.FirstOrDefault(candidate =>
            string.Equals(candidate.ResourceKey, _initialValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.DisplayName, _initialValue, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            SelectItem(item);
    }

    private void SelectItem(BackgroundImageItem item)
    {
        _selectedItem = item;
        SelectedResourceKey = item.ResourceKey;
        SelectedImagePath = item.FilePath;
        _preview.Source = item.FilePath is null ? null : LoadBitmap(item.FilePath, 420);
        _selectedText.Text = item.IsNone
            ? "Selected: None"
            : $"Selected: {item.ResourceKey}\n{item.FilePath}";
        UpdateTileSelection();
    }

    private void UpdateTileSelection()
    {
        foreach (var (item, button) in _tileButtons)
        {
            var selected = ReferenceEquals(item, _selectedItem);
            button.Background = selected ? Brush(68, 85, 110) : Brush(55, 55, 55);
            button.BorderBrush = selected ? Brush(125, 165, 220) : Brush(72, 72, 72);
            button.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
        }
    }

    private void AcceptSelected()
    {
        if (_selectedItem is null)
        {
            ThemedMessageBox.Show(this, "Select an image first.", "Select Background", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private string GetResourceKey(string path)
    {
        var baseRoot = Directory.Exists(_homeRoot) && IsSubPath(path, _homeRoot) ? _homeRoot : CurrentRoot;
        var relative = IoPath.GetRelativePath(baseRoot, path);
        relative = IoPath.ChangeExtension(relative, null) ?? relative;
        return relative.Replace('\\', '/');
    }

    private static bool IsSubPath(string path, string root)
    {
        var fullPath = IoPath.GetFullPath(path).TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar);
        var fullRoot = IoPath.GetFullPath(root).TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot + IoPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static ImageSource? LoadBitmap(string path, int decodePixelWidth)
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

    private static TextBox TextBox(string text) => new()
    {
        Text = text,
        Height = 30,
        MinHeight = 30,
        Background = Brush(38, 38, 38),
        Foreground = Brush(230, 230, 230),
        BorderBrush = Brush(75, 75, 75),
        CaretBrush = Brushes.White,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static Button Button(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 30,
        MinHeight = 30,
        Padding = new Thickness(8, 2, 8, 2),
        Background = Brush(58, 58, 58),
        Foreground = Brush(230, 230, 230),
        BorderBrush = Brush(86, 86, 86)
    };

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private sealed class BackgroundImageItem
    {
        public BackgroundImageItem(string? filePath, string resourceKey, string displayName, ImageSource? thumbnail)
        {
            FilePath = filePath;
            ResourceKey = resourceKey;
            DisplayName = displayName;
            Thumbnail = thumbnail;
        }

        public string? FilePath { get; }
        public string ResourceKey { get; }
        public string DisplayName { get; }
        public ImageSource? Thumbnail { get; }
        public bool IsNone => FilePath is null;

        public static BackgroundImageItem None() => new(null, "", "None", null);
    }
}

