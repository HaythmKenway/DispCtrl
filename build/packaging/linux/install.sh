#!/bin/sh
# Installs DispCtrl for Linux from this folder for the current user - no root:
#
#   ./install.sh                  install to ~/.local and start the engine
#   ./install.sh --no-engine      install, but do not enable the engine
#   ./install.sh --backlight-rule also install the backlight udev rule (sudo);
#                                 only needed where systemd-logind cannot set
#                                 the backlight (docs/LINUX.md, "Backlight")
#   ./install.sh --uninstall      remove everything this installed
#
# Settings in ~/.config/dispctrl-linux are kept on uninstall.
# POSIX sh, not bash: Alpine and other minimal systems have no bash.
set -eu

here="$(cd "$(dirname "$0")" && pwd)"
prefix="$HOME/.local"
lib="$prefix/lib/dispctrl-linux"
bin="$prefix/bin"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
units="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart"
unit=dispctrl-linux-engine.service
rule=/etc/udev/rules.d/90-dispctrl-backlight.rules

engine=true
backlight_rule=false
uninstall=false
for arg in "$@"; do
  case "$arg" in
    --no-engine) engine=false ;;
    --backlight-rule) backlight_rule=true ;;
    --uninstall) uninstall=true ;;
    -h|--help) sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $arg (--no-engine, --backlight-rule, --uninstall)" >&2; exit 2 ;;
  esac
done

have_user_systemd() { command -v systemctl >/dev/null && systemctl --user show-environment >/dev/null 2>&1; }

stop_engine() {
  # Stopped, never killed: stopping puts every warmed or dimmed ramp back.
  if have_user_systemd; then
    systemctl --user disable --now "$unit" >/dev/null 2>&1 || true
  fi
  if [ -x "$lib/dispctrl-linux" ]; then "$lib/dispctrl-linux" engine stop >/dev/null 2>&1 || true; fi
}

if [ "$uninstall" = true ]; then
  stop_engine
  rm -f "$units/$unit" "$bin/dispctrl-linux" "$bin/dispctrl-linux-gui" \
        "$apps/dispctrl-linux.desktop" "$icons/dispctrl-linux.png" "$autostart/dispctrl-linux-engine.desktop"
  rm -rf "$lib"
  have_user_systemd && systemctl --user daemon-reload || true
  if [ -f "$rule" ]; then echo "The backlight rule $rule is root's: remove it with sudo rm $rule"; fi
  echo "DispCtrl removed. Settings are kept in ${XDG_CONFIG_HOME:-$HOME/.config}/dispctrl-linux."
  exit 0
fi

[ -x "$here/lib/dispctrl-linux" ] || { echo "Run this from the extracted release folder (lib/dispctrl-linux is missing)." >&2; exit 1; }

# The bundled .NET runtime needs the C++ runtime every desktop has, but a
# minimal Alpine or Void does not; and an arm64 download on an x86 machine
# cannot run at all. Both are said here, before anything is copied.
if ! started=$("$here/lib/dispctrl-linux" version 2>&1); then
  ids=$( (. /etc/os-release 2>/dev/null; echo " ${ID:-} ${ID_LIKE:-} ") )
  case "$ids" in
    *" alpine "*) fix="sudo apk add libstdc++ libgcc" ;;
    *" void "*) fix="sudo xbps-install libstdc++ libgcc" ;;
    *" arch "*) fix="sudo pacman -S gcc-libs" ;;
    *suse*) fix="sudo zypper install libstdc++6 libgcc_s1" ;;
    *" fedora "*|*" rhel "*|*" centos "*) fix="sudo dnf install libstdc++ libgcc" ;;
    *" debian "*|*" ubuntu "*) fix="sudo apt install libstdc++6" ;;
    *) fix="install libstdc++ and libgcc with your package manager" ;;
  esac
  echo "DispCtrl cannot start on this system:" >&2
  echo "$started" | head -3 >&2
  echo "If that names libstdc++ or libgcc_s: $fix" >&2
  echo "If it says \"exec format error\": this download is for another processor ($(uname -m) here)." >&2
  exit 1
fi

stop_engine
mkdir -p "$lib" "$bin" "$apps" "$icons" "$units"
rm -rf "$lib"; mkdir -p "$lib"
cp -a "$here/lib/." "$lib/"
ln -sf "$lib/dispctrl-linux" "$bin/dispctrl-linux"
ln -sf "$lib/dispctrl-linux-gui" "$bin/dispctrl-linux-gui"
sed "s|^Exec=dispctrl-linux-gui|Exec=$lib/dispctrl-linux-gui|" "$here/share/applications/dispctrl-linux.desktop" > "$apps/dispctrl-linux.desktop"
cp "$here/share/icons/hicolor/256x256/apps/dispctrl-linux.png" "$icons/"
sed "s|^ExecStart=.*|ExecStart=$lib/dispctrl-linux engine|" "$here/systemd/$unit" > "$units/$unit"
command -v update-desktop-database >/dev/null && update-desktop-database "$apps" >/dev/null 2>&1 || true

if [ "$backlight_rule" = true ]; then
  sudo install -m 0644 "$here/udev/90-dispctrl-backlight.rules" "$rule"
  sudo udevadm control --reload
  sudo udevadm trigger --subsystem-match=backlight
  echo "Backlight rule installed. Join the video group too: sudo usermod -aG video $USER (then log in again)."
fi

if [ "$engine" = true ]; then
  if have_user_systemd; then
    systemctl --user daemon-reload
    systemctl --user enable "$unit" >/dev/null 2>&1
    # graphical-session.target is not active on every desktop; start it now
    # either way so it runs from this session on.
    systemctl --user start "$unit"
    echo "Engine enabled and started (systemctl --user status $unit)."
  else
    # No systemd user manager (elogind, OpenRC, runit): the desktop's own
    # autostart runs it at every login, and it is started now for this one.
    mkdir -p "$autostart"
    sed "s|exec dispctrl-linux engine|exec $lib/dispctrl-linux engine|" \
      "$here/share/autostart/dispctrl-linux-engine.desktop" > "$autostart/dispctrl-linux-engine.desktop"
    if [ -n "${DISPLAY:-}" ]; then
      # Detached from this terminal, so closing it does not stop the engine.
      if command -v setsid >/dev/null; then setsid "$lib/dispctrl-linux" engine >/dev/null 2>&1 < /dev/null &
      else nohup "$lib/dispctrl-linux" engine >/dev/null 2>&1 < /dev/null & fi
      echo "Engine started, and set to start at every login (from $autostart)."
    else
      echo "Engine set to start at every login (from $autostart); log in to a desktop session to start it."
    fi
  fi
fi

case ":$PATH:" in *":$bin:"*) ;; *) echo "Add $bin to your PATH to run dispctrl-linux from a terminal." ;; esac
echo "DispCtrl installed to $lib. Run dispctrl-linux doctor to check what it can reach."
