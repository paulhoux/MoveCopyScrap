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
            }
        }
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
