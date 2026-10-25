# Razer Island

**English** · [Українська](README.uk.md)

A Dynamic Island–style widget for Windows. A small island at the top of the screen shows the battery of your wireless devices, the time, the weather, your next calendar events and your Discord microphone state. Hover over it to expand it, or switch to a floating card that is always open.

## Features

- **Battery levels** in the island, the expanded card and as tray icons (one per device, percentage or device icon):
  - Razer wireless devices, read from Razer Synapse 4 logs.
  - Any connected Bluetooth device that reports its battery to Windows: phones, headphones, mice, keyboards, controllers *(0.2.0)*.
  - Experimental *(0.2.0)*: Logitech (Lightspeed / Unifying / Bolt receivers, HID++ 2.0), SteelSeries Arctis and Nova headsets (USB receiver) and Corsair (via iCUE SDK, iCUE must be running).
  - Device icons in brand colours for popular brands *(0.2.0)*.
- **Clock, date and weather** from [open-meteo.com](https://open-meteo.com) — no account or API key needed.
- **Schedule** from Google Calendar through its secret iCal link, including recurring events.
- **Discord microphone**: when you mute, the island shows it on top of all windows. Uses Discord's local RPC with your own Discord application.
- **Ukrainian and English** interface *(0.2.0)*.
- First-run setup wizard, start with Windows, desktop shortcut.

## Requirements

- Windows 10 1809 or later, x64.
- Razer Synapse 4 for Razer devices; iCUE for Corsair devices. Bluetooth, Logitech and SteelSeries need no extra software.

## Install

Download `RazerIsland-<version>-setup.exe` from [Releases](../../releases) and run it. It installs for the current user only (no administrator rights) into `%LOCALAPPDATA%\Programs\Razer Island`. Settings from the older PowerShell version are imported automatically on first start.

## Setup notes

- **Google Calendar**: Calendar settings → your calendar → *Integrate calendar* → copy *Secret address in iCal format* and paste it in Settings → Calendar. Anyone with this link can read your calendar; it is stored only on your computer.
- **Discord**: create an application at the [Discord Developer Portal](https://discord.com/developers/applications), copy its Client ID and Client Secret, add `http://localhost` as an OAuth2 redirect, then click *Connect* in Settings → Discord microphone and approve the request in Discord.

## Building

```powershell
dotnet build                 # development build
.\build.ps1                  # self-contained publish + installer → artifacts\installer
.\build.ps1 -SkipInstaller   # publish only → artifacts\publish
```

The installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`). The version is set once in `src\RazerIsland\RazerIsland.csproj` (`<Version>`); the installer reads it from the exe. Changes are listed in [CHANGELOG.md](CHANGELOG.md) (in Ukrainian).

## Project structure

```
src/RazerIsland/
  App.xaml(.cs)   startup, timers, commands, microphone state
  Core/           paths and version, settings, log, autostart, shortcuts,
                  single instance, legacy import, HID access, WinAPI
  Battery/        Synapse logs, Bluetooth, Logitech, SteelSeries, Corsair, brands
  Weather/        weather and city search (open-meteo)
  Calendar/       iCal + RRULE parsing
  Discord/        RPC client for the microphone state
  UI/             widget, microphone island, tray, settings and wizard
installer/RazerIsland.iss
```

## Data and privacy

Everything stays on your computer in `%LOCALAPPDATA%\Razer Island\`: `settings.json` (including Discord tokens), `calendars.txt` (iCal links) and `error.log`. The app talks only to open-meteo.com, your calendar links and Discord.

For testing, the `RAZER_ISLAND_HOME` environment variable points the app to a separate data folder; such an instance runs alongside the main one and does not touch autostart.

## Third-party components

`src/RazerIsland/Native/iCUESDK.x64_2019.dll` is Corsair iCUE SDK v4.0.84 from the official [CorsairOfficial/cue-sdk](https://github.com/CorsairOfficial/cue-sdk) releases (`redist` folder, signed by Corsair Memory, Inc.). The Logitech HID++ and SteelSeries protocols are implemented independently from the public protocol descriptions in [Solaar](https://github.com/pwr-Solaar/Solaar) and [HeadsetControl](https://github.com/Sapd/HeadsetControl); no code was copied from those projects.

Razer, Logitech, SteelSeries, Corsair and Discord are trademarks of their respective owners. This project is not affiliated with any of them.
