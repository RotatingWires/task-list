> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# TaskList v1.5.7

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
                       milestone notification storage, triggers, baselining, and claim API
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
  milestones.js        milestone claim/dialog/confetti behavior
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

`TaskList.csproj` is the authoritative application version.

The `GenerateWebVersion` MSBuild target writes `wwwroot/version.js` from `$(Version)` before each build. The checked-in `wwwroot/version.js` is also updated on every release so the browser can display the current frontend version after a normal Git pull even when an existing executable has not been rebuilt yet.

The service worker uses a stable shell-cache name and network-first asset requests, so it does not need another duplicated release number.

## Run

```powershell
dotnet restore
dotnet run --urls "http://0.0.0.0:8711"
```

The database is stored at `data\task-list.db` and authentication settings are stored at `data\auth.json`.

## Login and API authentication

On first launch, TaskList prints a random setup token in the server console. The setup screen requires that token plus a new password of at least 8 characters and password confirmation.

The plaintext password is never stored. `auth.json` contains a random salt and PBKDF2-SHA256 password hash.

After login, ASP.NET Core issues an HttpOnly, SameSite=Strict authentication cookie. Login and first-run setup are rate-limited per source IP to 5 attempts per minute.

## Themes

Use **View → Theme** to choose either **Light** or **Dark**. The choice is stored in `localStorage` and applies to both the main TaskList UI and the login/setup screen.

The dark theme keeps the same Windows 95 raised/recessed visual language rather than replacing it with a modern flat theme. Theme styling is centralized in `wwwroot/theme.css`, and `wwwroot/theme.js` applies the saved choice before the UI fully renders so the page does not briefly flash the wrong theme.

There is intentionally no automatic **System** theme mode; theme selection is strictly Light or Dark.

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

TaskList also keeps an append-only `task_events` table for state history. SQLite triggers record Created, Completed, Cancelled, and Reopened events with the Universal ID, event timestamp, old/new status, list/display hierarchy location, title snapshot, and provenance.

The current `items` row remains the fast current-state model while `task_events` preserves repeated state transitions for historical analysis.

When a pre-v1.5 database first runs under v1.5+, TaskList backfills the older Created/Completed/Cancelled/Reopened timestamps it can recover into the event log. Repeated old state cycles that were never stored cannot be reconstructed exactly; transitions recorded after the event log exists are retained individually.

TaskList expects the current recursive `items` schema. The old pre-v1.1 `tasks`/`subtasks` migration path has been removed.

## Milestone celebrations

TaskList keeps the shared milestone-notification ledger in the same SQLite database used by TaskList Stats. The notification rules now mirror the permanent milestone families in Stats:

- global Created / Completed / Cancelled / Reopened milestones at 1, 100, 500, 1,000, 2,000, 3,000, 5,000, and 10,000, then every 5,000 events indefinitely
- overall recorded-event milestones on the same global schedule
- Universal ID milestones at #1, #100, #500, #1,000, #2,000, #2,500, #3,000, #5,000, and #10,000, then every 5,000 IDs indefinitely
- per-list Created / Completed milestones at 100, 500, 1,000, 2,000, and 5,000, then every 5,000 events indefinitely
- yearly Created / Completed milestones at the first event, 100, 500, 1,000, and 2,000, then every 5,000 events indefinitely within that calendar year

TaskList maintains lightweight milestone counters with SQLite triggers as events are recorded. Reaching a qualifying threshold creates one pending notification row rather than rescanning all history after every action. Counter state is rebuilt from the event log at startup so it stays aligned with the authoritative history.

A real milestone is acknowledged globally: TaskList and TaskList Stats both use an atomic claim operation that sets the notification's viewed time and viewer. Whichever app claims it first shows the celebration, and the other app does not repeat it. If several milestones are reached by the same action, they are returned together and shown in one dialog with one confetti animation.

When an older TaskList database first gains a newer notification family, milestones that were already reached are baselined as viewed instead of generating retroactive celebration spam. Existing genuinely pending notifications are preserved.

The celebration is dependency-free, responsive on desktop and mobile, adapts to Light and Dark themes, and suppresses confetti when the browser requests reduced motion. The permanent analytical milestone timeline remains in TaskList Stats even after a popup has been acknowledged.

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

Open tasks use **Complete** as the primary action. To prevent accidental completions on touch screens, **Complete must be held for 800 milliseconds**. Mobile Safari/iOS uses an explicit touch hold path, while mouse/pen uses pointer events; a normal synthesized tap/click is always suppressed. While Complete is held, the button fills from left to right using the same theme-aware blue used for desktop task-row hover feedback. Releasing, cancelling the touch, or moving away before the hold finishes cancels the action. The hold duration is controlled by the single `COMPLETE_HOLD_MS` constant in `wwwroot/tasks.js`, so it can be changed without editing the styling.

Done and Cancelled tasks use **Reopen** normally, without the hold requirement. **Edit** stays directly accessible.

The attached More menu contains actions such as:

- Add Subtask
- Cancel
- Move...
- Delete

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

The hierarchy metrics are calculated against the task's actual source list/tree, including when Task Information is opened from Search results. Non-obvious metrics include hover/focus help, with tap/keyboard support on mobile.

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

## Current release: v1.5.7

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
