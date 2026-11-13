using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Services;

/// <summary>One line in the Details dialog, such as "Codec  HEVC Main 10".</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>A group of lines in the Details dialog: File, Video, Audio 1, Subtitles.</summary>
public sealed record DetailSection(string Title, IReadOnlyList<DetailRow> Rows);

/// <summary>
/// Reads technical details of a video by opening it in a hidden mpv instance with no video or
/// audio output. mpv understands every format Iris plays, so this works for MKV and HDR files
/// where Windows' own file properties come back empty.
/// </summary>
public static class MediaProbe
{
    private const int FileLoadedEvent = 8;
    private const int EndFileEvent = 7;
    private const int ShutdownEvent = 1;
    private const int Int64Format = (int)MpvPropertyFormat.Int64;
    private const int DoubleFormat = (int)MpvPropertyFormat.Double;
    private const int FlagFormat = (int)MpvPropertyFormat.Flag;
    private const int StringFormat = (int)MpvPropertyFormat.String;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(2);

    public static Task<IReadOnlyList<DetailSection>> ReadAsync(string filePath) => Task.Run(() => Read(filePath));

    private static IReadOnlyList<DetailSection> Read(string filePath)
    {
        var sections = new List<DetailSection>();
        var info = new FileInfo(filePath);

        var handle = MpvNative.mpv_create();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Iris couldn’t start mpv to read this file.");

        try
        {
            foreach (var (name, value) in new[]
                     {
                         ("config", "no"), ("terminal", "no"), ("msg-level", "all=no"), ("vo", "null"), ("ao", "null"),
                         ("pause", "yes"), ("hwdec", "no"), ("idle", "yes"), ("load-scripts", "no"), ("ytdl", "no"),
                         ("osc", "no"), ("input-default-bindings", "no"), ("sub-auto", "no")
                     })
                MpvNative.mpv_set_option_string(handle, name, value);

            if (MpvNative.mpv_initialize(handle) < 0)
                throw new InvalidOperationException("Iris couldn’t start mpv to read this file.");

            Command(handle, "loadfile", filePath, "replace");
            if (!WaitForFileLoaded(handle))
                throw new InvalidOperationException("mpv couldn’t open this file.");

            // Colour and pixel details only exist once a frame has been decoded.
            var watch = Stopwatch.StartNew();
            while (GetLong(handle, "video-params/w") is null && GetLong(handle, "track-list/count") is > 0
                   && HasTrack(handle, "video") && watch.Elapsed < FirstFrameTimeout)
                MpvNative.mpv_wait_event(handle, 0.05);

            sections.Add(ReadFileSection(handle, info));
            sections.AddRange(ReadTrackSections(handle));
        }
        finally
        {
            MpvNative.mpv_terminate_destroy(handle);
        }

        return sections;
    }

    private static bool WaitForFileLoaded(IntPtr handle)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < OpenTimeout)
        {
            var eventPointer = MpvNative.mpv_wait_event(handle, 0.25);
            if (eventPointer == IntPtr.Zero)
                continue;

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            switch (mpvEvent.EventId)
            {
                case FileLoadedEvent:
                    return true;
                case EndFileEvent:
                case ShutdownEvent:
                    return false;
            }
        }
        return false;
    }

    // ---------- Sections ----------

    private static DetailSection ReadFileSection(IntPtr handle, FileInfo info)
    {
        var rows = new List<DetailRow>
        {
            new("Name", info.Name),
            new("Location", info.DirectoryName ?? string.Empty)
        };

        var format = GetString(handle, "file-format");
        if (!string.IsNullOrEmpty(format))
            rows.Add(new("Container", DescribeContainer(format, info.Extension)));

        var size = info.Exists ? info.Length : GetLong(handle, "file-size") ?? 0;
        if (size > 0)
            rows.Add(new("Size", $"{FormatBytes(size)}  ({size.ToString("N0", CultureInfo.CurrentCulture)} bytes)"));

        var duration = GetDouble(handle, "duration");
        if (duration is > 0)
        {
            rows.Add(new("Duration", FormatDuration(duration.Value)));
            if (size > 0)
                rows.Add(new("Overall bitrate", FormatBitrate(size * 8 / duration.Value)));
        }

        var chapters = GetLong(handle, "chapter-list/count");
        if (chapters is > 1)
            rows.Add(new("Chapters", chapters.Value.ToString(CultureInfo.CurrentCulture)));

        var title = GetString(handle, "metadata/by-key/title");
        if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, Path.GetFileNameWithoutExtension(info.Name), StringComparison.Ordinal))
            rows.Add(new("Title tag", title));
        var encoder = GetString(handle, "metadata/by-key/encoder") ?? GetString(handle, "metadata/by-key/ENCODER");
        if (!string.IsNullOrWhiteSpace(encoder))
            rows.Add(new("Encoded with", encoder));

        if (info.Exists)
        {
            rows.Add(new("Created", info.CreationTime.ToString("f", CultureInfo.CurrentCulture)));
            rows.Add(new("Modified", info.LastWriteTime.ToString("f", CultureInfo.CurrentCulture)));
        }

        return new DetailSection("File", rows);
    }

    private static IEnumerable<DetailSection> ReadTrackSections(IntPtr handle)
    {
        var count = GetLong(handle, "track-list/count") ?? 0;
        var videoNumber = 0;
        var audioNumber = 0;
        var subtitleRows = new List<DetailRow>();
        var sections = new List<DetailSection>();

        for (var index = 0; index < count; index++)
        {
            var prefix = $"track-list/{index}/";
            var type = GetString(handle, prefix + "type");
            if (type == "video" && GetFlag(handle, prefix + "image") != true)
            {
                videoNumber++;
                sections.Add(ReadVideoTrack(handle, prefix, videoNumber));
            }
            else if (type == "audio")
            {
                audioNumber++;
                sections.Add(ReadAudioTrack(handle, prefix, audioNumber));
            }
            else if (type == "sub")
            {
                var parts = new List<string>();
                AddIf(parts, DescribeCodec(handle, prefix));
                if (GetFlag(handle, prefix + "default") == true)
                    parts.Add("Default");
                if (GetFlag(handle, prefix + "forced") == true)
                    parts.Add("Forced");
                if (GetFlag(handle, prefix + "external") == true)
                    parts.Add("External file");
                subtitleRows.Add(new(TrackName(handle, prefix, $"Track {subtitleRows.Count + 1}"), string.Join("  ·  ", parts)));
            }
        }

        if (subtitleRows.Count > 0)
            sections.Add(new DetailSection("Subtitles", subtitleRows));
        return sections;
    }

    private static DetailSection ReadVideoTrack(IntPtr handle, string prefix, int number)
    {
        var rows = new List<DetailRow>();
        AddRow(rows, "Codec", DescribeCodec(handle, prefix));

        var width = GetLong(handle, prefix + "demux-w") ?? GetLong(handle, "video-params/w");
        var height = GetLong(handle, prefix + "demux-h") ?? GetLong(handle, "video-params/h");
        if (width is > 0 && height is > 0)
            rows.Add(new("Resolution", $"{width} × {height}  ({DescribeAspect(width.Value, height.Value)})"));

        var fps = GetDouble(handle, prefix + "demux-fps") ?? GetDouble(handle, "container-fps");
        if (fps is > 0)
            rows.Add(new("Frame rate", $"{fps.Value.ToString("0.###", CultureInfo.CurrentCulture)} fps"));

        var bitrate = GetDouble(handle, prefix + "demux-bitrate") ?? GetLong(handle, prefix + "demux-bitrate");
        if (bitrate is > 0)
            rows.Add(new("Bitrate", FormatBitrate(bitrate.Value)));

        // Only for the playing video track: these come from the decoder.
        if (GetFlag(handle, prefix + "selected") == true)
        {
            AddRow(rows, "Pixel format", GetString(handle, "video-params/pixelformat"));
            var range = DescribeRange(GetString(handle, "video-params/gamma"));
            if (GetString(handle, "video-params/colormatrix") == "dolbyvision")
                range = range is null ? "Dolby Vision" : $"Dolby Vision  ·  {range}";
            AddRow(rows, "Dynamic range", range);
            AddRow(rows, "Colour primaries", GetString(handle, "video-params/primaries"));
            AddRow(rows, "Colour matrix", GetString(handle, "video-params/colormatrix"));
            AddRow(rows, "Transfer", GetString(handle, "video-params/gamma"));
            AddRow(rows, "Range", GetString(handle, "video-params/colorlevels"));
        }

        AddRow(rows, "Language", LanguageName(GetString(handle, prefix + "lang")));
        AddRow(rows, "Title", GetString(handle, prefix + "title"));
        return new DetailSection(number == 1 ? "Video" : $"Video {number}", rows);
    }

    private static DetailSection ReadAudioTrack(IntPtr handle, string prefix, int number)
    {
        var rows = new List<DetailRow>();
        AddRow(rows, "Codec", DescribeCodec(handle, prefix));

        var layout = GetString(handle, prefix + "demux-channels");
        var channels = GetLong(handle, prefix + "demux-channel-count");
        if (!string.IsNullOrEmpty(layout) || channels is > 0)
            rows.Add(new("Channels", DescribeChannels(layout, channels)));

        var sampleRate = GetLong(handle, prefix + "demux-samplerate");
        if (sampleRate is > 0)
            rows.Add(new("Sample rate", $"{sampleRate.Value / 1000d:0.#} kHz"));

        var bitrate = GetDouble(handle, prefix + "demux-bitrate") ?? GetLong(handle, prefix + "demux-bitrate");
        if (bitrate is > 0)
            rows.Add(new("Bitrate", FormatBitrate(bitrate.Value)));

        var language = LanguageName(GetString(handle, prefix + "lang"));
        AddRow(rows, "Language", language);
        AddRow(rows, "Title", GetString(handle, prefix + "title"));
        if (GetFlag(handle, prefix + "default") == true)
            rows.Add(new("Default track", "Yes"));

        var name = language is { Length: > 0 } ? $"Audio {number}  ·  {language}" : $"Audio {number}";
        return new DetailSection(name, rows);
    }

    // ---------- Descriptions ----------

    private static string? DescribeCodec(IntPtr handle, string prefix)
    {
        var codec = GetString(handle, prefix + "codec");
        var description = GetString(handle, prefix + "codec-desc");
        var profile = GetString(handle, prefix + "codec-profile");
        var name = !string.IsNullOrWhiteSpace(description) ? description : FriendlyCodec(codec);
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (!string.IsNullOrWhiteSpace(profile))
            name += $"  ·  {profile}";
        if (!string.IsNullOrWhiteSpace(codec) && !string.IsNullOrWhiteSpace(description))
            name += $"  ({codec})";
        return name;
    }

    private static string? FriendlyCodec(string? codec) => codec?.ToLowerInvariant() switch
    {
        null => null,
        "eac3" => "Dolby Digital Plus (E-AC-3)",
        "ac3" => "Dolby Digital (AC-3)",
        "truehd" => "Dolby TrueHD",
        "dts" => "DTS",
        "aac" => "AAC",
        "opus" => "Opus",
        "flac" => "FLAC",
        "mp3" => "MP3",
        "vorbis" => "Vorbis",
        "hevc" => "H.265 / HEVC",
        "h264" => "H.264 / AVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "subrip" => "SRT",
        "ass" => "ASS",
        "hdmv_pgs_subtitle" => "PGS (image)",
        "dvd_subtitle" => "VobSub (image)",
        "mov_text" => "MP4 text",
        "webvtt" => "WebVTT",
        _ => codec.ToUpperInvariant()
    };

    /// <summary>"en" or "eng" becomes "English"; unknown codes are shown as they are.</summary>
    private static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code is "und" or "unk")
            return null;
        try
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                if (string.Equals(culture.TwoLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(culture.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase))
                    return culture.EnglishName;
            }
        }
        catch (Exception) { }
        return code;
    }

    private static string TrackName(IntPtr handle, string prefix, string fallback)
    {
        var language = LanguageName(GetString(handle, prefix + "lang"));
        var title = GetString(handle, prefix + "title");
        return (language, title) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{language} · {title}",
            ({ Length: > 0 }, _) => language!,
            (_, { Length: > 0 }) => title!,
            _ => fallback
        };
    }

    private static string DescribeContainer(string format, string extension)
    {
        var known = format.Split(',')[0] switch
        {
            "matroska" or "mkv" => "Matroska",
            "mov" or "mp4" => "MPEG-4 / QuickTime",
            "avi" => "AVI",
            "mpegts" => "MPEG transport stream",
            "asf" => "Windows Media (ASF)",
            "flv" => "Flash Video",
            "webm" => "WebM",
            "mpeg" => "MPEG program stream",
            _ => format
        };
        return string.IsNullOrEmpty(extension) ? known : $"{known}  ({extension.TrimStart('.').ToUpperInvariant()})";
    }

    private static string? DescribeRange(string? transfer) => transfer switch
    {
        null => null,
        "pq" => "HDR10 (PQ)",
        "hlg" => "HDR (HLG)",
        _ => "SDR"
    };

    private static string DescribeChannels(string? layout, long? count)
    {
        if (layout is not null && layout.StartsWith("unknown", StringComparison.Ordinal))
            layout = count switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => null };
        var friendly = layout switch
        {
            "mono" => "Mono",
            "stereo" => "Stereo",
            "5.1" or "5.1(side)" => "5.1 surround",
            "7.1" or "7.1(wide)" => "7.1 surround",
            _ => layout
        };
        return count is > 0 && !string.IsNullOrEmpty(friendly) ? $"{friendly}  ({count} channels)"
            : count is > 0 ? $"{count} channels"
            : friendly ?? string.Empty;
    }

    private static string DescribeAspect(long width, long height)
    {
        var ratio = (double)width / height;
        foreach (var (value, label) in new[] { (16 / 9d, "16:9"), (4 / 3d, "4:3"), (21 / 9d, "21:9"), (2.39, "2.39:1"), (2.35, "2.35:1"), (1.85, "1.85:1"), (64 / 27d, "64:27"), (1d, "1:1") })
        {
            if (Math.Abs(ratio - value) < 0.02)
                return label;
        }
        return $"{ratio.ToString("0.00", CultureInfo.CurrentCulture)}:1";
    }

    private static string FormatBitrate(double bitsPerSecond)
        => bitsPerSecond >= 1_000_000
            ? $"{(bitsPerSecond / 1_000_000).ToString("0.0#", CultureInfo.CurrentCulture)} Mbps"
            : $"{(bitsPerSecond / 1_000).ToString("0", CultureInfo.CurrentCulture)} kbps";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private static string FormatDuration(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static void AddRow(List<DetailRow> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            rows.Add(new(label, value));
    }

    private static void AddIf(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add(value);
    }

    // ---------- mpv access ----------

    private static bool HasTrack(IntPtr handle, string type)
    {
        var count = GetLong(handle, "track-list/count") ?? 0;
        for (var index = 0; index < count; index++)
        {
            if (GetString(handle, $"track-list/{index}/type") == type)
                return true;
        }
        return false;
    }

    private static string? GetString(IntPtr handle, string name)
    {
        if (MpvNative.mpv_get_property_string(handle, name, StringFormat, out var value) < 0 || value == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            MpvNative.mpv_free(value);
        }
    }

    private static long? GetLong(IntPtr handle, string name)
        => MpvNative.mpv_get_property(handle, name, Int64Format, out var value) >= 0 ? value : null;

    private static double? GetDouble(IntPtr handle, string name)
        => MpvNative.mpv_get_property_double(handle, name, DoubleFormat, out var value) >= 0 ? value : null;

    private static bool? GetFlag(IntPtr handle, string name)
        => MpvNative.mpv_get_property_flag(handle, name, FlagFormat, out var value) >= 0 ? value != 0 : null;

    private static void Command(IntPtr handle, params string[] values)
    {
        var arguments = new IntPtr[values.Length + 1];
        try
        {
            for (var index = 0; index < values.Length; index++)
                arguments[index] = Marshal.StringToCoTaskMemUTF8(values[index]);
            var result = MpvNative.mpv_command(handle, arguments);
            if (result < 0)
                throw new InvalidOperationException(MpvNative.GetErrorMessage(result));
        }
        finally
        {
            foreach (var argument in arguments)
            {
                if (argument != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(argument);
            }
        }
    }
}
