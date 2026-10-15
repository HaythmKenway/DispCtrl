DispCtrl for Linux 1.0.2 makes lighting dependable:

- **When OpenRGB stops answering**, the Lighting page goes back to saying how to start it, instead of leaving an error under a device it can no longer reach. A **Refresh** button looks for devices again.
- **Pressing Set with no colour typed** asks for one, instead of reporting that "" is not a colour.
- **The advice is now to run OpenRGB headless:** `openrgb --server`. On this desk, OpenRGB's tray window (`--startminimized`) exited after its device scan and took the server with it.

DispCtrl for Linux 1.0.1 fixes five things found in 1.0.0:

- **Lighting in the snap:** the snap could not reach OpenRGB at all, because it lacked the network permission. It has that permission now.
- **The Lighting page without OpenRGB:** it now shows one card that says how to get OpenRGB and switch on its server, instead of controls that only answered with errors.
- **Choosing a wallpaper:** it did nothing from the snap. Neither the engine nor GNOME could read the picture where you chose it. The window now copies it to a folder of its own and sets it from there.
- **Arranging displays:** a dragged display no longer jumps from place to place behind the pointer. It follows the pointer, an outline shows where it will land, and it lands there when you let go.
- **Report a problem:** the button did nothing in the snap, because the confined engine may not open a browser. The window now opens the GitHub issue itself, through the desktop's launcher.

The rest of this page is 1.0's.

## DispCtrl for Linux 1.0

## Install

| Your system | Download | Install |
|---|---|---|
| Any, from the Snap Store | | `sudo snap install dispctrl-linux` |
| Debian, Ubuntu, Mint, Pop!_OS | `dispctrl-linux_1.0.2_amd64.deb` (or `_arm64`) | `sudo apt install ./dispctrl-linux_1.0.2_amd64.deb` |
| Fedora, RHEL, Rocky, Alma, openSUSE | `dispctrl-linux-1.0.2-1.x86_64.rpm` (or `.aarch64`) | `sudo dnf install ./dispctrl-linux-1.0.2-1.x86_64.rpm` |
| Arch, Manjaro, Void, Gentoo, NixOS and the rest | `dispctrl-linux-1.0.2-linux-x64.tar.gz` (or `-linux-arm64`) | unpack, then `./install.sh` |
| Alpine and other musl systems | `dispctrl-linux-1.0.2-linux-musl-x64.tar.gz` | unpack, then `./install.sh` |

Every package carries .NET inside it. `dispctrl-linux doctor` says what it can reach on your system, and how to install anything missing. The checksums are in `dispctrl-linux-1.0.2-SHA256SUMS.txt`.

## What's in 1.0

**Displays and brightness**
- Each display's own brightness: monitors over DDC/CI, and the laptop's backlight.
- Unison: every display together, each within its own range.
- Room light: brightness that follows the ambient light sensor.
- Dimming in software, and night light on a schedule.
- Dark mode, optionally on night light's hours.

**Arranging displays**
- Drag displays into place, drawn at their real size.
- Resolution, refresh rate and orientation, with fifteen seconds to keep a change.
- Extend, duplicate or one display only, as with Win+P.

**Your monitor's own controls**
- Contrast, input source, colour preset, picture mode, colour gains, sharpness and volume.
- Only the controls the monitor itself lists are ever written.

**Presets**
- Save the whole desk under a name: layout, brightness, night light, theme, monitor controls and lighting.
- Ready-made presets: Day, Evening, Night, Reading, Movie, Gaming and Power saver.
- Desk profiles apply a preset when its displays connect.
- App rules apply a preset while an app is in front.
- Launch a program with a preset, and the desk goes back when it exits.

**Screen care**
- Keep awake, and Stay active (a one-pixel mouse nudge after 55 seconds still).
- OLED care: dims the displays in two stages when idle, and pauses for full-screen windows and playing media.
- Focus mode, monitor sleep, and turning the displays off.

**Automation**
- Hotkeys for any command. On GNOME they appear among its own custom shortcuts.
- Features: named lists of steps.
- Triggers run a feature when:
  - a display connects or leaves
  - an app comes to the front
  - you've been idle for a while
  - the screen locks
  - the computer goes on battery
  - it resumes from sleep
  - it's a set time of day

**Windows**
- Snap layouts and Snap Assist, now softer and closer to Windows 11.
- Pin a window on top, gather every window onto one display, move a window to another display, span a window across displays.

**RGB lighting**
- Keyboards, mice, fans and strips, through OpenRGB.

**Tray icon and quick panel**
- Every switch above, one click away.
- The mouse wheel over the icon moves every display's brightness.

**Wayland**
- Brightness, night light (through GNOME's or KDE's own), keep awake, dark mode, presets, hotkeys on GNOME, and lighting all work.
- Dimming, arranging displays, snap layouts and the window tools need an X11 session.

The full guide: [docs/LINUX.md](https://github.com/HaythmKenway/DispCtrl-linux/blob/master/docs/LINUX.md).

DispCtrl began on Windows: see [jesvijonathan/DispCtrl](https://github.com/jesvijonathan/DispCtrl).
