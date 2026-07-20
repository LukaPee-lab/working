using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NexusEditor;

public sealed class ResearchExportPreviewWindow : Window
{
    private readonly Func<IReadOnlyList<ResearchDiffEntry>> _getDiff;
    private readonly Action<IReadOnlyCollection<ResearchDiffEntry>> _revert;
    private readonly Func<IReadOnlyCollection<ResearchDiffEntry>, string, ResearchSaveResult> _export;
    private readonly ObservableCollection<DiffRow> _rows = [];
    private readonly DataGrid _grid;
    private readonly TextBox _beforeBox;
    private readonly TextBox _afterBox;
    private readonly TextBox _exportIdBox;
    private readonly TextBlock _summary;

    public ResearchExportPreviewWindow(
        Func<IReadOnlyList<ResearchDiffEntry>> getDiff,
        Action<IReadOnlyCollection<ResearchDiffEntry>> revertAction,
        Func<IReadOnlyCollection<ResearchDiffEntry>, string, ResearchSaveResult> export,
        string lastExportId)
    {
        _getDiff = getDiff;
        _revert = revertAction;
        _export = export;

        Title = "Research Export Preview";
        Width = 1320;
        Height = 820;
        MinWidth = 980;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#202020");
        Foreground = Brush("#dedede");
        FontFamily = new FontFamily("Segoe UI");

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Export ID",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _exportIdBox = CreateTextBox();
        _exportIdBox.Text = lastExportId;
        Grid.SetColumn(_exportIdBox, 1);
        header.Children.Add(_exportIdBox);
        _summary = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#aaaaaa"),
            Margin = new Thickness(14, 0, 0, 0)
        };
        Grid.SetColumn(_summary, 2);
        header.Children.Add(_summary);
        var toggleAll = CreateButton("모두 선택/해제", 118);
        toggleAll.Click += (_, _) => ToggleAll();
        Grid.SetColumn(toggleAll, 3);
        header.Children.Add(toggleAll);
        root.Children.Add(header);

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Background = Brush("#202020"),
            Foreground = Brush("#dddddd"),
            BorderBrush = Brush("#444444"),
            RowBackground = Brush("#242424"),
            AlternatingRowBackground = Brush("#292929"),
            HorizontalGridLinesBrush = Brush("#383838"),
            VerticalGridLinesBrush = Brush("#383838"),
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            ItemsSource = _rows
        };
        _grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "✓",
            Width = 40,
            Binding = new Binding(nameof(DiffRow.IsIncluded)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
        });
        _grid.Columns.Add(TextColumn("Sheet", nameof(DiffRow.Sheet), 175));
        _grid.Columns.Add(TextColumn("Change", nameof(DiffRow.Change), 75));
        _grid.Columns.Add(TextColumn("ID", nameof(DiffRow.Key), 260));
        _grid.Columns.Add(TextColumn("Changed Columns", nameof(DiffRow.Columns), new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.SelectionChanged += (_, _) => ShowSelectedDiff();
        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var compare = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        compare.ColumnDefinitions.Add(new ColumnDefinition());
        compare.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        compare.ColumnDefinitions.Add(new ColumnDefinition());
        compare.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        compare.RowDefinitions.Add(new RowDefinition());
        compare.Children.Add(new TextBlock { Text = "Before", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4) });
        var afterTitle = new TextBlock { Text = "After", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4) };
        Grid.SetColumn(afterTitle, 2);
        compare.Children.Add(afterTitle);
        _beforeBox = CreateCompareBox();
        _afterBox = CreateCompareBox();
        Grid.SetRow(_beforeBox, 1);
        Grid.SetRow(_afterBox, 1);
        Grid.SetColumn(_afterBox, 2);
        compare.Children.Add(_beforeBox);
        compare.Children.Add(_afterBox);
        Grid.SetRow(compare, 2);
        root.Children.Add(compare);

        var footer = new DockPanel();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        var close = CreateButton("닫기", 88);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var exportButton = CreateButton("선택 항목 Export", 138);
        exportButton.Margin = new Thickness(0, 0, 8, 0);
        exportButton.Click += (_, _) => ExportSelected();
        DockPanel.SetDock(exportButton, Dock.Right);
        footer.Children.Add(exportButton);
        var revertButton = CreateButton("선택 변경 되돌리기", 148);
        revertButton.Click += (_, _) => RevertSelected();
        DockPanel.SetDock(revertButton, Dock.Left);
        footer.Children.Add(revertButton);

        Content = root;
        RefreshRows();
    }

    public string ExportId => _exportIdBox.Text.Trim();
    public bool ExportCompleted { get; private set; }

    private void RefreshRows()
    {
        _rows.Clear();
        foreach (var item in _getDiff())
            _rows.Add(new DiffRow(item));
        _summary.Text = $"변경 {_rows.Count:N0}건 / 선택 {_rows.Count(row => row.IsIncluded):N0}건";
        ShowSelectedDiff();
    }

    private void ToggleAll()
    {
        var selectedRows = _grid.SelectedItems.Cast<DiffRow>().ToList();
        var targets = selectedRows.Count > 0 ? selectedRows : _rows.ToList();
        var newValue = targets.All(row => row.IsIncluded);
        foreach (var row in targets)
            row.IsIncluded = !newValue;
        _summary.Text = $"변경 {_rows.Count:N0}건 / 선택 {_rows.Count(row => row.IsIncluded):N0}건";
    }

    private void ShowSelectedDiff()
    {
        if (_grid.SelectedItem is not DiffRow row)
        {
            _beforeBox.Text = "";
            _afterBox.Text = "";
            return;
        }
        _beforeBox.Text = row.Entry.BeforeValue;
        _afterBox.Text = row.Entry.AfterValue;
    }

    private void RevertSelected()
    {
        var selected = _grid.SelectedItems.Cast<DiffRow>().ToList();
        if (selected.Count == 0 && _grid.SelectedItem is DiffRow single)
            selected.Add(single);
        if (selected.Count == 0)
            return;

        _revert(selected.Select(row => row.Entry).ToList());
        RefreshRows();
    }

    private void ExportSelected()
    {
        var selected = _rows.Where(row => row.IsIncluded).Select(row => row.Entry).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageBox.Show("Export할 변경 항목을 선택하세요.", "Research Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(ExportId))
        {
            ThemedMessageBox.Show("Export ID를 입력하세요.", "Research Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var result = _export(selected, ExportId);
            var errors = result.NewValidationIssues.Count;
            if (errors > 0)
            {
                ThemedMessageBox.Show($"검증 오류 {errors}건 때문에 Export하지 못했습니다.", "Research Export", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            ExportCompleted = true;
            ThemedMessageBox.Show(
                $"연구 DB를 저장했습니다.\n변경 행: {result.ChangedRowCount:N0}개\n\n{result.OutSystemPath}\n{result.EffectPath}",
                "Research Export", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshRows();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(ex.Message, "Research Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static DataGridTextColumn TextColumn(string header, string binding, DataGridLength width) => new()
    {
        Header = header,
        Width = width,
        Binding = new Binding(binding)
    };

    private static TextBox CreateTextBox() => new()
    {
        Height = 30,
        Padding = new Thickness(7, 4, 7, 4),
        Background = Brush("#191919"),
        Foreground = Brush("#eeeeee"),
        BorderBrush = Brush("#555555")
    };

    private static TextBox CreateCompareBox() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(8),
        Background = Brush("#191919"),
        Foreground = Brush("#dddddd"),
        BorderBrush = Brush("#444444")
    };

    private static Button CreateButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 30,
        Background = Brush("#3a3a3a"),
        Foreground = Brushes.White,
        BorderBrush = Brush("#575757")
    };

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private sealed class DiffRow : INotifyPropertyChanged
    {
        private bool _isIncluded = true;

        public DiffRow(ResearchDiffEntry entry) => Entry = entry;

        public ResearchDiffEntry Entry { get; }
        public string Sheet => Entry.Sheet;
        public string Change => Entry.ChangeType.ToString();
        public string Key => Entry.Key;
        public string Columns => string.Join(", ", Entry.ChangedColumns);
        public bool IsIncluded
        {
            get => _isIncluded;
            set
            {
                if (_isIncluded == value)
                    return;
                _isIncluded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIncluded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
