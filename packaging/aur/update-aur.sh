#!/bin/sh
# Updates the AUR package nsky-protondrive-client-bin to a release whose archives are in <dist-dir>.
# Used by .github/workflows/release.yml (in an Arch Linux container); can also be run by hand on Arch.
#
#   update-aur.sh <version> <dist-dir>            prepare and test the update, show it, do not push
#   update-aur.sh <version> <dist-dir> --push     also push it to the AUR
#
# Steps: clone the AUR repo, set pkgver (pkgrel back to 1 for a new version), compute the checksums from the
# archives in <dist-dir> (no download), regenerate .SRCINFO, build and run the package on x86_64, commit, push.
#
# Environment: AUR_PACKAGE (default nsky-protondrive-client-bin), AUR_REPO (default the package's AUR git URL;
# a local path works for testing), GIT_AUTHOR_NAME/GIT_AUTHOR_EMAIL for the commit.
# Needs: git, makepkg, updpkgsums (pacman-contrib); ssh access to the AUR for --push.

set -eu

die() { echo "update-aur.sh: $*" >&2; exit 1; }

[ $# -ge 2 ] || die "usage: update-aur.sh <version> <dist-dir> [--push]"
version=$1
dist=$(CDPATH= cd -- "$2" && pwd) || die "no such directory: $2"
push=${3:-}

case "$version" in
    [0-9]*.[0-9]*.[0-9]*) ;;
    *) die "not a version: $version" ;;
esac

package=${AUR_PACKAGE:-nsky-protondrive-client-bin}
repo=${AUR_REPO:-ssh://aur@aur.archlinux.org/$package.git}

work=$(mktemp -d)
git clone -q "$repo" "$work/aur"
cd "$work/aur"
[ -f PKGBUILD ] || die "$repo has no PKGBUILD"

for rid in linux-x64 linux-arm64; do
    archive=protondrive-$version-$rid.tar.gz
    [ -f "$dist/$archive" ] || die "missing $dist/$archive"
    cp "$dist/$archive" .
done

current=$(sed -n 's/^pkgver=//p' PKGBUILD)
if [ "$current" != "$version" ]; then
    sed -i -e "s/^pkgver=.*/pkgver=$version/" -e "s/^pkgrel=.*/pkgrel=1/" PKGBUILD
fi
pkgrel=$(sed -n 's/^pkgrel=//p' PKGBUILD)

# makepkg uses the archives already in the folder, so the checksums are those of the files just released.
sed -i -e "s/^sha256sums_x86_64=.*/sha256sums_x86_64=('SKIP')/" -e "s/^sha256sums_aarch64=.*/sha256sums_aarch64=('SKIP')/" PKGBUILD
updpkgsums >/dev/null 2>&1 || die "updpkgsums failed"
! grep -q "'SKIP'" PKGBUILD || die "a checksum is still SKIP"
makepkg --printsrcinfo > .SRCINFO

if [ "$(uname -m)" = x86_64 ]; then
    makepkg -f --noconfirm >/dev/null || die "the package does not build"
    "pkg/$package/usr/lib/protondrive/protondrive" version
fi

git add PKGBUILD .SRCINFO
if git diff --cached --quiet; then
    echo "The AUR package is already at $version-$pkgrel with these archives; nothing to do."
    exit 0
fi

git commit -q -m "Update to $version-$pkgrel"
git --no-pager show --stat --format='%h %s (%an)' HEAD
git --no-pager diff HEAD~1 -- PKGBUILD

if [ "$push" = --push ]; then
    git push -q origin HEAD:master
    echo "Pushed $package $version-$pkgrel to the AUR."
else
    echo "Not pushed (dry run). Prepared in $work/aur"
fi
