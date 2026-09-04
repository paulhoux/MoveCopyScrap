using System.Numerics;
using ImageCuller2.Models;
using ImageCuller2.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI;

namespace ImageCuller2.Controls;

/// <summary>
/// The 3D image carousel.
///
/// Design notes
/// ------------
/// * A fixed pool of <c>2*Wing+1</c> element "slots" is recycled; only the slots within
///   Wing positions of the current index are realised, so a folder with 50 000 photos
///   costs the same as one with five.
/// * All motion is done with the Composition layer (Translation / Scale /
///   RotationAngleInDegrees / Opacity). Those animations run on the compositor thread, so
///   they stay at full frame rate even while images decode.
/// * Perspective comes from a 4x4 matrix with M34 = -1/d installed on the *parent* visual,
///   which is the supported way to get a vanishing point in Composition. Each slot then
///   simply rotates about the Y axis.
/// * Element sizes are derived from the image aspect ratio and never change when toggling
///   fill mode, so mode changes are pure compositor work with no XAML layout pass.
/// </summary>
public sealed class CarouselView : Grid
{
    // ---- tuning ----------------------------------------------------------
    private const int Wing = 3;                      // slots kept on each side
    private const double SideScale = 0.72;           // scale of the +/-1 neighbours
    private const double FarScale = 0.55;            // scale of the (invisible) +/-2,3 buffers
    private const float SideAngleDegrees = 26f;      // Y rotation of the neighbours
    private const double SideOpacity = 0.80;
    private const double OverlapFraction = 0.055;    // how much the centre may cover a neighbour
    private const double MinRevealFraction = 0.07;   // how much of a neighbour must stay uncovered
    private const double CellWidthFraction = 0.60;   // centre cell width, fraction of the control
    private const double CellHeightFraction = 0.94;
    private const float PerspectiveDistance = 1500f;
    private const double KenBurnsZoom = 1.16;
    private const double KenBurnsDrift = 0.05;
    private static readonly TimeSpan KenBurnsPeriod = TimeSpan.FromSeconds(22);

    public static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan ModeDuration = TimeSpan.FromMilliseconds(320);

    // ---- state -----------------------------------------------------------
    private readonly Grid _stage = new();
    private readonly List<Slot> _pool = new();
    private readonly Dictionary<int, Slot> _active = new();
    private readonly ImageLoader _loader = new();

    private Compositor? _compositor;
    private CompositionEasingFunction? _ease;
    private CompositionEasingFunction? _linear;

    private IReadOnlyList<MediaItem> _items = Array.Empty<MediaItem>();
    private int _currentIndex = -1;
    private bool _fillMode;

    private static readonly SolidColorBrush MarkedBorderBrush =
        new(Color.FromArgb(0xFF, 0xFF, 0xC4, 0x4D));
    private static readonly SolidColorBrush NormalBorderBrush =
        new(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush FrameBackgroundBrush =
        new(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1F));
    private static readonly SolidColorBrush ScrubberBackgroundBrush =
        new(Color.FromArgb(0xB8, 0x10, 0x10, 0x14));
    private static readonly SolidColorBrush MarkBadgeBrush =
        new(Color.FromArgb(0xFF, 0xFF, 0xC4, 0x4D));
    private static readonly SolidColorBrush MarkGlyphBrush =
        new(Color.FromArgb(0xFF, 0x1A, 0x14, 0x00));

    private const string GlyphStar = "\uE735";
    private const string GlyphPlay = "\uE768";
    private const string GlyphPause = "\uE769";

    public CarouselView()
    {
        Background = null;
        IsHitTestVisible = true;

        _stage.HorizontalAlignment = HorizontalAlignment.Stretch;
        _stage.VerticalAlignment = VerticalAlignment.Stretch;
        Children.Add(_stage);

        SizeChanged += (_, _) => { Relayout(false); RequestPictures(); };
        PointerWheelChanged += OnPointerWheelChanged;
        Loaded += (_, _) => { EnsureInitialised(); Relayout(false); };
    }

    // ---- public API ------------------------------------------------------

    /// <summary>Raised when the user asks to move by <c>delta</c> positions (mouse).</summary>
    public event EventHandler<int>? NavigationRequested;

    /// <summary>Raised when the centre item is clicked.</summary>
    public event EventHandler? CenterActivated;

    /// <summary>Raised when the centre item is right-clicked.</summary>
    public event EventHandler? MarkToggleRequested;

    public double ContentTopInset { get; set; } = 96;

    public double ContentBottomInset { get; set; } = 150;

    public int CurrentIndex => _currentIndex;

    public bool FillMode => _fillMode;

    public MediaItem? CurrentItem =>
        _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex] : null;

    public void SetItems(IReadOnlyList<MediaItem> items, int index)
    {
        EnsureInitialised();
        _items = items;
        _loader.Clear();

        foreach (var slot in _active.Values.ToList()) Recycle(slot);
        _active.Clear();

        _currentIndex = items.Count == 0 ? -1 : Math.Clamp(index, 0, items.Count - 1);
        SyncSlots();
        Relayout(false);
        RequestPictures();
    }

    public void SetCurrentIndex(int index, bool animate)
    {
        if (_items.Count == 0) return;
        index = Math.Clamp(index, 0, _items.Count - 1);
        if (index == _currentIndex) return;

        bool bigJump = Math.Abs(index - _currentIndex) > Wing;
        _currentIndex = index;

        // The outgoing picture must not keep drifting while it cross-fades out.
        if (_fillMode) StopKenBurns();

        SyncSlots();
        Relayout(animate && !bigJump);
        RequestPictures();
    }

    public void SetFillMode(bool fill)
    {
        if (_fillMode == fill) return;
        _fillMode = fill;

        StopKenBurns();
        Relayout(true);
        RequestPictures();
    }

    /// <summary>Re-reads <see cref="MediaItem.IsMarked"/> for the realised slots.</summary>
    public void RefreshMarkVisuals()
    {
        foreach (var slot in _active.Values) UpdateMarkVisual(slot);
    }

    /// <summary>Stops and releases every video player (call before closing the window).</summary>
    public void ReleaseMedia()
    {
        foreach (var slot in _active.Values) TeardownVideo(slot);
    }

    // ---- initialisation --------------------------------------------------

    private void EnsureInitialised()
    {
        if (_compositor is not null) return;

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.15f, 0.0f), new Vector2(0.0f, 1.0f));
        _linear = _compositor.CreateLinearEasingFunction();

        for (int i = 0; i < (Wing * 2) + 1; i++)
        {
            var slot = CreateSlot();
            _pool.Add(slot);
        }
    }

    private Slot CreateSlot()
    {
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var mediaHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var overlay = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var markBadge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = MarkBadgeBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10),
            Opacity = 0,
            Child = new FontIcon
            {
                Glyph = GlyphStar,
                FontSize = 14,
                Foreground = MarkGlyphBrush
            }
        };

        var content = new Grid();
        content.Children.Add(image);
        content.Children.Add(mediaHost);
        content.Children.Add(overlay);
        content.Children.Add(markBadge);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = FrameBackgroundBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = NormalBorderBrush,
            Child = content
        };

        // NOTE: the fade is driven through the Composition visual's Opacity, so the XAML
        // UIElement.Opacity is deliberately left at its default of 1 and never touched
        // again (XAML would otherwise overwrite the animated value).
        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 200,
            Height = 140
        };
        root.Children.Add(frame);

        ElementCompositionPreview.SetIsTranslationEnabled(root, true);
        var visual = ElementCompositionPreview.GetElementVisual(root);
        visual.RotationAxis = new Vector3(0, 1, 0);
        visual.Opacity = 0;

        var slot = new Slot
        {
            Root = root,
            Frame = frame,
            Image = image,
            MediaHost = mediaHost,
            Overlay = overlay,
            MarkBadge = markBadge,
            Visual = visual
        };

        root.Tapped += (_, _) => OnSlotTapped(slot);
        root.RightTapped += (_, e) => { e.Handled = true; OnSlotRightTapped(slot); };

        _stage.Children.Add(root);
        return slot;
    }

    // ---- slot lifecycle --------------------------------------------------

    private void SyncSlots()
    {
        if (_items.Count == 0 || _currentIndex < 0)
        {
            foreach (var slot in _active.Values.ToList()) Recycle(slot);
            _active.Clear();
            return;
        }

        int lo = Math.Max(0, _currentIndex - Wing);
        int hi = Math.Min(_items.Count - 1, _currentIndex + Wing);

        foreach (var index in _active.Keys.Where(k => k < lo || k > hi).ToList())
        {
            var slot = _active[index];
            _active.Remove(index);
            Recycle(slot);
        }

        for (int i = lo; i <= hi; i++)
        {
            if (_active.ContainsKey(i)) continue;
            if (_pool.Count == 0) break;

            var slot = _pool[^1];
            _pool.RemoveAt(_pool.Count - 1);
            slot.NeedsSnap = true;
            Assign(slot, i, _items[i]);
            _active[i] = slot;
        }
    }

    private void Assign(Slot slot, int index, MediaItem item)
    {
        slot.Index = index;
        slot.Item = item;
        slot.Aspect = item.AspectRatio;
        slot.Image.Source = item.Thumbnail;
        slot.LoadedWidth = 0;
        UpdateMarkVisual(slot);
    }

    private void Recycle(Slot slot)
    {
        TeardownVideo(slot);
        slot.Index = int.MinValue;
        slot.Item = null;
        slot.Image.Source = null;
        slot.LoadedWidth = 0;
        slot.Visual.StopAnimation("Opacity");
        slot.Visual.Opacity = 0;
        if (!_pool.Contains(slot)) _pool.Add(slot);
    }

    private static void UpdateMarkVisual(Slot slot)
    {
        bool marked = slot.Item?.IsMarked == true;
        slot.MarkBadge.Opacity = marked ? 1 : 0;
        slot.Frame.BorderBrush = marked ? MarkedBorderBrush : NormalBorderBrush;
        slot.Frame.BorderThickness = new Thickness(marked ? 3 : 1);
    }

    // ---- layout ----------------------------------------------------------

    private void Relayout(bool animate)
    {
        EnsureInitialised();
        if (_compositor is null) return;

        double w = ActualWidth, h = ActualHeight;
        if (w < 32 || h < 32) return;

        double top = _fillMode ? 0 : ContentTopInset;
        double bottom = _fillMode ? 0 : ContentBottomInset;
        double contentHeight = Math.Max(80, h - top - bottom);
        double contentCenterY = top + (contentHeight / 2);

        // Cell size is deliberately mode independent so the element size never changes.
        double normalHeight = Math.Max(80, h - ContentTopInset - ContentBottomInset);
        double cellWidth = w * CellWidthFraction;
        double cellHeight = normalHeight * CellHeightFraction;

        UpdatePerspective(w / 2, contentCenterY);

        // Size every slot first, so the centre width is known before placing the wings.
        foreach (var slot in _active.Values)
        {
            // Pick up an aspect ratio that arrived with a thumbnail after the slot was filled.
            if (slot.Item is not null) slot.Aspect = slot.Item.AspectRatio;

            var (sw, sh) = Fit(slot.Aspect, cellWidth, cellHeight);
            if (Math.Abs(slot.Width - sw) > 0.5 || Math.Abs(slot.Height - sh) > 0.5)
            {
                slot.Width = sw;
                slot.Height = sh;
                slot.Root.Width = sw;
                slot.Root.Height = sh;
            }
            slot.Visual.CenterPoint = new Vector3((float)(sw / 2), (float)(sh / 2), 0);
        }

        double centreWidth = cellWidth;
        if (_active.TryGetValue(_currentIndex, out var centre)) centreWidth = centre.Width;

        double overlap = w * OverlapFraction;
        var duration = _fillMode || WasFillTransition ? ModeDuration : TransitionDuration;
        WasFillTransition = false;

        foreach (var slot in _active.Values)
        {
            int r = slot.Index - _currentIndex;
            int magnitude = Math.Abs(r);
            int sign = Math.Sign(r);

            double tx, ty, scale, angle, opacity;

            if (_fillMode)
            {
                // "Fit to window" per the spec: cover the window, i.e. the larger of the
                // two ratios, so no letterboxing is visible.
                double cover = Math.Max(w / Math.Max(1, slot.Width), h / Math.Max(1, slot.Height));
                tx = 0;
                ty = 0;
                scale = cover;
                angle = 0;
                opacity = magnitude == 0 ? 1 : 0;
                slot.Frame.CornerRadius = new CornerRadius(0);
                slot.Frame.BorderThickness = new Thickness(0);
            }
            else
            {
                ty = contentCenterY - (h / 2);
                slot.Frame.CornerRadius = new CornerRadius(10);

                if (magnitude == 0)
                {
                    tx = 0;
                    scale = 1;
                    angle = 0;
                    opacity = 1;
                }
                else
                {
                    scale = magnitude == 1 ? SideScale : FarScale;

                    // Where a neighbour sits, measured from the centre of the control.
                    //
                    // The obvious rule - "the centre's half width, plus my own, less a bit
                    // of overlap" - silently loses tall, narrow neighbours: their own half
                    // width is tiny, so subtracting the overlap puts their outer edge
                    // *inside* the centre image, which is drawn on top of them. A 0.12
                    // aspect neighbour beside a square centre ended up 26px short of
                    // showing at all.
                    //
                    // So the placement is stated as a guarantee instead: at least `reveal`
                    // pixels of the neighbour must stick out past the centre's edge, where
                    // `reveal` is the whole neighbour when it is narrower than that. Wide
                    // neighbours are unaffected - for them the original rule already
                    // reveals far more than the minimum.
                    double sideHalf = slot.Width * SideScale / 2;
                    double firstRing = (centreWidth / 2) + sideHalf - overlap;

                    double reveal = Math.Min(slot.Width * SideScale, w * MinRevealFraction);
                    double revealRing = (centreWidth / 2) + reveal - sideHalf;
                    double edgeRing = (w / 2) - sideHalf - 8;   // but never push it off-screen
                    firstRing = Math.Max(firstRing, Math.Min(revealRing, edgeRing));

                    tx = sign * (magnitude == 1
                        ? firstRing
                        : firstRing + ((magnitude - 1) * slot.Width * SideScale * 0.85));
                    // Left neighbour turns its left edge towards the viewer, right neighbour
                    // its right edge. Flip the sign here if your taste differs.
                    angle = -sign * SideAngleDegrees;
                    opacity = magnitude == 1 ? SideOpacity : 0;
                }

                UpdateMarkVisual(slot);
            }

            Canvas.SetZIndex(slot.Root, 100 - magnitude);

            bool animateThis = animate && !slot.NeedsSnap;
            slot.NeedsSnap = false;
            ApplyTransform(slot, tx, ty, scale, angle, opacity, animateThis, duration);
        }

        UpdateVideoSlots();

        if (_fillMode) ScheduleKenBurns();
    }

    private bool WasFillTransition { get; set; }

    private void ApplyTransform(Slot slot, double tx, double ty, double scale, double angle,
                                double opacity, bool animate, TimeSpan duration)
    {
        var visual = slot.Visual;
        var translation = new Vector3((float)tx, (float)ty, 0);
        var scaleVector = new Vector3((float)scale, (float)scale, 1);

        if (!animate)
        {
            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");
            visual.StopAnimation("RotationAngleInDegrees");
            visual.StopAnimation("Opacity");

            visual.Properties.InsertVector3("Translation", translation);
            visual.Scale = scaleVector;
            visual.RotationAngleInDegrees = (float)angle;
            visual.Opacity = (float)opacity;
            return;
        }

        var compositor = _compositor!;

        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertExpressionKeyFrame(0f, "this.StartingValue");
        move.InsertKeyFrame(1f, translation, _ease!);
        move.Duration = duration;
        visual.StartAnimation("Translation", move);

        var zoom = compositor.CreateVector3KeyFrameAnimation();
        zoom.InsertExpressionKeyFrame(0f, "this.StartingValue");
        zoom.InsertKeyFrame(1f, scaleVector, _ease!);
        zoom.Duration = duration;
        visual.StartAnimation("Scale", zoom);

        var spin = compositor.CreateScalarKeyFrameAnimation();
        spin.InsertExpressionKeyFrame(0f, "this.StartingValue");
        spin.InsertKeyFrame(1f, (float)angle, _ease!);
        spin.Duration = duration;
        visual.StartAnimation("RotationAngleInDegrees", spin);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertExpressionKeyFrame(0f, "this.StartingValue");
        fade.InsertKeyFrame(1f, (float)opacity, _ease!);
        fade.Duration = duration;
        visual.StartAnimation("Opacity", fade);
    }

    private void UpdatePerspective(double centerX, double centerY)
    {
        var hostVisual = ElementCompositionPreview.GetElementVisual(_stage);

        var perspective = Matrix4x4.Identity;
        perspective.M34 = -1f / PerspectiveDistance;

        hostVisual.TransformMatrix =
            Matrix4x4.CreateTranslation((float)-centerX, (float)-centerY, 0) *
            perspective *
            Matrix4x4.CreateTranslation((float)centerX, (float)centerY, 0);
    }

    private static (double Width, double Height) Fit(double aspect, double maxWidth, double maxHeight)
    {
        if (aspect <= 0 || double.IsNaN(aspect)) aspect = 1.5;
        double width = maxWidth;
        double height = width / aspect;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * aspect;
        }
        return (Math.Max(24, width), Math.Max(24, height));
    }

    // ---- Ken Burns -------------------------------------------------------

    private DispatcherQueueTimer? _kenBurnsTimer;

    private void ScheduleKenBurns()
    {
        _kenBurnsTimer ??= DispatcherQueue.CreateTimer();
        _kenBurnsTimer.Stop();
        _kenBurnsTimer.Interval = ModeDuration + TimeSpan.FromMilliseconds(40);
        _kenBurnsTimer.IsRepeating = false;
        _kenBurnsTimer.Tick -= OnKenBurnsTick;
        _kenBurnsTimer.Tick += OnKenBurnsTick;
        _kenBurnsTimer.Start();
    }

    private void OnKenBurnsTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_fillMode || _compositor is null) return;
        if (!_active.TryGetValue(_currentIndex, out var slot)) return;
        if (slot.Item?.IsVideo == true) return;    // let video play untouched

        double w = ActualWidth, h = ActualHeight;
        double cover = Math.Max(w / Math.Max(1, slot.Width), h / Math.Max(1, slot.Height));

        float from = (float)cover;
        float to = (float)(cover * KenBurnsZoom);

        var zoom = _compositor.CreateVector3KeyFrameAnimation();
        zoom.InsertKeyFrame(0f, new Vector3(from, from, 1), _linear!);
        zoom.InsertKeyFrame(1f, new Vector3(to, to, 1), _linear!);
        zoom.Duration = KenBurnsPeriod;
        zoom.IterationBehavior = AnimationIterationBehavior.Forever;
        zoom.Direction = AnimationDirection.Alternate;
        slot.Visual.StartAnimation("Scale", zoom);

        float dx = (float)(w * KenBurnsDrift);
        float dy = (float)(h * KenBurnsDrift * 0.6);

        var pan = _compositor.CreateVector3KeyFrameAnimation();
        pan.InsertKeyFrame(0f, Vector3.Zero, _linear!);
        pan.InsertKeyFrame(1f, new Vector3(dx, dy, 0), _linear!);
        pan.Duration = KenBurnsPeriod;
        pan.IterationBehavior = AnimationIterationBehavior.Forever;
        pan.Direction = AnimationDirection.Alternate;
        slot.Visual.StartAnimation("Translation", pan);

        slot.KenBurnsRunning = true;
    }

    private void StopKenBurns()
    {
        _kenBurnsTimer?.Stop();
        WasFillTransition = true;

        foreach (var slot in _active.Values)
        {
            if (!slot.KenBurnsRunning) continue;
            slot.Visual.StopAnimation("Scale");
            slot.Visual.StopAnimation("Translation");
            slot.KenBurnsRunning = false;
        }
    }

    // ---- pictures --------------------------------------------------------

    /// <summary>
    /// Decodes a real bitmap for every realised slot, nearest first.
    ///
    /// Every slot, not just the visible ones: a slot only becomes a neighbour at the
    /// moment it slides into view, so requesting the decode then means watching the
    /// 256px thumbnail sharpen after the fact - and if you keep pressing an arrow key,
    /// never seeing anything but thumbnails. The off-screen buffer slots are staged
    /// instead, so a picture is already decoded by the time it appears.
    ///
    /// And at <c>slot.Width</c>, which is the width the picture would have *as the
    /// centre*, not the 72% it is drawn at while a neighbour. That costs nothing extra
    /// (the decode is bounded by the source's own size) and means promoting a neighbour
    /// to the centre needs no second decode at all.
    /// </summary>
    private void RequestPictures()
    {
        if (_items.Count == 0 || ActualWidth < 32) return;

        double rasterScale = XamlRoot?.RasterizationScale ?? 1.0;

        // Nearest first, so the centre is queued ahead of the buffer slots.
        foreach (var slot in _active.Values.OrderBy(s => Math.Abs(s.Index - _currentIndex)))
        {
            int magnitude = Math.Abs(slot.Index - _currentIndex);
            if (slot.Item is null) continue;

            double displayWidth = _fillMode && magnitude == 0
                ? ActualWidth
                : slot.Width;
            int target = (int)Math.Ceiling(displayWidth * rasterScale);
            if (target <= slot.LoadedWidth) continue;

            _ = LoadPictureAsync(slot, slot.Item, target);
        }
    }

    private async Task LoadPictureAsync(Slot slot, MediaItem item, int targetWidth)
    {
        try
        {
            var bitmap = await _loader.LoadAsync(item, targetWidth);
            if (bitmap is null) return;

            // The slot may have been recycled onto a different file while we were decoding.
            if (!ReferenceEquals(slot.Item, item)) return;

            slot.Image.Source = bitmap;
            slot.LoadedWidth = targetWidth;

            if (Math.Abs(slot.Aspect - item.AspectRatio) > 0.005)
            {
                slot.Aspect = item.AspectRatio;
                Relayout(true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Carousel] {item.FileName}: {ex.Message}");
        }
    }

    // ---- video -----------------------------------------------------------

    private void UpdateVideoSlots()
    {
        foreach (var slot in _active.Values)
        {
            bool shouldPlay = slot.Index == _currentIndex && slot.Item?.IsVideo == true;
            if (shouldPlay) EnsureVideo(slot);
            else TeardownVideo(slot);
        }
    }

    private void EnsureVideo(Slot slot)
    {
        if (slot.Player is not null || slot.Item is null) return;

        var player = new MediaPlayer
        {
            IsMuted = true,
            Volume = 0,
            IsLoopingEnabled = true,
            AutoPlay = true
        };

        var element = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        element.SetMediaPlayer(player);

        slot.MediaHost.Children.Add(element);
        slot.Player = player;
        slot.PlayerElement = element;

        BuildScrubber(slot);

        string path = slot.Item.Path;
        _ = SetVideoSourceAsync(player, path);

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => UpdateScrubber(slot);
        timer.Start();
        slot.Ticker = timer;
    }

    private static async Task SetVideoSourceAsync(MediaPlayer player, string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            player.Source = MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Carousel] video source: {ex.Message}");
        }
    }

    private void BuildScrubber(Slot slot)
    {
        var playPause = new Button
        {
            Content = new FontIcon { Glyph = GlyphPause, FontSize = 12 },
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.05,
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 80
        };

        var time = new TextBlock
        {
            Text = "0:00 / 0:00",
            FontSize = 12,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        var bar = new Grid { ColumnSpacing = 4, Padding = new Thickness(6, 2, 6, 2) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(playPause, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(time, 2);
        bar.Children.Add(playPause);
        bar.Children.Add(slider);
        bar.Children.Add(time);

        var host = new Border
        {
            Background = ScrubberBackgroundBrush,
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = bar
        };

        slider.ValueChanged += (_, e) =>
        {
            if (slot.SuppressScrub) return;
            var session = slot.Player?.PlaybackSession;
            if (session is null) return;
            try { session.Position = TimeSpan.FromSeconds(e.NewValue); } catch { }
        };

        playPause.Click += (_, _) =>
        {
            var player = slot.Player;
            if (player is null) return;
            var session = player.PlaybackSession;
            if (session.PlaybackState == MediaPlaybackState.Playing)
            {
                player.Pause();
                playPause.Content = new FontIcon { Glyph = GlyphPlay, FontSize = 12 };
            }
            else
            {
                player.Play();
                playPause.Content = new FontIcon { Glyph = GlyphPause, FontSize = 12 };
            }
        };

        slot.Overlay.Children.Add(host);
        slot.Scrubber = host;
        slot.ScrubSlider = slider;
        slot.TimeText = time;
    }

    private static void UpdateScrubber(Slot slot)
    {
        var session = slot.Player?.PlaybackSession;
        if (session is null || slot.ScrubSlider is null || slot.TimeText is null) return;

        try
        {
            var duration = session.NaturalDuration;
            if (duration <= TimeSpan.Zero) return;

            var position = session.Position;
            slot.SuppressScrub = true;
            slot.ScrubSlider.Maximum = duration.TotalSeconds;
            slot.ScrubSlider.Value = Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds);
            slot.SuppressScrub = false;

            slot.TimeText.Text = $"{Format(position)} / {Format(duration)}";
        }
        catch
        {
            slot.SuppressScrub = false;
        }
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private static void TeardownVideo(Slot slot)
    {
        if (slot.Ticker is not null)
        {
            slot.Ticker.Stop();
            slot.Ticker = null;
        }

        if (slot.Scrubber is not null)
        {
            slot.Overlay.Children.Remove(slot.Scrubber);
            slot.Scrubber = null;
            slot.ScrubSlider = null;
            slot.TimeText = null;
        }

        if (slot.PlayerElement is not null)
        {
            slot.PlayerElement.SetMediaPlayer(null);
            slot.MediaHost.Children.Remove(slot.PlayerElement);
            slot.PlayerElement = null;
        }

        if (slot.Player is not null)
        {
            try
            {
                slot.Player.Pause();
                slot.Player.Source = null;
                slot.Player.Dispose();
            }
            catch { }
            slot.Player = null;
        }
    }

    // ---- input -----------------------------------------------------------

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        NavigationRequested?.Invoke(this, delta > 0 ? -1 : 1);
    }

    private void OnSlotTapped(Slot slot)
    {
        if (slot.Index == _currentIndex) CenterActivated?.Invoke(this, EventArgs.Empty);
        else if (slot.Index > int.MinValue) NavigationRequested?.Invoke(this, slot.Index - _currentIndex);
    }

    private void OnSlotRightTapped(Slot slot)
    {
        if (slot.Index == _currentIndex) MarkToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---- slot record -----------------------------------------------------

    private sealed class Slot
    {
        public Grid Root = null!;
        public Border Frame = null!;
        public Image Image = null!;
        public Grid MediaHost = null!;
        public Grid Overlay = null!;
        public Border MarkBadge = null!;
        public Visual Visual = null!;

        public MediaItem? Item;
        public int Index = int.MinValue;
        public double Aspect = 1.5;
        public double Width;
        public double Height;
        public int LoadedWidth;
        public bool NeedsSnap = true;
        public bool KenBurnsRunning;

        public MediaPlayer? Player;
        public MediaPlayerElement? PlayerElement;
        public Border? Scrubber;
        public Slider? ScrubSlider;
        public TextBlock? TimeText;
        public DispatcherQueueTimer? Ticker;
        public bool SuppressScrub;
    }
}
