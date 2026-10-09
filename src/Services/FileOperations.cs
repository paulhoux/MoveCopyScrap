using MoveCopyScrap.Models;
using Windows.Storage;

namespace MoveCopyScrap.Services;

public enum FileOperationKind
{
    Copy,
    Move,
    Delete
}

public sealed class FileOperationResult
{
    public int Succeeded { get; set; }
    public List<(string FileName, string Error)> Failures { get; } = new();
    public List<MediaItem> Completed { get; } = new();

    public bool HasFailures => Failures.Count > 0;
}

/// <summary>Copy / move / delete for the marked set, with progress and per-file error capture.</summary>
public static class FileOperations
{
    /// <summary>
    /// True when deleting under <paramref name="path"/> can go to the Recycle Bin.
    /// UNC paths and mapped network drives have no Recycle Bin - deletes there are permanent.
    /// </summary>
    public static bool SupportsRecycleBin(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;

            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;

            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch
        {
            return false;
        }
    }

    public static Task<FileOperationResult> CopyAsync(
        IReadOnlyList<MediaItem> items, string destination,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
        => TransferAsync(items, destination, move: false, progress, ct);

    public static Task<FileOperationResult> MoveAsync(
        IReadOnlyList<MediaItem> items, string destination,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
        => TransferAsync(items, destination, move: true, progress, ct);

    private static async Task<FileOperationResult> TransferAsync(
        IReadOnlyList<MediaItem> items, string destination, bool move,
        IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        var result = new FileOperationResult();

        await Task.Run(() =>
        {
            Directory.CreateDirectory(destination);

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = items[i];

                // An item may be several files (a RAW+JPEG pair, sidecars). They go together
                // or not at all: if one fails, the ones already done are put back.
                var sources = item.AllPaths.ToList();
                var done = new List<(string From, string To)>(sources.Count);

                try
                {
                    var targets = UniqueDestinations(destination, sources.Select(p => Path.GetFileName(p)!).ToList());

                    for (int k = 0; k < sources.Count; k++)
                    {
                        if (move) MoveFile(sources[k], targets[k]);
                        else File.Copy(sources[k], targets[k], overwrite: false);
                        done.Add((sources[k], targets[k]));
                    }

                    result.Succeeded++;
                    result.Completed.Add(item);
                }
                catch (Exception ex)
                {
                    for (int k = done.Count - 1; k >= 0; k--)
                    {
                        try
                        {
                            if (move) MoveFile(done[k].To, done[k].From);
                            else File.Delete(done[k].To);
                        }
                        catch
                        {
                            // Best effort; the error below still reports the item as failed.
                        }
                    }

                    result.Failures.Add((item.FileName, ex.Message));
                }

                progress?.Report((i + 1, items.Count));
            }
        }, ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Deletes the given files. When <paramref name="useRecycleBin"/> is true the files go to
    /// the Recycle Bin; otherwise they are removed permanently.
    /// </summary>
    public static async Task<FileOperationResult> DeleteAsync(
        IReadOnlyList<MediaItem> items, bool useRecycleBin,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var result = new FileOperationResult();

        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];

            try
            {
                // Companions first and the shown file last: if something fails part-way,
                // the item keeps the file it is shown by and stays in the list.
                foreach (var path in item.CompanionPaths.Append(item.Path))
                {
                    if (useRecycleBin)
                    {
                        var file = await StorageFile.GetFileFromPathAsync(path);
                        await file.DeleteAsync(StorageDeleteOption.Default);
                    }
                    else
                    {
                        await Task.Run(() => File.Delete(path), ct).ConfigureAwait(true);
                    }
                }

                result.Succeeded++;
                result.Completed.Add(item);
            }
            catch (Exception ex)
            {
                result.Failures.Add((item.FileName, ex.Message));
            }

            progress?.Report((i + 1, items.Count));
        }

        return result;
    }

    private static void MoveFile(string from, string to)
    {
        try
        {
            File.Move(from, to);
        }
        catch (IOException) when (!File.Exists(to))
        {
            // Different volume, or a provider that does not support rename: fall back.
            File.Copy(from, to, overwrite: false);
            File.Delete(from);
        }
    }

    /// <summary>
    /// Non-colliding paths for a set of files that belong together, all given the same
    /// " (n)" suffix so they still match up afterwards. The suffix goes right after the
    /// shared base name, so DSC01234.JPG, DSC01234.ARW and DSC01234.ARW.xmp become
    /// DSC01234 (2).JPG, DSC01234 (2).ARW and DSC01234 (2).ARW.xmp.
    /// </summary>
    public static List<string> UniqueDestinations(string folder, IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count == 1) return new List<string> { UniqueDestination(folder, fileNames[0]) };

        string stem = Path.GetFileNameWithoutExtension(fileNames[0]);

        string Rename(string name, string suffix) =>
            name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
                ? stem + suffix + name[stem.Length..]
                : Path.GetFileNameWithoutExtension(name) + suffix + Path.GetExtension(name);

        for (int n = 1; n < 10000; n++)
        {
            string suffix = n == 1 ? "" : $" ({n})";
            var candidates = fileNames.Select(name => Path.Combine(folder, Rename(name, suffix))).ToList();
            if (candidates.All(c => !File.Exists(c))) return candidates;
        }

        string unique = $" ({Guid.NewGuid():N})";
        return fileNames.Select(name => Path.Combine(folder, Rename(name, unique))).ToList();
    }

    /// <summary>Returns a non-colliding path, appending " (2)", " (3)", ... as needed.</summary>
    public static string UniqueDestination(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);

        for (int n = 2; n < 10000; n++)
        {
            candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){ext}");
    }
}
