using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace Iris_MediaPlayer.Views;

public enum LibraryMode
{
    Videos,
    Folders,
    Collections
}

/// <summary>Library page with three modes: all videos, a folder browser and collections.</summary>
public sealed partial class LibraryView : UserControl
{
    private LibraryStore? _store;
    private LibraryMode _mode = LibraryMode.Videos;
    private string? _selectedFolderRoot;
    private string? _currentFolderPath;
    private MediaCollection? _selectedCollection;

    public LibraryView()
    {
        InitializeComponent();
    }

    public event EventHandler<MediaItem>? PlayRequested;
    public event EventHandler? OpenVideoRequested;
    public event EventHandler? AddFolderRequested;
    public event EventHandler<string>? SearchTextChanged;

    /// <summary>Keeps this search box in sync with the home page's search box.</summary>
    public string SearchText
    {
        get => LibrarySearchBox.Text;
        set
        {
            if (!string.Equals(LibrarySearchBox.Text, value, StringComparison.Ordinal))
                LibrarySearchBox.Text = value;
        }
    }

    public void Initialize(LibraryStore store)
    {
        _store = store;
        CollectionTiles.ItemsSource = store.Collections;
        store.LibraryChanged += OnLibraryChanged;
        store.ScanningChanged += OnScanningChanged;
        RefreshItems();
    }

    public void ShowMode(LibraryMode mode)
    {
        _mode = mode;
        LibraryTitle.Text = mode == LibraryMode.Videos ? "Library" : mode.ToString();
        if (mode == LibraryMode.Folders)
        {
            _selectedFolderRoot = null;
            _currentFolderPath = null;
        }
        else if (mode == LibraryMode.Collections)
        {
            _selectedCollection = null;
        }
        RefreshItems();
    }

    private void OnLibraryChanged()
    {
        if (_store is null)
            return;

        // Drop folder-browser state that no longer exists after a rescan.
        if (_selectedFolderRoot is not null && !_store.FolderPaths.Contains(_selectedFolderRoot, StringComparer.OrdinalIgnoreCase))
        {
            _selectedFolderRoot = null;
            _currentFolderPath = null;
        }
        else if (_currentFolderPath is not null && !System.IO.Directory.Exists(_currentFolderPath))
        {
            _currentFolderPath = _selectedFolderRoot;
        }

        RefreshItems();
    }

    private void OnScanningChanged(bool isScanning)
    {
        if (isScanning)
            LibrarySummary.Text = "Scanning folders for video files…";
    }

    private void RefreshItems()
    {
        if (_store is null)
            return;

        var query = LibrarySearchBox.Text.Trim();
        switch (_mode)
        {
            case LibraryMode.Folders:
                RefreshFolderBrowser(query);
                break;
            case LibraryMode.Collections:
                RefreshCollectionsView(query);
                break;
            default:
                RefreshAllVideos(query);
                break;
        }

        var mediaCount = _store.AllMedia.Count;
        var folderCount = _store.FolderPaths.Count;
        LibrarySummary.Text = mediaCount == 0
            ? "Add folders to discover media on this device."
            : $"{mediaCount:N0} video{(mediaCount == 1 ? "" : "s")} in {folderCount:N0} folder{(folderCount == 1 ? "" : "s")}.";
    }

    private static bool MatchesQuery(MediaItem media, string query)
        => string.IsNullOrWhiteSpace(query)
            || media.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || media.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    // ---------- Videos mode ----------

    private void RefreshAllVideos(string query)
    {
        // Always hand the repeater a new snapshot; AllMedia is a plain list that does not
        // notify WinUI when a folder scan clears and repopulates it.
        var matches = _store!.AllMedia.Where(media => MatchesQuery(media, query)).ToList();

        FolderBrowserPanel.Visibility = Visibility.Collapsed;
        CollectionsPanel.Visibility = Visibility.Collapsed;
        CollectionEmpty.Visibility = Visibility.Collapsed;
        LibraryItems.Visibility = Visibility.Visible;
        LibraryItems.ItemsSource = matches;
        LibraryEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmpty.Text = !string.IsNullOrWhiteSpace(query)
            ? "No video titles match your search."
            : "Your library is empty. Add a folder to scan for video files.";
    }

    // ---------- Folders mode ----------

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
            FolderTiles.ItemsSource = _store!.Folders.ToList();
            FolderBrowserEmpty.Visibility = _store.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LibraryItems.ItemsSource = Array.Empty<MediaItem>();
            LibraryEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        FolderBrowserEmpty.Visibility = Visibility.Collapsed;
        FolderBrowserBackButton.Visibility = Visibility.Visible;
        var relativePath = System.IO.Path.GetRelativePath(_selectedFolderRoot, _currentFolderPath);
        var rootName = PathUtils.GetFolderName(_selectedFolderRoot);
        if (string.IsNullOrWhiteSpace(rootName))
            rootName = _selectedFolderRoot;
        FolderBreadcrumb.Text = relativePath == "." ? rootName : $"{rootName}  /  {relativePath.Replace('\\', '/')}";
        FolderTiles.ItemsSource = _store!.GetChildFolders(_currentFolderPath);

        var videos = _store.AllMedia
            .Where(media => PathUtils.IsSamePath(media.FolderPath, _currentFolderPath) && MatchesQuery(media, query))
            .ToList();
        LibraryItems.ItemsSource = videos;
        LibraryEmpty.Visibility = videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmpty.Text = string.IsNullOrWhiteSpace(query)
            ? "This folder has no videos. Open a subfolder or go back to browse another folder."
            : "No videos in this folder match your search.";
    }

    private void Subfolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;

        _selectedFolderRoot ??= path;
        _currentFolderPath = path;
        RefreshItems();
    }

    private void FolderBrowserBack_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFolderRoot is null || _currentFolderPath is null)
            return;

        if (PathUtils.IsSamePath(_currentFolderPath, _selectedFolderRoot))
        {
            _selectedFolderRoot = null;
            _currentFolderPath = null;
        }
        else
        {
            var parent = System.IO.Path.GetDirectoryName(_currentFolderPath);
            _currentFolderPath = parent is not null && PathUtils.IsPathWithin(parent, _selectedFolderRoot)
                ? parent
                : _selectedFolderRoot;
        }

        RefreshItems();
    }

    // ---------- Collections mode ----------

    private void RefreshCollectionsView(string query)
    {
        var store = _store!;
        var isOverview = _selectedCollection is null;

        FolderBrowserPanel.Visibility = Visibility.Collapsed;
        CollectionsPanel.Visibility = Visibility.Visible;
        CollectionTiles.Visibility = isOverview ? Visibility.Visible : Visibility.Collapsed;
        CollectionEmpty.Visibility = isOverview && store.Collections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CollectionsBackButton.Visibility = isOverview ? Visibility.Collapsed : Visibility.Visible;
        CollectionHeading.Visibility = isOverview ? Visibility.Visible : Visibility.Collapsed;
        SelectedCollectionName.Visibility = isOverview ? Visibility.Collapsed : Visibility.Visible;
        SelectedCollectionCount.Visibility = isOverview ? Visibility.Collapsed : Visibility.Visible;
        AddCollectionVideosButton.Visibility = isOverview ? Visibility.Collapsed : Visibility.Visible;
        CreateCollectionButton.Visibility = isOverview ? Visibility.Visible : Visibility.Collapsed;
        DeleteCollectionButton.Visibility = isOverview ? Visibility.Collapsed : Visibility.Visible;

        if (_selectedCollection is null)
        {
            LibraryItems.Visibility = Visibility.Collapsed;
            LibraryEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        var videos = _selectedCollection.FilePaths
            .Select(store.ResolveMedia)
            .Where(media => media is not null)
            .Cast<MediaItem>()
            .Where(media => MatchesQuery(media, query))
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
            RefreshItems();
        }
    }

    private void CollectionsBack_Click(object sender, RoutedEventArgs e)
    {
        _selectedCollection = null;
        RefreshItems();
    }

    private async void CreateCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
            return;

        var nameBox = new TextBox { PlaceholderText = "Collection name" };
        var dialog = new ContentDialog
        {
            Title = "Create collection",
            Content = nameBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var name = nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        var collection = new MediaCollection { Name = name };
        _store.Collections.Add(collection);
        _selectedCollection = collection;
        _store.SaveCollections();
        RefreshItems();
    }

    private async void AddCollectionVideos_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _selectedCollection is null)
            return;

        var availableMedia = _store.GetAllKnownMedia();
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
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _selectedCollection.FilePaths = picker.SelectedItems.OfType<MediaItem>()
            .Select(media => media.FilePath)
            .ToList();

        _store.SaveCollections();
        RefreshItems();
    }

    private void DeleteCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _selectedCollection is null)
            return;

        _store.Collections.Remove(_selectedCollection);
        _selectedCollection = null;
        _store.SaveCollections();
        RefreshItems();
    }

    // ---------- Shared actions ----------

    private void MediaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filePath } && _store?.ResolveMedia(filePath) is { } media)
            PlayRequested?.Invoke(this, media);
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e) => OpenVideoRequested?.Invoke(this, EventArgs.Empty);

    private void AddFolder_Click(object sender, RoutedEventArgs e) => AddFolderRequested?.Invoke(this, EventArgs.Empty);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshItems();
        SearchTextChanged?.Invoke(this, LibrarySearchBox.Text);
    }
}
