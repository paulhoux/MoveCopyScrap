using MoveCopyScrap.Models;

namespace MoveCopyScrap.Services;

/// <summary>Enumerates the supported media files in a folder (non-recursive).</summary>
public static class MediaScanner
{
    public static HashSet<string> RawExtensions => MediaTypes.RawExtensions;
    public static HashSet<string> ImageExtensions => MediaTypes.ImageExtensions;
    public static HashSet<string> VideoExtensions => MediaTypes.VideoExtensions;

    public static bool IsRaw(string path) => MediaTypes.IsRaw(path);
    public static bool IsSupported(string path) => MediaTypes.IsMedia(path);

    /// <summary>
    /// Scans <paramref name="folder"/> on a background thread and returns the items
    /// sorted the way Explorer sorts them. Files that share a name are one item; see
    /// <see cref="MediaGrouping"/>.
    /// </summary>
    public static Task<List<MediaItem>> ScanAsync(string folder, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                files[info.Name] = info;
            }

            var groups = MediaGrouping.Build(files.Keys);
            var result = new List<MediaItem>(groups.Count);
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                var shown = files[group.Shown];
                var item = new MediaItem(shown.FullName, MediaTypes.IsVideo(shown.Name), shown.Length, shown.LastWriteTimeUtc);
                foreach (var name in group.Companions)
                    item.AddCompanion(files[name].FullName, files[name].Length);
                result.Add(item);
            }
            return result;
        }, ct);
}
