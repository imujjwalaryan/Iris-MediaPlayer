using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Services.Jellyfin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Views.Jellyfin;

/// <summary>
/// A Jellyfin library (or Favorites across all of them) as a poster grid, with filter chips, sort
/// and a breadcrumb for folders and collections opened inside it. Films, shows and episodes are
/// handed back to the Jellyfin page through <see cref="ItemInvoked"/>.
/// </summary>
public sealed partial class JellyfinLibraryView : UserControl
{
    private JellyfinClient? _client;
    /// <summary>The library, then folders or collections opened inside it. Empty for Favorites.</summary>
    private readonly List<JellyfinItem> _path = [];
    private JellyfinFilter _filter = JellyfinFilter.All;
    private JellyfinSort _sort = JellyfinSort.Name;
    private bool _favoritesOnly;
    private CancellationTokenSource? _loading;

    public JellyfinLibraryView()
    {
        InitializeComponent();
    }

    /// <summary>A film, show or video was clicked.</summary>
    public event EventHandler<JellyfinItem>? ItemInvoked;

    public string? LibraryId => _favoritesOnly ? null : _path.FirstOrDefault()?.Id;

    /// <summary>Shows <paramref name="library"/>, or every favorite when it's null.</summary>
    public Task ShowAsync(JellyfinClient client, JellyfinItem? library)
    {
        _client = client;
        _favoritesOnly = library is null;
        _path.Clear();
        if (library is not null)
            _path.Add(library);
        _filter = _favoritesOnly ? JellyfinFilter.Favorites : JellyfinFilter.All;
        FavoritesChip.Visibility = _favoritesOnly ? Visibility.Collapsed : Visibility.Visible;
        UpdateChips();
        return LoadAsync();
    }

    /// <summary>Loads the current folder again (after Refresh, or coming back from a details page).</summary>
    public Task ReloadAsync() => LoadAsync(keepScroll: true);

    private async Task LoadAsync(bool keepScroll = false)
    {
        if (_client is null)
            return;

        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        Message.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        UpdateHeader(count: null);
        var offset = ItemsScroller.VerticalOffset;
        try
        {
            var parent = _path.Count > 0 ? _path[^1] : null;
            var items = await _client.GetLibraryItemsAsync(parent, _filter, _sort, cancel.Token);
            if (cancel.IsCancellationRequested)
                return;

            var wide = items.Count > 0 && items.All(item => item.IsWide);
            JellyfinUi.LoadCardImages(_client, items, wide);
            PosterItems.ItemsSource = wide ? null : items;
            WideItems.ItemsSource = wide ? items : null;
            PosterItems.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            WideItems.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            ItemsScroller.ChangeView(null, keepScroll ? offset : 0, null, disableAnimation: true);
            UpdateHeader(items.Count);

            if (items.Count == 0)
            {
                Message.Show("", _filter == JellyfinFilter.All ? "Nothing here yet" : "Nothing matches",
                    _filter switch
                    {
                        JellyfinFilter.Unwatched => "You've watched everything here.",
                        JellyfinFilter.InProgress => "Nothing started and unfinished.",
                        JellyfinFilter.Favorites => "Mark films and shows as favorites with the heart on their page.",
                        _ => "This library or folder has no videos."
                    });
                Message.Visibility = Visibility.Visible;
            }
        }
        catch (JellyfinException exception) when (!cancel.IsCancellationRequested)
        {
            PosterItems.ItemsSource = null;
            WideItems.ItemsSource = null;
            Message.Show("", "Couldn't load this", exception.Message, "Try again");
            Message.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_loading, cancel))
                LoadingRing.IsActive = false;
        }
    }

    private void UpdateHeader(int? count)
    {
        Eyebrow.Text = (JellyfinSession.ServerName ?? "Jellyfin").ToUpperInvariant();
        PageTitle.Text = _favoritesOnly ? "Favorites" : _path.Count > 0 ? _path[^1].Name : "Library";
        if (count is { } total)
        {
            var noun = _path.FirstOrDefault()?.CollectionType switch
            {
                "movies" when _path.Count == 1 => total == 1 ? "film" : "films",
                "tvshows" when _path.Count == 1 => total == 1 ? "show" : "shows",
                _ => total == 1 ? "item" : "items"
            };
            CountText.Text = $"{total} {noun}" + (_filter == JellyfinFilter.All || _favoritesOnly ? string.Empty : " shown");
        }
        Breadcrumb.ItemsSource = _path.Count > 1 ? _path.Select(item => item.Name).ToList() : null;
    }

    private void Items_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        => Motion.EnterFromBelow(args.Element, TimeSpan.FromMilliseconds(Math.Min(args.Index, 16) * 22), 14);

    private async void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JellyfinItem item })
            return;

        // Folders and collections open here; films, shows and videos go to the Jellyfin page.
        if (item.IsFolder && item.Type is "Folder" or "CollectionFolder" or "BoxSet" or "UserView")
        {
            _path.Add(item);
            _filter = JellyfinFilter.All;
            UpdateChips();
            await LoadAsync();
            return;
        }
        ItemInvoked?.Invoke(this, item);
    }

    private async void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _path.Count - 1)
            return;
        _path.RemoveRange(args.Index + 1, _path.Count - args.Index - 1);
        await LoadAsync();
    }

    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<JellyfinFilter>(tag, out var filter))
        {
            _filter = filter;
            UpdateChips();
            await LoadAsync();
        }
    }

    private void UpdateChips()
    {
        foreach (var chip in Filters.Children.OfType<ToggleButton>())
            chip.IsChecked = chip.Tag is string tag && tag == _filter.ToString();
    }

    private async void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag } item && Enum.TryParse<JellyfinSort>(tag, out var sort))
        {
            _sort = sort;
            SortText.Text = "Sort: " + item.Text;
            await LoadAsync();
        }
    }

    private async void Message_ActionClick(object? sender, EventArgs e) => await LoadAsync();
}
