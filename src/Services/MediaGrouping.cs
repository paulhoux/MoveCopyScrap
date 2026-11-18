using MoveCopyScrap.Helpers;

namespace MoveCopyScrap.Services;

/// <summary>The file types the app shows, by extension (with the dot, any case).</summary>
public static class MediaTypes
{
    /// <summary>
    /// Camera RAW formats. Shown from the JPEG preview the camera embedded in them; see
    /// <c>ImageLoader</c> and <c>RawDevelop</c>.
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

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".m2ts", ".3gp"
    };

    /// <summary>What a camera or phone writes as the finished picture: shown in preference to anything else.</summary>
    public static readonly HashSet<string> CameraPictureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".heic", ".heif"
    };

    public static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    public static bool IsMedia(string path) => IsImage(path) || IsVideo(path);
}

/// <summary>
/// Decides which files in a folder belong together, i.e. which files with the same name
/// form one item. Each picture or video is its own item, with two kinds of files joining
/// it:
///
///  * Files that are not pictures or videos and share its name: edit sidecars
///    (DSC01234.xmp from Lightroom or Capture One), a video's thumbnail and proxy
///    (GX010001.MP4 + .THM + .LRV), an iPhone's edit file (.AAE).
///  * Files named after it in full: DSC01234.ARW.xmp from darktable, IMG_1234.jpg.json
///    from Google Takeout.
///
/// Two pictures or videos are never merged into one item, even with the same name: a
/// Live Photo's HEIC and MOV, or A.jpg and A.png, are separate items. The one exception
/// is the camera's RAW+JPEG pair - exactly one RAW and one JPEG (or HEIC) of a name -
/// which is shown by the JPEG and carries the RAW.
///
/// A same-name file that could belong to more than one item (IMG_1234.AAE beside
/// IMG_1234.HEIC and IMG_1234.MOV) joins none of them, so it is never moved or deleted
/// by mistake.
///
/// "Same name" means the name without its last extension, compared case-insensitively:
/// "Trip.2026.jpg" and "Trip.2026.xmp" belong together; "Trip.2026.jpg" and
/// "Trip.2027.jpg" do not. Files with no picture or video to join are not shown at all.
///
/// Free of WinUI so it can be tested on its own.
/// </summary>
public static class MediaGrouping
{
    public sealed record Group(string Shown, IReadOnlyList<string> Companions);

    /// <summary>
    /// Groups <paramref name="fileNames"/> (names only, all in one folder). The result is
    /// in Explorer's order of the shown names; companions are listed the paired RAW first,
    /// then everything else, in Explorer's order.
    /// </summary>
    public static List<Group> Build(IEnumerable<string> fileNames)
    {
        var names = fileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var present = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        // Files named after another file in full (DSC01234.ARW.xmp) go wherever that file
        // goes; set them aside until the items are known.
        var namedAfter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // file -> the file it is named after
        var byStem = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            // Never a picture or video, though: those are always items of their own.
            if (!MediaTypes.IsMedia(name) && stem.Length > 0 && stem.Length < name.Length && present.Contains(stem))
            {
                namedAfter[name] = stem;
                continue;
            }
            if (!byStem.TryGetValue(stem, out var members)) byStem[stem] = members = new List<string>(2);
            members.Add(name);
        }

        // Items, and which item each file belongs to.
        var items = new List<(string Shown, List<string> Companions)>();
        var itemOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var members in byStem.Values)
        {
            var media = members.Where(MediaTypes.IsMedia).ToList();
            if (media.Count == 0) continue;

            var raws = media.Where(MediaTypes.IsRaw).ToList();
            var pictures = media.Where(n => MediaTypes.CameraPictureExtensions.Contains(Path.GetExtension(n))).ToList();
            bool pair = raws.Count == 1 && pictures.Count == 1;

            int first = items.Count;
            foreach (var file in media)
            {
                if (pair && ReferenceEquals(file, raws[0])) continue;
                itemOf[file] = items.Count;
                items.Add((file, new List<string>()));
            }
            if (pair)
            {
                int at = itemOf[pictures[0]];
                items[at].Companions.Add(raws[0]);
                itemOf[raws[0]] = at;
            }

            // The rest of the name's files join the item only when there is just one.
            int created = items.Count - first;
            foreach (var other in members.Where(n => !MediaTypes.IsMedia(n)))
            {
                if (created != 1) continue;
                items[first].Companions.Add(other);
                itemOf[other] = first;
            }
        }

        // Full-name files, following chains such as X.jpg -> X.jpg.xmp -> X.jpg.xmp.bak.
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var (file, owner) in namedAfter.ToList())
            {
                if (!itemOf.TryGetValue(owner, out int at)) continue;
                items[at].Companions.Add(file);
                itemOf[file] = at;
                namedAfter.Remove(file);
                progress = true;
            }
        }

        var result = items.Select(item => new Group(
                item.Shown,
                item.Companions.OrderBy(n => MediaTypes.IsMedia(n) ? 0 : 1)
                               .ThenBy(n => n, NaturalComparer.Instance)
                               .ToList()))
            .ToList();
        result.Sort((a, b) => NaturalComparer.Instance.Compare(a.Shown, b.Shown));
        return result;
    }
}
