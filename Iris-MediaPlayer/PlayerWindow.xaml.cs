using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Iris_MediaPlayer;

/// <summary>
/// A player-only window for files opened from Explorer. It shares the library's saved state,
/// so progress, resume and recently played carry over to the full app.
/// </summary>
public sealed partial class PlayerWindow : Window
{
    private readonly LibraryStore _store;
    private readonly MiniPlayerMode _miniPlayer = new();
    private bool _isFullscreen;

    public PlayerWindow(IReadOnlyList<string> filePaths)
    {
        InitializeComponent();
        WindowChrome.ExtendIntoTitleBar(this, AppTitleBar);
        WindowChrome.SizeAndCenter(this, 1280, 760);

        // The store is only used for resume, progress and recently played; the library is never scanned here.
        _store = new LibraryStore(DispatcherQueue);

        Player.Initialize(_store);
        Player.IsStandalone = true;
        Player.FullscreenToggleRequested += (_, _) => SetFullscreen(!_isFullscreen);
        Player.MiniPlayerToggleRequested += (_, _) => SetMiniPlayer(!_miniPlayer.IsActive);
        Player.MiniPlayerCloseRequested += (_, _) => Close();
        Player.MediaChanged += (_, media) =>
        {
            Title = $"{media.Title} – Iris";
            TitleBarText.Text = media.Title;
        };

        Closed += (_, _) =>
        {
            Player.Shutdown();
            _store.Dispose();
        };

        PlayFiles(filePaths);
    }

    /// <summary>
    /// Plays the first file. Several files (a multi-select in Explorer) become the queue;
    /// a single file queues the rest of its folder, so next/previous work like in VLC.
    /// </summary>
    private void PlayFiles(IReadOnlyList<string> filePaths)
    {
        var media = filePaths.Select(_store.OpenFile).OfType<MediaItem>().ToList();
        if (media.Count == 0)
        {
            Player.ShowStatusMessage("Iris couldn’t open that file.");
            return;
        }

        var queue = media.Count > 1 ? media : _store.GetFolderQueue(media[0]);
        Player.Play(media[0], queue);
    }

    private void SetFullscreen(bool enabled)
    {
        if (_isFullscreen == enabled)
            return;
        if (enabled && _miniPlayer.IsActive)
            SetMiniPlayer(false);

        _isFullscreen = enabled;
        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        SetTitleBarVisible(!enabled);
        Player.SetFullscreen(enabled);
    }

    private void SetMiniPlayer(bool enabled)
    {
        if (_miniPlayer.IsActive == enabled)
            return;

        if (enabled)
        {
            if (_isFullscreen)
                SetFullscreen(false);
            _miniPlayer.Enter(AppWindow, Content.XamlRoot?.RasterizationScale ?? 1);
        }
        else
        {
            _miniPlayer.Exit(AppWindow);
        }

        SetTitleBarVisible(!enabled);
        Player.SetMiniMode(enabled);
    }

    private void SetTitleBarVisible(bool visible)
    {
        TitleBarRow.Height = visible ? new GridLength((double)Application.Current.Resources["IrisTitleBarHeight"]) : new GridLength(0);
        AppTitleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // Drop a video to play it, or a subtitle file to add it to the current video.

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        try
        {
            var paths = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().Select(file => file.Path).ToList();

            var subtitle = paths.FirstOrDefault(MediaLibraryService.IsSupportedSubtitleFile);
            if (subtitle is not null)
            {
                Player.AddSubtitle(subtitle);
                return;
            }

            var videos = paths.Where(MediaLibraryService.IsSupportedVideoFile).ToList();
            if (videos.Count > 0)
                PlayFiles(videos);
        }
        catch (Exception exception)
        {
            Player.ShowStatusMessage($"Could not open the dropped file: {exception.Message}");
        }
    }
}
