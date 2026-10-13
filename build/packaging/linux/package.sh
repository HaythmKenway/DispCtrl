#!/usr/bin/env bash
# Builds the Linux release into artifacts/linux-<version>/ (cleared first):
#
#   dispctrl-linux-<version>-linux-x64.tar.gz        self-contained, with install.sh
#   dispctrl-linux-<version>-linux-arm64.tar.gz      the same for 64-bit ARM
#   dispctrl-linux-<version>-linux-musl-x64.tar.gz   Alpine and other musl systems
#   dispctrl-linux_<version>_amd64.deb, _arm64.deb   Debian, Ubuntu and derivatives
#   dispctrl-linux-<version>-1.x86_64.rpm, .aarch64  Fedora, RHEL, openSUSE (needs rpmbuild)
#   dispctrl-linux-<version>-SHA256SUMS.txt
#
# Self-contained: no .NET on the target, and only the libraries every desktop
# already has (docs/LINUX.md lists them). DISPCTRL_LINUX_TARGETS picks fewer
# runtimes. Run through ./build.cmd linux-package.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
here="$repo/build/packaging/linux"
version="${DISPCTRL_VERSION:-$(sed -n 's:.*<DispCtrlVersion>\(.*\)</DispCtrlVersion>.*:\1:p' "$repo/build/linux/Linux.Build.props")}"
[[ -n $version ]] || { echo "No DispCtrlVersion in build/linux/Linux.Build.props" >&2; exit 1; }
targets="${DISPCTRL_LINUX_TARGETS:-linux-x64 linux-arm64 linux-musl-x64}"

out="$repo/artifacts/linux-$version"
work="$out/work"
rm -rf "$out"
mkdir -p "$work"

# The runtime this machine runs, so the published command line is tried here.
case "$(uname -m)" in x86_64) native=linux-x64 ;; aarch64) native=linux-arm64 ;; *) native="" ;; esac
[[ -e /lib/ld-musl-x86_64.so.1 && $native == linux-x64 ]] && native=linux-musl-x64

publish() { # rid -> $work/<rid>/lib
  local rid=$1 lib="$work/$1/lib"
  echo "== publish $version for $rid"
  # Both into one folder: they share one runtime, which halves the download.
  # ReadyToRun for the command line, which starts on every command.
  dotnet publish "$repo/src/DispCtrl.Linux/DispCtrl.Linux.csproj" -c Release -r "$rid" \
    --self-contained true -p:PublishReadyToRun=true -p:Version="$version" -o "$lib" -v q --nologo
  dotnet publish "$repo/src/DispCtrl.Linux.Gui/DispCtrl.Linux.Gui.csproj" -c Release -r "$rid" \
    --self-contained true -p:Version="$version" -o "$lib" -v q --nologo
  # .pdb files are for debugging. The tracing provider links liblttng-ust,
  # which few systems have and nothing here uses (the runtime loads it only
  # when present); left in, an .rpm's dependency scan would require it.
  rm -f "$lib"/*.pdb "$lib/libcoreclrtraceptprovider.so"
  # dotnet publish leaves mixed modes; packages want 0644, and 0755 on what runs.
  find "$lib" -type f -exec chmod 0644 {} +
  chmod 0755 "$lib/dispctrl-linux" "$lib/dispctrl-linux-gui"
  [[ -f "$lib/createdump" ]] && chmod 0755 "$lib/createdump"
  if [[ $rid == "$native" ]]; then
    "$lib/dispctrl-linux" version | grep -qx "dispctrl-linux $version" \
      || { echo "The published command line does not report $version" >&2; exit 1; }
  fi
}

tarball() { # rid: the same files install.sh expects next to it
  local rid=$1 name="dispctrl-linux-$version-$1"
  local tree="$work/$rid/$name"
  mkdir -p "$tree/share/applications" "$tree/share/autostart" "$tree/share/icons/hicolor/256x256/apps" "$tree/systemd" "$tree/udev"
  cp -a "$work/$rid/lib" "$tree/lib"
  cp "$here/desktop/dispctrl-linux.desktop" "$tree/share/applications/"
  cp "$here/desktop/dispctrl-linux-engine.desktop" "$here/desktop/dispctrl-linux-tray.desktop" "$tree/share/autostart/"
  cp "$repo/src/DispCtrl.Linux.Gui/Assets/dispctrl.png" "$tree/share/icons/hicolor/256x256/apps/dispctrl-linux.png"
  cp "$here/systemd/dispctrl-linux-engine.service" "$tree/systemd/"
  cp "$here/udev/90-dispctrl-backlight.rules" "$tree/udev/"
  install -m 0755 "$here/install.sh" "$tree/install.sh"
  cp "$repo/LICENSE" "$tree/"
  cp "$repo/docs/LINUX.md" "$tree/README.md"
  chmod -R go-w "$tree"
  echo "== $name.tar.gz"
  tar -C "$work/$rid" --owner=0 --group=0 -czf "$out/$name.tar.gz" "$name"
}

# The system-wide tree both the .deb and the .rpm install: /usr/lib/dispctrl-linux,
# links in /usr/bin, the user unit, the udev rule, the menu entry and its icon.
system_tree() { # rid root
  local rid=$1 root=$2
  mkdir -p "$root/usr/lib" "$root/usr/bin" "$root/usr/lib/systemd/user" "$root/usr/lib/udev/rules.d" \
           "$root/usr/share/applications" "$root/usr/share/icons/hicolor/256x256/apps" \
           "$root/usr/share/doc/dispctrl-linux" "$root/etc/xdg/autostart"
  cp -a "$work/$rid/lib" "$root/usr/lib/dispctrl-linux"
  ln -s ../lib/dispctrl-linux/dispctrl-linux "$root/usr/bin/dispctrl-linux"
  ln -s ../lib/dispctrl-linux/dispctrl-linux-gui "$root/usr/bin/dispctrl-linux-gui"
  cp "$here/systemd/dispctrl-linux-engine.service" "$root/usr/lib/systemd/user/"
  cp "$here/udev/90-dispctrl-backlight.rules" "$root/usr/lib/udev/rules.d/"
  cp "$here/desktop/dispctrl-linux.desktop" "$root/usr/share/applications/"
  # Where the user manager is systemd's this does nothing (it checks), so the
  # service stays the one engine; elsewhere it is how the engine starts.
  cp "$here/desktop/dispctrl-linux-engine.desktop" "$here/desktop/dispctrl-linux-tray.desktop" "$root/etc/xdg/autostart/"
  cp "$repo/src/DispCtrl.Linux.Gui/Assets/dispctrl.png" "$root/usr/share/icons/hicolor/256x256/apps/dispctrl-linux.png"
  cp "$repo/LICENSE" "$root/usr/share/doc/dispctrl-linux/copyright"
}

deb() { # rid arch
  local rid=$1 arch=$2 deb="$work/$1/deb"
  system_tree "$rid" "$deb"
  mkdir -p "$deb/DEBIAN"
  local size; size=$(du -sk "$deb/usr" | cut -f1)
  # Debian sorts "~" before anything, so 0.2.2~beta.1 comes before 0.2.2; a
  # hyphen would read as a Debian revision and sort after it.
  local debversion="${version//-/\~}"
  sed -e "s/@VERSION@/$debversion/" -e "s/@SIZE@/$size/" -e "s/@ARCH@/$arch/" "$here/deb/control" > "$deb/DEBIAN/control"
  printf '/etc/xdg/autostart/dispctrl-linux-engine.desktop\n/etc/xdg/autostart/dispctrl-linux-tray.desktop\n' > "$deb/DEBIAN/conffiles"
  install -m 0755 "$here/deb/postinst" "$here/deb/prerm" "$here/deb/postrm" "$deb/DEBIAN/"
  chmod -R go-w "$deb"
  find "$deb" -type d -exec chmod 0755 {} +
  echo "== dispctrl-linux_${version}_$arch.deb"
  fakeroot dpkg-deb --build -Zxz "$deb" "$out/dispctrl-linux_${version}_$arch.deb" >/dev/null
}

rpm() { # rid arch
  local rid=$1 arch=$2
  if ! command -v rpmbuild >/dev/null; then
    echo "-- no rpmbuild: skipping the $arch .rpm (sudo apt install rpm, or dnf install rpm-build)"
    return
  fi
  local root="$work/$rid/rpmroot"
  system_tree "$rid" "$root"
  bash "$here/rpm/build-rpm.sh" "$root" "$version" "$arch" "$out" "$work/$rid/rpmbuild"
}

for rid in $targets; do
  publish "$rid"
  tarball "$rid"
  case "$rid" in
    linux-x64) deb "$rid" amd64; rpm "$rid" x86_64 ;;
    linux-arm64) deb "$rid" arm64; rpm "$rid" aarch64 ;;
    # musl: the tarball only. Alpine's own packages come from its aports.
  esac
done

rm -rf "$work"
# nullglob: without rpmbuild there is no .rpm, and the pattern must vanish.
(cd "$out" && shopt -s nullglob && sha256sum -- *.tar.gz *.deb *.rpm > "dispctrl-linux-$version-SHA256SUMS.txt")
echo; ls -lh "$out"
