using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DimensionEventEditor;

public sealed class ClickSoundPickerWindow : Window
{
    private readonly ClickSoundCatalogService _service;
    private readonly string _currentValue;
    private readonly TextBox _searchBox;
    private readonly DataGrid _grid;
    private readonly TextBlock _status;
    private readonly TextBlock _selection;
    private readonly Button _playButton;
    private readonly Button _selectButton;
    private readonly MediaPlayer _player = new();
    private CancellationTokenSource? _playCts;
    private List<ClickSoundEntry> _sounds = [];
    private ICollectionView? _view;

    public string? SelectedClickSound { get; private set; }

    public ClickSoundPickerWindow(string devRoot, string cacheRoot, string currentValue)
    {
        _service = new ClickSoundCatalogService(devRoot, cacheRoot);
        _currentValue = currentValue.Trim();
        Title = "Select Click Sound";
        Width = 980;
        Height = 680;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush(30, 30, 30);
        Foreground = Brush(225, 225, 225);
        FontFamily = new FontFamily("Segoe UI");

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.Children.Add(new TextBlock
        {
            Text = "Search",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = Brush(180, 180, 180)
        });
        _searchBox = new TextBox
        {
            Height = 30,
            Padding = new Thickness(8, 4, 8, 4),
            Background = Brush(22, 22, 22),
            Foreground = Brushes.White,
            BorderBrush = Brush(76, 76, 76)
        };
        _searchBox.TextChanged += (_, _) => _view?.Refresh();
        Grid.SetColumn(_searchBox, 1);
        searchRow.Children.Add(_searchBox);
        root.Children.Add(searchRow);

        _status = new TextBlock
        {
            Text = "Loading sound catalog...",
            Foreground = Brush(155, 190, 220),
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            Background = Brush(24, 24, 24),
            Foreground = Brush(225, 225, 225),
            RowBackground = Brush(33, 33, 33),
            AlternatingRowBackground = Brush(38, 38, 38),
            BorderBrush = Brush(70, 70, 70),
            HorizontalGridLinesBrush = Brush(57, 57, 57),
            VerticalGridLinesBrush = Brush(57, 57, 57)
        };
        _grid.ColumnHeaderStyle = new Style(typeof(DataGridColumnHeader))
        {
            Setters =
            {
                new Setter(Control.BackgroundProperty, Brush(48, 48, 48)),
                new Setter(Control.ForegroundProperty, Brushes.White),
                new Setter(Control.BorderBrushProperty, Brush(76, 76, 76)),
                new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5))
            }
        };
        _grid.Columns.Add(new DataGridTextColumn { Header = "FMOD Path", Binding = new Binding(nameof(ClickSoundEntry.FmodPath)), Width = new DataGridLength(2.4, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Stream", Binding = new Binding(nameof(ClickSoundEntry.Name)), Width = new DataGridLength(1.5, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Bank", Binding = new Binding(nameof(ClickSoundEntry.RelativeBankPath)), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Subsong", Binding = new Binding(nameof(ClickSoundEntry.Subsong)), Width = 80 });
        _grid.SelectionChanged += (_, _) => UpdateSelection();
        _grid.MouseDoubleClick += (_, _) => AcceptSelection();
        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        _selection = new TextBlock
        {
            Text = "Select a sound.",
            Margin = new Thickness(2, 10, 2, 10),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(195, 195, 195)
        };
        Grid.SetRow(_selection, 3);
        root.Children.Add(_selection);

        var footer = new DockPanel { LastChildFill = false };
        _playButton = CreateButton("Play", 88);
        _playButton.IsEnabled = false;
        _playButton.Click += async (_, _) => await PlaySelectedAsync();
        DockPanel.SetDock(_playButton, Dock.Left);
        footer.Children.Add(_playButton);
        var stopButton = CreateButton("Stop", 88);
        stopButton.Click += (_, _) => StopPlayback();
        DockPanel.SetDock(stopButton, Dock.Left);
        footer.Children.Add(stopButton);

        var cancelButton = CreateButton("Cancel", 92);
        cancelButton.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(cancelButton, Dock.Right);
        footer.Children.Add(cancelButton);
        _selectButton = CreateButton("Select", 92);
        _selectButton.IsEnabled = false;
        _selectButton.Margin = new Thickness(0, 0, 8, 0);
        _selectButton.Click += (_, _) => AcceptSelection();
        DockPanel.SetDock(_selectButton, Dock.Right);
        footer.Children.Add(_selectButton);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        Content = root;
        Loaded += async (_, _) => await LoadCatalogAsync();
        Closed += (_, _) => StopPlayback();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
                Close();
            else if (args.Key == Key.Space && _grid.SelectedItem is ClickSoundEntry)
            {
                args.Handled = true;
                _ = PlaySelectedAsync();
            }
        };
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            var progress = new Progress<ClickSoundScanProgress>(item =>
            {
                _status.Text = $"Indexing banks {item.Processed}/{item.Total}  {item.CurrentBank}";
            });
            var catalog = await _service.LoadCatalogAsync(progress, CancellationToken.None);
            _sounds = catalog.Sounds;
            _view = CollectionViewSource.GetDefaultView(_sounds);
            _view.Filter = FilterSound;
            _grid.ItemsSource = _view;
            _status.Text = $"{_sounds.Count:N0} sounds / cache: {_service.CacheRoot}";
            var current = _sounds.FirstOrDefault(sound => string.Equals(sound.FmodPath, _currentValue, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                _grid.SelectedItem = current;
                _grid.ScrollIntoView(current);
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Sound catalog error: {ex.Message}";
        }
    }

    private bool FilterSound(object item)
    {
        if (item is not ClickSoundEntry sound)
            return false;
        var query = _searchBox.Text.Trim();
        return query.Length == 0
               || sound.FmodPath.Contains(query, StringComparison.OrdinalIgnoreCase)
               || sound.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || sound.RelativeBankPath.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSelection()
    {
        var sound = _grid.SelectedItem as ClickSoundEntry;
        _playButton.IsEnabled = sound is not null && _service.DecoderAvailable;
        _selectButton.IsEnabled = sound is not null;
        _selection.Text = sound is null
            ? "Select a sound."
            : $"{sound.FmodPath}\n{sound.RelativeBankPath} / subsong {sound.Subsong} / {(_service.IsCached(sound) ? "WAV cached" : "WAV not cached")}";
    }

    private async Task PlaySelectedAsync()
    {
        if (_grid.SelectedItem is not ClickSoundEntry sound)
            return;
        StopPlayback();
        _playCts = new CancellationTokenSource();
        try
        {
            _playButton.IsEnabled = false;
            _status.Text = _service.IsCached(sound) ? "Opening cached WAV..." : "Decoding selected sound to external cache...";
            var wav = await _service.EnsureDecodedAsync(sound, _playCts.Token);
            _player.Open(new Uri(wav, UriKind.Absolute));
            _player.Play();
            _status.Text = $"Playing: {sound.FmodPath}";
            UpdateSelection();
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Playback stopped.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Playback error: {ex.Message}";
        }
        finally
        {
            _playButton.IsEnabled = _grid.SelectedItem is ClickSoundEntry && _service.DecoderAvailable;
        }
    }

    private void StopPlayback()
    {
        _playCts?.Cancel();
        _playCts?.Dispose();
        _playCts = null;
        _player.Stop();
        _player.Close();
    }

    private void AcceptSelection()
    {
        if (_grid.SelectedItem is not ClickSoundEntry sound)
            return;
        SelectedClickSound = sound.FmodPath;
        DialogResult = true;
        Close();
    }

    private static Button CreateButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 32,
        Margin = new Thickness(0, 0, 8, 0),
        Background = Brush(58, 58, 58),
        Foreground = Brushes.White,
        BorderBrush = Brush(92, 92, 92),
        Padding = new Thickness(8, 3, 8, 3)
    };

    private static SolidColorBrush Brush(byte red, byte green, byte blue)
        => new(Color.FromRgb(red, green, blue));
}
