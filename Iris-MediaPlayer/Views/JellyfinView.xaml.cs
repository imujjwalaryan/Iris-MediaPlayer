using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Services.Jellyfin;
using Iris_MediaPlayer.Views.Jellyfin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// The Jellyfin section. Not signed in: a card that asks for the server address, then a username and
/// password (or a Quick Connect code). Signed in: a media center — Home, one tab per video library and
/// Favorites under a floating bar, with details pages for films and shows. Videos play in the normal
/// Iris player.
/// </summary>
public sealed partial class JellyfinView : UserControl
{
    private static readonly TimeSpan QuickConnectPoll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QuickConnectTimeout = TimeSpan.FromMinutes(5);

    /// <summary>One place in the section: Home, a library (null = Favorites), or a details page.</summary>
    private sealed record Place(string Kind, JellyfinItem? Item);

    private JellyfinClient? _client;
    private JellyfinServerInfo? _server;
    private IReadOnlyList<JellyfinItem> _libraries = [];
    private readonly Stack<Place> _history = new();
    private Place _current = new("Home", null);
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _quickConnect;
    private bool _hasLoaded;
    private bool _isActive;

    public JellyfinView()
    {
        InitializeComponent();
        JellyfinSession.Changed += () => DispatcherQueue.TryEnqueue(() => _ = ShowCurrentStateAsync());

        HomePage.ItemInvoked += (_, item) => _ = OpenAsync(item);
        HomePage.DetailsRequested += (_, item) => Navigate(new Place("Details", item));
        HomePage.PlayRequested += (_, item) => _ = PlayAsync(item);
        HomePage.LibraryRequested += (_, library) => SelectTab(_libraries.ToList().FindIndex(entry => entry.Id == library.Id) + 1);
        LibraryPage.ItemInvoked += (_, item) => _ = OpenAsync(item);
        DetailsPage.PlayRequested += (_, request) => PlayRequested?.Invoke(this, request);
    }

    public event EventHandler<PlayRequest>? PlayRequested;

    /// <summary>Called when the section is shown. Loads once; Refresh loads again.</summary>
    public void Activate()
    {
        _isActive = true;
        HomePage.IsActive = BrowseRoot.Visibility == Visibility.Visible && _current.Kind == "Home";
        if (!_hasLoaded)
            _ = ShowCurrentStateAsync();
    }

    /// <summary>Called when another section is shown, so the hero stops rotating.</summary>
    public void Deactivate()
    {
        _isActive = false;
        HomePage.IsActive = false;
    }

    private async Task ShowCurrentStateAsync()
    {
        _hasLoaded = true;
        CancelQuickConnect();
        _client = JellyfinSession.CreateClient();
        if (_client is null)
        {
            ShowAddressStep(JellyfinSession.Address);
            return;
        }

        SignInRoot.Visibility = Visibility.Collapsed;
        BrowseRoot.Visibility = Visibility.Visible;
        ServerNameText.Text = JellyfinSession.ServerName ?? "Jellyfin";
        await LoadLibrariesAsync();
    }

    // ---------- Libraries and navigation ----------

    private async Task LoadLibrariesAsync()
    {
        if (_client is null)
            return;
        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        BrowseMessage.Visibility = Visibility.Collapsed;
        try
        {
            _libraries = await _client.GetLibrariesAsync(cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            Tabs.Items = new[] { "Home" }.Concat(_libraries.Select(library => library.Name)).Append("Favorites").ToList();
            _history.Clear();
            Show(new Place("Home", null));
            await HomePage.LoadAsync(_client, _libraries, cancel.Token);
        }
        catch (JellyfinException exception) when (!cancel.IsCancellationRequested)
        {
            HomePage.Visibility = Visibility.Collapsed;
            BrowseMessage.Show("", "Couldn't reach Jellyfin", exception.Message, "Try again");
            BrowseMessage.Visibility = Visibility.Visible;
        }
    }

    private void Tabs_SelectionChanged(object? sender, int index) => SelectTab(index);

    private void SelectTab(int index)
    {
        if (index <= 0)
        {
            _history.Clear();
            Show(new Place("Home", null));
            return;
        }
        _history.Clear();
        var library = index <= _libraries.Count ? _libraries[index - 1] : null;
        Show(new Place("Library", library));
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
        LibraryPage.Visibility = place.Kind == "Library" ? Visibility.Visible : Visibility.Collapsed;
        DetailsPage.Visibility = place.Kind == "Details" ? Visibility.Visible : Visibility.Collapsed;
        HomePage.IsActive = _isActive && place.Kind == "Home";
        BrowseMessage.Visibility = Visibility.Collapsed;

        // The tabs show which section you're in; details pages keep the tab they were opened from.
        var tab = place.Kind switch
        {
            "Home" => 0,
            "Library" => place.Item is null ? _libraries.Count + 1 : _libraries.ToList().FindIndex(library => library.Id == place.Item.Id) + 1,
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
            { Kind: "Library", Item: null } => "Favorites",
            { Kind: "Library", Item: { } library } => library.Name,
            { Item: { } previous } => previous.Name,
            _ => "Back"
        } : "Back";

        switch (place.Kind)
        {
            case "Library" when !goingBack || LibraryPage.LibraryId != place.Item?.Id:
                _ = LibraryPage.ShowAsync(_client, place.Item);
                break;
            case "Details" when place.Item is { } item:
                _ = DetailsPage.ShowAsync(_client, item);
                break;
        }
        if (place.Kind != "Home")
            Motion.EnterFromBelow(place.Kind == "Library" ? LibraryPage : DetailsPage, TimeSpan.Zero, 10);
    }

    /// <summary>A card was clicked: films and shows open their page, episodes and videos play.</summary>
    private async Task OpenAsync(JellyfinItem item)
    {
        switch (item.Type)
        {
            case "Movie" or "Series":
                Navigate(new Place("Details", item));
                break;
            case "Season" when item.SeriesId is { } seriesId:
                Navigate(new Place("Details", new JellyfinItem { Id = seriesId, Name = item.SeriesName ?? item.Name, Type = "Series", IsFolder = true }));
                break;
            case "BoxSet" or "Folder" or "CollectionFolder":
                Navigate(new Place("Library", item));
                break;
            default:
                if (item.IsPlayable)
                    await PlayAsync(item);
                break;
        }
    }

    /// <summary>Plays a film or episode; an episode's queue is the rest of its season.</summary>
    private async Task PlayAsync(JellyfinItem item)
    {
        if (_client is null)
            return;
        var queue = new List<MediaItem>();
        if (item.Type == "Episode" && item.SeriesId is { } seriesId && item.SeasonId is { } seasonId)
        {
            try
            {
                var season = await _client.GetEpisodesAsync(seriesId, seasonId);
                JellyfinUi.LoadCardImages(_client, season.Where(entry => entry.Id != item.Id), wide: true);
                queue = season.Select(entry => JellyfinUi.ToMediaItem(_client, entry.Id == item.Id ? item : entry)).ToList();
            }
            catch (JellyfinException)
            {
                queue.Clear();
            }
        }

        var media = queue.FirstOrDefault(entry => entry.FilePath == JellyfinUi.Key(item)) ?? JellyfinUi.ToMediaItem(_client, item);
        PlayRequested?.Invoke(this, new PlayRequest(media, queue.Count > 0 ? queue : [media]));
    }

    /// <summary>
    /// Back from the player: once the server has the playback reports, the page shown reloads so
    /// progress bars, Continue watching and watched marks are up to date.
    /// </summary>
    public async void RefreshAfterPlayback()
    {
        if (_client is null || BrowseRoot.Visibility != Visibility.Visible)
            return;
        await JellyfinPlaybackReporter.WhenSentAsync();
        switch (_current.Kind)
        {
            case "Home":
                await HomePage.LoadAsync(_client, _libraries, CancellationToken.None);
                break;
            case "Library":
                await LibraryPage.ReloadAsync();
                break;
            case "Details" when _current.Item is { } item:
                await DetailsPage.ShowAsync(_client, item, quiet: true);
                break;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        switch (_current.Kind)
        {
            case "Home" when _client is not null:
                await HomePage.LoadAsync(_client, _libraries, CancellationToken.None);
                break;
            case "Library":
                await LibraryPage.ReloadAsync();
                break;
            case "Details" when _current.Item is { } item && _client is not null:
                await DetailsPage.ShowAsync(_client, item);
                break;
        }
    }

    private void ServerMenu_Opening(object? sender, object e)
        => Servers.ServerMenu.Fill(ServerMenuFlyout, MediaServerKind.Jellyfin, $"Signed in as {JellyfinSession.UserName}",
            () => Refresh_Click(this, new RoutedEventArgs()), JellyfinSession.SignOutAsync);

    private async void BrowseMessage_ActionClick(object? sender, EventArgs e)
    {
        HomePage.Visibility = Visibility.Visible;
        await LoadLibrariesAsync();
    }

    // ---------- Connecting and signing in ----------

    private void ShowSignInRoot()
    {
        SignInRoot.Visibility = Visibility.Visible;
        BrowseRoot.Visibility = Visibility.Collapsed;
        HomePage.IsActive = false;
        HideError();
    }

    private void ShowAddressStep(string? address = null)
    {
        ShowSignInRoot();
        StepTitle.Text = "Connect to your server";
        StepCaption.Text = "Type the address you use to open Jellyfin in a browser.";
        AddressStep.Visibility = Visibility.Visible;
        SignInStep.Visibility = Visibility.Collapsed;
        QuickConnectStep.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrEmpty(address))
            AddressBox.Text = address;
        // While another server is connected, Cancel goes back to it.
        CancelAddButton.Visibility = MediaServers.IsConnected(MediaServerKind.Stash) ? Visibility.Visible : Visibility.Collapsed;
        AddressBox.Focus(FocusState.Programmatic);
    }

    private void CancelAdd_Click(object sender, RoutedEventArgs e) => MediaServers.RequestAdd(MediaServerKind.None);

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
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
            ShowError("Type your server's address first.");
            return;
        }

        HideError();
        SetBusy(ConnectButton, true, "Connecting…");
        try
        {
            var client = new JellyfinClient(AddressBox.Text);
            _server = await client.GetServerInfoAsync();
            _client = client;
            AddressBox.Text = client.Address;
            ShowSignInStep(await client.IsQuickConnectEnabledAsync());
        }
        catch (JellyfinException exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(ConnectButton, false, "Connect");
        }
    }

    private void ShowSignInStep(bool quickConnect)
    {
        StepTitle.Text = $"Sign in to {_server?.Name ?? "Jellyfin"}";
        StepCaption.Text = quickConnect
            ? "Use your Jellyfin account, or approve a code from a device that's already signed in."
            : "Use your Jellyfin account.";
        AddressStep.Visibility = Visibility.Collapsed;
        SignInStep.Visibility = Visibility.Visible;
        QuickConnectStep.Visibility = Visibility.Collapsed;
        QuickConnectButton.Visibility = quickConnect ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Password = string.Empty;
        if (string.IsNullOrEmpty(UserNameBox.Text) && JellyfinSession.UserName is { } previous)
            UserNameBox.Text = previous;
        (string.IsNullOrEmpty(UserNameBox.Text) ? (Control)UserNameBox : PasswordBox).Focus(FocusState.Programmatic);
    }

    private void ChangeServer_Click(object sender, RoutedEventArgs e) => ShowAddressStep();

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await SignInAsync();

    private async void PasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SignInAsync();
        }
    }

    private async Task SignInAsync()
    {
        if (_client is null || _server is null)
            return;
        if (string.IsNullOrWhiteSpace(UserNameBox.Text))
        {
            ShowError("Type your username.");
            return;
        }

        HideError();
        SetBusy(SignInButton, true, "Signing in…");
        try
        {
            var signIn = await _client.SignInAsync(UserNameBox.Text.Trim(), PasswordBox.Password);
            PasswordBox.Password = string.Empty;
            JellyfinSession.Save(_server, signIn);
        }
        catch (JellyfinException exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(SignInButton, false, "Sign in");
        }
    }

    private async void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _server is null)
            return;

        HideError();
        CancelQuickConnect();
        var cancel = _quickConnect = new CancellationTokenSource(QuickConnectTimeout);
        try
        {
            var request = await _client.StartQuickConnectAsync(cancel.Token);
            QuickConnectCode.Text = request.Code;
            StepTitle.Text = "Approve this code";
            StepCaption.Text = "On a device that's signed in to Jellyfin, open your profile, choose Quick Connect and enter this code.";
            SignInStep.Visibility = Visibility.Collapsed;
            QuickConnectStep.Visibility = Visibility.Visible;

            while (!await _client.IsQuickConnectApprovedAsync(request, cancel.Token))
                await Task.Delay(QuickConnectPoll, cancel.Token);

            var signIn = await _client.FinishQuickConnectAsync(request, cancel.Token);
            JellyfinSession.Save(_server, signIn);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (ReferenceEquals(_quickConnect, cancel) && QuickConnectStep.Visibility == Visibility.Visible)
            {
                ShowSignInStep(quickConnect: true);
                ShowError("The code expired. Try again.");
            }
        }
        catch (JellyfinException exception)
        {
            ShowSignInStep(quickConnect: true);
            ShowError(exception.Message);
        }
    }

    private void CancelQuickConnect_Click(object sender, RoutedEventArgs e)
    {
        CancelQuickConnect();
        ShowSignInStep(quickConnect: true);
    }

    private void CancelQuickConnect()
    {
        var cancel = _quickConnect;
        _quickConnect = null;
        cancel?.Cancel();
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void HideError() => ErrorBar.IsOpen = false;

    private static void SetBusy(Button button, bool busy, string text)
    {
        button.IsEnabled = !busy;
        button.Content = text;
    }
}
