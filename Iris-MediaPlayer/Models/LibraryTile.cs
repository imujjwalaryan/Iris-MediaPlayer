namespace Iris_MediaPlayer.Models;

/// <summary>
/// A folder or collection shown as a card. <see cref="Preview"/> supplies the thumbnail;
/// <see cref="Source"/> is the <see cref="MediaFolder"/> or <see cref="MediaCollection"/> it stands for.
/// </summary>
public sealed record LibraryTile(string Name, string Detail, string Glyph, MediaItem? Preview, object Source);
