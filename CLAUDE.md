# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Unofficial Proton Drive command-line client (`protondrive`) with bidirectional folder sync, in C# / .NET 10, Linux only.
It is built on Proton's own Drive SDK and crypto libraries, never a reimplementation of Proton's API or encryption.

## Commands

Restoring needs `NUGET_GITHUB_USER` and `NUGET_GITHUB_TOKEN` (a classic PAT with `read:packages`): the `Proton.*`
packages come from the GitHub Packages feed `nuget.pkg.github.com/NSky`, not nuget.org (see `nuget.config`).

```bash
dotnet build                                          # solution: ProtonDrive.slnx
dotnet run --project src/ProtonDrive.Cli -- <command> # e.g. -- sync --dry-run
make test                                             # = dotnet run --project tests/ProtonDrive.Sync.Tests
dotnet run --project tests/ProtonDrive.Sync.Tests -- -class "*SyncPlannerTests"   # one class (xunit v3 runner)
dotnet run --project tests/ProtonDrive.Sync.Tests -- -method "*SomeTestName"      # one test
make                                                  # self-contained single-file release into artifacts/
make dist                                             # also the release .tar.gz
```

The test project is an xunit v3 executable, so it runs with `dotnet run` and takes xunit's own filter flags.
`sudo make install` only copies what `make` built; it never builds (no SDK or token under sudo).

## Architecture

Three projects, all namespaces under `NSkyLabs.ProtonDrive`:

- **`ProtonDrive.Core`**: login and session (`Auth/`), Drive navigation and path helpers (`Drive/`), the file lock.
  It pins the `Proton.Cryptography`, `Proton.Drive.Sdk` and `Proton.Drive.Sdk.Account` versions. Keep the two SDK
  packages at the same version.
- **`ProtonDrive.Sync`**: the sync engine. It has **no console output**: everything the user sees goes through
  `ISyncObserver`, which the CLI implements (`ConsoleSyncObserver`).
- **`ProtonDrive.Cli`**: the `protondrive` executable. `Program.cs` dispatches commands with a plain `switch`
  (no command-line library). Subfolders: `Drive/` (file commands), `Sync/`, `Service/` (systemd units, update
  restart), `Terminal/` (progress, prompts, ANSI), `Notifications/` (attention file, desktop notifications,
  systemd status).

Types are mostly `internal`, opened to the CLI and tests with `InternalsVisibleTo`.

**One sync pass** (`SyncEngine.RunPassAsync`):

1. `RemoteTree` reads the remote side: a full listing on the first pass, then only the Drive events feed since
   the last one.
2. `LocalScanner` scans local names, sizes and modification times. It reads no file content.
3. `SyncPlanner.Plan` compares both with the per-item record in `SyncStateStore` (SQLite, one database per pair),
   so it knows which side changed.
4. `SyncSafety.Check` holds back plans that look like accidents: mass local deletions, or a side that is
   suddenly empty.
5. `SyncExecutor` applies the plan. `SyncAttention` raises or clears "needs attention" notices.

`SyncWatchService` runs passes continuously for `sync --watch`. It reacts to file system events, polls the events
feed every 30 s, and runs a full scan every 15 min.

`SyncPlanner` and `SyncSafety` are pure, and most unit tests target them.

Sync invariants worth preserving (README "Sync" section has the full rules):

- Nothing is deleted permanently. Remote deletions go to the Proton Drive trash, local ones to
  `<folder>/.protondrive/trash/<date>/`.
- Downloads are written as `*.protondrive-part` and renamed when complete.
- Proton Drive is the reference copy: local accidents must never spread to it.
- An edit wins over a deletion.
- Conflicts keep both versions.
- Proton Docs and Sheets have no content blocks, so they are skipped.

**Deployment model**: installed system-wide as root, but sync runs as per-user systemd **user** units (templates
in `packaging/systemd/*.in`, written by `ServiceCommand`). Per-user data lives in `~/.config/protondrive/`
(`session.json`, or `session.cred` when encrypted with systemd-creds via `session encrypt`; `sync.json`) and
`~/.local/state/protondrive/sync/`. Several processes can share one session
(they coordinate through `session.lock`).

Exit codes the units rely on:

| Code | Meaning |
| --- | --- |
| 3 | Session ended (`RestartPreventExitStatus`) |
| 75 | Restart onto a newly installed version |
| 130 | Ctrl+C |

## Releases and docs

- The version lives only in `Directory.Build.props` (`<Version>`).
- Every user-visible change gets an entry in `CHANGELOG.md` (Keep a Changelog, with a bold one-line summary per
  entry).
- `README.md` documents behaviour in detail. Update it along with behaviour changes.
- Pushing tag `v<version>` runs `.github/workflows/release.yml`: it builds and tests linux-x64 and linux-arm64,
  publishes the GitHub release, and updates the AUR package via `packaging/aur/update-aur.sh`.
- `.github/workflows/proton-cryptography.yml` builds Proton's unchanged `ProtonDriveApps/dotnet-crypto` into the
  `Proton.Cryptography` package on the same feed. It runs weekly and publishes the next patch version when upstream
  `main` moved, and it never changes the version this app references.
- The workflow refuses a tag whose CHANGELOG section is missing or still says `Unreleased`. Replace it with the
  date before tagging.
