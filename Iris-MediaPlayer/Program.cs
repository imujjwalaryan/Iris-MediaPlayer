using Iris_MediaPlayer.Services;
using System;

namespace Iris_MediaPlayer;

/// <summary>
/// Iris's own entry point (the XAML-generated Main is turned off in the project file).
/// Velopack must run first: when the installer, an update or the uninstaller starts Iris to run
/// its install hooks, Velopack handles that and exits before any window is created.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Installation.RunVelopackHooks();
        XamlGeneratedProgram.XamlGeneratedMain();
    }
}
