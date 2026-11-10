using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;

namespace Iris_MediaPlayer;

public sealed partial class MainWindow : Window
{
    private const string FolderSettingsKey = "MediaFolders";
    private const string RecentFilesSettingsKey = "RecentMediaFiles";
    private readonly List<string> _folderPaths = [];
    private readonly List<MediaItem> _allMedia = [];
    private readonly ObservableCollection<MediaFolder> _folders = [];
    private readonly ObservableCollection<MediaCollection> _collections = [];
    private readonly List<string> _recentFilePaths = [];
    private readonly Dictionary<string, MediaItem> _oneOffMedia = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _folderWatchers = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _libraryRefreshTimer;
    private readonly DispatcherQueueTimer _playerControlsHideTimer;
    private MpvPlayback? _mpv;
    private MediaItem? _pendingMedia;
    private bool _returnToLibrary;
    private bool _isUpdatingSeek;
    private bool _isSeeking;
    private bool _isPaused = true;
    private bool _isMuted;
    private bool _isUpdatingTrackControls;
    private bool _isUpdatingPlaybackSpeed;
    private bool _isPlayerFullscreen;
    private bool _frameIsFlat;
    private string _libraryMode = "Videos";
    private string? _selectedFolderRoot;
    private string? _currentFolderPath;
    private MediaCollection? _selectedCollection;
    private double _durationSeconds;
    private IntPtr _boundSwapChain;

    public MainWindow()
    {
        InitializeComponent();
        FolderList.ItemsSource = _folders;
        CollectionTiles.ItemsSource = _collections;
        LoadCollections();
        AppNavigation.SelectedItem = AppNavigation.MenuItems[0];
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()!;
        _libraryRefreshTimer = _dispatcherQueue.CreateTimer();
        _libraryRefreshTimer.Interval = TimeSpan.FromMilliseconds(900);
        _libraryRefreshTimer.IsRepeating = false;
        _libraryRefreshTimer.Tick += async (_, _) => await RefreshLibraryAsync(showBusy: false);
        _playerControlsHideTimer = _dispatcherQueue.CreateTimer();
        _playerControlsHideTimer.Interval = TimeSpan.FromSeconds(3);
        _playerControlsHideTimer.IsRepeating = false;
        _playerControlsHideTimer.Tick += (_, _) => SetPlayerControlsVisible(false);
        Closed += MainWindow_Closed;
    }

    private async void AppNavigation_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshLibraryAsync();
    }

    private void AppNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (PlayerView.Visibility == Visibility.Visible)
        {
            SetPlayerFullscreen(false);
            SetFrameMode(false);
            _mpv?.Stop();
        }

        if (args.IsSettingsSelected)
        {
            ShowPlaceholder("Settings", "Settings will be added as the local library and player features grow.");
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item)
            return;

        switch (item.Tag?.ToString())
        {
            case "Library":
            case "LibraryVideos":
            case "LibraryFolders":
            case "LibraryCollections":
                _libraryMode = item.Tag.ToString() switch
                {
                    "LibraryFolders" => "Folders",
                    "LibraryCollections" => "Collections",
                    _ => "Videos"
                };
                LibraryTitle.Text = _libraryMode == "Videos" ? "Library" : _libraryMode;
                if (_libraryMode == "Folders")
                {
                    _selectedFolderRoot = null;
                    _currentFolderPath = null;
                }
                else if (_libraryMode == "Collections")
                {
                    _selectedCollection = null;
                }
                HomeView.Visibility = Visibility.Collapsed;
                PlaceholderView.Visibility = Visibility.Collapsed;
                PlayerView.Visibility = Visibility.Collapsed;
                LibraryView.Visibility = Visibility.Visible;
                RefreshFilteredMedia();
                _ = RefreshLibraryAsync(showBusy: false);
                break;
            case "Jellyfin":
                ShowPlaceholder("Jellyfin", "Jellyfin support is planned after the local media player is in place.");
                break;
            default:
                HomeView.Visibility = Visibility.Visible;
                LibraryView.Visibility = Visibility.Collapsed;
                PlaceholderView.Visibility = Visibility.Collapsed;
                PlayerView.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || _folderPaths.Any(path => string.Equals(path, folder.Path, StringComparison.OrdinalIgnoreCase)))
            return;

        _folderPaths.Add(folder.Path);
        SaveFolderPaths();
        await RefreshLibraryAsync();
    }

    private async void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in MediaLibraryService.SupportedVideoExtensions)
            picker.FileTypeFilter.Add(extension);

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        var media = MediaLibraryService.TryCreateMediaItem(file.Path);
        if (media is null)
        {
            PlaybackStatusText.Text = "Iris couldn’t access that video file.";
            return;
        }

        _oneOffMedia[media.FilePath] = media;
        StartPlayback(media);
    }

    private void App_DragEnter(object sender, DragEventArgs e)
    {
        ShowDropTarget(e);
    }

    private void App_DragOver(object sender, DragEventArgs e)
    {
        ShowDropTarget(e);
    }

    private void App_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void App_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>()
                .FirstOrDefault(item => MediaLibraryService.IsSupportedVideoFile(item.Path));
            if (file is null)
                return;

            var media = MediaLibraryService.TryCreateMediaItem(file.Path);
            if (media is null)
                return;

            _oneOffMedia[media.FilePath] = media;
            StartPlayback(media);
        }
        catch (Exception exception)
        {
            PlaybackStatusText.Text = $"Could not open the dropped video: {exception.Message}";
        }
    }

    private void ShowDropTarget(DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop to play in Iris";
            e.DragUIOverride.IsCaptionVisible = true;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;

        _folderPaths.RemoveAll(savedPath => string.Equals(savedPath, path, StringComparison.OrdinalIgnoreCase));
        SaveFolderPaths();
        await RefreshLibraryAsync();
    }

    private async Task RefreshLibraryAsync(bool showBusy = true)
    {
        LoadFolderPaths();
        if (showBusy)
            AddFolderBusy(true);

        try
        {
            _allMedia.Clear();
            _allMedia.AddRange(await MediaLibraryService.ScanFoldersAsync(_folderPaths));

            ConfigureFolderWatchers();

            _folders.Clear();
            foreach (var path in _folderPaths)
            {
                var count = _allMedia.Count(media => string.Equals(media.LibraryRootPath, path, StringComparison.OrdinalIgnoreCase));
                var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
                _folders.Add(new MediaFolder(path, name, count));
            }

            if (_selectedFolderRoot is not null && !_folderPaths.Contains(_selectedFolderRoot, StringComparer.OrdinalIgnoreCase))
            {
                _selectedFolderRoot = null;
                _currentFolderPath = null;
            }
            else if (_currentFolderPath is not null && !Directory.Exists(_currentFolderPath))
            {
                _currentFolderPath = _selectedFolderRoot;
            }

            FoldersEmpty.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshFilteredMedia();
            UpdateHomeFeature();
            LoadRecentMedia();
            _ = LoadThumbnailsAsync(_allMedia.ToArray());
        }
        finally
        {
            if (showBusy)
                AddFolderBusy(false);
        }
    }

    private void ConfigureFolderWatchers()
    {
        foreach (var watcher in _folderWatchers)
            watcher.Dispose();
        _folderWatchers.Clear();

        foreach (var path in _folderPaths.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true
                };
                watcher.Created += FolderWatcher_Changed;
                watcher.Deleted += FolderWatcher_Changed;
                watcher.Renamed += FolderWatcher_Renamed;
                watcher.Changed += FolderWatcher_Changed;
                watcher.Error += FolderWatcher_Error;
                _folderWatchers.Add(watcher);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void FolderWatcher_Changed(object sender, FileSystemEventArgs e) => QueueLibraryRefresh();
    private void FolderWatcher_Renamed(object sender, RenamedEventArgs e) => QueueLibraryRefresh();
    private void FolderWatcher_Error(object sender, ErrorEventArgs e) => QueueLibraryRefresh();

    private void QueueLibraryRefresh()
    {
        // FileSystemWatcher callbacks run on worker threads; use the window dispatcher.
        _dispatcherQueue.TryEnqueue(() =>
        {
            _libraryRefreshTimer.Stop();
            _libraryRefreshTimer.Start();
        });
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<MediaItem> mediaItems)
    {
        using var limit = new SemaphoreSlim(4);
        var tasks = mediaItems.Select(async media =>
        {
            await limit.WaitAsync();
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(media.FilePath);
                var properties = await file.Properties.GetVideoPropertiesAsync();
                media.SetTechnicalDetails(properties.Width, properties.Height, properties.Bitrate);

                if (media.Thumbnail is null)
                {
                    using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 480, ThumbnailOptions.ResizeThumbnail);
                    if (thumbnail is not null && thumbnail.Size > 0)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(thumbnail);
                        media.Thumbnail = bitmap;
                        if (FeaturedPlayButton.Tag is string featuredPath
                            && string.Equals(featuredPath, media.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            FeaturedImage.Source = bitmap;
                        }
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                limit.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private void AddFolderBusy(bool isBusy)
    {
        AppNavigation.IsEnabled = !isBusy;
        if (isBusy)
            LibrarySummary.Text = "Scanning folders for video files…";
    }

    private void LoadFolderPaths()
    {
        if (_folderPaths.Count > 0)
            return;

        var saved = ApplicationData.Current.LocalSettings.Values[FolderSettingsKey] as string;
        if (string.IsNullOrWhiteSpace(saved))
            return;

        try
        {
            _folderPaths.AddRange(JsonSerializer.Deserialize<List<string>>(saved) ?? []);
        }
        catch (JsonException)
        {
            ApplicationData.Current.LocalSettings.Values.Remove(FolderSettingsKey);
        }
    }

    private void SaveFolderPaths()
    {
        ApplicationData.Current.LocalSettings.Values[FolderSettingsKey] = JsonSerializer.Serialize(_folderPaths);
    }

    private void RefreshFilteredMedia()
    {
        var query = LibrarySearchBox.Text.Trim();
        // Always give ItemsControl a new snapshot. _allMedia is a mutable List and does not
        // notify WinUI when a folder scan clears and repopulates it.
        var matches = _allMedia
            .Where(media => string.IsNullOrWhiteSpace(query)
                || media.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || media.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        switch (_libraryMode)
        {
            case "Folders":
                RefreshFolderBrowser(query);
                break;
            case "Collections":
                RefreshCollectionsView(query);
                break;
            default:
                FolderBrowserPanel.Visibility = Visibility.Collapsed;
                CollectionsPanel.Visibility = Visibility.Collapsed;
                CollectionEmpty.Visibility = Visibility.Collapsed;
                LibraryItems.Visibility = Visibility.Visible;
                LibraryItems.ItemsSource = matches;
                LibraryEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                LibraryEmpty.Text = !string.IsNullOrWhiteSpace(query)
                    ? "No video titles match your search."
                    : "Your library is empty. Add a folder to scan for video files.";
                break;
        }
        LibrarySummary.Text = _allMedia.Count == 0
            ? "Add folders to discover media on this device."
            : $"{_allMedia.Count:N0} video{(_allMedia.Count == 1 ? "" : "s")} in {_folderPaths.Count:N0} folder{(_folderPaths.Count == 1 ? "" : "s")}.";
    }

    private void RefreshFolderBrowser(string query)
    {
        FolderBrowserPanel.Visibility = Visibility.Visible;
        CollectionsPanel.Visibility = Visibility.Collapsed;
        CollectionEmpty.Visibility = Visibility.Collapsed;
        LibraryItems.Visibility = Visibility.Visible;

        if (_selectedFolderRoot is null || _currentFolderPath is null)
        {
            FolderBrowserBackButton.Visibility = Visibility.Collapsed;
            FolderBreadcrumb.Text = "Choose a library folder to browse.";
            FolderTiles.ItemsSource = _folders.ToList();
            FolderBrowserEmpty.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LibraryItems.ItemsSource = Array.Empty<MediaItem>();
            LibraryEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        FolderBrowserEmpty.Visibility = Visibility.Collapsed;
        FolderBrowserBackButton.Visibility = Visibility.Visible;
        var relativePath = System.IO.Path.GetRelativePath(_selectedFolderRoot, _currentFolderPath);
        var rootName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(_selectedFolderRoot));
        if (string.IsNullOrWhiteSpace(rootName))
            rootName = _selectedFolderRoot;
        FolderBreadcrumb.Text = relativePath == "." ? rootName : $"{rootName}  /  {relativePath.Replace('\\', '/')}";
        FolderTiles.ItemsSource = GetChildFolders(_currentFolderPath);

        var videos = _allMedia
            .Where(media => IsSamePath(media.FolderPath, _currentFolderPath)
                && MatchesMediaQuery(media, query))
            .ToList();
        LibraryItems.ItemsSource = videos;
        LibraryEmpty.Visibility = videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmpty.Text = string.IsNullOrWhiteSpace(query)
            ? "This folder has no videos. Open a subfolder or go back to browse another folder."
            : "No videos in this folder match your search.";
    }

    private List<MediaFolder> GetChildFolders(string parentPath)
    {
        try
        {
            return Directory.GetDirectories(parentPath)
                .Select(path => new MediaFolder(path,
                    System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)),
                    _allMedia.Count(media => IsPathWithin(media.FolderPath, path))))
                .Where(folder => folder.MediaCount > 0)
                .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsPathWithin(string candidatePath, string parentPath)
    {
        var normalizedParent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(parentPath));
        var normalizedCandidate = System.IO.Path.GetFullPath(candidatePath);
        return normalizedCandidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedParent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePath(string firstPath, string secondPath)
        => string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(firstPath)),
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(secondPath)),
            StringComparison.OrdinalIgnoreCase);

    private static bool MatchesMediaQuery(MediaItem media, string query)
        => string.IsNullOrWhiteSpace(query)
            || media.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || media.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void Subfolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;

        if (_selectedFolderRoot is null)
            _selectedFolderRoot = path;
        _currentFolderPath = path;
        RefreshFilteredMedia();
    }

    private void FolderBrowserBack_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFolderRoot is null || _currentFolderPath is null)
            return;

        if (IsSamePath(_currentFolderPath, _selectedFolderRoot))
        {
            _selectedFolderRoot = null;
            _currentFolderPath = null;
        }
        else
        {
            var parent = System.IO.Path.GetDirectoryName(_currentFolderPath);
            _currentFolderPath = parent is not null && IsPathWithin(parent, _selectedFolderRoot)
                ? parent
                : _selectedFolderRoot;
        }

        RefreshFilteredMedia();
    }

    private void RefreshCollectionsView(string query)
    {
        FolderBrowserPanel.Visibility = Visibility.Collapsed;
        CollectionsPanel.Visibility = Visibility.Visible;
        CollectionTiles.Visibility = _selectedCollection is null ? Visibility.Visible : Visibility.Collapsed;
        CollectionEmpty.Visibility = _selectedCollection is null && _collections.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CollectionsBackButton.Visibility = _selectedCollection is null ? Visibility.Collapsed : Visibility.Visible;
        CollectionHeading.Visibility = _selectedCollection is null ? Visibility.Visible : Visibility.Collapsed;
        SelectedCollectionName.Visibility = _selectedCollection is null ? Visibility.Collapsed : Visibility.Visible;
        SelectedCollectionCount.Visibility = _selectedCollection is null ? Visibility.Collapsed : Visibility.Visible;
        AddCollectionVideosButton.Visibility = _selectedCollection is null ? Visibility.Collapsed : Visibility.Visible;
        CreateCollectionButton.Visibility = _selectedCollection is null ? Visibility.Visible : Visibility.Collapsed;
        DeleteCollectionButton.Visibility = _selectedCollection is null ? Visibility.Collapsed : Visibility.Visible;

        if (_selectedCollection is null)
        {
            LibraryItems.Visibility = Visibility.Collapsed;
            LibraryEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        var videos = _selectedCollection.FilePaths
            .Select(ResolveMedia)
            .Where(media => media is not null)
            .Cast<MediaItem>()
            .Where(media => MatchesMediaQuery(media, query))
            .ToList();
        SelectedCollectionName.Text = _selectedCollection.Name;
        SelectedCollectionCount.Text = $"{videos.Count:N0} video{(videos.Count == 1 ? "" : "s")}";
        LibraryItems.Visibility = Visibility.Visible;
        LibraryItems.ItemsSource = videos;
        LibraryEmpty.Visibility = videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmpty.Text = string.IsNullOrWhiteSpace(query)
            ? "This collection is empty. Add videos from your library."
            : "No videos in this collection match your search.";
    }

    private void CollectionTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MediaCollection collection })
        {
            _selectedCollection = collection;
            RefreshFilteredMedia();
        }
    }

    private void CollectionsBack_Click(object sender, RoutedEventArgs e)
    {
        _selectedCollection = null;
        RefreshFilteredMedia();
    }

    private async void CreateCollection_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "Collection name" };
        var dialog = new ContentDialog
        {
            Title = "Create collection",
            Content = nameBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = LibraryView.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var name = nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        var collection = new MediaCollection { Name = name };
        _collections.Add(collection);
        _selectedCollection = collection;
        SaveCollections();
        RefreshFilteredMedia();
    }

    private async void AddCollectionVideos_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCollection is null)
            return;

        var availableMedia = _allMedia
            .Concat(_oneOffMedia.Values)
            .GroupBy(media => media.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(media => media.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (availableMedia.Count == 0)
            return;

        var picker = new ListView
        {
            ItemsSource = availableMedia,
            DisplayMemberPath = nameof(MediaItem.Title),
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 480
        };
        foreach (var media in availableMedia.Where(media =>
                     _selectedCollection.FilePaths.Contains(media.FilePath, StringComparer.OrdinalIgnoreCase)))
            picker.SelectedItems.Add(media);
        var dialog = new ContentDialog
        {
            Title = $"Edit videos in {_selectedCollection.Name}",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Select videos from any folder or recently opened files. The files stay where they are." },
                    picker
                }
            },
            PrimaryButtonText = "Save selection",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = LibraryView.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _selectedCollection.FilePaths = picker.SelectedItems.OfType<MediaItem>()
            .Select(media => media.FilePath)
            .ToList();

        SaveCollections();
        RefreshFilteredMedia();
    }

    private void DeleteCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCollection is null)
            return;

        _collections.Remove(_selectedCollection);
        _selectedCollection = null;
        SaveCollections();
        RefreshFilteredMedia();
    }

    private void LoadCollections()
    {
        var filePath = GetCollectionsFilePath();
        if (!File.Exists(filePath))
            return;

        try
        {
            foreach (var collection in JsonSerializer.Deserialize<List<MediaCollection>>(File.ReadAllText(filePath)) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(collection.Name))
                {
                    collection.FilePaths ??= [];
                    _collections.Add(collection);
                }
            }
        }
        catch (JsonException)
        {
            _collections.Clear();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SaveCollections()
    {
        try
        {
            File.WriteAllText(GetCollectionsFilePath(), JsonSerializer.Serialize(_collections));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetCollectionsFilePath()
        => System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "media-collections.json");

    private void UpdateHomeFeature()
    {
        var featured = _allMedia.OrderByDescending(media => media.LastModified).FirstOrDefault();
        if (featured is null)
        {
            FeaturedImage.Source = null;
            FeaturedTitle.Text = "Your next favorite starts here";
            FeaturedSubtitle.Text = "Add a folder and Iris will discover your movies and shows.";
            FeaturedPlayButton.Tag = null;
            FeaturedPlayButton.IsEnabled = false;
            return;
        }

        FeaturedImage.Source = featured.Thumbnail;
        FeaturedTitle.Text = featured.Title;
        FeaturedSubtitle.Text = $"In {featured.FolderName}  ·  {featured.FileFormat}  ·  Added {featured.LastModified:d}";
        FeaturedPlayButton.Tag = featured.FilePath;
        FeaturedPlayButton.IsEnabled = true;
    }

    private void MediaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filePath })
            return;

        var media = ResolveMedia(filePath);
        if (media is not null)
            StartPlayback(media);
    }

    private void LibraryItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItem media)
            StartPlayback(media);
    }

    private void FeaturedPlay_Click(object sender, RoutedEventArgs e)
    {
        if (FeaturedPlayButton.Tag is not string filePath)
            return;

        var media = _allMedia.FirstOrDefault(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (media is not null)
            StartPlayback(media);
    }

    private void StartPlayback(MediaItem media)
    {
        _playerControlsHideTimer.Stop();
        SetPlayerControlsVisible(true);
        _returnToLibrary = LibraryView.Visibility == Visibility.Visible;
        _pendingMedia = media;
        HomeView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Collapsed;
        PlaceholderView.Visibility = Visibility.Collapsed;
        PlayerView.Visibility = Visibility.Visible;

        PlayerTitle.Text = media.Title;
        PlaybackStatusText.Text = "Starting mpv…";
        PlaybackProgress.IsActive = true;
        PlayerStatus.Visibility = Visibility.Visible;
        PlayPauseButton.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        _durationSeconds = 0;
        _isPaused = true;

        if (_mpv is not null)
            LoadPendingMedia();
        else if (VideoHost.ActualWidth > 0 && VideoHost.ActualHeight > 0)
            InitializePlayer();
    }

    private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(e.NewSize.Width * scale));
        var height = Math.Max(1, (int)Math.Round(e.NewSize.Height * scale));

        if (_mpv is null)
        {
            if (_pendingMedia is not null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                InitializePlayer(width, height);
            return;
        }

        _mpv.Resize(width, height);
    }

    private void InitializePlayer(int? width = null, int? height = null)
    {
        if (_mpv is not null)
            return;

        try
        {
            var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
            var outputWidth = width ?? Math.Max(1, (int)Math.Round(VideoHost.ActualWidth * scale));
            var outputHeight = height ?? Math.Max(1, (int)Math.Round(VideoHost.ActualHeight * scale));
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                             ?? throw new InvalidOperationException("The Windows UI dispatcher is unavailable.");

            _mpv = new MpvPlayback(dispatcher, outputWidth, outputHeight);
            _mpv.SwapChainChanged += Mpv_SwapChainChanged;
            _mpv.PropertyChanged += Mpv_PropertyChanged;

            var swapChain = _mpv.CurrentSwapChain;
            if (swapChain != IntPtr.Zero)
                Mpv_SwapChainChanged(swapChain);

            _mpv.SetVolume(VolumeSlider.Value);
            LoadPendingMedia();
        }
        catch (Exception exception)
        {
            _mpv?.Dispose();
            _mpv = null;
            PlaybackProgress.IsActive = false;
            PlaybackStatusText.Text = $"Could not start mpv: {exception.Message}";
            PlayerStatus.Visibility = Visibility.Visible;
        }
    }

    private void LoadPendingMedia()
    {
        if (_mpv is null || _pendingMedia is null)
            return;

        try
        {
            _mpv.LoadFile(_pendingMedia.FilePath);
        }
        catch (Exception exception)
        {
            PlaybackProgress.IsActive = false;
            PlaybackStatusText.Text = $"Could not open this video: {exception.Message}";
            PlayerStatus.Visibility = Visibility.Visible;
        }
    }

    private void Mpv_SwapChainChanged(IntPtr swapChain)
    {
        _boundSwapChain = swapChain;
        SwapChainPanelInterop.SetSwapChain(VideoPanel, swapChain);
    }

    private void Mpv_PropertyChanged(string name, object? value)
    {
        switch (name)
        {
            case "file-loaded":
                PlaybackProgress.IsActive = false;
                PlayerStatus.Visibility = Visibility.Collapsed;
                PlayPauseButton.IsEnabled = true;
                SeekBackwardButton.IsEnabled = true;
                SeekForwardButton.IsEnabled = true;
                MuteButton.IsEnabled = true;
                SeekSlider.IsEnabled = _durationSeconds > 0;
                RefreshTrackOptions();
                if (_pendingMedia is not null)
                    RecordRecentMedia(_pendingMedia);
                break;
            case "time-pos" when value is double position:
                CurrentTimeText.Text = FormatTime(position);
                if (!_isSeeking)
                    SetSeekSliderValue(position);
                break;
            case "duration" when value is double duration:
                _durationSeconds = Math.Max(0, duration);
                DurationText.Text = FormatTime(_durationSeconds);
                SeekSlider.IsEnabled = _durationSeconds > 0;
                SetSeekSliderMaximum(_durationSeconds);
                break;
            case "pause" when value is bool paused:
                _isPaused = paused;
                PlayPauseIcon.Glyph = paused ? "\uE768" : "\uE769";
                break;
            case "volume" when value is double volume:
                VolumeSlider.Value = Math.Clamp(volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
                break;
            case "mute" when value is bool muted:
                _isMuted = muted;
                MuteIcon.Glyph = muted ? "\uE74F" : "\uE767";
                break;
            case "speed" when value is double speed:
                UpdatePlaybackSpeedSelection(speed);
                break;
            case "media-title" when value is string title && !string.IsNullOrWhiteSpace(title):
                if (_pendingMedia is null)
                    PlayerTitle.Text = title;
                break;
            case "playback-error" when value is int error:
                PlaybackProgress.IsActive = false;
                PlaybackStatusText.Text = $"mpv could not play this file (error {error}).";
                PlayerStatus.Visibility = Visibility.Visible;
                PlayPauseButton.IsEnabled = false;
                break;
            case "playback-ended":
                PlaybackProgress.IsActive = false;
                PlayPauseButton.IsEnabled = false;
                break;
        }
    }

    private void SetSeekSliderMaximum(double duration)
    {
        _isUpdatingSeek = true;
        SeekSlider.Maximum = Math.Max(1, duration);
        _isUpdatingSeek = false;
    }

    private void SetSeekSliderValue(double position)
    {
        _isUpdatingSeek = true;
        SeekSlider.Value = Math.Clamp(position, SeekSlider.Minimum, SeekSlider.Maximum);
        _isUpdatingSeek = false;
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        TogglePlaybackPause();
    }

    private void TogglePlaybackPause()
    {
        if (_mpv is null)
            return;

        try
        {
            _isPaused = _mpv.TogglePause();
            PlayPauseIcon.Glyph = _isPaused ? "\uE768" : "\uE769";
        }
        catch (Exception exception)
        {
            PlaybackStatusText.Text = $"Could not toggle playback: {exception.Message}";
            PlayerStatus.Visibility = Visibility.Visible;
        }
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isUpdatingSeek && !_isSeeking && _mpv is not null && _durationSeconds > 0)
            _mpv.Seek(e.NewValue);
    }

    private void SeekSlider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isSeeking = true;
    }

    private void SeekSlider_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isSeeking = false;
        if (_mpv is not null && _durationSeconds > 0)
            _mpv.Seek(SeekSlider.Value);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _mpv?.SetVolume(e.NewValue);
    }

    private void SeekBarHost_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!SeekSlider.IsEnabled || _durationSeconds <= 0 || SeekBarHost.ActualWidth <= 0)
            return;

        var pointerX = e.GetCurrentPoint(SeekBarHost).Position.X;
        var progress = Math.Clamp(pointerX / SeekBarHost.ActualWidth, 0, 1);
        SeekPreviewText.Text = FormatTime(progress * _durationSeconds);
        SeekPreviewBadge.Visibility = Visibility.Visible;
        var badgeWidth = SeekPreviewBadge.Width > 0 ? SeekPreviewBadge.Width : 52;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(
            SeekPreviewBadge,
            Math.Clamp(pointerX - badgeWidth / 2, 0, Math.Max(0, SeekBarHost.ActualWidth - badgeWidth)));
    }

    private void SeekBarHost_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SeekPreviewBadge.Visibility = Visibility.Collapsed;
    }

    private void PlayerControlsBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Min(e.NewSize.Width, PlayerControlsBar.ActualWidth);

        // Progressively drop the least important controls so the seek bar always keeps room.
        VolumeSlider.Visibility = width < 760 ? Visibility.Collapsed : Visibility.Visible;

        var narrow = width < 560;
        var vis = narrow ? Visibility.Collapsed : Visibility.Visible;
        SeekBackwardButton.Visibility = vis;
        SeekForwardButton.Visibility = vis;

        var tiny = width < 420;
        TimeSeparatorText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        DurationText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        TimeGroup.MinWidth = tiny ? 40 : 104;

        PlayerControlsBar.Padding = narrow ? new Thickness(8, 6, 8, 6) : new Thickness(12, 6, 12, 6);
        ControlsRow.ColumnSpacing = narrow ? 8 : 12;
    }

    private void SeekBackward_Click(object sender, RoutedEventArgs e) => SeekBy(-10);

    private void SeekForward_Click(object sender, RoutedEventArgs e) => SeekBy(10);

    private void SeekBy(double seconds)
    {
        if (_mpv is null || _durationSeconds <= 0)
            return;

        var position = Math.Clamp(SeekSlider.Value + seconds, 0, _durationSeconds);
        SetSeekSliderValue(position);
        _mpv.Seek(position);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _mpv?.SetMuted(!_isMuted);
    }

    private void PlaybackSpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPlaybackSpeed || _mpv is null || PlaybackSpeedCombo.SelectedItem is not ComboBoxItem item)
            return;

        if (double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            _mpv.SetSpeed(speed);
    }

    private void UpdatePlaybackSpeedSelection(double speed)
    {
        var closestItem = PlaybackSpeedCombo.Items
            .OfType<ComboBoxItem>()
            .Select(item => (Item: item, Parsed: double.TryParse(item.Tag?.ToString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN))
            .Where(option => double.IsFinite(option.Parsed))
            .OrderBy(option => Math.Abs(option.Parsed - speed))
            .FirstOrDefault();

        if (closestItem.Item is null || Math.Abs(closestItem.Parsed - speed) > 0.001)
            return;

        _isUpdatingPlaybackSpeed = true;
        PlaybackSpeedCombo.SelectedItem = closestItem.Item;
        _isUpdatingPlaybackSpeed = false;
    }

    private void RefreshTrackOptions()
    {
        if (_mpv is null)
            return;

        _isUpdatingTrackControls = true;
        var audioTracks = _mpv.GetTracks("audio");
        AudioTrackCombo.Items.Clear();
        foreach (var track in audioTracks)
            AudioTrackCombo.Items.Add(new ComboBoxItem { Content = track.DisplayName, Tag = track.Id });
        AudioTrackCombo.SelectedItem = audioTracks.Select((track, index) => (track, index))
            .Where(item => item.track.IsSelected)
            .Select(item => AudioTrackCombo.Items[item.index])
            .FirstOrDefault();
        AudioTrackCombo.IsEnabled = audioTracks.Count > 0;

        var subtitleTracks = _mpv.GetTracks("sub");
        SubtitleTrackCombo.Items.Clear();
        SubtitleTrackCombo.Items.Add(new ComboBoxItem { Content = "Off", Tag = -1L });
        foreach (var track in subtitleTracks)
            SubtitleTrackCombo.Items.Add(new ComboBoxItem { Content = track.DisplayName, Tag = track.Id });
        var selectedSubtitle = subtitleTracks.FirstOrDefault(track => track.IsSelected);
        SubtitleTrackCombo.SelectedItem = selectedSubtitle is null
            ? SubtitleTrackCombo.Items[0]
            : SubtitleTrackCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selectedSubtitle.Id));
        SubtitleTrackCombo.IsEnabled = subtitleTracks.Count > 0;
        _isUpdatingTrackControls = false;
    }

    private void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTrackControls || _mpv is null || AudioTrackCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not long trackId)
            return;
        _mpv.SetAudioTrack(trackId);
    }

    private void SubtitleTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTrackControls || _mpv is null || SubtitleTrackCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not long trackId)
            return;
        _mpv.SetSubtitleTrack(trackId);
    }

    private void PlayerBack_Click(object sender, RoutedEventArgs e)
    {
        ReturnFromPlayer();
    }

    private void ReturnFromPlayer()
    {
        SetPlayerFullscreen(false);
        _mpv?.Stop();
        PlayerView.Visibility = Visibility.Collapsed;
        SetFrameMode(false);
        PlaceholderView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = _returnToLibrary ? Visibility.Collapsed : Visibility.Visible;
        LibraryView.Visibility = _returnToLibrary ? Visibility.Visible : Visibility.Collapsed;
        if (!_returnToLibrary)
            AppNavigation.SelectedItem = AppNavigation.MenuItems[0];
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        SetPlayerFullscreen(!_isPlayerFullscreen);
    }

    private void PlayerView_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPlayerFullscreen || PlayerView.Visibility != Visibility.Visible)
            return;

        SetPlayerControlsVisible(true);
        _playerControlsHideTimer.Stop();
        _playerControlsHideTimer.Start();
    }

    private void PlayerView_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && PlayerView.Visibility == Visibility.Visible)
        {
            ReturnFromPlayer();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.F)
        {
            SetPlayerFullscreen(!_isPlayerFullscreen);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Space && PlayerView.Visibility == Visibility.Visible)
        {
            TogglePlaybackPause();
            e.Handled = true;
        }
    }

    private void PlayerKeyboardAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (PlayerView.Visibility != Visibility.Visible)
            return;

        switch (sender.Key)
        {
            case Windows.System.VirtualKey.Escape:
                ReturnFromPlayer();
                args.Handled = true;
                break;
            case Windows.System.VirtualKey.F:
                SetPlayerFullscreen(!_isPlayerFullscreen);
                args.Handled = true;
                break;
            case Windows.System.VirtualKey.Space:
                TogglePlaybackPause();
                args.Handled = true;
                break;
        }
    }

    private void SetPlayerControlsVisible(bool visible)
    {
        var opacity = visible ? 1d : 0d;
        PlayerHeaderBar.Opacity = opacity;
        PlayerControlsBar.Opacity = opacity;
        PlayerHeaderBar.IsHitTestVisible = visible;
        PlayerControlsBar.IsHitTestVisible = visible;
    }

    private void SetPlayerFullscreen(bool enabled)
    {
        if (_isPlayerFullscreen == enabled)
            return;

        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        _isPlayerFullscreen = enabled;
        AppNavigation.IsPaneVisible = !enabled;
        SetFrameMode(enabled);
        FullscreenIcon.Glyph = enabled ? "\uE73F" : "\uE740";
        _playerControlsHideTimer.Stop();
        SetPlayerControlsVisible(true);
        if (enabled)
            _playerControlsHideTimer.Start();
    }

    private void SetFrameMode(bool flat)
    {
        _frameIsFlat = flat;
        RoundedContentSurface.Margin = flat
            ? new Thickness(0)
            : new Thickness(0, 0, 8, 8);
        RoundedContentSurface.CornerRadius = flat
            ? new CornerRadius(0)
            : new CornerRadius(16);
        RoundedContentSurface.BorderThickness = flat
            ? new Thickness(0)
            : new Thickness(1);
        ApplyPlayerCorners(flat);
        UpdateContentClip();
    }

    private void ApplyPlayerCorners(bool flat)
    {
        PlayerView.CornerRadius = flat ? new CornerRadius(0) : new CornerRadius(15);
        // Windowed: video sits between the header and controls bars, so it never reaches a corner.
        // Fullscreen: video spans all rows and the bars overlay it (auto-hide).
        Grid.SetRow(VideoHost, flat ? 0 : 1);
        Grid.SetRowSpan(VideoHost, flat ? 3 : 1);
        PlayerHeaderBar.CornerRadius = flat ? new CornerRadius(0) : new CornerRadius(15, 15, 0, 0);
        PlayerControlsBar.CornerRadius = flat ? new CornerRadius(0) : new CornerRadius(0, 0, 15, 15);
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
        _libraryRefreshTimer.Stop();
        _playerControlsHideTimer.Stop();
        foreach (var watcher in _folderWatchers)
            watcher.Dispose();
        _folderWatchers.Clear();
        if (_mpv is not null)
        {
            _mpv.SwapChainChanged -= Mpv_SwapChainChanged;
            _mpv.PropertyChanged -= Mpv_PropertyChanged;
        }
        if (_boundSwapChain != IntPtr.Zero)
            SwapChainPanelInterop.SetSwapChain(VideoPanel, IntPtr.Zero);
        _mpv?.Dispose();
        _mpv = null;
    }

    private void LoadRecentMedia()
    {
        if (_recentFilePaths.Count == 0)
        {
            var saved = ApplicationData.Current.LocalSettings.Values[RecentFilesSettingsKey] as string;
            if (!string.IsNullOrWhiteSpace(saved))
            {
                try
                {
                    _recentFilePaths.AddRange(JsonSerializer.Deserialize<List<string>>(saved) ?? []);
                }
                catch (JsonException)
                {
                    ApplicationData.Current.LocalSettings.Values.Remove(RecentFilesSettingsKey);
                }
            }
        }

        var recent = _recentFilePaths
            .Select(ResolveMedia)
            .Where(media => media is not null)
            .Cast<MediaItem>()
            .ToList();
        RecentItems.ItemsSource = recent;
        _ = LoadThumbnailsAsync(recent);
        RecentlyPlayedEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentItems.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private MediaItem? ResolveMedia(string filePath)
    {
        var libraryMedia = _allMedia.FirstOrDefault(media => string.Equals(media.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (libraryMedia is not null)
            return libraryMedia;

        if (_oneOffMedia.TryGetValue(filePath, out var oneOffMedia))
            return oneOffMedia;

        var resolved = MediaLibraryService.TryCreateMediaItem(filePath);
        if (resolved is not null)
            _oneOffMedia[filePath] = resolved;
        return resolved;
    }

    private void RecordRecentMedia(MediaItem media)
    {
        _recentFilePaths.RemoveAll(path => string.Equals(path, media.FilePath, StringComparison.OrdinalIgnoreCase));
        _recentFilePaths.Insert(0, media.FilePath);
        if (_recentFilePaths.Count > 8)
            _recentFilePaths.RemoveRange(8, _recentFilePaths.Count - 8);

        ApplicationData.Current.LocalSettings.Values[RecentFilesSettingsKey] = JsonSerializer.Serialize(_recentFilePaths);
        LoadRecentMedia();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox source)
        {
            var otherSearchBox = ReferenceEquals(source, SearchBox) ? LibrarySearchBox : SearchBox;
            if (!string.Equals(otherSearchBox.Text, source.Text, StringComparison.Ordinal))
            {
                otherSearchBox.Text = source.Text;
                return;
            }
        }

        RefreshFilteredMedia();
    }

    private void LibraryShortcut_Click(object sender, RoutedEventArgs e)
    {
        AppNavigation.SelectedItem = AppNavigation.MenuItems[1];
    }

    private void ShowPlaceholder(string title, string description)
    {
        HomeView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Collapsed;
        PlaceholderView.Visibility = Visibility.Visible;
        PlaceholderTitle.Text = title;
        PlaceholderDescription.Text = description;
    }
}