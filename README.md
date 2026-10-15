<div align="center">

<img src="site/assets/logo.png" alt="DispCtrl" width="104" height="104">

# DispCtrl for Linux

**Every monitor on your desk, controlled as one - on Linux.**

Brightness, night light, display modes, presets and snap layouts for every display,<br>
from a window, a tray icon with a quick panel, and a scriptable command line.

<a href="https://snapcraft.io/dispctrl-linux"><img alt="Get it from the Snap Store" src="https://snapcraft.io/en/dark/install.svg" height="48"></a>

<a href="https://snapcraft.io/dispctrl-linux"><img alt="dispctrl-linux" src="https://snapcraft.io/dispctrl-linux/badge.svg"></a> <a href="https://snapcraft.io/dispctrl-linux"><img alt="dispctrl-linux" src="https://snapcraft.io/dispctrl-linux/trending.svg?name=0"></a> <a href="#install"><img alt="Linux (X11)" src="https://img.shields.io/badge/platform-Linux%20(X11)-FCC624?logo=linux&amp;logoColor=black"></a> <a href="LICENSE"><img alt="Licence: MIT" src="https://img.shields.io/badge/licence-MIT-green"></a>

[Install](#install) · [Getting started](#getting-started) · [Guide](docs/LINUX.md)

</div>

## What it does

- **Brightness for every display**: external monitors over DDC/CI, the laptop's own panel through its backlight, and dimming in software below what a panel can do.
- **Night light** on a schedule, from 6500 K down to 1900 K, and **dark mode** on the same hours (GNOME, KDE, XFCE).
- **Displays**: drag them into place at their real size; change resolution, refresh rate and orientation with fifteen seconds to keep it; extend, duplicate or one display, as with Win+P.
- **Your monitor's own controls**: contrast, input source, colour preset, picture mode, colour gains and volume - only those it lists.
- **Presets**: save the whole desk under a name and put it back in one click.
- **Screen care**: keep awake, Stay active (a one-pixel nudge after 55 seconds still), OLED care, Focus mode, monitor sleep, turning the displays off.
- **Every display together**: unison brightness, each display in its own range, and room light from the ambient light sensor.
- **Automation**: hotkeys for any command, features (named steps) and triggers; presets that apply when a desk connects or an app is in front.
- **RGB lighting** for keyboards, mice, fans and strips, through OpenRGB.
- **Window tools**: pin on top, gather, move a window to another display, span.
- **Snap layouts and Snap Assist**: drag a window to the top centre, or press Super+Z.
- **A tray icon and quick panel** with all of it one click away, and `dispctrl-linux` for scripts.

It needs an X11 session for most of this; on Wayland the compositor keeps gamma and window placement to itself, and brightness still works. The [guide](docs/LINUX.md) has everything, feature by feature.

## Install

```bash
sudo snap install dispctrl-linux          # the Snap Store; --edge for the newest build
```

The snap cannot reach monitors over DDC/CI under strict confinement. For an external monitor's own brightness and controls, build the `.deb`, `.rpm` or tarball (below; with .NET inside, for x86-64 and 64-bit ARM); they will be attached to this repository's releases from the first one. [Install](docs/LINUX.md#install) has the details.

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

## Windows

Looking for DispCtrl on Windows? See the original, [jesvijonathan/DispCtrl](https://github.com/jesvijonathan/DispCtrl).

## Documentation

| | |
|---|---|
| [DispCtrl for Linux](docs/LINUX.md) | Install, every feature, the engine, the snap, Wayland |
| [Changelog](docs/CHANGELOG.md) | What changed in each release |
| [Traps](docs/developer/TRAPS.md#linux-client) | What was learned the hard way, for contributors |

## Build from source

```bash
git clone https://github.com/HaythmKenway/DispCtrl-linux.git && cd DispCtrl
./build.cmd setup             # the .NET 10 SDK, if missing
./build.cmd linux-build       # the command line, the window and the checks
./build.cmd linux-run gui
./build.cmd linux-package     # .deb, .rpm and tarballs into artifacts/
```

The Linux projects build with their own settings, in `build/linux/Linux.Build.props`. [Building from source](docs/LINUX.md#building-from-source) has the rest.

## Licence

[MIT](LICENSE). DispCtrl is © 2026 [Jesvi Jonathan](https://github.com/jesvijonathan); the Linux edition is maintained by [@HaythmKenway](https://github.com/HaythmKenway). If it helps you, consider [sponsoring the original author](https://github.com/sponsors/jesvijonathan).
