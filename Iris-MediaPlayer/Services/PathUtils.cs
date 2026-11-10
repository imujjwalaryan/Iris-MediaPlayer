using System;
using System.IO;

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
