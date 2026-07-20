using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NexusEditor;

public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        => Show(null, messageBoxText, caption, button, icon);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        var result = MessageBoxResult.None;
        var dialog = new Window
        {
            Title = caption,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 360,
            MaxWidth = 680,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            AllowsTransparency = true
        };

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5)
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(caption) ? "알림" : caption,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(28, 22, 28, 24),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(title, 0);
        layout.Children.Add(title);

        var separatorTop = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(separatorTop, 0);
        layout.Children.Add(separatorTop);

        var body = new TextBlock
        {
            Text = messageBoxText,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(198, 198, 198)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            Margin = new Thickness(48, 34, 48, 44),
            MaxWidth = 540
        };
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var footer = new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 18))
        };
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        var separatorBottom = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(separatorBottom, 2);
        layout.Children.Add(separatorBottom);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 20, 18, 20)
        };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);

        foreach (var spec in ButtonSpecs(button))
        {
            var dialogButton = CreateButton(spec.Label, spec.IsPrimary);
            dialogButton.Click += (_, _) =>
            {
                result = spec.Result;
                dialog.DialogResult = true;
                dialog.Close();
            };
            buttons.Children.Add(dialogButton);
        }

        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                result = CancelResult(button);
                dialog.DialogResult = true;
                dialog.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                result = PrimaryResult(button);
                dialog.DialogResult = true;
                dialog.Close();
                e.Handled = true;
            }
        };
        dialog.Closed += (_, _) =>
        {
            if (result == MessageBoxResult.None)
                result = CancelResult(button);
        };

        root.Child = layout;
        dialog.Content = root;
        dialog.ShowDialog();
        return result;
    }

    private static Button CreateButton(string text, bool primary)
    {
        var background = primary ? Color.FromRgb(77, 123, 196) : Color.FromRgb(72, 72, 72);
        var hover = primary ? Color.FromRgb(90, 140, 216) : Color.FromRgb(86, 86, 86);
        var pressed = primary ? Color.FromRgb(58, 103, 174) : Color.FromRgb(58, 58, 58);

        var button = new Button
        {
            Content = text,
            Width = 112,
            Height = 48,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(background),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Chrome";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hover), "Chrome"));
        template.Triggers.Add(hoverTrigger);

        var pressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(pressed), "Chrome"));
        template.Triggers.Add(pressedTrigger);

        button.Template = template;
        return button;
    }

    private static IEnumerable<(string Label, MessageBoxResult Result, bool IsPrimary)> ButtonSpecs(MessageBoxButton button)
        => button switch
        {
            MessageBoxButton.OKCancel =>
            [
                ("취소", MessageBoxResult.Cancel, false),
                ("확인", MessageBoxResult.OK, true)
            ],
            MessageBoxButton.YesNo =>
            [
                ("아니오", MessageBoxResult.No, false),
                ("예", MessageBoxResult.Yes, true)
            ],
            MessageBoxButton.YesNoCancel =>
            [
                ("취소", MessageBoxResult.Cancel, false),
                ("아니오", MessageBoxResult.No, false),
                ("예", MessageBoxResult.Yes, true)
            ],
            _ =>
            [
                ("확인", MessageBoxResult.OK, true)
            ]
        };

    private static MessageBoxResult PrimaryResult(MessageBoxButton button)
        => button switch
        {
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
            _ => MessageBoxResult.OK
        };

    private static MessageBoxResult CancelResult(MessageBoxButton button)
        => button switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
}
