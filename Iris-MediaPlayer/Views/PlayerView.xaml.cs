using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Globalization;
using System.Linq;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// In-app video player. Owns the mpv instance, the video surface and every playback control.
/// The window decides fullscreen and navigation; this view only asks for them through events.
/// </summary>
public sealed partial class PlayerView : UserControl
{
    private readonly DispatcherQueueTimer _controlsHideTimer;
    private MpvPlayback? _mpv;
    private MediaItem? _pendingMedia;
    private bool _isUpdatingSeek;
    private bool _isSeeking;
    private bool _isPaused = true;
    private bool _isMuted;
    private bool _isUpdatingTrackControls;
    private bool _isUpdatingPlaybackSpeed;
    private bool _isFullscreen;
    private double _durationSeconds;
    private IntPtr _boundSwapChain;

    public PlayerView()
    {
        InitializeComponent();
        _controlsHideTimer = DispatcherQueue.GetForCurrentThread()!.CreateTimer();
        _controlsHideTimer.Interval = TimeSpan.FromSeconds(3);
        _controlsHideTimer.IsRepeating = false;
        _controlsHideTimer.Tick += (_, _) => SetControlsVisible(false);
    }

    /// <summary>The user pressed Escape and wants to leave the player.</summary>
    public event EventHandler? BackRequested;

    /// <summary>The user pressed F or the fullscreen button.</summary>
    public event EventHandler? FullscreenToggleRequested;

    /// <summary>mpv finished opening a file, so it can be recorded as recently played.</summary>
    public event EventHandler<MediaItem>? MediaLoaded;

    // ---------- Public API used by the window ----------

    public void Play(MediaItem media)
    {
        _controlsHideTimer.Stop();
        SetControlsVisible(true);
        _pendingMedia = media;

        PlayerTitle.Text = media.Title;
        PlaybackStatusText.Text = "Starting mpv…";
        PlaybackProgress.IsActive = true;
        PlayerStatus.Visibility = Visibility.Visible;
        PlayPauseButton.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        _durationSeconds = 0;
        _isPaused = true;

        if (_mpv is not null)
            LoadPendingMedia();
        else if (VideoHost.ActualWidth > 0 && VideoHost.ActualHeight > 0)
            InitializePlayer();
        // Otherwise VideoHost_SizeChanged starts mpv once the view has been laid out.
    }

    public void Stop() => _mpv?.Stop();

    public void ShowStatusMessage(string message) => PlaybackStatusText.Text = message;

    /// <summary>Updates the fullscreen icon and auto-hiding controls. The window switches the presenter.</summary>
    public void SetFullscreen(bool enabled)
    {
        _isFullscreen = enabled;
        FullscreenIcon.Glyph = enabled ? "" : "";
        _controlsHideTimer.Stop();
        SetControlsVisible(true);
        if (enabled)
            _controlsHideTimer.Start();
    }

    /// <summary>Square corners and overlaid bars when flat (fullscreen); rounded and stacked otherwise.</summary>
    public void ApplyFrameMode(bool flat)
    {
        PlayerRoot.CornerRadius = flat ? new CornerRadius(0) : new CornerRadius(15);
        // Windowed: video sits between the header and controls bars, so it never reaches a corner.
        // Fullscreen: video spans all rows and the bars overlay it (auto-hide).
        Grid.SetRow(VideoHost, flat ? 0 : 1);
        Grid.SetRowSpan(VideoHost, flat ? 3 : 1);
        PlayerHeaderBar.CornerRadius = flat ? new CornerRadius(0) : new CornerRadius(15, 15, 0, 0);
        PlayerControlsBar.CornerRadius = flat ? new CornerRadius(0) : new CornerRadius(0, 0, 15, 15);
    }

    /// <summary>Releases mpv and the swap chain. Call once when the window closes.</summary>
    public void Shutdown()
    {
        _controlsHideTimer.Stop();
        if (_mpv is not null)
        {
            _mpv.SwapChainChanged -= Mpv_SwapChainChanged;
            _mpv.PropertyChanged -= Mpv_PropertyChanged;
        }
        if (_boundSwapChain != IntPtr.Zero)
            SwapChainPanelInterop.SetSwapChain(VideoPanel, IntPtr.Zero);
        _mpv?.Dispose();
        _mpv = null;
    }

    // ---------- mpv lifecycle ----------

    private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(e.NewSize.Width * scale));
        var height = Math.Max(1, (int)Math.Round(e.NewSize.Height * scale));

        if (_mpv is null)
        {
            if (_pendingMedia is not null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                InitializePlayer(width, height);
            return;
        }

        _mpv.Resize(width, height);
    }

    private void InitializePlayer(int? width = null, int? height = null)
    {
        if (_mpv is not null)
            return;

        try
        {
            var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
            var outputWidth = width ?? Math.Max(1, (int)Math.Round(VideoHost.ActualWidth * scale));
            var outputHeight = height ?? Math.Max(1, (int)Math.Round(VideoHost.ActualHeight * scale));
            var dispatcher = DispatcherQueue.GetForCurrentThread()
                             ?? throw new InvalidOperationException("The Windows UI dispatcher is unavailable.");

            _mpv = new MpvPlayback(dispatcher, outputWidth, outputHeight);
            _mpv.SwapChainChanged += Mpv_SwapChainChanged;
            _mpv.PropertyChanged += Mpv_PropertyChanged;

            var swapChain = _mpv.CurrentSwapChain;
            if (swapChain != IntPtr.Zero)
                Mpv_SwapChainChanged(swapChain);

            _mpv.SetVolume(VolumeSlider.Value);
            LoadPendingMedia();
        }
        catch (Exception exception)
        {
            _mpv?.Dispose();
            _mpv = null;
            PlaybackProgress.IsActive = false;
            PlaybackStatusText.Text = $"Could not start mpv: {exception.Message}";
            PlayerStatus.Visibility = Visibility.Visible;
        }
    }

    private void LoadPendingMedia()
    {
        if (_mpv is null || _pendingMedia is null)
            return;

        try
        {
            _mpv.LoadFile(_pendingMedia.FilePath);
        }
        catch (Exception exception)
        {
            PlaybackProgress.IsActive = false;
            PlaybackStatusText.Text = $"Could not open this video: {exception.Message}";
            PlayerStatus.Visibility = Visibility.Visible;
        }
    }

    private void Mpv_SwapChainChanged(IntPtr swapChain)
    {
        _boundSwapChain = swapChain;
        SwapChainPanelInterop.SetSwapChain(VideoPanel, swapChain);
    }

    private void Mpv_PropertyChanged(string name, object? value)
    {
        switch (name)
        {
            case "file-loaded":
                PlaybackProgress.IsActive = false;
                PlayerStatus.Visibility = Visibility.Collapsed;
                PlayPauseButton.IsEnabled = true;
                SeekBackwardButton.IsEnabled = true;
                SeekForwardButton.IsEnabled = true;
                MuteButton.IsEnabled = true;
                SeekSlider.IsEnabled = _durationSeconds > 0;
                RefreshTrackOptions();
                if (_pendingMedia is not null)
                    MediaLoaded?.Invoke(this, _pendingMedia);
                break;
            case "time-pos" when value is double position:
                CurrentTimeText.Text = FormatTime(position);
                if (!_isSeeking)
                    SetSeekSliderValue(position);
                break;
            case "duration" when value is double duration:
                _durationSeconds = Math.Max(0, duration);
                DurationText.Text = FormatTime(_durationSeconds);
                SeekSlider.IsEnabled = _durationSeconds > 0;
                SetSeekSliderMaximum(_durationSeconds);
                break;
            case "pause" when value is bool paused:
                _isPaused = paused;
                PlayPauseIcon.Glyph = paused ? "" : "";
                break;
            case "volume" when value is double volume:
                VolumeSlider.Value = Math.Clamp(volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
                break;
            case "mute" when value is bool muted:
                _isMuted = muted;
                MuteIcon.Glyph = muted ? "" : "";
                break;
            case "speed" when value is double speed:
                UpdatePlaybackSpeedSelection(speed);
                break;
            case "media-title" when value is string title && !string.IsNullOrWhiteSpace(title):
                if (_pendingMedia is null)
                    PlayerTitle.Text = title;
                break;
            case "playback-error" when value is int error:
                PlaybackProgress.IsActive = false;
                PlaybackStatusText.Text = $"mpv could not play this file (error {error}).";
                PlayerStatus.Visibility = Visibility.Visible;
                PlayPauseButton.IsEnabled = false;
                break;
            case "playback-ended":
                PlaybackProgress.IsActive = false;
                PlayPauseButton.IsEnabled = false;
                break;
        }
    }

    // ---------- Play, seek and volume ----------

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        if (_mpv is null)
            return;

        try
        {
            _isPaused = _mpv.TogglePause();
            PlayPauseIcon.Glyph = _isPaused ? "" : "";
        }
        catch (Exception exception)
        {
            PlaybackStatusText.Text = $"Could not toggle playback: {exception.Message}";
            PlayerStatus.Visibility = Visibility.Visible;
        }
    }

    private void SetSeekSliderMaximum(double duration)
    {
        _isUpdatingSeek = true;
        SeekSlider.Maximum = Math.Max(1, duration);
        _isUpdatingSeek = false;
    }

    private void SetSeekSliderValue(double position)
    {
        _isUpdatingSeek = true;
        SeekSlider.Value = Math.Clamp(position, SeekSlider.Minimum, SeekSlider.Maximum);
        _isUpdatingSeek = false;
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isUpdatingSeek && !_isSeeking && _mpv is not null && _durationSeconds > 0)
            _mpv.Seek(e.NewValue);
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isSeeking = true;

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isSeeking = false;
        if (_mpv is not null && _durationSeconds > 0)
            _mpv.Seek(SeekSlider.Value);
    }

    private void SeekBarHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!SeekSlider.IsEnabled || _durationSeconds <= 0 || SeekBarHost.ActualWidth <= 0)
            return;

        var pointerX = e.GetCurrentPoint(SeekBarHost).Position.X;
        var progress = Math.Clamp(pointerX / SeekBarHost.ActualWidth, 0, 1);
        SeekPreviewText.Text = FormatTime(progress * _durationSeconds);
        SeekPreviewBadge.Visibility = Visibility.Visible;
        var badgeWidth = SeekPreviewBadge.Width > 0 ? SeekPreviewBadge.Width : 52;
        Canvas.SetLeft(
            SeekPreviewBadge,
            Math.Clamp(pointerX - badgeWidth / 2, 0, Math.Max(0, SeekBarHost.ActualWidth - badgeWidth)));
    }

    private void SeekBarHost_PointerExited(object sender, PointerRoutedEventArgs e)
        => SeekPreviewBadge.Visibility = Visibility.Collapsed;

    private void SeekBackward_Click(object sender, RoutedEventArgs e) => SeekBy(-10);

    private void SeekForward_Click(object sender, RoutedEventArgs e) => SeekBy(10);

    private void SeekBy(double seconds)
    {
        if (_mpv is null || _durationSeconds <= 0)
            return;

        var position = Math.Clamp(SeekSlider.Value + seconds, 0, _durationSeconds);
        SetSeekSliderValue(position);
        _mpv.Seek(position);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => _mpv?.SetVolume(e.NewValue);

    private void MuteButton_Click(object sender, RoutedEventArgs e) => _mpv?.SetMuted(!_isMuted);

    // ---------- Speed and tracks ----------

    private void PlaybackSpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPlaybackSpeed || _mpv is null || PlaybackSpeedCombo.SelectedItem is not ComboBoxItem item)
            return;

        if (double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            _mpv.SetSpeed(speed);
    }

    private void UpdatePlaybackSpeedSelection(double speed)
    {
        var closestItem = PlaybackSpeedCombo.Items
            .OfType<ComboBoxItem>()
            .Select(item => (Item: item, Parsed: double.TryParse(item.Tag?.ToString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN))
            .Where(option => double.IsFinite(option.Parsed))
            .OrderBy(option => Math.Abs(option.Parsed - speed))
            .FirstOrDefault();

        if (closestItem.Item is null || Math.Abs(closestItem.Parsed - speed) > 0.001)
            return;

        _isUpdatingPlaybackSpeed = true;
        PlaybackSpeedCombo.SelectedItem = closestItem.Item;
        _isUpdatingPlaybackSpeed = false;
    }

    private void RefreshTrackOptions()
    {
        if (_mpv is null)
            return;

        _isUpdatingTrackControls = true;
        var audioTracks = _mpv.GetTracks("audio");
        AudioTrackCombo.Items.Clear();
        foreach (var track in audioTracks)
            AudioTrackCombo.Items.Add(new ComboBoxItem { Content = track.DisplayName, Tag = track.Id });
        AudioTrackCombo.SelectedItem = audioTracks.Select((track, index) => (track, index))
            .Where(item => item.track.IsSelected)
            .Select(item => AudioTrackCombo.Items[item.index])
            .FirstOrDefault();
        AudioTrackCombo.IsEnabled = audioTracks.Count > 0;

        var subtitleTracks = _mpv.GetTracks("sub");
        SubtitleTrackCombo.Items.Clear();
        SubtitleTrackCombo.Items.Add(new ComboBoxItem { Content = "Off", Tag = -1L });
        foreach (var track in subtitleTracks)
            SubtitleTrackCombo.Items.Add(new ComboBoxItem { Content = track.DisplayName, Tag = track.Id });
        var selectedSubtitle = subtitleTracks.FirstOrDefault(track => track.IsSelected);
        SubtitleTrackCombo.SelectedItem = selectedSubtitle is null
            ? SubtitleTrackCombo.Items[0]
            : SubtitleTrackCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selectedSubtitle.Id));
        SubtitleTrackCombo.IsEnabled = subtitleTracks.Count > 0;
        _isUpdatingTrackControls = false;
    }

    private void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTrackControls || _mpv is null || AudioTrackCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not long trackId)
            return;
        _mpv.SetAudioTrack(trackId);
    }

    private void SubtitleTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTrackControls || _mpv is null || SubtitleTrackCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not long trackId)
            return;
        _mpv.SetSubtitleTrack(trackId);
    }

    // ---------- Fullscreen, keyboard and auto-hiding controls ----------

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        => FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);

    private void Player_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullscreen || Visibility != Visibility.Visible)
            return;

        SetControlsVisible(true);
        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    private void Player_KeyDown(object sender, KeyRoutedEventArgs e)
        => e.Handled = HandlePlayerKey(e.Key);

    private void PlayerKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        => args.Handled = HandlePlayerKey(sender.Key);

    private bool HandlePlayerKey(Windows.System.VirtualKey key)
    {
        if (Visibility != Visibility.Visible)
            return false;

        switch (key)
        {
            case Windows.System.VirtualKey.Escape:
                BackRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case Windows.System.VirtualKey.F:
                FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case Windows.System.VirtualKey.Space:
                TogglePause();
                return true;
            default:
                return false;
        }
    }

    private void SetControlsVisible(bool visible)
    {
        var opacity = visible ? 1d : 0d;
        PlayerHeaderBar.Opacity = opacity;
        PlayerControlsBar.Opacity = opacity;
        PlayerHeaderBar.IsHitTestVisible = visible;
        PlayerControlsBar.IsHitTestVisible = visible;
    }

    private void PlayerControlsBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Min(e.NewSize.Width, PlayerControlsBar.ActualWidth);

        // Progressively drop the least important controls so the seek bar always keeps room.
        VolumeSlider.Visibility = width < 760 ? Visibility.Collapsed : Visibility.Visible;

        var narrow = width < 560;
        var seekButtonsVisibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        SeekBackwardButton.Visibility = seekButtonsVisibility;
        SeekForwardButton.Visibility = seekButtonsVisibility;

        var tiny = width < 420;
        TimeSeparatorText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        DurationText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        TimeGroup.MinWidth = tiny ? 40 : 104;

        PlayerControlsBar.Padding = narrow ? new Thickness(8, 6, 8, 6) : new Thickness(12, 6, 12, 6);
        ControlsRow.ColumnSpacing = narrow ? 8 : 12;
    }
}
