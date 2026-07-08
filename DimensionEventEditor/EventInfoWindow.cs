using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
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
        ApplyUnityResources();

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
                    Text = "현재 로드된 이벤트 테이블을 기준으로 보상, 비용, 난이도, 전투 정보를 집계합니다. 기존 Export Preview에서 확정하면 같은 내용이 한글 시트로 저장됩니다.",
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

    private void ApplyUnityResources()
    {
        var dictionary = (ResourceDictionary)XamlReader.Parse("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <SolidColorBrush x:Key="UnityPanelBrush" Color="#252525"/>
    <SolidColorBrush x:Key="UnityPanelDarkBrush" Color="#1f1f1f"/>
    <SolidColorBrush x:Key="UnityBorderBrush" Color="#111111"/>
    <SolidColorBrush x:Key="UnityLineBrush" Color="#3a3a3a"/>
    <SolidColorBrush x:Key="UnityTextBrush" Color="#d8d8d8"/>
    <SolidColorBrush x:Key="UnitySelectedBrush" Color="#3f6388"/>
    <SolidColorBrush x:Key="UnitySelectedBorderBrush" Color="#6aa4d8"/>

    <Style TargetType="{x:Type TabControl}">
        <Setter Property="Background" Value="{StaticResource UnityPanelDarkBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource UnityBorderBrush}"/>
        <Setter Property="Foreground" Value="{StaticResource UnityTextBrush}"/>
        <Setter Property="Padding" Value="8,8,8,8"/>
    </Style>
    <Style TargetType="{x:Type TabItem}">
        <Setter Property="Foreground" Value="#d8d8d8"/>
        <Setter Property="Background" Value="#2b2b2b"/>
        <Setter Property="BorderBrush" Value="#151515"/>
        <Setter Property="Padding" Value="12,5"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type TabItem}">
                    <Border x:Name="TabChrome"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="1,1,1,0"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter ContentSource="Header"
                                          RecognizesAccessKey="True"
                                          VerticalAlignment="Center"
                                          HorizontalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="TabChrome" Property="Background" Value="#3a3a3a"/>
                            <Setter Property="Foreground" Value="#ffffff"/>
                        </Trigger>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="TabChrome" Property="Background" Value="#454545"/>
                            <Setter Property="Foreground" Value="#ffffff"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="ScrollBarTrackButton" TargetType="{x:Type RepeatButton}">
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type RepeatButton}">
                    <Border Background="Transparent"/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style x:Key="UnityScrollThumb" TargetType="{x:Type Thumb}">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type Thumb}">
                    <Border x:Name="ThumbChrome"
                            Background="#5b5b5b"
                            BorderBrush="#6b6b6b"
                            BorderThickness="1"
                            CornerRadius="4"/>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="ThumbChrome" Property="Background" Value="#6f6f6f"/>
                            <Setter TargetName="ThumbChrome" Property="BorderBrush" Value="#808080"/>
                        </Trigger>
                        <Trigger Property="IsDragging" Value="True">
                            <Setter TargetName="ThumbChrome" Property="Background" Value="#7d7d7d"/>
                            <Setter TargetName="ThumbChrome" Property="BorderBrush" Value="#9a9a9a"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <ControlTemplate x:Key="VerticalUnityScrollBar" TargetType="{x:Type ScrollBar}">
        <Grid Width="18" Background="#2a2a2a">
            <Border Background="#303030" BorderBrush="#1a1a1a" BorderThickness="1"/>
            <Track x:Name="PART_Track"
                   Orientation="Vertical"
                   IsDirectionReversed="True"
                   Minimum="{TemplateBinding Minimum}"
                   Maximum="{TemplateBinding Maximum}"
                   Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"
                   ViewportSize="{TemplateBinding ViewportSize}"
                   Margin="3,2">
                <Track.DecreaseRepeatButton>
                    <RepeatButton Command="{x:Static ScrollBar.PageUpCommand}" Style="{StaticResource ScrollBarTrackButton}"/>
                </Track.DecreaseRepeatButton>
                <Track.Thumb>
                    <Thumb Style="{StaticResource UnityScrollThumb}" MinHeight="34"/>
                </Track.Thumb>
                <Track.IncreaseRepeatButton>
                    <RepeatButton Command="{x:Static ScrollBar.PageDownCommand}" Style="{StaticResource ScrollBarTrackButton}"/>
                </Track.IncreaseRepeatButton>
            </Track>
        </Grid>
    </ControlTemplate>
    <ControlTemplate x:Key="HorizontalUnityScrollBar" TargetType="{x:Type ScrollBar}">
        <Grid Height="18" Background="#2a2a2a">
            <Border Background="#303030" BorderBrush="#1a1a1a" BorderThickness="1"/>
            <Track x:Name="PART_Track"
                   Orientation="Horizontal"
                   Minimum="{TemplateBinding Minimum}"
                   Maximum="{TemplateBinding Maximum}"
                   Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"
                   ViewportSize="{TemplateBinding ViewportSize}"
                   Margin="2,3">
                <Track.DecreaseRepeatButton>
                    <RepeatButton Command="{x:Static ScrollBar.PageLeftCommand}" Style="{StaticResource ScrollBarTrackButton}"/>
                </Track.DecreaseRepeatButton>
                <Track.Thumb>
                    <Thumb Style="{StaticResource UnityScrollThumb}" MinWidth="34"/>
                </Track.Thumb>
                <Track.IncreaseRepeatButton>
                    <RepeatButton Command="{x:Static ScrollBar.PageRightCommand}" Style="{StaticResource ScrollBarTrackButton}"/>
                </Track.IncreaseRepeatButton>
            </Track>
        </Grid>
    </ControlTemplate>
    <Style TargetType="{x:Type ScrollBar}">
        <Setter Property="Background" Value="#2a2a2a"/>
        <Setter Property="Width" Value="18"/>
        <Setter Property="Height" Value="Auto"/>
        <Setter Property="Template" Value="{StaticResource VerticalUnityScrollBar}"/>
        <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
                <Setter Property="Width" Value="Auto"/>
                <Setter Property="Height" Value="18"/>
                <Setter Property="Template" Value="{StaticResource HorizontalUnityScrollBar}"/>
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style TargetType="{x:Type DataGridColumnHeader}">
        <Setter Property="Background" Value="#303030"/>
        <Setter Property="Foreground" Value="#e6e6e6"/>
        <Setter Property="BorderBrush" Value="#111111"/>
        <Setter Property="BorderThickness" Value="0,0,1,1"/>
        <Setter Property="Padding" Value="8,3"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type DataGridColumnHeader}">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter VerticalAlignment="Center"
                                          HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Background" Value="#3c3c3c"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType="{x:Type DataGridRow}">
        <Setter Property="Background" Value="#242424"/>
        <Setter Property="Foreground" Value="#d8d8d8"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
        <Style.Triggers>
            <Trigger Property="AlternationIndex" Value="1">
                <Setter Property="Background" Value="#2a2a2a"/>
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="#333333"/>
            </Trigger>
            <Trigger Property="IsSelected" Value="True">
                <Setter Property="Background" Value="{StaticResource UnitySelectedBrush}"/>
                <Setter Property="Foreground" Value="#ffffff"/>
            </Trigger>
        </Style.Triggers>
    </Style>
    <Style TargetType="{x:Type DataGridCell}">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="#d8d8d8"/>
        <Setter Property="BorderBrush" Value="#343434"/>
        <Setter Property="BorderThickness" Value="0,0,1,1"/>
        <Setter Property="Padding" Value="6,0"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type DataGridCell}">
                    <Border x:Name="CellChrome"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="CellChrome" Property="Background" Value="{StaticResource UnitySelectedBrush}"/>
                            <Setter TargetName="CellChrome" Property="BorderBrush" Value="{StaticResource UnitySelectedBorderBrush}"/>
                            <Setter Property="Foreground" Value="#ffffff"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType="{x:Type TextBox}">
        <Setter Property="Background" Value="#2b2b2b"/>
        <Setter Property="Foreground" Value="#d8d8d8"/>
        <Setter Property="BorderBrush" Value="#4a4a4a"/>
        <Setter Property="CaretBrush" Value="#ffffff"/>
        <Setter Property="SelectionBrush" Value="#3f6388"/>
        <Setter Property="Padding" Value="7,2"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type TextBox}">
                    <Border x:Name="TextBoxChrome"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="1">
                        <ScrollViewer x:Name="PART_ContentHost"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter TargetName="TextBoxChrome" Property="BorderBrush" Value="#6aa4d8"/>
                        </Trigger>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="TextBoxChrome" Property="BorderBrush" Value="#707070"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
""");
        Resources.MergedDictionaries.Add(dictionary);
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

        var sourceTable = ToDataTable(table);
        var filteredTable = sourceTable;
        var summarySearchBox = CreateSearchBox("집계 표 검색");

        var topPanel = new DockPanel
        {
            Margin = new Thickness(10, 8, 10, 7),
            LastChildFill = true
        };
        Grid.SetRow(topPanel, 0);
        grid.Children.Add(topPanel);

        var summarySearchPanel = CreateSearchPanel("검색", summarySearchBox);
        DockPanel.SetDock(summarySearchPanel, Dock.Right);
        topPanel.Children.Add(summarySearchPanel);

        var description = new TextBlock
        {
            Foreground = Brush("#b8b8b8"),
            Margin = new Thickness(0, 2, 12, 0),
            TextWrapping = TextWrapping.Wrap
        };
        topPanel.Children.Add(description);

        var dataGrid = new DataGrid
        {
            ItemsSource = filteredTable.DefaultView,
            IsReadOnly = true,
            AutoGenerateColumns = true,
            CanUserAddRows = false,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = Brush("#1f1f1f"),
            Foreground = Brush("#d8d8d8"),
            RowBackground = Brush("#242424"),
            AlternatingRowBackground = Brush("#2a2a2a"),
            AlternationCount = 2,
            BorderBrush = Brush("#111111"),
            HorizontalGridLinesBrush = Brush("#3a3a3a"),
            VerticalGridLinesBrush = Brush("#303030"),
            ColumnHeaderHeight = 28,
            RowHeight = 24,
            Margin = new Thickness(8, 0, 8, 8)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(dataGrid, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(dataGrid, ScrollBarVisibility.Auto);
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

        var detailSearchBox = CreateSearchBox("세부 위치 검색");
        var detailSearchPanel = CreateSearchPanel("검색", detailSearchBox);
        detailSearchPanel.Margin = new Thickness(4, 0, 4, 5);
        DockPanel.SetDock(detailSearchPanel, Dock.Top);
        detailsDock.Children.Add(detailSearchPanel);

        var detailsGrid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = Brush("#202020"),
            Foreground = Brush("#d8d8d8"),
            RowBackground = Brush("#242424"),
            AlternatingRowBackground = Brush("#292929"),
            AlternationCount = 2,
            BorderBrush = Brush("#111111"),
            HorizontalGridLinesBrush = Brush("#3a3a3a"),
            VerticalGridLinesBrush = Brush("#303030"),
            ColumnHeaderHeight = 26,
            RowHeight = 24
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(detailsGrid, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(detailsGrid, ScrollBarVisibility.Auto);
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

        void RefreshTopFilter()
        {
            filteredTable = FilterDataTable(sourceTable, summarySearchBox.Text);
            dataGrid.ItemsSource = filteredTable.DefaultView;
            description.Text = SummaryDescription(table, filteredTable.Rows.Count);
            UpdateDetails(table, dataGrid, detailsGrid, detailsTitle, detailSearchBox);
        }

        summarySearchBox.TextChanged += (_, _) => RefreshTopFilter();
        detailSearchBox.TextChanged += (_, _) => UpdateDetails(table, dataGrid, detailsGrid, detailsTitle, detailSearchBox);
        dataGrid.SelectionChanged += (_, _) => UpdateDetails(table, dataGrid, detailsGrid, detailsTitle, detailSearchBox);
        dataGrid.MouseDoubleClick += (_, _) =>
        {
            if (detailsGrid.Items.Count == 1 && detailsGrid.Items[0] is EventInfoLocation location)
                _navigate?.Invoke(location);
        };
        description.Text = SummaryDescription(table, filteredTable.Rows.Count);
        return grid;
    }

    private void UpdateDetails(EventInfoTable table, DataGrid sourceGrid, DataGrid detailGrid, TextBlock detailTitle, TextBox detailSearchBox)
    {
        if (sourceGrid.SelectedItem is not DataRowView row)
        {
            detailGrid.ItemsSource = null;
            detailTitle.Text = "세부 위치: 집계 행을 선택하면 표시됩니다.";
            return;
        }

        var key = TableRowKey(table, row);
        var allDetails = FindLocations(table, key);
        var details = allDetails.Where(location => MatchesSearch(location, detailSearchBox.Text)).ToList();
        detailGrid.ItemsSource = details;
        detailTitle.Text = details.Count == 0
            ? $"세부 위치: {key} / 연결된 노드 위치가 없습니다."
            : DetailsDescription(key, details.Count, allDetails.Count);
    }

    private static string SummaryDescription(EventInfoTable table, int visibleCount)
    {
        return visibleCount == table.Rows.Count
            ? $"{table.Description}  /  rows: {table.Rows.Count}"
            : $"{table.Description}  /  rows: {visibleCount} / {table.Rows.Count}";
    }

    private static DataTable FilterDataTable(DataTable source, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return source;

        var filtered = source.Clone();
        foreach (DataRow row in source.Rows)
        {
            if (MatchesSearch(row, query))
                filtered.ImportRow(row);
        }
        return filtered;
    }

    private static string DetailsDescription(string key, int visibleCount, int totalCount)
    {
        var countText = visibleCount == totalCount
            ? $"{visibleCount}건"
            : $"{visibleCount} / {totalCount}건";
        return $"세부 위치: {key} / {countText}  (더블클릭하면 Scene에서 해당 노드를 보여줍니다.)";
    }

    private static TextBox CreateSearchBox(string toolTip)
    {
        return new TextBox
        {
            Width = 260,
            Height = 24,
            ToolTip = toolTip,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private static DockPanel CreateSearchPanel(string label, TextBox searchBox)
    {
        var panel = new DockPanel
        {
            Width = 318,
            LastChildFill = true
        };
        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = Brush("#cfcfcf"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(labelBlock, Dock.Left);
        panel.Children.Add(labelBlock);
        panel.Children.Add(searchBox);
        return panel;
    }

    private static bool MatchesSearch(object? item, string? query)
    {
        var tokens = (query ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return true;

        var text = SearchText(item);
        return tokens.All(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string SearchText(object? item)
    {
        return item switch
        {
            DataRow row => string.Join(" ", row.ItemArray.Select(value => value?.ToString() ?? "")),
            DataRowView row => string.Join(" ", row.Row.ItemArray.Select(value => value?.ToString() ?? "")),
            EventInfoLocation location => string.Join(" ", new[]
            {
                location.TableName,
                location.Key,
                location.EventId,
                location.EventName,
                location.GroupId,
                location.ChoiceId,
                location.Branch,
                location.NodeKey,
                location.Kind,
                location.Label,
                location.Detail
            }),
            _ => item?.ToString() ?? ""
        };
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
