#!/usr/bin/env bash
# Builds the snap into artifacts/snap/:
#
#   build/packaging/linux/snap/pack.sh [snapcraft pack options]
#   build/packaging/linux/snap/pack.sh --use-lxd
#   build/packaging/linux/snap/pack.sh --platform arm64     (on an arm64 machine or builder)
#   build/packaging/linux/snap/pack.sh --docker              in Canonical's snapcraft image
#
# snapcraft gets a copy of the tree rather than the checkout itself: the files
# git tracks plus new ones it does not ignore, which leaves out bin/ and obj/
# (a Windows obj/ restored for other paths breaks the Linux restore) and
# keeps snapcraft's parts/, stage/ and prime/ out of the repository.
#
# The build runs in LXD, Multipass or a Docker container, never on this
# machine (--destructive-mode installs build packages onto it; inside a
# throwaway container it is the usual way). For LXD:
#   sudo snap install lxd && sudo lxd init --auto && sudo usermod -aG lxd "$USER"
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
out="$repo/artifacts/snap"
work="${DISPCTRL_SNAP_WORK:-${XDG_CACHE_HOME:-$HOME/.cache}/dispctrl-linux-snap}/project"

rm -rf "$work"
mkdir -p "$work/snap" "$out"
# A tracked file deleted or renamed in the working tree is skipped, not an error.
cd "$repo"
git ls-files -z --cached --others --exclude-standard \
  | while IFS= read -r -d '' f; do [[ -f $f ]] && printf '%s\0' "$f"; done \
  | xargs -0 cp --parents -t "$work"
chmod 0755 "$work/build/packaging/linux/snap/launch"

cd "$work"
if [[ ${1:-} == --docker ]]; then
  shift
  # The image's snapcraft, destructive inside the container only. Its entry
  # point is snapcraft itself, so the arguments go to it directly. The files
  # it leaves are root's, and are handed back with a second, tiny container.
  docker run --rm -v "$work":/project -w /project "${DISPCTRL_SNAPCRAFT_IMAGE:-ghcr.io/canonical/snapcraft:8_core24}" \
    pack --destructive-mode "$@"
  docker run --rm -v "$work":/project alpine:3.20 chown -R "$(id -u):$(id -g)" /project
else
  snapcraft pack "$@"
fi
# The image exits 0 even when snapcraft fails, so the snap itself is the test.
compgen -G "*.snap" >/dev/null || { echo "snapcraft made no .snap" >&2; exit 1; }
mv -f -- *.snap "$out/"
ls -lh "$out"
