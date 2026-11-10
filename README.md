# Iris

Iris is a Windows media player I’m building with C# and WinUI 3. It uses mpv as the playback backend. The idea is to make a fast, clean player for my local video library, with Jellyfin support coming later.

## Current features

- Scan and browse local video folders
- Search the library and view generated thumbnails
- Create collections independent of folder structure
- Open or drag and drop a video for instant playback
- Play video in-app with mpv
- Pause, seek, adjust volume, skip 10 seconds, and toggle fullscreen
- Select audio and subtitle tracks and change playback speed
- Save recently played items between launches

Jellyfin and playback progress aren’t hooked up yet.

## What I’m using

- C# and .NET 8
- WinUI 3 / Windows App SDK
- mpv (libmpv 0.41.0)

For now Iris is Windows-only. The project has x64 and ARM64 build configurations.

## Run it

1. Install Visual Studio with the **WinUI application development** workload and the .NET 8 SDK.
2. Open `Iris-MediaPlayer.slnx`.
3. Set `Iris-MediaPlayer` as the startup project and choose `x64` or `ARM64`.
4. Restore the NuGet packages and press **F5**.

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
