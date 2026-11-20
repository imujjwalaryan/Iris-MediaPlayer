using Iris_MediaPlayer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Iris_MediaPlayer.Services.Stash;

public enum StashKind { Scene, Performer, Studio, Tag }

/// <summary>A Stash scene, performer, studio or tag, shaped for the shared media-server cards.</summary>
public sealed class StashItem : IMediaCardItem
{
    public required string Id { get; init; }
    public required StashKind Kind { get; init; }
    public required string Name { get; init; }

    // Scenes
    public string? Details { get; init; }
    public string? Date { get; init; }
    public int? Rating100 { get; init; }
    public int PlayCount { get; init; }
    public int OCount { get; init; }
    public double DurationSeconds { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public long Size { get; init; }
    public double FrameRate { get; init; }
    public string? FilePath { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? LastPlayedAt { get; init; }
    /// <summary>Picture links as Stash gives them (without the API key; StashClient adds it).</summary>
    public string? ScreenshotPath { get; init; }
    public StashItem? Studio { get; init; }
    public IReadOnlyList<StashItem> Performers { get; init; } = [];
    public IReadOnlyList<StashItem> Tags { get; init; } = [];
    public IReadOnlyList<StashMarker> Markers { get; init; } = [];

    // Performers, studios, tags
    public string? ImagePath { get; init; }
    public int SceneCount { get; init; }
    public bool IsFavorite { get; init; }

    private double _resumeSeconds;
    public double ResumeSeconds
    {
        get => _resumeSeconds;
        set
        {
            if (Math.Abs(_resumeSeconds - value) < 0.01)
                return;
            _resumeSeconds = value;
            Changed(nameof(ResumeSeconds), nameof(Progress), nameof(ProgressVisibility), nameof(RowCaption));
        }
    }

    // ---------- What cards show ----------

    public bool IsPlayable => Kind == StashKind.Scene;
    public bool IsPlayed => Kind == StashKind.Scene && PlayCount > 0 && ResumeSeconds <= 0;

    public string DisplayName => Name;

    public string Caption => Kind switch
    {
        StashKind.Scene => string.Join("  ·  ", new[] { Studio?.Name, DateText, DurationSeconds > 0 ? FormatTime(DurationSeconds) : null }
            .Where(part => !string.IsNullOrEmpty(part))),
        _ => SceneCount == 1 ? "1 scene" : $"{SceneCount:N0} scenes"
    };

    public string RowTitle => Name;

    public string RowCaption
    {
        get
        {
            if (Kind != StashKind.Scene)
                return Caption;
            var parts = new List<string>();
            if (Studio?.Name is { Length: > 0 } studio)
                parts.Add(studio);
            if (ResumeSeconds > 0 && DurationSeconds > ResumeSeconds)
                parts.Add(FormatTime(DurationSeconds - ResumeSeconds) + " left");
            else if (DurationSeconds > 0)
                parts.Add(FormatTime(DurationSeconds));
            return string.Join("  ·  ", parts);
        }
    }

    public double Progress => DurationSeconds > 0 && ResumeSeconds > 0 ? Math.Clamp(ResumeSeconds / DurationSeconds, 0, 1) : 0;
    public Visibility ProgressVisibility => Progress > 0.01 && Progress < 0.97 ? Visibility.Visible : Visibility.Collapsed;
    public string UnplayedText => string.Empty;
    public Visibility UnplayedVisibility => Visibility.Collapsed;

    public string Glyph => Kind switch
    {
        StashKind.Scene => "",
        StashKind.Performer => "",
        StashKind.Studio => "",
        _ => ""
    };

    /// <summary>"2024-05-18" → "18 May 2024".</summary>
    public string DateText => DateTime.TryParse(Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : Date ?? string.Empty;

    public string ResolutionLabel => Math.Min(Width, Height) switch
    {
        >= 2160 => "4K",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        >= 720 => "720p",
        > 0 => "SD",
        _ => string.Empty
    };

    /// <summary>Stars out of five, from Stash's 1–100 rating.</summary>
    public double? Stars => Rating100 is int rating and > 0 ? Math.Round(rating / 20d, 1) : null;

    /// <summary>The file name without folders, used when a scene has no title.</summary>
    public static string NameFromPath(string? path) => string.IsNullOrEmpty(path) ? "Untitled" : Path.GetFileNameWithoutExtension(path);

    public static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min" : $"{Math.Max(1, (int)Math.Round(time.TotalMinutes))} min";
    }

    private ImageSource? _image;
    public ImageSource? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value))
                return;
            _image = value;
            Changed(nameof(Image));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A point of interest in a scene: a title (or its tag) at a time.</summary>
public sealed record StashMarker(string Id, string Title, double Seconds)
{
    public string Time => TimeSpan.FromSeconds(Seconds) is var time && time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
