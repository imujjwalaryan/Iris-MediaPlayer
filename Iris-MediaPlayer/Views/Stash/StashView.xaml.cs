using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Services.Stash;
using Iris_MediaPlayer.Views.Servers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Views.Stash;

/// <summary>
/// The Stash media center. Not connected: address, API key and an optional name. Connected: Home,
/// Scenes, Performers, Studios and Tags under a floating bar (server menu or Back, tabs, Refresh),
/// with a page per scene. Scenes play in the normal Iris player and report progress back to Stash.
/// </summary>
public sealed partial class StashView : UserControl
{
    private static readonly string[] TabNames = ["Home", "Scenes", "Performers", "Studios", "Tags"];

    /// <summary>One place in the section: Home, a list (with an optional scope) or a scene.</summary>
    private sealed record Place(string Kind, StashKind List = StashKind.Scene, StashScope? Scope = null, StashItem? Scene = null);

    private StashClient? _client;
    private readonly Stack<Place> _history = new();
    private Place _current = new("Home");
    private bool _isActive;
    private bool _hasLoaded;

    public StashView()
    {
        InitializeComponent();
        Tabs.Items = TabNames;
        StashSession.Changed += () => DispatcherQueue.TryEnqueue(() => _ = ShowCurrentStateAsync());

        HomePage.ItemInvoked += (_, item) => Open(item);
        HomePage.PlayRequested += (_, request) => PlayRequested?.Invoke(this, request);
        BrowsePage.ItemInvoked += (_, item) => Open(item);
        ScenePage.PlayRequested += (_, request) => PlayRequested?.Invoke(this, request);
        ScenePage.ScopeRequested += (_, scope) => Navigate(new Place("List", StashKind.Scene, scope));
    }

    public event EventHandler<PlayRequest>? PlayRequested;

    public void Activate()
    {
        _isActive = true;
        HomePage.IsActive = BrowseRoot.Visibility == Visibility.Visible && _current.Kind == "Home";
        if (!_hasLoaded)
            _ = ShowCurrentStateAsync();
    }

    public void Deactivate()
    {
        _isActive = false;
        HomePage.IsActive = false;
    }

    /// <summary>Shows the connect card even when connected (Add server › Stash when another server is in front).</summary>
    public void ShowConnect()
    {
        _hasLoaded = true;
        ShowConnectRoot();
    }

    private async Task ShowCurrentStateAsync()
    {
        _hasLoaded = true;
        _client = StashSession.CreateClient();
        if (_client is null)
        {
            ShowConnectRoot();
            return;
        }

        ConnectRoot.Visibility = Visibility.Collapsed;
        BrowseRoot.Visibility = Visibility.Visible;
        ServerNameText.Text = StashSession.ServerName;
        _history.Clear();
        Show(new Place("Home"));
        await HomePage.LoadAsync(_client, CancellationToken.None);
    }

    // ---------- Navigation ----------

    private void Tabs_SelectionChanged(object? sender, int index)
    {
        _history.Clear();
        Show(index switch
        {
            1 => new Place("List", StashKind.Scene),
            2 => new Place("List", StashKind.Performer),
            3 => new Place("List", StashKind.Studio),
            4 => new Place("List", StashKind.Tag),
            _ => new Place("Home")
        });
    }

    private void Open(StashItem item)
    {
        if (item.Kind == StashKind.Scene)
            Navigate(new Place("Scene", Scene: item));
        else
            Navigate(new Place("List", StashKind.Scene, new StashScope(item.Kind, item.Id, item.Name)));
    }

    private void Navigate(Place place)
    {
        _history.Push(_current);
        Show(place);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count > 0)
            Show(_history.Pop(), goingBack: true);
    }

    private void Show(Place place, bool goingBack = false)
    {
        if (_client is null)
            return;
        _current = place;
        HomePage.Visibility = place.Kind == "Home" ? Visibility.Visible : Visibility.Collapsed;
        BrowsePage.Visibility = place.Kind == "List" ? Visibility.Visible : Visibility.Collapsed;
        ScenePage.Visibility = place.Kind == "Scene" ? Visibility.Visible : Visibility.Collapsed;
        HomePage.IsActive = _isActive && place.Kind == "Home";

        var tab = place.Kind switch
        {
            "Home" => 0,
            "List" when place.Scope is null => place.List switch { StashKind.Performer => 2, StashKind.Studio => 3, StashKind.Tag => 4, _ => 1 },
            _ => Tabs.SelectedIndex
        };
        if (tab != Tabs.SelectedIndex)
            Tabs.SelectedIndex = tab;

        var hasBack = _history.Count > 0;
        BackButton.Visibility = hasBack ? Visibility.Visible : Visibility.Collapsed;
        ServerButton.Visibility = hasBack ? Visibility.Collapsed : Visibility.Visible;
        BackText.Text = hasBack ? _history.Peek() switch
        {
            { Kind: "Home" } => "Home",
            { Kind: "Scene", Scene: { } scene } => scene.Name,
            { Scope: { } scope } => scope.Name,
            { List: var list } => TabNames[list switch { StashKind.Performer => 2, StashKind.Studio => 3, StashKind.Tag => 4, _ => 1 }]
        } : "Back";

        switch (place.Kind)
        {
            case "List" when !goingBack || BrowsePage.Kind != place.List || BrowsePage.Scope != place.Scope:
                _ = BrowsePage.ShowAsync(_client, place.List, place.Scope);
                break;
            case "Scene" when place.Scene is { } item:
                _ = ScenePage.ShowAsync(_client, item);
                break;
        }
        if (place.Kind != "Home")
            Motion.EnterFromBelow(place.Kind == "List" ? BrowsePage : ScenePage, TimeSpan.Zero, 10);
    }

    /// <summary>Back from the player: reload once Stash has the progress, so resume points are current.</summary>
    public async void RefreshAfterPlayback()
    {
        if (_client is null || BrowseRoot.Visibility != Visibility.Visible)
            return;
        await StashPlaybackReporter.WhenSentAsync();
        await ReloadCurrentAsync(quiet: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadCurrentAsync(quiet: false);

    private async Task ReloadCurrentAsync(bool quiet)
    {
        if (_client is null)
            return;
        switch (_current.Kind)
        {
            case "Home":
                await HomePage.LoadAsync(_client, CancellationToken.None);
                break;
            case "List":
                await BrowsePage.ReloadAsync();
                break;
            case "Scene" when _current.Scene is { } scene:
                await ScenePage.ShowAsync(_client, scene, quiet);
                break;
        }
    }

    private void ServerMenu_Opening(object? sender, object e)
        => ServerMenu.Fill(ServerMenuFlyout, MediaServerKind.Stash, StashSession.Address is { } address ? $"Connected to {address}" : null,
            () => Refresh_Click(this, new RoutedEventArgs()), () =>
            {
                StashSession.Disconnect();
                return Task.CompletedTask;
            });

    // ---------- Connecting ----------

    private void ShowConnectRoot()
    {
        ConnectRoot.Visibility = Visibility.Visible;
        BrowseRoot.Visibility = Visibility.Collapsed;
        HomePage.IsActive = false;
        ErrorBar.IsOpen = false;
        AddressBox.Text = StashSession.Address ?? string.Empty;
        ApiKeyBox.Password = string.Empty;
        // Cancel goes back to another connected server, or to what Stash showed before.
        CancelButton.Visibility = MediaServers.IsConnected(MediaServerKind.Jellyfin) || StashSession.IsConnected
            ? Visibility.Visible : Visibility.Collapsed;
        AddressBox.Focus(FocusState.Programmatic);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void Field_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(AddressBox.Text))
        {
            ShowError("Type your Stash address first.");
            return;
        }

        ErrorBar.IsOpen = false;
        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Connecting…";
        try
        {
            var client = new StashClient(AddressBox.Text, ApiKeyBox.Password);
            await client.CheckAsync();
            ApiKeyBox.Password = string.Empty;
            StashSession.Save(client, NameBox.Text);
            MediaServers.Active = MediaServerKind.Stash;
        }
        catch (StashException exception)
        {
            ShowError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Connect";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (StashSession.IsConnected)
            _ = ShowCurrentStateAsync();
        MediaServers.RequestAdd(MediaServerKind.None);
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
