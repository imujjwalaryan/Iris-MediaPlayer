using System;
using Windows.Storage;

namespace Iris_MediaPlayer.Services;

/// <summary>Player preferences that persist between launches.</summary>
internal static class PlayerSettings
{
    private const string VolumeKey = "PlayerVolume";
    private const string AutoplayNextKey = "PlayerAutoplayNext";
    private const string ZoomModeKey = "PlayerZoomMode";

    public static double Volume
    {
        get => ApplicationData.Current.LocalSettings.Values[VolumeKey] is double volume
            ? Math.Clamp(volume, 0, 100)
            : 100;
        set => ApplicationData.Current.LocalSettings.Values[VolumeKey] = Math.Clamp(value, 0, 100);
    }

    /// <summary>Kept between videos and launches, like most players' "fill screen" setting.</summary>
    public static VideoZoomMode ZoomMode
    {
        get => ApplicationData.Current.LocalSettings.Values[ZoomModeKey] is string saved
               && Enum.TryParse<VideoZoomMode>(saved, out var mode)
            ? mode
            : VideoZoomMode.Fit;
        set => ApplicationData.Current.LocalSettings.Values[ZoomModeKey] = value.ToString();
    }

    public static bool AutoplayNext
    {
        get => ApplicationData.Current.LocalSettings.Values[AutoplayNextKey] is not bool enabled || enabled;
        set => ApplicationData.Current.LocalSettings.Values[AutoplayNextKey] = value;
    }
}
