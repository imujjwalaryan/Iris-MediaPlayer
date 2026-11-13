using System.Collections.Generic;
using System.Linq;

namespace Iris_MediaPlayer.Models;

/// <summary>
/// A folder or collection shown as a card. <see cref="Previews"/> supply the artwork (up to four);
/// <see cref="Source"/> is the <see cref="MediaFolder"/> or <see cref="MediaCollection"/> it stands for,
/// or <see cref="NewCollection"/> for the "New collection" card.
/// </summary>
public sealed record LibraryTile(string Name, string Detail, string Glyph, IReadOnlyList<MediaItem> Previews, object Source)
{
    /// <summary><see cref="Source"/> of the dashed "New collection" card at the end of the collections grid.</summary>
    public static readonly object NewCollection = new();

    public MediaItem? Preview => Previews.FirstOrDefault();

    public bool IsNewCollection => ReferenceEquals(Source, NewCollection);
}
