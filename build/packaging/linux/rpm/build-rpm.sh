#!/usr/bin/env bash
# Wraps a prepared system tree (/usr/lib/dispctrl-linux and friends, made by
# package.sh) into an .rpm for Fedora, RHEL and its rebuilds, and openSUSE.
#
#   build-rpm.sh <tree> <version> <x86_64|aarch64> <out dir> <scratch dir>
#
# Requirements are sonames - libX11.so.6()(64bit) rather than libX11 or
# libX11-6 - so one package installs on Fedora and openSUSE, whose package
# names differ. The automatic scan is off: it would read the bundled .NET
# runtime and claim its private libraries as the package's own provides.
set -euo pipefail

tree=$1 version=$2 arch=$3 out=$4 scratch=$5
# RPM versions may not contain "-": 0.2.2-beta.1 becomes 0.2.2~beta.1, which
# rpm sorts before 0.2.2, as the .deb does.
rpmversion="${version//-/\~}"
mkdir -p "$scratch"/{BUILD,RPMS,SPECS,SRPMS}

cat > "$scratch/SPECS/dispctrl-linux.spec" <<SPEC
Name:           dispctrl-linux
Version:        $rpmversion
Release:        1
Summary:        Per-monitor brightness, dimming, night light and arrangement
License:        MIT
URL:            https://github.com/HaythmKenway/DispCtrl
AutoReqProv:    no
Requires:       libstdc++.so.6()(64bit), libgcc_s.so.1()(64bit), libz.so.1()(64bit)
Requires:       libX11.so.6()(64bit), libXrandr.so.2()(64bit), libXi.so.6()(64bit), libXtst.so.6()(64bit), libXext.so.6()(64bit)
Requires:       libXfixes.so.3()(64bit), libXcomposite.so.1()(64bit), libcairo.so.2()(64bit)
Requires:       libpango-1.0.so.0()(64bit), libpangocairo-1.0.so.0()(64bit)
Requires:       libfontconfig.so.1()(64bit), libICE.so.6()(64bit), libSM.so.6()(64bit)
Recommends:     ddcutil

%description
DispCtrl for Linux sets each display's brightness - external monitors over
DDC/CI (through ddcutil) and a laptop's own panel through its backlight -
warms or dims any display through its gamma ramp with a night light
schedule, arranges the displays, and adds snap layouts. A command line
(dispctrl-linux), a window (dispctrl-linux-gui) and a small user engine.
Night light, dimming, arranging and snapping need an X11 session.

%install
cp -a "$tree/." %{buildroot}/

%post
# The backlight rule, and the menu, for everyone now rather than at reboot.
udevadm control --reload >/dev/null 2>&1 || :
udevadm trigger --subsystem-match=backlight >/dev/null 2>&1 || :
update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || :
systemctl --global enable dispctrl-linux-engine.service >/dev/null 2>&1 || :

%preun
if [ "\$1" -eq 0 ]; then
  systemctl --global disable dispctrl-linux-engine.service >/dev/null 2>&1 || :
fi

%files
%license /usr/share/doc/dispctrl-linux/copyright
/usr/lib/dispctrl-linux
/usr/bin/dispctrl-linux
/usr/bin/dispctrl-linux-gui
/usr/lib/systemd/user/dispctrl-linux-engine.service
/usr/lib/udev/rules.d/90-dispctrl-backlight.rules
/usr/share/applications/dispctrl-linux.desktop
/usr/share/icons/hicolor/256x256/apps/dispctrl-linux.png
%config(noreplace) /etc/xdg/autostart/dispctrl-linux-engine.desktop
%config(noreplace) /etc/xdg/autostart/dispctrl-linux-tray.desktop
SPEC

echo "== dispctrl-linux-$rpmversion-1.$arch.rpm"
rpmbuild -bb --quiet --target "$arch" \
  --define "_topdir $scratch" \
  --define "_build_id_links none" \
  --define "__strip /bin/true" \
  --define "__brp_strip /bin/true" \
  --define "__os_install_post %{nil}" \
  "$scratch/SPECS/dispctrl-linux.spec"
cp "$scratch"/RPMS/"$arch"/*.rpm "$out/"
