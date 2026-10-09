> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# TaskList v1.5.20

TaskList is a small self-hosted task manager with recursive subtasks, multiple lists, search, Markdown import, archives, Universal-ID deep links, milestone celebrations, a Windows 95-style interface, persistent Light/Dark themes, authentication, PWA support, and an append-only task-state event log.

It runs as an ASP.NET Core server with a plain HTML/CSS/JavaScript frontend. There is no Node.js build step or frontend framework.

## Stack

- ASP.NET Core / C# minimal API
- .NET 10
- SQLite via `Microsoft.Data.Sqlite`
- Plain HTML, CSS, and JavaScript
- Built-in ASP.NET Core cookie authentication and rate limiting
- Service worker + web manifest for PWA behavior
- No React, Node.js, npm, Electron, Bootstrap, Entity Framework, or ORM

## Project layout

TaskList intentionally keeps a small source tree. Frontend code is split by responsibility rather than by release version.

```text
TaskList.csproj        .NET project, dependencies, and authoritative release version
Program.cs             server, authentication, SQLite schema, event-log triggers, and API
MilestoneNotifications.cs
                       pending milestone queue, counter triggers, schema migration, and consume API
README.md              setup, architecture, behavior, security, and release notes

data/                  runtime/private data
  task-list.db         SQLite database
  auth.json            salted password hash
  siri-api.json        optional local runtime data

wwwroot/
  index.html           main application shell and dialogs
  login.html           login / first-run password setup
  version.js           frontend release version, regenerated from TaskList.csproj during build
  app.js               shared DOM/state/API/helpers
  tasks.js             task rendering, actions, edit/subtask/move/import and Task Information metrics
  lists.js             list selection, creation, management, archives
  search.js            keyword/date-time search and Search list filtering
  ui.js                menus, navigation, startup, deep-link startup, PWA registration
  milestones.js        pending milestone consumption/dialog/confetti behavior
  milestones.css       responsive Light/Dark milestone celebration styling
  theme.js             persistent Light/Dark theme selection and startup theme application
  login.js             login/setup behavior
  style.css            general Win95-style application/login/search layout
  theme.css            shared Light/Dark Win95 theme tokens and dark-theme styling
  task-controls.css    task-specific Move/action control styling
  sw.js                network-first service worker
  manifest.webmanifest PWA metadata
  icons/               PWA/favicon assets
```

There are no release-specific JavaScript override files. New behavior belongs in the normal module or stylesheet that owns it rather than in runtime wrappers, monkey patches, or release-specific shim files.

## Version handling

`TaskList.csproj` contains the single authoritative application version in its `<Version>` property. The .NET SDK derives the assembly/file/informational versions from that value, so they are not repeated as separate manually maintained properties.

The `GenerateWebVersion` MSBuild target writes `wwwroot/version.js` from `$(Version)` before each build for the browser's About/version label.

Frontend asset URLs no longer carry release-number query strings. The service worker uses a stable shell-cache name and network-first requests with `cache: 'no-store'`, updating the cached copy after a successful network response and using cache only as an offline fallback. That keeps frontend freshness independent of manually duplicated release numbers.

## Run

```powershell
dotnet restore
dotnet run --urls "http://0.0.0.0:8711"
```

The database is stored at `data\task-list.db` and authentication settings are stored at `data\auth.json`.

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

## Login and API authentication

On first launch, TaskList prints a random setup token in the server console. The setup screen requires that token plus a new password of at least 8 characters and password confirmation.

The plaintext password is never stored. `auth.json` contains a random salt and PBKDF2-SHA256 password hash.

After login, ASP.NET Core issues an HttpOnly, SameSite=Strict authentication cookie. Login and first-run setup are rate-limited per source IP to 5 attempts per minute.

## Themes

Use **View → Theme** to choose either **Light** or **Dark**. The choice is stored in `localStorage` and applies to both the main TaskList UI and the login/setup screen.

The dark theme keeps the same Windows 95 raised/recessed visual language rather than replacing it with a modern flat theme. Theme styling is centralized in `wwwroot/theme.css`, and `wwwroot/theme.js` applies the saved choice before the UI fully renders so the page does not briefly flash the wrong theme.

There is intentionally no automatic **System** theme mode; theme selection is strictly Light or Dark.

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

The current `items` row remains the fast current-state model while `task_events` preserves repeated transitions and later deletions for historical analysis. Moving a task between lists does not create a Deleted event even though the move implementation temporarily removes/reinserts item rows.

When a pre-v1.5 database first runs under v1.5+, TaskList backfills the older Created/Completed/Cancelled/Reopened timestamps it can recover into the event log. Repeated old state cycles that were never stored cannot be reconstructed exactly. Likewise, deletions performed before v1.5.10 cannot be backfilled because older versions stored no trustworthy deletion timestamp.

TaskList expects the current recursive `items` schema. The old pre-v1.1 `tasks`/`subtasks` migration path has been removed.

## Milestone celebrations

TaskList alone owns live milestone dialogs and confetti. It keeps a persistent pending-notification queue in its SQLite database. TaskList Stats stays read-only and provides a separate permanent Milestones analysis tab. The notification rules mirror those analytical milestone families:

- global Created / Completed / Cancelled / Reopened / Deleted milestones at 1, 100, 500, 1,000, 2,000, 3,000, 5,000, and 10,000, then every 500 events indefinitely
- overall recorded-event milestones on the same global schedule
- Universal ID milestones at #1, #100, #500, #1,000, #2,000, #2,500, #3,000, #5,000, and #10,000, then every 500 IDs indefinitely
- per-list Created / Completed milestones at 100, 500, 1,000, 2,000, and 5,000, then every 500 events indefinitely
- yearly Created / Completed milestones at the first event, 100, 500, 1,000, and 2,000, then every 500 events indefinitely within that calendar year

Continuing milestones start at 10,500 for global events, recorded events, and Universal IDs; 5,500 for per-list events; and 2,500 for yearly events. Each family continues at 500-step intervals.

TaskList maintains lightweight milestone counters with SQLite triggers as events are recorded. Reaching a qualifying threshold creates one pending notification row rather than rescanning all history after every action. Counter state is rebuilt from the event log at startup, before the triggers are installed, so existing history is the baseline and only future inserts can create new notifications. Counter rebuilding preserves progress across restarts.

The TaskList frontend calls authenticated `POST /api/milestones/consume` at startup and after successful writes. One atomic `DELETE ... RETURNING` removes and returns all pending notices. If several milestones are reached by the same action, they are returned together and shown in one dialog with one confetti animation. Concurrent TaskList tabs cannot consume the same row twice. Pending notices survive browser/server restarts until consumed; consumed notices are not replayed on restart or subsequent task actions. Consumption happens before display, so a lost response or a browser closing at that point can prevent a popup from being seen.

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

Deleting an item also deletes all descendants. Parent/root status controls which Open/Done/All view the entire tree appears in.

## Lists and archives

Each list has its own visible task-number sequence. Lists can be created, edited, archived, restored, or deleted.

Archived lists are hidden from the normal File list picker, search scope, and Move destinations, but their tasks remain in the database. Universal-ID deep links can still locate tasks in archived lists.

TaskList prevents archiving or deleting the only active list.

## Task status and actions

Open tasks use **Complete** as the primary action. To prevent accidental completions on touch screens, **Complete must be held for 800 milliseconds**. Mobile touch release/cancel events are guarded at the document capture phase so a quick tap cancels the hold even if the browser retargets the touch away from the button. A synthesized tap/click can never invoke Complete directly. Mouse/pen and keyboard holds use the same duration. While Complete is held, the button fills from left to right using the same theme-aware blue used for desktop task-row hover feedback. Releasing, cancelling the touch, or moving away before the hold finishes cancels the action. The hold duration is controlled by the single `COMPLETE_HOLD_MS` constant in `wwwroot/tasks.js`, so it can be changed without editing the styling.

Done and Cancelled tasks use **Reopen** normally, without the hold requirement. **Edit** stays directly accessible.

The attached More menu contains actions such as:

- Add Subtask
- Cancel
- Move...
- Delete

Destructive/structural confirmations use TaskList's own Win95-style modal instead of the browser's native `confirm()` prompt. The custom confirmation dialog is used for task/subtask deletion, list deletion, and list archiving. Cancel receives initial focus so opening a confirmation does not put the destructive action under Enter by default. Delete confirmations also state that recorded task-event history is retained for TaskList Stats even after the current task/list rows are removed.

Moving a task preserves its Universal ID, title, description, status, timestamps, child numbering, and descendant hierarchy while assigning a new visible top-level ID in the destination list.

On mouse/trackpad devices, task rows highlight on real hover so the controls at the right side of a wide row remain visually tied to the correct task. Touch/coarse-pointer devices do not use row hover highlighting, which avoids sticky mobile hover states. The desktop hover treatment has separate Light/Dark theme colors and preserves nested-subtask status/hierarchy markings.

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

The hierarchy metrics are calculated against the task's actual source list/tree, including when Task Information is opened from Search results. Tasks with a non-empty description show a small document indicator beside the title; selecting it opens Task Information and briefly highlights the Description row. Non-obvious metrics include hover/focus help, with tap/keyboard support on mobile.

## Search

**File → Search...** supports:

- fuzzy keyword/title/description search across active lists
- compact creation-date ranges typed into one field
- compact creation-time ranges typed into one field
- time-only searches across all dates
- overnight time windows
- task and subtask results
- filtering the current result set by list
- **View** to open the correct list, switch to All, scroll to the item, and highlight it

The Search list filter is intentionally scoped to one open Search window. It is retained when another search is run while the dialog remains open, but resets to **All lists** after Search is closed and reopened. If the previously selected list has no matches in the next result set, the filter falls back to **All lists**.

Date ranges are typed like `10/3 - 10/8`. Each side accepts `m/d`, `m/d/yy`, or `m/d/yyyy`; dates without a year use the current year. Time ranges are typed like `9am - 9pm` and keep the same flexible 12-hour parsing as before. A normal hyphen, en dash, or em dash can separate the two values.

The compact Date/Time form uses the same two fields on desktop and mobile, leaving more room for results while keeping date-only, time-only, date/time, and overnight-time searches.

## Deep links

Canonical task links use the task's Universal ID:

```text
http://tasklist.lehighradio.com:8711/task/1643
```

TaskList resolves the correct list and visible task ID, switches to **All**, scrolls to the task, and briefly highlights it. Deep links survive authentication redirects.

The old query-string deep-link format is no longer supported.

## Markdown import

Import preserves arbitrary checklist nesting. Root groups are imported bottom-up so older roots receive lower visible IDs and Universal IDs, while descendants keep their source order.

Source reference: https://community.obsidian.md/plugins/obsidian-tasks-plugin

`➕ YYYY-MM-DD` becomes Created and `✅ YYYY-MM-DD` becomes Completed. Missing source dates are stored as `Unknown` where applicable. Imported items use `Unknown` for Last updated when the source has no update timestamp.

## Keyboard behavior

In task, subtask, and list description boxes:

- **Enter** saves
- **Shift+Enter** inserts a new line

## PWA behavior

TaskList uses a network-first service worker for same-origin application assets. API requests are never cached by the service worker.

The shell cache contains the login/main frontend files, shared theme and milestone files, generated version file, manifest, and icons. Cached files are used only as an offline fallback when a network request fails.

## Security notes

TaskList is a single-user application.

- Passwords are stored only as salted PBKDF2-SHA256 hashes.
- Authentication cookies are HttpOnly and SameSite=Strict.
- Use HTTPS when the application crosses an untrusted network.
- Prefer LAN/VPN/private reverse-proxy access when possible.
- Keep `data/`, database copies, and auth files private.
- The setup/login rate limiter permits 5 attempts per source IP per minute.

## Current release: v1.5.20

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

- Add list archives, Move Task, Universal-ID deep links, Search → View highlighting, advanced date/time search, network-first PWA behavior, full-viewport layout, and the current recursive schema.
- Remove legacy query-string deep links, old migration helpers, and release-specific compatibility code.

Older release-by-release details remain available in the Git commit history and version tags.
