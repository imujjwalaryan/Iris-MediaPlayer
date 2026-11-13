using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.ComponentModel;

namespace Iris_MediaPlayer.Views.Controls;

/// <summary>
/// Video card: 16:9 thumbnail with quality and duration badges and an optional watch-progress bar,
/// then title and one line of detail. On hover the artwork zooms 6% and a play button fades in.
/// DataContext must be a <see cref="MediaItem"/>.
/// </summary>
public sealed partial class MediaCard : UserControl
{
    /// <summary>Key of the connected animation from a card's thumbnail into the player.</summary>
    public const string PlayerAnimationKey = "IrisCardToPlayer";

    private static readonly TimeSpan ArtworkZoomDuration = TimeSpan.FromMilliseconds(600);

    private MediaItem? _media;

    public MediaCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MediaItem);
        Motion.SetOpacity(PlayHint, 0);
        Loaded += (_, _) => Motion.SetScale(PlayHintButton, 0.8f);
    }

    /// <summary>When true, the detail line shows time left instead of folder and format.</summary>
    public bool ShowTimeRemaining { get; set; }

    /// <summary>Hands the thumbnail to the player so it can grow into the video (the "Card to player" motion).</summary>
    public void PrepareToPlay()
    {
        try
        {
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(PlayerAnimationKey, ThumbnailFrame);
        }
        catch (Exception)
        {
            // The animation is decoration; playback must start without it.
        }
    }

    /// <summary>Finds the card inside a clicked card button and prepares the animation from it.</summary>
    public static void PrepareToPlay(object? cardButton)
    {
        if (cardButton is ContentControl { Content: MediaCard card })
            card.PrepareToPlay();
    }

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
        UpdateWatchFill();
        DetailText.Text = media is null
            ? string.Empty
            : ShowTimeRemaining && hasProgress
                ? media.WatchRemaining
                : $"{media.FolderName}  ·  {media.FileFormat}";

        QualityText.Text = media?.QualityLabel ?? string.Empty;
        QualityBadge.Visibility = string.IsNullOrEmpty(QualityText.Text) ? Visibility.Collapsed : Visibility.Visible;
        DurationText.Text = media?.DurationLabel ?? string.Empty;
        // On partly watched videos the progress bar sits where the duration badge would.
        DurationBadge.Visibility = string.IsNullOrEmpty(DurationText.Text) || hasProgress
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateWatchFill()
        => WatchFill.Width = Math.Max(0, ThumbnailFrame.ActualWidth * (_media?.WatchProgress ?? 0));

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the thumbnail 16:9 at any card width.
        if (e.NewSize.Width > 0)
            ThumbnailFrame.Height = Math.Round(e.NewSize.Width * 9 / 16);
        UpdateWatchFill();
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Motion.Scale(ThumbnailImage, 1.06f, ArtworkZoomDuration);
        Motion.Fade(PlayHint, 1, Motion.Normal);
        Motion.Scale(PlayHintButton, 1f, Motion.Slow, decelerate: true);
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (PointerHelper.IsInside(this, e))
            return;
        Motion.Scale(ThumbnailImage, 1f, ArtworkZoomDuration);
        Motion.Fade(PlayHint, 0, Motion.Normal);
        Motion.Scale(PlayHintButton, 0.8f, Motion.Slow, decelerate: true);
    }
}
