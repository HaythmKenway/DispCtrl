<div align="center">

<img src="site/assets/logo.png" alt="DispCtrl" width="104" height="104">

# DispCtrl for Linux

**Every monitor on your desk, controlled as one - on Linux.**

Brightness, night light, display modes, presets and snap layouts for every display,<br>
from a window, a tray icon with a quick panel, and a scriptable command line.

[![Snap Store](https://img.shields.io/badge/snap-dispctrl--linux-82BEA0?logo=snapcraft&logoColor=white)](https://snapcraft.io/dispctrl-linux) [![Releases](https://img.shields.io/github/v/release/HaythmKenway/DispCtrl?include_prereleases&color=8b5cf6&label=release)](https://github.com/HaythmKenway/DispCtrl/releases) [![Linux](https://img.shields.io/badge/platform-Linux%20(X11)-FCC624?logo=linux&logoColor=black)](#install) [![Licence: MIT](https://img.shields.io/badge/licence-MIT-green)](LICENSE)

[Install](#install) · [Getting started](#getting-started) · [Guide](docs/LINUX.md) · [Windows and macOS](#windows-and-macos)

</div>

## What it does

- **Brightness for every display**: external monitors over DDC/CI, the laptop's own panel through its backlight, and dimming in software below what a panel can do.
- **Night light** on a schedule, from 6500 K down to 1900 K, and **dark mode** on the same hours (GNOME, KDE, XFCE).
- **Displays**: drag them into place at their real size; change resolution, refresh rate and orientation with fifteen seconds to keep it; extend, duplicate or one display, as with Win+P.
- **Your monitor's own controls**: contrast, input source, colour preset, picture mode, colour gains and volume - only those it lists.
- **Presets**: save the whole desk under a name and put it back in one click.
- **Screen care**: keep the computer awake for good or for a while, Stay active (a one-pixel nudge after 55 seconds still), turn the displays off.
- **Snap layouts and Snap Assist**, as on Windows 11: drag a window to the top centre, or press Super+Z.
- **A tray icon and quick panel** with all of it one click away, and `dispctrl-linux` for scripts.

It needs an X11 session for most of this; on Wayland the compositor keeps gamma and window placement to itself, and brightness still works. The [guide](docs/LINUX.md) has everything, feature by feature.

## Install

```bash
sudo snap install dispctrl-linux          # the Snap Store; --edge for the newest build
```

Or from the [releases](https://github.com/HaythmKenway/DispCtrl/releases): a `.deb` (Debian, Ubuntu, Mint), an `.rpm` (Fedora, RHEL, openSUSE), and tarballs for everything else, Alpine included, each for x86-64 and 64-bit ARM, with .NET inside. The snap cannot reach monitors over DDC/CI under strict confinement; for an external monitor's own brightness and controls, use a package. [Install](docs/LINUX.md#install) has the details.

## Getting started

1. Open **DispCtrl** from the applications menu. Its icon appears in the panel's tray; on GNOME that needs the AppIndicator extension, which Ubuntu has on.
2. Click the icon for the quick panel: a slider for each display, night light, keep awake and the rest.
3. Run `dispctrl-linux doctor` to see what it can reach, and what to install for what it cannot.

```bash
dispctrl-linux displays                    every display, monitor and backlight
dispctrl-linux brightness 60 --ddc 1       a monitor's own brightness
dispctrl-linux nightlight --from 20:00 --to 07:00
dispctrl-linux display project extend      or duplicate, internal, external
dispctrl-linux preset save Evening         and later: preset apply Evening
dispctrl-linux awake for 2h
dispctrl-linux help                        everything
```

**A screen too dark or too orange?** `dispctrl-linux restore`, or **Restore** in the window or the tray, puts every display back.

## Windows and macOS

DispCtrl began on Windows, and this is its Linux edition, kept in its own repository.

- **Windows 11**: [jesvijonathan/DispCtrl](https://github.com/jesvijonathan/DispCtrl) - the original, by [Jesvi Jonathan](https://github.com/jesvijonathan), from the Microsoft Store or its installer.
- **macOS**: coming soon, from the same project.

## Documentation

| | |
|---|---|
| [DispCtrl for Linux](docs/LINUX.md) | Install, every feature, the engine, the snap, Wayland |
| [Changelog](docs/CHANGELOG.md) | What changed in each release |
| [Traps](docs/developer/TRAPS.md#linux-client) | What was learned the hard way, for contributors |

## Build from source

```bash
git clone https://github.com/HaythmKenway/DispCtrl.git && cd DispCtrl
./build.cmd setup             # the .NET 10 SDK, if missing
./build.cmd linux-build       # the command line, the window and the checks
./build.cmd linux-run gui
./build.cmd linux-package     # .deb, .rpm and tarballs into artifacts/
```

The Linux projects build with their own settings (`build/linux/Linux.Build.props`), not the Windows app's. [Building from source](docs/LINUX.md#building-from-source) has the rest.

## Licence

[MIT](LICENSE). DispCtrl is © 2026 [Jesvi Jonathan](https://github.com/jesvijonathan); the Linux edition is maintained by [@HaythmKenway](https://github.com/HaythmKenway). If it helps you, consider [sponsoring the original author](https://github.com/sponsors/jesvijonathan).
