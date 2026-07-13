using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DimensionEventEditor;

public sealed class BackgroundPreviewWindow : Window
{
    private readonly string _resourceKey;
    private readonly string _imagePath;

    public BackgroundPreviewWindow(string resourceKey, string imagePath)
    {
        _resourceKey = resourceKey;
        _imagePath = imagePath;

        Title = $"Background Preview - {_resourceKey}";
        Width = 1180;
        Height = 760;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush(18, 18, 18);
        Foreground = Brush(230, 230, 230);

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = _resourceKey,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(245, 245, 245),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        header.Children.Add(new TextBlock
        {
            Text = _imagePath,
            FontSize = 12,
            Foreground = Brush(150, 150, 150),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var content = new Grid();
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var failed = new TextBlock
        {
            Text = "Preview load failed",
            Foreground = Brush(170, 170, 170),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        image.Source = LoadBitmap(_imagePath, 1800);
        failed.Visibility = image.Source is null ? Visibility.Visible : Visibility.Collapsed;
        content.Children.Add(image);
        content.Children.Add(failed);

        var frame = new Border
        {
            Background = Brushes.Black,
            BorderBrush = Brush(70, 70, 70),
            BorderThickness = new Thickness(1),
            Child = content
        };
        Grid.SetRow(frame, 1);
        root.Children.Add(frame);

        var close = new Button
        {
            Content = "닫기",
            Width = 120,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Background = Brush(58, 58, 58),
            Foreground = Brush(230, 230, 230),
            BorderBrush = Brush(86, 86, 86)
        };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 2);
        root.Children.Add(close);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
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

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
