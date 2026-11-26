# Changelog

All notable changes to protondrive-client are listed here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.2.6] - Unreleased

### Added

- **`move` command** (also `mv`) to move or rename files and folders on Proton Drive, like `mv`:
  - several sources go into an existing folder and keep their names;
  - a single source can be given a new path or a new name;
  - nothing is ever overwritten: every path and target name is checked first (items in the trash count), and if
    anything is wrong, nothing is moved;
  - for items in synced folders, it says what the next sync will do with the local copy.
- **Colour-coded sync output.** On a terminal, `sync` and `sync --dry-run` color each line by what it means, so
  conflicts stand out:
  - real conflicts in bold yellow, starting with `CONFLICT`;
  - warnings and held deletions in yellow;
  - moves to a trash in red, and failures in bold red;
  - uploads and new remote folders in green;
  - downloads, new local folders and moves in cyan;
  - bookkeeping in dim text.

  The plan summary colors its counts the same way. After a sync, the summary line says how many conflicts there were.
  Colors are off when the output is not a terminal, when `NO_COLOR` is set, or with `TERM=dumb`.

### Changed

- **Quieter commands.** The beta and "provided as is" notice is now shown only in the help text
  (`protondrive help`). `login`, `sync add`, `version` and `sync --watch` no longer print it.
- **Social preview image:** the beta badge and the word "Unofficial" are gone; "Not affiliated with Proton AG"
  stays.
- **Clearer dry runs for files on both sides.** A dry run no longer lists every file present on both sides as
  "changed on both sides". It now tells them apart without reading file contents:
  - a different size is reported as a certain conflict, with both sizes;
  - a matching size is reported as "compared by checksum when syncing";
  - with no checksum on the server, a matching size and modification time is reported as identical.

  This matters most on a first sync of a folder that already holds a copy of the files.

## [0.2.5] - 2026-11-26

### Added

- **Live status line.** Every command shows what it is doing during long steps:
  - the step, with a counter, the elapsed time and the item being worked on, e.g.
    `⠹ Listing the remote folder on Proton Drive: 48,210 items (3:12) · Photos/2019`;
  - steps covered: sync (checking for changes, listing the remote folder, scanning the local folder, comparing), and
    `download`, `upload`, `delete` and `ls`;
  - it appears only for steps that take longer than a moment, and gives way to the transfer display;
  - `--no-progress` or `-q` turns it off.
- **Progress lines in logs.** When the output is not a terminal (scripts, the systemd journal), a step that runs longer
  than 30 seconds prints a line every 30 seconds, e.g. `Still listing the remote folder on Proton Drive: …`.

### Changed

- **Faster full listing of a remote folder tree**, used by a first sync and by `download`:
  - the tree is listed one folder level at a time;
  - the contents of up to 8 folders are fetched at once;
  - the details of a whole level are fetched together, up to 50 items per request instead of one request per
    folder.

  On a test tree, the number of detail requests dropped from 42 to 6.
- **Faster folder creation** in `upload` and when a sync creates remote folders: folders are created one level at a
  time, with up to 8 created at once, about four times faster in testing.

[0.2.6]: https://github.com/NSky/protondrive-client/compare/v0.2.5...HEAD
[0.2.5]: https://github.com/NSky/protondrive-client/compare/v0.2.4...v0.2.5
