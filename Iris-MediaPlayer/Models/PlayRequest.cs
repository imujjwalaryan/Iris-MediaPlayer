using System.Collections.Generic;

namespace Iris_MediaPlayer.Models;

/// <summary>
/// A request to play <paramref name="Media"/>. <paramref name="Queue"/> is the list that
/// next/previous moves through; when null, the player uses the other videos in the same folder.
/// </summary>
public sealed record PlayRequest(MediaItem Media, IReadOnlyList<MediaItem>? Queue = null);
