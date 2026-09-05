using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoveCopyScrap.Models;

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
    private readonly Dictionary<string, string> _assignments = new(StringComparer.OrdinalIgnoreCase);
    private List<MarkGroup> _groups = new() { new MarkGroup() };
    private string? _activeGroupId;
    private readonly object _writeSync = new();
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
    public static Task<MarkStore> OpenAsync(string folder) => OpenAsync(folder, StateDirectory);

    internal static async Task<MarkStore> OpenAsync(string folder, string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);

        string normalized = Normalize(folder);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..16];
        string leaf = Sanitize(Path.GetFileName(normalized));
        if (leaf.Length == 0) leaf = "root";

        var store = new MarkStore(folder, Path.Combine(stateDirectory, $"{leaf}-{hash}.json"));
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
                if (state.Groups is { Count: > 0 })
                    _groups = state.Groups.Where(g => !string.IsNullOrWhiteSpace(g.Id))
                        .DistinctBy(g => g.Id).ToList();
                if (_groups.Count == 0) _groups.Add(new MarkGroup());
                _activeGroupId = state.ActiveGroupId;
                var assignments = new Dictionary<string, string>(state.Assignments, StringComparer.OrdinalIgnoreCase);
                foreach (var name in _marked)
                    _assignments[name] = assignments.TryGetValue(name, out var id) &&
                        _groups.Any(g => g.Id == id) ? id : _groups[0].Id;
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

    public List<MarkGroup> Groups { get { lock (_sync) return _groups.Select(g => g.Clone()).ToList(); } }
    public string ActiveGroupId { get { lock (_sync) return _groups.Any(g => g.Id == _activeGroupId) ? _activeGroupId! : _groups[0].Id; } }
    public string? GroupFor(string fileName)
    {
        lock (_sync) return _assignments.GetValueOrDefault(fileName);
    }

    public void SaveGroups(IEnumerable<MarkGroup> groups, string activeId)
    {
        lock (_sync)
        {
            _groups = groups.Select(g => g.Clone()).ToList();
            _activeGroupId = activeId;
            _dirty = true;
        }
        ScheduleSave();
    }

    public void Assign(string fileName, string? groupId)
    {
        lock (_sync)
        {
            if (groupId is null) { _marked.Remove(fileName); _assignments.Remove(fileName); }
            else { _marked.Add(fileName); _assignments[fileName] = groupId; }
            _dirty = true;
        }
        ScheduleSave();
    }

    public void Set(string fileName, bool marked)
    {
        lock (_sync)
        {
            bool changed = marked ? _marked.Add(fileName) : _marked.Remove(fileName);
            if (marked) _assignments.TryAdd(fileName, _groups[0].Id);
            else _assignments.Remove(fileName);
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
            foreach (var name in fileNames) { changed |= _marked.Remove(name); _assignments.Remove(name); }
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
            _assignments.Clear();
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
            foreach (var name in _assignments.Keys.Where(n => !keep.Contains(n)).ToList()) _assignments.Remove(name);
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
        lock (_writeSync) FlushCore();
    }

    private void FlushCore()
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
                Marked = _marked.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                Groups = _groups.Select(g => g.Clone()).ToList(),
                ActiveGroupId = _activeGroupId,
                Assignments = new Dictionary<string, string>(_assignments, StringComparer.OrdinalIgnoreCase)
            };
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
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
    public List<MarkGroup> Groups { get; set; } = new();
    public string? ActiveGroupId { get; set; }
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MarkState))]
internal partial class MarkJsonContext : JsonSerializerContext
{
}
