# Iris

Iris is a Windows media player I’m building with C# and WinUI 3. It uses mpv as the playback backend. The idea is to make a fast, clean player for my local video library, with Jellyfin support coming later.

## Current features

- Scan and browse local video folders
- Search the library and view generated thumbnails
- Create collections independent of folder structure
- Open or drag and drop a video for instant playback
- Play video in-app with mpv
- Pause, seek, adjust volume, skip 10 seconds, and toggle fullscreen
- Click the video to pause, double-click for fullscreen, with keyboard shortcuts for everything
- Resume where you left off, with a Continue watching row on Home
- Next and previous through the folder or collection, with autoplay of the next video
- Select audio and subtitle tracks, load external subtitles, and adjust subtitle and audio delay
- Change playback speed, aspect ratio, zoom (fit, fill screen, stretch) and crop
- Remember volume and recently played items between launches
- Mini player: a small always-on-top window for watching while you work

Jellyfin isn’t hooked up yet.

## Player shortcuts

| Key | Action |
| --- | --- |
| Space / K | Play or pause |
| ← / → | Back or forward 5 seconds |
| J / L | Back or forward 10 seconds |
| ↑ / ↓ | Volume up or down |
| M | Mute |
| V | Subtitles on or off |
| F / F11 | Fullscreen |
| I | Mini player |
| Esc | Close the settings sheet, leave the mini player or fullscreen, then leave the player |
| Shift+N / Shift+P | Next or previous video |
| Page Down / Page Up | Next or previous chapter |
| Mouse wheel | Volume up or down |
| C | Cycle zoom: Fit, Fill screen, Stretch |
| Z / X | Subtitle delay −0.1 s / +0.1 s |

In the library, Ctrl+K (or Ctrl+F) jumps to search and Ctrl+O opens a video file.

## What I’m using

- C# and .NET 8
- WinUI 3 / Windows App SDK
- mpv (libmpv 0.41.0)

For now Iris is Windows-only. The project has x64 and ARM64 build configurations.

## Install

Download `IrisMediaPlayer-win-Setup.exe` from the Releases page and run it. It installs Iris for
your account in a few seconds (no admin prompt, no wizard), adds it to the Start menu, the desktop
and "Open with" for video files, and starts it. Nothing else needs installing: .NET and the Windows
App SDK are included.

Iris keeps itself up to date: when a new release is out, it downloads it in the background and
offers **Restart** (or installs it the next time you open Iris).

Remove it from Settings › Apps like any program. Your library data lives in `%LocalAppData%\Iris`
and is kept when you uninstall. To make Iris the default player, pick it in Settings › Apps ›
Default apps (Windows doesn't let installers choose this for you).

## Run it from source

1. Install Visual Studio with the **WinUI application development** workload and the .NET 8 SDK.
2. Open `Iris-MediaPlayer.slnx`.
3. Set `Iris-MediaPlayer` as the startup project, choose `x64` or `ARM64`, and the
   **Iris-MediaPlayer (Unpackaged)** launch profile.
4. Restore the NuGet packages and press **F5**.

## Build the installer

Iris is packaged with [Velopack](https://velopack.io) (free and open source). Double-click

```powershell
```

The script publishes Iris in Release and packs it with the `vpk` tool (restored from
that run only, so it also works where Windows PowerShell only allows signed scripts.

To release a new version:

1. Raise `<Version>` in `Iris-MediaPlayer/Iris-MediaPlayer.csproj`.
   token in `GITHUB_TOKEN`), or upload everything in `artifacts\releases` to a new GitHub Release.

Installed copies find the release on their next start and offer to update.

The setup isn't code-signed, so Windows SmartScreen shows "Windows protected your PC" the first
time; choose **More info › Run anyway**.

## Project folders

```text
Iris-MediaPlayer/
├── Iris-MediaPlayer.slnx
├── THIRD-PARTY-NOTICES.md
└── Iris-MediaPlayer/
    ├── Assets/
    ├── Models/
    ├── Services/       # Library scanning and mpv playback
    ├── Properties/
    ├── App.xaml
    ├── MainWindow.xaml
    └── Iris-MediaPlayer.csproj
```

Still a work in progress, so things will change as I keep building it.
