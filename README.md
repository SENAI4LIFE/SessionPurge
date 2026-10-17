# SessionPurge

An offline, portable Windows browser for local Claude Code and Codex history, with conservative permanent deletion. MIT licensed. Supported JSON history can be deleted together with its matching shared records. SQLite-backed homes, linked sessions, and unrecognized artifact layouts remain browse-only; this is not a complete purge utility for every current provider installation.

## Usage

Run the root-level `SessionPurge.exe` as a standard user on Windows 10 or 11 x64 with .NET Framework 4.8. No installer, downloads, or package dependencies are required. The executable is unsigned.

1. Select Claude Code or Codex on every launch, then **Scan history**. Nothing scans automatically. Homes come from `CLAUDE_CONFIG_DIR` or `CODEX_HOME`, with the corresponding user-profile directory as fallback. Production accepts only that configured home on a local NTFS volume.
2. Browse project groups; search and filter by project, status, and age. Names start hidden. Enable **Show session names / prompt excerpts** to display and search them. Full conversation bodies and tool outputs stay hidden. Changing filters or name visibility clears selection.
3. Select eligible sessions individually or with **Select visible**, then **Review deletion**. The preview lists each transcript and every shared file to update, record counts, and before/after sizes. Type `DELETE` and confirm within two minutes. At most 500 sessions can be selected.

Close possible provider hosts before scanning for deletion, including Codex, Claude, editor integrations, Node.js, and WSL. The app reports recognized running process names without inspecting command lines. This includes a Codex app being used to develop or discuss SessionPurge. Files modified within 15 minutes, or dated in the future, are also blocked. Close the hosts and rescan. Session details include home-level safety checks and specific storage blockers; a remaining unsupported-store message requires an adapter, not removal or relocation of real history.

Wide windows show the session list and details together; compact windows provide Sessions/Session details navigation. `Ctrl+F` focuses search; `F5` rescans.

## Names

Labels never identify deletion targets: UUIDs and exact paths remain independent. Claude uses the latest matching custom title, provider title, summary linked to an owned message, then a meaningful user prompt. Codex uses the last matching append in `session_index.jsonl`, a matching inline title event, then a meaningful user prompt. Malformed or unowned labels fall back to **Untitled session** without inventing information.

System/assistant messages, tool outputs, internal wrappers, commands, and flagged metadata are excluded from prompt naming. Labels are normalized, capped at 120 characters, and filtered for known credential-like patterns and deceptive controls. No filter recognizes every secret; enabling names can expose user-supplied text. Names, searches, contents, and usage are never saved.

## Supported storage and safety

- Claude: `projects/<project>/<UUID>.jsonl`, stable `2.1.x` envelopes, verified session IDs and unique message IDs with a local parent chain. Recognized messages, ordinary tools, system/progress records, matching titles, owned summaries/snapshots, and scoped queue operations are supported. Unknown fields and record types fail closed.
- Codex: dated `sessions/YYYY/MM/DD/rollout-<timestamp>-<UUID>.jsonl` and flat `archived_sessions` rollouts, stable `0.x.y` envelopes, verified first-record ownership, and recognized message/tool/reasoning/context/event records. Prereleases, linked/immutable rollouts, delegation, unknown schemas, and embedded compaction replacement history are unsupported. A version within these envelopes alone does not establish compatibility.
- Shared JSON: Claude and Codex `history.jsonl`, Codex `session_index.jsonl`, and Claude `projects/<project>/sessions-index.json` version 1. Only records with an exact selected UUID are removed. Retained JSONL bytes are unchanged. Retained project-index values keep their original JSON tokens, including large integers; array formatting may change. Metadata-supplied paths are never followed, and contradictory project-index paths or sidechain ownership are refused.
- Limits: complete UTF-8 JSONL without BOM or blank rows; 32 MiB per transcript/shared file, 1 MiB per JSON row or project-index document, 100,000 rows, and 20,000 history-tree entries. Structural previews show at most 200 records. The optional Codex naming index has an additional 8 MiB/20,000-row display limit.

SQLite databases and sidecars, external `CODEX_SQLITE_HOME`, session-related directories, and other unrecognized layouts block deletion. These are preserved, not silently ignored. Authentication, settings, plugins, skills, and source-project contents are never opened. WSL, remote/cloud-only storage, Claude Desktop/Cowork, and cloud conversation deletion are unsupported. No filesystem operations follow paths embedded in conversation contents.

The app checks current-user ownership, canonical paths, NTFS identities, single hard-link counts, file types, permissions, sizes, timestamps, SHA-256 fingerprints, and directory inventories. Ancestor directories are pinned against rename. Reparse points, junctions, hard links, offline/special files, and read-only targets are refused. All transcripts and supported shared stores, including unselected peers, are locked and revalidated before mutation. Activity and inventories are checked again before commit. No administrator privileges or alternate deletion fallback is used.

When supported shared files exist, transcript deletions and shared-record updates use one transactional NTFS operation. Failure, cancellation, or process termination before commit rolls back the selection. No application temporary file, backup, or recovery journal is created. Windows manages its own filesystem transaction journal. Microsoft discourages new reliance on transactional NTFS; support is conditional on Windows and the volume, and unavailability blocks the operation. There is no unsafe fallback. Without shared files, transcript unlinks are independent; interruption can leave a partially completed selection. Rescan before retrying.

Deletion bypasses the Recycle Bin. It is not forensic secure erasure and does not remove copies from backups, snapshots, cloud synchronization, Windows paging, crash handling, or OS-managed storage. Physical power-loss durability is untested. Activity checks cannot prove renamed/custom/hostile writers are absent or eliminate every cross-process race; close all writers. This app is not a security boundary against another process running as the same user.

Runtime has no network requests, telemetry, updates, persistent settings, logs, caches, services, startup entries, or background process. Parsed history exists only in process memory.

## Build and tests

From the project directory, using the installed Windows x64 .NET Framework compiler:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

The execution-policy override affects only that process. No downloads or restores occur. Omit `-Test` to build only. Source is in `src`, automated tests in `tests`, test binaries in `build`, and synthetic development output in `dev`. The portable application is built directly into the project root. Tests generate and clean isolated fixtures beneath `dev`; production has no synthetic-root or activity-bypass option. No actual provider history is used for development.

Executed in the development environment: **68 passed, 0 failed, 2 unavailable**. Tests cover deletion and unrelated-data preservation; malformed/unknown formats; ownership and metadata contradictions; active-session gates; peer locks, content/identity changes, and inventory races; transactional failure/cancellation/process-termination rollback and retry; hard links and junctions; naming/privacy; responsive GUI interactions; confirmation expiry; and loading/rendering the root production executable.

ACL-denial fixture setup was denied by the execution environment. Symbolic-link creation returned privilege error 1314 with both attempted flag combinations. Those two scenarios remain unverified. Foreign-owner rejection, physical power loss, other Windows versions, and real provider histories were not tested.

Independent source review identified and corrected contradictory project-index ownership, namespaced delegation checks, and false rejection of nameless Codex reasoning/tool-output records. Earlier review corrected an unselected-peer mutation gap. Regression tests cover the corrections.

Format references: [Claude session storage](https://code.claude.com/docs/en/sessions#where-transcripts-are-stored), [Claude session metadata parser](https://github.com/anthropics/claude-agent-sdk-python/blob/main/src/claude_agent_sdk/_internal/sessions.py), [Codex rollout recorder](https://github.com/openai/codex/blob/main/codex-rs/rollout/src/recorder.rs), [Codex session index](https://github.com/openai/codex/blob/main/codex-rs/rollout/src/session_index.rs), [Codex SQLite state](https://github.com/openai/codex/blob/main/codex-rs/state/src/lib.rs), and [Windows transactional NTFS limitations](https://learn.microsoft.com/en-us/windows/win32/fileio/deprecation-of-txf). Provider internals can change without a stable storage API.