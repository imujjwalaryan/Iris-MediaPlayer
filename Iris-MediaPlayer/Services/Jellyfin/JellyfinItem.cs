using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Iris_MediaPlayer.Services.Jellyfin;

/// <summary>A library, folder, collection, film, show, season or episode on a Jellyfin server.</summary>
public sealed class JellyfinItem : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Jellyfin's type: Movie, Series, Season, Episode, Video, Folder, BoxSet, CollectionFolder…</summary>
    public required string Type { get; init; }
    /// <summary>For libraries: movies, tvshows, homevideos… Null otherwise.</summary>
    public string? CollectionType { get; init; }
    public bool IsFolder { get; init; }
    public int? Year { get; init; }
    public int? EndYear { get; init; }
    /// <summary>For shows: "Continuing" or "Ended".</summary>
    public string? Status { get; init; }
    public string? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public string? SeasonId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public double RunTimeSeconds { get; init; }
    public string? Container { get; init; }
    public DateTime DateCreated { get; init; }
    public DateTime? PremiereDate { get; init; }
    public string? Overview { get; init; }
    public string? Tagline { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Studios { get; init; } = [];
    public IReadOnlyList<JellyfinPerson> People { get; init; } = [];
    public double? CommunityRating { get; init; }
    public double? CriticRating { get; init; }
    public string? OfficialRating { get; init; }
    public string? TrailerUrl { get; init; }
    public int? ChildCount { get; init; }
    public int? UnplayedCount { get; init; }
    public IReadOnlyList<JellyfinMediaSource> MediaSources { get; init; } = [];

    // Pictures: the item's own tags, then the parent's (a season or episode borrows the show's).
    public string? PrimaryTag { get; init; }
    public string? ThumbTag { get; init; }
    public string? LogoTag { get; init; }
    public string? BackdropTag { get; init; }
    public string? SeriesPrimaryTag { get; init; }
    public string? ParentThumbItemId { get; init; }
    public string? ParentThumbTag { get; init; }
    public string? ParentLogoItemId { get; init; }
    public string? ParentLogoTag { get; init; }
    public string? ParentBackdropItemId { get; init; }
    public string? ParentBackdropTag { get; init; }

    private double _resumeSeconds;
    public double ResumeSeconds
    {
        get => _resumeSeconds;
        set { if (Set(ref _resumeSeconds, value)) Changed(nameof(Progress), nameof(ProgressVisibility)); }
    }

    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }

    private bool _isPlayed;
    public bool IsPlayed
    {
        get => _isPlayed;
        set { if (Set(ref _isPlayed, value)) Changed(nameof(PlayedVisibility)); }
    }

    // ---------- What cards show ----------

    public bool IsPlayable => !IsFolder && Type is "Movie" or "Episode" or "Video" or "MusicVideo" or "Trailer";

    /// <summary>Episodes and home videos have wide stills; everything else has tall posters.</summary>
    public bool IsWide => Type is "Episode" or "Video" or "MusicVideo" or "CollectionFolder" or "UserView";

    /// <summary>The name on cards: "3. Pilot" for episodes, the name otherwise.</summary>
    public string DisplayName => Type == "Episode" && EpisodeNumber is { } number ? $"{number}. {Name}" : Name;

    /// <summary>"S2:E4" for episodes, empty otherwise.</summary>
    public string EpisodeCode => SeasonNumber is { } season && EpisodeNumber is { } episode ? $"S{season}:E{episode}" : string.Empty;

    /// <summary>The line under a card: year, running time or episode number.</summary>
    public string Caption
    {
        get
        {
            var parts = new List<string>();
            if (Type == "Episode" && EpisodeCode.Length > 0)
                parts.Add(EpisodeCode);
            else if (YearText.Length > 0)
                parts.Add(YearText);
            if (RunTimeSeconds > 0 && Type is not ("Series" or "Season"))
                parts.Add(FormatRunTime(RunTimeSeconds));
            if (parts.Count == 0)
                parts.Add(Type switch
                {
                    "Series" => "Show",
                    "Season" => "Season",
                    "BoxSet" => "Collection",
                    "Folder" or "CollectionFolder" => "Folder",
                    _ => string.Empty
                });
            return string.Join("  ·  ", parts.Where(part => part.Length > 0));
        }
    }

    /// <summary>For wide cards in rows: the show and episode, or what's left to watch.</summary>
    public string RowTitle => Type == "Episode" && SeriesName is { } series ? series : Name;

    public string RowCaption
    {
        get
        {
            var parts = new List<string>();
            if (Type == "Episode")
                parts.Add(EpisodeCode.Length > 0 ? $"{EpisodeCode} · {Name}" : Name);
            if (ResumeSeconds > 0 && RunTimeSeconds > ResumeSeconds)
                parts.Add(FormatRunTime(RunTimeSeconds - ResumeSeconds) + " left");
            else if (Type != "Episode" && YearText.Length > 0)
                parts.Add(YearText);
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>"E3" in an episode list.</summary>
    public string EpisodeLabel => EpisodeNumber is { } number ? $"E{number}" : string.Empty;

    /// <summary>"54 min · 12 Mar 2022" under an episode, plus what's left when it's started.</summary>
    public string EpisodeInfo
    {
        get
        {
            var parts = new List<string>();
            if (RunTimeSeconds > 0)
                parts.Add(FormatRunTime(RunTimeSeconds));
            if (PremiereDate is { } aired)
                parts.Add(aired.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            if (ResumeSeconds > 0 && RunTimeSeconds > ResumeSeconds)
                parts.Add(FormatRunTime(RunTimeSeconds - ResumeSeconds) + " left");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>"2024", or "2007 – 2019" / "2022 – present" for shows.</summary>
    public string YearText
    {
        get
        {
            if (Year is not { } year)
                return string.Empty;
            if (Type != "Series")
                return year.ToString(CultureInfo.InvariantCulture);
            if (Status == "Continuing")
                return $"{year} – present";
            return EndYear is { } end && end != year ? $"{year} – {end}" : year.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>How much has been watched, from 0 to 1.</summary>
    public double Progress => RunTimeSeconds > 0 && ResumeSeconds > 0 ? Math.Clamp(ResumeSeconds / RunTimeSeconds, 0, 1) : 0;

    public Visibility ProgressVisibility => Progress > 0.01 && Progress < 0.97 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlayedVisibility => IsPlayed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Unwatched episodes in a show or season, shown as a small count.</summary>
    public string UnplayedText => UnplayedCount is int count and > 0 ? count.ToString(CultureInfo.InvariantCulture) : string.Empty;

    public Visibility UnplayedVisibility => UnplayedCount is > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown while the picture loads, or when there is none.</summary>
    public string Glyph => IsPlayable ? "" : Type is "Series" or "Season" ? "" : "";

    private ImageSource? _image;
    /// <summary>The card picture, set by the page that shows the card.</summary>
    public ImageSource? Image { get => _image; set => Set(ref _image, value); }

    public static string FormatRunTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min" : $"{Math.Max(1, (int)Math.Round(time.TotalMinutes))} min";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void Changed(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Someone in the credits: an actor (with the part they play), director, writer…</summary>
public sealed class JellyfinPerson
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Role { get; init; }
    /// <summary>Actor, Director, Writer, Producer, GuestStar…</summary>
    public required string Type { get; init; }
    public string? ImageTag { get; init; }
    public ImageSource? Image { get; set; }
    /// <summary>First letter, shown when there's no photo.</summary>
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
}

/// <summary>One version of a video (a film can have a 4K and a 1080p copy), with its tracks.</summary>
public sealed class JellyfinMediaSource
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public long Size { get; init; }
    public string? Container { get; init; }
    public IReadOnlyList<JellyfinStream> Streams { get; init; } = [];
    public int? DefaultAudioIndex { get; init; }
    public int? DefaultSubtitleIndex { get; init; }

    public JellyfinStream? Video => Streams.FirstOrDefault(stream => stream.Type == "Video");
    public IEnumerable<JellyfinStream> AudioStreams => Streams.Where(stream => stream.Type == "Audio");
    public IEnumerable<JellyfinStream> SubtitleStreams => Streams.Where(stream => stream.Type == "Subtitle");

    /// <summary>"2160p · Dolby Vision · 18.4 GB": what the version picker shows.</summary>
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (Video is { } video)
            {
                parts.Add(video.ResolutionLabel);
                if (video.RangeLabel.Length > 0)
                    parts.Add(video.RangeLabel);
            }
            if (Size > 0)
                parts.Add($"{Size / 1_000_000_000d:0.0} GB");
            return parts.Count > 0 ? string.Join(" · ", parts) : Name ?? "Default";
        }
    }

    /// <summary>
    /// The id mpv gives this track: mpv numbers each kind of track 1, 2, 3… in file order, while
    /// Jellyfin numbers every stream in the file. External subtitle files aren't in the stream, so null.
    /// </summary>
    public long? GetMpvTrackId(JellyfinStream stream)
    {
        if (stream.IsExternal)
            return null;
        var ordinal = Streams.Where(other => other.Type == stream.Type && !other.IsExternal && other.Index <= stream.Index).Count();
        return ordinal > 0 ? ordinal : null;
    }
}

/// <summary>A video, audio or subtitle track inside a version.</summary>
public sealed class JellyfinStream
{
    public int Index { get; init; }
    public required string Type { get; init; }
    public string? Language { get; init; }
    public string? DisplayTitle { get; init; }
    public string? Codec { get; init; }
    public bool IsExternal { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? VideoRangeType { get; init; }

    public string Label => DisplayTitle ?? Language ?? $"Track {Index}";

    public string ResolutionLabel => Width >= 3200 || Height >= 2000 ? "4K"
        : Width >= 2400 || Height >= 1400 ? "1440p"
        : Width >= 1800 || Height >= 1000 ? "1080p"
        : Width >= 1200 || Height >= 700 ? "720p"
        : Width > 0 ? "SD" : string.Empty;

    /// <summary>Dolby Vision, HDR10+, HDR10, HLG or empty for SDR.</summary>
    public string RangeLabel => (VideoRangeType ?? string.Empty) switch
    {
        var range when range.StartsWith("DOVI", StringComparison.OrdinalIgnoreCase) => "Dolby Vision",
        var range when range.Contains("HDR10Plus", StringComparison.OrdinalIgnoreCase) => "HDR10+",
        var range when range.Contains("HDR10", StringComparison.OrdinalIgnoreCase) => "HDR10",
        var range when range.Contains("HLG", StringComparison.OrdinalIgnoreCase) => "HLG",
        _ => string.Empty
    };

    public override string ToString() => Label;
}
