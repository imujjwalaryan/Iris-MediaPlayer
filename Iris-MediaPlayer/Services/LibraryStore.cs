using Iris_MediaPlayer.Models;
using Microsoft.UI.Dispatching;
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

    private readonly List<string> _folderPaths = [];
    private readonly List<MediaItem> _allMedia = [];
    private readonly List<string> _recentFilePaths = [];
    private readonly Dictionary<string, MediaItem> _oneOffMedia = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _folderWatchers = [];
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _refreshTimer;

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
    }

    public IReadOnlyList<string> FolderPaths => _folderPaths;
    public IReadOnlyList<MediaItem> AllMedia => _allMedia;
    public ObservableCollection<MediaFolder> Folders { get; } = [];
    public ObservableCollection<MediaCollection> Collections { get; } = [];
    public IReadOnlyList<MediaItem> RecentMedia { get; private set; } = [];

    /// <summary>Raised after a folder scan finishes and <see cref="AllMedia"/> is up to date.</summary>
    public event Action? LibraryChanged;

    /// <summary>Raised when <see cref="RecentMedia"/> changes.</summary>
    public event Action? RecentMediaChanged;

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
                var count = _allMedia.Count(media => string.Equals(media.LibraryRootPath, path, StringComparison.OrdinalIgnoreCase));
                Folders.Add(new MediaFolder(path, PathUtils.GetFolderName(path), count));
            }

            LibraryChanged?.Invoke();
            RefreshRecentMedia();
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
                .Select(path => new MediaFolder(path, PathUtils.GetFolderName(path),
                    _allMedia.Count(media => PathUtils.IsPathWithin(media.FolderPath, path))))
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
                media.SetTechnicalDetails(properties.Width, properties.Height, properties.Bitrate);

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

        ApplicationData.Current.LocalSettings.Values[RecentFilesSettingsKey] = JsonSerializer.Serialize(_recentFilePaths);
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
        var saved = ApplicationData.Current.LocalSettings.Values[RecentFilesSettingsKey] as string;
        if (string.IsNullOrWhiteSpace(saved))
            return;

        try
        {
            _recentFilePaths.AddRange(JsonSerializer.Deserialize<List<string>>(saved) ?? []);
        }
        catch (JsonException)
        {
            ApplicationData.Current.LocalSettings.Values.Remove(RecentFilesSettingsKey);
        }
    }

    // ---------- Collections ----------

    public void SaveCollections()
    {
        try
        {
            File.WriteAllText(GetCollectionsFilePath(), JsonSerializer.Serialize(Collections));
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
            foreach (var collection in JsonSerializer.Deserialize<List<MediaCollection>>(File.ReadAllText(filePath)) ?? [])
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
        => Path.Combine(ApplicationData.Current.LocalFolder.Path, "media-collections.json");

    public void Dispose()
    {
        _refreshTimer.Stop();
        DisposeWatchers();
    }
}
