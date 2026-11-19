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
- Rust toolchain (`cargo`) to build the cryptography library
- This layout (sibling directories):

```
Proton/
├── sdk/                        # https://github.com/ProtonDriveApps/sdk
├── dotnet-crypto/              # https://github.com/ProtonDriveApps/dotnet-crypto
├── local-nuget/                # local NuGet feed (created below)
└── nsky-protondrive-client/    # this repository
```

The SDK location can be overridden with `-p:ProtonSdkRoot=/path/to/sdk/`.

### Build `Proton.Cryptography`

The SDK depends on the `Proton.Cryptography` NuGet package, which is not published on nuget.org.
Build it from source for your platform and pack it into the local feed (version must match
`RustCryptoVersion` in `sdk/config/cs/Packages.props`):

```bash
cd ../dotnet-crypto/src/rust/proton-crypto-ffi
cargo build --release --target x86_64-unknown-linux-gnu
mkdir -p ../../../bin/runtimes/linux-x64/native
cp target/x86_64-unknown-linux-gnu/release/libproton_crypto.{so,a} ../../../bin/runtimes/linux-x64/native/
cd ../../..
dotnet pack -c Release -p:Version=0.40.1 -p:TreatWarningsAsErrors=false \
  src/dotnet/Proton.Cryptography.csproj --output ../local-nuget
```

`TreatWarningsAsErrors=false` is needed because .NET 10.0.1xx reports nullability warnings (CS8620)
in the upstream sources, and Release builds turn those into errors.

`Directory.Build.rsp` makes every build in this repository use the Rust crypto backend and this
repository's `nuget.config` (including for the referenced SDK projects). It also suppresses `NU1603`
(asked for 0.32.1, got 0.40.1), which is harmless: 0.40.1 is the intended version.

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
