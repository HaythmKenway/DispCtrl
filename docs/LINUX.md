# DispCtrl for Linux

Each display's brightness, software dimming and a night light with a
schedule; the arrangement, resolution, refresh rate and orientation; the
monitors' own controls; unison brightness and room light; keep awake, Stay
active, OLED care, Focus mode, monitor sleep and dark mode; presets, desk
profiles and app rules; hotkeys, features and triggers; window tools and snap
layouts; RGB lighting through OpenRGB; and a tray icon with a quick panel.
From a command line (`dispctrl-linux`), a window and the tray
(`dispctrl-linux-gui`), kept by a small engine that runs with your session.

Most of it needs an X11 session (see [Wayland](#wayland)). What follows is
everything it does.

| | How | Needs |
|---|---|---|
| An external monitor's own brightness | DDC/CI, through ddcutil | `ddcutil`, and a monitor with DDC/CI switched on in its menu |
| A laptop panel's brightness | the kernel backlight, through systemd-logind | nothing: the active session may set it |
| Software dimming, per display | the output's gamma ramp | an X11 session |
| Night light, with a schedule | the output's gamma ramp, 6500 K to 1900 K | an X11 session; the engine for the schedule |
| Snap layouts and Snap Assist | moving windows through the window manager | an X11 session and the engine |
| Arranging displays, and the main display | XRandR positions, in one change | an X11 session |
| Resolution, refresh rate, orientation; extend, duplicate, one display | XRandR, with fifteen seconds to keep it in the window | an X11 session |
| A monitor's own controls: contrast, input, colour preset, picture mode, colour gains, volume | DDC/CI, only the codes the monitor lists | `ddcutil` (not in the snap) |
| Keep awake, for good or for a while | the desktop's inhibitors and logind's sleep lock, held by the engine | the engine |
| Stay active (a pointer nudge after 55 s still) | XTest | an X11 session and the engine |
| Turn off displays | DPMS | an X11 session |
| Dark mode, and dark on night light's hours | GNOME's gsettings, KDE's colour scheme, XFCE's xfconf | GNOME, KDE or XFCE |
| Presets of the whole desk | all of the above, saved under a name | as each part needs |
| Unison: every display together, each in its own range; room light | the targets above; iio-sensor-proxy for the light | room light: an ambient light sensor |
| Hotkeys for any command | GNOME's custom shortcuts on GNOME, an X key grab elsewhere | the engine |
| Features (named steps) and triggers | the engine watches displays, the app in front, idle, lock, power, resume, the time | the engine; libXss for idle |
| Desk profiles and app rules | presets applied when a desk connects, or while an app is in front | the engine |
| Pin on top, gather, move to a display, span | the window manager (EWMH) | an X11 session |
| OLED care, monitor sleep | the gamma ramp after idle; DPMS | an X11 session and the engine |
| Focus mode | a veil over all but the window in front | an X11 session with a compositor, and the engine |
| RGB lighting | OpenRGB's SDK server | OpenRGB running, with its server on |
| Tray icon and quick panel | StatusNotifierItem | a tray: KDE, XFCE, Cinnamon, MATE, Budgie; GNOME with the AppIndicator extension (on in Ubuntu) |

## Install

Every package is self-contained: .NET comes with it, and it runs on any
distribution with glibc 2.27 or later (Ubuntu 18.04, Debian 10, RHEL 8 and
later), or musl (Alpine), on x86-64 or 64-bit ARM. The snap is in the
[Snap Store](https://snapcraft.io/dispctrl-linux); the others are attached to
the [releases](https://github.com/HaythmKenway/DispCtrl-linux/releases) from the
first one, and until then are built with `./build.cmd linux-package`
([Building from source](#building-from-source)):

| Your system | Download | Install |
|---|---|---|
| Debian, Ubuntu, Mint, Pop!_OS | `dispctrl-linux_<version>_amd64.deb` (or `_arm64`) | `sudo apt install ./dispctrl-linux_*.deb` |
| Fedora, RHEL, Rocky, Alma, openSUSE | `dispctrl-linux-<version>-1.x86_64.rpm` (or `.aarch64`) | `sudo dnf install ./dispctrl-linux-*.rpm` or `sudo zypper install ./dispctrl-linux-*.rpm` |
| Anything else: Arch, Manjaro, Void, Gentoo, NixOS... | `dispctrl-linux-<version>-linux-x64.tar.gz` (or `-linux-arm64`) | `./install.sh`, below |
| Alpine and other musl systems | `dispctrl-linux-<version>-linux-musl-x64.tar.gz` | `./install.sh`, below |
| Any, as a snap | the [Snap Store](https://snapcraft.io/dispctrl-linux) | `sudo snap install dispctrl-linux` (`--edge` for the newest build; see [The snap](#the-snap)) |

### From the apt or dnf repository

The easiest way on Debian and Fedora's families: a signed repository, so
DispCtrl updates with the rest of the system.

```bash
# Debian, Ubuntu, Mint, Pop!_OS
curl -fsSL https://haythmkenway.github.io/DispCtrl-linux/dispctrl-linux.gpg | sudo tee /usr/share/keyrings/dispctrl-linux.gpg >/dev/null
echo "deb [signed-by=/usr/share/keyrings/dispctrl-linux.gpg] https://haythmkenway.github.io/DispCtrl-linux/apt stable main" | sudo tee /etc/apt/sources.list.d/dispctrl-linux.list
sudo apt update && sudo apt install dispctrl-linux

# Fedora, RHEL, Rocky, Alma
sudo curl -fsSL https://haythmkenway.github.io/DispCtrl-linux/rpm/dispctrl-linux.repo -o /etc/yum.repos.d/dispctrl-linux.repo
sudo dnf install dispctrl-linux

# openSUSE
sudo zypper addrepo https://haythmkenway.github.io/DispCtrl-linux/rpm/dispctrl-linux.repo
sudo zypper install dispctrl-linux
```

The signing key's fingerprint is `392A 1EEB 594E C9FB 0300 BC66 A1AE 3F54 9366 D3D9`.

The .deb and .rpm pull in what they need. Start the engine once after
installing them; it starts by itself at every login after:

```bash
systemctl --user start dispctrl-linux-engine
```

The tarball installs for your account only, without root, and needs no bash:

```bash
tar xzf dispctrl-linux-*.tar.gz
cd dispctrl-linux-*/
./install.sh                 # to ~/.local, and starts the engine
./install.sh --uninstall     # removes it; settings stay
```

Before copying anything, `install.sh` checks the program can start here, and
names the package when the C++ runtime is missing (only on minimal systems:
`apk add libstdc++ libgcc` on Alpine, `xbps-install libstdc++ libgcc` on Void)
or when the download is for another processor. Without a systemd user manager
(OpenRC, runit, s6, elogind) the engine starts through the desktop's own
autostart instead of a service. Then check what it can reach:

```bash
dispctrl-linux doctor
```

`doctor` names anything missing with your distribution's own install command:
the X libraries a desktop already has (libX11, libXrandr, libXi, libXfixes,
libXcomposite, cairo, pango, fontconfig, libICE, libSM), and ddcutil for
external monitors (`sudo apt install ddcutil`, `dnf`, `zypper`, `pacman -S`,
`apk add`, `xbps-install`), whose package lets the logged-in user use the I2C
buses. Laptop panels have no DDC/CI channel - that is correct, not a fault;
their backlight is used instead. Nothing needs the `xrandr` program: the layout
is read and moved through libXrandr directly.

### The snap

The snap runs under strict confinement, which keeps it from `/dev/i2c-*`: an
external monitor's own brightness over DDC/CI is not available in it, and the
window and `doctor` say so, and so are the monitors' own controls and input
switching. Everything else is: night light, dimming, arranging and display
modes, keep awake, Stay active, dark mode, presets, the tray icon, snap
layouts and the laptop's backlight. The
backlight needs one permission the Store does not connect by itself:

```bash
sudo snap connect dispctrl-linux:login-session-control
```

For DDC/CI, use the .deb, .rpm or tarball instead.

## Use

Open **DispCtrl** from the applications menu, or:

```
dispctrl-linux displays                      outputs, DDC/CI monitors, backlights, with current levels
dispctrl-linux brightness 60 --ddc 1         a DDC/CI monitor (the number from displays)
dispctrl-linux brightness 40 --backlight amdgpu_bl2
dispctrl-linux dim 0.7 --output DP-1         software dimming, 0.1 to 1
dispctrl-linux dim off --all
dispctrl-linux nightlight 60                 strength 0-100, and on
dispctrl-linux nightlight --from 20:00 --to 07:00
dispctrl-linux nightlight off
dispctrl-linux restore                       night light off, no dimming, every ramp back to normal
dispctrl-linux status                        what is on, and what every ramp holds
```

The window has the Windows app's pages: **Displays** (the arrangement,
display mode, and each display's card with its resolution, refresh rate,
orientation and the monitor's own controls), **Brightness** (hardware
brightness, dimming, night light and dark mode), **Screen care** (keep awake,
Stay active, turning the displays off), **Windows** (snap layouts),
**Presets** and **Settings** (the engine and the tray icon).

`dispctrl-linux help` lists everything. Exit codes are those of `dispctrl.exe`
on Windows: 0 done, 1 refused, 2 asked wrongly.

**If a screen is too dark or too orange**, `dispctrl-linux restore` puts
everything back, as does the window's **Restore** button. Dimming never goes
below 10%, so the controls stay readable.

## Arranging displays

The Displays page draws the desk at each display's real size (or by
resolution, with **By resolution**). Drag a display to where it sits: it only
ever lands flush against another, at an end or centred, and a gap it leaves is
closed when you let go, as on Windows. Nothing changes until **Apply**, which
moves every display in one XRandR change and reads it back. **Identify** shows
each display's number on it; **Reset** puts them side by side, tops aligned.
Each display's card makes it the main one.

```
dispctrl-linux display                                each lit output: main, mode, position, size, model
dispctrl-linux display set --output eDP --x 333 --y 1440
dispctrl-linux display set --output DP-1 --primary true
dispctrl-linux display arrange DP-1=0,0 eDP=2560,0    the whole layout at once
dispctrl-linux display arrange reset
```

Positions may be negative, as `dispctrl.exe display set` takes them relative to
the main display; X's desktop starts at 0,0, so the layout is shifted as a
whole. A gap or an overlap is refused, as Windows refuses it.

GNOME and KDE keep a saved layout of their own (GNOME's in
`~/.config/monitors.xml`) and may put it back at the next login or when a
display is plugged in; DispCtrl does not write theirs.

### Resolution, refresh rate, orientation and display mode

Each display's card on the Displays page changes its resolution, refresh
rate and orientation; **Display mode** above them has Windows' Win+P choices.
A change is undone after fifteen seconds unless you keep it, so a mode the
monitor cannot show puts itself back. Displays to the right of or below one
that changes size move with its new edge.

```
dispctrl-linux display modes [--output DP-1]          sizes and refresh rates each offers
dispctrl-linux display set --output DP-1 --mode 1920x1080 --rate 60
dispctrl-linux display set --output DP-1 --rotate left    normal, left, right, inverted
dispctrl-linux display set --output HDMI-1 --enabled false
dispctrl-linux display project extend                 extend, duplicate, internal, external
dispctrl-linux display only DP-1                      that display alone
```

The command line does not count down: a script asked for it.

## A monitor's own controls

Over DDC/CI, a monitor offers more than brightness. DispCtrl reads what it
lists (`ddcutil capabilities`) and offers those on its display's card:
contrast, input source, colour preset, picture mode, the red, green and blue
gains, sharpness and volume. It never writes a code the monitor did not list,
a manufacturer code (E0 to FF), a factory reset or its power mode; those stay
in the monitor's own menu.

```
dispctrl-linux monitor                       DDC/CI monitors and their numbers
dispctrl-linux monitor 1                     the controls it lists, with their values
dispctrl-linux monitor 1 set contrast 70
dispctrl-linux monitor 1 set picture-mode Movie
dispctrl-linux input 1                       its input sources, the current one starred
dispctrl-linux input 1 HDMI-1
```

## Screen care

**Keep awake** stops the computer sleeping, until you switch it off or for a
while. The engine holds it, through every inhibitor the session offers:
GNOME's session manager, `org.freedesktop.ScreenSaver` (KDE, XFCE, Cinnamon),
KDE's power management and logind's sleep lock. They all end with the engine,
so nothing is left keeping a laptop awake in a bag. With **Keep the displays
on**, the screen does not blank or lock either.

**Stay active** moves the pointer one pixel and back after 55 seconds without
it moving, so chat apps and remote sessions do not call you away; a hand on
the mouse is never fought. **Turn off displays** switches every display off
through DPMS; any key or the mouse brings them back.

```
dispctrl-linux awake on | off
dispctrl-linux awake for 30m | 2h | 1h30m
dispctrl-linux awake until 18:30
dispctrl-linux awake displays on|off         keep the displays on too
dispctrl-linux stayactive on|off
dispctrl-linux displays-off
```

### OLED care, Focus mode and monitor sleep

**OLED care** dims every display when nothing has been touched for a while (4
minutes by default), then dims more (after 10), so a still picture does not
stay lit at full strength; the first touch brings it back. It waits while a
window is full screen or a media player says it is playing. **Rest now** dims
at once until the next touch. **Focus mode** darkens everything but the window
in front, on overlays clicks pass through; a full-screen window lifts it. It
needs a compositor. **Monitor sleep** switches the displays off (DPMS) after a
while idle, unless keep awake holds them on.

```
dispctrl-linux oled on --idle-minutes 5 --dim 50 --second-minutes 12 --second-dim 90
dispctrl-linux oled rest
dispctrl-linux focus on --dim 60
dispctrl-linux monitor-sleep on --minutes 20
```

## Every display together: unison and room light

**Unison** gives every display one level, each within its own range, so a dim
laptop panel and a bright monitor reach their lowest and highest together. The
brightness hotkeys and the tray icon's wheel move it. **Room light** moves
unison with the ambient light sensor (through iio-sensor-proxy, which GNOME
and KDE use too).

```
dispctrl-linux unison on
dispctrl-linux unison 60 | up | down 10
dispctrl-linux unison range DELL 20 80       a display by part of its name, its lowest and highest
dispctrl-linux unison exclude built-in
dispctrl-linux brightness up | down          every display, through unison when it is on
dispctrl-linux roomlight on --min 20 --max 100
```

## Hotkeys, features and triggers

The engine holds global shortcuts that run any `dispctrl-linux` command. On
GNOME they become its own custom shortcuts (Settings, Keyboard), since GNOME
keeps Super for itself; elsewhere the engine grabs them. The defaults:

| Keys | Does |
|---|---|
| Ctrl+Alt+Page Up / Page Down | Every display brighter / dimmer |
| Ctrl+Alt+N | Night light |
| Ctrl+Alt+D | The quick panel |
| Ctrl+Alt+O | Turn the displays off |
| Ctrl+Alt+R | Restore: night light off, no dimming |
| Ctrl+Alt+K | Keep awake |
| Ctrl+Alt+P / G | Pin the window in front on top / gather every window here |

They differ from Windows' where Linux desktops already use the key:
Ctrl+Alt+L locks many of them, and Ctrl+Alt+Backspace can end an X session.

A **feature** is a named list of steps: commands, `wait MS`, or `run PROGRAM`.
A **trigger** runs one when something happens: a display connects or leaves,
an app comes to the front or leaves, idle for some minutes and back, lock and
unlock, on battery or mains power, after resume, or at a time.

```
dispctrl-linux hotkeys add Ctrl+Alt+M preset apply Movie
dispctrl-linux features add Late "nightlight 80; dim 0.6 --all; rgb colour warm"
dispctrl-linux triggers add at-time Late --match 22:30
dispctrl-linux triggers add display-connected Docked --match DELL
dispctrl-linux triggers add idle Late --minutes 20
```

## Windows

**Pin on top** keeps a window above the others; **gather** brings every window
to one display; **move** sends one to the next or a given display, keeping its
place; **span** stretches one across displays. The Windows page lists the open
windows with each.

```
dispctrl-linux pin [on | off | toggle] [--window 0x3a00007]
dispctrl-linux placement gather [--to 2 | pointer]
dispctrl-linux placement move --to next
dispctrl-linux placement span [--displays 1,2]
```

## Lighting

Keyboards, mice, fans, LED strips and boards, through
[OpenRGB](https://openrgb.org), which knows each maker's devices and holds the
permissions they need. Start OpenRGB with its SDK server on (`openrgb
--server`, or Settings, SDK Server); DispCtrl talks to it on 127.0.0.1:6742.
Presets keep the lighting too; the ready-made Night dims it to amber and Movie
turns it off.

```
dispctrl-linux rgb                           devices and their effects
dispctrl-linux rgb colour warm | #FF9329 [--device 2]
dispctrl-linux rgb mode "Rainbow Wave"
dispctrl-linux rgb off
```

## Dark mode

```
dispctrl-linux darkmode on | off | toggle
dispctrl-linux darkmode schedule on          dark during night light's hours
```

On GNOME it sets the colour scheme and, where the theme has a dark sibling
(Yaru, Adwaita), the GTK theme with it, as Ubuntu's Appearance page does; on
KDE the Breeze colour scheme; on XFCE the theme's dark sibling. On a schedule
it switches at each end of night light's hours only, so a change by hand
lasts until the next.

## Presets

A preset is the desk as it was when saved: where each display sits and its
mode, each display's brightness, night light and dimming, the theme, and the
monitors' own controls (not the input, which would take the picture away).
Applying one writes only what differs. Monitors are remembered by model and
serial, so a preset made for one monitor is not applied to another on the
same port; `preset list` says whether each fits the displays attached.

```
dispctrl-linux preset save Evening [--skip layout,controls]
dispctrl-linux preset apply Evening
dispctrl-linux preset list | show Evening | rename Evening Night | delete Night
dispctrl-linux preset set Evening --skip none
```

Parts: `layout`, `brightness`, `nightlight` (with dimming), `theme`, `controls`, `lighting`.

A **desk profile** applies by itself when exactly its displays connect; an
**app rule** applies a preset while an app is in front, then puts the desk
back, keeps it, or applies another; **launch** runs a program with a preset
and restores the desk when it ends.

```
dispctrl-linux preset desk Docked on
dispctrl-linux preset rule add vlc Movie --then previous --delay 3
dispctrl-linux preset launch Gaming steam
```

**Ready-made presets** are a starting point on any desk: Day, Evening, Night,
Reading, Movie, Gaming and Power saver. Each sets every display's brightness,
and night light, the theme or the monitors' picture mode (Movie, Game,
Standard, on monitors that list one by that name) where it helps; none moves a
display, and night light's hours are kept. Add them from the Presets page, or:

```
dispctrl-linux preset suggested              what each one does
dispctrl-linux preset add Night Movie        or: preset add all
```

## The tray icon and quick panel

The icon starts at sign-in. Its menu has each display's brightness, night
light and its strength, dark mode, keep awake, Stay active, turning the
displays off, display mode, Identify, presets, snap layouts, Restore, and
the engine. A left click opens the **quick panel** beside it: tiles for the
switches, a slider for each display's brightness and night light's strength,
display mode and presets. The mouse wheel over the icon moves every
display's brightness by 5%. Settings chooses whether a click opens the panel
or the window, or hides the icon (`dispctrl-linux tray off`).

GNOME shows tray icons only with the AppIndicator extension, which Ubuntu
ships on; elsewhere on GNOME, install "AppIndicator and KStatusNotifierItem
Support". Opening DispCtrl while the tray runs brings up its window rather
than a second copy.

## Snap layouts and Snap Assist

As on Windows 11, and only on Linux: DispCtrl for Windows leaves this to
Windows.

- **Drag a window to the top centre of a display.** A bar appears there;
  carry the window onto it and the layouts open - halves, two thirds and one
  third, thirds where there is room, a half and two quarters, quarters, and a
  centre column on an ultrawide or rows on a portrait monitor. Drop it on a
  zone and it fills that space.
- **Or press Super+Z** for the window in front, then click a zone, or use the
  arrow keys and Enter. Esc closes it.
- **Snap Assist**: after a window snaps, the spaces left in that layout offer
  your other windows, with live pictures of them. Click one to put it there;
  Esc, or a click elsewhere, leaves the space empty.

```
dispctrl-linux snap                    the settings, and whether the engine holds the shortcut
dispctrl-linux snap on | off
dispctrl-linux snap shortcut Ctrl+Alt+S
dispctrl-linux snap drag off           only the shortcut
dispctrl-linux snap assist off         snap without the window picker
dispctrl-linux snap gap 8              pixels between snapped windows
dispctrl-linux snap halves 1           put the window in front in a zone (layouts: snap layouts)
dispctrl-linux windows                 the open windows, with the ids --window takes
```

The window's **Windows** section has the same settings. On GNOME the shortcut
is one of GNOME's own custom shortcuts (Settings, Keyboard, Custom Shortcuts),
because GNOME Shell keeps the Super key for itself; DispCtrl adds that entry
while snap layouts are on and the engine runs, and removes it otherwise.
Ubuntu's Tiling Assistant can stay on: its snapping happens at the very edges
of the screen, and DispCtrl's bar sits just below the top edge.

Snapping needs the engine, an X11 session and a compositor (GNOME, KDE,
Cinnamon and most others have one). On Wayland only the compositor may move
windows, and the section says so.

## The engine

`dispctrl-linux-engine` is a systemd user service. It follows the night light
schedule (it wakes at the boundaries rather than polling), puts a ramp back
when a mode change or a monitor plugged in resets it (within ten seconds), and
when it stops - at logout, or `systemctl --user stop dispctrl-linux-engine` -
it puts every ramp it warmed or dimmed back to normal.

The command line and the window send their commands to it when it runs, so
there is one writer of the ramps and one queue in front of each DDC/CI
monitor; without it they do the work themselves, and everything except the
schedule still works.

```
systemctl --user status dispctrl-linux-engine
journalctl --user -u dispctrl-linux-engine     what it changed, and why
dispctrl-linux engine status
```

It listens on `$XDG_RUNTIME_DIR/dispctrl-linux.sock`, readable by your account
only. A request is one line of JSON, `{"args":["nightlight","on"]}`, and the
reply is `{"exitCode":0,"stdout":"...","stderr":""}`.

Settings live in `~/.config/dispctrl-linux/settings.json`, written whole and
renamed into place; the engine sees a change at once. A hand edit is fine:
values out of range are pulled back, and a file that is not JSON is set aside
as `settings.json.bad`.

## Things that get in the way

- **GNOME's own night light** writes the same ramps; whichever writes last
  wins. Switch one off (`doctor` says when GNOME's is on).
- **Redshift, gammastep or a calibration loader** - the same. DispCtrl leaves
  a ramp alone while its own night light and dimming are off.
- **DDC/CI switched off in the monitor's menu**, or a dock or KVM that does not
  pass it through: the monitor will not appear under DDC/CI in `displays`.
- **Backlight over SSH or on a second seat**: logind only lets the active local
  session set it. There, install `/usr/lib/udev/rules.d/90-dispctrl-backlight.rules`
  (the .deb does; `./install.sh --backlight-rule` for the tarball) and join the
  video group: `sudo usermod -aG video $USER`.

## Wayland

Wayland compositors keep gamma ramps and window placement to themselves. On
Wayland, night light goes through the desktop's own (GNOME's or KDE's: its
strength, schedule and on or off), brightness, unison, room light, keep awake,
dark mode, presets' brightness and theme, hotkeys on GNOME, and lighting all
work, and the rest says it needs X11. Log in to an X11 session ("Ubuntu on
Xorg", "Plasma (X11)") for dimming, arranging, snap layouts, the window tools,
OLED care and Focus mode.

## Updates and problems

`dispctrl-linux update` asks this project's releases whether a newer one is
out (one request, nothing downloaded); the snap updates itself.
`dispctrl-linux report` shows what a problem report needs - the version, the
desktop and `doctor` - with serial numbers, your name and home folder taken
out; `report --open --what "..."` starts a GitHub issue with it for you to read
before sending. `dispctrl-linux wallpaper PICTURE` sets the desktop's picture
on GNOME, KDE and XFCE.

## Building from source

```bash
./build.cmd setup              # the .NET 10 SDK into .tools/, if missing
./build.cmd linux-build        # the command line, the window and the checks
./build.cmd linux-run cli doctor
./build.cmd linux-run gui
./build.cmd test               # includes DispCtrl.Linux.Checks on Linux
./build.cmd linux-package      # tarballs, .deb and .rpm (with rpmbuild), in artifacts/linux-<version>/
build/packaging/linux/snap/pack.sh --docker      # the snap, in artifacts/snap/
build/packaging/linux/distro-check.sh artifacts/linux-<version>   # install on nine distributions (Docker)
```

`src/DispCtrl.Linux.Core` holds the hardware (ddcutil, the backlight, XRandR
ramps through libXrandr), settings, every command and the engine's protocol;
`src/DispCtrl.Linux` is the command line and the engine; `src/DispCtrl.Linux.Gui`
the Avalonia window; `build/packaging/linux` the packages, the systemd unit and
the udev rule. `tests/DispCtrl.Linux.Checks` needs no monitors and no X server:
it points X at a display that cannot answer, so it never changes a ramp. The
release workflow builds and attaches the Linux packages, and the snaps, after
the Windows ones; CI installs the tarballs on nine distributions whenever the
Linux client or its packaging changes.
The traps already paid for are in [TRAPS.md](developer/TRAPS.md#linux-client).

## Verified on

Ubuntu 24.04, an X11 GNOME session, an AMD laptop panel (`eDP`, backlight
`amdgpu_bl2`, no DDC/CI) and a Dell P2723DE over DisplayPort (`DP-1-0`,
DDC/CI on `/dev/i2c-5`), on 2026-10-04:

- DDC/CI brightness written and read back on the Dell, and restored.
- The laptop backlight set through logind by an account in neither the video
  nor the i2c group, and restored.
- Night light at strength 60 read back from the CRTC as 1.000 / 0.784 / 0.614;
  dimming at 80% on top as 0.800 / 0.627 / 0.491 - composed, not fighting.
- The engine: a schedule window opened and closed on the minute; a ramp reset
  by `xrandr` repaired on the next pass; SIGTERM put both ramps back and removed
  the socket; a second engine refused to start; the socket was 0600.
- The window, driven through the accessibility tree: switching night light on,
  a strength sweep, dimming and Restore each reached the hardware and the
  settings; opening it wrote nothing.
- The .deb's binaries and the tarball's `install.sh` / `--uninstall` against a
  scratch home. The .deb has not been installed system-wide on this machine.
- Snap layouts, on throwaway test windows only: a window dragged by synthetic
  pointer input onto the bar, then onto "halves, zone 2", landed at exactly
  935 x 1048 at +1318+1472; Super+Z, through the GNOME custom shortcut, opened
  the layouts and Right, Return snapped the window; Snap Assist then put the
  chosen window in the other half. Windows decorated by the window manager and
  GTK windows with client-side shadows both landed pixel-exact. Assist's cards
  show each window's live picture, cropped to what is visible. GNOME's custom
  shortcut list was back exactly as before once the engine stopped.

On 2026-10-11, in containers, with `build/packaging/linux/distro-check.sh`:
the tarball on Debian 12, Ubuntu 20.04 and 24.04, Fedora 42, Rocky 8, openSUSE
Tumbleweed, Arch, Alpine 3.20 (musl) and Void - install.sh, doctor's install
command for each, a display read through libXrandr from Xvfb, the window
running, uninstall - and the .rpm installed with its dependencies resolved on
Fedora 42, Rocky 8 and openSUSE. The snap was built, unpacked and run against
Xvfb, but not installed: that needs `snap install --dangerous` on a desk.
