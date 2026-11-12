using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Linq;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Iris_MediaPlayer;

/// <summary>
/// App shell: navigation rail, switching between sections, file pickers, drag and drop,
/// and fullscreen. Each section's own UI and logic lives in its view under Views/.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly LibraryStore _store;
    private bool _returnToLibrary;
    private bool _isPlayerFullscreen;
    private bool _frameIsFlat;

    public MainWindow()
    {
        InitializeComponent();

        _store = new LibraryStore(DispatcherQueue);
        _store.ScanningChanged += isScanning => AppNavigation.IsEnabled = !isScanning;

        HomePage.Initialize(_store);
        HomePage.PlayRequested += (_, request) => StartPlayback(request);
        HomePage.OpenVideoRequested += (_, _) => PickAndPlayVideo();
        HomePage.AddFolderRequested += (_, _) => PickAndAddFolder();
        HomePage.BrowseLibraryRequested += (_, _) => AppNavigation.SelectedItem = AppNavigation.MenuItems[1];

        LibraryPage.Initialize(_store);
        LibraryPage.PlayRequested += (_, request) => StartPlayback(request);
        LibraryPage.AddFolderRequested += (_, _) => PickAndAddFolder();

        PlayerPage.Initialize(_store);
        PlayerPage.BackRequested += (_, _) => ReturnFromPlayer();
        PlayerPage.FullscreenToggleRequested += (_, _) => SetPlayerFullscreen(!_isPlayerFullscreen);

        AppNavigation.SelectedItem = AppNavigation.MenuItems[0];
        Closed += MainWindow_Closed;
    }

    // ---------- Navigation ----------

    private async void AppNavigation_Loaded(object sender, RoutedEventArgs e)
    {
        await _store.RefreshAsync();
    }

    private void AppNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (PlayerPage.Visibility == Visibility.Visible)
        {
            SetPlayerFullscreen(false);
            SetFrameMode(false);
            PlayerPage.Stop();
        }

        if (args.IsSettingsSelected)
        {
            ShowPlaceholder("", "Settings are on the way",
                "Player options live in the gear menu while a video plays. App-wide settings will land here.");
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item)
            return;

        switch (item.Tag?.ToString())
        {
            case "Library":
            case "LibraryVideos":
                ShowLibrary(LibraryMode.Videos);
                break;
            case "LibraryFolders":
                ShowLibrary(LibraryMode.Folders);
                break;
            case "LibraryCollections":
                ShowLibrary(LibraryMode.Collections);
                break;
            case "Jellyfin":
                ShowPlaceholder("", "Jellyfin is coming",
                    "Stream from your Jellyfin server right inside Iris. This arrives after the local player is finished.");
                break;
            default:
                ShowSection(HomePage);
                break;
        }
    }

    private void ShowLibrary(LibraryMode mode)
    {
        LibraryPage.ShowMode(mode);
        ShowSection(LibraryPage);
        _ = _store.RefreshAsync(showBusy: false);
    }

    private void ShowPlaceholder(string glyph, string title, string description)
    {
        PlaceholderPage.SetContent(glyph, title, description);
        ShowSection(PlaceholderPage);
    }

    /// <summary>Shows exactly one of the section views.</summary>
    private void ShowSection(UIElement section)
    {
        HomePage.Visibility = section == HomePage ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = section == LibraryPage ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPage.Visibility = section == PlaceholderPage ? Visibility.Visible : Visibility.Collapsed;
        PlayerPage.Visibility = section == PlayerPage ? Visibility.Visible : Visibility.Collapsed;
        TopBar.Visibility = section == PlayerPage ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Search ----------

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        // The library page filters live; the dropdown offers quick jumps straight into a video.
        LibraryPage.SearchText = sender.Text;
        sender.ItemsSource = _store.Search(sender.Text.Trim()).Take(8).ToList();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is MediaItem media)
        {
            StartPlayback(new PlayRequest(media));
            return;
        }

        // Enter with no suggestion chosen shows every match in the library.
        LibraryPage.SearchText = sender.Text;
        if (!string.IsNullOrWhiteSpace(sender.Text) && LibraryPage.Visibility != Visibility.Visible)
            AppNavigation.SelectedItem = AppNavigation.MenuItems[1];
    }

    private void SearchAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TopBar.Visibility == Visibility.Visible)
        {
            SearchBox.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e) => PickAndPlayVideo();

    // ---------- Opening files and folders ----------

    private async void PickAndAddFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            await _store.AddFolderAsync(folder.Path);
    }

    private async void PickAndPlayVideo()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in MediaLibraryService.SupportedVideoExtensions)
            picker.FileTypeFilter.Add(extension);
        InitializePicker(picker);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        var media = _store.OpenFile(file.Path);
        if (media is null)
        {
            PlayerPage.ShowStatusMessage("Iris couldn’t access that video file.");
            return;
        }

        StartPlayback(new PlayRequest(media));
    }

    private void InitializePicker(object picker)
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
    }

    private void App_DragEnter(object sender, DragEventArgs e) => ShowDropTarget(e);

    private void App_DragOver(object sender, DragEventArgs e) => ShowDropTarget(e);

    private void App_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void App_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        try
        {
            var files = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().ToList();

            // A subtitle dropped onto a playing video is added to it.
            var subtitle = files.FirstOrDefault(item => MediaLibraryService.IsSupportedSubtitleFile(item.Path));
            if (subtitle is not null && PlayerPage.Visibility == Visibility.Visible)
            {
                PlayerPage.AddSubtitle(subtitle.Path);
                return;
            }

            var file = files.FirstOrDefault(item => MediaLibraryService.IsSupportedVideoFile(item.Path));
            if (file is null)
                return;

            var media = _store.OpenFile(file.Path);
            if (media is not null)
                StartPlayback(new PlayRequest(media));
        }
        catch (Exception exception)
        {
            PlayerPage.ShowStatusMessage($"Could not open the dropped video: {exception.Message}");
        }
    }

    private void ShowDropTarget(DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = PlayerPage.Visibility == Visibility.Visible
                ? "Drop a video to play, or subtitles to add them"
                : "Drop to play in Iris";
            e.DragUIOverride.IsCaptionVisible = true;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Player and fullscreen ----------

    private void StartPlayback(PlayRequest request)
    {
        _returnToLibrary = LibraryPage.Visibility == Visibility.Visible;
        ShowSection(PlayerPage);
        PlayerPage.Play(request.Media, request.Queue ?? _store.GetFolderQueue(request.Media));
    }

    private void ReturnFromPlayer()
    {
        SetPlayerFullscreen(false);
        PlayerPage.Stop();
        SetFrameMode(false);
        ShowSection(_returnToLibrary ? LibraryPage : HomePage);
        if (!_returnToLibrary)
            AppNavigation.SelectedItem = AppNavigation.MenuItems[0];
    }

    private void SetPlayerFullscreen(bool enabled)
    {
        if (_isPlayerFullscreen == enabled)
            return;

        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        _isPlayerFullscreen = enabled;
        AppNavigation.IsPaneVisible = !enabled;
        SetFrameMode(enabled);
        PlayerPage.SetFullscreen(enabled);
    }

    /// <summary>Flat removes the rounded content frame so video can fill the screen edge to edge.</summary>
    private void SetFrameMode(bool flat)
    {
        _frameIsFlat = flat;
        RoundedContentSurface.Margin = flat ? new Thickness(0) : new Thickness(0, 0, 8, 8);
        RoundedContentSurface.CornerRadius = flat ? new CornerRadius(0) : new CornerRadius(16);
        RoundedContentSurface.BorderThickness = flat ? new Thickness(0) : new Thickness(1);
        PlayerPage.ApplyFrameMode(flat);
        UpdateContentClip();
    }

    private void ContentRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateContentClip();

    private void UpdateContentClip()
    {
        var visual = ElementCompositionPreview.GetElementVisual(ContentRoot);
        if (_frameIsFlat || ContentRoot.ActualWidth <= 0 || ContentRoot.ActualHeight <= 0)
        {
            visual.Clip = null;
            return;
        }

        const float innerCornerRadius = 15f;
        var radius = new Vector2(innerCornerRadius);
        visual.Clip = visual.Compositor.CreateRectangleClip(
            0,
            0,
            (float)ContentRoot.ActualWidth,
            (float)ContentRoot.ActualHeight,
            radius,
            radius,
            radius,
            radius);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _store.Dispose();
        PlayerPage.Shutdown();
    }
}
