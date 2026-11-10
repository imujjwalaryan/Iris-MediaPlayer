using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace Iris_MediaPlayer.Views;

/// <summary>Home page: featured video, recently played, saved folders and quick actions.</summary>
public sealed partial class HomeView : UserControl
{
    private LibraryStore? _store;
    private MediaItem? _featured;

    public HomeView()
    {
        InitializeComponent();
    }

    public event EventHandler<MediaItem>? PlayRequested;
    public event EventHandler? OpenVideoRequested;
    public event EventHandler? AddFolderRequested;
    public event EventHandler? BrowseLibraryRequested;
    public event EventHandler<string>? SearchTextChanged;

    /// <summary>Keeps this search box in sync with the library page's search box.</summary>
    public string SearchText
    {
        get => SearchBox.Text;
        set
        {
            if (!string.Equals(SearchBox.Text, value, StringComparison.Ordinal))
                SearchBox.Text = value;
        }
    }

    public void Initialize(LibraryStore store)
    {
        _store = store;
        FolderList.ItemsSource = store.Folders;
        store.LibraryChanged += OnLibraryChanged;
        store.RecentMediaChanged += OnRecentMediaChanged;
        OnLibraryChanged();
        OnRecentMediaChanged();
    }

    private void OnLibraryChanged()
    {
        if (_store is null)
            return;

        FoldersEmpty.Visibility = _store.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFeatured();
    }

    private void OnRecentMediaChanged()
    {
        if (_store is null)
            return;

        var recent = _store.RecentMedia;
        RecentItems.ItemsSource = recent;
        RecentlyPlayedEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentItems.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateFeatured()
    {
        _featured = _store?.AllMedia.OrderByDescending(media => media.LastModified).FirstOrDefault();
        FeaturedImage.DataContext = _featured;

        if (_featured is null)
        {
            FeaturedTitle.Text = "Your next favorite starts here";
            FeaturedSubtitle.Text = "Add a folder and Iris will discover your movies and shows.";
            FeaturedPlayButton.IsEnabled = false;
            return;
        }

        FeaturedTitle.Text = _featured.Title;
        FeaturedSubtitle.Text = $"In {_featured.FolderName}  ·  {_featured.FileFormat}  ·  Added {_featured.LastModified:d}";
        FeaturedPlayButton.IsEnabled = true;
    }

    private void FeaturedPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_featured is not null)
            PlayRequested?.Invoke(this, _featured);
    }

    private void MediaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filePath } && _store?.ResolveMedia(filePath) is { } media)
            PlayRequested?.Invoke(this, media);
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && _store is not null)
            await _store.RemoveFolderAsync(path);
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e) => OpenVideoRequested?.Invoke(this, EventArgs.Empty);

    private void AddFolder_Click(object sender, RoutedEventArgs e) => AddFolderRequested?.Invoke(this, EventArgs.Empty);

    private void LibraryShortcut_Click(object sender, RoutedEventArgs e) => BrowseLibraryRequested?.Invoke(this, EventArgs.Empty);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => SearchTextChanged?.Invoke(this, SearchBox.Text);
}
