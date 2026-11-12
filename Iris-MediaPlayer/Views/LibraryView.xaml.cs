using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Iris_MediaPlayer.Views;

public enum LibraryMode
{
    Videos,
    Folders,
    Collections
}

/// <summary>
/// Library page with three modes that share one layout: a header with the mode's actions,
/// a breadcrumb when drilled in, a grid of folder or collection cards and a grid of videos.
/// </summary>
public sealed partial class LibraryView : UserControl
{
    private const string FolderGlyph = "";
    private const string CollectionGlyph = "";

    private LibraryStore? _store;
    private LibraryMode _mode = LibraryMode.Videos;
    private string _query = string.Empty;
    private string _sort = "Name";

    // Folders mode: the library folder being browsed and the subfolder currently shown.
    private string? _folderRoot;
    private string? _currentFolder;

    // Collections mode
    private MediaCollection? _selectedCollection;

    // What the empty message's button should do.
    private Action? _emptyAction;

    public LibraryView()
    {
        InitializeComponent();
    }

    public event EventHandler<PlayRequest>? PlayRequested;
    public event EventHandler? AddFolderRequested;

    /// <summary>Filter text from the app's search box.</summary>
    public string SearchText
    {
        get => _query;
        set
        {
            var query = value?.Trim() ?? string.Empty;
            if (string.Equals(query, _query, StringComparison.Ordinal))
                return;
            _query = query;
            RefreshItems();
        }
    }

    public void Initialize(LibraryStore store)
    {
        _store = store;
        store.LibraryChanged += OnLibraryChanged;
        store.ScanningChanged += isScanning =>
        {
            if (isScanning)
                PageSummary.Text = "Scanning your folders for videos…";
        };
        RefreshItems();
    }

    public void ShowMode(LibraryMode mode)
    {
        _mode = mode;
        if (mode == LibraryMode.Folders)
        {
            _folderRoot = null;
            _currentFolder = null;
        }
        else if (mode == LibraryMode.Collections)
        {
            _selectedCollection = null;
        }
        RefreshItems(scrollToTop: true);
    }

    private void OnLibraryChanged()
    {
        if (_store is null)
            return;

        // Drop folder-browser state that no longer exists after a rescan.
        if (_folderRoot is not null && !_store.FolderPaths.Contains(_folderRoot, StringComparer.OrdinalIgnoreCase))
        {
            _folderRoot = null;
            _currentFolder = null;
        }
        else if (_currentFolder is not null && !System.IO.Directory.Exists(_currentFolder))
        {
            _currentFolder = _folderRoot;
        }

        RefreshItems();
    }

    // ---------- Rendering ----------

    private void RefreshItems(bool scrollToTop = false)
    {
        if (_store is null)
            return;

        PageTitle.Text = _mode.ToString();
        SortPanel.Visibility = _mode == LibraryMode.Videos ? Visibility.Visible : Visibility.Collapsed;
        AddFolderButton.Visibility = _mode == LibraryMode.Folders ? Visibility.Visible : Visibility.Collapsed;
        NewCollectionButton.Visibility = _mode == LibraryMode.Collections && _selectedCollection is null
            ? Visibility.Visible : Visibility.Collapsed;
        EditCollectionButton.Visibility = _mode == LibraryMode.Collections && _selectedCollection is not null
            ? Visibility.Visible : Visibility.Collapsed;
        CollectionMoreButton.Visibility = EditCollectionButton.Visibility;

        switch (_mode)
        {
            case LibraryMode.Folders:
                RenderFolders();
                break;
            case LibraryMode.Collections:
                RenderCollections();
                break;
            default:
                RenderVideos();
                break;
        }

        if (scrollToTop)
            ContentScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>Fills the page. Pass null for a section that does not apply to this mode.</summary>
    private void Render(
        IReadOnlyList<LibraryTile>? folderTiles,
        IReadOnlyList<LibraryTile>? collectionTiles,
        IReadOnlyList<MediaItem> videos,
        string tilesHeader)
    {
        var tiles = folderTiles ?? collectionTiles ?? [];
        FolderTileItems.ItemsSource = folderTiles ?? [];
        CollectionTileItems.ItemsSource = collectionTiles ?? [];
        FolderTileItems.Visibility = folderTiles is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        CollectionTileItems.Visibility = collectionTiles is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        VideoItems.ItemsSource = videos;

        TilesSection.Visibility = tiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VideosSection.Visibility = videos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Section headings only help when both kinds of content are on screen.
        var showHeaders = tiles.Count > 0 && videos.Count > 0;
        TilesHeader.Text = tilesHeader;
        TilesHeader.Visibility = showHeaders ? Visibility.Visible : Visibility.Collapsed;
        VideosHeader.Visibility = showHeaders ? Visibility.Visible : Visibility.Collapsed;

        var isEmpty = tiles.Count == 0 && videos.Count == 0;
        ContentScroller.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyMessage.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEmpty(string glyph, string title, string description, string? actionText = null, Action? action = null)
    {
        EmptyMessage.Show(glyph, title, description, actionText);
        _emptyAction = action;
    }

    private bool Matches(MediaItem media)
        => string.IsNullOrEmpty(_query)
           || media.Title.Contains(_query, StringComparison.CurrentCultureIgnoreCase)
           || media.FolderName.Contains(_query, StringComparison.CurrentCultureIgnoreCase);

    private bool Matches(string name)
        => string.IsNullOrEmpty(_query) || name.Contains(_query, StringComparison.CurrentCultureIgnoreCase);

    private static string Count(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";

    // ---------- Videos mode ----------

    private void RenderVideos()
    {
        var store = _store!;
        Breadcrumb.Visibility = Visibility.Collapsed;

        IEnumerable<MediaItem> videos = store.AllMedia.Where(Matches);
        videos = _sort switch
        {
            "Added" => videos.OrderByDescending(media => media.LastModified),
            "Folder" => videos.OrderBy(media => media.FolderName, NaturalStringComparer.Instance)
                              .ThenBy(media => media.Title, NaturalStringComparer.Instance),
            _ => videos.OrderBy(media => media.Title, NaturalStringComparer.Instance)
        };
        var list = videos.ToList();

        PageSummary.Text = !string.IsNullOrEmpty(_query)
            ? $"{Count(list.Count, "result")} for “{_query}”"
            : store.AllMedia.Count == 0
                ? "Add folders to build your library."
                : $"{Count(store.AllMedia.Count, "video")} in {Count(store.FolderPaths.Count, "folder")}";

        if (store.FolderPaths.Count == 0)
            ShowEmpty(FolderGlyph, "Your library is empty", "Add a folder and Iris will find every video inside it.",
                "Add folder", () => AddFolderRequested?.Invoke(this, EventArgs.Empty));
        else if (!string.IsNullOrEmpty(_query))
            ShowEmpty("", "No matches", $"Nothing in your library matches “{_query}”.");
        else
            ShowEmpty("", "No videos yet", "Iris didn’t find any videos in your folders.",
                "Add folder", () => AddFolderRequested?.Invoke(this, EventArgs.Empty));

        Render(null, null, list, string.Empty);
    }

    // ---------- Folders mode ----------

    private void RenderFolders()
    {
        var store = _store!;

        if (_currentFolder is null || _folderRoot is null)
        {
            Breadcrumb.Visibility = Visibility.Collapsed;
            var roots = store.Folders.Where(folder => Matches(folder.Name)).Select(ToTile).ToList();
            PageSummary.Text = store.Folders.Count == 0
                ? "Folders you add appear here."
                : $"{Count(store.Folders.Count, "library folder")} · {Count(store.AllMedia.Count, "video")}";

            if (store.Folders.Count == 0)
                ShowEmpty(FolderGlyph, "No folders yet", "Add a folder and Iris will scan it, and everything inside it, for videos.",
                    "Add folder", () => AddFolderRequested?.Invoke(this, EventArgs.Empty));
            else
                ShowEmpty("", "No matches", $"No library folders match “{_query}”.");

            Render(roots, null, [], "Folders");
            return;
        }

        UpdateFolderBreadcrumb();
        var subfolders = store.GetChildFolders(_currentFolder).Where(folder => Matches(folder.Name)).Select(ToTile).ToList();
        var videos = store.AllMedia
            .Where(media => PathUtils.IsSamePath(media.FolderPath, _currentFolder) && Matches(media))
            .OrderBy(media => media.Title, NaturalStringComparer.Instance)
            .ToList();

        var parts = new List<string>();
        if (subfolders.Count > 0)
            parts.Add(Count(subfolders.Count, "folder"));
        parts.Add(Count(videos.Count, "video"));
        PageSummary.Text = string.Join(" · ", parts);

        ShowEmpty(FolderGlyph, "Nothing here",
            string.IsNullOrEmpty(_query) ? "This folder has no videos." : $"Nothing in this folder matches “{_query}”.");
        Render(subfolders, null, videos, "Folders");
    }

    private static LibraryTile ToTile(MediaFolder folder)
        => new(folder.Name, folder.MediaCountLabel, FolderGlyph, folder.Preview, folder);

    private void UpdateFolderBreadcrumb()
    {
        var crumbs = new List<Crumb> { new("All folders", null) };
        var rootName = PathUtils.GetFolderName(_folderRoot!);
        crumbs.Add(new Crumb(string.IsNullOrWhiteSpace(rootName) ? _folderRoot! : rootName, _folderRoot));

        var relative = System.IO.Path.GetRelativePath(_folderRoot!, _currentFolder!);
        if (relative != ".")
        {
            var path = _folderRoot!;
            foreach (var segment in relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                path = System.IO.Path.Combine(path, segment);
                crumbs.Add(new Crumb(segment, path));
            }
        }

        Breadcrumb.ItemsSource = crumbs;
        Breadcrumb.Visibility = Visibility.Visible;
    }

    // ---------- Collections mode ----------

    private void RenderCollections()
    {
        var store = _store!;

        if (_selectedCollection is null)
        {
            Breadcrumb.Visibility = Visibility.Collapsed;
            var tiles = store.Collections
                .Where(collection => Matches(collection.Name))
                .Select(collection => new LibraryTile(collection.Name, collection.VideoCountLabel, CollectionGlyph,
                    collection.FilePaths.Select(store.ResolveMedia).FirstOrDefault(media => media is not null), collection))
                .ToList();
            PageSummary.Text = store.Collections.Count == 0
                ? "Playlists you build from any of your videos."
                : Count(store.Collections.Count, "collection");

            if (store.Collections.Count == 0)
                ShowEmpty(CollectionGlyph, "No collections yet",
                    "Collections are playlists you build from any videos, wherever they’re stored.",
                    "New collection", CreateCollection);
            else
                ShowEmpty("", "No matches", $"No collections match “{_query}”.");

            Render(null, tiles, [], "Collections");
            return;
        }

        Breadcrumb.ItemsSource = new List<Crumb> { new("All collections", null), new(_selectedCollection.Name, _selectedCollection.Name) };
        Breadcrumb.Visibility = Visibility.Visible;

        var videos = GetCollectionMedia(_selectedCollection).Where(Matches).ToList();
        PageSummary.Text = Count(_selectedCollection.FilePaths.Count, "video");

        if (_selectedCollection.FilePaths.Count == 0)
            ShowEmpty(CollectionGlyph, "This collection is empty", "Add videos from anywhere in your library.",
                "Edit videos", EditCollectionVideos);
        else
            ShowEmpty("", "No matches", $"Nothing in this collection matches “{_query}”.");

        Render(null, null, videos, string.Empty);
    }

    private List<MediaItem> GetCollectionMedia(MediaCollection collection)
        => collection.FilePaths.Select(_store!.ResolveMedia).OfType<MediaItem>().ToList();

    private async void CreateCollection()
    {
        if (_store is null)
            return;

        var name = await PromptForNameAsync("New collection", "Create", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var collection = new MediaCollection { Name = name };
        _store.Collections.Add(collection);
        _store.SaveCollections();
        _selectedCollection = collection;
        RefreshItems(scrollToTop: true);
    }

    private void EditCollectionVideos()
    {
        if (_selectedCollection is not null)
            EditCollectionVideos(_selectedCollection);
    }

    private async void EditCollectionVideos(MediaCollection collection)
    {
        if (_store is null)
            return;

        var availableMedia = _store.GetAllKnownMedia();
        if (availableMedia.Count == 0)
            return;

        var picker = new ListView
        {
            ItemsSource = availableMedia,
            DisplayMemberPath = nameof(MediaItem.Title),
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 440
        };
        foreach (var media in availableMedia.Where(media =>
                     collection.FilePaths.Contains(media.FilePath, StringComparer.OrdinalIgnoreCase)))
            picker.SelectedItems.Add(media);

        var dialog = new ContentDialog
        {
            Title = $"Videos in {collection.Name}",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Pick videos from any folder or recently opened files. The files stay where they are.",
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["IrisBodySecondaryTextStyle"]
                    },
                    picker
                }
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        collection.FilePaths = picker.SelectedItems.OfType<MediaItem>().Select(media => media.FilePath).ToList();
        _store.SaveCollections();
        RefreshItems();
    }

    private async System.Threading.Tasks.Task<string?> PromptForNameAsync(string title, string primaryText, string initialName)
    {
        var nameBox = new TextBox { PlaceholderText = "Collection name", Text = initialName };
        nameBox.SelectAll();
        var dialog = new ContentDialog
        {
            Title = title,
            Content = nameBox,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? nameBox.Text.Trim() : null;
    }

    // ---------- Card and header actions ----------

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LibraryTile tile })
            OpenTile(tile);
    }

    private void OpenTile(LibraryTile tile)
    {
        switch (tile.Source)
        {
            case MediaFolder folder:
                _folderRoot ??= folder.Path;
                _currentFolder = folder.Path;
                break;
            case MediaCollection collection:
                _selectedCollection = collection;
                break;
        }
        RefreshItems(scrollToTop: true);
    }

    private void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is not Crumb crumb)
            return;

        if (_mode == LibraryMode.Folders)
        {
            if (crumb.Key is null)
            {
                _folderRoot = null;
                _currentFolder = null;
            }
            else
            {
                _currentFolder = crumb.Key;
            }
        }
        else if (_mode == LibraryMode.Collections && crumb.Key is null)
        {
            _selectedCollection = null;
        }
        RefreshItems(scrollToTop: true);
    }

    private void MediaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filePath } || _store?.ResolveMedia(filePath) is not { } media)
            return;

        // Inside a collection, next/previous follow the collection's order. Everywhere else
        // the player falls back to the other videos in the same folder.
        var queue = _mode == LibraryMode.Collections && _selectedCollection is not null
            ? GetCollectionMedia(_selectedCollection)
            : null;
        PlayRequested?.Invoke(this, new PlayRequest(media, queue));
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo.SelectedItem is ComboBoxItem { Tag: string sort })
        {
            _sort = sort;
            RefreshItems();
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e) => AddFolderRequested?.Invoke(this, EventArgs.Empty);

    private void CreateCollection_Click(object sender, RoutedEventArgs e) => CreateCollection();

    private void EditCollectionVideos_Click(object sender, RoutedEventArgs e) => EditCollectionVideos();

    private void EmptyMessage_ActionClick(object? sender, EventArgs e) => _emptyAction?.Invoke();

    // ---------- Card menus ----------
    // Menus are built fresh for each card. A single MenuFlyout shared by many buttons in a
    // template can open empty in WinUI, and building per card lets each show only what applies.

    private void TileMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryTile tile } target)
            BuildTileMenu(tile)?.ShowAt(target, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private void Tile_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: LibraryTile tile } target || BuildTileMenu(tile) is not { } menu)
            return;

        var options = new FlyoutShowOptions();
        if (args.TryGetPosition(target, out var point))
            options.Position = point;
        menu.ShowAt(target, options);
        args.Handled = true;
    }

    private void CollectionMore_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCollection is not null && sender is FrameworkElement target)
            BuildCollectionMenu(_selectedCollection, includeOpen: false)
                .ShowAt(target, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private MenuFlyout? BuildTileMenu(LibraryTile tile) => tile.Source switch
    {
        MediaFolder folder => BuildFolderMenu(tile, folder),
        MediaCollection collection => BuildCollectionMenu(collection, includeOpen: true),
        _ => null
    };

    private MenuFlyout BuildFolderMenu(LibraryTile tile, MediaFolder folder)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(CreateMenuItem("Open", "", () => OpenTile(tile)));
        menu.Items.Add(CreateMenuItem("Show in File Explorer", "",
            async () => await Windows.System.Launcher.LaunchFolderPathAsync(folder.Path)));

        // Only top-level library folders can be removed; subfolders come with their parent.
        if (_store?.FolderPaths.Contains(folder.Path, StringComparer.OrdinalIgnoreCase) == true)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(CreateMenuItem("Remove from library", "", () => RemoveFolder(folder)));
        }
        return menu;
    }

    private MenuFlyout BuildCollectionMenu(MediaCollection collection, bool includeOpen)
    {
        var menu = new MenuFlyout();
        if (includeOpen)
        {
            menu.Items.Add(CreateMenuItem("Open", "", () =>
            {
                _selectedCollection = collection;
                RefreshItems(scrollToTop: true);
            }));
            menu.Items.Add(CreateMenuItem("Edit videos", "", () => EditCollectionVideos(collection)));
        }
        menu.Items.Add(CreateMenuItem("Rename", "", () => RenameCollection(collection)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("Delete collection", "", () => DeleteCollection(collection)));
        return menu;
    }

    private static MenuFlyoutItem CreateMenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private async void RemoveFolder(MediaFolder folder)
    {
        if (_store is null)
            return;

        var dialog = new ContentDialog
        {
            Title = $"Remove “{folder.Name}” from your library?",
            Content = "Iris will stop showing its videos. Nothing is deleted from your computer, and you can add the folder again any time.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await _store.RemoveFolderAsync(folder.Path);
    }

    private async void RenameCollection(MediaCollection collection)
    {
        if (_store is null)
            return;

        var name = await PromptForNameAsync("Rename collection", "Rename", collection.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        collection.Name = name;
        _store.SaveCollections();
        RefreshItems();
    }

    private async void DeleteCollection(MediaCollection collection)
    {
        if (_store is null)
            return;

        var dialog = new ContentDialog
        {
            Title = $"Delete “{collection.Name}”?",
            Content = "The videos stay where they are. Only the collection is removed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _store.Collections.Remove(collection);
        _store.SaveCollections();
        if (ReferenceEquals(_selectedCollection, collection))
            _selectedCollection = null;
        RefreshItems(scrollToTop: true);
    }

    /// <summary>A breadcrumb step. <see cref="Key"/> is the folder path or collection name; null is the top level.</summary>
    private sealed record Crumb(string Label, string? Key)
    {
        public override string ToString() => Label;
    }
}
