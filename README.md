# Iris

**A clean, fast video player for Windows.**

Point Iris at your video folders and it turns them into a library you can browse, search and
pick up right where you left off. It plays almost any video file, including 4K, HDR and MKV
movies with multiple audio and subtitle tracks.

## Download

1. Go to the [**Releases**](../../releases) page and download **`IrisMediaPlayer-win-Setup.exe`**
   from the latest release.
2. Run it. Iris installs in a few seconds and opens. No admin rights or extra downloads needed.

> **"Windows protected your PC"?** Iris isn't code-signed yet, so Windows shows this the first
> time. Click **More info**, then **Run anyway**.

Iris keeps itself up to date. When a new version is out, it downloads it in the background and
asks you to restart, or installs it the next time you open Iris.

Works on **Windows 10 and Windows 11** (64-bit).

## What you can do

**Build your library**
- Add your video folders and Iris finds every video inside them, with thumbnails.
- **Home** shows what you were watching, what's new and what you played recently.
- Browse by video or by folder, filter (4K, in progress, unwatched) and search everything.
- Make **collections**, your own playlists from any videos, wherever they're stored.
- Right-click any video to add it to a collection, open its folder, see its details, or delete it.

**Watch**
- Plays almost any format, smoothly, using the [mpv](https://mpv.io) engine.
- **Resumes** where you stopped, and moves on to the next video in the folder automatically.
- Switch **audio and subtitle** tracks, load your own subtitle files and fix out-of-sync subtitles.
- Jump between **chapters** in movies that have them.
- Change speed, zoom (fit, fill or stretch), crop and aspect ratio.
- Pop out a **mini player** that stays on top while you do other things.
- Scroll the mouse wheel over the video to change the volume.

**Open videos from anywhere**
- Right-click a video in File Explorer and choose **Open with › Iris**, or drag a file onto Iris.
- To make Iris your default player, choose it in **Settings › Apps › Default apps**.

## Keyboard shortcuts

| Key | What it does |
| --- | --- |
| Space | Play or pause |
| ← / → | Back or forward 5 seconds |
| J / L | Back or forward 10 seconds |
| ↑ / ↓ or mouse wheel | Volume |
| M | Mute |
| F | Fullscreen |
| V | Subtitles on or off |
| I | Mini player |
| Page Up / Page Down | Previous or next chapter |
| Shift+P / Shift+N | Previous or next video |
| C | Change zoom |
| Z / X | Move subtitles earlier or later |
| Esc | Go back |
| Ctrl+K | Search your library |
| Ctrl+O | Open a video file |

## Good to know

- Your library, collections and resume points are saved on your PC in `%LocalAppData%\Iris`.
- To uninstall, go to **Settings › Apps › Installed apps**. Your library is kept, in case you
  come back.
- **Coming later:** streaming from your Jellyfin server.

Iris is a work in progress, so expect it to keep changing.

---

## For developers

Iris is built with C#, .NET 8, WinUI 3 and [mpv](https://mpv.io), and is installed and updated
with [Velopack](https://velopack.io).

**Run it from source**

1. Install Visual Studio with the **WinUI application development** workload.
2. Open `Iris-MediaPlayer.slnx`, pick `x64` and press **F5**.

**Make a release**

1. Raise the version in `Iris-MediaPlayer/Iris-MediaPlayer.csproj` (`<Version>`).
3. Create a new release on GitHub and upload **everything** in `artifacts\releases`.

Installed copies of Iris pick up the new release on their next start.

**Project layout**

```text
Iris-MediaPlayer/          the app
THIRD-PARTY-NOTICES.md     licences of the libraries Iris uses
```
