using Iris_MediaPlayer.Services.Jellyfin;
using Iris_MediaPlayer.Services.Stash;
using System;

namespace Iris_MediaPlayer.Services;

/// <summary>The kinds of media server Iris can browse. Plex is listed but not built yet.</summary>
public enum MediaServerKind { None, Jellyfin, Stash, Plex }

/// <summary>
/// Which media server the Servers section shows. Each server keeps its own sign-in
/// (<see cref="JellyfinSession"/>, <see cref="StashSession"/>); this only remembers which one is in
/// front and asks the section to open the "add" flow for a kind. Use on the UI thread.
/// </summary>
internal static class MediaServers
{
    private const string ActiveKey = "ActiveMediaServer";

    /// <summary>Raised when the server in front changes, or a server is added or removed.</summary>
    public static event Action? Changed;

    /// <summary>Raised when the person picks Add server › kind.</summary>
    public static event Action<MediaServerKind>? AddRequested;

    public static bool IsConnected(MediaServerKind kind) => kind switch
    {
        MediaServerKind.Jellyfin => JellyfinSession.IsSignedIn,
        MediaServerKind.Stash => StashSession.IsConnected,
        _ => false
    };

    /// <summary>
    /// The server shown: the saved choice when it's still connected, otherwise any connected server,
    /// otherwise None (the section then asks which server to add).
    /// </summary>
    public static MediaServerKind Active
    {
        get
        {
            if (Enum.TryParse<MediaServerKind>(AppStorage.GetString(ActiveKey), out var saved) && IsConnected(saved))
                return saved;
            if (IsConnected(MediaServerKind.Jellyfin))
                return MediaServerKind.Jellyfin;
            return IsConnected(MediaServerKind.Stash) ? MediaServerKind.Stash : MediaServerKind.None;
        }
        set
        {
            AppStorage.SetString(ActiveKey, value.ToString());
            Changed?.Invoke();
        }
    }

    /// <summary>Opens the connect flow for <paramref name="kind"/>.</summary>
    public static void RequestAdd(MediaServerKind kind) => AddRequested?.Invoke(kind);

    /// <summary>Called by the sessions when a server is connected or removed.</summary>
    public static void NotifyChanged() => Changed?.Invoke();

    public static string DisplayName(MediaServerKind kind) => kind switch
    {
        MediaServerKind.Jellyfin => JellyfinSession.ServerName ?? "Jellyfin",
        MediaServerKind.Stash => StashSession.ServerName,
        MediaServerKind.Plex => "Plex",
        _ => "Media servers"
    };
}
