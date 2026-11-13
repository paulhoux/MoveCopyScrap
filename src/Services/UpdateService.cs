using Velopack;
using Velopack.Sources;

namespace MoveCopyScrap.Services;

/// <summary>
/// Updates an installed copy from the GitHub releases of this repository, with Velopack,
/// when asked to (the update button): <see cref="CheckAndDownloadAsync"/> looks for a newer
/// release and downloads it (a delta when there is one). The window then restarts into it
/// on request, or <see cref="ApplyOnExit"/> installs it once the app has closed, so an
/// update never interrupts a cull. Nothing is checked unless the user asks.
///
/// Disabled in a build-tree or zip copy: only a copy installed by Velopack's Setup.exe has
/// an updater beside it.
///
/// A release withdrawn on GitHub (deleted, or marked pre-release) is followed back to the
/// newest remaining one (AllowVersionDowngrade), which is how a bad release is rolled back.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/paulhoux/MoveCopyScrap";

    private readonly UpdateManager? _manager;
    private readonly CancellationTokenSource _stop = new();
    private UpdateInfo? _ready;

    public UpdateService()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false),
                                            new UpdateOptions { AllowVersionDowngrade = true });
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MoveCopyScrap] Updates off: {ex.Message}");
        }
    }

    /// <summary>False in a copy not installed by Velopack, which cannot update itself.</summary>
    public bool IsEnabled => _manager is not null;

    /// <summary>The installed version, or null when not installed by Velopack.</summary>
    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>
    /// Checks GitHub and downloads a newer release, reporting download progress (0-100) on a
    /// worker thread. Returns its version, or null when this copy is up to date. Throws when
    /// GitHub cannot be reached.
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync(Action<int>? progress = null)
    {
        if (_manager is null) return null;
        if (_ready is not null) return _ready.TargetFullRelease.Version.ToString();

        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;
        await _manager.DownloadUpdatesAsync(info, progress, _stop.Token).ConfigureAwait(false);
        _ready = info;
        return info.TargetFullRelease.Version.ToString();
    }

    /// <summary>Installs the downloaded update and restarts the app. Does not return.</summary>
    public void ApplyAndRestart()
    {
        if (_manager is not null && _ready is not null) _manager.ApplyUpdatesAndRestart(_ready);
    }

    /// <summary>
    /// When an update is downloaded, starts Velopack's updater, which waits for this process
    /// to exit and then installs it without UI. Call while the app is closing.
    /// </summary>
    public void ApplyOnExit()
    {
        if (_manager is null || _ready is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MoveCopyScrap] Could not apply the update: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
