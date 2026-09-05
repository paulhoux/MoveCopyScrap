using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MoveCopyScrap.Models;

/// <summary>
/// One image or video in the current folder. Everything the filmstrip and the carousel
/// bind to lives here; property changes are raised on the UI thread by the callers.
/// </summary>
public sealed class MediaItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _isMarked;
    private bool _isCurrent;
    private double _aspectRatio = 3.0 / 2.0;
    private int _rotationSteps;
    private int _pendingDiskSteps;

    public MediaItem(string path, bool isVideo, long sizeBytes, DateTime lastWriteUtc)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        IsVideo = isVideo;
        SizeBytes = sizeBytes;
        LastWriteUtc = lastWriteUtc;
    }

    public string Path { get; private set; }

    public string FileName { get; private set; }

    public bool IsVideo { get; }

    public long SizeBytes { get; }

    public DateTime LastWriteUtc { get; }

    /// <summary>Width / height of the original media. Defaults to 3:2 until known.</summary>
    public double AspectRatio
    {
        get => _aspectRatio;
        set
        {
            if (value > 0.01 && value < 100 && Math.Abs(value - _aspectRatio) > 0.0001)
            {
                _aspectRatio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectiveAspectRatio));
            }
        }
    }

    // ---- rotation --------------------------------------------------------
    //
    // Two counters, because the picture on screen and the file on disk are only briefly
    // out of step but are genuinely different things:
    //
    //   RotationSteps    quarter turns to draw on top of the pixels we currently hold.
    //                    Non-zero only while a rotation is waiting to be written, and for
    //                    as long as it takes to re-decode the file afterwards.
    //   PendingDiskSteps quarter turns still owed to the file. Reaching zero (the user
    //                    turned the picture back to where it started) cancels the write.

    /// <summary>Quarter turns clockwise applied to the loaded pixels when drawing.</summary>
    public int RotationSteps => _rotationSteps;

    /// <summary>Quarter turns clockwise not yet written to the file.</summary>
    public int PendingDiskSteps => _pendingDiskSteps;

    public double RotationAngle => _rotationSteps * 90.0;

    /// <summary>
    /// The filmstrip's thumbnail turns with the picture. A fresh transform object each
    /// time rather than a mutated one, because x:Bind only notices a new value.
    /// </summary>
    public Transform ThumbnailTransform => new RotateTransform { Angle = RotationAngle };

    /// <summary>Width / height as the picture is currently *drawn*, rotation included.</summary>
    public double EffectiveAspectRatio =>
        (_rotationSteps & 1) != 0 && _aspectRatio > 0.0001 ? 1.0 / _aspectRatio : _aspectRatio;

    /// <summary>Turns the picture a quarter turn: +1 clockwise, -1 anticlockwise.</summary>
    public void Rotate(int quarterTurns)
    {
        _rotationSteps = Wrap(_rotationSteps + quarterTurns);
        _pendingDiskSteps = Wrap(_pendingDiskSteps + quarterTurns);
        RaiseRotationChanged();
    }

    /// <summary>
    /// Records that <paramref name="steps"/> quarter turns have reached the file.
    /// <paramref name="pixelsReloaded"/> says whether we have also re-decoded it: only
    /// then does the drawn rotation come off, because only then do the pixels we hold
    /// already carry it. Subtracting rather than zeroing keeps a rotation that arrived
    /// while the write was in flight.
    /// </summary>
    public void CommitRotation(int steps, bool pixelsReloaded)
    {
        _pendingDiskSteps = Wrap(_pendingDiskSteps - steps);
        if (pixelsReloaded) _rotationSteps = Wrap(_rotationSteps - steps);
        RaiseRotationChanged();
    }

    /// <summary>Gives up on writing the pending rotation, leaving the view as it is.</summary>
    public void AbandonPendingRotation()
    {
        if (_pendingDiskSteps == 0) return;
        _pendingDiskSteps = 0;
        OnPropertyChanged(nameof(PendingDiskSteps));
    }

    private static int Wrap(int steps) => ((steps % 4) + 4) % 4;

    private void RaiseRotationChanged()
    {
        OnPropertyChanged(nameof(RotationSteps));
        OnPropertyChanged(nameof(PendingDiskSteps));
        OnPropertyChanged(nameof(RotationAngle));
        OnPropertyChanged(nameof(ThumbnailTransform));
        OnPropertyChanged(nameof(EffectiveAspectRatio));
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { if (!ReferenceEquals(_thumbnail, value)) { _thumbnail = value; OnPropertyChanged(); } }
    }

    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked != value)
            {
                _isMarked = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MarkBadgeVisibility));
                OnPropertyChanged(nameof(MarkOverlayOpacity));
            }
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectionOpacity));
                OnPropertyChanged(nameof(UnselectedDim));
            }
        }
    }

    // Computed helpers so the filmstrip template needs no value converters.
    public Visibility MarkBadgeVisibility => IsMarked ? Visibility.Visible : Visibility.Collapsed;

    public double MarkOverlayOpacity => IsMarked ? 1.0 : 0.0;

    public double SelectionOpacity => IsCurrent ? 1.0 : 0.0;

    public double UnselectedDim => IsCurrent ? 1.0 : 0.62;

    public Visibility VideoBadgeVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Called after the file has been moved on disk.</summary>
    public void UpdatePath(string newPath)
    {
        Path = newPath;
        FileName = System.IO.Path.GetFileName(newPath);
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(FileName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
