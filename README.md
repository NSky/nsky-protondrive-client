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
| Drive operations / sync | planned |

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

## Where data is stored

The session (tokens and the key passphrases derived from your password) is stored in
`~/.config/protondrive/session.json` with `0600` permissions. Your password itself is never stored.
Moving these secrets to the OS secret store is planned.

## Application identification

Requests carry `x-pm-appversion: external-drive-nsky_protondrive@<version>-alpha`, as required by
Proton's [third-party usage guidelines](https://github.com/ProtonDriveApps/sdk#usage-guidelines-for-personal-projects).
