using MoveCopyScrap.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MoveCopyScrap.Services;

/// <summary>
/// Loads the large bitmaps shown by the carousel, decoded straight to the size they are
/// displayed at (WIC does the downscale during decode, which is dramatically cheaper than
/// decoding a 50 MP file and letting the GPU scale it).
///
/// Must be called from the UI thread: <see cref="BitmapImage"/> is a DependencyObject.
/// All of the expensive work happens inside WinRT async calls, i.e. off the UI thread.
/// </summary>
public sealed class ImageLoader
{
    // The carousel realises seven slots and decodes for all of them, so the cache has to
    // hold at least that many or navigating one step would evict what is still on screen.
    private const int CacheCapacity = 12;

    // Decode sizes are rounded up to a multiple of this, so a small window resize does not
    // trigger a re-decode. Kept modest on purpose: the step is also the *smallest* decode,
    // and a tall, narrow picture asked for 512px wide comes back thousands of pixels tall.
    private const int BucketStep = 256;

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();

    private sealed class CacheEntry
    {
        public BitmapImage Bitmap = null!;
        public int DecodedWidth;
        public LinkedListNode<string> Node = null!;
    }

    public void Clear()
    {
        _cache.Clear();
        _order.Clear();
    }

    /// <summary>
    /// Returns a bitmap for <paramref name="item"/> decoded to at least
    /// <paramref name="targetPixelWidth"/> physical pixels wide, rounded up to a
    /// <see cref="BucketStep"/> multiple. Never decodes larger than the source.
    /// </summary>
    public async Task<BitmapImage?> LoadAsync(MediaItem item, int targetPixelWidth)
    {
        int bucket = Math.Clamp(
            ((targetPixelWidth + BucketStep - 1) / BucketStep) * BucketStep, BucketStep, 8192);

        if (_cache.TryGetValue(item.Path, out var cached) && cached.DecodedWidth >= bucket)
        {
            Touch(cached);
            return cached.Bitmap;
        }

        var payload = await MediaImageSource.OpenAsync(item.Path, (uint)bucket).ConfigureAwait(true);
        if (payload is null) return null;

        var data = payload.Value;
        try
        {
            var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Physical };

            uint natural = data.OriginalWidth;
            int decodeWidth = data.DecodePixelWidth > 0
                ? (int)data.DecodePixelWidth
                : (natural > 0 ? (int)Math.Min(natural, (uint)bucket) : bucket);
            bitmap.DecodePixelWidth = Math.Max(1, decodeWidth);

            await bitmap.SetSourceAsync(data.Stream);

            if (data.OriginalWidth > 0 && data.OriginalHeight > 0)
                item.AspectRatio = data.OriginalWidth / (double)data.OriginalHeight;
            else if (bitmap.PixelHeight > 0)
                item.AspectRatio = bitmap.PixelWidth / (double)bitmap.PixelHeight;

            Insert(item.Path, bitmap, bucket);
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageLoader] {item.FileName}: {ex.Message}");
            return null;
        }
        finally
        {
            data.Stream.Dispose();
        }
    }

    private void Touch(CacheEntry entry)
    {
        _order.Remove(entry.Node);
        _order.AddFirst(entry.Node);
    }

    private void Insert(string path, BitmapImage bitmap, int decodedWidth)
    {
        if (_cache.TryGetValue(path, out var existing))
        {
            existing.Bitmap = bitmap;
            existing.DecodedWidth = decodedWidth;
            Touch(existing);
            return;
        }

        var node = _order.AddFirst(path);
        _cache[path] = new CacheEntry { Bitmap = bitmap, DecodedWidth = decodedWidth, Node = node };

        while (_order.Count > CacheCapacity)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _cache.Remove(last.Value);
        }
    }
}
