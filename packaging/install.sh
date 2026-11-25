#!/bin/sh
# Installs (or removes) protondrive system-wide. Run as root, from an unpacked release archive or via
# "sudo make install":
#
#   sudo ./install.sh                     install into /usr/local
#   sudo ./install.sh --prefix /usr       install into /usr (for distribution packages)
#   sudo ./install.sh --uninstall         remove it again
#
# Options: --prefix DIR (default /usr/local), --sysconfdir DIR (default /etc), --destdir DIR (stage into DIR, for
# packaging), --uninstall.
#
# Layout (PREFIX=/usr/local):
#   /usr/local/lib/protondrive/            the program (self-contained: no .NET runtime needed)
#   /usr/local/bin/protondrive             symlink to it
#   /usr/local/lib/systemd/user/           systemd *user* units, one instance per user who enables them
#   /usr/local/share/doc/protondrive/      README, license
#   /usr/local/share/licenses/protondrive/ license
#   /etc/profile.d/protondrive.sh          shows sync problems when a user opens a terminal (prints nothing otherwise)
#
# Nothing is written to home folders: each user's login, settings and sync state stay in their own
# ~/.config/protondrive and ~/.local/state/protondrive, and are not removed by --uninstall.

set -eu

PREFIX=/usr/local
SYSCONFDIR=/etc
DESTDIR=
ACTION=install

while [ $# -gt 0 ]; do
    case "$1" in
        --prefix) PREFIX=${2:?--prefix needs a directory}; shift 2 ;;
        --prefix=*) PREFIX=${1#*=}; shift ;;
        --destdir) [ $# -ge 2 ] || { echo "install.sh: --destdir needs a directory" >&2; exit 2; }; DESTDIR=$2; shift 2 ;;
        --destdir=*) DESTDIR=${1#*=}; shift ;;
        --sysconfdir) SYSCONFDIR=${2:?--sysconfdir needs a directory}; shift 2 ;;
        --sysconfdir=*) SYSCONFDIR=${1#*=}; shift ;;
        --uninstall) ACTION=uninstall; shift ;;
        -h|--help) sed -n '2,23p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "install.sh: unknown option '$1' (see --help)" >&2; exit 2 ;;
    esac
done

case "$PREFIX" in
    /*) ;;
    *) echo "install.sh: --prefix must be an absolute path" >&2; exit 2 ;;
esac
PREFIX=${PREFIX%/}

SOURCE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
APPDIR=$PREFIX/lib/protondrive
BINDIR=$PREFIX/bin
UNITDIR=$PREFIX/lib/systemd/user
DOCDIR=$PREFIX/share/doc/protondrive
LICENSEDIR=$PREFIX/share/licenses/protondrive
PROFILE_SCRIPT=${SYSCONFDIR%/}/profile.d/protondrive.sh
UNITS="protondrive-sync-watch.service protondrive-sync.service protondrive-sync.timer"

require_writable() {
    target=$DESTDIR$PREFIX
    while [ ! -e "$target" ]; do target=$(dirname -- "$target"); done
    if [ ! -w "$target" ]; then
        echo "install.sh: no write access to $target; run it with sudo" >&2
        exit 1
    fi
}

uninstall() {
    require_writable
    rm -f "$DESTDIR$BINDIR/protondrive"
    rm -rf "$DESTDIR$APPDIR" "$DESTDIR$DOCDIR" "$DESTDIR$LICENSEDIR"
    rm -f "$DESTDIR$PROFILE_SCRIPT"
    for unit in $UNITS; do rm -f "$DESTDIR$UNITDIR/$unit"; done
    echo "Removed protondrive from $PREFIX."
    echo "Users' settings and sync state in their home folders were left in place."
    echo "Users who enabled the sync service should first run: protondrive service disable"
}

install_files() {
    if [ ! -x "$SOURCE/protondrive/protondrive" ]; then
        echo "install.sh: $SOURCE/protondrive/protondrive not found. Build it first with 'make' (as your user)." >&2
        exit 1
    fi

    require_writable

    # The program: replace the whole folder so files from an older version do not linger.
    rm -rf "$DESTDIR$APPDIR"
    install -d -m 755 "$DESTDIR$APPDIR" "$DESTDIR$BINDIR" "$DESTDIR$UNITDIR" "$DESTDIR$DOCDIR" "$DESTDIR$LICENSEDIR"
    for file in "$SOURCE"/protondrive/*; do
        case "$file" in
            */protondrive|*.so) install -m 755 "$file" "$DESTDIR$APPDIR/" ;;
            *) install -m 644 "$file" "$DESTDIR$APPDIR/" ;;
        esac
    done
    ln -sfn "$APPDIR/protondrive" "$DESTDIR$BINDIR/protondrive"

    for unit in $UNITS; do
        sed -e "s|@BINDIR@|$BINDIR|g" -e "s|@DOCDIR@|$DOCDIR|g" "$SOURCE/systemd/$unit.in" > "$DESTDIR$UNITDIR/$unit"
        chmod 644 "$DESTDIR$UNITDIR/$unit"
    done

    install -m 644 "$SOURCE/README.md" "$SOURCE/LICENSE" "$DESTDIR$DOCDIR/"
    install -m 644 "$SOURCE/LICENSE" "$DESTDIR$LICENSEDIR/"
    install -d -m 755 "$(dirname -- "$DESTDIR$PROFILE_SCRIPT")"
    install -m 644 "$SOURCE/profile.d/protondrive.sh" "$DESTDIR$PROFILE_SCRIPT"

    echo "Installed protondrive $(cat "$SOURCE/VERSION" 2>/dev/null || echo) into $PREFIX."

    if [ -z "$DESTDIR" ]; then
        case ":$PATH:" in
            *":$BINDIR:"*) ;;
            *) echo "Note: $BINDIR is not in PATH." ;;
        esac

        case "$UNITDIR" in
            /usr/lib/systemd/user|/usr/local/lib/systemd/user|/etc/systemd/user) ;;
            *) echo "Note: systemd does not look for user units in $UNITDIR; the sync service will not be available." ;;
        esac

        echo
        echo "Each user can now run, as themselves (not root):"
        echo "  protondrive login"
        echo "  protondrive sync add <local-folder> <remote-folder>"
        echo "  protondrive service enable      # keep syncing in the background, also after logging out"
    fi
}

case "$ACTION" in
    install) install_files ;;
    uninstall) uninstall ;;
esac
