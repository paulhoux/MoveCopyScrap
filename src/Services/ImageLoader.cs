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

    // Loads under way, so a picture asked for again before its decode is done (the
    // carousel re-requests on every layout pass) shares that decode instead of starting
    // another. Matters for developed RAWs, which take a second each.
    private readonly Dictionary<string, (int Bucket, Task<BitmapImage?> Task)> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

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
        _inFlight.Clear();
    }

    /// <summary>
    /// Returns a bitmap for <paramref name="item"/> decoded to at least
    /// <paramref name="targetPixelWidth"/> physical pixels wide, rounded up to a
    /// <see cref="BucketStep"/> multiple. Never decodes larger than the source.
    /// Null when the picture cannot be loaded, or - for a RAW being developed - when a
    /// newer request overtook this one; asking again later is fine.
    /// </summary>
    public async Task<BitmapImage?> LoadAsync(MediaItem item, int targetPixelWidth)
    {
        int bucket = Bucket(targetPixelWidth);

        if (_cache.TryGetValue(item.Path, out var cached) && cached.DecodedWidth >= bucket)
        {
            Touch(cached);
            return cached.Bitmap;
        }

        if (_inFlight.TryGetValue(item.Path, out var pending) && pending.Bucket >= bucket)
            return await pending.Task;

        var task = DecodeAsync(item, bucket, "");
        _inFlight[item.Path] = (bucket, task);
        try
        {
            return await task;
        }
        finally
        {
            if (_inFlight.TryGetValue(item.Path, out var current) && current.Task == task)
                _inFlight.Remove(item.Path);
        }
    }

    /// <summary>
    /// Decodes afresh, ignoring - and then replacing - anything already cached for this
    /// file. Used after the file itself has changed on disk.
    ///
    /// The old bitmap deliberately stays in the cache until the new one is in hand: the
    /// carousel may ask for this picture at any moment, and handing it a stale bitmap is
    /// far better than handing it nothing, or than opening a window in which a fresh
    /// decode gets the rotation applied twice.
    /// </summary>
    public Task<BitmapImage?> ReloadAsync(MediaItem item, int targetPixelWidth) =>
        DecodeAsync(item, Bucket(targetPixelWidth), "reload ");

    private static int Bucket(int targetPixelWidth) =>
        Math.Clamp(((targetPixelWidth + BucketStep - 1) / BucketStep) * BucketStep, BucketStep, 8192);

    private async Task<BitmapImage?> DecodeAsync(MediaItem item, int bucket, string what)
    {
        var (payload, satisfies) = MediaScanner.IsRaw(item.Path)
            ? await OpenRawAsync(item.Path, bucket).ConfigureAwait(true)
            : (await MediaImageSource.OpenAsync(item.Path, (uint)bucket).ConfigureAwait(true), bucket);
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

            Insert(item.Path, bitmap, satisfies);
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageLoader] {what}{item.FileName}: {ex.Message}");
            return null;
        }
        finally
        {
            data.Stream.Dispose();
        }
    }

    /// <summary>
    /// A RAW is shown from the JPEG the camera embedded in it whenever that is big enough.
    /// Windows' RAW codec would also answer, but past a certain size its shell thumbnail
    /// switches from that JPEG to decoding the sensor data without the camera's lens
    /// corrections - visibly darker corners and bowed lines - so one picture would change
    /// look as the window grew. Beyond the JPEG's size the RAW is developed with the
    /// camera's own corrections (<see cref="RawDevelop"/>); where that is not possible the
    /// JPEG is shown at its full size.
    ///
    /// Also returns the request size the result satisfies, which is what gets cached: a
    /// JPEG standing in for a develop that was overtaken must not stop a later develop.
    /// </summary>
    private static async Task<(MediaImagePayload? Payload, int Satisfies)> OpenRawAsync(string path, int bucket)
    {
        var preview = await Task.Run(() => RawPreview.Find(path)).ConfigureAwait(false);
        if (preview is not { } jpeg)
            return (await MediaImageSource.OpenAsync(path, (uint)bucket).ConfigureAwait(false), bucket);

        int jpegWidth = jpeg.Orientation >= 5 && jpeg.Orientation <= 8 ? jpeg.Height : jpeg.Width;
        if (bucket <= jpegWidth)
            return (await RawPreview.OpenAsync(path, (uint)bucket).ConfigureAwait(false), bucket);

        var (outcome, developed) = await RawDevelop.OpenAsync(path, (uint)bucket).ConfigureAwait(false);
        return outcome switch
        {
            RawDevelop.Outcome.Developed => (developed, bucket),
            RawDevelop.Outcome.Superseded => (null, 0),
            _ => (await RawPreview.OpenAsync(path, 0).ConfigureAwait(false), bucket),
        };
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
