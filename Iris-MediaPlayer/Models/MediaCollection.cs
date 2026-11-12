using System.Collections.Generic;

namespace Iris_MediaPlayer.Models;

public sealed class MediaCollection
{
    public string Name { get; set; } = string.Empty;
    public List<string> FilePaths { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string VideoCountLabel => $"{FilePaths.Count:N0} video{(FilePaths.Count == 1 ? "" : "s")}";
}
