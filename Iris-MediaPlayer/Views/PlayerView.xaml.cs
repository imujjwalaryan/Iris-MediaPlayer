using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Globalization.NumberFormatting;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// In-app video player. Owns the mpv instance, the video surface, the play queue and every
/// playback control. The window decides fullscreen and navigation; this view asks for them
/// through events.
/// </summary>
public sealed partial class PlayerView : UserControl
{
    private const int AutoplayCountdownSeconds = 5;
    private const double ProgressSaveIntervalSeconds = 5;
    private const double PreviousRestartThresholdSeconds = 5;

    private readonly DispatcherQueueTimer _controlsHideTimer;
    private readonly DispatcherQueueTimer _singleTapTimer;
    private readonly DispatcherQueueTimer _resumeBannerTimer;
    private readonly DispatcherQueueTimer _osdTimer;
    private readonly DispatcherQueueTimer _autoplayTimer;

    private LibraryStore? _store;
    private MpvPlayback? _mpv;
    private IntPtr _boundSwapChain;

    // Queue and current file
    private IReadOnlyList<MediaItem> _queue = [];
    private int _queueIndex;
    private MediaItem? _currentMedia;
    private double _resumedFrom;

    // Playback state reported by mpv
    private bool _isFileLoaded;
    private bool _isAtEnd;
    private bool _isPaused = true;
    private bool _isMuted;
    private double _durationSeconds;
    private double _positionSeconds;
    private double _lastSavedPosition;

    // UI state
    private bool _isInitializing = true;
    private bool _isUpdatingSeek;
    private bool _isSeeking;
    private bool _isUpdatingTrackControls;
    private bool _isUpdatingPlaybackSpeed;
    private bool _isUpdatingFileSettings;
    private bool _isFullscreen;
    private bool _isPointerOverBars;
    private bool _isCursorHidden;
    private bool _isUpdatingZoom;
    private VideoZoomMode _zoomMode;
    private int _autoplaySecondsLeft;
    private DateTime _lastDoubleTapUtc;

    public PlayerView()
    {
        var savedVolume = PlayerSettings.Volume;
        InitializeComponent();

        var dispatcher = DispatcherQueue.GetForCurrentThread()!;
        _controlsHideTimer = CreateTimer(dispatcher, TimeSpan.FromSeconds(3), ControlsHideTimer_Tick);
        _singleTapTimer = CreateTimer(dispatcher, TimeSpan.FromMilliseconds(250), (_, _) => TogglePause());
        _resumeBannerTimer = CreateTimer(dispatcher, TimeSpan.FromSeconds(8), (_, _) => HideResumeBanner());
        _osdTimer = CreateTimer(dispatcher, TimeSpan.FromSeconds(1.5), (_, _) => OsdBanner.Visibility = Visibility.Collapsed);
        _autoplayTimer = CreateTimer(dispatcher, TimeSpan.FromSeconds(1), AutoplayTimer_Tick, repeating: true);

        var delayFormatter = new DecimalFormatter
        {
            FractionDigits = 1,
            IntegerDigits = 1,
            IsGrouped = false,
            NumberRounder = new IncrementNumberRounder { Increment = 0.1, RoundingAlgorithm = RoundingAlgorithm.RoundHalfUp }
        };
        AudioDelayBox.NumberFormatter = delayFormatter;
        SubtitleDelayBox.NumberFormatter = delayFormatter;

        VolumeSlider.Value = savedVolume;
        AutoplayToggle.IsOn = PlayerSettings.AutoplayNext;
        SetZoomMode(PlayerSettings.ZoomMode);
        _isInitializing = false;

        // Preview so player shortcuts win over focused buttons and sliders inside the player.
        PreviewKeyDown += Player_PreviewKeyDown;
    }

    /// <summary>The user asked to leave the player (Back button or Escape).</summary>
    public event EventHandler? BackRequested;

    /// <summary>The user pressed F, double-clicked the video or used the fullscreen button.</summary>
    public event EventHandler? FullscreenToggleRequested;

    private bool HasPrevious => _queueIndex > 0;
    private bool HasNext => _queueIndex < _queue.Count - 1;
    private MediaItem? NextMedia => HasNext ? _queue[_queueIndex + 1] : null;

    // ---------- Public API used by the window ----------

    public void Initialize(LibraryStore store) => _store = store;

    /// <summary>Plays <paramref name="media"/>; next/previous move through <paramref name="queue"/>.</summary>
    public void Play(MediaItem media, IReadOnlyList<MediaItem> queue)
    {
        var index = queue.ToList().FindIndex(item => string.Equals(item.FilePath, media.FilePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _queue = [media];
            index = 0;
        }
        else
        {
            _queue = queue;
        }

        PlayQueueItem(index);
    }

    /// <summary>Saves progress and stops playback. Called when leaving the player.</summary>
    public void Stop()
    {
        SaveProgress();
        HideEndPanel();
        HideResumeBanner();
        _singleTapTimer.Stop();
        _isFileLoaded = false;
        SetCursorHidden(false);
        TryMpv(mpv => mpv.Stop());
    }

    public void ShowStatusMessage(string message) => PlaybackStatusText.Text = message;

    /// <summary>Loads an external subtitle file into the current video.</summary>
    public void AddSubtitle(string filePath)
    {
        if (_mpv is null || !_isFileLoaded)
        {
            ShowOsd("Start a video before adding subtitles");
            return;
        }

        try
        {
            _mpv.AddSubtitle(filePath);
            RefreshTrackOptions();
            ShowOsd($"Subtitles: {Path.GetFileName(filePath)}");
        }
        catch (Exception exception)
        {
            ShowOsd($"Couldn’t load subtitles: {exception.Message}");
        }
    }

    /// <summary>Updates the fullscreen icon and auto-hiding controls. The window switches the presenter.</summary>
    public void SetFullscreen(bool enabled)
    {
        _isFullscreen = enabled;
        FullscreenIcon.Glyph = enabled ? "" : "";
        ToolTipService.SetToolTip(FullscreenButton, enabled ? "Exit fullscreen (F)" : "Fullscreen (F)");
        ShowControlsTemporarily();
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

    /// <summary>Saves progress and releases mpv and the swap chain. Call once when the window closes.</summary>
    public void Shutdown()
    {
        SaveProgress();
        foreach (var timer in new[] { _controlsHideTimer, _singleTapTimer, _resumeBannerTimer, _osdTimer, _autoplayTimer })
            timer.Stop();
        SetCursorHidden(false);

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

    // ---------- Queue ----------

    private void PlayQueueItem(int index)
    {
        SaveProgress();
        HideEndPanel();
        HideResumeBanner();

        _queueIndex = index;
        _currentMedia = _queue[index];
        _resumedFrom = _store?.GetResumePosition(_currentMedia) ?? 0;

        _isFileLoaded = false;
        _isAtEnd = false;
        _isPaused = true;
        _durationSeconds = 0;
        _positionSeconds = 0;
        _lastSavedPosition = 0;

        PlayerTitle.Text = _currentMedia.Title;
        PlaybackStatusText.Text = "Starting mpv…";
        PlaybackProgress.IsActive = true;
        PlayerStatus.Visibility = Visibility.Visible;
        PlayPauseButton.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        LoadSubtitleButton.IsEnabled = false;
        CurrentTimeText.Text = FormatTime(0);
        DurationText.Text = FormatTime(0);
        SetSeekSliderValue(0);
        UpdateQueueControls();
        ResetPerFileSettings();
        ShowControlsTemporarily();

        if (_mpv is not null)
            LoadCurrentMedia();
        else if (VideoHost.ActualWidth > 0 && VideoHost.ActualHeight > 0)
            InitializePlayer();
        // Otherwise VideoHost_SizeChanged starts mpv once the view has been laid out.

        // The view may have only just become visible, so focus after layout.
        DispatcherQueue.TryEnqueue(() => Focus(FocusState.Programmatic));
    }

    private void PlayNext()
    {
        if (HasNext)
            PlayQueueItem(_queueIndex + 1);
    }

    /// <summary>Restarts the current video if it has been playing a while; otherwise goes back one.</summary>
    private void PlayPrevious()
    {
        if (HasPrevious && _positionSeconds <= PreviousRestartThresholdSeconds)
        {
            PlayQueueItem(_queueIndex - 1);
            return;
        }

        if (_isFileLoaded)
        {
            HideEndPanel();
            TryMpv(mpv =>
            {
                if (_isAtEnd)
                    mpv.Replay();
                else
                    mpv.Seek(0);
            });
        }
    }

    private void UpdateQueueControls()
    {
        PreviousButton.IsEnabled = HasPrevious || _isFileLoaded;
        NextButton.IsEnabled = HasNext;
        ToolTipService.SetToolTip(NextButton, NextMedia is { } next ? $"Next: {next.Title} (Shift+N)" : "Next (Shift+N)");
        QueuePositionText.Text = _queue.Count > 1 ? $"{_queueIndex + 1} of {_queue.Count}" : string.Empty;
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => PlayPrevious();

    private void Next_Click(object sender, RoutedEventArgs e) => PlayNext();

    // ---------- mpv lifecycle ----------

    private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(e.NewSize.Width * scale));
        var height = Math.Max(1, (int)Math.Round(e.NewSize.Height * scale));

        if (_mpv is null)
        {
            if (_currentMedia is not null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
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
            _mpv.SetZoomMode(_zoomMode);
            LoadCurrentMedia();
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

    private void LoadCurrentMedia()
    {
        if (_mpv is null || _currentMedia is null)
            return;

        try
        {
            _mpv.LoadFile(_currentMedia.FilePath, _resumedFrom);
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
                OnFileLoaded();
                break;
            case "time-pos" when value is double position:
                _positionSeconds = position;
                CurrentTimeText.Text = FormatTime(position);
                if (!_isSeeking)
                    SetSeekSliderValue(position);
                SaveProgressIfDue();
                break;
            case "duration" when value is double duration:
                _durationSeconds = Math.Max(0, duration);
                DurationText.Text = FormatTime(_durationSeconds);
                SeekSlider.IsEnabled = _isFileLoaded && _durationSeconds > 0;
                SetSeekSliderMaximum(_durationSeconds);
                break;
            case "pause" when value is bool paused:
                _isPaused = paused;
                PlayPauseIcon.Glyph = paused ? "" : "";
                if (paused)
                    SaveProgress();
                break;
            case "eof-reached" when value is bool atEnd:
                OnEndReachedChanged(atEnd);
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
            case "playback-error" when value is int error:
                PlaybackProgress.IsActive = false;
                PlaybackStatusText.Text = $"mpv could not play this file (error {error}).";
                PlayerStatus.Visibility = Visibility.Visible;
                PlayPauseButton.IsEnabled = false;
                break;
        }
    }

    private void OnFileLoaded()
    {
        _isFileLoaded = true;
        PlaybackProgress.IsActive = false;
        PlayerStatus.Visibility = Visibility.Collapsed;
        PlayPauseButton.IsEnabled = true;
        SeekBackwardButton.IsEnabled = true;
        SeekForwardButton.IsEnabled = true;
        MuteButton.IsEnabled = true;
        LoadSubtitleButton.IsEnabled = true;
        SeekSlider.IsEnabled = _durationSeconds > 0;
        RefreshTrackOptions();
        UpdateQueueControls();

        if (_currentMedia is not null)
            _store?.RecordRecent(_currentMedia);
        if (_resumedFrom > 0)
            ShowResumeBanner();
    }

    // ---------- Progress and resume ----------

    private void SaveProgressIfDue()
    {
        if (Math.Abs(_positionSeconds - _lastSavedPosition) >= ProgressSaveIntervalSeconds)
            SaveProgress();
    }

    private void SaveProgress()
    {
        if (_store is null || _currentMedia is null || !_isFileLoaded || _durationSeconds <= 0)
            return;

        var position = _isAtEnd ? _durationSeconds : _positionSeconds;
        _store.SaveProgress(_currentMedia, position, _durationSeconds);
        _lastSavedPosition = _positionSeconds;
    }

    private void ShowResumeBanner()
    {
        ResumeBannerText.Text = $"Resumed from {FormatTime(_resumedFrom)}";
        ResumeBanner.Visibility = Visibility.Visible;
        _resumeBannerTimer.Stop();
        _resumeBannerTimer.Start();
    }

    private void HideResumeBanner()
    {
        _resumeBannerTimer.Stop();
        ResumeBanner.Visibility = Visibility.Collapsed;
    }

    private void StartOver_Click(object sender, RoutedEventArgs e)
    {
        HideResumeBanner();
        TryMpv(mpv => mpv.Seek(0));
        Focus(FocusState.Programmatic);
    }

    // ---------- End of video and autoplay ----------

    private void OnEndReachedChanged(bool atEnd)
    {
        if (!_isFileLoaded)
            return;

        _isAtEnd = atEnd;
        if (!atEnd)
        {
            HideEndPanel();
            return;
        }

        SaveProgress();
        HideResumeBanner();
        _controlsHideTimer.Stop();
        SetControlsVisible(true);

        if (NextMedia is not null && PlayerSettings.AutoplayNext)
            ShowAutoplayCountdown();
        else
            ShowReplayPanel();
    }

    private void ShowAutoplayCountdown()
    {
        _autoplaySecondsLeft = AutoplayCountdownSeconds;
        EndPanelHeading.Text = "UP NEXT";
        EndPanelTitle.Text = NextMedia!.Title;
        UpdateCountdownText();
        EndPanelCountdown.Visibility = Visibility.Visible;
        EndPlayNextButton.Visibility = Visibility.Visible;
        EndCancelButton.Visibility = Visibility.Visible;
        EndReplayButton.Visibility = Visibility.Collapsed;
        EndPanel.Visibility = Visibility.Visible;
        _autoplayTimer.Stop();
        _autoplayTimer.Start();
    }

    private void ShowReplayPanel()
    {
        _autoplayTimer.Stop();
        var next = NextMedia;
        EndPanelHeading.Text = next is null ? "FINISHED" : "UP NEXT";
        EndPanelTitle.Text = next?.Title ?? _currentMedia?.Title ?? string.Empty;
        EndPanelCountdown.Visibility = Visibility.Collapsed;
        EndPlayNextButton.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        EndCancelButton.Visibility = Visibility.Collapsed;
        EndReplayButton.Visibility = Visibility.Visible;
        EndPanel.Visibility = Visibility.Visible;
    }

    private void HideEndPanel()
    {
        _autoplayTimer.Stop();
        EndPanel.Visibility = Visibility.Collapsed;
    }

    private void AutoplayTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _autoplaySecondsLeft--;
        if (_autoplaySecondsLeft <= 0)
        {
            PlayNext();
            return;
        }
        UpdateCountdownText();
    }

    private void UpdateCountdownText()
        => EndPanelCountdown.Text = $"Playing in {_autoplaySecondsLeft} second{(_autoplaySecondsLeft == 1 ? "" : "s")}";

    private void EndPlayNext_Click(object sender, RoutedEventArgs e) => PlayNext();

    private void EndCancel_Click(object sender, RoutedEventArgs e)
    {
        ShowReplayPanel();
        Focus(FocusState.Programmatic);
    }

    private void EndReplay_Click(object sender, RoutedEventArgs e) => Replay();

    private void Replay()
    {
        HideEndPanel();
        TryMpv(mpv => mpv.Replay());
        Focus(FocusState.Programmatic);
    }

    // ---------- Play, seek and volume ----------

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        if (_mpv is null || !_isFileLoaded)
            return;

        if (_isAtEnd)
        {
            Replay();
            return;
        }

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
            TryMpv(mpv => mpv.Seek(e.NewValue));
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isSeeking = true;

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isSeeking = false;
        if (_mpv is not null && _durationSeconds > 0)
            TryMpv(mpv => mpv.Seek(SeekSlider.Value));
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
        if (_mpv is null || !_isFileLoaded || _durationSeconds <= 0)
            return;

        var position = Math.Clamp(SeekSlider.Value + seconds, 0, _durationSeconds);
        SetSeekSliderValue(position);
        TryMpv(mpv => mpv.Seek(position));
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        TryMpv(mpv => mpv.SetVolume(e.NewValue));
        PlayerSettings.Volume = e.NewValue;
    }

    private void ChangeVolume(double delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, VolumeSlider.Minimum, VolumeSlider.Maximum);
        ShowOsd($"Volume {VolumeSlider.Value:0}%");
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (_mpv is null)
            return;
        TryMpv(mpv => mpv.SetMuted(!_isMuted));
        ShowOsd(_isMuted ? "Unmuted" : "Muted");
    }

    // ---------- Settings flyout: tracks, delays, speed, aspect, autoplay ----------

    private void ResetPerFileSettings()
    {
        _isUpdatingFileSettings = true;
        AudioDelayBox.Value = 0;
        SubtitleDelayBox.Value = 0;
        AspectRatioCombo.SelectedIndex = 0;
        CropCombo.SelectedIndex = 0;
        _isUpdatingFileSettings = false;

        TryMpv(mpv =>
        {
            mpv.SetAudioDelay(0);
            mpv.SetSubtitleDelay(0);
            mpv.SetAspectOverride("-1");
            mpv.SetCropRatio(null);
        });
    }

    // Zoom is a lasting preference; crop and aspect ratio are per video.

    private void SetZoomMode(VideoZoomMode mode)
    {
        _zoomMode = mode;
        if (!_isInitializing)
            PlayerSettings.ZoomMode = mode;

        _isUpdatingZoom = true;
        ZoomModeCombo.SelectedItem = ZoomModeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, mode.ToString()));
        _isUpdatingZoom = false;

        ToolTipService.SetToolTip(ZoomButton, $"Zoom: {GetZoomLabel(mode)} (C)");
        TryMpv(mpv => mpv.SetZoomMode(mode));
    }

    private void CycleZoomMode()
    {
        var next = _zoomMode switch
        {
            VideoZoomMode.Fit => VideoZoomMode.Fill,
            VideoZoomMode.Fill => VideoZoomMode.Stretch,
            _ => VideoZoomMode.Fit
        };
        SetZoomMode(next);
        ShowOsd($"Zoom: {GetZoomLabel(next)}");
    }

    private static string GetZoomLabel(VideoZoomMode mode) => mode switch
    {
        VideoZoomMode.Fill => "Fill screen",
        VideoZoomMode.Stretch => "Stretch",
        _ => "Fit"
    };

    private void ZoomButton_Click(object sender, RoutedEventArgs e) => CycleZoomMode();

    private void ZoomModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingZoom
            || ZoomModeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<VideoZoomMode>(tag, out var mode))
            return;
        SetZoomMode(mode);
    }

    private void CropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingFileSettings || CropCombo.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            return;
        TryMpv(mpv => mpv.SetCropRatio(ratio > 0 ? ratio : null));
    }

    private void PlaybackSpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPlaybackSpeed || _mpv is null || PlaybackSpeedCombo.SelectedItem is not ComboBoxItem item)
            return;

        if (double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            TryMpv(mpv => mpv.SetSpeed(speed));
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

    private void AspectRatioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingFileSettings || AspectRatioCombo.SelectedItem is not ComboBoxItem { Tag: string aspect })
            return;
        TryMpv(mpv => mpv.SetAspectOverride(aspect));
    }

    private void AudioDelayBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingFileSettings)
            return;
        var delay = double.IsFinite(args.NewValue) ? args.NewValue : 0;
        TryMpv(mpv => mpv.SetAudioDelay(delay));
    }

    private void SubtitleDelayBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingFileSettings)
            return;
        var delay = double.IsFinite(args.NewValue) ? args.NewValue : 0;
        TryMpv(mpv => mpv.SetSubtitleDelay(delay));
    }

    private void AdjustSubtitleDelay(double delta)
    {
        if (!_isFileLoaded)
            return;

        var current = double.IsFinite(SubtitleDelayBox.Value) ? SubtitleDelayBox.Value : 0;
        SubtitleDelayBox.Value = Math.Clamp(Math.Round(current + delta, 1), SubtitleDelayBox.Minimum, SubtitleDelayBox.Maximum);
        ShowOsd($"Subtitle delay {SubtitleDelayBox.Value:+0.0;-0.0;0.0} s");
    }

    private void AutoplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
            PlayerSettings.AutoplayNext = AutoplayToggle.IsOn;
    }

    private async void LoadSubtitle_Click(object sender, RoutedEventArgs e)
    {
        PlayerSettingsFlyout.Hide();
        if (XamlRoot?.ContentIslandEnvironment is not { } environment)
            return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in MediaLibraryService.SupportedSubtitleExtensions)
            picker.FileTypeFilter.Add(extension);
        var windowHandle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            AddSubtitle(file.Path);
        Focus(FocusState.Programmatic);
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
        TryMpv(mpv => mpv.SetAudioTrack(trackId));
    }

    private void SubtitleTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTrackControls || _mpv is null || SubtitleTrackCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not long trackId)
            return;
        TryMpv(mpv => mpv.SetSubtitleTrack(trackId));
    }

    // ---------- Mouse, keyboard and auto-hiding controls ----------

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        => FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);

    private void VideoHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        // A double-click raises Tapped as well; ignore taps that belong to one.
        if ((DateTime.UtcNow - _lastDoubleTapUtc).TotalMilliseconds < 400)
            return;

        // Wait briefly so a double-click toggles fullscreen without also pausing.
        _singleTapTimer.Stop();
        _singleTapTimer.Start();
    }

    private void VideoHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _singleTapTimer.Stop();
        _lastDoubleTapUtc = DateTime.UtcNow;
        FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Player_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Visibility != Visibility.Visible || IsModifierDown(VirtualKey.Control) || IsModifierDown(VirtualKey.Menu))
            return;

        e.Handled = HandlePlayerKey(e.Key, IsModifierDown(VirtualKey.Shift));
    }

    private bool HandlePlayerKey(VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.Space:
            case VirtualKey.K:
                TogglePause();
                break;
            case VirtualKey.Left:
                SeekBy(-5);
                ShowControlsTemporarily();
                break;
            case VirtualKey.Right:
                SeekBy(5);
                ShowControlsTemporarily();
                break;
            case VirtualKey.J:
                SeekBy(-10);
                ShowControlsTemporarily();
                break;
            case VirtualKey.L:
                SeekBy(10);
                ShowControlsTemporarily();
                break;
            case VirtualKey.Up:
                ChangeVolume(5);
                break;
            case VirtualKey.Down:
                ChangeVolume(-5);
                break;
            case VirtualKey.M:
                ToggleMute();
                break;
            case VirtualKey.F:
            case VirtualKey.F11:
                FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.Escape:
                // Escape leaves fullscreen first, then leaves the player.
                if (_isFullscreen)
                    FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
                else
                    BackRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.N when shift:
                PlayNext();
                break;
            case VirtualKey.P when shift:
                PlayPrevious();
                break;
            case VirtualKey.C:
                CycleZoomMode();
                break;
            case VirtualKey.Z:
                AdjustSubtitleDelay(-0.1);
                break;
            case VirtualKey.X:
                AdjustSubtitleDelay(0.1);
                break;
            default:
                return false;
        }
        return true;
    }

    private static bool IsModifierDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Player_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isFullscreen && Visibility == Visibility.Visible)
            ShowControlsTemporarily();
    }

    private void Bars_PointerEntered(object sender, PointerRoutedEventArgs e) => _isPointerOverBars = true;

    private void Bars_PointerExited(object sender, PointerRoutedEventArgs e) => _isPointerOverBars = false;

    /// <summary>Shows the bars; in fullscreen they hide again after a few idle seconds.</summary>
    private void ShowControlsTemporarily()
    {
        SetControlsVisible(true);
        _controlsHideTimer.Stop();
        if (_isFullscreen)
            _controlsHideTimer.Start();
    }

    private void ControlsHideTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        // Keep the controls up while the user is using them or the video has ended.
        if (!_isFullscreen || _isPointerOverBars || PlayerSettingsFlyout.IsOpen || EndPanel.Visibility == Visibility.Visible)
            return;
        SetControlsVisible(false);
    }

    private void SetControlsVisible(bool visible)
    {
        var opacity = visible ? 1d : 0d;
        PlayerHeaderBar.Opacity = opacity;
        PlayerControlsBar.Opacity = opacity;
        PlayerHeaderBar.IsHitTestVisible = visible;
        PlayerControlsBar.IsHitTestVisible = visible;
        if (!visible)
            _isPointerOverBars = false;
        SetCursorHidden(!visible && _isFullscreen);
    }

    private void ShowOsd(string message)
    {
        OsdText.Text = message;
        OsdBanner.Visibility = Visibility.Visible;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    private void PlayerControlsBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Min(e.NewSize.Width, PlayerControlsBar.ActualWidth);

        // Progressively drop the least important controls so the seek bar always keeps room.
        VolumeSlider.Visibility = width < 820 ? Visibility.Collapsed : Visibility.Visible;

        var narrow = width < 620;
        var seekButtonsVisibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        SeekBackwardButton.Visibility = seekButtonsVisibility;
        SeekForwardButton.Visibility = seekButtonsVisibility;

        var tiny = width < 460;
        TimeSeparatorText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        DurationText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        TimeGroup.MinWidth = tiny ? 40 : 104;
        PreviousButton.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;

        PlayerControlsBar.Padding = narrow ? new Thickness(8, 6, 8, 6) : new Thickness(12, 6, 12, 6);
        ControlsRow.ColumnSpacing = narrow ? 8 : 12;
    }

    // ---------- Helpers ----------

    /// <summary>Runs an mpv call if mpv is running, showing failures instead of crashing the app.</summary>
    private void TryMpv(Action<MpvPlayback> action)
    {
        if (_mpv is null)
            return;

        try
        {
            action(_mpv);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException exception)
        {
            ShowOsd(exception.Message);
        }
    }

    private static DispatcherQueueTimer CreateTimer(
        DispatcherQueue dispatcher,
        TimeSpan interval,
        Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object> tick,
        bool repeating = false)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += tick;
        return timer;
    }

    private void SetCursorHidden(bool hidden)
    {
        // ShowCursor keeps a per-thread counter, so only call it when the state actually changes.
        if (_isCursorHidden == hidden)
            return;
        _isCursorHidden = hidden;
        ShowCursor(!hidden);
    }

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
}
