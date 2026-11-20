using Iris_MediaPlayer.Models;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Services.Stash;

/// <summary>
/// Reports one Stash scene's playback back to Stash: the resume point and time watched
/// (sceneSaveActivity), and one play once a tenth of the scene has been watched (sceneAddPlay).
/// A scene watched to the end resumes from the start next time.
/// </summary>
public sealed class StashPlaybackReporter(StashClient client, string sceneId, double durationSeconds) : IRemotePlayback
{
    private static Task _pending = Task.CompletedTask;
    private static readonly object Gate = new();

    private readonly Stopwatch _watching = new();
    private bool _playCounted;

    /// <summary>Finishes once every report sent so far has been answered (used before reloading pages).</summary>
    public static Task WhenSentAsync()
    {
        lock (Gate)
            return _pending;
    }

    public void ReportStarted(double positionSeconds, bool isPaused)
    {
        _watching.Restart();
        if (isPaused)
            _watching.Stop();
    }

    public void ReportProgress(double positionSeconds, bool isPaused)
    {
        Save(positionSeconds);
        if (isPaused)
            _watching.Stop();
        else
            _watching.Start();
    }

    public void ReportStopped(double positionSeconds)
    {
        var finished = durationSeconds > 0 && positionSeconds >= durationSeconds * 0.95;
        Save(finished ? 0 : positionSeconds, finished);
        _watching.Reset();
    }

    private void Save(double positionSeconds, bool finished = false)
    {
        var watched = _watching.Elapsed.TotalSeconds;
        _watching.Restart();
        var countPlay = !_playCounted && (finished || (durationSeconds > 0 && positionSeconds >= durationSeconds * 0.1));
        if (countPlay)
            _playCounted = true;

        Enqueue(async () =>
        {
            await client.SaveActivityAsync(sceneId, positionSeconds, watched);
            if (countPlay)
                await client.AddPlayAsync(sceneId);
        });
    }

    private static void Enqueue(Func<Task> send)
    {
        lock (Gate)
        {
            _pending = _pending.ContinueWith(async _ =>
            {
                try
                {
                    await send();
                }
                catch (StashException)
                {
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }
}
