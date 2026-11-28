# Security review

You give protondrive your Proton password, so you should be able to check what it does with it. This document
describes how the code handles your password, your session and your files. Each point names the code it comes from,
so you can check it yourself.

**Scope and method.** This review covers the whole repository at commit `54f13fc` (version 0.2.9, 2026-11-27).
That is the code under `src/`, the packaging scripts and systemd units under `packaging/`, and the GitHub workflows
that build the releases and the Proton packages. Proton's own libraries, the
[Drive SDK](https://github.com/ProtonDriveApps/sdk) and [dotnet-crypto](https://github.com/ProtonDriveApps/dotnet-crypto),
were only read where this app calls them, not audited. The project ran the review itself with an AI assistant
(Claude): one pass over all the code, then a separate check of each possible issue. **It is not an independent,
third-party audit.**

**Result.** No high- or medium-severity vulnerabilities were found. Three low-impact points came up, and they are
fixed in version 0.2.10; see [Hardening notes](#hardening-notes).

## Your password

- **Proton's code does the login.** protondrive calls the Proton Drive SDK's `ProtonApiSession.BeginAsync`
  (`src/ProtonDrive.Core/Auth/AuthService.cs`), which uses SRP. That is the same protocol Proton's own apps use, and
  the password itself is never sent to the server, only a proof computed from it. protondrive has no login code of
  its own and does not reimplement any encryption.
- **It never leaves memory.** The password is not written to disk, logged, or put in an environment variable. It
  cannot be given on the command line, so it never shows up in `ps` or in shell history. Only the username has an
  option (`--username`).
- **Typed without echo, then cleared.** The password and the mailbox password (for two-password accounts) are read
  key by key without echo. They are collected as characters and turned into bytes without ever becoming a .NET
  `string`. Each buffer is overwritten with zeros as soon as it has been used
  (`src/ProtonDrive.Cli/Terminal/ConsolePrompt.cs`, `ReadSecret`; `AuthService.cs`, the `finally` blocks).
  Copies the SDK makes internally are outside this app's control.
- **Piped input is the exception.** When standard input is not a terminal, the password is read as one line, which
  is an ordinary string and is not wiped. The two-factor code is read with echo, like any one-time code.
- **The login is checked before anything is saved.** After the login, protondrive unlocks your primary address key.
  A wrong mailbox password is caught there, before any session is saved (`AuthService.cs`, `VerifyKeysAsync`).

## Your session

- **What is saved.** The session holds the Proton session ID and access and refresh tokens, your username and user
  ID, and the key passphrases the SDK derives from your password (`src/ProtonDrive.Core/Auth/StoredSession.cs`).
  Your password is not saved. **The session file is still as valuable as a login:** its tokens give access to your
  account, and the key passphrases decrypt your files. README, [Where data is stored](README.md#where-data-is-stored),
  explains how to handle it.
- **Readable only by you.** The file is written to a new temporary file created with mode `0600`. Creating it fails
  if anything already exists at that path, so a planted symbolic link cannot redirect the write. The temporary file
  is then renamed over the old one, so a crash never leaves a half-written session
  (`src/ProtonDrive.Core/Auth/SessionStore.cs`, `WriteAtomically`). New configuration and state folders are created
  with mode `0700` (`src/ProtonDrive.Core/AppInfo.cs`, `PrivateDirectory`).
- **Optional encryption at rest.** `protondrive session encrypt` encrypts the session with `systemd-creds`, bound
  to this computer and your user, and to the TPM when there is one. The session goes to `systemd-creds` on standard
  input and comes back on standard output. It is never on a command line or in a temporary file. Encryption is
  switched on only after a test decryption matches (`src/ProtonDrive.Core/Auth/SessionProtector.cs`,
  `SessionStore.SetEncrypted`). This protects copies of the file, for example in backups or on another computer.
  It does not protect against programs running as you, which can ask systemd to decrypt it, just as they could read
  the plain file.
- **Logging out ends the session.** `protondrive logout` ends the session on Proton's server first, then deletes it
  locally (`AuthService.cs`, `LogoutAsync`). A stolen copy of the file stops working at that point.

## Network

- **Only Proton's servers, through Proton's code.** protondrive contains no server addresses of its own. Every
  request goes through the SDK's HTTP clients.
- **TLS is left to the SDK.** protondrive adds no certificate-validation callback and no proxy, and does not change
  the SDK's TLS policy. The SDK's default is strict validation with Proton's certificate pinning. The one handler
  protondrive adds (`src/ProtonDrive.Core/Auth/ProtonHttpHandler.cs`) only coordinates token refreshes between
  processes that share one session. It reads two token fields from refresh responses and logs neither of them.
- **No telemetry.** protondrive does not set the SDK's telemetry option, so the SDK uses `NullTelemetry`, which
  records nothing. protondrive sends nothing to anyone but Proton. It identifies itself to Proton as a third-party
  app, as [Application identification](README.md#application-identification) describes.
- **Debug output is opt-in.** `PROTONDRIVE_DEBUG_HTTP=1` prints one line per request (method, URL, status). `=2`
  also prints request headers, with `Authorization`, `x-pm-uid`, `Cookie` and any header whose name contains
  "token" replaced by `<redacted>` (`src/ProtonDrive.Core/Diagnostics/HttpTrace.cs`). Request and response bodies are never printed. Without the
  variable, nothing is printed.

## Your files

- **Remote names cannot escape the folder.** File and folder names come from Proton Drive, and someone you share a
  folder with can choose them. Sync refuses any name that is empty, `.`, `..`, or contains `/` or NUL, before it
  becomes a local path (`src/ProtonDrive.Sync/SyncPaths.cs`, `IsPortableName`, applied in `RemoteTree.cs`).
  `download` converts each name to a single path segment and then checks that the result is inside the download
  folder (`src/ProtonDrive.Core/Drive/LocalNames.cs`, `ToLocalName` and `EnsureInside`).
- **Symbolic links are never followed.** The local scanner skips symbolic links, devices and other special files
  (`src/ProtonDrive.Sync/LocalScanner.cs`), and so does `upload` (`src/ProtonDrive.Cli/Drive/UploadCommand.cs`). So a
  link in a synced folder to, say, `~/.ssh` is neither uploaded nor written through. `sync add` refuses the file
  system root and any folder that contains protondrive's own configuration or state, so your home folder cannot be
  synced as a whole (`src/ProtonDrive.Cli/Sync/SyncCommand.cs`).
- **Nothing is deleted permanently.** Remote deletions go to the Proton Drive trash. Local deletions are moves into
  `<folder>/.protondrive/trash/`. Sync downloads are written under a random name in `<folder>/.protondrive/tmp/` and
  renamed into place only when complete (`src/ProtonDrive.Sync/SyncExecutor.cs`).
- **Mass deletions are held back.** Before a sync applies changes, a plan that would delete many files, or that
  follows one side suddenly becoming empty, is held until you confirm it (`src/ProtonDrive.Sync/SyncSafety.cs`).
- **Names cannot control your terminal.** A name can contain escape sequences that would change the window
  title or rewrite earlier lines. On a terminal, all output is filtered: control characters and invisible
  text-direction characters are shown as visible escapes (`\x1B`, `\u202E`), and only this program's own color
  codes get through (`src/ProtonDrive.Cli/Terminal/TerminalOutput.cs`).
- **Downloads never write through a link.** The partial file a download is written to is always created new,
  which fails rather than follows a symbolic link. A leftover file or link at that name is removed first, which
  removes the link, not what it points to (`src/ProtonDrive.Core/IO/PartialFile.cs`).
- **Sync state is private.** The state databases are in `~/.local/state/protondrive/sync/`, a folder only you can
  open. They contain decrypted file and folder names (not file contents). Every query passes its values as
  parameters (`src/ProtonDrive.Sync/State/SyncStateStore.cs`).

## Other programs it runs

protondrive starts `systemctl`, `loginctl`, `systemd-creds` and `notify-send` (or `gdbus`), and its own binary to
read the version after an update. Each is started directly with a list of arguments, never through a shell, so no
name or path can be interpreted as a command (`src/ProtonDrive.Cli/Service/ServiceCommand.cs`,
`src/ProtonDrive.Cli/Notifications/DesktopNotifier.cs`, `src/ProtonDrive.Cli/Service/UpdateWatcher.cs`).

The systemd units are fixed templates (`packaging/systemd/*.in`), filled in once by the installer with the install
folders. No user data is ever written into a unit. `protondrive service enable` refuses to run as root or under
`sudo`: sync always runs as the user who owns the files.

## Building and releases

- **Releases are built in public.** Each release is built by `.github/workflows/release.yml` from a version tag, on
  GitHub's runners, and the archives come with `SHA256SUMS`. No workflow runs on pull requests, so code from a pull
  request never runs with the repository's secrets.
- **Proton's libraries come from this project's feed.** Proton does not publish its .NET libraries on nuget.org.
  This repository's workflows (`proton-cryptography.yml`, `proton-drive-sdk.yml`) build them from Proton's
  repositories, at Proton's commits and release tags, without changing the sources. Each package records the exact
  Proton commit it was built from, and links to the workflow run that built it. Two build settings differ from
  Proton's own build: the package source configuration, and lock files being off. README,
  [Proton packages](README.md#proton-packages), explains why. You do have to trust that build pipeline.
  You can check it: every build log is public, and you can rebuild the packages yourself from the same commits.

## Hardening notes

These came up during the review. None was judged exploitable in practice, and all three are fixed in version 0.2.10:

1. **Control characters in file names reached the terminal.** Someone you share a folder with, with edit rights,
   could rename an item so that `list`, `download` or `sync` output changed the window title or hid lines in that
   run's output, such as the list of held deletions. Now everything written to a terminal is filtered, as
   described under [Your files](#your-files). Output to pipes, files and the journal is not changed.
2. **`download` into a folder other users can write to.** `download` wrote to `<file>.protondrive-part` next to
   the destination, without checking for a symbolic link already at that name. Another local user who could write
   to the download folder could have made a download overwrite a file the link pointed to. The default
   `fs.protected_symlinks` setting already blocked this in `/tmp`. Now the partial file is always created new, in
   `download` and in sync.
3. **The level-2 HTTP trace showed block tokens.** `PROTONDRIVE_DEBUG_HTTP=2` printed the `pm-storage-token` header.
   These are short-lived tokens for single encrypted blocks: they grant no account access and decrypt nothing. Now
   any header with "token" in its name is redacted.

## Reporting a problem

If you find a security problem, please report it privately through
[GitHub's private vulnerability reporting](https://github.com/NSky/protondrive-client/security/advisories/new)
rather than in a public issue. Problems in Proton's own libraries belong with Proton (see their repositories).
