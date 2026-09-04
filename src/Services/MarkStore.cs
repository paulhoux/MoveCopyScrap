using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoveCopyScrap.Services;

/// <summary>
/// Persists the set of marked files for one folder.
///
/// The state lives in %LOCALAPPDATA%\MoveCopyScrap\marks\&lt;folder&gt;-&lt;hash&gt;.json so that
/// nothing is ever written into the user's picture folders (which may be read-only or on a
/// network share). Writes are debounced and atomic, so a crash leaves either the previous
/// or the new file intact - never a half-written one.
/// </summary>
public sealed class MarkStore : IDisposable
{
    private const int DebounceMilliseconds = 600;

    private readonly string _stateFile;
    private readonly string _folder;
    private readonly HashSet<string> _marked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly object _sync = new();
    private bool _dirty;
    private bool _disposed;

    private MarkStore(string folder, string stateFile)
    {
        _folder = folder;
        _stateFile = stateFile;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static string StateDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "MoveCopyScrap", "marks");

    public string StateFilePath => _stateFile;

    public int Count { get { lock (_sync) return _marked.Count; } }

    /// <summary>Opens (or creates) the mark state for a folder and loads any previous marks.</summary>
    public static async Task<MarkStore> OpenAsync(string folder)
    {
        Directory.CreateDirectory(StateDirectory);

        string normalized = Normalize(folder);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..16];
        string leaf = Sanitize(Path.GetFileName(normalized));
        if (leaf.Length == 0) leaf = "root";

        var store = new MarkStore(folder, Path.Combine(StateDirectory, $"{leaf}-{hash}.json"));
        await store.LoadAsync().ConfigureAwait(false);
        return store;
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            await using var stream = File.OpenRead(_stateFile);
            var state = await JsonSerializer.DeserializeAsync(stream, MarkJsonContext.Default.MarkState)
                            .ConfigureAwait(false);
            if (state?.Marked is null) return;

            lock (_sync)
            {
                foreach (var name in state.Marked) _marked.Add(name);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MarkStore] load failed: {ex.Message}");
        }
    }

    public bool IsMarked(string fileName)
    {
        lock (_sync) return _marked.Contains(fileName);
    }

    public void Set(string fileName, bool marked)
    {
        lock (_sync)
        {
            bool changed = marked ? _marked.Add(fileName) : _marked.Remove(fileName);
            if (!changed) return;
            _dirty = true;
        }
        ScheduleSave();
    }

    public void Remove(IEnumerable<string> fileNames)
    {
        bool changed = false;
        lock (_sync)
        {
            foreach (var name in fileNames) changed |= _marked.Remove(name);
            if (!changed) return;
            _dirty = true;
        }
        ScheduleSave();
    }

    public void ClearAll()
    {
        lock (_sync)
        {
            if (_marked.Count == 0) return;
            _marked.Clear();
            _dirty = true;
        }
        ScheduleSave();
    }

    /// <summary>Drops marks whose files no longer exist in the folder listing.</summary>
    public void Prune(IEnumerable<string> existingFileNames)
    {
        var keep = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            int before = _marked.Count;
            _marked.IntersectWith(keep);
            if (_marked.Count == before) return;
            _dirty = true;
        }
        ScheduleSave();
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync) return _marked.ToList();
    }

    private void ScheduleSave()
    {
        if (_disposed) return;
        try { _timer.Change(DebounceMilliseconds, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Writes the state immediately (also called on shutdown).</summary>
    public void Flush()
    {
        MarkState state;
        lock (_sync)
        {
            if (!_dirty) return;
            _dirty = false;
            state = new MarkState
            {
                Folder = _folder,
                UpdatedUtc = DateTime.UtcNow,
                Marked = _marked.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        try
        {
            Directory.CreateDirectory(StateDirectory);
            string temp = _stateFile + ".tmp";
            string json = JsonSerializer.Serialize(state, MarkJsonContext.Default.MarkState);
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, _stateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MarkStore] save failed: {ex.Message}");
            lock (_sync) _dirty = true;   // try again on the next change / on shutdown
        }
    }

    private static string Normalize(string folder)
    {
        try { folder = Path.GetFullPath(folder); } catch { /* keep as-is */ }
        return folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c == ':' ? '_' : c);
        var result = sb.ToString().Trim('_', '.', ' ');
        return result.Length > 48 ? result[..48] : result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Flush();
        _timer.Dispose();
    }
}

internal sealed class MarkState
{
    [JsonPropertyName("folder")] public string Folder { get; set; } = string.Empty;
    [JsonPropertyName("updatedUtc")] public DateTime UpdatedUtc { get; set; }
    [JsonPropertyName("marked")] public List<string> Marked { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MarkState))]
internal partial class MarkJsonContext : JsonSerializerContext
{
}
