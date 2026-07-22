using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NexusEditor;

public enum RuntimeLaunchTarget
{
    Godot,
    EpicSevenDev
}

public sealed class RuntimeTargetPickerWindow : Window
{
    private readonly ListBox _devList;
    private readonly Button _devRunButton;

    public RuntimeLaunchTarget SelectedTarget { get; private set; }
    public EpicSevenDevInstance? SelectedDevInstance => _devList.SelectedItem as EpicSevenDevInstance;

    public RuntimeTargetPickerWindow(IReadOnlyList<EpicSevenDevInstance> devInstances)
    {
        Title = "실행 대상 선택";
        Width = 680;
        Height = 460;
        MinWidth = 560;
        MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(37, 37, 37));
        Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 224));
        FontFamily = new FontFamily("Segoe UI");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "현재 이벤트를 어디에서 실행할까요?",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(title);

        var godotPanel = CreateSectionBorder();
        Grid.SetRow(godotPanel, 1);
        var godotGrid = new Grid();
        godotGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        godotGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        godotGrid.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Godot Event Player", FontSize = 17, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = "현재 편집 내용을 임시 엑셀로 만들어 별도 플레이어에서 실행합니다.", Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), Margin = new Thickness(0, 5, 12, 0) }
            }
        });
        var godotButton = CreateButton("Godot에서 실행", 142);
        godotButton.Click += (_, _) => Complete(RuntimeLaunchTarget.Godot);
        Grid.SetColumn(godotButton, 1);
        godotGrid.Children.Add(godotButton);
        godotPanel.Child = godotGrid;
        root.Children.Add(godotPanel);

        var devPanel = CreateSectionBorder();
        devPanel.Margin = new Thickness(0, 12, 0, 12);
        Grid.SetRow(devPanel, 2);
        var devGrid = new Grid();
        devGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        devGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        devGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        devGrid.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = $"Epic Seven DEV ({devInstances.Count}개)", FontSize = 17, FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = "실행 중인 DEV의 console에 현재 이벤트 치트를 보냅니다. STOVE 라이브는 대상에서 제외됩니다.",
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    Margin = new Thickness(0, 5, 0, 8),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        });

        _devList = new ListBox
        {
            ItemsSource = devInstances,
            DisplayMemberPath = nameof(EpicSevenDevInstance.DisplayLabel),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 225)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(74, 74, 74)),
            Margin = new Thickness(0, 0, 0, 10)
        };
        _devList.MouseDoubleClick += (_, _) =>
        {
            if (SelectedDevInstance?.HasConsole == true)
                Complete(RuntimeLaunchTarget.EpicSevenDev);
        };
        _devList.SelectionChanged += (_, _) => UpdateDevButton();
        Grid.SetRow(_devList, 1);
        devGrid.Children.Add(_devList);

        var devFooter = new Grid();
        devFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        devFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var noDevMessage = new TextBlock
        {
            Text = devInstances.Count == 0
                ? "실행 중인 DEV 클라이언트를 찾지 못했습니다. Preference > Asset Path의 Client Path와 DEV console을 확인하세요."
                : "console 입력창이 있는 DEV를 선택하세요.",
            Foreground = new SolidColorBrush(Color.FromRgb(205, 160, 90)),
            VerticalAlignment = VerticalAlignment.Center
        };
        devFooter.Children.Add(noDevMessage);
        _devRunButton = CreateButton("선택한 클라이언트에서 실행", 210);
        _devRunButton.Click += (_, _) => Complete(RuntimeLaunchTarget.EpicSevenDev);
        Grid.SetColumn(_devRunButton, 1);
        devFooter.Children.Add(_devRunButton);
        Grid.SetRow(devFooter, 2);
        devGrid.Children.Add(devFooter);
        devPanel.Child = devGrid;
        root.Children.Add(devPanel);

        var cancelButton = CreateButton("취소", 92);
        cancelButton.HorizontalAlignment = HorizontalAlignment.Right;
        cancelButton.Click += (_, _) => { DialogResult = false; Close(); };
        Grid.SetRow(cancelButton, 3);
        root.Children.Add(cancelButton);

        Content = root;
        if (devInstances.Count > 0)
            _devList.SelectedIndex = 0;
        UpdateDevButton();
    }

    private static Border CreateSectionBorder() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(43, 43, 43)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(76, 76, 76)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(14)
    };

    private static Button CreateButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 36,
        Margin = new Thickness(6, 0, 0, 0),
        Background = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(92, 92, 92)),
        Padding = new Thickness(10, 4, 10, 4)
    };

    private void UpdateDevButton()
    {
        _devRunButton.IsEnabled = SelectedDevInstance?.HasConsole == true;
    }

    private void Complete(RuntimeLaunchTarget target)
    {
        if (target == RuntimeLaunchTarget.EpicSevenDev && SelectedDevInstance?.HasConsole != true)
            return;
        SelectedTarget = target;
        DialogResult = true;
        Close();
    }
}
