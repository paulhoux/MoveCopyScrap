using MoveCopyScrap.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace MoveCopyScrap.Services;

/// <summary>
/// Produces filmstrip thumbnails on background threads.
///
/// The Windows shell thumbnail cache is used first (it is by far the fastest source and
/// it also yields poster frames for videos). If that fails - some codecs, some network
/// shares - we fall back to decoding the file ourselves at a reduced size.
/// Only the final <see cref="BitmapImage"/> creation happens on the UI thread, because
/// XAML image sources are DependencyObjects.
///
/// Scheduling. Requests go on a stack and a small pool of workers takes them newest
/// first, because the most recent request is the tile the user is looking at right now.
/// When a tile scrolls out of the filmstrip its request is only flagged as cancelled, not
/// dug out of the stack; a worker that reaches a flagged request drops it unopened. So a
/// fast fling across thousands of images costs nothing for the ones flown past, and the
/// tiles the user stops on are served first instead of waiting behind all of them.
/// A request that has already started is left to finish: a single thumbnail is quick,
/// and the result is cached on the item, so it is not wasted.
/// </summary>
public sealed class ThumbnailService
{
    private sealed class Pending
    {
        public Pending(MediaItem item, int generation)
        {
            Item = item;
            Generation = generation;
        }

        public MediaItem Item { get; }
        public int Generation { get; }

        // Both only read and written under _lock.
        public bool Cancelled;
        public bool Started;
    }

    private readonly DispatcherQueue _dispatcher;
    private readonly uint _pixelSize;
    private readonly int _maxWorkers;

    private readonly object _lock = new();
    // One entry per item that is queued or being loaded. Keyed by the item itself rather
    // than its path, so a file moved while queued still finds its entry.
    private readonly Dictionary<MediaItem, Pending> _pending = new(ReferenceEqualityComparer.Instance);
    // Newest on top. May hold the same entry twice (re-requested after a cancel) or entries
    // that are cancelled or already started; workers skip those when they pop them.
    private readonly Stack<Pending> _stack = new();
    private int _workers;
    private int _generation;

    public ThumbnailService(DispatcherQueue dispatcher, uint pixelSize = 256)
    {
        _dispatcher = dispatcher;
        _pixelSize = pixelSize;
        _maxWorkers = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    }

    /// <summary>Abandons every pending request (call when the folder changes).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _generation++;
            foreach (var entry in _pending.Values) entry.Cancelled = true;
            _pending.Clear();
            _stack.Clear();
        }
    }

    /// <summary>
    /// Queues a thumbnail load, ahead of everything requested earlier. Cheap and
    /// idempotent - safe to call from ElementPrepared. Re-requesting an item that was
    /// cancelled but not yet dropped revives it.
    /// </summary>
    public void Request(MediaItem item)
    {
        if (item.Thumbnail is not null) return;

        bool startWorker = false;
        lock (_lock)
        {
            if (_pending.TryGetValue(item, out var existing))
            {
                if (existing.Started) return;         // already loading; let it finish
                existing.Cancelled = false;
                _stack.Push(existing);                 // move it back to the top
            }
            else
            {
                var entry = new Pending(item, _generation);
                _pending.Add(item, entry);
                _stack.Push(entry);
            }

            if (_workers < _maxWorkers)
            {
                _workers++;
                startWorker = true;
            }
        }

        if (startWorker) _ = Task.Run(WorkAsync);
    }

    /// <summary>
    /// Says the item's thumbnail is no longer wanted (its tile left the filmstrip). If no
    /// worker has started on it yet it will be skipped; otherwise this does nothing.
    /// </summary>
    public void Cancel(MediaItem item)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(item, out var entry) && !entry.Started)
                entry.Cancelled = true;
        }
    }

    /// <summary>One worker: takes the newest live request until there are none left.</summary>
    private async Task WorkAsync()
    {
        while (true)
        {
            Pending? next = null;
            lock (_lock)
            {
                while (_stack.Count > 0)
                {
                    var candidate = _stack.Pop();
                    if (candidate.Started) continue;  // a duplicate of one already taken

                    if (candidate.Cancelled || candidate.Generation != _generation ||
                        candidate.Item.Thumbnail is not null)
                    {
                        // Dropped. Forget it only if the map still points at this very entry
                        // (after a Reset it may hold a newer one for the same item).
                        if (_pending.TryGetValue(candidate.Item, out var current) &&
                            ReferenceEquals(current, candidate))
                            _pending.Remove(candidate.Item);
                        continue;
                    }

                    candidate.Started = true;
                    next = candidate;
                    break;
                }

                if (next is null)
                {
                    // Decided under the lock, so a Request racing with this sees either a
                    // worker that will still pop its entry or a free worker slot.
                    _workers--;
                    return;
                }
            }

            try
            {
                await LoadAsync(next).ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    if (_pending.TryGetValue(next.Item, out var current) && ReferenceEquals(current, next))
                        _pending.Remove(next.Item);
                }
            }
        }
    }

    private bool IsCurrentGeneration(int generation)
    {
        lock (_lock) return generation == _generation;
    }

    private async Task LoadAsync(Pending request)
    {
        var item = request.Item;
        try
        {
            var loaded = await MediaImageSource.OpenAsync(item.Path, _pixelSize).ConfigureAwait(false);
            if (loaded is null) return;

            if (!IsCurrentGeneration(request.Generation))
            {
                loaded.Value.Stream.Dispose();
                return;
            }

            var payload = loaded.Value;
            var completed = new TaskCompletionSource();

            bool queued = _dispatcher.TryEnqueue(() => _ = ApplyOnUiThreadAsync(item, payload, completed));

            if (!queued)
            {
                payload.Stream.Dispose();
                return;
            }

            await completed.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Thumbnail] {item.FileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes one thumbnail on the calling thread, which must be the UI thread, and
    /// hands it back rather than assigning it. Used when a file has changed underneath
    /// us and the caller needs to swap the thumbnail and something else over together.
    /// </summary>
    public async Task<Microsoft.UI.Xaml.Media.ImageSource?> ReloadThumbnailAsync(string path)
    {
        var loaded = await MediaImageSource.OpenAsync(path, _pixelSize);
        if (loaded is null) return null;

        var payload = loaded.Value;
        try
        {
            var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Physical };
            if (payload.DecodePixelWidth > 0)
                bitmap.DecodePixelWidth = (int)payload.DecodePixelWidth;

            await bitmap.SetSourceAsync(payload.Stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Thumbnail] reload {path}: {ex.Message}");
            return null;
        }
        finally
        {
            payload.Stream.Dispose();
        }
    }

    /// <summary>Runs on the UI thread: turns the stream into a XAML image source.</summary>
    private static async Task ApplyOnUiThreadAsync(MediaItem item, MediaImagePayload payload, TaskCompletionSource completed)
    {
        try
        {
            var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Physical };
            if (payload.DecodePixelWidth > 0)
                bitmap.DecodePixelWidth = (int)payload.DecodePixelWidth;

            await bitmap.SetSourceAsync(payload.Stream);

            if (payload.OriginalWidth > 0 && payload.OriginalHeight > 0)
                item.AspectRatio = payload.OriginalWidth / (double)payload.OriginalHeight;
            else if (bitmap.PixelHeight > 0)
                item.AspectRatio = bitmap.PixelWidth / (double)bitmap.PixelHeight;

            item.Thumbnail = bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Thumbnail] {item.FileName}: {ex.Message}");
        }
        finally
        {
            payload.Stream.Dispose();
            completed.TrySetResult();
        }
    }
}

/// <summary>A decoded-ready stream plus what we know about the original media.</summary>
internal readonly struct MediaImagePayload
{
    public MediaImagePayload(IRandomAccessStream stream, uint originalWidth, uint originalHeight, uint decodePixelWidth)
    {
        Stream = stream;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        DecodePixelWidth = decodePixelWidth;
    }

    public IRandomAccessStream Stream { get; }
    public uint OriginalWidth { get; }
    public uint OriginalHeight { get; }
    public uint DecodePixelWidth { get; }
}

internal static class MediaImageSource
{
    /// <summary>
    /// Opens a stream suitable for a <see cref="BitmapImage"/> at roughly
    /// <paramref name="pixelSize"/> across. Never throws; returns null on failure.
    /// </summary>
    public static async Task<MediaImagePayload?> OpenAsync(string path, uint pixelSize)
    {
        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(path);
        }
        catch
        {
            return null;
        }

        // 1. Shell thumbnail cache (fast, and the only source of video poster frames).
        try
        {
            var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, pixelSize, ThumbnailOptions.ResizeThumbnail);

            if (thumbnail is not null && thumbnail.Size > 0)
                return new MediaImagePayload(thumbnail, thumbnail.OriginalWidth, thumbnail.OriginalHeight, 0);

            thumbnail?.Dispose();
        }
        catch
        {
            // fall through
        }

        if (MediaScanner.VideoExtensions.Contains(Path.GetExtension(path)))
            return null; // no shell frame available and we do not decode video ourselves

        // 2. Decode the image ourselves at a reduced size.
        IRandomAccessStream? stream = null;
        try
        {
            stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            // OrientedPixelWidth/Height, not PixelWidth/Height. The plain pair is the raw
            // pixel array as stored; the oriented pair has the EXIF orientation tag applied,
            // so for a photo tagged 6 or 8 (the two most common camera portrait tags) the two
            // are transposed. We size the carousel slot from this, and the decoder scales to
            // DecodePixelWidth *after* orienting, so the oriented pair is the one that matches
            // what ends up on screen. Files without a tag report both pairs identically.
            uint w = decoder.OrientedPixelWidth;
            uint h = decoder.OrientedPixelHeight;
            stream.Seek(0);
            uint decodeWidth = pixelSize == 0 ? 0 : Math.Min(w == 0 ? pixelSize : w, pixelSize);
            return new MediaImagePayload(stream, w, h, decodeWidth);
        }
        catch
        {
            stream?.Dispose();
            return null;
        }
    }
}
