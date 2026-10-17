# Changelog

All notable changes to DispCtrl are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). The release workflow publishes the
section matching the tag as the release notes, so write entries for people
using DispCtrl, not for people reading the diff.

## [Unreleased]

### Added
- **Linux: `settings`, `apply`, `commands` and `topology`, as on Windows.** `settings get|set|reset|export|validate|import` reads and changes any saved setting by its JSON path (`settings set --path /unison/level --value 50`), refusing a value that would not load; `apply FILE [--dry-run]` runs a file of commands, one a line; `commands` lists the command words; `topology extend|duplicate|internal|external` is `display project`.
- **Linux: unison calibration.** As on Windows, `dispctrl-linux unison calibrate start|capture|cancel|status` (and *Calibrate the range* on the Brightness page) takes every display to its dimmest, lets you match them by eye, captures, then does the same for the brightest; the two captures become each display's range. Cancel puts the displays and the slider back, a brightest not above the dimmest is refused, and unison, room light and the brightness keys stand still while it is open.
- **Linux: a log you can send.** The engine, the window and the command line now write one rotating log (1 MB, three files; `~/.local/state/dispctrl-linux/logs`, or the snap's own folder) with levels, times and components, serial numbers, your name and home folder taken out before a line is written, and every unhandled exception recorded in full. `dispctrl-linux logs [--tail N] [--path] [--open]` reads it. `report` now adds the log's end to the GitHub issue (cut to fit the address, with a note), `report --mail` opens an email to hello@akileswar.com for you to read and send, and `report --save [FILE]` writes the whole redacted report and log to a file to attach (GitHub cannot take attachments through a link). Settings > Report a problem has Open GitHub issue, Email the log, Save report… and Open log folder; nothing is ever sent for you.

### Fixed
- **Linux: the Lighting page and `rgb` commands no longer fail on an unexpected OpenRGB answer.** Only a refused connection was handled, so a device description DispCtrl could not parse, a stalled server or a listener that was not OpenRGB left the page blank or an effect click doing nothing. Now one unreadable device is skipped and the rest stay usable, listing is retried when OpenRGB has not finished scanning, a socket is never left open after a failed handshake, a device that cannot be drawn shows a note instead of emptying the page, overlapping refreshes no longer duplicate cards, and every failure ends as a message saying what to do (and a line in the log), never a crash. A headless OpenRGB server that is older than its keyboards' device nodes (a keyboard re-enumerated and the server went on writing to the old nodes, so every colour was accepted and nothing lit) is now restarted before the next lighting command, and a page drawn before OpenRGB renumbered its devices reads the list again instead of sending to the wrong one.

### Changed
- **Linux: the look follows your desktop, not just GNOME.** The quick panel, Snap Assist and the window now read their dark or light mode, interface font and accent colour from where each desktop keeps them: GNOME and Budgie's settings, Cinnamon's and MATE's own, KDE's `kdeglobals` (Plasma's accent colour and Breeze blue as the fallback) and XFCE's xfconf. Ubuntu's Yaru accents still work, but nothing assumes Ubuntu.
- **Linux: at most one install runs.** The snap, the .deb/.rpm and the tarball can no longer sit side by side with two engines and two trays fighting over gamma ramps, hotkeys and settings. The .deb and .rpm refuse to install beside the snap or a tarball install, naming `sudo snap remove dispctrl-linux`; `install.sh` refuses beside the snap, a package or another copy (`--force` overrides, only there); the packages declare `Conflicts` with other DispCtrl package names. At run time every engine and window refuses to start while another install's engine runs (found in `/proc` and through the `org.dispctrl.Linux` name on the session bus, which the snap claims through a new `dbus` slot), with a line saying how to remove the other. `doctor` has a "one install" line, and the window and the tray tooltip show a second install. `install.sh` no longer leaves an engine autostart entry beside the systemd unit it enables. A snap cannot see the host's files or all of `/proc`, so its check is partial; see docs/LINUX.md.
- **Linux: five ready-made keyboard looks.** Productivity, Coding, Gaming, Focus and Meow, on any keyboard with a light for each key: one click on the Lighting page, or `dispctrl-linux rgb look NAME`. Each is worked out from the keyboard's layout, so it fits a 60%, a tenkeyless and a full board alike. The saved colour sets are now one-click chips, and the lighting page has fewer buttons.
- **Linux: saved colour sets for each keyboard.** Name the colours a per-key keyboard holds (Gaming, Night) and put them back later, from the Lighting page or with `dispctrl-linux rgb preset save|apply|delete|list NAME`. Sets are kept per keyboard, so two keyboards can each have their own.
- **Linux: keyboard colours match the keyboard.** Some LEDs take their colours in another order (the EVision keyboard on the reference desk shows red as green). A device's colour order is set with `dispctrl-linux rgb order GRB`, or found by the Lighting page's *Fix the colours* steps, which ask what the keyboard shows; EVision keyboards start as GRB. Colours drawn, saved and sent are always the ones meant.
- **Linux: a clearer keyboard editor.** The keyboard has a card of its own, scaled to fit so the numpad is no longer cut off, blank unplaced keys are gone, and there are ready colours beside the picker; keys lift under the pointer and press when coloured, and buttons respond to the pointer (unless the desktop turns animations off).
- **Linux: a new display arrangement canvas.** The Displays page draws your monitors to scale with name, resolution, refresh, pixel density, a main badge and an orientation marker. Drag with snapping and alignment guides (no gaps or overlaps), or select one and nudge with the arrow keys; Ctrl+Z undoes, Ctrl+wheel and +/-/0 zoom. An inspector holds resolution, refresh, orientation, position, brightness and dim, and arranging offers keep-or-revert for fifteen seconds. Mirrored and switched-off displays are shown as such, and without X or on Wayland the page says what cannot be done. Hardware reads and writes stay off the interface thread, slider writes roll back when refused, display lists update in place and pages cross-fade briefly unless GNOME animations are off.

### Fixed
- **Linux: pin on top works after clicking the desktop.** With the pointer last on the desktop, GNOME's desktop-icons window was the active one and `pin` (and Ctrl+Alt+P, move and span) refused it as no application window; they now act on the topmost real window, and a pin button that fails springs back.
- **Linux: the tray icon no longer sometimes opens nothing.** The icon registers again when GNOME's shell or its AppIndicator extension restarts, opens a new bus connection when the old one drops, and waits for the tray at sign-in instead of giving up. A window that failed to open is built again on the next click rather than activated for ever, the quick panel that never took focus is shown instead of closed, and the window comes to the front on another workspace. Starting `dispctrl-linux-gui` while it runs now uses a lock that a killed process cannot leave stale, ignores a client that connects and says nothing, and waits for the running one to confirm; if its interface is blocked it says so and exits with 1. Every click and hand-off is logged to stderr (the snap's journal). `--open` names the window explicitly.
- **Linux: Focus mode no longer dims the dock, the top bar or the window's own popups.** Ubuntu Dock and the GNOME top bar, the focused window's dialogs, menus and popups, and DispCtrl's own windows now stay clear; only the rest is veiled.

### New
- **Linux: per-key RGB.** Keyboards with a light for each key are drawn on the Lighting page, to select keys and colour them (gradient, rainbow, whole rows), and `dispctrl-linux rgb key a,s,d,f red` / `rgb keys "esc=green,space=#8000FF"` do the same from the command line.
- **Linux: lighting sets itself up.** `dispctrl-linux rgb start` finds OpenRGB (PATH, AppImage, Flatpak, snap) and starts it headless; `rgb setup` downloads the official 1.0 AppImage, verified against a pinned SHA-256, when there is none; lighting commands start it themselves (`rgb autostart off` stops that), the engine does at sign-in once lighting is in use, and the Lighting page has Start lighting and Set up lighting. The udev rules are checked and the one root command printed. Without OpenRGB, a laptop's keyboard backlight (`/sys/class/leds`) is used: its colour where it has one, otherwise its brightness, and `rgb` says which.
- LG UltraGear (GSM-5B71): response time and 1ms Motion Blur Reduction can be set from DispCtrl ([#33](https://github.com/jesvijonathan/DispCtrl/issues/33)).
- LG UltraWide (GSM-5BF7): colour temperature can be set as Warm, Medium, Cool or Custom, as the monitor's menu names them ([#31](https://github.com/jesvijonathan/DispCtrl/issues/31)).
- **DispCtrl for Linux** (early, X11): a command line (`dispctrl-linux`) and a window for each display's brightness - external monitors over DDC/CI, a laptop's own panel through its backlight with no root or setup - plus software dimming and a night light with a schedule, kept by a small engine that runs with your session. A `.deb` for Debian and Ubuntu, and a tarball with a per-user installer. See [DispCtrl for Linux](LINUX.md).
- **Arrange displays on Linux** (X11): drag each display to where it sits, drawn at its real size, then apply; Identify, Reset and the main display as on Windows. The same from the command line: `dispctrl-linux display set --output DP-1 --x 0 --y 0`, `display arrange`, `display set --primary true`.
- **The Linux window looks and reads like the Windows app**: a navigation pane with the same pages - Displays, Brightness, Windows and Settings - and each display's card with its brightness, dimming, mode, size and position.
- **DispCtrl for Linux on every distribution**: an .rpm for Fedora, RHEL and openSUSE beside the .deb, both also for 64-bit ARM; tarballs for x86-64, ARM and Alpine (musl); and a snap. `install.sh` needs no bash and says what is missing before it copies anything, the engine starts without systemd through the desktop's own autostart, and `dispctrl-linux doctor` names any missing library with your distribution's install command.
- **Linux: presets.** Save the whole desk under a name - each display's place, mode and orientation, its brightness, night light and dimming, the theme and the monitors' own controls - and put it back in one step, from the new Presets page, the tray or `dispctrl-linux preset save|apply`.
- **Linux: a tray icon and quick panel.** From sign-in, a menu with each display's brightness, night light, dark mode, keep awake, Stay active, display mode, presets, Identify and the engine; a left click opens a quick panel of tiles and sliders.
- **Linux: resolution, refresh rate, orientation and display mode.** Each display's card changes them, with fifteen seconds to keep the change; Extend, Duplicate and one display as with Win+P. `dispctrl-linux display set --mode 1920x1080 --rate 60 --rotate left`, `display project`, `display only`.
- **Linux: a monitor's own controls and input switching** over DDC/CI: contrast, input source, colour preset, picture mode, colour gains, sharpness and volume, only those the monitor lists. `dispctrl-linux monitor 1 set contrast 70`, `input 1 HDMI-1`.
- **Linux: keep awake, Stay active and turning the displays off** on a new Screen care page: keep the computer (and its displays) awake for good, for a while or until a time; nudge the pointer after 55 seconds still; switch every display off. `dispctrl-linux awake for 2h`, `stayactive on`, `displays-off`.
- **Linux 1.0: hotkeys, features and triggers.** Global shortcuts for any command (GNOME's own custom shortcuts on GNOME), named lists of steps, and running them when a display connects, an app comes to the front, after idle, on lock, on battery, after resume or at a time. Desk profiles and app rules apply presets by themselves; `preset launch` runs a program with one.
- **Linux 1.0: unison brightness and room light.** One level for every display, each in its own range, moved by the brightness hotkeys and the tray icon's wheel; and unison that follows the ambient light sensor.
- **Linux 1.0: OLED care, Focus mode and monitor sleep** on the Screen care page, and **window tools** (pin on top, gather, move to a display, span) on the Windows page.
- **Linux 1.0: RGB lighting through OpenRGB** - keyboards, mice, fans and strips - on a Lighting page, in the tray and in presets.
- **Linux 1.0: night light on Wayland** through GNOME's or KDE's own; `update`, `report` and `wallpaper`; a softer Snap Assist with a fade-in; and a tray icon whose panel opens at once.
- **Linux: ready-made presets** - Day, Evening, Night, Reading, Movie, Gaming and Power saver - for any desk, from the Presets page or `dispctrl-linux preset add all`. The mouse wheel over the tray icon moves every display's brightness.
- **Linux: dark mode**, on GNOME, KDE and XFCE, and dark during night light's hours. `dispctrl-linux darkmode on`, `darkmode schedule on`.
- **Snap layouts and Snap Assist on Linux** (X11): drag a window to the top centre of a display, or press Super+Z, and choose where it goes - halves, thirds, quarters and more, picked for each display's shape; then fill the spaces left from pictures of your other windows. On GNOME the shortcut appears among its custom keyboard shortcuts.

### Fixed
- Linux: the window is easier to read and use - headings stand out from card titles, cards are an even height, slider readouts line up, the Windows page's gap slider has an icon, the default size is smaller, the Brightness and Displays pages show a progress bar while scanning and say when no display has a control, and a display without a brightness or dimming control no longer logs a binding error for every slider.
- Linux 1.0.2: the Lighting page goes back to how to start OpenRGB when it stops answering, has a Refresh button, and asks for a colour when Set is pressed with none; the advice is OpenRGB headless (`openrgb --server`), since its tray window can exit after the device scan.
- Linux snap 1.0.0: lighting could not reach OpenRGB (the snap lacked the network permission), and Report a problem did nothing (the engine may not open a browser; the window now does). The Lighting page says how to get OpenRGB when it is not running, rather than showing controls that fail. Choosing a wallpaper works from the snap, and a display being arranged follows the pointer instead of jumping behind it.
- Linux snap: a GNOME shortcut (snap layouts' Super+Z) named the snap revision's own path, which the next refresh deleted.
- Linux snap: the tray icon's menu never opened (the snap's confinement refused the path it was served at), the engine's Start button did nothing, and after a refresh the old engine ran on unreachable.
- Linux: the window showed the generic application icon in GNOME's dock and KDE's task manager instead of DispCtrl's.
- Linux: the window would not open on desktops without GNOME's `gsettings` (KDE or XFCE without GLib's tools, Alpine, Void), or with no system font installed.
- Linux: arranging displays and reading the layout no longer need the `xrandr` program, and the laptop backlight works on distributions without systemd (through `dbus-send` or `gdbus`).
- LG UltraWide (GSM-5BF7): DispCtrl no longer calls FE "Gamma"; the monitor ignores writes to it ([#31](https://github.com/jesvijonathan/DispCtrl/issues/31)).
- In Duplicate, the app said only one display was connected and offered no way back to Extend or one screen.
- With two external monitors, "PC screen only" lit "Second screen only" on the Displays page and in the quick panel. The lit choice is now the one Windows' own Project flyout shows, and presets record it the same way.
- The quick panel's display mode buttons follow a switch between Duplicate and one screen straight away, instead of after the panel is reopened.
- The Hotkeys page opens at once instead of after a second or two.

## [0.2.1] - 2026-10-03

### Highlights
- **Presets** (Beta): save the whole desk and put it back in one click, automatically when a dock's displays connect, or while an app is in front. Choose what each preset restores.
- **Taskbar glass looks**: blur, clear, opaque or acrylic, in your own colour or Windows' accent, with or without the top border.
- **Triggers**: run a feature when a display connects, an app comes to the front, the computer locks or wakes, at a time of day, and more.
- **A clearer app**: Brightness, Screen care and Windows have pages of their own, and each card shows the common settings first.
- **Smaller and lighter**: downloads are about 40% smaller, and the background engine uses about a third less memory.

### New
- OLED care can turn a resting display fully off as a third stage, and pauses while a video plays.
- Night light warmth and focus dimming can be set per display.
- Move the window in front to another display, or stretch it across all of them.
- Shortcuts for the Win+P arrangements, for more features, and for your own commands and programs (all off until you switch them on).
- A Brightness section at the top of the quick panel, and a slider that dims in software for displays with no brightness control of their own.
- Company laptop switches under Miscellaneous: sign-in, lock screen and dynamic lock.
- A simpler way to name a monitor's own controls: change a setting with its buttons and DispCtrl finds it.
- Monitor sleep can put a display in standby or suspend instead of off, choosing from the states that monitor lists.
- A monitor control you map yourself can say it is written in smaller units than it reads, as LG's Black Stabilizer is.

### Improved
- The quick panel opens and closes more smoothly, and starts with Brightness and Quick toggles open.
- Virtual displays (Parsec, spacedesk), USB adapters and Miracast are recognised; FancyZones keeps placing its own windows.
- `dispctrl help` is organised by topic, and the old short commands still work.
- Update checks are on by default: once a day, the version number only, never a download.
- The portable download is now named `...-portable.zip`.
- One `SHA256SUMS.txt` lists every download.

### Fixed
- Identify's numbers stay on their own displays.
- The pinned taskbar icon no longer shows a square behind it.
- Several preset fixes: Discard clears the banner, apply no longer undoes changes made meanwhile, and long or reserved names save.
- The window no longer vanishes when its monitor was unplugged.
- Settings saved by the engine and the app at the same moment are no longer lost.
- After an install for everyone, Settings shows the Start menu shortcut as on, as Setup made it.
- Monitor sleep is no longer offered for the LG UltraWide (GSM-5BF7), which cannot be woken from it, and is only ever sent in a state the monitor lists ([#30](https://github.com/jesvijonathan/DispCtrl/issues/30)).
- LG UltraWide (GSM-5BF7): Black Stabilizer goes 0 to 100 in steps of 5, as its menu does, and Gamma and three more picture modes have their menu names ([#29](https://github.com/jesvijonathan/DispCtrl/issues/29)).

## [0.1.5] - 2026-09-26

### Added
- Pin any window on top - Ctrl+Alt+P, the quick panel or `dispctrl pin`. A coloured border marks it, focus mode leaves it clear, and it steps aside while a film or game is fullscreen in front of it.
- Gather every window onto one display - Ctrl+Alt+G, the quick panel or `dispctrl placement gather` - maximized, fullscreen and minimized windows included. Windows can also go back to a monitor when it returns, and new windows can open on the display you are using.
- OLED care can rest each display on its own while you work on another, keeps a display awake while an app you list is showing on it, and lists every monitor it has seen, connected ones first, so one can be marked OLED even while it is unplugged.
- Leave a display out of unison brightness; turn the mouse wheel over the tray icon to change brightness; switch Windows to dark mode on night light's schedule.
- A guard that stops DispCtrl talking to a monitor whose capabilities read crashed Windows, and a read-only probe for monitors that do not say what they support.
- An opt-in check for new versions (Settings > Updates): off until you switch it on, and never for the Microsoft Store version, which updates itself.
- The installer can install for everybody on the PC as well as just for you.

### Changed
- Focus mode's two pointer switches are one choice: keep the focused window clear, the window under the pointer, or both.
- A settings file written by a newer DispCtrl now loads in an older one: what it cannot read is set aside and kept, instead of every setting going back to its default.
- A display's card fills in at once; its colour profile, which Windows can take seconds to report, arrives on its own.
- The quick panel's new window tiles and Windows section start hidden; add them from the Quick panel page.

### Fixed
- Dragging the unison slider was jumpy and could end a step away from where you let go: the link to Windows' brightness took the slider's own changes for the brightness keys.
- OLED care's rest and Turn off displays now cover the taskbar too. It could come up over them and stay undimmed.
- Scrolling an expanded display card to the bottom looped and never got there.
- A display's wallpaper preview stayed empty when its wallpaper file had been replaced - ASUS OLED Shifter does this every few minutes. It now falls back to Windows' own copy of that display's wallpaper.
- With Stay active on, OLED care's dimming never started.
- Pinning refused a maximized window on a display whose taskbar DispCtrl hides, calling it fullscreen.

## [0.1.4] - 2026-09-25

These changes also cover 0.1.3, whose entries were not split out.

### Changed
- A toggle in the app or quick panel now reaches the engine in about 10 ms instead of 135, and a brightness slider reaches the monitor as it moves instead of waiting until you stop.
- The engine starts and takes effect sooner at sign-in: it answers the app and command line almost at once, no longer waits for OLED care's overlays, the brightness keys' listener or the monitor history before carrying on, and applies taskbar glass and hiding the moment Explorer's taskbar appears.
- Downloads are about 30% smaller (the desktop zip is ~74 MB, was ~104): parts of the Windows App SDK that DispCtrl never uses are no longer shipped.
- Beta and test builds show their version in the title, and monitor shares include the version they came from.
- Stable builds no longer log routine activity, only problems.
- Releases can update and commit the version automatically before tagging. A test channel creates numbered GitHub prereleases alongside beta and stable releases; packaging scripts use the project version by default.
- With one display connected, unison steps aside: the quick panel's unison section shows that display's own brightness, simple view drops "All displays", and the Displays page says why its unison slider is idle. Display mode and "Multiple displays" are greyed until a second display is connected. Nothing is switched off, and unison carries on from where it was left when another display arrives.
- Replace Windows brightness no longer pulls the laptop's screen back inside its calibrated range while it is the only display, so the brightness keys reach the whole range.

### Fixed
- The quick panel could open on another display, or halfway along the screen wherever the pointer was. It now always opens beside the notification area, like Windows' own flyouts.
- The quick panel opened with a grey background until clicked, and could be seen sliding under a translucent taskbar.
- Unplugging the main monitor left the taskbar in a mess: the laptop's screen became the primary display, and DispCtrl kept trying to hide a taskbar Windows always puts back, and took the whole screen for windows under it. A display set to hide its taskbar now leaves it to Windows while it is the primary display, and hides it again once it is not.
- A monitor plugged in or reconnected often showed no brightness or controls until Rescan was pressed, because it was read before it could answer. DispCtrl now waits for the displays to settle, reads a new monitor again a few seconds later if it did not answer, and updates the Displays page, its arrangement diagram and an open quick panel by itself.
- A loose cable that drops a monitor for a moment no longer sets off a round of taskbar and brightness changes: a change counts only once the displays have stayed the same for a second and a half.
- Night light reaches a newly connected monitor straight away, instead of up to 20 seconds later.
- `build clean` failed with "access denied" on the taskbar glass helper, because Windows Explorer had loaded it straight from the build folder. The engine now gives Explorer its own copy in DispCtrl's data folder, and clean skips, with a note, a helper an older engine left loaded until Explorer restarts.
- Taskbar glass did nothing after switching between the installer or a zip and the Microsoft Store version until Windows Explorer was restarted: Explorer still held the other build's glass helper. The engine now retires that helper and attaches its own, as long as no other DispCtrl engine is running.
- Following the room's light kept the engine waking about four times a second in a still room, because a light sensor's readings jitter. Readings too small to count are now noted without waking it at all.

## [0.1.2] - 2026-09-25

### Added
- `dispctrl ambient get|set|reset|capture|forget`: every setting for following the room's light, what the sensor reads now, and calibration - cover the sensor and `capture --as dark`, light it and `capture --as bright`.
- The tray icon's right-click menu can switch the quick panel between simple and full view, stop the engine, close the app while the engine carries on, or exit DispCtrl altogether - the app, the quick panel and the engine.
- Following the room's light learns: a unison level set by hand while it follows - the slider, a hotkey, the brightness keys - is remembered for that light. `dispctrl ambient forget` starts again.

### Changed
- The Displays page's light sensor section sits last under Unison brightness, with its options folded under a chevron: the sensor, the levels for a dark room and bright light, calibration ("This is dark", "This is bright") with the sensor's reading shown live, and learning from your adjustments. Switching it on switches unison brightness on too, since that is what it drives.

### Fixed
- In the Microsoft Store version, "open the log" and the other folder buttons opened nothing, and no DispCtrl folder appeared in `%LOCALAPPDATA%`. The data was always kept, in the app's own folder under `%LOCALAPPDATA%\Packages`; the app now uses and shows that real folder.
- Taskbar glass did nothing in the Microsoft Store version: Windows would not let Explorer load its helper from the Store's install folder.
- Following the room's light barely moved when the sensor was covered, never reached full brightness under a torch, and was slow to react: it stopped partway after the sensor's last reading, and a light that kept changing kept restarting its wait. It now reaches the new level within a few seconds, walks there in steps rather than jumping, and still ignores a hand passing the sensor.
- Following the room's light no longer fights Replace Windows brightness over the built-in panel.
- The simple quick panel lines up: each name, slider and value on the same edges as the title.
- The quick panel takes focus when it opens from the tray icon, instead of opening inactive with a grey background until clicked.

## [0.1.1] - 2026-09-24

### Added
- **Follow the room's light**: unison brightness can follow an ambient light sensor, from a level for a dark room to one for bright light, smoothed so passing shadows do not move it. Under Unison brightness, with the sensor to follow.
- **Undo the way back**: Settings > Undo the way back, or `dispctrl restore undo`, switches back on what Ctrl+Alt+Backspace last turned off - each monitor's taskbar hiding and dimming, night light, focus, OLED care and taskbar opacity. `dispctrl restore now` does what the shortcut does.
- **Ctrl+Alt+Backspace puts every display back**: it undoes displays off, dimming, night light, taskbar hiding and opacity, keeping every other setting. On by default, and the Hotkeys page asks before it is switched off or removed.
- The device library counts how many times each model has been contributed, by issue number only; the catalogue shows it.
- Turn off displays shows Keep awake and Stay active beside it - the same switches as everywhere else, so what is on stays on and chat apps keep showing you as available while the displays are off.
- Turn off displays can also turn each display's real backlight down, at 90% darkness and more, and puts it back as the display wakes.
- Turn off displays can lock the computer when they wake: whoever brings them back meets the lock screen, however they do it.
- Turn off displays parks the pointer in a corner of a display that went off, and puts it back afterwards unless the mouse woke them.
- **Turn off displays**: blacks every display out, as if switched off, while the computer keeps running and stays awake. Ctrl+Alt+L, the quick panel tile, the Keep awake section, or `dispctrl awake displays-off --enabled on`; any input brings them back. Choose how dark, which displays (all, all but the main one, all but the one with the pointer or the active window, or only the main one), the delay, and whether only the pointer wakes a display.
- **By resolution** on the arrangement: draws each display by its pixel count, as Windows does, instead of by its real size.

### Changed
- The Devices page offers to **contribute** a monitor rather than share it, and the command is `dispctrl devices contribute`; `devices share` still works.
- New installs start with the author's own setup: unison and Replace Windows brightness on, focus mode following the mouse with each display keeping its own window, OLED care's two-stage rest, taskbar glass and reveal timing, the quick panel's layout, and no logging.
- Only one DispCtrl window runs at a time: opening it again brings the running one forward, restored if it was minimized.
- The engine starts whenever DispCtrl is opened, and a Store install starts it at sign-in by default.
- The quick panel opens in simple mode on a new install; the dot in its title bar switches to the full panel.

### Fixed
- Focus mode kept a vanished notification (Teams, for one) clear in the dim until the next click.
- Switching displays off from the quick panel left the tile's tooltip lit on top of the black.
- A hidden taskbar on a monitor with another stacked against its edge appeared and disappeared instantly; it slides again, clipped to its own screen.
- A resting or turned-off display no longer keeps the engine checking for you ten times a second: it waits for the keyboard or mouse instead.
- With Stay active on, turned-off displays came back on by themselves after a minute: its pointer nudge counted as someone returning.
- Shared monitor records were never turned into pull requests: the intake on GitHub tried to run a Windows program on Linux.
- The arrangement preview showed no wallpaper when the file Windows reports was gone, online-only, or in a format the preview cannot read; it now falls back to Windows' own copy.
- A hidden taskbar on a monitor placed above another showed along the top of the lower screen.
- The Store version's tray icon was never kept on the taskbar, and the switch to keep it there was greyed out.
- When Windows Explorer still has an earlier build's taskbar glass loaded, the Taskbar page offers to restart Explorer instead of only saying so.

## [0.1.0] - 2026-09-24

The first public release.

### Added
- Repair and maintenance: **Settings > Updates and maintenance** and `dispctrl maintenance repair|clear-cache` put back the sign-in task and Start menu shortcut and clear logs and cached monitor data. The installer offers the same, plus a settings reset that keeps a backup.
- Help > Report a problem and `dispctrl report`: a scrubbed diagnostic report, reviewed before a prefilled issue opens.
- Devices learn each new monitor by themselves; remove one from the list, share one or all.
- Hotkeys show whether each shortcut is working or taken by another program, warn about combinations Windows keeps, and list Windows' own display shortcuts.
- Sponsor entry in the navigation, and contact details on the About page.
- **Stay active**: keeps the screen on and stops the lock screen and chat apps showing you as Away, with a one-pixel pointer nudge after a minute idle. A quick panel tile, a switch under Keep awake, a hotkey action and `dispctrl awake set --stay-active on`.
- The tray icon is drawn bolder while Stay active or Keep awake is on, and can use the Windows accent colour.
- The device library can say what a model's panel is, so built-in laptop panels, which have no DDC/CI, can be described too. A panel the library knows as OLED gets burn-in protection without being marked by hand. `dispctrl devices panel --monitor 1 --technology OLED` sets it; sharing carries it.
- Unison brightness across every display, each inside a calibrated range, with
  the option to replace Windows brightness: the Quick Settings slider and the
  laptop's brightness keys drive every display.
- Hardware brightness over DDC/CI and WMI, and software dimming below the
  hardware minimum.
- Night light per display or in unison, scheduled or following Windows, with an
  optional one-off lift of Windows' gamma limit for warmer and dimmer settings.
- Per-monitor taskbar hiding, per-monitor wallpaper, and a display arrangement
  drawn at physical size.
- Every DDC/CI control a monitor offers, and a device library that learns
  manufacturer-specific codes and shares them as a reviewed GitHub issue.
- Focus mode, OLED care with two-stage idle dimming, keep-awake and dark mode.
- A quick panel on the tray icon, with a simple mode, a lock, and sections that
  can be reordered, folded and hidden.
- Global hotkeys registered by the engine, with a default set.
- `dispctrl.exe`, a console CLI with JSON output covering every option in the app.
- A per-user installer, portable and CLI zips, and an MSIX for the Microsoft
  Store.

[Unreleased]: https://github.com/jesvijonathan/DispCtrl/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/jesvijonathan/DispCtrl/compare/v0.1.5...v0.2.1
[0.1.5]: https://github.com/jesvijonathan/DispCtrl/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/jesvijonathan/DispCtrl/compare/v0.1.2...v0.1.4
[0.1.2]: https://github.com/jesvijonathan/DispCtrl/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/jesvijonathan/DispCtrl/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/jesvijonathan/DispCtrl/releases/tag/v0.1.0
