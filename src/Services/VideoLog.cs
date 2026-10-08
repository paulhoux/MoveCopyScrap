namespace MoveCopyScrap.Services;

/// <summary>
/// TEMPORARY diagnostics for the "video freezes when switching between videos" bug.
/// Appends one line per event to %LOCALAPPDATA%\MoveCopyScrap\video.log; the file is
/// started afresh each time the app runs. Remove this class and its calls once fixed.
/// </summary>
internal static class VideoLog
{
    private static readonly object Gate = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static bool _started;
    private static int _nextId;

    public static string FilePath => Path.Combine(SettingsStore.StateDirectory, "video.log");

    public static int NextId() => Interlocked.Increment(ref _nextId);

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (!_started)
                {
                    Directory.CreateDirectory(SettingsStore.StateDirectory);
                    File.WriteAllText(FilePath, $"video log started {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                    _started = true;
                }

                File.AppendAllText(FilePath,
                    $"{Clock.ElapsedMilliseconds,8} ms  t{Environment.CurrentManagedThreadId,-3} {message}\n");
            }
        }
        catch
        {
            // Diagnostics must never take the app down.
        }
    }
}
