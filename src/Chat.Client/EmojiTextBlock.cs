using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Chat.Client;

/// <summary>TextBlock that replaces the LAB2 catalog emoji with local color PNGs.</summary>
public sealed class EmojiTextBlock : TextBlock
{
    public static readonly DependencyProperty EmojiTextProperty =
        DependencyProperty.Register(
            nameof(EmojiText),
            typeof(string),
            typeof(EmojiTextBlock),
            new FrameworkPropertyMetadata(string.Empty, OnEmojiTextChanged));

    public string? EmojiText
    {
        get => (string?)GetValue(EmojiTextProperty);
        set => SetValue(EmojiTextProperty, value);
    }

    public EmojiTextBlock()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnEmojiTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((EmojiTextBlock)d).RebuildInlines();
    }

    private void OnAssetsReady(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RebuildInlines);
            return;
        }
        RebuildInlines();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EmojiCatalog.AssetsReady += OnAssetsReady;
        RebuildInlines();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EmojiCatalog.AssetsReady -= OnAssetsReady;
    }

    private void RebuildInlines()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RebuildInlines);
            return;
        }

        Inlines.Clear();
        foreach (var segment in EmojiCatalog.Split(EmojiText))
        {
            if (segment.Glyph is null || !EmojiCatalog.TryGetImage(segment.Text, out var image) || image is null)
            {
                Inlines.Add(new Run(segment.Text));
                continue;
            }

            var side = Math.Clamp(FontSize * 1.22, 15, 32);
            var imageControl = new Image
            {
                Source = image,
                Width = side,
                Height = side,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                ToolTip = segment.Text,
            };
            AutomationProperties.SetName(imageControl, segment.Text);
            Inlines.Add(new InlineUIContainer(imageControl)
            {
                BaselineAlignment = BaselineAlignment.TextBottom
            });
        }
    }
}
