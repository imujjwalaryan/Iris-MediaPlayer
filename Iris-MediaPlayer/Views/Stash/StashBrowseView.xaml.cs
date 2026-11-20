using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Services.Stash;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Views.Stash;

/// <summary>
/// One Stash list: scenes (with filters, sort and an optional performer/studio/tag scope), or
/// performers, studios or tags. Searches as you type and loads more as you scroll.
/// </summary>
public sealed partial class StashBrowseView : UserControl
{
    private const int PageSize = 60;

    private readonly ObservableCollection<StashItem> _items = [];
    private readonly DispatcherQueueTimer _searchTimer;
    private StashClient? _client;
    private StashKind _kind = StashKind.Scene;
    private StashScope? _scope;
    private StashSceneFilter _filter = StashSceneFilter.All;
    private StashSceneSort _sort = StashSceneSort.RecentlyAdded;
    private int _page;
    private int _total;
    private bool _isLoadingMore;
    private CancellationTokenSource? _loading;
    private bool _isResettingSearch;

    public StashBrowseView()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
        _searchTimer = DispatcherQueue.GetForCurrentThread()!.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(350);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += async (_, _) => await LoadAsync();
    }

    /// <summary>A card was clicked: a scene (open its page) or a performer, studio or tag (open its scenes).</summary>
    public event EventHandler<StashItem>? ItemInvoked;

    public StashKind Kind => _kind;
    public StashScope? Scope => _scope;

    /// <summary>The loaded scenes, used as the play queue.</summary>
    public System.Collections.Generic.IReadOnlyList<StashItem> LoadedItems => _items;

    public Task ShowAsync(StashClient client, StashKind kind, StashScope? scope = null)
    {
        _client = client;
        _kind = kind;
        _scope = scope;
        _filter = StashSceneFilter.All;
        _isResettingSearch = true;
        SearchBox.Text = string.Empty;
        _isResettingSearch = false;
        UpdateChrome();
        return LoadAsync();
    }

    public Task ReloadAsync() => LoadAsync();

    private void UpdateChrome()
    {
        var scenes = _kind == StashKind.Scene;
        Eyebrow.Text = StashSession.ServerName.ToUpperInvariant();
        PageTitle.Text = _scope is { } scope ? scope.Name : _kind switch
        {
            StashKind.Performer => "Performers",
            StashKind.Studio => "Studios",
            StashKind.Tag => "Tags",
            _ => "Scenes"
        };
        SearchBox.PlaceholderText = _kind switch
        {
            StashKind.Performer => "Search performers",
            StashKind.Studio => "Search studios",
            StashKind.Tag => "Search tags",
            _ => _scope is null ? "Search scenes" : $"Search {_scope.Name}'s scenes"
        };
        Filters.Visibility = scenes ? Visibility.Visible : Visibility.Collapsed;
        SortButton.Visibility = scenes ? Visibility.Visible : Visibility.Collapsed;
        foreach (var chip in Filters.Children.OfType<ToggleButton>())
            chip.IsChecked = chip.Tag is string tag && tag == _filter.ToString();
        ClearScopeButton.Visibility = _scope is null ? Visibility.Collapsed : Visibility.Visible;
        ScopeText.Text = _scope is { } current ? $"{current.Kind}: {current.Name}" : string.Empty;

        (Items.ItemTemplate, GridLayout.MinItemWidth) = _kind switch
        {
            StashKind.Performer => ((DataTemplate)Resources["PosterTemplate"], 176d),
            StashKind.Studio => ((DataTemplate)Resources["LogoTemplate"], 260d),
            StashKind.Tag => ((DataTemplate)Resources["WideTemplate"], 240d),
            _ => ((DataTemplate)Resources["WideTemplate"], 288d)
        };
    }

    private async Task LoadAsync()
    {
        if (_client is null)
            return;
        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        _items.Clear();
        _page = 0;
        _total = 0;
        Message.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        CountText.Text = string.Empty;
        ItemsScroller.ChangeView(null, 0, null, disableAnimation: true);
        try
        {
            await LoadPageAsync(cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            CountText.Text = _total == 1 ? "1 result" : $"{_total:N0} {(_kind == StashKind.Scene ? "scenes" : "results")}";
            if (_items.Count == 0)
            {
                Message.Show("", "Nothing here", string.IsNullOrWhiteSpace(SearchBox.Text)
                    ? "Nothing matches these filters." : $"Nothing matches “{SearchBox.Text.Trim()}”.");
                Message.Visibility = Visibility.Visible;
            }
        }
        catch (StashException exception) when (!cancel.IsCancellationRequested)
        {
            Message.Show("", "Couldn't load this", exception.Message, "Try again");
            Message.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_loading, cancel))
                LoadingRing.IsActive = false;
        }
    }

    private async Task LoadPageAsync(CancellationToken cancel)
    {
        var page = _page + 1;
        var search = SearchBox.Text;
        var result = _kind == StashKind.Scene
            ? await _client!.FindScenesAsync(_filter, _sort, search, _scope, page, PageSize, cancel)
            : await _client!.FindAsync(_kind, search, page, PageSize, popularFirst: string.IsNullOrWhiteSpace(search), cancel);
        if (cancel.IsCancellationRequested)
            return;
        _page = page;
        _total = result.Total;
        StashUi.LoadCardImages(_client, result.Items);
        foreach (var item in result.Items)
            _items.Add(item);
    }

    /// <summary>Loads the next page when the list is scrolled near its end.</summary>
    private async void ItemsScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isLoadingMore || _client is null || _items.Count == 0 || _items.Count >= _total)
            return;
        if (ItemsScroller.VerticalOffset < ItemsScroller.ScrollableHeight - 900)
            return;

        _isLoadingMore = true;
        MoreRing.IsActive = true;
        var cancel = _loading?.Token ?? CancellationToken.None;
        try
        {
            await LoadPageAsync(cancel);
        }
        catch (StashException)
        {
        }
        finally
        {
            _isLoadingMore = false;
            MoreRing.IsActive = false;
        }
    }

    private void Items_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index < 24)
            Motion.EnterFromBelow(args.Element, TimeSpan.FromMilliseconds(args.Index * 20), 14);
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StashItem item })
            ItemInvoked?.Invoke(this, item);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isResettingSearch)
            return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<StashSceneFilter>(tag, out var filter))
        {
            _filter = filter;
            UpdateChrome();
            await LoadAsync();
        }
    }

    private async void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag } item && Enum.TryParse<StashSceneSort>(tag, out var sort))
        {
            _sort = sort;
            SortText.Text = "Sort: " + item.Text;
            await LoadAsync();
        }
    }

    private async void ClearScope_Click(object sender, RoutedEventArgs e)
    {
        _scope = null;
        UpdateChrome();
        await LoadAsync();
    }

    private async void Message_ActionClick(object? sender, EventArgs e) => await LoadAsync();
}
