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
| Upload / sync | planned |

## Prerequisites

- .NET SDK 10
- The [Proton Drive SDK](https://github.com/ProtonDriveApps/sdk) checked out next to this repository:

```
Proton/
├── sdk/                        # https://github.com/ProtonDriveApps/sdk
└── nsky-protondrive-client/    # this repository
```

The SDK location can be overridden with `-p:ProtonSdkRoot=/path/to/sdk/`.

### `Proton.Cryptography` package

The SDK depends on the `Proton.Cryptography` NuGet package, which is not published on nuget.org.
It is built from the fork [NSky/proton-dotnet-crypto](https://github.com/NSky/proton-dotnet-crypto) by a
GitHub Actions workflow (linux-x64, linux-arm64, win-x64, osx-arm64, osx-x64) and published to
`https://nuget.pkg.github.com/NSky/index.json`. The version used is `ProtonCryptographyPackageVersion` in
`Directory.Build.props`.

GitHub Packages requires authentication even for public packages. Create a classic personal access token
with the `read:packages` scope and export it before restoring:

```bash
export NUGET_GITHUB_USER=<your GitHub username>
export NUGET_GITHUB_TOKEN=<token>
```

`Directory.Build.rsp` makes every build in this repository use the Rust crypto backend and this
repository's `nuget.config`, including for the referenced SDK projects. Each setting is passed both as `-p`
and as `-restoreProperty`, because the restore that `dotnet build` runs ignores `-p` properties.

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

Remote paths are relative to My files: `Docs/2024` and `/Docs/2024` are the same.

`download`:

- A **file** is saved into `<local-folder>` (created if needed). An existing local file is never overwritten.
- A **folder's contents** are mirrored into `<local-folder>`, which must be empty or not exist yet.
- Progress is shown by default: a live status line on a terminal, or one line per file when stderr is
  redirected. `--no-progress` (or `-q`) prints only errors and the summary.
- Files are written as `*.protondrive-part` and renamed when complete, so an interrupted download never
  leaves a truncated file. Modification times are preserved. Up to 4 files download in parallel, and a
  file that fails in a resumable way is retried up to 3 times.
- Proton Docs and Sheets are skipped: they have no file content to download.
- Names that are not valid on the local system get `_` in place of the invalid characters.
- Exit code 1 if any file failed, 130 if cancelled with Ctrl+C.

## Where data is stored

The session (tokens and the key passphrases derived from your password) is stored in
`~/.config/protondrive/session.json` with `0600` permissions. Your password itself is never stored.
Moving these secrets to the OS secret store is planned.

## Application identification

Requests carry `x-pm-appversion: external-drive-nsky_protondrive@<version>-alpha`, as required by
Proton's [third-party usage guidelines](https://github.com/ProtonDriveApps/sdk#usage-guidelines-for-personal-projects).
