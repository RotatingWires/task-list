> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# TaskList v1.7.50

TaskList is a small self-hosted task manager with recursive subtasks, multiple lists, search, ZIP backup/restore, Markdown import, archives, Universal-ID deep links, milestone celebrations, a Windows 95-style interface, persistent Light/Dark themes, authentication, PWA support, and an append-only task-state event log.

It runs as an ASP.NET Core server with a plain HTML/CSS/JavaScript frontend. There is no Node.js build step or frontend framework.

- [Technology stack](#technology-stack)
- [Project layout](#project-layout)
- [Version handling](#version-handling)
- [Configuration and running](#configuration-and-running)
- [Runtime logging](#runtime-logging)
- [Authentication](#authentication)
- [First-run backup reminder](#first-run-backup-reminder)
- [Themes](#themes)
- [Data model](#data-model)
- [Milestone celebrations](#milestone-celebrations)
- [Nested subtasks](#nested-subtasks)
- [Lists and archives](#lists-and-archives)
- [Task status and actions](#task-status-and-actions)
- [Bulk actions](#bulk-actions)
- [Task Information](#task-information)
- [Search](#search)
- [Deep links](#deep-links)
- [Keyboard behavior](#keyboard-behavior)
- [Import and export](#import-and-export)
- [Fresh Start](#fresh-start)
- [HTTP API](#http-api)
- [Home screen and PWA](#home-screen-and-pwa)
- [Security and privacy](#security-and-privacy)
- [Troubleshooting](#troubleshooting)
- [Development checks](#development-checks)
- [Release notes](#release-notes)

## Technology stack

- ASP.NET Core / C# minimal API
- .NET 10
- SQLite via `Microsoft.Data.Sqlite`
- Plain HTML, CSS, and JavaScript
- Built-in ASP.NET Core cookie authentication and rate limiting
- Web manifest for adding to a phone's home screen (no offline copy, like NewsReview)
- No React, Node.js, npm, Electron, Bootstrap, Entity Framework, or ORM

The frontend is plain HTML, CSS, and JavaScript modules (`<script type="module">`) with no build step, the same in all four apps. Two small files stay classic scripts and are the same word for word in every app: `theme.js`, which has to run before the page draws so it never flashes the wrong theme, and `dropdown.js`.

## Project layout
<img width="2558" height="1299" alt="image" src="https://github.com/user-attachments/assets/ca4697ac-a541-4c8b-8f1c-c60aee5d782f" />
<img width="949" height="899" alt="image" src="https://github.com/user-attachments/assets/594d1b9b-c4ff-4174-b6c3-f359433a903c" />
<img width="945" height="899" alt="image" src="https://github.com/user-attachments/assets/419abc23-4edc-49fb-b5d2-84d10de5d2a5" />
<img width="893" height="401" alt="image" src="https://github.com/user-attachments/assets/1fd3458f-1a4b-4775-abac-e2feab769203" />
<img width="2560" height="1305" alt="image" src="https://github.com/user-attachments/assets/63054a38-1d9d-4096-b846-4f9f0c479273" />


TaskList keeps a small source tree: the C# server code lives in `src/`, and frontend code in `wwwroot/` is split by responsibility rather than by release version.

```text
TaskList.csproj        .NET project, dependencies, and authoritative release version
src/
  Program.cs           startup, middleware, sign-in endpoints, and API routes
  Store.cs             SQLite schema, migrations, event-log triggers, connections, and list/task queries (like SpendTracker's and NewsReview's)
  Transfer.cs          authenticated ZIP exports/restores, validation, recovery snapshots and ID preservation
  Onboarding.cs        first-run backup reminder state and authenticated acknowledgement
  MilestoneNotifications.cs
                       pending milestone queue, counter triggers, schema migration, and consume API
  FreshStart.cs        selective Fresh start: summary, clearing chosen data, recovery-backup removal, compaction
  TaskEventBackfill.cs legacy history events for current tasks, shared by startup and Fresh start
  SingleFileLogger.cs  console/file logging with in-place 10 MiB truncation
  PasswordAuth.cs      setup token, password file, and sign-in cookie (the same file in all four apps)
appsettings.json       listen address and log level
deploy/tasklist.service
                       example systemd unit
README.md              setup, architecture, behavior, security, and release notes

data/                  runtime/private data
  tasklist.db         SQLite database
  auth.json            salted password hash
  keys/                sign-in cookie keys (machine-scope DPAPI on Windows)
  backups/             private recovery ZIPs created before full restores
  import-reminder.pending
                       first-run reminder marker, removed when acknowledged or a ZIP is restored
  siri-api.json        optional local runtime data

wwwroot/
  index.html           main application shell and dialogs
  login.html           login / first-run password setup
  version.json         frontend release version, regenerated from TaskList.csproj during build
  app.js               shared DOM/state/API/helpers
  tasks.js             task rendering, actions, edit/subtask/move and Task Information metrics
  bulk.js              persistent selection mode, bulk toolbar, confirmations and execution
  transfer.js          Import/Export dialog, downloads, ZIP restore and Markdown upload
  fresh-start.js       Fresh Start dialog: stored-data summary, choices, confirmation, and refresh
  lists.js             list selection, creation, management, archives
  search.js            keyword/date-time search and Search list filtering
  ui.js                menus, navigation, startup/first-run reminder, deep links, removal of the old offline service worker
  milestones.js        pending milestone consumption/dialog/confetti behavior
  milestones.css       responsive Light/Dark milestone celebration styling
  theme.js             persistent Light/Dark theme selection and startup theme application
  login.js             login/setup behavior
  auth.css             login/setup styling, its own small stylesheet like the other apps
  style.css            general Win95-style application/login/search layout
  theme.css            Light/Dark color tokens; the only stylesheet with color values
  task-controls.css    task-specific Move/action control styling
  sw.js                removes the offline cache earlier releases installed
  manifest.webmanifest PWA metadata
  icons/               favicon.svg, favicon.ico, and touch-180/192/512.png, the same names in every app
```

There are no release-specific JavaScript override files. New behavior belongs in the normal module or stylesheet that owns it rather than in runtime wrappers, monkey patches, or release-specific shim files.

## Version handling

`TaskList.csproj` contains the single authoritative application version in its `<Version>` property. The .NET SDK derives the assembly/file/informational versions from that value, so they are not repeated as separate manually maintained properties.

The `GenerateWebVersion` MSBuild target writes `wwwroot/version.json` from `$(Version)` before each build; the login page and About label fetch it, as in TaskList Stats and SpendTracker. It rewrites the file only when the version changes, so building does not leave the committed copy modified.

The project is framework-dependent and not tied to one operating system: `dotnet build` and `dotnet run` work on Windows, Linux, and macOS, and the build includes the SQLite native library for each of them.

Frontend asset URLs carry no release-number query strings. The server marks pages and scripts `Cache-Control: no-cache`, so the browser checks each with the server on every load and always runs the current release.

## Configuration and running

```powershell
dotnet restore
dotnet run
```

Without configuration, TaskList listens on loopback only, at `http://127.0.0.1:8711`. To choose another address, pass `--urls` (or set the standard `ASPNETCORE_URLS` environment variable). For example, to accept LAN connections:

```powershell
dotnet run --urls "http://0.0.0.0:8711"
```

When Task Scheduler launches `TaskList.exe` directly, add the same `--urls "http://0.0.0.0:8711"` argument if other devices need to connect.

The database is stored at `data\tasklist.db` and authentication settings are stored at `data\auth.json`. Releases before v1.7.22 named the database `task-list.db`; TaskList renames it on its first start (if another program, such as a running TaskList Stats on Windows, has it open, TaskList keeps the old name for that run and logs a warning). Exports made before v1.7.22 still restore.

To keep the data somewhere else, pass `--data-dir`, set `TaskList:DataDirectory` in `appsettings.json`, or set `TASKLIST_DATA_DIR`.

| Environment variable | Purpose |
| --- | --- |
| `TASKLIST_DATA_DIR` | Data folder, overridden by `--data-dir` |
| `ASPNETCORE_URLS` | Listening address, like `--urls`; default `http://127.0.0.1:8711` |

| Command-line option | Purpose |
| --- | --- |
| `--urls URL` | Listening address, for example `http://0.0.0.0:8711` for LAN access |
| `--data-dir PATH` | Data folder (database, password, sign-in keys, recovery backups) |
| `--version` | Print the version and exit |
| `backup --output FILE [--database FILE]` | Consistent SQLite copy of `data/tasklist.db` (or `--database`), checked with `PRAGMA integrity_check`; refuses to overwrite an existing file |

### Run as a Linux service

`deploy/tasklist.service` is a systemd template, not an installer, like NewsReview's: build with `dotnet publish -c Release -o /opt/tasklist`, create the service account, `/var/lib/tasklist` (the data folder) and `/opt/tasklist/logs`, adjust `User`, `WorkingDirectory`, `ExecStart`, and `ReadWritePaths` to match, then install and enable it. It listens on loopback at port 8711; change its `--urls` for LAN access, and find the first-run setup token in the journal.

`appsettings.json` holds the default listen address (`Urls`) and log level, the same keys in every app. `--urls` and `ASPNETCORE_URLS` override it.

### SQLite performance settings

TaskList configures SQLite for the small, write-heavy transactions used by task status changes and edits:

- `journal_mode=WAL` on local/fixed storage. WAL appends commits to a write-ahead log, reducing rollback-journal churn and allowing Stats/readers to coexist with TaskList writes more efficiently.
- `synchronous=NORMAL` on every TaskList database connection. In WAL mode this avoids the most aggressive filesystem synchronization on every small commit while keeping the database structurally consistent after crashes; a sudden OS/power loss can still lose the most recent committed transactions that had not reached durable storage.
- `busy_timeout=5000` on every connection so short-lived SQLite lock contention can wait briefly instead of failing immediately.
- `foreign_keys=ON` remains enabled on every connection.

TaskList deliberately does not enable WAL when the database path is a UNC path or Windows reports the backing drive as Network/Unknown, because SQLite WAL relies on local shared-memory/file-locking semantics. The selected journal mode and connection settings are written to the normal runtime log at startup.

Task status/title/description PATCH operations also use the updated task returned by the API to update the existing in-memory task tree. They no longer re-download and rebuild the entire current list after a successful PATCH. The UI still waits for the PATCH to finish before changing, so updates are not optimistic.

## Runtime logging

TaskList writes its normal ASP.NET/runtime log stream to:

```text
logs\console.log
```

The same framework messages still go to the normal console when TaskList is launched interactively. The file includes application startup/shutdown messages, HTTP request routing/status/timing messages, warnings, errors, and exceptions emitted through the normal .NET logging pipeline.

Only one log file is kept. When `logs\console.log` would exceed 10 MiB, TaskList truncates that same file and continues writing from the beginning instead of creating rotated backup files. The `logs/` directory is ignored by Git.

Sensitive first-run authentication material is intentionally excluded from file logging. In particular, the one-time TaskList setup token is still printed directly to the interactive server console and is not sent through the file logger. Passwords are not logged.

Task Scheduler can therefore launch `TaskList.exe` directly rather than using `cmd.exe` only for output redirection.

## Authentication

On first launch, TaskList prints a random setup token in the server console. The setup screen requires that token plus a new password of at least 8 characters and password confirmation.

The plaintext password is never stored. `auth.json` contains a random salt and a PBKDF2-SHA256 password hash using 600,000 iterations, and records its iteration count. Hashes created by earlier releases with 210,000 iterations still verify; after the next successful login, TaskList re-hashes the password with 600,000 iterations and replaces `auth.json` by writing a temporary file and renaming it. A wrong password never changes the file.

After login, ASP.NET Core issues an HttpOnly, SameSite=Strict authentication cookie. Login and first-run setup are rate-limited per source IP to 5 attempts per minute.

## First-run backup reminder

When TaskList creates a fresh database, it records a pending reminder outside SQLite. After password setup/login, the main application shows **Restore a Backup Before Starting** before its task controls become usable:

> If you have a TaskList ZIP backup, import it before creating tasks. Use File > Import/Export... to restore it. Restoring a ZIP replaces all current lists, tasks/subtasks, and recorded history, so anything you create first would be replaced by a later restore. If you don't have a backup, choose Start Fresh.

**Import/Export...** acknowledges the reminder and opens the existing import/export dialog. **Start Fresh** acknowledges it and continues; it does not reset or delete data. Escape follows Start Fresh. Acknowledgement is saved for the server, so the reminder stays dismissed across browsers and restarts. Existing installations do not acquire the reminder just by updating. A successful ZIP restore also acknowledges it, including restores made directly through the API. The reminder marker is outside the database and is not part of exported ZIPs.

## Themes

Use **View > Theme** to choose either **Light** or **Dark**. The choice is stored in `localStorage` and applies to both the main TaskList UI and the login/setup screen.

The dark theme keeps the same Windows 95 raised/recessed visual language rather than replacing it with a modern flat theme. Every color is a token in `wwwroot/theme.css`, defined once for Light and once for Dark with the token names TaskList Stats, NewsReview, and SpendTracker also use; `style.css`, `task-controls.css`, `milestones.css`, and the scripts contain no color values except the two browser `theme-color` values `theme.js` writes to the page metadata. `wwwroot/theme.js` applies the saved choice before the UI fully renders so the page does not briefly flash the wrong theme.

There is intentionally no automatic **System** theme mode; theme selection is strictly Light or Dark.

**View > Use Retro Font** switches the whole app, including the login page and chart labels, to W95FA, an open-source recreation of the Windows 95 MS Sans Serif font bundled in `wwwroot/fonts`, so the interface looks the same on Windows, Linux, macOS, Android, and iOS without installing fonts. It is off by default and saved per browser, separately for each app.

Help opens About directly. Its 16px text and 1.5 line spacing match TaskList Stats Help, with about.lehighradio.com and the application version at the bottom.

## Data model

Tasks and subtasks live in one recursive `items` table. The visible task ID is stored directly as `display_id`:

```text
174
174.1
174.1.1
174.1.1.1
```

`parent_display_id` points to the immediate parent in the same list. The composite primary key `(list_id, display_id)` lets separate lists each have their own `#1`, `#1.1`, and so on.

Every item also has a global Universal ID. Universal IDs increment across all lists and are never reused. Each item has its own `next_child_number`, so nesting is not limited to one subtask level.

TaskList also keeps an append-only `task_events` table for task history. SQLite triggers record Created, Completed, Cancelled, and Reopened events with the Universal ID, event timestamp, old/new status, list/display hierarchy location, title snapshot, and provenance. Starting with v1.5.10, direct task/subtask deletion and list deletion also append a Deleted event before the current item row is removed; that event records the task's status immediately before deletion and uses `Deleted` as the destination state.

Starting with v1.5.24, a list-deletion trigger preserves the list's last name in `deleted_lists`, independently of the deleted list and its tasks. TaskList Stats can read these names for historical events. Lists deleted before this version have no recoverable saved name.

The current `items` row remains the fast current-state model while `task_events` preserves repeated transitions and later deletions for historical analysis. Moving a task between lists does not create a Deleted event even though the move implementation temporarily removes/reinserts item rows.

When a pre-v1.5 database first runs under v1.5+, TaskList backfills the older Created/Completed/Cancelled/Reopened timestamps it can recover into the event log. Repeated old state cycles that were never stored cannot be reconstructed exactly. Likewise, deletions performed before v1.5.10 cannot be backfilled because older versions stored no trustworthy deletion timestamp.

TaskList expects the current recursive `items` schema. The old pre-v1.1 `tasks`/`subtasks` migration path has been removed.

The database records its schema version in `PRAGMA user_version` (currently 1), like SpendTracker and NewsReview. TaskList refuses to start with a database from a newer version, logging why and leaving the file untouched, and refuses backups from a newer schema.

## Milestone celebrations

TaskList alone owns live milestone dialogs and confetti. It keeps a persistent pending-notification queue in its SQLite database. TaskList Stats stays read-only and provides a separate permanent Milestones analysis tab. The notification rules mirror those analytical milestone families:

- global Created / Completed / Cancelled / Reopened / Deleted milestones at 1, 100, 500, 1,000, 2,000, 3,000, 5,000, and 10,000, then every 500 events indefinitely
- overall recorded-event milestones on the same global schedule
- Universal ID milestones at #1, #100, #500, #1,000, #2,000, #2,500, #3,000, #5,000, and #10,000, then every 500 IDs indefinitely
- per-list Created / Completed milestones at 100, 500, 1,000, 2,000, and 5,000, then every 500 events indefinitely
- yearly Created / Completed milestones at the first event, 100, 500, 1,000, and 2,000, then every 500 events indefinitely within that calendar year

Continuing milestones start at 10,500 for global events, recorded events, and Universal IDs; 5,500 for per-list events; and 2,500 for yearly events. Each family continues at 500-step intervals.

TaskList maintains lightweight milestone counters with SQLite triggers as events are recorded. Reaching a qualifying threshold creates one pending notification row rather than rescanning all history after every action. Counter state is rebuilt from the event log at startup, before the triggers are installed, so existing history is the baseline and only future inserts can create new notifications. Counter rebuilding preserves progress across restarts.

Global and per-list event milestones count every recorded event, including events whose date is "Unknown" or invalid. Milestones advance in event-record order, matching Stats, so importing an older date does not reorder the counters. Missing dates display as unknown rather than becoming a made-up timestamp. Yearly milestones need a valid date and use UTC years for timestamps; date-only imports use their written calendar year. Universal-ID milestones track every allocated ID. Moving a task does not move its original creation event to another list, and renaming a list preserves its ID and milestone progress. Startup rebuilds counters without replaying past milestones; already pending notices remain queued.

The TaskList frontend calls authenticated `POST /api/milestones/consume` at startup and after successful writes while its page is visible and focused, and checks again when it regains focus. A write during an in-flight check schedules another check. If focus is lost after a response arrives, received notices stay in page memory for display on return. One atomic `DELETE ... RETURNING` removes and returns all pending notices. If several milestones are reached by the same action, they are returned together and shown in one dialog with one confetti animation. Concurrent TaskList tabs cannot consume the same row twice. Pending notices survive browser/server restarts until consumed; consumed notices are not replayed on restart or subsequent task actions. Consumption happens before display, so a lost response or a browser closing at that point can prevent a popup from being seen.

Startup migrates both older global-only and scoped notification ledgers into the pending queue in the same transaction as counter rebuilding and trigger installation. Genuinely pending rows are preserved; acknowledged and historical baseline rows are discarded. The queue no longer stores `viewed_at`, `viewed_by`, or historical baseline records, and the old notification schema-version state is removed. Rebuilding counters from current history prevents upgrades from suddenly celebrating milestones that were already reached.

The celebration is dependency-free, responsive on desktop and mobile, adapts to Light and Dark themes, and suppresses confetti when the browser requests reduced motion. The permanent analytical milestone timeline in TaskList Stats is computed from event history independently of this queue and remains available after a popup is consumed.

## Nested subtasks

Every item can have subtasks:

```text
#174
├─ #174.1
│  ├─ #174.1.1
│  │  └─ #174.1.1.1
│  └─ #174.1.2
└─ #174.2
```

Deleting an item also deletes all descendants. Parent/root status controls which Open/Done/All view the entire tree appears in. Subtask tree markers and titles start 18 pixels inside the task column, with another 18 pixels for each deeper level.

## Lists and archives

Each list has its own visible task-number sequence. Lists can be created, edited, archived, restored, or deleted. **File > Manage Lists...** has **Archives...** and **Create a List...** at the bottom left; the new list opens right away.

Archived lists are hidden from the normal File list picker, search scope, and Move destinations, but their tasks remain in the database. Universal-ID deep links can still locate tasks in archived lists.

TaskList prevents archiving or deleting the only active list.

## Task status and actions

Open tasks use **Complete** as the primary action; Done and Cancelled tasks use **Reopen**. Both actions must be held for **800 milliseconds**. Mobile touch release/cancel events are guarded at the document capture phase so a quick tap cancels the hold even if the browser retargets the touch away from the button. A synthesized tap/click can never invoke either action directly. Mouse/pen and keyboard holds use the same duration. While held, the button fills from left to right using the same theme-aware blue used for desktop task-row hover feedback. Releasing, cancelling the touch, or moving away before the hold finishes cancels the action. Complete and Reopen share the same handler, progress styling, and `TASK_ACTION_HOLD_MS` constant in `wwwroot/tasks.js`. The button isolates its progress and label layers, so the label scrolls beneath the sticky column headers. There is no Edit button: double-click (or double-tap) a task row to edit it.

A task with subtasks has a small arrow before its title: select it to fold the subtasks away (the arrow points right) or show them again (it points down). Folds are saved in this browser by the task's Universal ID, so they survive a reload and a move to another list. Opening a folded subtask from search or a link unfolds the tasks above it first.


The attached More menu contains actions such as:

- Add Subtask
- Cancel
- Move...
- Delete

Destructive/structural confirmations use TaskList's own Win95-style modal instead of the browser's native `confirm()` prompt. The custom confirmation dialog is used for task/subtask deletion, list deletion, and list archiving. Cancel receives initial focus so opening a confirmation does not put the destructive action under Enter by default. Delete confirmations also state that recorded task-event history is retained for TaskList Stats even after the current task/list rows are removed.

Moving a task preserves its Universal ID, title, description, status, timestamps, child numbering, and descendant hierarchy while assigning a new visible top-level ID in the destination list.

On mouse/trackpad devices, task rows highlight on real hover so the controls at the right side of a wide row remain visually tied to the correct task. Touch/coarse-pointer devices do not use row hover highlighting, which avoids sticky mobile hover states. The desktop hover treatment has separate Light/Dark theme colors and preserves nested-subtask status/hierarchy markings.

## Bulk actions

**File > Select...**, below Import/Export..., toggles selection mode and closes the menu. A check box (✓ when on), as in SpendTracker, marks the enabled option, and the preference survives reloads. Click a row or its checkbox to select tasks and subtasks; keyboard users can focus a row and press Space or Enter. Selecting or deselecting a root task also selects or deselects every descendant, at every depth. Subtasks can still be toggled individually. The toolbar and dialog counts include all selected descendants. Selected rows keep the same Light/Dark color as row hover, including on touchscreens. Selection applies to the current list and view; switching either clears the selection.

Press **Escape** to exit selection mode and clear selected tasks and the range anchor. The disabled preference is saved. If a dialog is open, Escape dismisses that dialog first; selection mode remains active until you press Escape again. Selection cannot be changed while a bulk action is running.

Shift-click a row or its checkbox to select the inclusive range from the last normally clicked row, in the current visible order. Existing selections stay selected. Roots in the range include their descendants. Repeated Shift-clicks keep the same anchor until a normal click; switching lists/views or turning selection mode off resets it.

The menu bar shows **Selected: N** and the same split action button used on individual tasks. Its primary Complete/Reopen action uses the same 800 ms hold. The attached menu offers Complete, Reopen, Cancel, Move..., and Delete. Every bulk action opens a confirmation dialog listing the selection and effects. Move first shows the selected tasks/subtasks and asks for a destination, then confirms. Both Move dialogs use the same selection preview as the other bulk confirmations (the first 12 items, followed by the remaining count).

Complete affects selected Open items; Reopen and Cancel skip items already in the requested state. Status changes apply only to selected items. Moving or deleting includes descendants; selecting both a parent and child processes that subtree once. Moving a selected subtask without its ancestor makes it a top-level task in the destination. Delete preserves recorded history for Stats.

Bulk Move preserves the selected tasks' relative visible order in the destination. Dates are not used for ordering: root tasks display by descending task number. Each moved subtree receives the next root number in the destination, so the batch submits moves bottom-to-top to keep the original top-to-bottom order. Selected descendants travel once with their selected ancestor; independent selected subtasks become roots. All task dates and Universal IDs are preserved. Existing destination tasks appear below the moved batch.

Actions run sequentially through the existing authenticated endpoints, each with its existing server behavior and transaction boundaries. A batch is not a single database transaction. Milestone notifications are checked once after the batch finishes. Successful items leave the selection; an error stops the remaining requests and reports how many succeeded. Remaining visible tasks stay selected for review or retry. If a refresh fails after an error or structural action, selection clears and the status asks for a refresh before retrying.

## Task Information

Task Information exposes the task's stored metadata plus hierarchy/time statistics such as:

- Type
- Nesting depth
- Direct children
- Descendants
- Siblings
- Root tree size
- Current age for Open tasks
- Terminal time for Done/Cancelled tasks when a real clock time is known

Dialog buttons use shared footer spacing of 6px above and below, so Task Information, Search, Help, and other dialogs consistently place their buttons lower.

The Task Information title bar and OK button stay visible on smaller screens. Only the details inside scroll, and the dialog keeps its natural height when the contents fit.

The hierarchy metrics are calculated against the task's actual source list/tree, including when Task Information is opened from Search results. Tasks with a non-empty description show a small document indicator beside the title; selecting it opens Task Information and briefly highlights the Description row. Non-obvious metrics include hover/focus help, with tap/keyboard support on mobile.

## Search

**File > Search...** supports:

- fuzzy keyword/title/description search across active lists
- creation-date and time ranges combined in one field, matching NewsReview
- time-only searches across all dates
- overnight time windows
- task and subtask results
- filtering the current result set by list
- **View** to open the correct list, switch to All, scroll to the item, and highlight it

The Search list filter is intentionally scoped to one open Search window. It is retained when another search is run while the dialog remains open, but resets to **All lists** after Search is closed and reopened. If the previously selected list has no matches in the next result set, the filter falls back to **All lists**.

Use the single **Date/time range** field under Search > Date/Time: `10/3 - 10/8, 9am - 9pm`. The comma separates the date range from the time range. Either range alone is accepted, such as `10/3 - 10/8` or `9am - 9pm`.

Each date accepts `m/d`, `m/d/yy`, or `m/d/yyyy`; dates without a year use the current year. Times keep flexible 12-hour parsing, and times without AM/PM are treated as AM. A normal hyphen, en dash, or em dash can separate range endpoints. An overnight window such as `10pm - 2am` matches late-night and early-morning times on each selected date. Time-only searches cover all dates; time filters exclude tasks with an unknown creation time, including date-only imports. The same single field is used on desktop and mobile.

## Deep links

Canonical task links use the task's Universal ID:

```text
http://127.0.0.1:8711/task/1643
```

TaskList resolves the correct list and visible task ID, switches to **All**, scrolls to the task, and briefly highlights it. Deep links survive authentication redirects.

The old query-string deep-link format is no longer supported.

## Keyboard behavior

In task, subtask, and list description boxes:

- **Enter** saves
- **Shift+Enter** inserts a new line

## Import and export

**File > Import/Export...**, immediately after Search..., opens a dialog with **Download Backup**, **Choose File to Restore...**, and **Import Markdown...**, an explanation above each, like SpendTracker's and NewsReview's Import/Export. Import no longer has a separate menu-bar button. The dialog keeps its title and lower OK button fixed while its content scrolls on smaller screens.

Rebuild and restart TaskList after deploying this update so the new export/restore server endpoints are available.

Download Backup creates one `tasklist-backup-YYYYMMDD-HHMMSS.zip` (local time, like the other apps' downloads) containing:

| File | Contents |
| --- | --- |
| `tasklist.db` | A standalone SQLite snapshot of the complete TaskList database, including its schema and all application tables. |
| `manifest.json` | Format/version marker, TaskList version, UTC export time, counts for lists, archived lists, root tasks, subtasks, and recorded history events, the database schema version, and the database's SHA-256 checksum. |

The database includes every active and archived list; all tasks/subtasks and descriptions, hierarchy, statuses and dates; visible and Universal IDs and allocation counters; recorded Created/Completed/Cancelled/Reopened/Deleted events; saved deleted-list names; and pending milestone notices/counters. It preserves what TaskList has already recorded and cannot reconstruct missing historical data. Deleted tasks remain historical records rather than becoming live tasks.

Export uses SQLite's [online backup API](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup) so committed WAL data is included in a consistent snapshot while TaskList runs. The exported database is made standalone, so `-wal` and `-shm` sidecars are unnecessary. Exports and recovery ZIPs are unencrypted and should be stored like the database. API exports/restores require the normal authenticated cookie and export responses are not cached.

The ZIP excludes `auth.json`, the login password, authentication cookies/keys, logs, runtime files outside SQLite, application binaries/configuration, and browser preferences such as theme and selection mode. Configure a new server's login normally before restoring through its UI. Restore keeps the current server's password and local browser preferences.

### Restore a TaskList ZIP

Choose a TaskList backup ZIP with **Choose File to Restore...**, then confirm **Restore**. This replaces all current active/archived lists, tasks/subtasks and recorded history with the backup; it does not merge lists. The server validates the archive, format/application version, schema version, checksum (exports from v1.7.23 on; older exports have none and still restore), database integrity, table structure, nesting, relationships and counters before changing live records. Backups from a newer TaskList version require updating TaskList first. Import accepts up to **256 MiB compressed ZIP** and **1 GiB database**, with a bounded manifest; an upstream proxy may impose a smaller upload limit.

Before replacing data, the server creates `data/backups/before-restore-<UTC timestamp>-<unique ID>.zip` containing the previous database. These private recovery files remain until you delete them manually or with **File > Fresh Start...**. Copy one from the server and use the same Import button to restore it if needed. The server must be able to create that recovery file before restoration proceeds.

Restore copies known TaskList data tables in one SQLite transaction and keeps the running application's schema and triggers. Imported triggers/code are never installed or executed. Existing triggers are suspended inside the transaction while data is copied, then restored before commit; this avoids generating duplicate history or milestone notices. If copying or validation fails, changes and trigger changes roll back together. Existing queued milestone notices in the backup remain pending.

Restore rebuilds milestone counters from the restored history rather than trusting a backup's derived totals. It also changes a server-owned history revision, allowing updated Stats clients to replace their cached event history even when the restored database has the same final event. This revision is generated by the running server and is not copied from the backup; older backups remain supported.

Recorded IDs are preserved. The lifetime Universal-ID allocation ledger stays append-only, and list/event autoincrement counters, matching-list task numbers, and matching-task child numbers keep the higher value from the backup or current server. Restoring an older backup therefore does not let future entries reuse IDs/numbers already allocated after it. Task/history rows are replaced, while these allocation records remain monotonic.

After a successful import the browser reloads confirmed lists and tasks, clears selections, and displays the restored totals. If a restore response or refresh fails, it rechecks server data and clears stale rows if it cannot confirm them. Refresh TaskList Stats after restoring so its display reflects the restored data.

ZIP import currently restores a complete dataset. Adding an exported ZIP alongside existing data would require allocating new list/task/Universal IDs, remapping parent and history references, handling deleted-list names and duplicate imports, and recalculating milestone state. That merge mode is not implemented. Markdown import already adds tasks safely with newly allocated IDs.

### Import Markdown

Choose a `.md`, `.markdown`, or plain-text `.txt` checklist to add tasks to the current list without replacing other lists or history.

Import preserves arbitrary checklist nesting. Root groups are imported bottom-up so older roots receive lower visible IDs and Universal IDs, while descendants keep their source order.

Source reference: https://community.obsidian.md/plugins/obsidian-tasks-plugin

`➕ YYYY-MM-DD` becomes Created and `✅ YYYY-MM-DD` becomes Completed. Missing source dates are stored as `Unknown` where applicable. Imported items use `Unknown` for Last updated when the source has no update timestamp.

## Fresh Start

**File > Fresh Start...**, at the bottom of the File menu below Log Out so it is kept apart from everyday commands, clears only what you choose and then compacts the database so the space goes back to the disk. It works like NewsReview's Fresh start and uses the same dialog size, frame, title bar with a close button, footer, and **Reset Selected Data** button, with Win95 check boxes like SpendTracker's: tick the items to clear, type `RESET SELECTED`, and choose **Reset Selected Data**. Each choice shows how much it holds (tasks, lists, history events, or backup files and size), and the dialog shows the current database size. History of deleted tasks and pre-restore recovery backups are ticked to start with; nothing that removes a current task is.

| Choice | What is cleared |
| --- | --- |
| Finished tasks | Done and Cancelled tasks and subtasks in every list, but only when none of their subtasks are still Open; a finished task with an open subtask stays with its whole tree. |
| Archived lists | Every archived list and its tasks. Active lists are untouched. |
| Every list and task | All lists and tasks. One empty **Tasks** list is created so TaskList always has an active list. |
| History of deleted tasks | Recorded events for tasks that no longer exist, plus the saved names of deleted lists that no remaining event uses. History for current tasks stays. |
| All task history | Every recorded event. Each remaining task then gets one legacy event per date it still has (Created, and Completed/Cancelled/Reopened where set), the same events TaskList records at startup for tasks with no history. |
| Pre-restore recovery backups | The `before-restore-*.zip` files in `data/backups/`. |

Removed tasks get Deleted events, exactly like a normal delete, unless the same Fresh start also clears history. Universal IDs, list task numbers, and subtask numbers are never reused afterwards. Clearing history changes the server's history revision, so TaskList Stats reloads its event history, and it rebuilds milestone counters from the history that remains, as startup and restore do; milestone progress therefore restarts from that history. Rebuilt legacy events never queue milestone notices.

The database part runs in one SQLite transaction, so a failure changes nothing. Compaction (SQLite `VACUUM`, then a WAL checkpoint) runs afterwards; if it fails, the data is still cleared and the freed pages are reused for new data. The status bar reports what was removed and how much space was freed. Your password, logs, and browser preferences are kept. Fresh start cannot be undone in TaskList, so download an export first if you might want the data back. It is not a secure disk wipe.

## HTTP API

Paths below are relative to the TaskList server URL. JSON uses camelCase property names. Send JSON bodies with `Content-Type: application/json`; Markdown and ZIP imports send raw file bytes/text, not multipart form data. `{id}` and `{listId}` are numeric list IDs from list responses. `{displayId}` is the list-local visible task ID, such as `75` or `75.1.2`, **not** the Universal ID.

The server enforces the same text limits as the page, like SpendTracker: task and subtask titles 500 characters, task descriptions 4,000, list names 100, and list descriptions 1,000 (including Markdown-imported titles). Longer values return 400 with `{ error }`; edits only check the fields they change, so older long text never blocks a status change.

All `/api/` endpoints require the `TaskList.Auth` cookie except auth status, initial password setup and login. The cookie is HttpOnly and SameSite=Strict, lasts 30 days, and uses sliding expiration. Setup/login share a limit of 5 attempts per source IP per minute. API writes apply immediately: holds and confirmation dialogs are browser UI behavior rather than requirements enforced on direct API requests.

### Authentication

| Method | Endpoint | Request | Result |
| --- | --- | --- | --- |
| GET | `/api/auth/status` | None; public. | `{configured, authenticated}` reports whether a password exists and whether this request is logged in. |
| POST | `/api/auth/setup` | Public JSON: `setupToken`, `password`, `confirmPassword`. | Validates the console setup token, minimum 8-character password and confirmation; saves the password hash and sets the auth cookie. Returns `{authenticated: true}`. Available only before a password exists. |
| POST | `/api/auth/login` | Public JSON: `password`. | Checks the configured password and sets the auth cookie. Returns `{authenticated: true}`. |
| POST | `/api/auth/logout` | None; authenticated. | Clears this client's auth cookie. Returns 204 with no body. |

### Lists

List responses contain `id`, `name`, `description`, `createdAt`, `taskCount` (root tasks only), and `archived`. Names must be nonempty and unique across active and archived lists, ignoring case. Omitted/null PATCH fields keep their old values; an empty description clears it.

| Method | Endpoint | Request | Result |
| --- | --- | --- | --- |
| GET | `/api/lists` | None. | Active lists in ascending list-ID order. |
| GET | `/api/lists/archives` | None. | Archived lists in ascending list-ID order. |
| POST | `/api/lists` | JSON: required `name`, optional `description`. | Creates an active list. Returns 201, the list object and a Location header. |
| PATCH | `/api/lists/{id}` | JSON: optional `name`, `description`. | Edits a list and returns the updated list object. |
| POST | `/api/lists/{id}/archive` | None. | Archives a list while preserving its tasks/history. Returns the list object; rejects archiving the only active list. Already archived is a successful no-op. |
| POST | `/api/lists/{id}/restore` | None. | Makes an archived list active and returns its list object. Already active is a successful no-op. This restores an archived list, not a ZIP backup. |
| DELETE | `/api/lists/{id}` | None. | Deletes the list and every task/subtask, records Deleted events and saves its last name for history. Returns 204. Existing recorded history and the lifetime ID ledger remain. Rejects deleting the only active list. |

### Tasks and subtasks

Item responses contain `universalId`, `listId`, `displayId`, `parentDisplayId`, `isSubtask`, `title`, `description`, `status`, `createdAt`, `updatedAt`, `completedAt`, `cancelledAt`, `reopenedAt`, and `subtasks`. New entries start Open with server UTC creation times. Titles must be nonempty; descriptions are optional. Status values are Open, Done and Cancelled (case-insensitive on PATCH). Date-only/Unknown import values are returned as stored.

| Method | Endpoint | Request | Result |
| --- | --- | --- | --- |
| GET | `/api/lists/{listId}/tasks` | None. | All statuses in the list, including archived lists, as recursive task trees. Root tasks are newest visible ID first; child IDs are ascending. There is no paging or server-side view filter. |
| POST | `/api/lists/{listId}/tasks` | JSON: required `title`, optional `description`. | Creates a root task with new visible and Universal IDs. Returns 201, its item object and a Location header. |
| POST | `/api/lists/{listId}/items/{displayId}/subtasks` | JSON: required `title`, optional `description`. | Creates a direct child under an existing task/subtask, allocating a child number and Universal ID. Returns 201, the child object and a Location header. |
| PATCH | `/api/lists/{listId}/items/{displayId}` | JSON: optional `title`, `description`, `status`. | Edits that item, updates its last-updated time and records a history event for a status transition. Returns the updated item with an empty `subtasks` array; it does not return the full subtree. Null/omitted fields keep old values; an empty description clears it. Descendants keep their own statuses. |
| POST | `/api/lists/{listId}/items/{displayId}/move` | JSON: `targetListId`. | Moves the whole subtree to a different active list; a moved subtask becomes a root. Preserves Universal IDs, status, content, dates and hierarchy, while changing the visible root ID/prefix. Returns `{targetListId, displayId, universalId, movedCount}`. |
| DELETE | `/api/lists/{listId}/items/{displayId}` | None. | Deletes that item and all descendants, records Deleted events and preserves existing history/lifetime IDs. Returns 204. |
| POST | `/api/lists/{listId}/import/markdown` | Raw UTF-8 checklist text; `Content-Type: text/markdown; charset=utf-8`. | Adds roots/subtasks to that list using new IDs and the documented Markdown date/nesting rules. Returns `{imported, tasksImported, subtasksImported}`. Existing entries remain. |

Creation Location headers identify the created resource path; there are no standalone GET handlers for a single list or item at those paths. Read lists with the list endpoints and items with the list's tasks endpoint. Search, Task Information, view filtering, Universal-ID lookup and bulk actions use these existing data endpoints in the browser; they do not have separate TaskList API routes. Bulk actions make sequential individual requests and are not one server-side batch transaction. TaskList Stats has its own endpoints and is a separate application.

### Stats and milestone notifications

| Method | Endpoint | Request | Result |
| --- | --- | --- | --- |
| GET | `/api/stats` | None. | `{highestUniversalId}`: highest lifetime ID in the allocation ledger, including deleted entries. This is not the TaskList Stats analytics API. |
| POST | `/api/milestones/consume` | None. | Atomically removes and returns all pending milestone notices, oldest reached time first. Fields: `kind`, `threshold`, `scope`, `scopeValue`, `scopeLabel`, `reachedAt`. Repeated calls return an empty array when no notices are pending. Task/history data and milestone counters are kept. |

### Import/export and first-run reminder

| Method | Endpoint | Request | Result |
| --- | --- | --- | --- |
| GET | `/api/backup` | None. | Downloads `application/zip` with a timestamped filename in Content-Disposition. Contains `tasklist.db` and `manifest.json`; uses a consistent online database snapshot. Cache-Control is no-store. |
| POST | `/api/restore` | Raw TaskList export ZIP; `Content-Type: application/zip`. | Validates the backup, saves a private recovery ZIP and replaces lists/tasks/history atomically, keeping current schema/triggers and monotonic ID allocations. Returns `{restored: true, totals, recoveryBackup}`; `totals` has `lists`, `archivedLists`, `tasks`, `subtasks`, `events`, and `recoveryBackup` is the saved filename under `data/backups/`. Clears a pending first-run reminder after success. See Import and export for format/size limits and restore behavior. |
| GET | `/api/onboarding` | None. | `{importReminderPending}`: whether this fresh server database still has its backup reminder pending. Cache-Control is no-store. |
| POST | `/api/onboarding/acknowledge` | None. | Dismisses the backup reminder for this server. Returns 204; repeated acknowledgements are safe. Only reminder state changes. |
| GET | `/api/fresh-start` | None. | What Fresh start could clear: `{finishedTasks, archivedLists, archivedListTasks, lists, tasks, deletedTaskEvents, events, recoveryBackups, recoveryBackupBytes, databaseBytes}`. |
| POST | `/api/fresh-start` | `{confirmation: "RESET SELECTED", selections}`; `selections` maps any of `finishedTasks`, `archivedLists`, `everything`, `deletedTaskHistory`, `allHistory`, `recoveryBackups` to true/false, with at least one true. | Clears the selected data and compacts the database. Returns `{removedTasks, removedLists, removedEvents, removedBackups, freedBytes, compacted, warning}`; `warning` is null unless compaction or a backup deletion failed. 400 for a missing confirmation or unknown option, 409 while the database is busy. See Fresh start. |

Most results are JSON; export is a ZIP and 204 responses have no body. Validation errors usually include `{error: "message"}`. Common status codes are 400 for invalid input, 401 for missing/expired authentication or incorrect credentials, 404 for missing lists/items, 409 for setup conflicts or a busy backup restore, 429 for the auth cooldown, and 413 when a hosting upload limit rejects the request. Some errors have an empty body. No direct history-query or ZIP-merge endpoint is provided; recorded history is included in the database export.

### Browser pages and static assets

| Method | Path | Behavior |
| --- | --- | --- |
| GET/HEAD | `/`, `/index.html` | Main TaskList application. Unauthenticated requests redirect to login. |
| GET/HEAD | `/login.html` | Login/initial password setup. Already authenticated requests redirect to the main app. |
| GET/HEAD | `/task/{universalId}` | Authenticated Universal-ID deep link. Serves the app shell; the browser locates the matching item across active/archived lists. |
| GET/HEAD | Other frontend/PWA files in `wwwroot`, including CSS, JavaScript, `/sw.js`, `/manifest.webmanifest`, and `/icons/*` | Static application assets. Database, auth, logs and recovery files outside `wwwroot` are not static downloads. |

The authenticated app-shell fallback can serve HTML for other unmatched non-file GET/HEAD paths. Use only the listed API routes when expecting JSON; frontend asset availability does not mean new server endpoints are installed, so rebuild/restart after backend updates.

## Home screen and PWA

TaskList keeps no offline copy of its pages, like NewsReview: without a connection the browser shows its own offline page, which makes a connection problem obvious instead of opening a stale app that cannot reach its data. It can still be added to a phone's home screen from the web manifest. `sw.js` only removes the service worker and cache that releases before v1.7.28 installed.

## Security and privacy

TaskList is a single-user application.

- Passwords are stored only as salted PBKDF2-SHA256 hashes with 600,000 iterations.
- Authentication cookies are HttpOnly and SameSite=Strict.
- Use HTTPS when the application crosses an untrusted network.
- Prefer LAN/VPN/private reverse-proxy access when possible.
- Keep `data/`, database copies, and auth files private.
- The setup/login rate limiter permits 5 attempts per source IP per minute.

## Troubleshooting

### I only see Create Password

That is expected on first run. Look at the TaskList server console for the current setup token; it changes each time TaskList restarts until a password is saved.

### Login says "Too many attempts"

Setup and login allow 5 attempts per source IP per minute. Wait for the minute to pass before trying again.

### I forgot the password

Stop TaskList, delete only `data/auth.json`, and start it again. It prints a new setup token so you can create a password; your lists and tasks are untouched.

### Every browser was signed out after moving TaskList

Sign-in keys live in `data/keys/` (the startup log names it). Copy `data/keys/` along with the rest of `data/`, or sign in again.

### TaskList stops at startup with a message about a newer database

The database was written by a newer TaskList. Update TaskList; it leaves the file untouched until then.

### Other devices can't reach TaskList

It listens on loopback only (`http://127.0.0.1:8711`) unless started with `--urls`, for example `--urls "http://0.0.0.0:8711"`, or `ASPNETCORE_URLS`.

### The database is on a network or pooled drive

TaskList does not turn on WAL for UNC paths or drives Windows reports as Network/Unknown; the startup log names the journal mode it chose. If saves are slow or fail there, try a copy of the data folder on a local disk with `--data-dir`.

## Development checks

Run `dotnet build` and then `python3 -m unittest discover -s tests -v` for SQL integrity tests and `node tests/frontend.test.cjs` for frontend tests. SQL fixtures execute the schema, triggers, and operation SQL from the C# source against disposable databases. Cases cover moves, renames, status changes, imports, deletion, restore/rollback, IDs, counters, and 1,200 seeded mixed operations. `tests/test_fresh_start.py` starts the built TaskList against a throwaway data folder and checks every Fresh start choice: validation, open trees kept, Deleted events, history clears surviving a restart with unchanged milestone counters, the Stats history revision, recovery-backup removal, compaction, and IDs never being reused; it is skipped until TaskList has been built. JavaScript tests exercise the actual selection/bulk-action and milestone-check code with DOM fixtures. Python and Node are test tools, not TaskList runtime dependencies.

`tests/server.py` (the same file in TaskList, TaskList Stats, and SpendTracker) builds the app and starts the real server on a temporary data folder; `tests/test_sign_in.py` (also the same in all three) checks first-run setup, login, logout, and the saved password file. `tests/test_api.py` runs lists, tasks, subtasks, and error replies against that server.

With the adjacent TaskList Stats checkout and Node on PATH, the Python suite also compares actual TL trigger output against Stats' milestone code across six mixed datasets up to 11,000 creations. `TASKLIST_TEST_NODE` can select another Node executable. See [the audit coverage](tests/AUDIT.md).

GitHub Actions (`.github/workflows/tests.yml`) builds TaskList and runs the Python and Node suites on every push and pull request, with TaskList Stats checked out alongside so the cross-app milestone parity test runs too.

## Release notes

Current release: **v1.7.50**.

### v1.7.50

- Restarting the server no longer signs you out on Windows: sign-in keys always live in `data/keys/` (protected with machine-scope DPAPI), not in the Windows profile's key ring, which a service account does not always have loaded when the app starts (then the keys only lived in memory). Everyone signs in once more after updating.

### v1.7.49

- A task with subtasks has an arrow before its title to fold them away or show them again; folds are saved in this browser.
- No **Edit** button on task rows: double-click (or double-tap) a row to edit it.

### v1.7.48

- Shared `dropdown.js` (same in every app): on touch screens a dropdown in a dialog opens in place under its button instead of floating, so a phone's keyboard no longer makes it jump; a dialog opens scrolled to its top.

### v1.7.47

- Dropdowns open scrolled to the top each time, instead of where the list was left after a choice or in an earlier dialog.
- On a phone, a dropdown's search box inside a dialog works: when the keyboard opens and the dialog scrolls, the dropdown moves with its button instead of closing (which also put the keyboard away).

### v1.7.46

- Fix: a dropdown inside a dialog that opens upward (when there is more room above it, as on a phone) no longer collapses out of sight below the screen; `dropdown.js`, the same in every app, now clears the stylesheet's position when it opens a menu upward.

### v1.7.45

- Code: the scripts are JavaScript modules (`<script type="module">`) with imports and exports, like NewsReview's, instead of classic scripts sharing globals; the app starts once every module has loaded. `theme.js` and `dropdown.js` stay classic scripts, the same in every app, so the theme is applied before the page draws.

### v1.7.44

- Code: the same kind of server code sits in the same file as in the other apps. Database schema and queries moved from `Program.cs` into `Store.cs` (like SpendTracker's and NewsReview's), `DataTransfer.cs` is now `Transfer.cs`, and `Onboarding.cs` is the same file as SpendTracker's and NewsReview's.

### v1.7.43

- Code: every script starts with a short comment saying what it holds (and `'use strict';` where it is not a module), like SpendTracker's; `SingleFileLogger.cs` is now the same file in all four apps.

### v1.7.42

- Code: elements are built with the `el(tag, className, text)` helper, the same one in every app.

### v1.7.41

- Code: `$(selector)` and `$$(selector)` helpers like TaskList Stats, NewsReview, and SpendTracker, instead of calling `document.querySelector` directly.
- New optional `tests/test_browser.py` (Playwright, like SpendTracker's): tasks, subtasks, double-click edit, search, menus by keyboard, theme, and phone width.

### v1.7.40

- MANUAL.md uses the same section names and order as the other apps, with a contents list at the top and a **Current release** line under Release notes; no text was removed, only moved or retitled.
- README.md has a **Sibling apps** section linking all four apps.
- `.gitignore` has the same rules in every app.
- New **Troubleshooting** section, like the other apps' manuals. The leftover `siri-api.json` ignore rule is gone.

### v1.7.39

- Tests start the real server on a temporary data folder with `tests/server.py`, and `tests/test_sign_in.py` checks setup, login, logout, and the password file; both files are the same in TaskList, TaskList Stats, and SpendTracker.
- An unknown API address answers 404 `{ "error": "Not found" }` like NewsReview, instead of the app page (GET) or 405 (POST).
- New `tests/test_api.py` runs lists, tasks, and subtasks against that server; until now the tests only ran SQL taken from the source.

### v1.7.38

- `theme.css` starts with one shared palette, the same color names and values in every app (light and dark), followed by the colors only this app uses. `--subtask-divider` is now `--divider-subtle`, the name TaskList Stats used.

### v1.7.37

- Dropdowns and menus share one file, `dropdown.js`, the same in every app: Arrow Down/Up move through a menu (Arrow Down on a closed dropdown opens it), Home and End jump to the first and last item, and Escape closes it and returns to its button without closing the dialog under it. A dropdown that would be cut off inside a dialog floats over the page and opens upward when there is more room there, like SpendTracker's.
- Selection-mode check boxes are the Windows 95 kind, like the rest of the app.

### v1.7.36

- Double-click a task row to edit it, like SpendTracker's entries (not in selection mode, where a click selects).

### v1.7.35

- Every dialog is a `<form method="dialog">`, and its Cancel/Close/OK button is marked `data-close="dialogId"`; one shared handler closes it, the same code in every app.
- Keyboard focus draws the same dotted Windows 95 focus rectangle in every app; mouse clicks draw none.
- Screen readers get the same structure in every app: a hidden page heading with the app name and a **Skip to content** link that appears when you press Tab.
- In Fresh Start, Enter in the confirmation box does what Reset Selected Data does (it no longer does nothing).

### v1.7.34

- The login and main pages share one set of page tags, the same in every app: a description, the iPhone home-screen and status-bar settings, the manifest, and icons.
- Icons live in `/icons/` with the same names in every app: `favicon.svg`, `favicon.ico`, and `touch-180/192/512.png`. The manifest has an app ID, scope, and description.
- The login page has its own small stylesheet (`auth.css`) like the other apps, and shows the server's version if `version.json` can't be read.

### v1.7.33

- Database calls are synchronous, like SpendTracker's and NewsReview's. Microsoft.Data.Sqlite runs its async calls synchronously anyway, so this changes no behavior.

### v1.7.32

- Sign-in code lives in `src/PasswordAuth.cs`, the same file in all four apps. Passwords are capped at 1024 characters and `auth.json` is saved readable only by its owner on Linux and macOS.
- New `appsettings.json` with the listen address and log level, the same keys in every app.
- New `deploy/tasklist.service` systemd template, like NewsReview's.

### v1.7.31

- Wording matches the other apps: menu paths are written **File > Import/Export...**, only "..." and straight quotes are used.
- Counts in Manage Lists, Archives and Search use the browser's number format like the rest of the app.
- **Reset Selected Data** is in Title Case like the other buttons.

### v1.7.30

- The File menu uses the same groups and order as the other apps: Refresh, Manage Lists..., Search..., and Select... first, then Import/Export..., then the new **Highlight rows on hover** (like SpendTracker and TaskList Stats), then Log Out, then Fresh Start....
- The About box describes TaskList and gives a few tips, like the other apps'.

### v1.7.29

- Import/Export has **Download Backup**, **Choose File to Restore...**, and **Import Markdown...**, like the other apps (formerly Download Export and one Choose File to Import... for both kinds of file).
- Download names follow one pattern in every app, in local time: `tasklist-backup-YYYYMMDD-HHMMSS.zip`.

### v1.7.28

- No offline copy, like NewsReview: without a connection the browser shows its own offline page instead of a stale app. The old service worker and its cache are removed automatically.
- One `theme.js` shared word for word with the other three apps: the page marks its theme with `data-theme`, and it loads at the top of every page, login included, so it never flashes the wrong theme.

### v1.7.27

- **View > Use Retro Font**: the whole app, login page and chart labels included, can use W95FA, an open-source recreation of the Windows 95 font bundled with the app (SIL Open Font License, `wwwroot/fonts/OFL.txt`), so it looks the same on Windows, Linux, macOS, Android, and iOS. Off by default; saved per browser.

### v1.7.26

- The Fresh Start dialog has no × in its title bar, like every other dialog in the four apps.

### v1.7.25

- Backup addresses match SpendTracker and NewsReview: `GET /api/backup` downloads a backup ZIP and `POST /api/restore` restores one (formerly `/api/export` and `/api/import/backup`).

### v1.7.24

- The server enforces the page's text limits (task titles 500, descriptions 4,000, list names 100, list descriptions 1,000), like SpendTracker.

### v1.7.23

- The database records its schema version in `PRAGMA user_version` (1), like SpendTracker and NewsReview. A database or backup from a newer schema is refused, and startup stops with a clear log message instead of changing the file.
- Exports include the schema version and a SHA-256 checksum of the database, checked on restore; older exports without them still restore.

### v1.7.22

- The database is now `tasklist.db` (no hyphen). An existing `task-list.db` is renamed on first start; exports made before this release still restore.
- Command-line options like NewsReview: `--data-dir PATH`, `--version`, and `backup --output FILE`. The data folder can also come from `TaskList:DataDirectory` or `TASKLIST_DATA_DIR`.

### v1.7.21

- Startup logs the version and data folder, the database settings, and where the sign-in keys are, in the same three lines as the other apps.
- Without a Windows profile key ring (Linux, Task Scheduler), sign-in keys now live in data/keys like NewsReview, so a systemd service with ProtectHome stays signed in. Expect to log in once after updating.
- The login page words wrong-password and too-many-attempts messages itself; setup conflicts say "A password is already configured."
- `/api/auth/status` includes `version`, like the other apps.

### v1.7.20

- Browser hardening like NewsReview: a content security policy, no MIME sniffing, no referrer, and writes from other sites are refused.
- API replies are never stored by the browser; pages and scripts are revalidated on each load.
- Requests give up after 30 seconds instead of waiting forever on a stopped server (backups, restores, and imports excepted).

### v1.7.19

- The browser tab title is just "TaskList", without the current list's name, like the other apps; the title bar still names the list.

### v1.7.18

- Opening the app signed out goes to plain `login.html`, like NewsReview, instead of `login.html?ReturnUrl=%2F`; deep links still keep their address through signing in.

### v1.7.17

- The Home Screen icons leave a margin around the window, like NewsReview's, so iOS's rounded corners no longer cut off its frame and title bar. They have new file names (touch-180.png, touch-192.png, touch-512.png) so iOS downloads them; remove the app from your Home Screen and add it again to update it.

### v1.7.16

- The Home Screen icons have new file names (home-180.png, home-192.png, home-512.png), so iOS downloads the current icon instead of reusing one it saved earlier. To update an app already on your Home Screen, remove it and add it again from Safari's Share menu.

### v1.7.15

- On phones (650px wide or less) the title bar leaves out the version number, which was often cut off; desktops and the About box still show it.

### v1.7.14

- The room above the title bar on phones is the `--ios-top-offset` variable (20px), shared with TaskList Stats, NewsReview, and SpendTracker.

### v1.7.13

- The Fresh start dialog puts **Cancel** first and **Reset Selected Data** on the right, the same order as every other dialog in TaskList, TaskList Stats, NewsReview, and SpendTracker.
- On touch screens, text boxes use 16px text so phones do not zoom in when one is focused.

### v1.7.12

- Adding a task or subtask, deleting, and moving update the list in the browser from the server's answer instead of loading the whole list again; anything unexpected still reloads it.
- The server compresses its responses (Brotli or gzip), so data, scripts, and styles cross the network several times smaller.
- favicon.ico holds compressed PNG images: 960 bytes instead of 15 KB.

### v1.7.11

- Muted text in the Light theme is `#555`, and dialogs dim the page with NewsReview's `#0007` backdrop, matching NewsReview, TaskList Stats, and SpendTracker.

### v1.7.10

- **Fresh Start...** moves to the bottom of the File menu, below Log Out.
- The Fresh Start dialog matches NewsReview's Fresh start dialog (size, frame, title bar with ×, footer, and a **Reset Selected Data** button) and uses Win95 check boxes like SpendTracker's.

### v1.7.9

- Add **File > Fresh Start...**, like NewsReview's: choose what to clear (finished tasks, archived lists, every list and task, history of deleted tasks, all task history, or pre-restore recovery backups), type `RESET SELECTED`, and the database is compacted afterwards to free disk space.
- Move the legacy history backfill into `TaskEventBackfill.cs` so startup and Fresh start share it.

### v1.7.8

- New favicon and app icon in the same style as TaskList Stats and SpendTracker: a gray window with a navy title bar, holding a checklist.

### v1.7.7

- **Create a List...** moved from the File menu into **Manage Lists**, after **Archives...**.
- **File > Select...** shows a check box like SpendTracker's instead of a radio dot.
- File, View, and Help highlight in the title color on mouse hover and while their menu is open, like NewsReview's top bar.

### v1.7.6

- Selection mode's menu-bar count reads **Selected: N**, matching SpendTracker.

### v1.7.5

- Build and run on Windows, Linux, and macOS again: `TaskList.csproj` no longer pins the `win-x64` runtime, which made `dotnet run` fail to load SQLite on other systems.
- Replace the generated `wwwroot/version.js` global with `wwwroot/version.json`, fetched by the login page and About label like TaskList Stats and SpendTracker.

### v1.7.4

- Move every color into `wwwroot/theme.css` as Light/Dark tokens, using the token names shared with TaskList Stats, NewsReview, and SpendTracker (`--face`, `--surface`, `--ink`, `--muted`, `--light-edge`, `--dark-edge`, `--title`, `--link`, and so on). The theme-only rules that used to repaint `style.css` and `task-controls.css` now live in the rules they belong to, so each color is set once; rules that were always overridden are removed.
- `milestones.css` uses the tokens directly instead of fallback colors, and the confetti colors are `--confetti-1` through `--confetti-6`.
- The look is unchanged: every view, menu, and dialog renders identically in both themes on desktop and phone widths. A few dark-theme spots that never had their own dark value keep their light colors as `--classic-*` tokens.

### v1.7.3

- Move the C# server sources into `src/`. `TaskList.csproj`, `wwwroot/`, `tests/`, and every build, run, and test command stay where they were; behavior is unchanged.

### v1.7.2

- Space the File, View, and Help menubar buttons 1px apart, matching TaskList Stats.

### v1.7.1

- Draw the title-bar minimize, maximize, and close symbols with CSS in equal-size boxes so they line up; the old `_` sat on the text baseline below the other two. TaskList Stats and NewsReview use the same markup and styles.
- Keep Complete/Reopen labels beneath the sticky Actions header while the task list scrolls; the hold-progress label no longer competes with the header's stacking order.

### v1.7.0

- Listen on `http://127.0.0.1:8711` by default instead of ASP.NET's port 5000; `--urls` and `ASPNETCORE_URLS` still choose another address, matching TaskList Stats.
- Hash new passwords with 600,000 PBKDF2-SHA256 iterations and upgrade 210,000-iteration hashes after the next successful login, replacing `auth.json` atomically.
- Generate `wwwroot/version.js` with its trailing semicolon and without a byte-order mark, and only when the version changes, so builds no longer modify the committed file.
- Verified default/`--urls`/`ASPNETCORE_URLS` addresses, first-run setup, legacy-hash login and upgrade, unchanged hash on a wrong password, restart, and the login rate limit against the running app.

### v1.6.11

- Combine Search's creation date/time ranges into one field using NewsReview's comma-separated format; retain date-only, time-only, and overnight searches.
- Remove the separate range fields and stale start/end bindings; explicitly bind the new field and retain existing list filters, task/subtask results, and local-time matching.
- Cover real form submission, malformed ranges, missing times, date bounds, noon/midnight, and field reset with frontend tests.

### v1.6.10

- Normalize title-bar typography, single-line labels, centered context, and decorative window symbols with TaskList Stats and NewsReview.
- Allow long application/list names to truncate within their columns instead of overlapping the other title-bar content.

### v1.6.9

- Count undated events in global/list milestones and match Stats' event-record ordering; keep yearly milestones tied to valid UTC dates.
- Rebuild derived milestone counters on ZIP restore and rotate a history revision for Stats cache invalidation.
- Preserve distinct same-timestamp status transitions, while guarding reconstructed/moved events against duplicates; remove duplicate startup trigger definitions.
- Recheck milestones after writes during an in-flight request and defer background-tab consumption until focus returns.
- Add database, frontend, and cross-app regression coverage for data identity, histories, moves, renames, imports, status changes, restore, and milestone parity.

### v1.6.8

- Match event-based milestone popup counters to Stats' dated milestone history, excluding unknown/invalid dates while retaining date-only imports.
- Rebuild corrected counters at startup and keep already pending notices without replaying historical milestones.
- Preserve list IDs across renames, original event attribution after moves, Universal-ID milestones, and the existing threshold schedules.
- Remove the now-unreachable unknown-date fallback from dated event notification inserts.
- Add SQLite regression checks using the actual notification and task-event SQL, including renamed lists, moved undated imports, and a newly created subtask.

### v1.6.7

- Process bulk moves bottom-to-top to preserve relative visible order under destination task-number sorting.
- Preserve subtree deduplication, independent-subtask promotion, dates, Universal IDs, confirmation previews, and partial-failure handling.
- Document move ordering and update project/frontend release metadata.

### v1.6.6

- Exit selection mode with Escape, clear selected tasks and the range anchor, and persist the disabled preference.
- Share mode changes between File > Select... and Escape; retain dialog cancellation and busy-state protection.
- Update project/frontend version metadata.

### v1.6.5

- Add a server-scoped first-run backup reminder before task controls become usable, with Import/Export... and Start Fresh choices.
- Persist acknowledgement outside SQLite; retain it across browsers/restarts, avoid prompting existing installations, and clear it on successful ZIP restore.
- Document all HTTP endpoints, request/response fields, authentication, data effects, browser routes and API limitations.
- Share scrollable-dialog chrome and centralize the shared main-window DOM reference; update project/frontend version metadata.

### v1.6.4

- Replace the top-level Import button with File > Import/Export... immediately after Search..., with export and file-picker buttons in a scrolling-content dialog.
- Download a complete, authenticated ZIP backup containing a consistent standalone SQLite snapshot and manifest; include archived lists, task history, deleted-list names and milestone data.
- Add confirmed ZIP restore with validation, private pre-restore recovery ZIPs, atomic table replacement, current trigger preservation, and monotonic allocation counters.
- Keep Markdown import into the current list; remove the old import controls/handlers and place transfer behavior in its owning frontend/backend modules.
- Share authenticated response handling for JSON and binary downloads, refresh confirmed imported data, and cache the transfer module with the app shell.
- Update documentation and project/frontend version metadata; add no dependencies.

### v1.6.3

- Move subtask tree markers and titles 18 pixels farther right at every nesting level, on desktop and mobile.
- Keep the existing depth spacing, status lines, selection behavior, and shared Light/Dark styles.
- Update project/frontend version metadata and documentation.

### v1.6.2

- Add inclusive Shift-click selection in visible task-row order, including the checkbox path.
- Preserve existing selections and root/descendant behavior; repeated ranges use the last normal selection as their anchor.
- Clear the range anchor with list/view changes, disabled selection mode, and failed refreshes; discard it when its row disappears.
- Share selection clearing between those paths and update project/frontend version metadata.

### v1.6.1

- Place File > Select... immediately below Search... in the File menu.
- Select and deselect every descendant with a root task; include those subtasks in toolbar and confirmation counts.
- Share a selection preview between bulk confirmations and the Move destination dialog; clear the preview for individual moves.
- Simplify structural-action wording to tasks/subtasks while preserving subtree deduplication and existing server actions.
- Reuse one recursive task-flattening helper for selection and Task Information; update project/frontend version metadata.

### v1.6

- Add persistent File > Select... mode, row/checkbox/keyboard selection, and sticky theme-aware highlighting.
- Center the selection count and shared Complete/Reopen split action in the menu bar, with responsive layout for smaller screens.
- Confirm bulk Complete, Reopen, Cancel, Move, and Delete; include descendants for structural actions and process overlapping selections once.
- Reuse existing authenticated endpoints and hold controls; stop on errors, report partial success, and retain remaining visible selections.
- Cache the selection module with the application shell and update project/frontend version metadata.

### v1.5.24

- Preserve the last name of deleted lists in a separate history table before deletion, including empty lists and archived lists.
- Keep list names and task deletion events atomic with the existing deletion transaction; retain task/subtask cascades and permanent list IDs.
- Update project/frontend version metadata and documentation to v1.5.24.

### v1.5.23

- Require the same 800 ms hold for Reopen as Complete, including the progress fill and touch/mouse/pen/keyboard cancellation behavior.
- Share one action-labelled hold handler and remove Complete-only names from the shared JavaScript and CSS.
- Update project/frontend version metadata and documentation to v1.5.23.

### v1.5.22

- Make lower dialog buttons the shared default: redistribute footer padding to 6px above and below, moving Task Information OK and other dialog buttons down 6px without changing the standard footer height.
- Remove the Search-specific button transform; retain its existing lower position through shared footer layout.
- Remove redundant milestone footer padding and keep only its mobile horizontal inset so live milestone buttons use the same default spacing.
- Preserve fixed Task Information/Search title bars and footers, content scrolling, tap help, and button actions.
- Update release metadata/documentation to v1.5.22.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.21

- Constrain Task Information to the viewport and keep its title bar and OK footer outside the scrolling content on phones, laptops, and desktops.
- Make the existing info-body the scroll container and allow it to shrink while the title and footer retain their height; preserve content-sized dialogs when everything fits.
- Remove the obsolete whole-dialog scrolling rule from task-controls.css instead of overriding it with another rule.
- Move tap-help dismissal to the content scroll event so tooltips close when the task details move; preserve keyboard help, Description highlighting, and task metadata.
- Update release metadata/documentation to v1.5.21.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.20

- Match Stats Help with 16px text and 1.5 line spacing in About; keep the typography change scoped to that dialog.
- Keep the direct Help action, owner/version footer, and existing dialog layout.
- Cleanup audit found no safely removable source files or unused frontend functions; avoid manufacturing unrelated cleanup changes.
- Preserve SQLite WAL/performance settings, migrations and event compatibility, pending milestone consumption, non-optimistic updates, optimized PATCH merging, and real-mouse hover detection.
- Update release metadata/documentation to v1.5.20.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.19

- Continue global event, total recorded-event, Universal-ID, per-list, and yearly milestone notifications every 500 after the existing base thresholds.
- Start continuing global/recorded/Universal-ID milestones at 10,500, per-list milestones at 5,500, and yearly milestones at 2,500, matching TaskList Stats v2.1.11.
- Preserve the pending-only queue, one-time consumption, startup baseline, and every existing early threshold; upgrades do not create retroactive celebration popups.
- Replace the old 5,000-step trigger predicates without adding duplicate notification paths or schema changes.
- Update release metadata/documentation to v1.5.19.

### v1.5.18

- Make TaskList the sole owner of live milestone dialogs and confetti; Stats retains its independent historical Milestones tab.
- Replace the cross-app viewed-state claim API with an authenticated pending-queue consume API using atomic `DELETE ... RETURNING`.
- Preserve pending notifications from both supported older ledger schemas and discard acknowledged/baseline rows during a transactional migration.
- Keep every existing milestone counter, threshold family, and continuation schedule; rebuild counters from history without creating retroactive notifications.
- Remove obsolete viewed/viewer/historical fields, schema-version state, and historical baseline-generation code.
- Keep pending notices across restarts and prevent consumed notices from replaying across TaskList tabs or later actions.
- Update release metadata/documentation to v1.5.18.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.17

- Add a compact document/note indicator immediately after task titles when the task has a non-empty description.
- Keep rows without descriptions unchanged instead of adding a mostly empty Description column.
- Make the indicator open Task Information and briefly highlight the Description label/value with the same theme-aware yellow used for deep-link/Search visit highlighting.
- Use an inline dependency-free document icon so appearance is consistent across operating systems rather than relying on emoji.
- Support root tasks and nested subtasks without changing task IDs, status/actions, or row behavior.
- Update release metadata to v1.5.17.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.16

- Replace CSS pointer-capability media-query gating with actual mouse movement detection for task-row hover.
- Enable row hover whenever a real mouse or trackpad moves, including on touchscreen Windows laptops whose browser reports no hover-capable or fine pointer through CSS media queries.
- Remove hover mode immediately on touch input and ignore synthetic mouse events briefly afterward so touch-only devices do not regain sticky row highlights.
- Preserve existing root/subtask hover colors and deep-link/Search visit highlighting.
- Update release metadata to v1.5.16.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.15

- Restore task-row hover highlighting on touchscreen laptops and other hybrid devices by detecting any attached hover-capable fine pointer instead of requiring the device's primary pointer to be fine.
- Keep sticky hover suppression on phones and touch-only/coarse-pointer devices.
- Preserve the existing desktop root/subtask hover colors and deep-link/Search visit highlighting.
- Update release metadata to v1.5.15.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.14

- Make `TaskList.csproj`'s single `<Version>` property the only manually maintained runtime release number; let the .NET SDK derive assembly/file/informational versions from it.
- Remove release-number query strings from CSS, JavaScript, and dynamic milestone imports so individual asset URLs no longer need manual version bumps.
- Replace the per-release service-worker cache name with a stable shell cache; keep network-first `cache: 'no-store'` fetches so successful online loads always refresh the cached copy.
- Keep `version.js` generated from the authoritative project version during build for the browser version label.
- Remove the remaining duplicate v1.5.12 README release heading.
- Update release metadata/documentation to v1.5.14.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.13

- Consolidate the shared SQLite connection PRAGMA command so synchronous mode, busy timeout, and foreign-key settings have one source of truth for sync and async connections.
- Normalize versioned frontend/PWA asset references to the current release instead of carrying a mix of older cache-busting version strings.
- Remove duplicate v1.5.11 and v1.5.10 release-history headings left by earlier README updates.
- Keep the existing purpose-based frontend modules; the cleanup audit found no unreferenced JavaScript functions or safely removable runtime/source files.
- Update project/frontend metadata to v1.5.13.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.12

- Enable SQLite WAL mode on local/fixed database storage and automatically skip WAL for UNC/network/unknown Windows drives.
- Apply `synchronous=NORMAL`, `busy_timeout=5000`, and `foreign_keys=ON` to TaskList database connections and log the selected journal mode at startup.
- Stop re-downloading the entire current list after task PATCH operations; merge the returned task into the existing in-memory tree while preserving loaded subtasks, then re-render.
- Keep task updates non-optimistic: the UI changes only after the PATCH has completed successfully.
- Cache-bust the changed task JavaScript and advance the PWA shell cache.
- Document SQLite durability/performance tradeoffs and PATCH refresh behavior.
- Update project/frontend version metadata to v1.5.12.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.11

- Add native single-file runtime logging at `logs/console.log` while preserving the normal interactive console output.
- Capture the ASP.NET/.NET logging pipeline, including startup/shutdown, request status/timing, warnings, errors, and exceptions, without requiring a `cmd.exe` redirection wrapper.
- Keep exactly one log file: when it would exceed 10 MiB, truncate that same file and continue writing rather than creating rotated copies.
- Keep the one-time first-run setup token console-only so sensitive setup material is not persisted in the runtime log.
- Ignore the runtime `logs/` directory in Git and document logging behavior, retention, and Task Scheduler use.
- Update project/frontend version metadata to v1.5.11.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.10

- Add Deleted as a first-class append-only task event with the task's prior status, deletion timestamp, list/display hierarchy location, title snapshot, and Universal ID.
- Record Deleted events for every task/subtask removed by direct task deletion and for every remaining item removed by list deletion.
- Keep task moves from generating false Deleted events even though moving internally removes/reinserts item rows.
- Migrate existing task_events tables to allow the Deleted event type while preserving all existing event IDs/history and rebuilding the normal event indexes/triggers.
- Do not fabricate Deleted events for tasks removed before v1.5.10 because no trustworthy historical deletion timestamp exists.
- Extend global milestone/celebration copy to understand Deleted milestones generated by the existing event milestone system.
- Update milestone asset cache-busting and project/frontend metadata to v1.5.10.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.9

- Replace native browser confirmation prompts with a reusable TaskList Win95-style confirmation dialog.
- Use the custom dialog for task/subtask deletion, list deletion, and list archiving.
- Give each confirmation a purpose-specific title/action label and focus Cancel first to reduce accidental destructive actions.
- Explain in delete confirmations that recorded task-event history remains available to Stats after current task/list rows are removed.
- Cache-bust the changed dialog/app/list/task assets in the application shell and PWA cache.
- Update project/frontend metadata and README documentation to v1.5.9.
- Add no monkey patches or new frontend/runtime dependencies.

### v1.5.8

- Harden mobile hold-to-complete by guarding touchend/touchcancel at the document capture phase, so a quick release always cancels the 800 ms timer even if mobile Safari retargets the touch.
- Block compatibility/synthesized Complete clicks with a capture-phase stopImmediatePropagation guard.
- Disable native touch gestures/callouts on the Complete button itself while preserving normal page scrolling everywhere else.
- Cache-bust the Complete JS/CSS assets in the application shell and PWA precache so mobile clients cannot keep running an older click-to-complete tasks.js alongside newer styling.
- Preserve the desktop hold behavior and the mobile no-hover-row behavior from v1.5.7.
- Update project/frontend metadata and README documentation to v1.5.8.
- Add no monkey patches or new frontend/runtime dependencies.

### v1.5.7

- Make the 800 ms Complete hold reliable on mobile Safari/iOS with an explicit non-passive touch path instead of relying on touch-flavored pointer events.
- Suppress synthesized tap/click completion so a normal mobile tap cannot complete an Open task.
- Preserve the same left-to-right hold progress, early-release/move-away cancellation, mouse/pen hold, and keyboard hold behavior.
- Restrict task-row hover highlighting to devices with a real fine pointer and hover capability so mobile touch no longer leaves a sticky highlighted task row.
- Keep deep-link/Search visit highlighting intact because it is intentional navigation feedback rather than hover state.
- Version the PWA shell cache and bypass the browser HTTP cache for network-first shell fetches so mobile clients pick up updated JavaScript/CSS more reliably after a release.
- Update project/frontend metadata and README documentation to v1.5.7.
- Add no monkey patches or new frontend/runtime dependencies.

### v1.5.6

- Expand shared milestone celebrations to mirror the permanent Stats milestone families, including global event, recorded-event, Universal-ID, per-list, and yearly milestones.
- Continue all long-running milestone families at 5,000-step intervals so celebration notifications do not end permanently.
- Keep milestone generation lightweight with SQLite counters/triggers and baseline newly introduced families as viewed on upgrade instead of replaying old achievements.
- Preserve atomic one-time claiming across TaskList and TaskList Stats; multiple simultaneously reached milestones share one celebration dialog/confetti run.
- Require an 800 ms hold on the **Complete** primary action to prevent accidental mobile completions.
- Fill the Complete button from left to right with the existing theme-aware task-hover blue while the hold progresses; releasing or moving away early cancels it.
- Keep Reopen/Edit/More behavior unchanged and expose the hold duration through one `COMPLETE_HOLD_MS` constant.
- Add no monkey patches or new frontend/runtime dependencies.

### v1.5.5

- Add lightweight shared milestone-notification records for major Created, Completed, and Universal-ID thresholds.
- Claim each real milestone notification atomically so it is celebrated only once across TaskList and TaskList Stats.
- Baseline already-attained thresholds as viewed on first upgrade instead of replaying old achievements.
- Add a responsive Win95-style Light/Dark celebration dialog with short dependency-free confetti and reduced-motion support.
- Check for pending milestones after successful TaskList writes so newly reached thresholds can appear immediately.
- Add the milestone frontend files to the existing network-first PWA shell.
- Keep the permanent milestone-history view in TaskList Stats unchanged.
- Add no monkey patches or new runtime/frontend dependencies.

### v1.5.4

- Remove the System theme choice and all `prefers-color-scheme` / system-following logic so theme selection is strictly Light or Dark.
- Add a **Tasks** heading above Open, Done, and All in the View menu to match the existing Theme section label.
- Restore File, Import, View, and Help to the original flat menubar appearance instead of raised button chrome.
- Increase Task Information grid divider contrast in dark mode while preserving the light-theme appearance.
- Keep the existing Win95 Light/Dark styling, task behavior, Search, lists, authentication, event logging, and PWA behavior unchanged.
- Add no monkey patches or new runtime dependencies.
- Bump TaskList project and frontend metadata to v1.5.4.

### v1.5.3

- Add persistent Light, Dark, and initially System theme support across the main UI and login/setup page.
- Add shared semantic Win95 theme tokens for window chrome, menus, dialogs, forms, tables, task rows, nested subtasks, Search, Task Information, highlights, links, tooltips, and authentication screens.
- Preserve the Windows 95 raised/recessed visual language in dark mode.
- Cache the shared theme assets in the existing network-first PWA shell.
- Keep task/list/search/archive/deep-link/event-log/authentication behavior unchanged.
- Add no runtime dependencies or release-specific patch files.

### v1.5.2

- Replace the four Date/Time Search inputs with two compact fields: **Date range** and **Time range**.
- Accept date ranges such as `10/3 - 10/8`, with either side using `m/d`, `m/d/yy`, or `m/d/yyyy`.
- Accept time ranges such as `9am - 9pm` while preserving flexible 12-hour parsing and overnight windows.
- Accept hyphen, en dash, or em dash range separators.
- Preserve date-only, time-only, and combined date/time search behavior while removing the old separate Start/End field parsing and mobile-order CSS.

### v1.5

- Add the append-only `task_events` table for complete future task-state history.
- Record Created, Completed, Cancelled, and Reopened transitions with Universal ID, timestamps, old/new status, list/display hierarchy location, title snapshot, and provenance.
- Use SQLite triggers so normal task/subtask creation and status changes are logged without duplicating event-writing logic across API endpoints.
- Preserve repeated future state cycles instead of overwriting older transitions in the per-task timestamp columns.
- Backfill recoverable pre-v1.5 state timestamps on first startup while acknowledging that older repeated cycles cannot be reconstructed if they were never stored.

### v1.4.x highlights

- Replace crowded per-task actions with a split Complete/Reopen control plus More menu.
- Add Task Information hierarchy/time metrics and mobile-friendly tooltips.
- Split the old all-in-one frontend into purpose-based modules.
- Make `TaskList.csproj` the authoritative version source and generate the frontend version at build time.
- Remove release-specific runtime overrides and move behavior back into normal source files.

### v1.3.x highlights

- Add list archives, Move Task, Universal-ID deep links, Search > View highlighting, advanced date/time search, network-first PWA behavior, full-viewport layout, and the current recursive schema.
- Remove legacy query-string deep links, old migration helpers, and release-specific compatibility code.

Older release-by-release details remain available in the Git commit history and version tags.
