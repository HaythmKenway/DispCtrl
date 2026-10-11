#!/usr/bin/env bash
# Installs the release tarballs on clean distributions, in containers, and
# checks each one starts, finds its libraries, reads a display and opens its
# window - the "does it work on every distro" check. Needs Docker.
#
#   build/packaging/linux/distro-check.sh <artifacts/linux-VERSION> [image ...]
#
# With no images, every one in the table below. Exit code = failures.
#
# Each container starts minimal, as a fresh install does not: install.sh must
# say what is missing before copying anything; doctor must name the packages
# with this distribution's own package manager, and those names must install;
# then the command line reads an Xvfb display through libXrandr, and the
# window runs for eight seconds without aborting. Nothing touches this
# machine's displays: the X server is inside the container.
set -uo pipefail

dir="${1:?usage: distro-check.sh <artifacts/linux-VERSION> [image ...]}"
dir="$(cd "$dir" && pwd)"
shift

# image | tarball runtime | refresh | Xvfb package | the C++ runtime a minimal image lacks
table=(
  "debian:12|linux-x64|apt-get update|apt-get install -y xvfb|true"
  "ubuntu:20.04|linux-x64|apt-get update|DEBIAN_FRONTEND=noninteractive apt-get install -y xvfb|true"
  "ubuntu:24.04|linux-x64|apt-get update|DEBIAN_FRONTEND=noninteractive apt-get install -y xvfb|true"
  "fedora:42|linux-x64|true|dnf install -y xorg-x11-server-Xvfb|true"
  "rockylinux:8|linux-x64|true|dnf install -y xorg-x11-server-Xvfb|true"
  "opensuse/tumbleweed|linux-x64|zypper --non-interactive refresh|zypper --non-interactive install xorg-x11-server-Xvfb|true"
  "archlinux|linux-x64|pacman -Sy --noconfirm|pacman -S --noconfirm xorg-server-xvfb|true"
  "alpine:3.20|linux-musl-x64|apk update|apk add xvfb|apk add libstdc++ libgcc"
  "ghcr.io/void-linux/void-glibc:latest|linux-x64|xbps-install -Syu xbps && xbps-install -y tar gzip|xbps-install -y xorg-server-xvfb|xbps-install -y libstdc++ libgcc"
)

inside=$(cat <<'EOS'
set -u
fail=0
bad() { echo "FAIL $*"; fail=$((fail + 1)); }
ok() { echo "PASS $*"; }
sh -c "$REFRESH" >/dev/null 2>&1
cd /tmp && tar xzf /dist/"$TARBALL" && cd dispctrl-linux-*/ || { echo "FAIL unpacking $TARBALL"; exit 1; }
if [ "$CXX" != true ]; then
  # Minimal image: install.sh must refuse with the fix, not half-install.
  if out=$(sh ./install.sh --no-engine 2>&1); then bad "install.sh installed without the C++ runtime"
  else echo "$out" | grep -q "libstdc++" && ok "install.sh names the missing C++ runtime" || bad "install.sh refused without saying why: $out"; fi
  sh -c "$CXX" >/dev/null 2>&1
fi
sh ./install.sh --no-engine >/tmp/install.log 2>&1 && ok "install.sh" || { bad "install.sh"; tail -5 /tmp/install.log; }
app="$HOME/.local/lib/dispctrl-linux/dispctrl-linux"
"$app" version >/dev/null 2>&1 && ok "version" || bad "version: $("$app" version 2>&1 | head -2)"
fix=$("$app" doctor --local 2>&1 | grep -A1 '^\[--\]  libraries' | tail -1 | sed 's/^ *sudo //')
if [ -n "$fix" ]; then
  # Unattended, as root: the same command with the manager's "yes" flag.
  fix=$(echo "$fix" | sed -e 's/^apt install/apt-get install -y/' -e 's/^dnf install/dnf install -y/' \
    -e 's/^zypper install/zypper --non-interactive install/' -e 's/^pacman -S/pacman -S --noconfirm/' -e 's/^xbps-install/xbps-install -y/')
  sh -c "$fix" >/tmp/libs.log 2>&1 && ok "doctor's package names install: $fix" || { bad "doctor's command failed: $fix"; tail -3 /tmp/libs.log; }
fi
"$app" doctor --local 2>&1 | grep -q '^\[ok\]  libraries' && ok "every library loads" || bad "libraries: $("$app" doctor --local 2>&1 | grep libraries)"
sh -c "$XVFB" >/tmp/xvfb.log 2>&1 || { bad "installing Xvfb"; tail -3 /tmp/xvfb.log; }
Xvfb :9 -screen 0 1920x1080x24 >/dev/null 2>&1 &
sleep 2
DISPLAY=:9 "$app" display --local 2>&1 | grep -q '1920x1080 at 0,0' && ok "reads the display through libXrandr" || bad "display: $(DISPLAY=:9 "$app" display --local 2>&1 | head -2)"
DISPLAY=:9 timeout 8 "$HOME/.local/lib/dispctrl-linux/dispctrl-linux-gui" >/tmp/gui.log 2>&1
rc=$?
# Still running when stopped: 124 from GNU timeout, 143 (SIGTERM) from BusyBox's.
{ [ $rc -eq 124 ] || [ $rc -eq 143 ]; } && ok "the window runs" || { bad "the window exited with $rc"; grep -v '^   at' /tmp/gui.log | head -4; }
sh ./install.sh --uninstall >/dev/null 2>&1 && [ ! -e "$app" ] && ok "uninstall" || bad "uninstall"
exit $fail
EOS
)

failures=0
for row in "${table[@]}"; do
  IFS='|' read -r image rid refresh xvfb cxx <<<"$row"
  if [[ $# -gt 0 ]] && ! printf '%s\n' "$@" | grep -qx "$image"; then continue; fi
  tarball=$(cd "$dir" && ls dispctrl-linux-*-"$rid".tar.gz 2>/dev/null | head -1)
  echo "=== $image ($rid)"
  if [[ -z $tarball ]]; then echo "FAIL no $rid tarball in $dir"; failures=$((failures + 1)); continue; fi
  docker run --rm -v "$dir":/dist:ro -e TARBALL="$tarball" -e REFRESH="$refresh" -e XVFB="$xvfb" -e CXX="$cxx" \
    "$image" sh -c "$inside"
  failures=$((failures + $?))
done
echo; echo "$failures failure(s)."
exit "$failures"
