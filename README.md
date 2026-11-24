# nsky-protondrive-client

Unofficial command-line client for Proton Drive, written in C# on top of the
[Proton Drive SDK](https://github.com/ProtonDriveApps/sdk). The goal is a CLI with
bidirectional synchronization; the current milestone covers authentication.

> This is a third-party application not officially supported by Proton.

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
| Sync | planned |

## Prerequisites

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

## Build and run

```bash
dotnet build
dotnet run --project src/NskyProtonDrive.Cli -- login
dotnet run --project src/NskyProtonDrive.Cli -- whoami
dotnet run --project src/NskyProtonDrive.Cli -- logout
```

The executable is `src/NskyProtonDrive.Cli/bin/Debug/net10.0/protondrive`.

## Commands

| Command | Description |
| --- | --- |
| `login [-u <name>]` | Sign in; password, 2FA code and mailbox password are prompted |
| `whoami` | Show the signed-in account and check that the keys unlock |
| `logout` | End the session on the server and delete the local session |
| `ls [path]` | List one level of a folder in My files (default: the root) |
| `download <remote-path> <local-folder> [--no-progress]` | Download a file or a folder |
| `upload <local-path> [remote-folder] [--no-progress]` | Upload a file or a folder |

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

## Where data is stored

The session (tokens and the key passphrases derived from your password) is stored in
`~/.config/protondrive/session.json` with `0600` permissions. Your password itself is never stored.
Moving these secrets to the OS secret store is planned.

## Application identification

Requests carry `x-pm-appversion: external-drive-nsky_protondrive@<version>-alpha`, as required by
Proton's [third-party usage guidelines](https://github.com/ProtonDriveApps/sdk#usage-guidelines-for-personal-projects).
