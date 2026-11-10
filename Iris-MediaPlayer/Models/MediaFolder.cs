namespace Iris_MediaPlayer.Models;

public sealed record MediaFolder(string Path, string Name, int MediaCount)
{
    public string MediaCountLabel => $"{MediaCount:N0} video{(MediaCount == 1 ? "" : "s")}";
}
