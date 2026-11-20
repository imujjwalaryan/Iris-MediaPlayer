using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;

namespace Iris_MediaPlayer.Models;

/// <summary>
/// What a media-server card shows (Jellyfin films and episodes, Stash scenes, performers, studios…).
/// Poster cards use <see cref="DisplayName"/> and <see cref="Caption"/>; wide cards use
/// <see cref="RowTitle"/> and <see cref="RowCaption"/>.
/// </summary>
public interface IMediaCardItem : INotifyPropertyChanged
{
    ImageSource? Image { get; }
    string Glyph { get; }
    string DisplayName { get; }
    string Caption { get; }
    string RowTitle { get; }
    string RowCaption { get; }
    bool IsPlayable { get; }
    bool IsPlayed { get; }
    double Progress { get; }
    Visibility ProgressVisibility { get; }
    string UnplayedText { get; }
    Visibility UnplayedVisibility { get; }
}
