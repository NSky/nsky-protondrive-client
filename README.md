# protondrive-client

Unofficial command-line client for Proton Drive, written in C# on top of the
[Proton Drive SDK](https://github.com/ProtonDriveApps/sdk), with bidirectional synchronization of local
folders.

> [!WARNING]
> **Alpha version.** This is unofficial, third-party software. It is not affiliated with, endorsed by or
> supported by Proton AG; "Proton" and "Proton Drive" are trademarks of Proton AG. It is provided "as is",
> without warranty of any kind, and the authors accept no responsibility or liability for any loss of data,
> damage to your account or other harm resulting from its use. The sync feature changes and deletes files on
> both sides: keep independent backups of anything important. See [Disclaimer](#disclaimer).

## What it is, and what it isn't

- **Not an rclone wrapper, and not a reverse-engineered client.** It does not re-implement Proton's API or
  encryption.
- **Built on Proton's own open-source libraries:** the [Proton Drive SDK](https://github.com/ProtonDriveApps/sdk)
  and Proton's [cryptography library](https://github.com/ProtonDriveApps/dotnet-crypto). Login, keys,
  encryption and API calls all go through Proton's code; this project adds the command line and the sync
  engine. Proton does not publish these libraries on nuget.org, so the packages are built from forks of their
  repositories (see [Proton packages](#proton-packages)).
- **Sync follows the Drive events feed.** After the first sync, it asks Drive what changed since the last pass
  instead of walking the whole remote folder tree each time. That keeps it fast, and light on the API, with
  large folders. See [Finding changes](#sync).

## Status

| Feature | State |
| --- | --- |
| Login (SRP password, TOTP 2FA, two-password accounts) | ✅ |
| Session persistence and resume (token refresh saved automatically) | ✅ |
| Logout | ✅ |
| Human verification (CAPTCHA) | ❌ not supported yet |
| List a folder (`ls`) | ✅ |
| Download a file or folder (`download`) | ✅ |
| Upload a file or folder (`upload`), never overwriting | ✅ |
| Delete files or folders (`delete`): to the trash, or permanently | ✅ |
| Bidirectional sync of local folders (`sync`), once or continuously (`--watch`) | ✅ |

## Install

protondrive is installed system-wide, so every user on the machine can use it. Each user then signs in and
chooses folders on their own: logins, settings and sync state stay in each user's home folder.

The release archives are self-contained: no .NET runtime or other packages are needed. They work on
x86-64 and ARM64 Linux distributions with glibc 2.34 or newer (Ubuntu 22.04+, Debian 12+, Fedora 35+,
RHEL/Rocky/Alma 9+, openSUSE Leap 15.6+/Tumbleweed, Arch, and others). Alpine and other musl-based distributions
are not supported.

**On Arch Linux**, from the AUR: `yay -S nsky-protondrive-client-bin` (or any AUR helper, or `makepkg -si`).

**From a release** (download `protondrive-<version>-linux-x64.tar.gz` or `-linux-arm64.tar.gz` from the
[releases page](https://github.com/NSky/protondrive-client/releases), and check it against `SHA256SUMS`):

```bash
tar -xzf protondrive-0.2.0-linux-x64.tar.gz
sudo protondrive-0.2.0-linux-x64/install.sh            # into /usr/local; --prefix /usr is also possible
```

**From source** (needs the build prerequisites below):

```bash
make                  # as your user
sudo make install     # PREFIX=/usr/local by default; DESTDIR is supported for packaging
```

What goes where (with the default prefix `/usr/local`):

| What | Where |
| --- | --- |
| The program and its two native libraries | `/usr/local/lib/protondrive/` |
| The `protondrive` command (symlink) | `/usr/local/bin/protondrive` |
| systemd user units | `/usr/local/lib/systemd/user/` |
| README and license | `/usr/local/share/doc/protondrive/`, `/usr/local/share/licenses/protondrive/` |
| Login message about sync problems | `/etc/profile.d/protondrive.sh` (`SYSCONFDIR`, `--sysconfdir`) |

To remove it: `sudo ./install.sh --uninstall` from the release folder, or `sudo make uninstall`. Users' settings and
sync state in their home folders are left in place. Users who enabled background sync should first run
`protondrive service disable`.

### Background sync for each user

Each user who wants their folders kept in sync without a terminal open runs, as themselves (not with sudo):

```bash
protondrive login
protondrive sync add ~/Proton /Backup/Laptop
protondrive service enable            # continuous sync; or: protondrive service enable --timer (every 15 min)
protondrive service status
```

`service enable` enables a systemd **user** service, so each user runs their own instance under their own
account, independently of other users. It also enables *linger* for the user (`loginctl enable-linger`), so
the service starts at boot and keeps running after the user logs out. If the system does not allow users to do that,
`service enable` prints the command an administrator can run (`sudo loginctl enable-linger <user>`).

`protondrive service disable` stops it. Logs: `journalctl --user -u protondrive-sync-watch`.

## Building from source

### Prerequisites

- .NET SDK 10

### Proton packages

The Proton packages are not published on nuget.org. They come from the GitHub Packages feed
`https://nuget.pkg.github.com/NSky/index.json`, where GitHub Actions workflows in two forks publish them:

| Package | Built from | Version setting in `Directory.Build.props` |
| --- | --- | --- |
| `Proton.Sdk`, `Proton.Drive.Sdk`, `Proton.Drive.Sdk.Account` | [NSky/proton-drive-sdk](https://github.com/NSky/proton-drive-sdk) | `ProtonDriveSdkPackageVersion` |
| `Proton.Cryptography` (linux-x64, linux-arm64, win-x64, osx-arm64, osx-x64) | [NSky/proton-dotnet-crypto](https://github.com/NSky/proton-dotnet-crypto) | `ProtonCryptographyPackageVersion` |

`nuget.config` sends every `Proton.*` package to that feed and everything else to nuget.org.

GitHub Packages requires authentication even for public packages. Create a classic personal access token
with the `read:packages` scope and export it before restoring:

```bash
export NUGET_GITHUB_USER=<your GitHub username>
export NUGET_GITHUB_TOKEN=<token>
```

### Build and run

```bash
dotnet build
dotnet run --project src/ProtonDrive.Cli -- login
dotnet run --project src/ProtonDrive.Cli -- whoami
```

The development build is `src/ProtonDrive.Cli/bin/Debug/net10.0/protondrive`.

| Make target | What it does |
| --- | --- |
| `make` | Self-contained release build for this machine's architecture into `artifacts/protondrive-<version>-<rid>/` (`RID=linux-arm64` to choose) |
| `sudo make install` / `sudo make uninstall` | Install or remove it system-wide (`PREFIX`, `DESTDIR`) |
| `make dist` | Also pack the release archive `artifacts/protondrive-<version>-<rid>.tar.gz` |
| `make test` | Run the unit tests (sync planner, session refresh) |

### Releases

Pushing a tag `v<version>` (matching `<Version>` in `Directory.Build.props`) runs
`.github/workflows/release.yml`. It builds and tests x86-64 and ARM64 archives on Ubuntu 22.04, smoke-tests the
installer, and publishes them with `SHA256SUMS` as a GitHub pre-release. For the workflow to read the `Proton.*`
packages, each of the four packages must grant this repository read access (package settings, **Manage Actions
access**).

After publishing the release, the workflow updates the AUR package
[`nsky-protondrive-client-bin`](https://aur.archlinux.org/packages/nsky-protondrive-client-bin) with
`packaging/aur/update-aur.sh`. In an Arch Linux container, the script sets the new `pkgver`, computes the
checksums from the archives it just built, regenerates `.SRCINFO`, test-builds and runs the package, and pushes.
This needs:

- the repository secret `AUR_SSH_PRIVATE_KEY`: the private half of an SSH key whose public half is added to the
  AUR account that maintains the package (AUR **My Account**, **SSH Public Key**);
- optionally, the repository variables `AUR_GIT_NAME` and `AUR_GIT_EMAIL` for the author of the AUR commit
  (by default the GitHub owner name and its noreply address).

To check an update by hand without pushing (on Arch), run
`packaging/aur/update-aur.sh <version> <folder with both archives>`.

### Projects

All namespaces start with `NSkyLabs.ProtonDrive`.

| Project | Contents |
| --- | --- |
| `src/ProtonDrive.Core` | Login and session store, Drive navigation, shared helpers |
| `src/ProtonDrive.Sync` | The sync engine: state database, local and remote scanning, planner, executor, watch mode. No console output |
| `src/ProtonDrive.Cli` | The `protondrive` command: argument parsing and console output |
| `tests/ProtonDrive.Sync.Tests` | Unit tests |

## Commands

| Command | Description |
| --- | --- |
| `login [-u <name>]` | Sign in; password, 2FA code and mailbox password are prompted |
| `whoami` | Show the signed-in account and check that the keys unlock |
| `logout` | End the session on the server and delete the local session |
| `ls [path]` | List one level of a folder in My files (default: the root) |
| `download <remote-path> <local-folder> [--no-progress]` | Download a file or a folder |
| `upload <local-path> [remote-folder] [--no-progress]` | Upload a file or a folder |
| `delete <remote-path>... [-r] [--permanent] [-y]` | Move files or folders to the Proton Drive trash, or delete them permanently |
| `sync add <local-folder> <remote-folder>` | Keep a local folder and a remote folder in sync |
| `sync remove <local-folder\|id>` | Stop syncing a folder; no files are changed |
| `sync list`, `sync status` | Show synced folders, their last sync and whether a sync is running |
| `sync [<local-folder\|id>] [--watch] [--dry-run] [--force] [--no-progress]` | Sync all folders (or one) once, or continuously |
| `service enable [--timer]`, `service disable`, `service status` | Sync in the background as your own systemd user service, also after logging out |

Remote paths are relative to My files: `Docs/2024` and `/Docs/2024` are the same.

`download`:

- A **file** is saved into `<local-folder>` (created if needed). An existing local file is never overwritten.
- A **folder's contents** are mirrored into `<local-folder>`, which must be empty or not exist yet.
- Progress is shown by default. On a terminal it is a live block with one line per file in progress and an
  overall line below (total, files done, speed, ETA). When stderr is redirected it prints one line per
  finished file instead. `--no-progress` (or `-q`) prints only errors and the summary.
- Files are written as `*.protondrive-part` and renamed when complete, so an interrupted download never
  leaves a truncated file. Modification times are preserved. Up to 4 files download in parallel, and a
  file that fails in a resumable way is retried up to 3 times.
- Proton Docs and Sheets are skipped: they have no file content to download.
- Names that are not valid on the local system get `_` in place of the invalid characters.
- Exit code 1 if any file failed, 130 if cancelled with Ctrl+C.

`upload`:

- A **file** goes into `[remote-folder]`. A **folder** is uploaded with everything in it as
  `[remote-folder]/<folder name>`. `[remote-folder]` must already exist; the default is the root of My files.
- **Nothing on the server is overwritten.** Before anything is sent, every name is checked against the server.
  If any file already exists there, or a local folder name is taken by a remote file, the conflicts are listed
  and nothing is uploaded. Trashed items count as conflicts too: empty the trash or rename them first.
- A remote folder with the same name as a local folder is reused, so new files can be added to an existing
  folder tree, as long as none of the files are already there.
- If an item appears on the server after the check, the server rejects it and it is reported as failed.
  It is still not overwritten.
- Symbolic links are skipped. Modification times are preserved. Up to 4 files upload in parallel, with the
  same progress display and retries as `download`.
- Exit code 1 if there were conflicts or any file failed, 130 if cancelled with Ctrl+C.

`delete` (also `rm`):

- Moves each file or folder (with everything in it) to the **Proton Drive trash**, where it can be restored from
  the web or apps. With `--permanent` it is deleted for good, which cannot be undone.
- **Folders need `--recursive` (or `-r`)**, as with `rm -r`: without it, a folder is refused, so a mistyped path
  cannot remove a whole folder tree.
- Every path is checked first: if any of them does not exist, or is a folder without `--recursive`, nothing is
  deleted. The root of My files cannot be deleted.
- The items are listed and you are asked to confirm. `--yes` (or `-y`) skips the question; without a terminal
  (scripts, pipes) `--yes` is required.
- If an item is inside a synced folder, the next sync moves the local copy to that folder's local trash. If it is
  a synced folder itself, syncing that folder stops until it is restored or the pair is removed. Both cases are
  pointed out before you confirm.
- Exit code 1 if any item could not be deleted or you answered no.

## Sync

```bash
protondrive sync add ~/Proton /Backup/Laptop   # creates either folder if missing
protondrive sync --dry-run                     # show what the first sync would do
protondrive sync                               # one pass over all synced folders
protondrive sync --watch                       # keep syncing until stopped
```

**How it decides.** Each synced folder has a state database recording, for every item, what both sides agreed
on after the last sync. A pass compares the local folder, the remote folder and that record, so it knows which
side changed:

- A change on one side is applied to the other: new files and folders, edits (uploaded as a new revision, so
  Proton keeps the old version in the file's history), and deletions.
- An edit wins over a deletion: a file edited on one side and deleted on the other is restored.
- A file changed on both sides is compared by content (SHA-1). If it differs, both are kept: the local copy is
  renamed to `name (conflict <host> <date time>).ext` and uploaded, and the remote version is downloaded.
  If the server has no SHA-1 for the file (some uploaders don't store one), a matching size and modification
  time (within a second) counts as the same file.
- Adding a folder that already holds a copy of the remote files (made with another tool) is safe: the first
  pass matches identical files as above without transferring them, and copies files that exist on only one side
  to the other. Files deleted on one side since that copy was made come back, so tidy up first, and check
  with `sync --dry-run`.
- A folder deleted on one side is deleted on the other only if nothing in it changed there; otherwise it is
  re-created and only the unchanged items in it are deleted.
- A file or folder moved or renamed on the server is moved locally, not downloaded again.

**Nothing is deleted permanently.** Remote deletions go to the Proton Drive trash; local ones to
`<synced folder>/.protondrive/trash/<date>/`. A local file is replaced by a download only if it is still
unchanged since the scan; if it changed meanwhile, it is left alone and synced on the next pass.

**Safety checks.** Proton Drive is the reference copy: the local folder follows it, but local accidents must not
spread to it.

- **Deletions on Proton Drive** are applied locally without confirmation, however many there are, unless they
  leave the remote folder empty (see below). The local copies go to the local trash folder.
- **Deletions in the local folder** are applied on Proton Drive unless they look like an accident: more than 10
  items and more than half of everything synced. Those deletions are then *held back* and everything else keeps
  syncing. The held items are listed in the output and the journal, and `protondrive sync status` shows how many
  are waiting. Confirm them with `protondrive sync --force <folder>` (they go to the Proton Drive trash). To get
  them back locally instead, download them again with `protondrive download`. While deletions are held, a
  one-shot `protondrive sync` exits with code 1, so cron and the timer unit report it.
- **The remote folder suddenly empty** although items were synced before (emptied by mistake, a failed scan):
  the local files are kept and the folder is paused. Nothing is uploaded either, because a new upload would make
  the remote folder look non-empty and the next pass would then delete the local files without asking. A warning
  lists the kept items, and `protondrive sync status` shows the pause. Syncing resumes by itself once you restore
  the items from the Proton Drive trash; if they were deleted on purpose, run `protondrive sync --force <folder>`
  to move the local copies to the local trash.
- **The local folder suddenly empty** although items were synced before (an unmounted disk, a failed scan):
  nothing is synced at all until `--force`.
- A missing local folder is never re-created.

**When something needs your attention.** Held deletions, a paused folder, a missing folder, an ended session,
or a sync that keeps failing (3 times in a row) are reported in several places, so you notice without reading
the log:

- **A notice file in the synced folder**: `PROTONDRIVE-SYNC-NEEDS-ATTENTION.txt`, explaining the problem, listing
  the affected items and giving the command that resolves it. It is never synced, and it is not written into a
  missing folder.
- **A message when you open a terminal** (also over SSH): `/etc/profile.d/protondrive.sh` prints one line per
  affected folder, and nothing when all is well.
- **A desktop notification**, when sync runs in the background and you are logged into a desktop session; also
  when the problem is resolved. On a headless system this is skipped.
- **The systemd status line**: `systemctl --user status protondrive-sync-watch` (and `protondrive service
  status`) shows "Needs attention: ..." or "N folder(s) in sync".
- `protondrive sync status` shows `NEEDS ATTENTION` for the folder.

All of them disappear by themselves once the problem is resolved.

**Finding changes.** Remote changes come from the Drive events feed: the first pass lists the whole remote
folder, later passes fetch only the events since the last one. Local changes are found by a scan of names,
sizes and modification times (no file content is read). With `--watch`, file system events trigger a pass a few
seconds after changes settle, the events feed is polled every 30 seconds, and a full scan runs every 15 minutes.

**Running it automatically.** `protondrive service enable` runs the sync as your own systemd user service (see
[Background sync for each user](#background-sync-for-each-user)). The installed units are:

- `protondrive-sync-watch.service`: runs `sync --watch` continuously (the default).
- `protondrive-sync.service` + `protondrive-sync.timer`: one pass every 15 minutes (`--timer`).

Two passes on the same folder never overlap (a lock per folder), so a sync started by hand or from cron while the
service runs is safe. If the Proton session ends, the watch service exits with code 3 and stays stopped; run
`protondrive login`, then `protondrive service enable` again.

**Not synced:** symbolic links, special files, names that are not valid on this system, Proton Docs and Sheets
(they have no file content), and editor lock and swap files (`.~lock.*#`, `~$*`, `.*.swp`), `.DS_Store`,
`Thumbs.db`, `desktop.ini`. A folder that cannot be read is left unchanged on both sides.

**Known limitations:**

- A local rename or move is synced as a new item plus a deletion, so its content is uploaded again. (A rename
  on the server is detected as a move.)
- Two folders on the same computer cannot be synced to overlapping remote folders.
- Names are compared case-sensitively, as on Linux.

## Where data is stored

| What | Where |
| --- | --- |
| Session: tokens and the key passphrases derived from your password (`0600`) | `~/.config/protondrive/session.json` |
| Synced folder pairs | `~/.config/protondrive/sync.json` |
| Sync state databases and locks | `~/.local/state/protondrive/sync/` (`$XDG_STATE_HOME`) |
| Downloads in progress, local trash | `<synced folder>/.protondrive/` |

Your password itself is never stored. Moving the secrets to the OS secret store is planned.

Several processes can use the session at once, for example the sync service and a command run by hand. Proton
refresh tokens can only be used once, so token refreshes are coordinated through a lock file
(`session.lock`): a process that finds the tokens already refreshed by another one uses those instead of
refreshing again.

## Application identification

Requests carry `x-pm-appversion: external-drive-nsky_protondrive@<version>-alpha`, as required by
Proton's [third-party usage guidelines](https://github.com/ProtonDriveApps/sdk#usage-guidelines-for-personal-projects).

## Disclaimer

This software is an **alpha version** under active development. Expect bugs, and expect behaviour and stored
data formats to change between versions.

- It is an **unofficial, third-party client**. It is not affiliated with, endorsed by, sponsored by or supported
  by Proton AG. "Proton" and "Proton Drive" are trademarks of Proton AG and are used here only to describe what
  the software works with. Do not contact Proton support about problems with it.
- It is provided **"as is", without warranty of any kind**, express or implied, as stated in the
  [MIT License](LICENSE). In no event shall the authors or copyright holders be liable for any claim, damages or
  other liability, including **loss or corruption of data** and problems with your Proton account, arising from
  its use.
- `sync`, `upload` and `download` create, change, move and delete files on your computer and in your Proton
  Drive. Try them on unimportant folders first, use `sync --dry-run` to preview, and **keep independent backups**.
- You are responsible for using it in line with Proton's terms of service and its
  [guidelines for third-party clients](https://github.com/ProtonDriveApps/sdk#usage-guidelines-for-personal-projects).

