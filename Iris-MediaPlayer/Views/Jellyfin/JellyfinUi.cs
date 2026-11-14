using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services.Jellyfin;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;

namespace Iris_MediaPlayer.Views.Jellyfin;

/// <summary>Small helpers the Jellyfin pages share: pictures and turning server items into playable videos.</summary>
internal static class JellyfinUi
{
    public const int PosterHeight = 420;
    public const int WideWidth = 600;
    public const int BackdropWidth = 1920;
    public const int LogoWidth = 800;
    public const int FaceHeight = 220;

    /// <summary>Gives each item its card picture: posters for tall cards, stills or thumbs for wide ones.</summary>
    public static void LoadCardImages(JellyfinClient client, IEnumerable<JellyfinItem> items, bool wide)
    {
        foreach (var item in items)
        {
            var url = wide ? client.GetImageUrl(item, JellyfinImage.Wide, WideWidth) : client.GetImageUrl(item, JellyfinImage.Poster, PosterHeight);
            item.Image = url is null ? null : Load(url, wide ? null : PosterHeight, wide ? WideWidth : null);
        }
    }

    public static ImageSource? Load(string? url, int? decodeHeight = null, int? decodeWidth = null)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        var image = new BitmapImage(new Uri(url));
        if (decodeHeight is { } height)
            image.DecodePixelHeight = height;
        else if (decodeWidth is { } width)
            image.DecodePixelWidth = width;
        return image;
    }

    /// <summary>
    /// A video the Iris player can open. <paramref name="source"/> picks a version (null: the
    /// default one); the audio and subtitle tracks are mpv's ids (subtitles -1 = off).
    /// </summary>
    public static MediaItem ToMediaItem(JellyfinClient client, JellyfinItem item, string? context = null,
        JellyfinMediaSource? source = null, long? audioTrack = null, long? subtitleTrack = null)
    {
        var detail = item.Type == "Episode" && item.SeriesName is { } series
            ? item.EpisodeCode.Length > 0 ? $"{series}  ·  {item.EpisodeCode}" : series
            : context ?? JellyfinSession.ServerName ?? "Jellyfin";
        var extension = "." + ((source?.Container ?? item.Container)?.Split(',')[0] ?? "mkv");
        return new MediaItem(Key(item), item.Name, string.Empty, detail, extension, item.DateCreated)
        {
            StreamUrl = client.GetStreamUrl(item, source?.Id),
            ServerResumeSeconds = item.ResumeSeconds,
            StartAudioTrack = audioTrack,
            StartSubtitleTrack = subtitleTrack,
            Thumbnail = item.Image
        };
    }

    public static string Key(JellyfinItem item) => $"jellyfin://{JellyfinSession.ServerId}/{item.Id}";

    /// <summary>
    /// The facts line under a title: community rating, critic score, year, age rating, running time,
    /// and (on details pages) when it would end if started now.
    /// </summary>
    public static void FillMeta(Microsoft.UI.Xaml.Controls.Panel panel, JellyfinItem item, bool withGenres, bool withEndTime)
    {
        panel.Children.Clear();
        var muted = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["IrisTextMutedBrush"];

        if (item.CommunityRating is { } rating && rating > 0)
            panel.Children.Add(Rated("", Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0xC4, 0x52), rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
        if (item.CriticRating is { } critics && critics > 0)
            panel.Children.Add(Rated("", critics >= 60 ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xE5, 0x53, 0x3D) : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x5B, 0xD6, 0x9A),
                $"{critics:0}%"));
        if (item.YearText.Length > 0)
            panel.Children.Add(Text(item.YearText, muted));
        if (!string.IsNullOrEmpty(item.OfficialRating))
            panel.Children.Add(Badge(item.OfficialRating));
        if (item.RunTimeSeconds > 0 && item.Type is not ("Series" or "Season"))
            panel.Children.Add(Text(JellyfinItem.FormatRunTime(item.RunTimeSeconds), muted));
        if (item.Type == "Series" && item.ChildCount is int seasons and > 0)
            panel.Children.Add(Text(seasons == 1 ? "1 season" : $"{seasons} seasons", muted));
        if (withGenres && item.Genres.Count > 0)
            panel.Children.Add(Text(string.Join(" · ", System.Linq.Enumerable.Take(item.Genres, 2)), muted));
        if (withEndTime && item.RunTimeSeconds > 0 && item.IsPlayable)
        {
            var endsAt = DateTime.Now.AddSeconds(item.RunTimeSeconds - item.ResumeSeconds);
            panel.Children.Add(Text($"Ends at {endsAt:HH:mm}", (Brush)Microsoft.UI.Xaml.Application.Current.Resources["IrisTextTertiaryBrush"]));
        }
    }

    public static Microsoft.UI.Xaml.Controls.Border Badge(string text) => new()
    {
        Padding = new Microsoft.UI.Xaml.Thickness(7, 2, 7, 3),
        CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6),
        BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
        BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)),
        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        Child = new Microsoft.UI.Xaml.Controls.TextBlock { Text = text, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 40 }
    };

    private static Microsoft.UI.Xaml.Controls.TextBlock Text(string text, Brush brush) => new()
    {
        Text = text,
        FontSize = 14,
        Foreground = brush,
        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
    };

    private static Microsoft.UI.Xaml.Controls.StackPanel Rated(string glyph, Windows.UI.Color color, string value)
    {
        var panel = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };
        panel.Children.Add(new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = glyph, FontSize = 13, Foreground = new SolidColorBrush(color) });
        panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = value, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        return panel;
    }
}
