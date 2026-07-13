using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace DimensionEventEditor;

public sealed class PreferencesWindow : Window
{
    private readonly TextBox _dbPathBox;
    private readonly TextBox _clientPathBox;
    private readonly TextBlock _validationText;

    public string DbTablePath { get; private set; } = "";
    public string ClientPath { get; private set; } = "";

    public PreferencesWindow(string? dbTablePath, string? clientPath)
    {
        Title = "Preferences";
        Width = 760;
        Height = 390;
        MinWidth = 640;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = Brush(37, 37, 37);
        Foreground = Brush(224, 224, 224);
        FontFamily = new FontFamily("Segoe UI");

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tabs = new TabControl
        {
            Background = Brush(43, 43, 43),
            Foreground = Brush(225, 225, 225),
            BorderBrush = Brush(75, 75, 75)
        };
        var assetTab = new TabItem
        {
            Header = "Asset Path",
            Background = Brush(52, 52, 52),
            Foreground = Brush(230, 230, 230)
        };
        tabs.Items.Add(assetTab);
        root.Children.Add(tabs);

        var assetPanel = new StackPanel { Margin = new Thickness(16, 18, 16, 12) };
        assetTab.Content = assetPanel;
        assetPanel.Children.Add(new TextBlock
        {
            Text = "에디터가 사용할 DB 테이블과 Epic Seven DEV 클라이언트 위치를 지정합니다.",
            Foreground = Brush(185, 185, 185),
            Margin = new Thickness(0, 0, 0, 18)
        });

        _dbPathBox = AddPathRow(
            assetPanel,
            "DB Table Path",
            dbTablePath ?? "",
            "nexus_event 차원 탐사 이벤트.xlsx 파일 또는 파일이 들어 있는 DB 폴더",
            BrowseDbPath);
        _clientPathBox = AddPathRow(
            assetPanel,
            "Client Path",
            clientPath ?? "",
            "Epic Seven dev 폴더 또는 그 상위 repos 폴더. 배경 이미지는 game\\Resources\\res\\nexus에서 읽습니다.",
            BrowseClientPath);

        _validationText = new TextBlock
        {
            Foreground = Brush(225, 110, 110),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        assetPanel.Children.Add(_validationText);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        var cancel = CreateButton("취소", 92);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);

        var save = CreateButton("저장", 92);
        save.Margin = new Thickness(0, 0, 8, 0);
        save.Click += (_, _) => SaveAndClose();
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(save);

        Content = root;
    }

    private static TextBox AddPathRow(
        Panel parent,
        string label,
        string value,
        string description,
        RoutedEventHandler browseHandler)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });

        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox
        {
            Text = value,
            Height = 30,
            Padding = new Thickness(7, 4, 7, 4),
            Background = Brush(30, 30, 30),
            Foreground = Brush(230, 230, 230),
            BorderBrush = Brush(82, 82, 82)
        };
        row.Children.Add(box);
        var browse = CreateButton("찾아보기", 88);
        browse.Margin = new Thickness(7, 0, 0, 0);
        browse.Click += browseHandler;
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        parent.Children.Add(row);

        parent.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = Brush(155, 155, 155),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        return box;
    }

    private void BrowseDbPath(object sender, RoutedEventArgs e)
    {
        var current = _dbPathBox.Text.Trim().Trim('"');
        var initialDirectory = File.Exists(current)
            ? Path.GetDirectoryName(current)
            : Directory.Exists(current) ? current : null;
        var dialog = new OpenFileDialog
        {
            Title = "nexus_event 차원 탐사 이벤트.xlsx 선택",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            InitialDirectory = initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true)
            _dbPathBox.Text = dialog.FileName;
    }

    private void BrowseClientPath(object sender, RoutedEventArgs e)
    {
        var current = _clientPathBox.Text.Trim().Trim('"');
        var dialog = new OpenFolderDialog
        {
            Title = "Epic Seven dev 폴더 또는 repos 폴더를 선택하세요",
            InitialDirectory = Directory.Exists(current)
                ? current
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            _clientPathBox.Text = dialog.FolderName;
    }

    private void SaveAndClose()
    {
        var dbPath = NexusPathResolver.ResolveEventWorkbookSelection(_dbPathBox.Text);
        if (dbPath is null)
        {
            _validationText.Text = "DB Table Path에서 올바른 nexus_event 테이블을 찾지 못했습니다.";
            return;
        }

        var clientPath = NexusPathResolver.FindDevRoot(_clientPathBox.Text);
        if (clientPath is null)
        {
            _validationText.Text = "Client Path에서 game\\Resources\\res\\nexus 폴더를 찾지 못했습니다.";
            return;
        }

        DbTablePath = dbPath;
        ClientPath = clientPath;
        DialogResult = true;
        Close();
    }

    private static Button CreateButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 32,
        Background = Brush(58, 58, 58),
        Foreground = Brushes.White,
        BorderBrush = Brush(92, 92, 92),
        Padding = new Thickness(8, 3, 8, 3)
    };

    private static SolidColorBrush Brush(byte red, byte green, byte blue)
        => new(Color.FromRgb(red, green, blue));
}
