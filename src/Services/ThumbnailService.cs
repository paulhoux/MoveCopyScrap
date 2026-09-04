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
/// </summary>
public sealed class ThumbnailService
{
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _gate;
    private readonly HashSet<string> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly uint _pixelSize;
    private int _generation;

    public ThumbnailService(DispatcherQueue dispatcher, uint pixelSize = 256)
    {
        _dispatcher = dispatcher;
        _pixelSize = pixelSize;
        _gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
    }

    /// <summary>Abandons every pending request (call when the folder changes).</summary>
    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        lock (_inflight) _inflight.Clear();
    }

    /// <summary>Queues a thumbnail load. Cheap and idempotent - safe to call from ElementPrepared.</summary>
    public void Request(MediaItem item)
    {
        if (item.Thumbnail is not null) return;

        lock (_inflight)
        {
            if (!_inflight.Add(item.Path)) return;
        }

        int generation = Volatile.Read(ref _generation);
        _ = Task.Run(() => LoadAsync(item, generation));
    }

    private async Task LoadAsync(MediaItem item, int generation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _generation)) return;

            var loaded = await MediaImageSource.OpenAsync(item.Path, _pixelSize).ConfigureAwait(false);
            if (loaded is null) return;

            if (generation != Volatile.Read(ref _generation))
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
        finally
        {
            lock (_inflight) _inflight.Remove(item.Path);
            _gate.Release();
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
            uint w = decoder.PixelWidth;
            uint h = decoder.PixelHeight;
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
