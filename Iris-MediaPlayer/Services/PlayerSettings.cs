using System;

namespace Iris_MediaPlayer.Services;

/// <summary>Player preferences that persist between launches (stored by <see cref="AppStorage"/>).</summary>
internal static class PlayerSettings
{
    private const string VolumeKey = "PlayerVolume";
    private const string AutoplayNextKey = "PlayerAutoplayNext";
    private const string ZoomModeKey = "PlayerZoomMode";

    public static double Volume
    {
        get => AppStorage.GetDouble(VolumeKey) is { } volume ? Math.Clamp(volume, 0, 100) : 100;
        set => AppStorage.SetDouble(VolumeKey, Math.Clamp(value, 0, 100));
    }

    /// <summary>Kept between videos and launches, like most players' "fill screen" setting.</summary>
    public static VideoZoomMode ZoomMode
    {
        get => Enum.TryParse<VideoZoomMode>(AppStorage.GetString(ZoomModeKey), out var mode) ? mode : VideoZoomMode.Fit;
        set => AppStorage.SetString(ZoomModeKey, value.ToString());
    }

    public static bool AutoplayNext
    {
        get => AppStorage.GetBool(AutoplayNextKey) ?? true;
        set => AppStorage.SetBool(AutoplayNextKey, value);
    }
}
