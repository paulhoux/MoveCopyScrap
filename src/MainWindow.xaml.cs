using System.Collections.ObjectModel;
using System.Numerics;
using ImageCuller2.Models;
using ImageCuller2.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace ImageCuller2;

public sealed partial class MainWindow : Window
{
    private const double ThumbSize = 98;
    private const double ThumbSpacing = 6;
    private const string GlyphStarOutline = "\uE734";
    private const string GlyphStarFilled = "\uE735";

    private readonly ObservableCollection<MediaItem> _items = new();
    private readonly ThumbnailService _thumbnails;

    private MarkStore? _markStore;
    private string? _folder;
    private int _currentIndex = -1;
    private bool _fillMode;
    private bool _busy;
    private AppWindow? _appWindow;

    public MainWindow()
    {
        InitializeComponent();

        _thumbnails = new ThumbnailService(DispatcherQueue);

        Title = "ImageCuller 2";
        ConfigureWindow();

        Filmstrip.ItemsSource = _items;

        Carousel.NavigationRequested += (_, delta) => SetIndex(_currentIndex + delta, animate: true);
        Carousel.CenterActivated += (_, _) => SetFillMode(!_fillMode);
        Carousel.MarkToggleRequested += (_, _) => ToggleMark();

        RootGrid.PreviewKeyDown += OnPreviewKeyDown;
        RootGrid.SizeChanged += (_, _) => UpdateInsets();
        RootGrid.Loaded += (_, _) =>
        {
            UpdateInsets();
            RootGrid.Focus(FocusState.Programmatic);
        };

        Closed += OnClosed;

        UpdateCommandState();
        UpdateCaption();
    }

    // ---- window plumbing -------------------------------------------------

    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

        try
        {
            _appWindow?.Resize(new SizeInt32(1500, 940));
        }
        catch { /* ignore on odd DPI setups */ }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyAppIcon();

        if (_appWindow?.TitleBar is { } bar)
        {
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xE6, 0xEB);
            bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x88);
            bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
            bar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        }
    }

    /// <summary>
    /// Gives the window, its taskbar button, the title bar and the empty state the app icon.
    /// The icon embedded in the .exe covers Explorer and Alt+Tab, but WinUI 3 windows do not
    /// pick it up on their own, so the loose copy next to the exe is used here.
    /// </summary>
    private void ApplyAppIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ImageCuller.ico");
        if (!File.Exists(iconPath)) return;

        try { _appWindow?.SetIcon(iconPath); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Icon] SetIcon: {ex.Message}"); }

        try
        {
            var uri = new Uri(iconPath);
            // UriSource is assigned last: that is what kicks off the decode, so the decode
            // hints have to be in place first. The .ico carries 16..256px frames and WIC
            // picks the closest one.
            TitleBarIcon.Source = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 18,
                UriSource = uri
            };
            EmptyStateIcon.Source = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 72,
                UriSource = uri
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Icon] load: {ex.Message}");
        }
    }

    private void UpdateInsets()
    {
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        double rightInset = (_appWindow?.TitleBar.RightInset ?? 0) / Math.Max(0.1, scale);
        TitleBarRightPad.Width = new GridLength(rightInset > 1 ? rightInset : 150);

        if (TopChrome.ActualHeight > 0) Carousel.ContentTopInset = TopChrome.ActualHeight + 8;
        if (FilmstripHost.ActualHeight > 0) Carousel.ContentBottomInset = FilmstripHost.ActualHeight + 8;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Carousel.ReleaseMedia();
        _markStore?.Dispose();
        _markStore = null;
    }

    // ---- folder loading --------------------------------------------------

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null) await LoadFolderAsync(folder);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not open the folder picker", ex.Message);
            return null;
        }
    }

    private async Task LoadFolderAsync(string folder)
    {
        ShowBusy($"Reading {System.IO.Path.GetFileName(folder)}…", 0);

        try
        {
            _markStore?.Dispose();
            _markStore = await MarkStore.OpenAsync(folder);

            var found = await MediaScanner.ScanAsync(folder);

            _thumbnails.Reset();
            _folder = folder;

            _items.Clear();
            foreach (var item in found)
            {
                item.IsMarked = _markStore.IsMarked(item.FileName);
                _items.Add(item);
            }

            _markStore.Prune(_items.Select(i => i.FileName));

            _currentIndex = _items.Count == 0 ? -1 : 0;
            foreach (var item in _items) item.IsCurrent = false;
            if (_currentIndex >= 0) _items[_currentIndex].IsCurrent = true;

            Carousel.SetItems(_items, Math.Max(0, _currentIndex));
            PrefetchThumbnails();

            FolderLabel.Text = folder;
            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_items.Count == 0)
                ShowStatus(InfoBarSeverity.Informational, "No supported media in that folder", folder);

            DispatcherQueue.TryEnqueue(() => CenterFilmstrip(_currentIndex, animate: false));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not read that folder", ex.Message);
        }
        finally
        {
            HideBusy();
            UpdateCommandState();
            UpdateCaption();
            RootGrid.Focus(FocusState.Programmatic);
        }
    }

    // ---- navigation ------------------------------------------------------

    private MediaItem? CurrentItem =>
        _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex] : null;

    private void SetIndex(int index, bool animate)
    {
        if (_items.Count == 0) return;
        index = Math.Clamp(index, 0, _items.Count - 1);
        if (index == _currentIndex) return;

        if (_currentIndex >= 0 && _currentIndex < _items.Count) _items[_currentIndex].IsCurrent = false;
        _currentIndex = index;
        _items[_currentIndex].IsCurrent = true;

        Carousel.SetCurrentIndex(index, animate);
        CenterFilmstrip(index, animate);
        PrefetchThumbnails();
        UpdateCommandState();
        UpdateCaption();
    }

    private void CenterFilmstrip(int index, bool animate)
    {
        if (index < 0) return;

        double pitch = ThumbSize + ThumbSpacing;
        double target = (index * pitch) + (ThumbSize / 2) - (FilmstripScroller.ViewportWidth / 2);
        double max = Math.Max(0, FilmstripScroller.ScrollableWidth);
        FilmstripScroller.ChangeView(Math.Clamp(target, 0, max), null, null, disableAnimation: !animate);
    }

    /// <summary>Warms the thumbnail cache around the current position.</summary>
    private void PrefetchThumbnails()
    {
        int from = Math.Max(0, _currentIndex - 12);
        int to = Math.Min(_items.Count - 1, _currentIndex + 12);
        for (int i = from; i <= to; i++) _thumbnails.Request(_items[i]);
    }

    private void OnFilmstripElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index >= 0 && args.Index < _items.Count)
            _thumbnails.Request(_items[args.Index]);

        if (args.Element is FrameworkElement element)
        {
            element.Tapped -= OnThumbnailTapped;
            element.Tapped += OnThumbnailTapped;
        }
    }

    private void OnThumbnailTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not UIElement element) return;
        int index = Filmstrip.GetElementIndex(element);
        if (index >= 0) SetIndex(index, animate: true);
        RootGrid.Focus(FocusState.Programmatic);
    }

    // ---- marking ---------------------------------------------------------

    private void OnMarkClick(object sender, RoutedEventArgs e)
    {
        ToggleMark();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void ToggleMark()
    {
        var item = CurrentItem;
        if (item is null || _markStore is null) return;

        item.IsMarked = !item.IsMarked;
        _markStore.Set(item.FileName, item.IsMarked);

        Carousel.RefreshMarkVisuals();
        UpdateCommandState();
        UpdateCaption();
    }

    private List<MediaItem> MarkedItems() => _items.Where(i => i.IsMarked).ToList();

    // ---- file commands ---------------------------------------------------

    private async void OnCopyMarkedClick(object sender, RoutedEventArgs e)
    {
        var marked = MarkedItems();
        if (marked.Count == 0 || _busy) return;

        string? destination = await PickFolderAsync();
        if (destination is null) return;

        if (string.Equals(destination.TrimEnd('\\'), _folder?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus(InfoBarSeverity.Warning, "Pick a different folder",
                "The destination is the folder you are culling.");
            return;
        }

        ShowBusy($"Copying {marked.Count} file(s)…", marked.Count);
        var result = await FileOperations.CopyAsync(marked, destination, CreateProgress());
        HideBusy();

        Report("Copied", result, destination);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private async void OnMoveMarkedClick(object sender, RoutedEventArgs e)
    {
        var marked = MarkedItems();
        if (marked.Count == 0 || _busy) return;

        string? destination = await PickFolderAsync();
        if (destination is null) return;

        if (string.Equals(destination.TrimEnd('\\'), _folder?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus(InfoBarSeverity.Warning, "Pick a different folder",
                "The destination is the folder you are culling.");
            return;
        }

        bool confirmed = await ConfirmAsync(
            $"Move {marked.Count} file{(marked.Count == 1 ? "" : "s")}?",
            $"The marked files will be moved out of this folder into:\n\n{destination}\n\n" +
            "They will disappear from the carousel and their marks will be cleared.",
            "Move");

        if (!confirmed) return;

        ShowBusy($"Moving {marked.Count} file(s)…", marked.Count);
        var result = await FileOperations.MoveAsync(marked, destination, CreateProgress());
        HideBusy();

        RemoveItems(result.Completed);
        Report("Moved", result, destination);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private async void OnDeleteMarkedClick(object sender, RoutedEventArgs e)
    {
        var marked = MarkedItems();
        if (marked.Count == 0 || _busy || _folder is null) return;

        bool recycle = FileOperations.SupportsRecycleBin(_folder);

        string body = recycle
            ? $"{marked.Count} file{(marked.Count == 1 ? "" : "s")} will be moved to the Recycle Bin."
            : $"This folder is on a network or removable location with no Recycle Bin.\n\n" +
              $"{marked.Count} file{(marked.Count == 1 ? "" : "s")} will be deleted PERMANENTLY and cannot be recovered.";

        bool confirmed = await ConfirmAsync(
            recycle ? $"Delete {marked.Count} file{(marked.Count == 1 ? "" : "s")}?"
                    : $"Permanently delete {marked.Count} file{(marked.Count == 1 ? "" : "s")}?",
            body,
            recycle ? "Delete" : "Delete permanently");

        if (!confirmed) return;

        ShowBusy($"Deleting {marked.Count} file(s)…", marked.Count);
        var result = await FileOperations.DeleteAsync(marked, recycle, CreateProgress());
        HideBusy();

        RemoveItems(result.Completed);
        Report(recycle ? "Moved to the Recycle Bin" : "Deleted", result, null);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void RemoveItems(IReadOnlyList<MediaItem> removed)
    {
        if (removed.Count == 0) return;

        var set = new HashSet<MediaItem>(removed);
        int anchor = _currentIndex;

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (!set.Contains(_items[i])) continue;
            if (i < anchor) anchor--;
            _items[i].IsMarked = false;
            _items[i].IsCurrent = false;
            _items.RemoveAt(i);
        }

        _markStore?.Remove(set.Select(item => item.FileName));
        _markStore?.Flush();

        _currentIndex = _items.Count == 0 ? -1 : Math.Clamp(anchor, 0, _items.Count - 1);
        if (_currentIndex >= 0) _items[_currentIndex].IsCurrent = true;

        Carousel.SetItems(_items, Math.Max(0, _currentIndex));
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        DispatcherQueue.TryEnqueue(() => CenterFilmstrip(_currentIndex, animate: false));
        PrefetchThumbnails();
        UpdateCommandState();
        UpdateCaption();
    }

    private IProgress<(int done, int total)> CreateProgress() =>
        new Progress<(int done, int total)>(p =>
        {
            BusyProgress.IsIndeterminate = false;
            BusyProgress.Maximum = Math.Max(1, p.total);
            BusyProgress.Value = p.done;
            BusyText.Text = $"{p.done} of {p.total}";
        });

    private void Report(string verb, FileOperationResult result, string? destination)
    {
        if (result.HasFailures)
        {
            string detail = string.Join("\n", result.Failures.Take(5).Select(f => $"{f.FileName}: {f.Error}"));
            if (result.Failures.Count > 5) detail += $"\n…and {result.Failures.Count - 5} more.";

            ShowStatus(InfoBarSeverity.Warning,
                $"{verb} {result.Succeeded}, {result.Failures.Count} failed", detail);
        }
        else
        {
            string where = destination is null ? "" : $" to {destination}";
            ShowStatus(InfoBarSeverity.Success,
                $"{verb} {result.Succeeded} file{(result.Succeeded == 1 ? "" : "s")}{where}", null);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            // Cancel is the default so that a stray Enter never destroys files.
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    // ---- fill mode -------------------------------------------------------

    private void OnFillModeClick(object sender, RoutedEventArgs e)
    {
        SetFillMode(FillModeButton.IsChecked == true);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void SetFillMode(bool on)
    {
        if (_items.Count == 0) on = false;
        if (_fillMode == on)
        {
            FillModeButton.IsChecked = on;
            return;
        }

        _fillMode = on;
        FillModeButton.IsChecked = on;

        UpdateInsets();
        Carousel.SetFillMode(on);

        AnimateChrome(TopChrome, on ? -(float)Math.Max(1, TopChrome.ActualHeight) : 0f, on ? 0f : 1f);
        AnimateChrome(FilmstripHost, on ? (float)Math.Max(1, FilmstripHost.ActualHeight) : 0f, on ? 0f : 1f);

        TopChrome.IsHitTestVisible = !on;
        FilmstripHost.IsHitTestVisible = !on;
        Status.IsOpen = false;
    }

    private static void AnimateChrome(FrameworkElement element, float translateY, float opacity)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.15f, 0f), new Vector2(0f, 1f));

        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertExpressionKeyFrame(0f, "this.StartingValue");
        move.InsertKeyFrame(1f, new Vector3(0, translateY, 0), ease);
        move.Duration = Controls.CarouselView.ModeDuration;
        visual.StartAnimation("Translation", move);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertExpressionKeyFrame(0f, "this.StartingValue");
        fade.InsertKeyFrame(1f, opacity, ease);
        fade.Duration = Controls.CarouselView.ModeDuration;
        visual.StartAnimation("Opacity", fade);
    }

    private void ToggleFullScreen()
    {
        if (_appWindow is null) return;

        bool isFullScreen = _appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        _appWindow.SetPresenter(isFullScreen ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);
    }

    // ---- keyboard --------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_busy) return;

        bool ctrl = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case VirtualKey.Left:
                SetIndex(_currentIndex - 1, animate: true);
                e.Handled = true;
                break;

            case VirtualKey.Right:
                SetIndex(_currentIndex + 1, animate: true);
                e.Handled = true;
                break;

            case VirtualKey.Home:
                SetIndex(0, animate: true);
                e.Handled = true;
                break;

            case VirtualKey.End:
                SetIndex(_items.Count - 1, animate: true);
                e.Handled = true;
                break;

            case VirtualKey.Space:
                ToggleMark();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
            case VirtualKey.F:
                SetFillMode(!_fillMode);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                if (_fillMode) { SetFillMode(false); e.Handled = true; }
                break;

            case VirtualKey.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;

            case VirtualKey.Delete:
                if (MarkedItems().Count > 0) { OnDeleteMarkedClick(this, new RoutedEventArgs()); e.Handled = true; }
                break;

            case VirtualKey.O:
                if (ctrl) { OnOpenFolderClick(this, new RoutedEventArgs()); e.Handled = true; }
                break;

            case VirtualKey.C:
                if (ctrl && MarkedItems().Count > 0) { OnCopyMarkedClick(this, new RoutedEventArgs()); e.Handled = true; }
                break;

            case VirtualKey.M:
                if (ctrl && MarkedItems().Count > 0) { OnMoveMarkedClick(this, new RoutedEventArgs()); e.Handled = true; }
                break;
        }
    }

    // ---- chrome state ----------------------------------------------------

    private void UpdateCommandState()
    {
        int markedCount = _items.Count(i => i.IsMarked);
        bool hasMarked = markedCount > 0;

        MarkCountText.Text = markedCount == 1 ? "1 marked" : $"{markedCount} marked";
        CopyButton.IsEnabled = hasMarked && !_busy;
        MoveButton.IsEnabled = hasMarked && !_busy;
        DeleteButton.IsEnabled = hasMarked && !_busy;
        MarkButton.IsEnabled = CurrentItem is not null && !_busy;

        bool marked = CurrentItem?.IsMarked == true;
        MarkGlyph.Glyph = marked ? GlyphStarFilled : GlyphStarOutline;
        MarkButtonText.Text = marked ? "Unmark" : "Mark";

        PositionText.Text = _items.Count == 0 ? "" : $"{_currentIndex + 1} / {_items.Count}";
    }

    private void UpdateCaption()
    {
        var item = CurrentItem;
        if (item is null)
        {
            CaptionText.Text = _folder is null ? "" : "No supported media in this folder.";
            return;
        }

        string size = item.SizeBytes >= 1024 * 1024
            ? $"{item.SizeBytes / 1024.0 / 1024.0:0.#} MB"
            : $"{Math.Max(1, item.SizeBytes / 1024)} KB";

        CaptionText.Text = $"{item.FileName}    ·    {size}    ·    {item.LastWriteUtc.ToLocalTime():g}" +
                           (item.IsMarked ? "    ·    marked" : "");
    }

    // ---- status / busy ---------------------------------------------------

    private void ShowStatus(InfoBarSeverity severity, string title, string? message)
    {
        Status.Severity = severity;
        Status.Title = title;
        Status.Message = message ?? string.Empty;
        Status.IsOpen = true;
    }

    private void ShowBusy(string text, int total)
    {
        _busy = true;
        BusyText.Text = text;
        BusyProgress.IsIndeterminate = total <= 0;
        BusyProgress.Maximum = Math.Max(1, total);
        BusyProgress.Value = 0;
        BusyOverlay.Visibility = Visibility.Visible;
        UpdateCommandState();
    }

    private void HideBusy()
    {
        _busy = false;
        BusyOverlay.Visibility = Visibility.Collapsed;
        UpdateCommandState();
    }
}
