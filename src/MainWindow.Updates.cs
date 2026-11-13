using MoveCopyScrap.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveCopyScrap;

// The update button (see UpdateService), at the right of the button bar. One button runs
// the whole flow: Check for updates -> Checking... -> Downloading n% -> Restart to update.
// An update that is downloaded but not restarted into is installed once the window closes.
public sealed partial class MainWindow
{
    private const string GlyphUpdateCheck   = ""; // Sync
    private const string GlyphUpdateRestart = ""; // Refresh

    private readonly UpdateService _updates = new();
    private bool _checkingForUpdates;
    private bool _restartForUpdate;
    private string? _readyVersion;

    private void InitUpdates()
    {
        if (_updates.IsEnabled)
        {
            ToolTipService.SetToolTip(UpdateButton,
                $"MoveCopyScrap {_updates.CurrentVersion}. Check GitHub for a newer version.");
        }
        else
        {
            UpdateButton.IsEnabled = false;
            ToolTipService.SetToolTip(UpdateButton,
                "Only a copy installed with MoveCopyScrap's Setup can update itself.");
        }
    }

    private void SetUpdateButton(string glyph, string text)
    {
        UpdateGlyph.Glyph = glyph;
        UpdateText.Text = text;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_readyVersion is not null)
        {
            RestartForUpdate();
            return;
        }
        if (_checkingForUpdates) return;

        _checkingForUpdates = true;
        SetUpdateButton(GlyphUpdateCheck, "Checking…");
        try
        {
            string? version = await _updates.CheckAndDownloadAsync(percent =>
                DispatcherQueue.TryEnqueue(() => UpdateText.Text = $"Downloading {percent}%"));

            if (version is null)
            {
                SetUpdateButton(GlyphUpdateCheck, "Check for updates");
                ShowStatus(InfoBarSeverity.Success, "Up to date",
                           $"MoveCopyScrap {_updates.CurrentVersion} is the latest version.");
                return;
            }

            _readyVersion = version;
            SetUpdateButton(GlyphUpdateRestart, "Restart to update");
            ToolTipService.SetToolTip(UpdateButton,
                $"Restart into MoveCopyScrap {version}. Otherwise it is installed when you close the app.");
            ShowStatus(InfoBarSeverity.Informational, "Update ready",
                       $"MoveCopyScrap {version} is downloaded. Restart to use it, " +
                       "or it is installed when you close the app.");
        }
        catch (Exception ex)
        {
            SetUpdateButton(GlyphUpdateCheck, "Check for updates");
            ShowStatus(InfoBarSeverity.Warning, "Could not check for updates", ex.Message);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    private void RestartForUpdate()
    {
        if (_busy)
        {
            ShowStatus(InfoBarSeverity.Informational, "Still working",
                       "Restart once the current operation has finished.");
            return;
        }
        // Closing runs OnClosed, which saves marks and pending rotations first.
        _restartForUpdate = true;
        Close();
    }

    /// <summary>Last thing OnClosed does, once marks and rotations are saved.</summary>
    private void FinishUpdatesOnClose()
    {
        if (_restartForUpdate)
            _updates.ApplyAndRestart();
        else
            _updates.ApplyOnExit();
        _updates.Dispose();
    }
}
