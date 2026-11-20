using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Views.Servers;

/// <summary>
/// The server chip's menu, shared by every media-server page: who's signed in, the connected servers
/// (tick on the one shown; pick another to switch), Add server › Jellyfin / Stash / Plex (coming soon),
/// Refresh, and Sign out (Jellyfin) or Disconnect (Stash).
/// </summary>
internal static class ServerMenu
{
    public static void Fill(MenuFlyout menu, MediaServerKind current, string? signedInAs, Action refresh, Func<Task> signOut)
    {
        menu.Items.Clear();
        if (!string.IsNullOrEmpty(signedInAs))
        {
            menu.Items.Add(new MenuFlyoutItem { Text = signedInAs, IsEnabled = false, Icon = Icon("") });
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (var kind in new[] { MediaServerKind.Jellyfin, MediaServerKind.Stash })
        {
            if (!MediaServers.IsConnected(kind))
                continue;
            var item = new ToggleMenuFlyoutItem
            {
                Text = $"{MediaServers.DisplayName(kind)}  ·  {kind}",
                IsChecked = kind == current,
                Icon = Icon(GlyphFor(kind))
            };
            item.Click += (_, _) =>
            {
                if (kind != current)
                    MediaServers.Active = kind;
                else
                    item.IsChecked = true;
            };
            menu.Items.Add(item);
        }

        var add = new MenuFlyoutSubItem { Text = "Add server", Icon = Icon("") };
        foreach (var kind in new[] { MediaServerKind.Jellyfin, MediaServerKind.Stash, MediaServerKind.Plex })
        {
            var connected = MediaServers.IsConnected(kind);
            var item = new MenuFlyoutItem
            {
                Text = kind switch
                {
                    MediaServerKind.Plex => "Plex  ·  Coming soon",
                    _ when connected => $"{kind}  ·  Connected",
                    _ => kind.ToString()
                },
                IsEnabled = kind != MediaServerKind.Plex && !connected,
                Icon = Icon(GlyphFor(kind))
            };
            item.Click += (_, _) => MediaServers.RequestAdd(kind);
            add.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(add);

        menu.Items.Add(new MenuFlyoutSeparator());
        var refreshItem = new MenuFlyoutItem { Text = "Refresh", Icon = Icon("") };
        refreshItem.Click += (_, _) => refresh();
        menu.Items.Add(refreshItem);
        var signOutItem = new MenuFlyoutItem { Text = current == MediaServerKind.Stash ? "Disconnect" : "Sign out", Icon = Icon("") };
        signOutItem.Click += async (_, _) => await signOut();
        menu.Items.Add(signOutItem);
    }

    public static string GlyphFor(MediaServerKind kind) => kind switch
    {
        MediaServerKind.Stash => "",
        MediaServerKind.Plex => "",
        _ => ""
    };

    private static FontIcon Icon(string glyph) => new() { Glyph = glyph };
}
