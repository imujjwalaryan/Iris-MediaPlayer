using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// The media-servers section of the rail. Shows the server in front (<see cref="MediaServers.Active"/>):
/// Jellyfin or Stash, each with its own pages and server menu. With nothing connected, or while
/// adding a server from the menu, it shows that server's connect screen (or a chooser).
/// </summary>
public sealed partial class ServersView : UserControl
{
    /// <summary>A server being added from the menu; its connect screen shows until it's connected or cancelled.</summary>
    private MediaServerKind? _adding;
    private bool _isActive;

    public ServersView()
    {
        InitializeComponent();
        JellyfinPage.PlayRequested += (_, request) => PlayRequested?.Invoke(this, request);
        StashPage.PlayRequested += (_, request) => PlayRequested?.Invoke(this, request);
        MediaServers.Changed += () => DispatcherQueue.TryEnqueue(Update);
        MediaServers.AddRequested += kind => DispatcherQueue.TryEnqueue(() =>
        {
            // None cancels adding.
            _adding = kind == MediaServerKind.None ? null : kind;
            Update();
            if (_adding == MediaServerKind.Stash)
                StashPage.ShowConnect();
        });
    }

    public event EventHandler<PlayRequest>? PlayRequested;

    public void Activate()
    {
        _isActive = true;
        Update();
    }

    public void Deactivate()
    {
        _isActive = false;
        JellyfinPage.Deactivate();
        StashPage.Deactivate();
    }

    public void RefreshAfterPlayback()
    {
        if (JellyfinPage.Visibility == Visibility.Visible)
            JellyfinPage.RefreshAfterPlayback();
        else if (StashPage.Visibility == Visibility.Visible)
            StashPage.RefreshAfterPlayback();
    }

    private void Update()
    {
        // A server added from the menu is now connected: put it in front.
        if (_adding is { } added && MediaServers.IsConnected(added))
        {
            _adding = null;
            if (MediaServers.Active != added)
            {
                MediaServers.Active = added;
                return;
            }
        }

        var kind = _adding ?? MediaServers.Active;
        JellyfinPage.Visibility = kind == MediaServerKind.Jellyfin ? Visibility.Visible : Visibility.Collapsed;
        StashPage.Visibility = kind == MediaServerKind.Stash ? Visibility.Visible : Visibility.Collapsed;
        Chooser.Visibility = kind == MediaServerKind.None ? Visibility.Visible : Visibility.Collapsed;
        if (!_isActive)
            return;

        if (kind == MediaServerKind.Jellyfin)
        {
            StashPage.Deactivate();
            JellyfinPage.Activate();
        }
        else if (kind == MediaServerKind.Stash)
        {
            JellyfinPage.Deactivate();
            StashPage.Activate();
        }
        else
        {
            JellyfinPage.Deactivate();
            StashPage.Deactivate();
        }
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<MediaServerKind>(tag, out var kind))
            MediaServers.RequestAdd(kind);
    }
}
