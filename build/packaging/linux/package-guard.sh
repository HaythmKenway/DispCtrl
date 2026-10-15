#!/bin/sh
# Refuses to install the DispCtrl package while another DispCtrl install is
# there: the snap, or a tarball install in someone's home. Two installs each
# run an engine and a tray that fight over the gamma ramps, hotkeys and
# settings, so there may be one.
#
# One file, two uses: the .deb's DEBIAN/preinst and the .rpm's pre-install scriptlet
# (build-rpm.sh pastes it in). Hence POSIX sh, no percent signs (rpm reads them as macros),
# and an argument that is "install"/"upgrade" for dpkg and 1/2 for rpm.
# DISPCTRL_GUARD_ROOT lets the checks run it against a fake root.
set -e

case "${1:-}" in
    install|upgrade|1|2|"") ;;
    *) exit 0 ;;
esac

root="${DISPCTRL_GUARD_ROOT:-}"

refuse() {
    echo "dispctrl-linux: not installing - $1" >&2
    echo "DispCtrl runs from one install only; two would fight over the gamma ramps, hotkeys and tray." >&2
    echo "Remove the other first: $2" >&2
    exit 1
}

if command -v snap >/dev/null 2>&1 && snap list dispctrl-linux >/dev/null 2>&1; then
    refuse "the snap is installed" "sudo snap remove dispctrl-linux"
fi
for dir in "$root/snap/dispctrl-linux" "$root/var/lib/snapd/snap/dispctrl-linux"; do
    if [ -e "$dir" ]; then
        refuse "the snap is installed ($dir)" "sudo snap remove dispctrl-linux"
    fi
done

# A tarball install belongs to one user, but the engine it enables runs in
# that user's session on the same display as everyone's.
for lib in "$root"/home/*/.local/lib/dispctrl-linux "$root"/root/.local/lib/dispctrl-linux; do
    if [ -d "$lib" ]; then
        refuse "a tarball install is present ($lib)" "$lib/install.sh --uninstall   (as the user it belongs to)"
    fi
done

exit 0
