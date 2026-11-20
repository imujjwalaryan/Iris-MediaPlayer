using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services.Stash;
using Iris_MediaPlayer.Views.Jellyfin;
using System.Collections.Generic;
using System.Linq;

namespace Iris_MediaPlayer.Views.Stash;

/// <summary>Helpers the Stash pages share: card pictures and turning scenes into playable videos.</summary>
internal static class StashUi
{
    public const int SceneImageWidth = 640;
    public const int PosterHeight = 420;
    public const int FaceHeight = 220;

    /// <summary>Gives each item its card picture: the screenshot for scenes, the image for the rest.</summary>
    public static void LoadCardImages(StashClient client, IEnumerable<StashItem> items)
    {
        foreach (var item in items)
        {
            if (item.Image is not null)
                continue;
            item.Image = item.Kind switch
            {
                StashKind.Scene => JellyfinUi.Load(client.GetImageUrl(item.ScreenshotPath), decodeWidth: SceneImageWidth),
                StashKind.Performer => JellyfinUi.Load(client.GetImageUrl(item.ImagePath), decodeHeight: PosterHeight),
                _ => JellyfinUi.Load(client.GetImageUrl(item.ImagePath), decodeWidth: SceneImageWidth)
            };
        }
    }

    /// <summary>
    /// A scene the Iris player can open, reporting progress back to Stash. <paramref name="startAt"/>
    /// (a marker) starts there whatever the resume setting says.
    /// </summary>
    public static MediaItem ToMediaItem(StashClient client, StashItem scene, double? startAt = null)
    {
        var extension = System.IO.Path.GetExtension(scene.FilePath ?? string.Empty);
        return new MediaItem(Key(client, scene), scene.Name, string.Empty, scene.Studio?.Name ?? StashSession.ServerName,
            string.IsNullOrEmpty(extension) ? ".mp4" : extension, scene.CreatedAt ?? System.DateTime.MinValue)
        {
            StreamUrl = client.GetStreamUrl(scene.Id),
            ServerResumeSeconds = scene.ResumeSeconds,
            StartAtSeconds = startAt,
            Remote = new StashPlaybackReporter(client, scene.Id, scene.DurationSeconds),
            Thumbnail = scene.Image
        };
    }

    /// <summary>Playable queue from a list: every scene in it, starting from <paramref name="scene"/>.</summary>
    public static PlayRequest Request(StashClient client, StashItem scene, IEnumerable<StashItem> list, double? startAt = null)
    {
        var queue = list.Where(item => item.Kind == StashKind.Scene).Select(item => item.Id == scene.Id
            ? ToMediaItem(client, item, startAt) : ToMediaItem(client, item)).ToList();
        var media = queue.FirstOrDefault(entry => entry.FilePath == Key(client, scene)) ?? ToMediaItem(client, scene, startAt);
        return new PlayRequest(media, queue.Count > 0 ? queue : [media]);
    }

    public static string Key(StashClient client, StashItem scene) => $"stash://{new System.Uri(client.Address).Authority}/{scene.Id}";
}
