using MoveCopyScrap.Helpers;
using MoveCopyScrap.Models;

namespace MoveCopyScrap.Services;

/// <summary>Enumerates the supported media files in a folder (non-recursive).</summary>
public static class MediaScanner
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif",
        ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".ico", ".dds"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".m2ts", ".3gp"
    };

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    /// <summary>
    /// Scans <paramref name="folder"/> on a background thread and returns the items
    /// sorted the way Explorer sorts them.
    /// </summary>
    public static Task<List<MediaItem>> ScanAsync(string folder, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var result = new List<MediaItem>(512);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();

                var ext = info.Extension;
                bool isImage = ImageExtensions.Contains(ext);
                bool isVideo = !isImage && VideoExtensions.Contains(ext);
                if (!isImage && !isVideo) continue;

                result.Add(new MediaItem(info.FullName, isVideo, info.Length, info.LastWriteTimeUtc));
            }

            result.Sort((a, b) => NaturalComparer.Instance.Compare(a.FileName, b.FileName));
            return result;
        }, ct);
}
