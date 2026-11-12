using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.Activation;

namespace Iris_MediaPlayer;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opened with video files (from Explorer, "Open with" or as the default app): show only
    /// the standalone player. Launched normally: show the full library.
    /// </summary>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var files = GetFilesToOpen();
        _window = files.Count > 0 ? new PlayerWindow(files) : new MainWindow();
        _window.Activate();
    }

    private static List<string> GetFilesToOpen()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.File && activation.Data is IFileActivatedEventArgs fileArgs)
        {
            return fileArgs.Files
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrEmpty(path) && MediaLibraryService.IsSupportedVideoFile(path))
                .ToList();
        }

        // Also accept paths on the command line, e.g. "Iris-MediaPlayer.exe movie.mkv".
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(path => MediaLibraryService.IsSupportedVideoFile(path) && File.Exists(path))
            .ToList();
    }
}
