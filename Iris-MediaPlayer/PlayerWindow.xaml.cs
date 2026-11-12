using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;

namespace Iris_MediaPlayer;

/// <summary>
/// A player-only window for files opened from Explorer. It shares the library's saved state,
/// so progress, resume and recently played carry over to the full app.
/// </summary>
public sealed partial class PlayerWindow : Window
{
    private readonly LibraryStore _store;
    private bool _isFullscreen;

    public PlayerWindow(IReadOnlyList<string> filePaths)
    {
        InitializeComponent();
        SizeAndCenter(1280, 760);

        // The store is only used for resume, progress and recently played; the library is never scanned here.
        _store = new LibraryStore(DispatcherQueue);

        Player.Initialize(_store);
        Player.IsStandalone = true;
        Player.ApplyFrameMode(squareCorners: true, overlayBars: false);
        Player.FullscreenToggleRequested += (_, _) => SetFullscreen(!_isFullscreen);
        Player.MediaChanged += (_, media) => Title = $"{media.Title} – Iris";

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

        _isFullscreen = enabled;
        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        Player.ApplyFrameMode(squareCorners: true, overlayBars: enabled);
        Player.SetFullscreen(enabled);
    }

    private void SizeAndCenter(int width, int height)
    {
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);
        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height));
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
