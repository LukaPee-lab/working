using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DimensionEventEditor;

public sealed class SplashWindow : Window
{
    private readonly TextBlock _status;

    public SplashWindow(string status)
    {
        Width = 760;
        Height = 560;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Black;
        ShowInTaskbar = false;
        Topmost = true;

        var root = new Grid { Background = Brushes.Black };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(400) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var imageFrame = new Grid { Background = Brushes.Black };
        var image = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/SplashImage.png")),
            Stretch = Stretch.UniformToFill,
            ClipToBounds = true
        };
        imageFrame.Children.Add(image);

        var progress = new ProgressBar
        {
            Height = 7,
            Minimum = 0,
            Maximum = 100,
            Value = 34,
            IsIndeterminate = true,
            Background = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
            Foreground = new SolidColorBrush(Color.FromRgb(34, 176, 86)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        imageFrame.Children.Add(progress);
        Grid.SetRow(imageFrame, 0);
        root.Children.Add(imageFrame);

        var footer = new Grid { Margin = new Thickness(22, 18, 22, 16) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new TextBlock
        {
            Text = "Dimension Event Editor",
            Foreground = Brushes.White,
            FontSize = 34,
            FontWeight = FontWeights.Bold
        });
        brand.Children.Add(new TextBlock
        {
            Text = "Super Creative",
            Foreground = new SolidColorBrush(Color.FromRgb(118, 220, 190)),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(2, 2, 0, 0)
        });
        Grid.SetColumn(brand, 0);
        footer.Children.Add(brand);

        var statusStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _status = new TextBlock
        {
            Text = status,
            Foreground = new SolidColorBrush(Color.FromRgb(172, 172, 172)),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        statusStack.Children.Add(_status);
        statusStack.Children.Add(new TextBlock
        {
            Text = "2026.7",
            Foreground = Brushes.White,
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        });
        Grid.SetColumn(statusStack, 1);
        footer.Children.Add(statusStack);

        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        var closeHint = new TextBlock
        {
            Text = "×",
            Foreground = Brushes.White,
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 14, 0),
            Opacity = 0.85
        };
        imageFrame.Children.Add(closeHint);
        Content = root;
    }

    public void SetStatus(string status) => _status.Text = status;
}
