using ImageCuller2.Models;
using Windows.Storage;

namespace ImageCuller2.Services;

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

                try
                {
                    string target = UniqueDestination(destination, item.FileName);

                    if (move)
                    {
                        try
                        {
                            File.Move(item.Path, target);
                        }
                        catch (IOException)
                        {
                            // Different volume, or a provider that does not support rename: fall back.
                            File.Copy(item.Path, target, overwrite: false);
                            File.Delete(item.Path);
                        }
                    }
                    else
                    {
                        File.Copy(item.Path, target, overwrite: false);
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
                if (useRecycleBin)
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.Path);
                    await file.DeleteAsync(StorageDeleteOption.Default);
                }
                else
                {
                    await Task.Run(() => File.Delete(item.Path), ct).ConfigureAwait(true);
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
