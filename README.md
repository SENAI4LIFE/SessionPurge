<p align="center">
  <img src="assets/SessionPurge.png" width="96" alt="SessionPurge icon">
</p>

# SessionPurge

Browse and permanently delete local Claude Code and Codex session history on Windows.

[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-lightgrey.svg)](#requirements)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512bd4.svg)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

SessionPurge is an offline, portable desktop app that lists the sessions stored
by Claude Code and Codex on the local machine, shows each one by its title, and
deletes the sessions you select together with the files and shared history
records that belong to them. Eligibility is decided per session: a session that
cannot be deleted safely is marked as blocked and shows the specific reason.

## Requirements

- Windows 10 or 11, x64, with .NET Framework 4.8
- A local NTFS volume for the provider home
- A standard (non-elevated) user account; SessionPurge refuses to run elevated
- For Codex homes that keep thread data in SQLite: the Codex CLI with a
  `delete` command

The executable is unsigned, so Smart App Control or another application-control
policy may block it.

## Installation

There is no installer and nothing is downloaded. Build the executable from a
checkout with the .NET Framework compiler that ships with Windows:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

This produces `SessionPurge.exe` in the project root. To also create a Desktop
shortcut to it, add `-Shortcut`:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Shortcut
```

## Usage

### Graphical interface

1. Choose **Claude Code** or **Codex**, then **Scan history**. Nothing is read
   before you scan. The home is `CLAUDE_CONFIG_DIR` or `CODEX_HOME`, otherwise
   `%USERPROFILE%\.claude` or `%USERPROFILE%\.codex`.
2. Search and filter by project, status and age. Each session is listed by its
   title, with the project folder alongside. Clear **Show session names /
   prompt excerpts** to hide titles from the list, the details pane and search;
   the setting is kept when you switch providers. Conversation bodies and tool
   output are never shown.
3. Select eligible sessions (at most 500) and choose **Review deletion**. The
   review lists every transcript and every file and folder deleted with it.
   For direct deletion it also lists every shared history file that will
   change, with record counts and sizes; for Codex CLI deletion it lists the
   commands that will run.
   A session that other sessions build on (subagents, forks, continued
   conversations) can only be deleted together with them.
4. Type `DELETE` and confirm within two minutes of opening the review.
5. Rescan to see the result.

`Ctrl+F` focuses search and `F5` rescans.

### Session titles

Titles are chosen in this order, falling back to *Untitled session* when none
exists:

- **Claude Code:** the latest custom title, then the provider title or a summary
  linked to the session, then the first meaningful user prompt.
- **Codex:** the title in `session_index.jsonl`, then an inline title event, then
  the subagent nickname, then the first meaningful user prompt.

Titles are capped at 120 characters and filtered for credential-like text and
control characters. No filter recognizes every secret.

## What deletion removes

- **Claude Code:** the transcript `projects/<project>/<UUID>.jsonl`, its
  `projects/<project>/<UUID>/` folder (subagent transcripts and tool results),
  the per-session `file-history/<UUID>`, `session-env/<UUID>` and
  `tasks/<UUID>` folders, and the matching records in `history.jsonl` and
  `projects/<project>/sessions-index.json`. All of this happens in one
  transactional NTFS operation; failure, cancellation or process termination
  before commit changes nothing. Without shared history or session files,
  transcripts are deleted one at a time.
- **Codex with the Codex CLI installed:** each thread is deleted with
  `codex delete --force <UUID>`, subagent threads first, which also removes
  the records in Codex's own database and index. Before each command every
  eligible rollout is checked again; after each command only that rollout and
  its empty date folders may have disappeared. A final rescan confirms that the
  remaining rollouts are byte-for-byte unchanged. Each command is separate, so
  an interruption can leave part of a selection deleted.
- **Codex without the CLI:** the rollout file and the matching records in
  `history.jsonl` and `session_index.jsonl`, in one transactional NTFS
  operation when those files exist. This is refused when Codex keeps thread data in SQLite.

Only records with a selected session ID are removed from shared history files.
Other JSONL lines keep their exact bytes; retained Claude project-index entries
keep their JSON text, although whitespace between entries may change.

## Safety

A session is blocked, with a specific reason, if it:

- is listed as running in Claude Code's `sessions/<pid>.json` with a live process
- was modified in the last 15 minutes or is dated in the future
- started subagents whose transcripts are not in its session folder
- is a Claude session with a sidechain, fork, team or agent link, or with
  messages that refer to another transcript
- is built on by another session that is itself blocked
- is split across several rollout files or shares its ID with another file
- is named by another file in the history folders

Some conditions block more than one session. In a Codex home, every session is
blocked while any rollout header cannot be read; in a Claude project, every
session is blocked while another transcript in that project cannot be read
completely. Every Claude session is blocked if the running-session list cannot
be read, and every Codex session is blocked if Codex keeps thread data in SQLite
and no Codex CLI with a `delete` command is found.

Before a direct deletion, every selected transcript, every other eligible
transcript and every shared history file is locked and checked again against
the scan: ownership, canonical path, NTFS file ID, a single hard link, no
reparse points, size, timestamp and SHA-256. Links and junctions in the history
folders block the whole home. Authentication, settings, plugins and skills are
never opened, and paths found inside history are never followed.

If a process such as `claude`, `codex`, `node` or a code editor is running, the
scan and the review show a warning. The warning does not block deletion.

SessionPurge writes no temporary files, backups, logs, settings or telemetry
and makes no network connections. Codex deletion runs the installed Codex CLI,
which is outside SessionPurge's control.

## Limitations

- Deletion bypasses the Recycle Bin and is not secure erasure. Copies in
  backups, snapshots, sync folders, paging files or crash dumps remain.
- Process checks cannot detect renamed or hostile writers. The app is not a
  security boundary against other processes running as the same user.
- Microsoft discourages new use of
  [transactional NTFS](https://learn.microsoft.com/en-us/windows/win32/fileio/deprecation-of-txf);
  if it is unavailable, deletions that need a transaction are refused.
- SessionPurge cannot read Codex's SQLite databases; their records are removed
  only by Codex's own command.
- Not supported: WSL, remote or cloud-only storage, Claude Desktop and cloud
  conversation deletion.
- Provider storage formats can change without notice.

## Development

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

`build.ps1` compiles with the Windows .NET Framework x64 compiler and treats
warnings as errors. With `-Test` it also builds `build\SessionPurge.Tests.exe`
and runs it against synthetic fixtures under `dev\`, which are removed
afterwards. Test windows stay off-screen, out of the taskbar and are never
activated; the Codex CLI is replaced by a stub. Cases the environment cannot
run, such as symbolic links without the required privilege, are reported under
`UNAVAILABLE CASES`.

The window follows the Windows high-contrast setting and uses system colors
when it is on.

Source layout:

- `src/` the application: history scanning, deletion engine and windows
- `tests/` the test runner and test cases
- `assets/` the application icon
