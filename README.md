# Turris Studio

A lightweight Windows app for the screens on **TRYX Turris 620** and **TRYX Stage 360** coolers: show GIFs, videos and images with a fully customizable live stats overlay (CPU/GPU temperature, usage, clocks, power, clock/date, text, badges).

## Download

Get **TurrisStudio-Setup-x.y.z.exe** from [Releases](../../releases) and run it. The installer is not code-signed yet, so Windows SmartScreen may warn: click **More info → Run anyway**.

The installer:
- includes everything (no .NET or FFmpeg install needed)
- installs the PawnIO sensor driver used for CPU temperatures
- starts the app hidden in the tray when you sign in (can be turned off in Settings)

> Quit KANALI before using Turris Studio. Only one program can control the cooler at a time.

## Features

- Media library: drag & drop GIFs, videos and images; Fill / Fit / Stretch; rotate; playback modes
- Stage 360: one picture across both screens, or a different picture on each screen
- Overlay designer with a live preview over your moving background; changes sync instantly
- Automatic sensors: picks the right CPU/GPU readings (dedicated GPU first) with the right unit
- Built-in sensor reader (LibreHardwareMonitor); HWiNFO shared memory is used too when it's running
- Brightness, screen on/off, mirror; all settings are remembered and restored on reconnect

## Supported coolers

| Cooler | USB ID | Screen | Status |
|---|---|---|---|
| TRYX Turris 620 | 391a:2011 | 1280×720 | Tested |
| TRYX Stage 360 | 391a:1041 | 2 × 720×720 (1440×720) | Implemented from the official app's device table, **not yet tested on hardware**. Reports welcome |

## Building

Requires the .NET 8 SDK. `dotnet build TurrisStudio.sln`. To build the installer, install Inno Setup 6, put FFmpeg (`ffmpeg.exe`, `ffprobe.exe`, `LICENSE`) in `installer/deps/ffmpeg` and `PawnIO_setup.exe` in `installer/deps`, then run `installer/build-installer.ps1`.

## Credits

- Protocol details: [Tryx-Linux-GUI](https://github.com/DXVSI/Tryx-Linux-GUI) (MIT)
- Sensors: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0), [PawnIO](https://pawnio.eu/)
- Media conversion: [FFmpeg](https://ffmpeg.org/) (GPL build from gyan.dev, bundled in the installer)
- UI: [WPF-UI](https://github.com/lepoco/wpfui)

Not affiliated with or endorsed by TRYX.
