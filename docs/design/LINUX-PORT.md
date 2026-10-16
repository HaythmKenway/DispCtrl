# A Linux port, for Snapstore distribution

This lays out what has to change for DispCtrl to run on Linux, in what order,
and what does not survive the move at all. The client shipped in
`src/DispCtrl.Linux*` (see `docs/LINUX.md`) covers steps 1-4 and the
.deb and tarball of 6; the rest is still only this plan. Written against the architecture in
`docs/developer/ARCHITECTURE.md` - read that first.

## Why this is a port, not a build flag

Every layer below `DispCtrl.Core` talks to a Windows-only surface: DDC/CI
through Dxva2/`Monitor_Dxva2Functions`, mode and topology changes through CCD
(`QueryDisplayConfig`/`SetDisplayConfig`), brightness for built-in panels
through WMI, gamma ramps through GDI, taskbar and window placement through
Explorer's window classes and `SetWindowPos`/WinEvent hooks, and the panel
itself through WinUI 3 (which cannot even *build* off Windows — `GenXbf.dll`).
None of that exists on Linux, under any display server. This is a rewrite of
`DispCtrl.Display`, `DispCtrl.Engine`'s hooks, and `DispCtrl.App`'s toolkit,
sharing only the parts of `DispCtrl.Core` that are pure model/geometry (presets,
arrangement solving, settings schema).

## What ports as-is

- `DispCtrl.Core`: display model, settings schema, preset diff, physical
  arrangement geometry (`PhysicalLayout`, `ArrangementSolver`), gamma-curve
  math. No hardware calls — already platform-neutral .NET.
- `DispCtrl.Control`'s JSON command shape and the `dispctrl` CLI surface — the
  command vocabulary (`displays`, `brightness`, `nightlight`, `preset apply`,
  ...) is a good fit for a Linux daemon + CLI split too. The transport (named
  pipe) does not port; see below.
- `presetcheck`'s pure-geometry assertions (arrangement, diff, schedules).
- The device library's *data* (`devices/BRAND/PRODUCT/*.json`) and its schema —
  DDC/CI VCP codes are the same standard on both platforms. `devicecheck`
  itself is already cross-platform .NET (the repo's CI already runs it on
  Linux for the intake pipeline).

## What each layer needs instead

### DDC/CI (`DispCtrl.Display`)

Linux has no Dxva2 equivalent in the kernel; DDC/CI goes over `/dev/i2c-*`
via the `i2c-dev` kernel module, exactly what **ddcutil** already wraps.
Two real choices:
- **Shell/link against ddcutil** (`libddcutil`, LGPL) rather than
  reimplementing VCP framing, retries and the 40 ms inter-message gap this
  project already had to learn the hard way — ddcutil has paid the same tax.
  A C# binding is a P/Invoke layer over `libddcutil.so`.
- Internal panels (eDP) still have **no DDC/CI channel**, matching this
  project's own machine table. Brightness there is
  `/sys/class/backlight/*/brightness`, not WMI — a sysfs write, gated by
  udev permissions (needs a udev rule or a setgid helper; there is no
  unelevated-by-default story here the way Windows session brightness is).

### Display configuration (mode, position, primary)

CCD's `SetDisplayConfig` has no analogue. Two backends, not one, because the
project cannot assume a compositor:
- **X11**: XRandR (`libxrandr`/`xrandr` protocol) — mode, position, primary,
  broadly matches the CCD model DispCtrl already targets (explicit positions,
  a primary, per-output modes).
- **Wayland**: no cross-compositor protocol for this. Each compositor exposes
  its own (GNOME Mutter's D-Bus `org.gnome.Mutter.DisplayConfig`, KDE's
  `kscreen`/KWin D-Bus, wlr-based compositors' `wlr-output-management-v1`).
  Realistically this means **three backends** behind one interface, or
  **X11-only for v1** and Wayland later — most distros still run an XWayland
  session under the hood but XRandR calls there do not reach the real outputs.

### Gamma / night light / dimming

`GdiIcmGammaRange` and the identity-ramp clamp this project spent real effort
mapping do not exist on Linux. XRandR carries per-CRTC gamma ramps directly
(`XRRSetCrtcGamma`), with no clamp to fight — likely *simpler* here, not
harder. Wayland has no ramp protocol; `wlr-gamma-control-unstable-v1` covers
wlroots compositors, GNOME and KDE again need their own path (GNOME's night
light is already built in and not controllable externally in the way this
project overrides Windows'). The shade-overlay idea already on this project's
list (from MonitorControl-mac) becomes the portable fallback everywhere a
ramp protocol is missing.

### Taskbar hiding, window pinning, gathering, hotkeys

These assume Explorer's `Shell_TrayWnd` and Win32 WinEvent hooks, both
meaningless on Linux. There is no equivalent concept of "the taskbar" across
desktop environments (GNOME has none by default, KDE's Plasma panel and
XFCE's panel are configured differently, and only some expose an auto-hide
D-Bus/config surface). Pinning windows on top and gathering are compositor
window-manager operations — the closest cross-DE mechanism is **EWMH**
(`_NET_WM_STATE_ABOVE`, `_NET_WM_STATE_STICKY`) on X11 via `libwnck`/direct
Xlib, with no Wayland equivalent at all (Wayland deliberately gives clients no
window-placement authority; only the compositor can do this, so "gather
windows" would need a GNOME Shell extension or a KWin script per DE).
Global hotkeys have the same split: X11 (`XGrabKey`) is straightforward;
Wayland needs the desktop portal's `GlobalShortcuts` interface
(`org.freedesktop.portal.GlobalShortcuts`), which is opt-in per compositor.
**This is the area with the least likely payoff** — much of it may have to
ship as "X11 only, degrades gracefully under Wayland" for a long time.

### The UI (`DispCtrl.App`)

WinUI 3 does not exist on Linux. Candidates, given the Core layer is already
.NET: **Avalonia** (closest to a drop-in replacement conceptually — XAML-like,
runs on .NET, ships on Linux/Snapstore already for other apps) is the
pragmatic choice over a GTK/Qt rewrite, which would mean leaving .NET for the
UI layer entirely. The quick-panel window behaviour (cloak-then-slide,
clip-to-taskbar-edge, notification-area anchoring) is Windows-DWM-specific and
would need a from-scratch design for Linux's tray protocols
(`org.kde.StatusNotifierItem`, which GNOME needs an extension for).

### The engine and IPC

A resident process is the right model on Linux too, but as a **systemd user
service** (`build/packaging/linux/systemd/dispctrl-linux-engine.service`) rather
than a scheduled task, giving equivalent "start at login, restart on
failure" semantics for free. The named-pipe command broker becomes a **Unix
domain socket** in `$XDG_RUNTIME_DIR` — implemented as `dispctrl-linux
engine`, see `docs/LINUX.md` "Engine" for the request/response shape and
its verification. It reuses `dispctrl-linux`'s own command dispatch rather
than a JSON command shape shared with the Windows engine's named pipe -
unifying the two wire formats is future work, not done here.

## Snap packaging

`snap/snapcraft.yaml`, built by `build/packaging/linux/snap/pack.sh` (in
LXD, Multipass, or `--docker` in Canonical's snapcraft image). What was
decided, and why:

- **Strict, without DDC/CI.** The `i2c` interface is slotted by gadget snaps
  on Ubuntu Core and has no slot on a desktop; `custom-device` needs a Store
  grant and still cannot load i2c-dev. The Store grants classic confinement
  to compilers, IDEs and shells, not display utilities. So no ddcutil is
  staged, and `doctor` and the window say DDC/CI is not in the snap rather
  than asking for an install that could only fail (`Distro.InSnap`).
- **Everything X11 works strictly**, because the `x11` plug is the whole X
  protocol: ramps, key grabs, XInput2, overlays, EWMH window moves,
  Composite and RandR layout changes.
- **The backlight** goes through logind with `login-session-control`, which
  is not auto-connected; the docs give the `snap connect` line.
- **The engine starts through snapd's autostart**, not `daemon-scope: user`,
  which is still behind an experimental flag and would start before the
  session's `DISPLAY`.
- **A self-contained publish** in an override-build, with Microsoft's SDK at
  global.json's version, rather than the `dotnet` plugin; the X libraries,
  cairo, pango and fontconfig are staged, GLib and FreeType come from core24.
- **gsettings** reads the host's dconf database through a link `launch`
  makes, or a GNOME shortcut DispCtrl set would read back as missing.

The release workflow's snap job builds amd64 and arm64 and attaches them to
the release; it uploads to the Store only for a published release once the
`SNAPCRAFT_STORE_CREDENTIALS` secret exists (`snapcraft export-login`). The
name has to be registered first (`snapcraft register dispctrl-linux`).

## Status

Shipping, early (see [`docs/LINUX.md`](../LINUX.md) for the user guide and
what was verified on hardware). Steps 1-4 below are done, plus the parts of 6
that do not need a store:

- DDC/CI through the `ddcutil` command; the backlight through sysfs or, for an
  account that may not write it, systemd-logind's `Session.SetBrightness`.
- Gamma ramps through libXrandr directly (`GammaRamp`), because `xrandr
  --gamma` is an exponent and cannot warm a screen. Warmth and dimming compose
  in one write, as on Windows. Pure Wayland sessions are refused, not faked.
- One command set (`DispCtrl.Linux.Core/Commands`) behind the command line,
  the engine's socket and the window; clients go through the engine when it
  runs and work alone when it does not, as `dispctrl.exe` does.
- The engine: a systemd user service bound to the graphical session, with the
  night light schedule, ramp repair after resets, release at stop, and a
  0600 socket. It does not share a wire format with the Windows engine's pipe;
  unifying them is future work.
- Packages: a `.deb` and a self-contained tarball with a per-user
  `install.sh`, built by `./build.cmd linux-package` and attached to each
  release by the Linux job in `release.yml`, with the .rpm and, from the snap
  job, the snaps.

The arrangement geometry is the first code shared with `DispCtrl.Core`:
`Core/Arrangement` is pure geometry, so its three files are compiled into
`DispCtrl.Linux.Core` as they are, and a drag on Linux lands where it would on
Windows. The rest of Core targets Windows. Splitting its other platform-neutral
parts (settings schema, presets, the warmth maths now copied into `Warmth`)
into a `net10.0` library is the step that lets presets reach Linux without a
second copy.

## Recommended order

1. ~~DDC/CI~~ - done, through the `ddcutil` command; binding `libddcutil`
   instead would save a process per call.
2. ~~XRandR layout and gamma ramps~~ - done; ramps through libXrandr.
3. ~~An Avalonia window~~ - done, in the WinUI app's frame: a navigation pane
   and its page names (Displays, Brightness, Windows, Settings), showing only
   what Linux does.
4. ~~The engine as a systemd user service with a Unix-socket broker~~ - done,
   with the schedule and ramp reconciliation.
5. ~~The arrangement~~ - done on X11, on Core's own geometry. Then split
   DispCtrl.Core's other platform-neutral parts into a `net10.0` library, and
   presets. Changing a display's mode or rotation needs a "keep these
   changes?" countdown first, since a mode the monitor cannot show leaves it
   black.
6. ~~Packaging~~ - done: .deb and .rpm for amd64 and arm64, tarballs for
   x64, arm64 and musl, and a strict snap without DDC/CI (the `i2c`
   interface has no slot on a desktop, and classic is not granted to display
   utilities), checked on nine distributions by `distro-check.sh`.
7. ~~Snap layouts and Snap Assist (Linux only)~~ - done on X11: XInput2 raw
   events for drags, EWMH to place windows, cairo overlays, Composite for
   Assist's pictures, the shortcut through GNOME's custom shortcuts.
8. Hotkeys (X11 `XGrabKey`, the portal's GlobalShortcuts on Wayland), a tray
   icon (StatusNotifierItem), and Wayland ramps per compositor
   (`wlr-gamma-control`, Mutter and KWin's D-Bus).
