using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace NexusEditor;

public sealed class ResearchPreferencesWindow : Window
{
    private readonly TextBox _outSystemPathBox;
    private readonly TextBox _effectPathBox;
    private readonly TextBlock _validationText;

    public ResearchWorkbookPaths? SelectedPaths { get; private set; }

    public ResearchPreferencesWindow(ResearchWorkbookPaths? current)
    {
        Title = "Research Preferences";
        Width = 820;
        Height = 420;
        MinWidth = 680;
        MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
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
            BorderBrush = Brush(75, 75, 75),
            ItemContainerStyle = CreateTabItemStyle()
        };
        var databaseTab = new TabItem { Header = "Database Path" };
        tabs.Items.Add(databaseTab);
        root.Children.Add(tabs);

        var panel = new StackPanel { Margin = new Thickness(18) };
        databaseTab.Content = panel;
        panel.Children.Add(new TextBlock
        {
            Text = "연구 노드 편집에 사용할 두 DB 원본을 지정합니다. 두 파일은 같은 DB 폴더에 있어도 되고 서로 다른 위치에 있어도 됩니다.",
            Foreground = Brush(185, 185, 185),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        });

        _outSystemPathBox = AddPathRow(
            panel,
            "Out System Table",
            current?.OutSystemPath ?? "",
            "nexus_node_category와 nexus_node 시트를 포함한 nexus_out_system 엑셀 파일",
            box => BrowseWorkbook(box, "nexus_out_system 연구 노드 DB 선택"));

        _effectPathBox = AddPathRow(
            panel,
            "Effect Table",
            current?.EffectPath ?? "",
            "nexus_effect 시트를 포함한 nexus_effect 엑셀 파일",
            box => BrowseWorkbook(box, "nexus_effect DB 선택"));

        _validationText = new TextBlock
        {
            Foreground = Brush(230, 110, 110),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        panel.Children.Add(_validationText);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        var cancel = CreateButton("취소", 96);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);

        var save = CreateButton("저장", 96);
        save.Margin = new Thickness(0, 0, 8, 0);
        save.Click += (_, _) => SaveAndClose();
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(save);

        Content = root;
    }

    private void SaveAndClose()
    {
        var outPath = NormalizePath(_outSystemPathBox.Text);
        var effectPath = NormalizePath(_effectPathBox.Text);
        if (!File.Exists(outPath))
        {
            _validationText.Text = "Out System Table 경로가 올바르지 않습니다.";
            return;
        }
        if (!File.Exists(effectPath))
        {
            _validationText.Text = "Effect Table 경로가 올바르지 않습니다.";
            return;
        }

        SelectedPaths = new ResearchWorkbookPaths(outPath, effectPath);
        DialogResult = true;
        Close();
    }

    private void BrowseWorkbook(TextBox target, string title)
    {
        var current = NormalizePath(target.Text);
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Excel Workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            InitialDirectory = File.Exists(current)
                ? Path.GetDirectoryName(current)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FileName;
    }

    private static TextBox AddPathRow(
        Panel parent,
        string label,
        string value,
        string description,
        Action<TextBox> browseHandler)
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
            Height = 32,
            Padding = new Thickness(8, 5, 8, 5),
            Background = Brush(30, 30, 30),
            Foreground = Brush(230, 230, 230),
            BorderBrush = Brush(82, 82, 82)
        };
        row.Children.Add(box);

        var browse = CreateButton("찾아보기", 94);
        browse.Margin = new Thickness(7, 0, 0, 0);
        browse.Click += (_, _) => browseHandler(box);
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

    private static Style CreateTabItemStyle()
    {
        var style = new Style(typeof(TabItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(47, 47, 47)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(188, 188, 188)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(70, 70, 70)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 7, 14, 7)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));

        var template = new ControlTemplate(typeof(TabItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Chrome";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 0));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(58, 58, 58)));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, Brush(230, 230, 230)));
        template.Triggers.Add(hover);
        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush(67, 67, 67)));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(96, 96, 96)));
        template.Triggers.Add(selected);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Button CreateButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 32,
        Background = Brush(58, 58, 58),
        Foreground = Brushes.White,
        BorderBrush = Brush(86, 86, 86)
    };

    private static string NormalizePath(string? value)
    {
        var text = (value ?? "").Trim().Trim('"');
        try { return Path.GetFullPath(text); }
        catch { return text; }
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
