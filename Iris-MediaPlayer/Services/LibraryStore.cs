using Iris_MediaPlayer.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Iris_MediaPlayer.Services;

/// <summary>
/// Library state shared by every view: saved folders, scanned media, collections and
/// recently played files. Create and use it on the UI thread.
/// </summary>
public sealed class LibraryStore : IDisposable
{
    private const string FolderSettingsKey = "MediaFolders";
    private const string RecentFilesSettingsKey = "RecentMediaFiles";
    private const int MaxRecentItems = 8;
    private const int MaxContinueWatchingItems = 12;

    private readonly List<string> _folderPaths = [];
    private readonly List<MediaItem> _allMedia = [];
    private readonly List<string> _recentFilePaths = [];
    private readonly Dictionary<string, MediaItem> _oneOffMedia = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _folderWatchers = [];
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly PlaybackProgressStore _progress = new();
    private readonly Dictionary<string, ImageSource> _backdrops = new(StringComparer.OrdinalIgnoreCase);

    public LibraryStore(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _refreshTimer = dispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(900);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(showBusy: false);

        LoadFolderPaths();
        LoadRecentPaths();
        LoadCollections();
        RefreshContinueWatching();
    }

    public IReadOnlyList<string> FolderPaths => _folderPaths;
    public IReadOnlyList<MediaItem> AllMedia => _allMedia;
    public ObservableCollection<MediaFolder> Folders { get; } = [];
    public ObservableCollection<MediaCollection> Collections { get; } = [];
    public IReadOnlyList<MediaItem> RecentMedia { get; private set; } = [];
    public IReadOnlyList<MediaItem> ContinueWatching { get; private set; } = [];

    /// <summary>Raised after a folder scan finishes and <see cref="AllMedia"/> is up to date.</summary>
    public event Action? LibraryChanged;

    /// <summary>Raised when <see cref="RecentMedia"/> changes.</summary>
    public event Action? RecentMediaChanged;

    /// <summary>Raised when the videos in <see cref="ContinueWatching"/> or their order change.</summary>
    public event Action? ContinueWatchingChanged;

    /// <summary>Raised with true when a user-visible scan starts and false when it ends.</summary>
    public event Action<bool>? ScanningChanged;

    // ---------- Folders and scanning ----------

    public async Task RefreshAsync(bool showBusy = true)
    {
        if (showBusy)
            ScanningChanged?.Invoke(true);

        try
        {
            var scanned = await MediaLibraryService.ScanFoldersAsync(_folderPaths);
            _allMedia.Clear();
            _allMedia.AddRange(scanned);

            ConfigureFolderWatchers();

            Folders.Clear();
            foreach (var path in _folderPaths)
            {
                var media = _allMedia.Where(item => string.Equals(item.LibraryRootPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
                var preview = media.OrderByDescending(item => item.LastModified).FirstOrDefault();
                Folders.Add(new MediaFolder(path, PathUtils.GetFolderName(path), media.Count, preview));
            }

            LibraryChanged?.Invoke();
            RefreshRecentMedia();
            // A rescan creates new MediaItem objects, so rebuild the list even if paths are unchanged.
            RefreshContinueWatching();
            _ = LoadThumbnailsAsync(_allMedia.ToArray());
        }
        finally
        {
            if (showBusy)
                ScanningChanged?.Invoke(false);
        }
    }

    public async Task<bool> AddFolderAsync(string path)
    {
        if (_folderPaths.Any(saved => string.Equals(saved, path, StringComparison.OrdinalIgnoreCase)))
            return false;

        _folderPaths.Add(path);
        SaveFolderPaths();
        await RefreshAsync();
        return true;
    }

    public async Task RemoveFolderAsync(string path)
    {
        _folderPaths.RemoveAll(saved => string.Equals(saved, path, StringComparison.OrdinalIgnoreCase));
        SaveFolderPaths();
        await RefreshAsync();
    }

    /// <summary>Returns the subfolders of <paramref name="parentPath"/> that contain library videos.</summary>
    public List<MediaFolder> GetChildFolders(string parentPath)
    {
        try
        {
            return Directory.GetDirectories(parentPath)
                .Select(path =>
                {
                    var media = _allMedia.Where(item => PathUtils.IsPathWithin(item.FolderPath, path)).ToList();
                    var preview = media.OrderByDescending(item => item.LastModified).FirstOrDefault();
                    return new MediaFolder(path, PathUtils.GetFolderName(path), media.Count, preview);
                })
                .Where(folder => folder.MediaCount > 0)
                .OrderBy(folder => folder.Name, NaturalStringComparer.Instance)
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

    private void ConfigureFolderWatchers()
    {
        DisposeWatchers();

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
                watcher.Created += (_, _) => QueueRefresh();
                watcher.Deleted += (_, _) => QueueRefresh();
                watcher.Renamed += (_, _) => QueueRefresh();
                watcher.Changed += (_, _) => QueueRefresh();
                watcher.Error += (_, _) => QueueRefresh();
                _folderWatchers.Add(watcher);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void QueueRefresh()
    {
        // FileSystemWatcher callbacks run on worker threads; debounce on the UI dispatcher.
        _dispatcherQueue.TryEnqueue(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _folderWatchers)
            watcher.Dispose();
        _folderWatchers.Clear();
    }

    private void LoadFolderPaths()
    {
        var saved = AppStorage.GetString(FolderSettingsKey);
        if (string.IsNullOrWhiteSpace(saved))
            return;

        try
        {
            _folderPaths.AddRange(JsonSerializer.Deserialize(saved, IrisJsonContext.Default.ListString) ?? []);
        }
        catch (JsonException)
        {
            AppStorage.SetString(FolderSettingsKey, null);
        }
    }

    private void SaveFolderPaths()
    {
        AppStorage.SetString(FolderSettingsKey, JsonSerializer.Serialize(_folderPaths, IrisJsonContext.Default.ListString));
    }

    // ---------- Media lookup ----------

    /// <summary>Finds a library item by path, or creates a one-off item for a file outside the library.</summary>
    public MediaItem? ResolveMedia(string filePath)
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

    /// <summary>Opens a file that the user picked or dropped, without adding it to the library.</summary>
    public MediaItem? OpenFile(string filePath)
    {
        var media = MediaLibraryService.TryCreateMediaItem(filePath);
        if (media is not null)
            _oneOffMedia[media.FilePath] = media;
        return media;
    }

    /// <summary>
    /// The videos in <paramref name="media"/>'s folder in natural name order, used for next/previous.
    /// The list always contains <paramref name="media"/> itself.
    /// </summary>
    public IReadOnlyList<MediaItem> GetFolderQueue(MediaItem media)
    {
        try
        {
            var queue = Directory.EnumerateFiles(media.FolderPath)
                .Where(MediaLibraryService.IsSupportedVideoFile)
                .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
                .Select(ResolveMedia)
                .OfType<MediaItem>()
                .ToList();

            var index = queue.FindIndex(item => string.Equals(item.FilePath, media.FilePath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return [media];

            queue[index] = media;
            return queue;
        }
        catch (IOException)
        {
            return [media];
        }
        catch (UnauthorizedAccessException)
        {
            return [media];
        }
    }

    /// <summary>The newest videos in the library, by file date.</summary>
    public IReadOnlyList<MediaItem> GetRecentlyAdded(int count)
        => _allMedia.OrderByDescending(media => media.LastModified).Take(count).ToList();

    /// <summary>Library videos whose title or folder contains <paramref name="query"/>.</summary>
    public IEnumerable<MediaItem> Search(string query)
        => string.IsNullOrWhiteSpace(query)
            ? []
            : _allMedia.Where(media => media.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                       || media.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// A large frame for full-width artwork such as the Home hero. Card thumbnails are too small
    /// to stretch across the page. Falls back to the card thumbnail.
    /// </summary>
    public async Task<ImageSource?> GetBackdropAsync(MediaItem media)
    {
        if (_backdrops.TryGetValue(media.FilePath, out var cached))
            return cached;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(media.FilePath);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 1600, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0)
                return media.Thumbnail;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            _backdrops[media.FilePath] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return media.Thumbnail;
        }
    }

    /// <summary>All library and one-off media, de-duplicated and sorted by title.</summary>
    public List<MediaItem> GetAllKnownMedia()
        => _allMedia
            .Concat(_oneOffMedia.Values)
            .GroupBy(media => media.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(media => media.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

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
                media.SetTechnicalDetails(properties.Width, properties.Height, properties.Bitrate, properties.Duration);

                if (media.Thumbnail is null)
                {
                    using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 480, ThumbnailOptions.ResizeThumbnail);
                    if (thumbnail is not null && thumbnail.Size > 0)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(thumbnail);
                        media.Thumbnail = bitmap;
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

    // ---------- Recently played ----------

    public void RecordRecent(MediaItem media)
    {
        _recentFilePaths.RemoveAll(path => string.Equals(path, media.FilePath, StringComparison.OrdinalIgnoreCase));
        _recentFilePaths.Insert(0, media.FilePath);
        if (_recentFilePaths.Count > MaxRecentItems)
            _recentFilePaths.RemoveRange(MaxRecentItems, _recentFilePaths.Count - MaxRecentItems);

        AppStorage.SetString(RecentFilesSettingsKey, JsonSerializer.Serialize(_recentFilePaths, IrisJsonContext.Default.ListString));
        RefreshRecentMedia();
    }

    private void RefreshRecentMedia()
    {
        var recent = _recentFilePaths
            .Select(ResolveMedia)
            .Where(media => media is not null)
            .Cast<MediaItem>()
            .ToList();
        RecentMedia = recent;
        RecentMediaChanged?.Invoke();
        _ = LoadThumbnailsAsync(recent);
    }

    private void LoadRecentPaths()
    {
        var saved = AppStorage.GetString(RecentFilesSettingsKey);
        if (string.IsNullOrWhiteSpace(saved))
            return;

        try
        {
            _recentFilePaths.AddRange(JsonSerializer.Deserialize(saved, IrisJsonContext.Default.ListString) ?? []);
        }
        catch (JsonException)
        {
            AppStorage.SetString(RecentFilesSettingsKey, null);
        }
    }

    // ---------- Playback progress ----------

    /// <summary>Where to resume <paramref name="media"/>, or null to start from the beginning.</summary>
    public double? GetResumePosition(MediaItem media) => _progress.GetResumePosition(media.FilePath);

    public void SaveProgress(MediaItem media, double positionSeconds, double durationSeconds)
    {
        media.SetWatchProgress(positionSeconds, durationSeconds);
        _progress.Save(media.FilePath, positionSeconds, durationSeconds);
        RefreshContinueWatching();
    }

    private void RefreshContinueWatching()
    {
        var items = new List<MediaItem>();
        foreach (var (filePath, entry) in _progress.MostRecent())
        {
            if (items.Count == MaxContinueWatchingItems)
                break;

            var media = ResolveMedia(filePath);
            if (media is null)
                continue;

            media.SetWatchProgress(entry.Position, entry.Duration);
            items.Add(media);
        }

        var unchanged = items.Count == ContinueWatching.Count
            && items.Zip(ContinueWatching).All(pair => ReferenceEquals(pair.First, pair.Second));
        if (unchanged)
            return;

        ContinueWatching = items;
        ContinueWatchingChanged?.Invoke();
        _ = LoadThumbnailsAsync(items);
    }

    // ---------- Collections ----------

    public bool IsInCollection(MediaCollection collection, MediaItem media)
        => collection.FilePaths.Contains(media.FilePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds <paramref name="media"/> to the end of <paramref name="collection"/> if it isn't there yet.</summary>
    public void AddToCollection(MediaCollection collection, MediaItem media)
    {
        if (IsInCollection(collection, media))
            return;
        collection.FilePaths.Add(media.FilePath);
        SaveCollections();
        CollectionsChanged?.Invoke();
    }

    public void RemoveFromCollection(MediaCollection collection, MediaItem media)
    {
        if (collection.FilePaths.RemoveAll(path => string.Equals(path, media.FilePath, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            SaveCollections();
            CollectionsChanged?.Invoke();
        }
    }

    /// <summary>Raised when a video is added to or removed from a collection.</summary>
    public event Action? CollectionsChanged;

    // ---------- Deleting ----------

    /// <summary>
    /// Moves the video file to the Recycle Bin and forgets it everywhere: library, collections,
    /// recently played and resume points. Throws if Windows refuses (file in use, no access).
    /// </summary>
    public async Task DeleteMediaAsync(MediaItem media)
    {
        var file = await StorageFile.GetFileFromPathAsync(media.FilePath);
        await file.DeleteAsync(StorageDeleteOption.Default);

        _allMedia.RemoveAll(item => string.Equals(item.FilePath, media.FilePath, StringComparison.OrdinalIgnoreCase));
        _oneOffMedia.Remove(media.FilePath);
        _backdrops.Remove(media.FilePath);
        _progress.Remove(media.FilePath);

        if (_recentFilePaths.RemoveAll(path => string.Equals(path, media.FilePath, StringComparison.OrdinalIgnoreCase)) > 0)
            AppStorage.SetString(RecentFilesSettingsKey, JsonSerializer.Serialize(_recentFilePaths, IrisJsonContext.Default.ListString));

        var inCollections = false;
        foreach (var collection in Collections)
            inCollections |= collection.FilePaths.RemoveAll(path => string.Equals(path, media.FilePath, StringComparison.OrdinalIgnoreCase)) > 0;
        if (inCollections)
        {
            SaveCollections();
            CollectionsChanged?.Invoke();
        }

        // Rescan so folder counts and artwork update; the folder watcher would also catch it, a moment later.
        await RefreshAsync(showBusy: false);
    }

    public void SaveCollections()
    {
        try
        {
            File.WriteAllText(GetCollectionsFilePath(), JsonSerializer.Serialize(Collections.ToList(), IrisJsonContext.Default.ListMediaCollection));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void LoadCollections()
    {
        var filePath = GetCollectionsFilePath();
        if (!File.Exists(filePath))
            return;

        try
        {
            foreach (var collection in JsonSerializer.Deserialize(File.ReadAllText(filePath), IrisJsonContext.Default.ListMediaCollection) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(collection.Name))
                {
                    collection.FilePaths ??= [];
                    Collections.Add(collection);
                }
            }
        }
        catch (JsonException)
        {
            Collections.Clear();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetCollectionsFilePath()
        => AppStorage.GetFilePath("media-collections.json");

    public void Dispose()
    {
        _refreshTimer.Stop();
        DisposeWatchers();
    }
}
