using Iris_MediaPlayer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace Iris_MediaPlayer.Views.Controls;

/// <summary>
/// Folder or collection card: a dimmed thumbnail from one of its videos with an icon on top,
/// then name and detail. DataContext must be a <see cref="LibraryTile"/>.
/// </summary>
public sealed partial class TileCard : UserControl
{
    private LibraryTile? _tile;
    private MediaItem? _preview;

    public TileCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as LibraryTile);
    }

    private void Attach(LibraryTile? tile)
    {
        _tile = tile;
        NameText.Text = tile?.Name ?? string.Empty;
        DetailText.Text = tile?.Detail ?? string.Empty;
        TileGlyph.Glyph = tile?.Glyph ?? "";
        ToolTipService.SetToolTip(this, tile?.Name);

        // The preview's thumbnail may still be loading, so follow it.
        if (_preview is not null)
            _preview.PropertyChanged -= Preview_PropertyChanged;
        _preview = tile?.Preview;
        if (_preview is not null)
            _preview.PropertyChanged += Preview_PropertyChanged;
        PreviewImage.Source = _preview?.Thumbnail;
    }

    private void Preview_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaItem.Thumbnail))
            PreviewImage.Source = _preview?.Thumbnail;
    }

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
            ThumbnailFrame.Height = e.NewSize.Width * 9 / 16;
    }
}
