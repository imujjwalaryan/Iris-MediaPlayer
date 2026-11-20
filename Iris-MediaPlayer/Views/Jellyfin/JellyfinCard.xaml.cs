using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;

namespace Iris_MediaPlayer.Views.Jellyfin;

/// <summary>
/// The card for a media-server item (Jellyfin, Stash), in three shapes: "Poster" (2:3), "Wide" (16:9)
/// and "Logo" (16:9, picture fitted rather than cropped, for studio logos). It follows its item's
/// changes (watched, progress, picture) and zooms its artwork on hover like the local cards.
/// </summary>
public sealed partial class JellyfinCard : UserControl
{
    private static readonly TimeSpan ArtworkZoomDuration = TimeSpan.FromMilliseconds(600);
    private IMediaCardItem? _item;

    public JellyfinCard()
    {
        InitializeComponent();
        Motion.SetOpacity(PlayHint, 0);
        Loaded += (_, _) => Motion.SetScale(PlayHintButton, 0.8f);
        DataContextChanged += (_, _) => Attach(DataContext as IMediaCardItem);
        Unloaded += (_, _) => Attach(null);
    }

    /// <summary>"Poster" (default), "Wide" or "Logo".</summary>
    public string Kind { get; set; } = "Poster";

    private bool IsLogo => string.Equals(Kind, "Logo", StringComparison.OrdinalIgnoreCase);
    private bool IsWide => IsLogo || string.Equals(Kind, "Wide", StringComparison.OrdinalIgnoreCase);

    private void Attach(IMediaCardItem? item)
    {
        if (_item is not null)
            _item.PropertyChanged -= Item_PropertyChanged;
        _item = item;
        if (_item is not null)
            _item.PropertyChanged += Item_PropertyChanged;
        Refresh();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        if (_item is not { } item)
            return;

        ArtImage.Source = item.Image;
        ArtImage.Stretch = IsLogo ? Microsoft.UI.Xaml.Media.Stretch.Uniform : Microsoft.UI.Xaml.Media.Stretch.UniformToFill;
        ArtImage.Margin = IsLogo ? new Thickness(24, 18, 24, 18) : new Thickness(0);
        PlaceholderGlyph.Glyph = item.Glyph;
        TitleText.Text = IsWide ? item.RowTitle : item.DisplayName;
        CaptionText.Text = IsWide ? item.RowCaption : item.Caption;
        PlayedBadge.Visibility = item.IsPlayed && item.UnplayedVisibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        UnplayedBadge.Visibility = item.UnplayedVisibility;
        UnplayedText.Text = item.UnplayedText;
        ProgressTrack.Visibility = item.ProgressVisibility;
        ProgressFill.Width = Art.ActualWidth * item.Progress;
        PlayHintGlyph.Glyph = item.IsPlayable ? "" : "";
    }

    private void Art_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5)
            return;
        Art.Height = Math.Round(e.NewSize.Width * (IsWide ? 9d / 16 : 1.5));
        if (_item is not null)
            ProgressFill.Width = e.NewSize.Width * _item.Progress;
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Motion.Scale(ArtImage, 1.06f, ArtworkZoomDuration);
        Motion.Fade(PlayHint, 1, Motion.Normal);
        Motion.Scale(PlayHintButton, 1f, Motion.Slow, decelerate: true);
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (PointerHelper.IsInside(this, e))
            return;
        Motion.Scale(ArtImage, 1f, ArtworkZoomDuration);
        Motion.Fade(PlayHint, 0, Motion.Normal);
        Motion.Scale(PlayHintButton, 0.8f, Motion.Slow, decelerate: true);
    }
}
