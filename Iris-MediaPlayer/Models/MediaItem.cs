using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Iris_MediaPlayer.Models;

public sealed record MediaItem(
    string FilePath,
    string Title,
    string FolderPath,
    string FolderName,
    string FileExtension,
    System.DateTime LastModified) : INotifyPropertyChanged
{
    public string LibraryRootPath { get; init; } = FolderPath;

    /// <summary>
    /// For videos streamed from a Jellyfin server: the address mpv plays. Null for local files.
    /// Streamed videos use a "jellyfin://server/item" <see cref="FilePath"/> as their key.
    /// </summary>
    public string? StreamUrl { get; init; }

    /// <summary>Where the server says playback stopped last time, in seconds (streamed videos only).</summary>
    public double ServerResumeSeconds { get; init; }

    /// <summary>mpv audio track to start with (chosen on a Jellyfin details page); null keeps mpv's choice.</summary>
    public long? StartAudioTrack { get; init; }

    /// <summary>mpv subtitle track to start with; -1 starts with subtitles off, null keeps mpv's choice.</summary>
    public long? StartSubtitleTrack { get; init; }

    /// <summary>For server videos: reports playback back to the server (resume point, watched).</summary>
    public IRemotePlayback? Remote { get; init; }

    /// <summary>True for videos streamed from a server rather than files on this PC.</summary>
    public bool IsRemote => StreamUrl is not null;
    public string FileFormat => FileExtension.TrimStart('.').ToUpperInvariant();
    public string Subtitle => $"{FolderName}  ·  {FileFormat}  ·  {LastModified:d}";

    private string _resolution = "Reading resolution…";
    public string Resolution
    {
        get => _resolution;
        private set => SetProperty(ref _resolution, value);
    }

    private string _bitrate = "Reading bitrate…";
    public string Bitrate
    {
        get => _bitrate;
        private set => SetProperty(ref _bitrate, value);
    }

    private string _qualityLabel = string.Empty;
    /// <summary>Short badge such as "4K" or "1080p"; empty until the file has been read.</summary>
    public string QualityLabel
    {
        get => _qualityLabel;
        private set => SetProperty(ref _qualityLabel, value);
    }

    private string _durationLabel = string.Empty;
    /// <summary>Running time such as "1:42:05"; empty until the file has been read.</summary>
    public string DurationLabel
    {
        get => _durationLabel;
        private set => SetProperty(ref _durationLabel, value);
    }

    public void SetTechnicalDetails(uint width, uint height, uint bitrate, System.TimeSpan duration)
    {
        Resolution = width > 0 && height > 0 ? $"{width} × {height}" : "Resolution unavailable";
        Bitrate = bitrate > 0 ? FormatBitrate(bitrate) : "Bitrate unavailable";
        QualityLabel = GetQualityLabel(System.Math.Min(width, height));
        DurationLabel = duration > System.TimeSpan.Zero
            ? duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")
            : string.Empty;
    }

    private static string GetQualityLabel(uint shortSide) => shortSide switch
    {
        >= 2160 => "4K",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        >= 720 => "720p",
        > 0 => "SD",
        _ => string.Empty
    };

    private static string FormatBitrate(uint bitsPerSecond)
        => bitsPerSecond >= 1_000_000
            ? $"{bitsPerSecond / 1_000_000d:0.0} Mbps"
            : $"{bitsPerSecond / 1_000d:0} kbps";

    private double _watchProgress;
    /// <summary>How much of the video has been watched, from 0 to 1.</summary>
    public double WatchProgress
    {
        get => _watchProgress;
        private set => SetProperty(ref _watchProgress, value);
    }

    private string _watchRemaining = string.Empty;
    public string WatchRemaining
    {
        get => _watchRemaining;
        private set => SetProperty(ref _watchRemaining, value);
    }

    /// <summary>Back to unwatched: no progress bar, no time left.</summary>
    public void ClearWatchProgress()
    {
        WatchProgress = 0;
        WatchRemaining = string.Empty;
    }

    public void SetWatchProgress(double positionSeconds, double durationSeconds)
    {
        if (durationSeconds <= 0)
            return;

        WatchProgress = System.Math.Clamp(positionSeconds / durationSeconds, 0, 1);
        var left = System.TimeSpan.FromSeconds(System.Math.Max(0, durationSeconds - positionSeconds));
        WatchRemaining = left.TotalHours >= 1
            ? $"{(int)left.TotalHours} h {left.Minutes} min left"
            : left.TotalMinutes >= 1
                ? $"{(int)left.TotalMinutes} min left"
                : "Less than a minute left";
    }

    private Microsoft.UI.Xaml.Media.ImageSource? _thumbnail;
    public Microsoft.UI.Xaml.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
                return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
