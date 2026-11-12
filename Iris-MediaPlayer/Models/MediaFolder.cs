namespace Iris_MediaPlayer.Models;

/// <param name="Preview">A video inside the folder, used as the folder card's thumbnail.</param>
public sealed record MediaFolder(string Path, string Name, int MediaCount, MediaItem? Preview = null)
{
    public string MediaCountLabel => $"{MediaCount:N0} video{(MediaCount == 1 ? "" : "s")}";
}
