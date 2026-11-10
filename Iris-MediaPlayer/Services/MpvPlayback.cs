using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Iris_MediaPlayer.Services;

/// <summary>Owns a libmpv instance configured to render into a WinUI composition swap chain.</summary>
internal sealed class MpvPlayback : IDisposable
{
    private const int PropertyChangeEvent = 22;
    private const int ShutdownEvent = 1;
    private const int EndFileEvent = 7;
    private const int FileLoadedEvent = 8;
    private const int FlagFormat = (int)MpvPropertyFormat.Flag;
    private const int Int64Format = (int)MpvPropertyFormat.Int64;
    private const int DoubleFormat = (int)MpvPropertyFormat.Double;

    private readonly DispatcherQueue _dispatcher;
    private readonly Thread _eventThread;
    private readonly object _gate = new();
    private IntPtr _handle;
    private bool _disposed;

    public event Action<IntPtr>? SwapChainChanged;
    public event Action<string, object?>? PropertyChanged;

    public IntPtr CurrentSwapChain
    {
        get
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero || MpvNative.mpv_get_property(_handle, "display-swapchain", Int64Format, out var value) < 0)
                    return IntPtr.Zero;
                return new IntPtr(value);
            }
        }
    }

    public MpvPlayback(DispatcherQueue dispatcher, int width, int height)
    {
        _dispatcher = dispatcher;
        _handle = MpvNative.mpv_create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("mpv could not create a playback context.");

        try
        {
            SetOption("config", "no");
            SetOption("terminal", "no");
            SetOption("vo", "gpu-next");
            SetOption("gpu-context", "d3d11");
            SetOption("d3d11-output-mode", "composition");
            SetOption("d3d11-composition-size", $"{Math.Max(width, 1)}x{Math.Max(height, 1)}");
            SetOption("hwdec", "auto-safe");
            SetOption("force-window", "immediate");
            SetOption("idle", "yes");
            SetOption("keep-open", "yes");
            SetOption("osc", "no");
            SetOption("osd-level", "0");
            SetOption("input-default-bindings", "no");

            var result = MpvNative.mpv_initialize(_handle);
            ThrowIfError(result, "initialize mpv");

            Observe("display-swapchain", Int64Format);
            Observe("time-pos", DoubleFormat);
            Observe("duration", DoubleFormat);
            Observe("pause", FlagFormat);
            Observe("volume", DoubleFormat);
            Observe("mute", FlagFormat);
            Observe("speed", DoubleFormat);
            Observe("media-title", (int)MpvPropertyFormat.String);
            Observe("idle-active", FlagFormat);

            _eventThread = new Thread(EventLoop)
            {
                IsBackground = true,
                Name = "Iris mpv event loop"
            };
            _eventThread.Start();
        }
        catch
        {
            MpvNative.mpv_terminate_destroy(_handle);
            _handle = IntPtr.Zero;
            throw;
        }
    }

    public void LoadFile(string filePath)
    {
        RunCommand("loadfile", filePath, "replace");
    }

    public void Stop()
    {
        RunCommand("stop");
    }

    public bool TogglePause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_get_property_flag(_handle, "pause", FlagFormat, out var current),
                "read pause state");
            var next = current == 0 ? 1 : 0;
            ThrowIfError(MpvNative.mpv_set_property_flag(_handle, "pause", FlagFormat, ref next), "toggle pause");
            return next != 0;
        }
    }

    public void Seek(double positionSeconds)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_double(_handle, "time-pos", DoubleFormat, ref positionSeconds), "seek");
        }
    }

    public void SetVolume(double volume)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_double(_handle, "volume", DoubleFormat, ref volume), "set volume");
        }
    }

    public void SetMuted(bool muted)
    {
        var value = muted ? 1 : 0;
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_flag(_handle, "mute", FlagFormat, ref value), "set mute");
        }
    }

    public void SetSpeed(double speed)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_double(_handle, "speed", DoubleFormat, ref speed), "set playback speed");
        }
    }

    public void SetAudioTrack(long trackId) => SetTrack("aid", trackId);

    public void SetSubtitleTrack(long trackId) => SetTrack("sid", trackId);

    public IReadOnlyList<MpvTrack> GetTracks(string requestedType)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (MpvNative.mpv_get_property(_handle, "track-list/count", Int64Format, out var count) < 0)
                return Array.Empty<MpvTrack>();

            var tracks = new List<MpvTrack>();
            for (long index = 0; index < count; index++)
            {
                var prefix = $"track-list/{index}/";
                var type = GetStringProperty(prefix + "type");
                if (type is null || !string.Equals(type, requestedType, StringComparison.Ordinal))
                    continue;

                if (MpvNative.mpv_get_property(_handle, prefix + "id", Int64Format, out var id) < 0)
                    continue;

                var selected = MpvNative.mpv_get_property_flag(_handle, prefix + "selected", FlagFormat, out var flag) >= 0
                               && flag != 0;
                tracks.Add(new MpvTrack(id, type, GetStringProperty(prefix + "title"),
                    GetStringProperty(prefix + "lang"), selected));
            }
            return tracks;
        }
    }

    private void SetTrack(string property, long trackId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property(_handle, property, Int64Format, ref trackId), $"select {property} track");
        }
    }

    private string? GetStringProperty(string name)
    {
        if (MpvNative.mpv_get_property_string(_handle, name, (int)MpvPropertyFormat.String, out var value) < 0
            || value == IntPtr.Zero)
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

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1)
            return;

        lock (_gate)
        {
            if (_handle == IntPtr.Zero || _disposed)
                return;
            ThrowIfError(MpvNative.mpv_set_property_string(_handle, "d3d11-composition-size", $"{width}x{height}"), "resize video output");
        }
    }

    private void SetOption(string name, string value)
    {
        ThrowIfError(MpvNative.mpv_set_option_string(_handle, name, value), $"set mpv option '{name}'");
    }

    private void Observe(string name, int format)
    {
        ThrowIfError(MpvNative.mpv_observe_property(_handle, 1, name, format), $"observe mpv property '{name}'");
    }

    private void RunCommand(params string[] values)
    {
        var arguments = new IntPtr[values.Length + 1];
        try
        {
            for (var index = 0; index < values.Length; index++)
                arguments[index] = Marshal.StringToCoTaskMemUTF8(values[index]);

            lock (_gate)
            {
                ThrowIfDisposed();
                ThrowIfError(MpvNative.mpv_command(_handle, arguments), values[0]);
            }
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

    private void EventLoop()
    {
        while (true)
        {
            IntPtr handle;
            lock (_gate)
            {
                if (_handle == IntPtr.Zero)
                    return;
                handle = _handle;
            }

            var eventPointer = MpvNative.mpv_wait_event(handle, 0.5);

            if (eventPointer == IntPtr.Zero)
                continue;

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            if (mpvEvent.EventId == ShutdownEvent)
                return;

            if (mpvEvent.EventId == FileLoadedEvent)
            {
                _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke("file-loaded", true));
                continue;
            }

            if (mpvEvent.EventId == EndFileEvent && mpvEvent.Data != IntPtr.Zero)
            {
                var endFile = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
                if (endFile.Reason == 4)
                    _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke("playback-error", endFile.Error));
                else if (endFile.Reason == 0)
                    _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke("playback-ended", true));
                continue;
            }

            if (mpvEvent.EventId != PropertyChangeEvent || mpvEvent.Data == IntPtr.Zero)
                continue;

            var property = Marshal.PtrToStructure<MpvEventProperty>(mpvEvent.Data);
            var name = Marshal.PtrToStringUTF8(property.Name);
            if (string.IsNullOrEmpty(name) || property.Data == IntPtr.Zero)
                continue;

            object? value = ReadPropertyValue(property);
            if (name == "display-swapchain")
            {
                var swapChain = value is long address ? new IntPtr(address) : IntPtr.Zero;
                _dispatcher.TryEnqueue(() => SwapChainChanged?.Invoke(swapChain));
            }
            else
            {
                _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke(name, value));
            }
        }
    }

    private static object? ReadPropertyValue(MpvEventProperty property)
    {
        return property.Format switch
        {
            FlagFormat => Marshal.ReadInt32(property.Data) != 0,
            Int64Format => Marshal.ReadInt64(property.Data),
            DoubleFormat => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(property.Data)),
            (int)MpvPropertyFormat.String => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data)),
            _ => null
        };
    }

    private static void ThrowIfError(int result, string action)
    {
        if (result < 0)
            throw new InvalidOperationException($"Could not {action}: {MpvNative.GetErrorMessage(result)}");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || _handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(MpvPlayback));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed || _handle == IntPtr.Zero)
                return;
            _disposed = true;
            MpvNative.mpv_command_string(_handle, "quit");
        }

        _eventThread.Join(TimeSpan.FromSeconds(3));
        lock (_gate)
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
                MpvNative.mpv_terminate_destroy(handle);
        }
    }
}
