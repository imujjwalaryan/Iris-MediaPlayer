using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// Home page: a featured video (the one to resume, or the newest) with full-width artwork,
/// then rows for Continue watching, Recently added and Recently played.
/// </summary>
public sealed partial class HomeView : UserControl
{
    private const int RowSize = 16;

    private LibraryStore? _store;
    private MediaItem? _featured;
    private bool _hasScanned;

    public HomeView()
    {
        InitializeComponent();
    }

    public event EventHandler<PlayRequest>? PlayRequested;
    public event EventHandler? OpenVideoRequested;
    public event EventHandler? AddFolderRequested;
    public event EventHandler? BrowseLibraryRequested;

    public void Initialize(LibraryStore store)
    {
        _store = store;
        store.LibraryChanged += () =>
        {
            _hasScanned = true;
            Refresh();
        };
        store.RecentMediaChanged += Refresh;
        store.ContinueWatchingChanged += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        if (_store is null)
            return;

        var continueWatching = _store.ContinueWatching;
        var recentlyAdded = _store.GetRecentlyAdded(RowSize);
        var recentlyPlayed = _store.RecentMedia;

        ShowRow(ContinueSection, ContinueItems, continueWatching);
        ShowRow(AddedSection, AddedItems, recentlyAdded);
        ShowRow(RecentSection, RecentItems, recentlyPlayed);

        var resume = continueWatching.FirstOrDefault();
        UpdateHero(resume ?? recentlyAdded.FirstOrDefault() ?? recentlyPlayed.FirstOrDefault(), isResume: resume is not null);
    }

    private static void ShowRow(FrameworkElement section, ItemsRepeater items, System.Collections.Generic.IReadOnlyList<MediaItem> media)
    {
        items.ItemsSource = media;
        section.Visibility = media.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Hero ----------

    private async void UpdateHero(MediaItem? featured, bool isResume)
    {
        if (featured is null)
        {
            _featured = null;
            Hero.Visibility = Visibility.Collapsed;
            UpdateWelcome();
            return;
        }

        WelcomeState.Visibility = Visibility.Collapsed;
        Hero.Visibility = Visibility.Visible;
        HeroEyebrow.Text = isResume ? "CONTINUE WATCHING" : "RECENTLY ADDED";
        HeroTitle.Text = featured.Title;
        HeroMeta.Text = $"{featured.FolderName}  ·  {featured.FileFormat}  ·  {featured.LastModified:d}";
        HeroPlayText.Text = isResume ? "Resume" : "Play";
        HeroProgressPanel.Visibility = isResume ? Visibility.Visible : Visibility.Collapsed;
        HeroProgress.Value = featured.WatchProgress;
        HeroRemaining.Text = featured.WatchRemaining;

        if (ReferenceEquals(featured, _featured))
            return;

        _featured = featured;
        HeroImage.Source = featured.Thumbnail;
        var backdrop = await _store!.GetBackdropAsync(featured);
        if (ReferenceEquals(featured, _featured) && backdrop is not null)
            HeroImage.Source = backdrop;
    }

    private void UpdateWelcome()
    {
        // Avoid flashing "no videos" while the first scan is still running.
        if (_store is null || (!_hasScanned && _store.FolderPaths.Count > 0))
        {
            WelcomeState.Visibility = Visibility.Collapsed;
            return;
        }

        if (_store.FolderPaths.Count == 0)
        {
            WelcomeState.Show("", "Welcome to Iris",
                "Add a folder of videos and Iris will build your library. You can also open a single file to play it right away.",
                "Add folder", "Open video");
        }
        else
        {
            WelcomeState.Show("", "No videos found yet",
                "Iris didn’t find any videos in your folders. Add another folder, or open a single file to play it.",
                "Add folder", "Open video");
        }
        WelcomeState.Visibility = Visibility.Visible;
    }

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_featured is not null)
            PlayRequested?.Invoke(this, new PlayRequest(_featured));
    }

    private void BrowseLibrary_Click(object sender, RoutedEventArgs e) => BrowseLibraryRequested?.Invoke(this, EventArgs.Empty);

    private void WelcomeAction_Click(object? sender, EventArgs e) => AddFolderRequested?.Invoke(this, EventArgs.Empty);

    private void WelcomeSecondaryAction_Click(object? sender, EventArgs e) => OpenVideoRequested?.Invoke(this, EventArgs.Empty);

    // ---------- Rows ----------

    private void MediaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filePath } && _store?.ResolveMedia(filePath) is { } media)
            PlayRequested?.Invoke(this, new PlayRequest(media));
    }

    private void ContinueCard_Click(object sender, RoutedEventArgs e)
    {
        // Use the Continue watching instance so its saved progress is the one resumed.
        if (sender is not Button { Tag: string filePath } || _store is null)
            return;

        var media = _store.ContinueWatching.FirstOrDefault(item =>
                        string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    ?? _store.ResolveMedia(filePath);
        if (media is not null)
            PlayRequested?.Invoke(this, new PlayRequest(media));
    }

    private void ScrollRowBack_Click(object sender, RoutedEventArgs e) => ScrollRow(sender, -1);

    private void ScrollRowForward_Click(object sender, RoutedEventArgs e) => ScrollRow(sender, 1);

    private void ScrollRow(object sender, int direction)
    {
        if (sender is FrameworkElement { Tag: string name } && FindName(name) is ScrollViewer scroller)
            scroller.ChangeView(scroller.HorizontalOffset + direction * scroller.ViewportWidth * 0.8, null, null);
    }
}
