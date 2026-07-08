using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DimensionEventEditor;

public sealed class EventInfoWindow : Window
{
    private readonly EventInfoReport _report;
    private readonly Action<EventInfoLocation>? _navigate;

    public EventInfoWindow(EventInfoReport report, Action<EventInfoLocation>? navigate = null)
    {
        _report = report;
        _navigate = navigate;
        Title = "Event info";
        Width = 1180;
        Height = 760;
        MinWidth = 860;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#252525");
        Foreground = Brush("#d8d8d8");

        var root = new DockPanel { LastChildFill = true };
        Content = root;

        var header = new Border
        {
            Background = Brush("#2f2f2f"),
            BorderBrush = Brush("#151515"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 9, 12, 8)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Event info",
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#ffffff")
                },
                new TextBlock
                {
                    Text = "현재 로드된 이벤트 테이블을 기준으로 보상, 비용, 난이도, 전투, 흐름 정보를 집계합니다. 기존 Export Preview에서 확정하면 같은 내용이 한글 시트로 저장됩니다.",
                    Margin = new Thickness(0, 4, 0, 0),
                    FontSize = 12,
                    Foreground = Brush("#b8b8b8")
                }
            }
        };

        var tabs = new TabControl
        {
            Background = Brush("#1f1f1f"),
            BorderBrush = Brush("#111111"),
            Margin = new Thickness(8)
        };
        root.Children.Add(tabs);

        foreach (var table in report.Tables)
        {
            tabs.Items.Add(new TabItem
            {
                Header = table.Name,
                Content = BuildTableTab(table)
            });
        }
    }

    private Grid BuildTableTab(EventInfoTable table)
    {
        var grid = new Grid
        {
            Background = Brush("#1f1f1f"),
            Margin = new Thickness(0)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var description = new TextBlock
        {
            Text = $"{table.Description}  /  rows: {table.Rows.Count}",
            Foreground = Brush("#b8b8b8"),
            Margin = new Thickness(10, 8, 10, 7),
            TextWrapping = TextWrapping.Wrap
        };
        grid.Children.Add(description);

        var dataGrid = new DataGrid
        {
            ItemsSource = ToDataTable(table).DefaultView,
            IsReadOnly = true,
            AutoGenerateColumns = true,
            CanUserAddRows = false,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = Brush("#1f1f1f"),
            Foreground = Brush("#d8d8d8"),
            RowBackground = Brush("#242424"),
            AlternatingRowBackground = Brush("#2a2a2a"),
            BorderBrush = Brush("#111111"),
            HorizontalGridLinesBrush = Brush("#3a3a3a"),
            VerticalGridLinesBrush = Brush("#303030"),
            ColumnHeaderHeight = 28,
            RowHeight = 24,
            Margin = new Thickness(8, 0, 8, 8)
        };
        Grid.SetRow(dataGrid, 1);
        grid.Children.Add(dataGrid);

        var splitter = new GridSplitter
        {
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brush("#333333")
        };
        Grid.SetRow(splitter, 2);
        grid.Children.Add(splitter);

        var detailsDock = new DockPanel
        {
            Margin = new Thickness(8, 3, 8, 8),
            Background = Brush("#202020")
        };
        Grid.SetRow(detailsDock, 3);
        grid.Children.Add(detailsDock);

        var detailsTitle = new TextBlock
        {
            Text = "세부 위치: 집계 행을 선택하면 표시됩니다.",
            Foreground = Brush("#b8b8b8"),
            FontSize = 12,
            Margin = new Thickness(4, 0, 4, 5)
        };
        DockPanel.SetDock(detailsTitle, Dock.Top);
        detailsDock.Children.Add(detailsTitle);

        var detailsGrid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = Brush("#202020"),
            Foreground = Brush("#d8d8d8"),
            RowBackground = Brush("#242424"),
            AlternatingRowBackground = Brush("#292929"),
            BorderBrush = Brush("#111111"),
            HorizontalGridLinesBrush = Brush("#3a3a3a"),
            VerticalGridLinesBrush = Brush("#303030"),
            ColumnHeaderHeight = 26,
            RowHeight = 24
        };
        AddDetailColumn(detailsGrid, "Event", "EventId", 92);
        AddDetailColumn(detailsGrid, "Name", "EventName", 160);
        AddDetailColumn(detailsGrid, "Group", "GroupId", 150);
        AddDetailColumn(detailsGrid, "Choice", "ChoiceId", 170);
        AddDetailColumn(detailsGrid, "Branch", "Branch", 70);
        AddDetailColumn(detailsGrid, "Kind", "Kind", 72);
        AddDetailColumn(detailsGrid, "Detail", "Detail", 360);
        detailsGrid.MouseDoubleClick += (_, _) =>
        {
            if (detailsGrid.SelectedItem is EventInfoLocation location)
                _navigate?.Invoke(location);
        };
        detailsDock.Children.Add(detailsGrid);

        dataGrid.SelectionChanged += (_, _) => UpdateDetails(table, dataGrid, detailsGrid, detailsTitle);
        dataGrid.MouseDoubleClick += (_, _) =>
        {
            if (detailsGrid.Items.Count == 1 && detailsGrid.Items[0] is EventInfoLocation location)
                _navigate?.Invoke(location);
        };
        return grid;
    }

    private void UpdateDetails(EventInfoTable table, DataGrid sourceGrid, DataGrid detailGrid, TextBlock detailTitle)
    {
        if (sourceGrid.SelectedItem is not DataRowView row)
        {
            detailGrid.ItemsSource = null;
            detailTitle.Text = "세부 위치: 집계 행을 선택하면 표시됩니다.";
            return;
        }

        var key = TableRowKey(table, row);
        var details = FindLocations(table, key);
        detailGrid.ItemsSource = details;
        detailTitle.Text = details.Count == 0
            ? $"세부 위치: {key} / 연결된 노드 위치가 없습니다."
            : $"세부 위치: {key} / {details.Count}건  (더블클릭하면 Scene에서 해당 노드를 보여줍니다.)";
    }

    private List<EventInfoLocation> FindLocations(EventInfoTable table, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return [];

        if (table.Name is "이벤트별 요약" or "이벤트 난이도")
        {
            return _report.Locations
                .Where(l => string.Equals(l.EventId, key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => l.Kind)
                .ThenBy(l => l.GroupId)
                .ThenBy(l => l.ChoiceId)
                .ToList();
        }

        return _report.Locations
            .Where(l => string.Equals(l.TableName, table.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.EventId)
            .ThenBy(l => l.GroupId)
            .ThenBy(l => l.ChoiceId)
            .ToList();
    }

    private static string TableRowKey(EventInfoTable table, DataRowView row)
    {
        var preferred = table.Name switch
        {
            "이벤트별 요약" => "event_id",
            "이벤트 난이도" => "event_id",
            "보상 정보" => "reward_id(type)",
            "비용 정보" => "cost_type",
            "전투 정보" => "stage_id",
            "흐름 정보" => "key",
            "밸런스 체크" => "check",
            _ => table.Columns.FirstOrDefault() ?? ""
        };
        if (!string.IsNullOrWhiteSpace(preferred) && row.Row.Table.Columns.Contains(preferred))
            return row[preferred]?.ToString() ?? "";
        var first = table.Columns.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(first) && row.Row.Table.Columns.Contains(first)
            ? row[first]?.ToString() ?? ""
            : "";
    }

    private static void AddDetailColumn(DataGrid grid, string header, string binding, double width)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(binding),
            Width = width
        });
    }

    private static DataTable ToDataTable(EventInfoTable source)
    {
        var table = new DataTable(source.Name);
        foreach (var (column, index) in source.Columns.Select((value, i) => (value, i)))
            table.Columns.Add(column, InferColumnType(source, index));

        foreach (var row in source.Rows)
        {
            var values = new object?[source.Columns.Count];
            for (var i = 0; i < source.Columns.Count; i++)
                values[i] = i < row.Count ? CoerceValue(row[i], table.Columns[i].DataType) : DBNull.Value;
            table.Rows.Add(values);
        }
        return table;
    }

    private static object CoerceValue(object? value, Type targetType)
    {
        if (value is null)
            return DBNull.Value;
        if (targetType == typeof(string))
            return value.ToString() ?? "";
        if (targetType == typeof(double))
            return Convert.ToDouble(value);
        if (targetType == typeof(long))
            return Convert.ToInt64(value);
        if (targetType == typeof(int))
            return Convert.ToInt32(value);
        return value;
    }

    private static Type InferColumnType(EventInfoTable table, int columnIndex)
    {
        var values = table.Rows
            .Where(r => columnIndex < r.Count && r[columnIndex] is not null)
            .Select(r => r[columnIndex])
            .ToList();
        if (values.Count == 0)
            return typeof(string);
        if (values.All(v => v is int))
            return typeof(int);
        if (values.All(v => v is int or long))
            return typeof(long);
        if (values.All(v => v is int or long or double or decimal))
            return typeof(double);
        return typeof(string);
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
