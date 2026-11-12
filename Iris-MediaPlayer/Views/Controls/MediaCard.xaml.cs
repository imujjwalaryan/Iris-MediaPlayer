using Iris_MediaPlayer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace Iris_MediaPlayer.Views.Controls;

/// <summary>
/// Video card: 16:9 thumbnail with quality and duration badges, an optional watch-progress bar,
/// then title and one line of detail. DataContext must be a <see cref="MediaItem"/>.
/// </summary>
public sealed partial class MediaCard : UserControl
{
    private MediaItem? _media;

    public MediaCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MediaItem);
    }

    /// <summary>When true, the detail line shows time left instead of folder and format.</summary>
    public bool ShowTimeRemaining { get; set; }

    private void Attach(MediaItem? media)
    {
        if (ReferenceEquals(media, _media))
            return;

        if (_media is not null)
            _media.PropertyChanged -= Media_PropertyChanged;
        _media = media;
        if (_media is not null)
            _media.PropertyChanged += Media_PropertyChanged;

        Update();
    }

    private void Media_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var media = _media;
        ThumbnailImage.Source = media?.Thumbnail;
        TitleText.Text = media?.Title ?? string.Empty;
        ToolTipService.SetToolTip(this, media?.Title);

        var hasProgress = media is { WatchProgress: > 0 };
        WatchBar.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
        WatchBar.Value = media?.WatchProgress ?? 0;
        DetailText.Text = ShowTimeRemaining && hasProgress ? media!.WatchRemaining : media?.Subtitle ?? string.Empty;

        QualityText.Text = media?.QualityLabel ?? string.Empty;
        QualityBadge.Visibility = string.IsNullOrEmpty(QualityText.Text) ? Visibility.Collapsed : Visibility.Visible;
        DurationText.Text = media?.DurationLabel ?? string.Empty;
        // The progress bar already tells the story on partly watched videos.
        DurationBadge.Visibility = string.IsNullOrEmpty(DurationText.Text) || hasProgress
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the thumbnail 16:9 at any card width.
        if (e.NewSize.Width > 0)
            ThumbnailFrame.Height = e.NewSize.Width * 9 / 16;
    }
}
