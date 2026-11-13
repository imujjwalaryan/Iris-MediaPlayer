using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace Iris_MediaPlayer.Services;

public sealed class PlaybackProgressEntry
{
    public double Position { get; set; }
    public double Duration { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Remembers how far into each video the user got, so playback can resume.</summary>
internal sealed class PlaybackProgressStore
{
    /// <summary>Positions earlier than this are treated as "not really started".</summary>
    private const double MinimumResumeSeconds = 10;

    /// <summary>Resume a little before the saved point so the user regains context.</summary>
    private const double ResumeRewindSeconds = 2;

    private readonly Dictionary<string, PlaybackProgressEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public PlaybackProgressStore()
    {
        Load();
    }

    /// <summary>Entries for partly watched videos, most recently watched first.</summary>
    public IEnumerable<(string FilePath, PlaybackProgressEntry Entry)> MostRecent()
        => _entries
            .OrderByDescending(pair => pair.Value.UpdatedUtc)
            .Select(pair => (pair.Key, pair.Value));

    public double? GetResumePosition(string filePath)
        => _entries.TryGetValue(filePath, out var entry)
            ? Math.Max(0, entry.Position - ResumeRewindSeconds)
            : null;

    public void Save(string filePath, double position, double duration)
    {
        if (duration <= 0 || !double.IsFinite(position) || !double.IsFinite(duration))
            return;

        // The library and a standalone player window can run at the same time as separate
        // processes. Start from what is on disk so neither overwrites the other's progress.
        _entries.Clear();
        Load();

        if (position < MinimumResumeSeconds || IsFinished(position, duration))
        {
            if (_entries.Remove(filePath))
                Persist();
            return;
        }

        _entries[filePath] = new PlaybackProgressEntry
        {
            Position = position,
            Duration = duration,
            UpdatedUtc = DateTime.UtcNow
        };
        Persist();
    }

    /// <summary>Forgets a file's progress, e.g. after the file was deleted.</summary>
    public void Remove(string filePath)
    {
        _entries.Clear();
        Load();
        if (_entries.Remove(filePath))
            Persist();
    }

    /// <summary>Close enough to the end (credits, last few seconds) to count as watched.</summary>
    public static bool IsFinished(double position, double duration)
        => position >= duration * 0.95 || duration - position < 15;

    private void Load()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
                return;

            var saved = JsonSerializer.Deserialize(File.ReadAllText(path), IrisJsonContext.Default.DictionaryStringPlaybackProgressEntry);
            if (saved is null)
                return;

            foreach (var (filePath, entry) in saved)
                _entries[filePath] = entry;
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Persist()
    {
        try
        {
            File.WriteAllText(GetFilePath(), JsonSerializer.Serialize(_entries, IrisJsonContext.Default.DictionaryStringPlaybackProgressEntry));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetFilePath()
        => AppStorage.GetFilePath("playback-progress.json");
}
