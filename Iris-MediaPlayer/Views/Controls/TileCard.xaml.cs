using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;

namespace Iris_MediaPlayer.Views.Controls;

/// <summary>
/// Folder or collection card: artwork from up to four of its videos, then name and detail.
/// DataContext must be a <see cref="LibraryTile"/>.
/// </summary>
public sealed partial class TileCard : UserControl
{
    private static readonly TimeSpan FanDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan MosaicZoomDuration = TimeSpan.FromMilliseconds(600);

    // Rest and hover poses of the three stacked prints: rotation (degrees), offset and scale.
    private static readonly (float Angle, Vector3 Offset, float Scale)[] RestPose =
        [(-7, new(-14, 6, 0), 1), (6, new(14, 6, 0), 1), (0, Vector3.Zero, 1)];
    private static readonly (float Angle, Vector3 Offset, float Scale)[] HoverPose =
        [(-12, new(-44, 10, 0), 1), (11, new(44, 10, 0), 1), (0, new(0, -6, 0), 1.04f)];

    private readonly List<MediaItem> _watched = [];
    private LibraryTile? _tile;
    private bool _isHovered;
    private bool _isFolder;

    public TileCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as LibraryTile);
    }

    private Border[] Prints => [Print1, Print2, Print3];
    private Image[] PrintImages => [Print1Image, Print2Image, Print3Image];
    private Image[] MosaicImages => [Mosaic1, Mosaic2, Mosaic3, Mosaic4];

    private void Attach(LibraryTile? tile)
    {
        _tile = tile;
        _isFolder = tile?.Source is MediaFolder;
        NameText.Text = tile?.Name ?? string.Empty;
        DetailText.Text = tile?.Detail ?? string.Empty;
        TileGlyph.Glyph = tile?.Glyph ?? "";
        EmptyGlyph.Glyph = tile?.Glyph ?? "";
        GlyphChip.Visibility = _isFolder ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(this, tile?.Name);

        // Thumbnails may still be loading, so follow the preview items.
        foreach (var media in _watched)
            media.PropertyChanged -= Preview_PropertyChanged;
        _watched.Clear();
        if (tile is not null)
        {
            _watched.AddRange(tile.Previews.Take(4));
            foreach (var media in _watched)
                media.PropertyChanged += Preview_PropertyChanged;
        }

        UpdateArtwork();
        LayoutPrints();
    }

    private void Preview_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaItem.Thumbnail))
            UpdateArtwork();
    }

    private void UpdateArtwork()
    {
        var previews = _watched;
        EmptyArt.Visibility = previews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StackArt.Visibility = _isFolder && previews.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MosaicArt.Visibility = !_isFolder && previews.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_isFolder)
        {
            // The newest video is the print on top; older ones sit behind it.
            var front = previews.ElementAtOrDefault(0);
            var left = previews.ElementAtOrDefault(1);
            var right = previews.ElementAtOrDefault(2);
            Print3Image.Source = front?.Thumbnail;
            Print1Image.Source = left?.Thumbnail;
            Print2Image.Source = right?.Thumbnail;
            Print3.Visibility = front is null ? Visibility.Collapsed : Visibility.Visible;
            Print1.Visibility = left is null ? Visibility.Collapsed : Visibility.Visible;
            Print2.Visibility = right is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            var images = MosaicImages;
            for (var index = 0; index < images.Length; index++)
                images[index].Source = previews.Count == 0 ? null : previews[index % previews.Count].Thumbnail;
        }
    }

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;

        var printWidth = Math.Round(e.NewSize.Width * 0.7);
        var printHeight = Math.Round(printWidth * 9 / 16);
        foreach (var print in Prints)
        {
            print.Width = printWidth;
            print.Height = printHeight;
        }
        ArtArea.Height = Math.Max(150, printHeight + 44);
        DispatcherQueue.TryEnqueue(LayoutPrints);
    }

    private void LayoutPrints()
    {
        var pose = _isHovered ? HoverPose : RestPose;
        var prints = Prints;
        for (var index = 0; index < prints.Length; index++)
            Motion.RotateAndTranslate(prints[index], pose[index].Angle, pose[index].Offset, pose[index].Scale, FanDuration, animate: false);
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e) => SetHovered(true);

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Exits from the card's own children bubble up here; ignore them.
        if (!PointerHelper.IsInside(this, e))
            SetHovered(false);
    }

    private void SetHovered(bool hovered)
    {
        _isHovered = hovered;
        if (_tile is null)
            return;

        if (_isFolder)
        {
            var pose = hovered ? HoverPose : RestPose;
            var prints = Prints;
            for (var index = 0; index < prints.Length; index++)
                Motion.RotateAndTranslate(prints[index], pose[index].Angle, pose[index].Offset, pose[index].Scale, FanDuration);
        }
        else
        {
            foreach (var image in MosaicImages)
                Motion.Scale(image, hovered ? 1.05f : 1f, MosaicZoomDuration);
        }
    }
}
