namespace Iris_MediaPlayer.Services;

/// <summary>A chapter mark in the playing file. <see cref="Time"/> is where it starts, in seconds.</summary>
public sealed record MpvChapter(int Index, double Time, string? Title)
{
    /// <summary>The chapter's own title, or "Chapter 3" when the file doesn't name it.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? $"Chapter {Index + 1}" : Title.Trim();
}
