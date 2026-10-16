<div align="center">

<img src="site/assets/logo.png" alt="DispCtrl" width="104" height="104">

# DispCtrl for Linux

**Every monitor on your desk, controlled as one - on Linux.**

Brightness, night light, display modes, presets and snap layouts for every display,<br>
from a window, a tray icon with a quick panel, and a scriptable command line.

<a href="https://snapcraft.io/dispctrl-linux"><img alt="Get it from the Snap Store" src="https://snapcraft.io/en/dark/install.svg" height="48"></a>&nbsp;&nbsp;<a href="https://apps.microsoft.com/detail/9PNQWKNRGVR0?mode=direct"><img alt="Get DispCtrl for Windows from the Microsoft Store" src="https://get.microsoft.com/images/en-us%20dark.svg" height="48"></a>

<a href="https://snapcraft.io/dispctrl-linux"><img alt="dispctrl-linux" src="https://snapcraft.io/dispctrl-linux/badge.svg"></a> <a href="#install"><img alt="Linux (X11)" src="https://img.shields.io/badge/platform-Linux%20(X11)-FCC624?logo=linux&amp;logoColor=black"></a> <a href="LICENSE"><img alt="Licence: MIT" src="https://img.shields.io/badge/licence-MIT-green"></a>

[Install](#install) · [Getting started](#getting-started) · [Guide](docs/LINUX.md)

<br>

<img src="docs/images/linux/hero.png" alt="The DispCtrl window with the quick panel open in front of it" width="100%">

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
- **RGB lighting** for keyboards, mice, fans and strips, through OpenRGB: per-key colours on a drawn keyboard, five ready-made looks (Productivity, Coding, Gaming, Focus, Meow), saved colour sets, and a fix for colours that show in the wrong order.
- **Window tools**: pin on top, gather, move a window to another display, span.
- **Snap layouts and Snap Assist**: drag a window to the top centre, or press Super+Z.
- **A tray icon and quick panel** with all of it one click away, and `dispctrl-linux` for scripts.

It needs an X11 session for most of this; on Wayland the compositor keeps gamma and window placement to itself, and brightness still works. The [guide](docs/LINUX.md) has everything, feature by feature.

## A tour

### The quick panel

Click the tray icon: a slider for every display, then night light, keep awake and the rest a fold away.

<div align="center">
<img src="docs/images/linux/quick-panel.png" alt="The quick panel: quick toggles, a brightness slider for each display and night light" width="300">
</div>

### The app

<table>
<tr>
<td width="50%" valign="top"><b>Displays</b>: the arrangement at real size<br><br>
<img src="docs/images/linux/displays.png" alt="The Displays page with the arrangement of two displays" width="100%"></td>
<td width="50%" valign="top"><b>Brightness</b>: unison, calibration, night light<br><br>
<img src="docs/images/linux/brightness.png" alt="The Brightness page with unison brightness, calibration and night light" width="100%"></td>
</tr>
<tr>
<td width="50%" valign="top"><b>Screen care</b>: keep awake, OLED care, focus<br><br>
<img src="docs/images/linux/care.png" alt="The Screen care page with keep awake and OLED care" width="100%"></td>
<td width="50%" valign="top"><b>Lighting</b>: colours, looks and a key-by-key keyboard<br><br>
<img src="docs/images/linux/lighting.png" alt="The Lighting page with colours and the keyboard editor" width="100%"></td>
</tr>
<tr>
<td width="50%" valign="top"><b>Presets</b>: a whole desk, saved<br><br>
<img src="docs/images/linux/presets.png" alt="The Presets page with saved and ready-made presets" width="100%"></td>
<td width="50%" valign="top"><b>Automation</b>: hotkeys, features, triggers<br><br>
<img src="docs/images/linux/automation.png" alt="The Automation page with hotkeys and triggers" width="100%"></td>
</tr>
</table>

<details>
<summary><b>More screenshots</b>: Settings</summary>

<br>

<img src="docs/images/linux/settings.png" alt="The Settings page: the engine, the tray and updates" width="50%">

</details>

## Install

```bash
sudo snap install dispctrl-linux          # the Snap Store; --edge for the newest build
```

Or from the signed apt, dnf and zypper repositories, which update with the rest of the system and, unlike the snap, reach monitors over DDC/CI:

```bash
# Debian, Ubuntu, Mint, Pop!_OS
curl -fsSL https://haythmkenway.github.io/DispCtrl-linux/dispctrl-linux.gpg | sudo tee /usr/share/keyrings/dispctrl-linux.gpg >/dev/null
echo "deb [signed-by=/usr/share/keyrings/dispctrl-linux.gpg] https://haythmkenway.github.io/DispCtrl-linux/apt stable main" | sudo tee /etc/apt/sources.list.d/dispctrl-linux.list
sudo apt update && sudo apt install dispctrl-linux

# Fedora, RHEL, Rocky, Alma
sudo curl -fsSL https://haythmkenway.github.io/DispCtrl-linux/rpm/dispctrl-linux.repo -o /etc/yum.repos.d/dispctrl-linux.repo
sudo dnf install dispctrl-linux
```

openSUSE and the key's fingerprint are in [the install guide](docs/LINUX.md#from-the-apt-or-dnf-repository).

Install one way only: the snap, the packages and the tarball refuse to sit side by side.

The snap cannot reach monitors over DDC/CI under strict confinement. For an external monitor's own brightness and controls, use the `.deb`, `.rpm` or tarball from the [releases](https://github.com/HaythmKenway/DispCtrl-linux/releases) (with .NET inside, for x86-64 and 64-bit ARM). [Install](docs/LINUX.md#install) has the details.

## Getting started

1. Open **DispCtrl** from the applications menu. Its icon appears in the panel's tray; on GNOME that needs the AppIndicator extension (already on in Ubuntu).
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

Looking for DispCtrl on Windows? Get it from the Microsoft Store, or see the original project, [jesvijonathan/DispCtrl](https://github.com/jesvijonathan/DispCtrl).

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

## Support the project

DispCtrl for Linux is free and open source, made in spare time. If it helps you, a donation keeps it going.

| | |
|---|---|
| **PayPal** | [paypal.me/0x4kil](https://paypal.me/0x4kil) |
| **UPI** | `akileswar2002@oksbi` |
| **Bitcoin** | `13pu8fvcNt6Rx8tXJGfFDVWf59uhZuaFEp` |

<p>
<img src="site/assets/support/upi.png" alt="UPI QR code for akileswar2002@oksbi" width="160" height="160">&nbsp;&nbsp;
<img src="site/assets/support/bitcoin.png" alt="Bitcoin QR code for 13pu8fvcNt6Rx8tXJGfFDVWf59uhZuaFEp" width="160" height="160">
</p>

DispCtrl itself is [Jesvi Jonathan's](https://github.com/jesvijonathan) work, and you can [sponsor the original project](https://github.com/sponsors/jesvijonathan) too.

## Licence

[MIT](LICENSE). DispCtrl is © 2026 [Jesvi Jonathan](https://github.com/jesvijonathan); the Linux edition is maintained by [@HaythmKenway](https://github.com/HaythmKenway).
