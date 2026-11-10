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

    public void SetTechnicalDetails(uint width, uint height, uint bitrate)
    {
        Resolution = width > 0 && height > 0 ? $"{width} × {height}" : "Resolution unavailable";
        Bitrate = bitrate > 0 ? FormatBitrate(bitrate) : "Bitrate unavailable";
    }

    private static string FormatBitrate(uint bitsPerSecond)
        => bitsPerSecond >= 1_000_000
            ? $"{bitsPerSecond / 1_000_000d:0.0} Mbps"
            : $"{bitsPerSecond / 1_000d:0} kbps";

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
