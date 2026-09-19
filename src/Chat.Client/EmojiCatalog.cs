using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Chat.Client;

/// <summary>
/// The small local emoji catalog used by the LAB2 picker and inline message
/// renderer. Unicode strings are still the protocol representation; these
/// images are only a local presentation detail.
/// </summary>
public static class EmojiCatalog
{
    private static readonly EmojiGlyph[] GlyphArray =
    [
        new("😀", "1f600"), new("😃", "1f603"), new("😄", "1f604"),
        new("😁", "1f601"), new("😆", "1f606"), new("😅", "1f605"),
        new("😂", "1f602"), new("🙂", "1f642"), new("🙃", "1f643"),
        new("😉", "1f609"), new("😊", "1f60a"), new("😍", "1f60d"),
        new("🥰", "1f970"), new("😘", "1f618"), new("😎", "1f60e"),
        new("🤗", "1f917"), new("🤔", "1f914"), new("😴", "1f634"),
        new("😢", "1f622"), new("😭", "1f62d"), new("😡", "1f621"),
        new("😱", "1f631"), new("🙌", "1f64c"), new("👍", "1f44d"),
        new("👎", "1f44e"), new("👋", "1f44b"), new("👏", "1f44f"),
        new("🙏", "1f64f"), new("💬", "1f4ac"), new("❤️", "2764"),
        new("💙", "1f499"), new("💚", "1f49a"), new("💛", "1f49b"),
        new("💜", "1f49c"), new("🧡", "1f9e1"), new("🎉", "1f389"),
        new("🔥", "1f525"), new("✨", "2728"), new("✅", "2705"),
        new("🚀", "1f680"), new("☕", "2615"), new("🌟", "1f31f"),
        new("🎯", "1f3af"), new("💡", "1f4a1"), new("🤝", "1f91d"),
        new("🌈", "1f308"), new("🍀", "1f340"), new("🎵", "1f3b5"),
        new("💪", "1f4aa"), new("🥳", "1f973"), new("😇", "1f607"),
        new("😋", "1f60b"), new("🤩", "1f929"), new("🤣", "1f923"),
        new("😏", "1f60f"), new("🙄", "1f644")
    ];

    private static readonly IReadOnlyList<EmojiGlyph> Glyphs = GlyphArray;
    private static readonly ConcurrentDictionary<string, ImageSource> Images = new(StringComparer.Ordinal);
    private static readonly object PreloadLock = new();
    private static readonly Assembly AssetAssembly = typeof(EmojiCatalog).Assembly;
    private static readonly IReadOnlyDictionary<string, string> ResourceNames =
        AssetAssembly.GetManifestResourceNames()
            .Where(static name => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                static name => name[..^4].Split('.').Last(),
                static name => name,
                StringComparer.OrdinalIgnoreCase);
    private static Task? _preloadTask;

    public static event EventHandler? AssetsReady;

    public static IReadOnlyList<EmojiGlyph> All => Glyphs;

    /// <summary>
    /// Loads the independent PNG streams in parallel away from the dispatcher.
    /// This is deliberately a bounded CPU/cache warm-up so the first rendered
    /// chat messages do not perform serial image decoding on the UI thread.
    /// </summary>
    public static Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        lock (PreloadLock)
        {
            _preloadTask ??= Task.Run(() =>
            {
                Parallel.ForEach(
                    Glyphs,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
                    },
                    glyph => Images.TryAdd(glyph.Character, LoadImage(glyph)));

                AssetsReady?.Invoke(null, EventArgs.Empty);
            }, cancellationToken);
            return _preloadTask;
        }
    }

    public static bool TryGetImage(string emoji, out ImageSource? image) =>
        Images.TryGetValue(emoji, out image);

    public static ImageSource LoadImage(EmojiGlyph glyph)
    {
        var resourceName = ResourceNames.FirstOrDefault(pair =>
            string.Equals(pair.Key, glyph.AssetName, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(resourceName))
            throw new FileNotFoundException($"Không tìm thấy emoji asset {glyph.AssetName}.png.");

        using var stream = AssetAssembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Không thể mở emoji asset {glyph.AssetName}.png.");

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.DecodePixelWidth = 72;
        bitmap.DecodePixelHeight = 72;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static IReadOnlyList<EmojiSegment> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var segments = new List<EmojiSegment>();
        var plainStart = 0;
        var index = 0;
        while (index < text.Length)
        {
            EmojiGlyph? match = null;
            foreach (var glyph in Glyphs)
            {
                if (glyph.Character.Length <= text.Length - index
                    && string.CompareOrdinal(text, index, glyph.Character, 0, glyph.Character.Length) == 0)
                {
                    match = glyph;
                    break;
                }
            }

            if (match is null)
            {
                index++;
                continue;
            }

            if (index > plainStart)
                segments.Add(new EmojiSegment(text[plainStart..index], null));
            segments.Add(new EmojiSegment(match.Character, match));
            index += match.Character.Length;
            plainStart = index;
        }

        if (plainStart < text.Length)
            segments.Add(new EmojiSegment(text[plainStart..], null));
        return segments;
    }

    public sealed record EmojiGlyph(string Character, string AssetName);

    public sealed record EmojiSegment(string Text, EmojiGlyph? Glyph);
}
