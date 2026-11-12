using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Iris_MediaPlayer.Services;

internal static class PathUtils
{
    public static bool IsPathWithin(string candidatePath, string parentPath)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSamePath(string firstPath, string secondPath)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(firstPath)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(secondPath)),
            StringComparison.OrdinalIgnoreCase);

    public static string GetFolderName(string path)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
}

/// <summary>Sorts names the way File Explorer does, so "Episode 2" comes before "Episode 10".</summary>
internal sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string first, string second);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);
}
