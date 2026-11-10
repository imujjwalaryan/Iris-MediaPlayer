using Iris_MediaPlayer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Services;

public static class MediaLibraryService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".flv", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg",
        ".mts", ".ogv", ".ts", ".webm", ".wmv"
    };

    public static string[] SupportedVideoExtensions => VideoExtensions.OrderBy(extension => extension).ToArray();

    public static bool IsSupportedVideoFile(string filePath)
        => VideoExtensions.Contains(Path.GetExtension(filePath));

    public static MediaItem? TryCreateMediaItem(string filePath)
    {
        try
        {
            if (!IsSupportedVideoFile(filePath))
                return null;

            var file = new FileInfo(filePath);
            if (!file.Exists)
                return null;

            var folderPath = Path.GetDirectoryName(file.FullName) ?? string.Empty;
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath));
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "Video file";

            return new MediaItem(file.FullName, Path.GetFileNameWithoutExtension(file.Name), folderPath,
                folderName, file.Extension, file.LastWriteTime);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Task<IReadOnlyList<MediaItem>> ScanFoldersAsync(IEnumerable<string> folderPaths)
    {
        var paths = folderPaths.ToArray();
        return Task.Run<IReadOnlyList<MediaItem>>(() => ScanFolders(paths));
    }

    private static IReadOnlyList<MediaItem> ScanFolders(IEnumerable<string> folderPaths)
    {
        var results = new List<MediaItem>();

        foreach (var rootPath in folderPaths)
        {
            if (!Directory.Exists(rootPath))
                continue;

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootPath);

            while (pendingDirectories.Count > 0)
            {
                var currentPath = pendingDirectories.Pop();
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(currentPath);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & FileAttributes.ReparsePoint) == 0)
                                pendingDirectories.Push(entry);
                            continue;
                        }

                        if (!IsSupportedVideoFile(entry))
                            continue;

                        var media = TryCreateMediaItem(entry);
                        if (media is not null)
                            results.Add(media with { LibraryRootPath = rootPath });
                    }
                    catch (IOException)
                    {
                        // Skip files that disappear or become unavailable while scanning.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip entries the current user cannot inspect.
                    }
                }
            }
        }

        return results
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
