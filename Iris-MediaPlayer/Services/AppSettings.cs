using System;

namespace Iris_MediaPlayer.Services;

/// <summary>How a video that was stopped part-way starts the next time.</summary>
internal enum ResumeMode
{
    /// <summary>Pick up where it stopped, with a "Start over" button for a few seconds.</summary>
    Always,
    /// <summary>Start from the beginning and offer a "Resume" button for a few seconds.</summary>
    Ask,
    /// <summary>Always start from the beginning.</summary>
    Never
}

/// <summary>
/// App-wide preferences that aren't about the player itself (library and Home). Like
/// <see cref="PlayerSettings"/> they live in settings.json through <see cref="AppStorage"/>.
/// Setters raise <see cref="Changed"/> so open pages (Home) can update without a restart.
/// </summary>
internal static class AppSettings
{
    private const string HideContinueWatchingKey = "HideContinueWatching";
    private const string WatchFoldersKey = "WatchLibraryFolders";
    private const string AutoUpdateKey = "AutoUpdate";

    /// <summary>Raised on the thread that changed a setting (the UI thread for the Settings page).</summary>
    public static event Action? Changed;

    /// <summary>Hides the Continue watching row on Home (progress is still remembered).</summary>
    public static bool HideContinueWatching
    {
        get => AppStorage.GetBool(HideContinueWatchingKey) ?? false;
        set => Set(HideContinueWatchingKey, value);
    }

    /// <summary>Rescans library folders on their own when files are added, removed or renamed.</summary>
    public static bool WatchFolders
    {
        get => AppStorage.GetBool(WatchFoldersKey) ?? true;
        set => Set(WatchFoldersKey, value);
    }

    /// <summary>Looks for and downloads new versions in the background after startup.</summary>
    public static bool AutoUpdate
    {
        get => AppStorage.GetBool(AutoUpdateKey) ?? true;
        set => Set(AutoUpdateKey, value);
    }

    private static void Set(string key, bool value)
    {
        AppStorage.SetBool(key, value);
        Changed?.Invoke();
    }
}
