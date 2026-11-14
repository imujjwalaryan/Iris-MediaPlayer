namespace Iris_MediaPlayer.Models;

/// <summary>
/// Tells a media server what's happening while one of its videos plays, so it can keep the resume
/// point and mark the video watched. Calls return straight away; sending happens in the background,
/// in order, and failures are ignored.
/// </summary>
public interface IRemotePlayback
{
    void ReportStarted(double positionSeconds, bool isPaused);
    void ReportProgress(double positionSeconds, bool isPaused);
    void ReportStopped(double positionSeconds);
}
