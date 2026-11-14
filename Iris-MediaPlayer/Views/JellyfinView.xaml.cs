using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services.Jellyfin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// The Jellyfin page. Not signed in: a card that asks for the server address, then a username and
/// password (or a Quick Connect code). Signed in: the user's video libraries as tabs, with posters,
/// and folders, shows and seasons that open in place. Videos play in the normal Iris player.
/// </summary>
public sealed partial class JellyfinView : UserControl
{
    private const int PosterHeight = 420;
    private const int WideHeight = 300;
    private static readonly TimeSpan QuickConnectPoll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QuickConnectTimeout = TimeSpan.FromMinutes(5);

    private JellyfinClient? _client;
    private JellyfinServerInfo? _server;
    private IReadOnlyList<JellyfinItem> _libraries = [];
    /// <summary>Where you are: the library, then any folder, show or season opened inside it.</summary>
    private readonly List<JellyfinItem> _path = [];
    private IReadOnlyList<JellyfinItem> _items = [];
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _quickConnect;
    private bool _hasLoaded;

    public JellyfinView()
    {
        InitializeComponent();
        JellyfinSession.Changed += () => DispatcherQueue.TryEnqueue(() => _ = ShowCurrentStateAsync());
    }

    public event EventHandler<PlayRequest>? PlayRequested;

    /// <summary>Called whenever the page is shown. Loads once; Refresh loads again.</summary>
    public void Activate()
    {
        if (!_hasLoaded)
            _ = ShowCurrentStateAsync();
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

        ShowBrowsing();
        await LoadLibrariesAsync();
    }

    // ---------- Connecting and signing in ----------

    private void ShowSignInPanel()
    {
        SignInPanel.Visibility = Visibility.Visible;
        BrowsePanel.Visibility = Visibility.Collapsed;
        BrowseBar.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Collapsed;
        PageTitle.Text = "Jellyfin";
        PageSummary.Text = "Stream from your Jellyfin server.";
        HideError();
    }

    private void ShowAddressStep(string? address = null)
    {
        ShowSignInPanel();
        StepTitle.Text = "Connect to your server";
        StepCaption.Text = "Type the address you use to open Jellyfin in a browser.";
        AddressStep.Visibility = Visibility.Visible;
        SignInStep.Visibility = Visibility.Collapsed;
        QuickConnectStep.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrEmpty(address))
            AddressBox.Text = address;
        AddressBox.Focus(FocusState.Programmatic);
    }

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
            var quickConnect = await client.IsQuickConnectEnabledAsync();
            ShowSignInStep(quickConnect);
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

    // ---------- Browsing ----------

    private void ShowBrowsing()
    {
        SignInPanel.Visibility = Visibility.Collapsed;
        BrowsePanel.Visibility = Visibility.Visible;
        BrowseBar.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Visible;
        PageTitle.Text = JellyfinSession.ServerName ?? "Jellyfin";
        PageSummary.Text = $"Signed in as {JellyfinSession.UserName}";
    }

    private async Task LoadLibrariesAsync()
    {
        if (_client is null)
            return;

        var cancel = StartLoading();
        try
        {
            _libraries = await _client.GetLibrariesAsync(cancel.Token);
            if (cancel.IsCancellationRequested)
                return;

            var selected = _path.Count > 0 ? _path[0].Id : null;
            LibraryTabs.Items = _libraries.Select(library => library.Name).ToList();
            if (_libraries.Count == 0)
            {
                StopLoading();
                ShowMessage("", "No video libraries", "This account can't see any movie, show or video libraries on the server.");
                return;
            }

            var index = Math.Max(0, _libraries.ToList().FindIndex(library => library.Id == selected));
            LibraryTabs.SelectedIndex = index;
            // Keep the folders opened inside the same library; start over otherwise.
            if (_path.Count == 0 || _path[0].Id != _libraries[index].Id)
            {
                _path.Clear();
                _path.Add(_libraries[index]);
            }
            await LoadItemsAsync();
        }
        catch (JellyfinException exception) when (!cancel.IsCancellationRequested)
        {
            StopLoading();
            ShowMessage("", "Couldn't reach Jellyfin", exception.Message, "Try again");
        }
    }

    private async Task LoadItemsAsync()
    {
        if (_client is null || _path.Count == 0)
            return;

        UpdateBreadcrumb();
        var cancel = StartLoading();
        try
        {
            var items = await _client.GetChildrenAsync(_path[^1], cancel.Token);
            if (cancel.IsCancellationRequested)
                return;

            var wide = items.Count > 0 && items.All(item => item.IsWide);
            foreach (var item in items)
            {
                if (_client.GetImageUrl(item, wide ? WideHeight : PosterHeight) is { } url)
                    item.Image = new BitmapImage(new Uri(url)) { DecodePixelHeight = wide ? WideHeight : PosterHeight };
            }

            _items = items;
            StopLoading();
            PosterItems.ItemsSource = wide ? null : items;
            WideItems.ItemsSource = wide ? items : null;
            PosterItems.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            WideItems.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            ItemsScroller.ChangeView(null, 0, null, disableAnimation: true);
            if (items.Count == 0)
                ShowMessage("", "Nothing here yet", "This library or folder has no videos.");
        }
        catch (JellyfinException exception) when (!cancel.IsCancellationRequested)
        {
            StopLoading();
            ShowMessage("", "Couldn't load this", exception.Message, "Try again");
        }
    }

    private CancellationTokenSource StartLoading()
    {
        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        BrowseMessage.Visibility = Visibility.Collapsed;
        PosterItems.ItemsSource = null;
        WideItems.ItemsSource = null;
        LoadingRing.IsActive = true;
        return cancel;
    }

    private void StopLoading() => LoadingRing.IsActive = false;

    private void ShowMessage(string glyph, string title, string description, string? action = null)
    {
        BrowseMessage.Show(glyph, title, description, action);
        BrowseMessage.Visibility = Visibility.Visible;
    }

    private async void BrowseMessage_ActionClick(object? sender, EventArgs e)
    {
        if (_libraries.Count == 0)
            await LoadLibrariesAsync();
        else
            await LoadItemsAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadLibrariesAsync();

    private async void LibraryTabs_SelectionChanged(object? sender, int index)
    {
        if (index < 0 || index >= _libraries.Count)
            return;
        _path.Clear();
        _path.Add(_libraries[index]);
        await LoadItemsAsync();
    }

    private void UpdateBreadcrumb()
    {
        // The library itself is already the selected tab, so the trail starts below it.
        Breadcrumb.ItemsSource = _path.Count > 1 ? _path.Select(item => item.Name).ToList() : null;
    }

    private async void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _path.Count - 1)
            return;
        _path.RemoveRange(args.Index + 1, _path.Count - args.Index - 1);
        await LoadItemsAsync();
    }

    private async void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JellyfinItem item })
            return;

        if (item.IsPlayable)
        {
            Play(item);
            return;
        }

        _path.Add(item);
        await LoadItemsAsync();
    }

    /// <summary>Keeps card pictures in shape: 2:3 posters, 16:9 stills (the ratio is in the art's Tag).</summary>
    private void Art_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string ratioText } art
            && double.TryParse(ratioText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ratio)
            && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
        {
            art.Height = Math.Round(e.NewSize.Width * ratio);
        }
    }

    // ---------- Playing ----------

    private void Play(JellyfinItem item)
    {
        if (_client is null)
            return;

        // Next and previous move through the videos on this page (a season's episodes, for example).
        var queue = _items.Where(other => other.IsPlayable).Select(ToMediaItem).ToList();
        var media = queue.First(entry => entry.FilePath == Key(item));
        PlayRequested?.Invoke(this, new PlayRequest(media, queue));
    }

    private string Key(JellyfinItem item) => $"jellyfin://{JellyfinSession.ServerId}/{item.Id}";

    private MediaItem ToMediaItem(JellyfinItem item)
    {
        var context = item.Type == "Episode" && item.SeriesName is { } series
            ? item.SeasonNumber is { } season && item.EpisodeNumber is { } episode ? $"{series}  ·  S{season}:E{episode}" : series
            : _path.Count > 0 ? _path[^1].Name : JellyfinSession.ServerName ?? "Jellyfin";
        var extension = "." + (item.Container?.Split(',')[0] ?? "mkv");
        return new MediaItem(Key(item), item.Name, string.Empty, context, extension, item.DateCreated)
        {
            StreamUrl = _client!.GetStreamUrl(item),
            ServerResumeSeconds = item.ResumeSeconds,
            Thumbnail = item.Image
        };
    }
}
