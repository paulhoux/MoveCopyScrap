using MoveCopyScrap.Helpers;
using MoveCopyScrap.Models;

namespace MoveCopyScrap.Services;

/// <summary>Enumerates the supported media files in a folder (non-recursive).</summary>
public static class MediaScanner
{
    /// <summary>
    /// Camera RAW formats. Decoded by Windows when a RAW codec is installed (Microsoft's
    /// free "Raw Image Extension"), otherwise shown from the JPEG preview inside the file.
    /// </summary>
    public static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw", ".srf", ".sr2",            // Sony
        ".cr2", ".cr3", ".crw",            // Canon
        ".nef", ".nrw",                    // Nikon
        ".dng",                            // Adobe, Leica, Pentax, phones
        ".orf",                            // Olympus / OM System
        ".rw2", ".rwl",                    // Panasonic, Leica
        ".raf",                            // Fujifilm
        ".pef",                            // Pentax
        ".srw",                            // Samsung
        ".3fr", ".fff",                    // Hasselblad
        ".iiq",                            // Phase One
        ".erf", ".kdc", ".dcr", ".mrw", ".mos", ".x3f"
    };

    public static readonly HashSet<string> ImageExtensions = new(
        new[]
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif",
            ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".ico", ".dds"
        }.Concat(RawExtensions),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));

    /// <summary>Formats a camera writes beside a RAW in RAW+JPEG mode.</summary>
    private static readonly HashSet<string> PairPartnerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".heic", ".heif"
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
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            var media = new List<(FileInfo Info, bool IsVideo)>(512);
            // .xmp sidecars, keyed by the name they belong to: "DSC01234" for DSC01234.xmp
            // (Lightroom, Capture One) and "DSC01234.ARW" for DSC01234.ARW.xmp (darktable).
            var sidecars = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();

                var ext = info.Extension;
                if (ext.Equals(".xmp", StringComparison.OrdinalIgnoreCase))
                {
                    string owner = Path.GetFileNameWithoutExtension(info.Name);
                    if (!sidecars.TryGetValue(owner, out var list)) sidecars[owner] = list = new List<FileInfo>(1);
                    list.Add(info);
                    continue;
                }

                bool isImage = ImageExtensions.Contains(ext);
                bool isVideo = !isImage && VideoExtensions.Contains(ext);
                if (!isImage && !isVideo) continue;

                media.Add((info, isVideo));
            }

            // RAW+JPEG pairs: same name, one RAW and one JPEG/HEIF beside it. Anything less
            // clear-cut (two RAWs, or a JPEG and a HEIC, with one name) is left unpaired.
            static string Stem(FileInfo f) => Path.GetFileNameWithoutExtension(f.Name);

            var raws = media.Where(m => !m.IsVideo && RawExtensions.Contains(m.Info.Extension))
                            .GroupBy(m => Stem(m.Info), StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() == 1)
                            .ToDictionary(g => g.Key, g => g.First().Info, StringComparer.OrdinalIgnoreCase);

            var rawFor = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);   // partner path -> RAW
            var pairedRaws = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in media.Where(m => !m.IsVideo && PairPartnerExtensions.Contains(m.Info.Extension))
                                       .GroupBy(m => Stem(m.Info), StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() != 1 || !raws.TryGetValue(group.Key, out var raw)) continue;
                rawFor[group.First().Info.FullName] = raw;
                pairedRaws.Add(raw.FullName);
            }

            void AddSidecars(MediaItem item, FileInfo raw)
            {
                foreach (var key in new[] { Stem(raw), raw.Name })
                    if (sidecars.TryGetValue(key, out var list))
                        foreach (var sidecar in list) item.AddCompanion(sidecar.FullName, sidecar.Length, isPairedRaw: false);
            }

            var result = new List<MediaItem>(media.Count);
            foreach (var (info, isVideo) in media)
            {
                if (pairedRaws.Contains(info.FullName)) continue; // shown through its JPEG

                var item = new MediaItem(info.FullName, isVideo, info.Length, info.LastWriteTimeUtc);

                if (rawFor.TryGetValue(info.FullName, out var raw))
                {
                    item.AddCompanion(raw.FullName, raw.Length, isPairedRaw: true);
                    AddSidecars(item, raw);
                }
                else if (!isVideo && RawExtensions.Contains(info.Extension))
                {
                    AddSidecars(item, info);
                }

                result.Add(item);
            }

            result.Sort((a, b) => NaturalComparer.Instance.Compare(a.FileName, b.FileName));
            return result;
        }, ct);
}
