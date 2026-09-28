# TaskList v1.3.17

A small self-hosted task-list application that runs on Windows, Linux, and macOS, with web/PWA clients.

## Stack

- ASP.NET Core / C# minimal API
- SQLite via Microsoft.Data.Sqlite
- Plain HTML, CSS, and JavaScript
- Built-in ASP.NET Core cookie authentication
- No React, Node.js, npm, Electron, Bootstrap, Entity Framework, or ORM

## Run

```powershell
dotnet restore
dotnet run --urls "http://0.0.0.0:8711"
```

The database is stored at `data\task-list.db` and authentication settings are stored at `data\auth.json`.

## Login and API authentication

On first launch, TaskList prints a random setup token in the server console. The setup screen requires that token plus a new password of at least 8 characters and password confirmation. The plaintext password is never stored; `auth.json` contains a random salt and a PBKDF2-SHA256 password hash.

After login, ASP.NET Core issues an HttpOnly, SameSite=Strict authentication cookie. Login and first-run setup are rate-limited per source IP to 5 attempts per minute.

## Data model

Tasks and subtasks live in one recursive `items` table. The visible task ID is stored directly as `display_id`:

```text
174
174.1
174.1.1
174.1.1.1
```

`parent_display_id` points to the immediate parent in the same list. The composite primary key `(list_id, display_id)` allows different lists to have their own `#1`, `#1.1`, and so on.

Every item also has a global Universal ID. Universal IDs increment across all lists and are never reused. Each item has its own `next_child_number`, so nesting is not limited to one subtask level.

TaskList now expects this current recursive `items` schema. The old pre-v1.1 `tasks`/`subtasks` migration path has been removed.

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

## Deep links

Canonical task links use the task's Universal ID:

```text
http://tasklist.lehighradio.com:8711/task/1643
```

TaskList resolves the correct list and visible task ID, switches to **All**, scrolls the task to the top divider position used by Search → View, and briefly highlights it. Deep links survive authentication redirects.

The old query-string deep-link format is no longer supported.

## Markdown import

Import preserves arbitrary checklist nesting. Root groups are imported bottom-up so older roots receive lower visible IDs and Universal IDs, while descendants keep their source order.

`➕ YYYY-MM-DD` becomes Created and `✅ YYYY-MM-DD` becomes Completed. Missing source dates are stored as `Unknown` where applicable. Imported items use `Unknown` for Last updated when the source has no update timestamp.

## Keyboard behavior

In task, subtask, and list description boxes, **Enter** saves and **Shift+Enter** inserts a new line.

## v1.3.17

- Replace the Move Task dialog's browser-native list selector with the same Windows 95-style custom menu used elsewhere in TaskList.
- Use the existing radio-dot indicator for single-choice dropdown menus.
- Move the **Move...** action after **Edit** in task and subtask action rows.
- Change task-visit highlighting to a CSS class so nested-task flashes stop at the red/green hierarchy line instead of painting to the absolute left edge.
- Keep Search → View and Universal-ID deep-link highlighting behavior otherwise unchanged.
- Bump TaskList UI, assembly, README, About text, and PWA shell-cache metadata to v1.3.17.

## v1.3.16

- Make **Search → View** use the same scroll-and-highlight behavior as Universal-ID deep links, so the selected result visibly flashes after TaskList switches to its list.
- Add **Move...** to task and subtask actions, with a destination-list dialog.
- Moving an item assigns the destination list's next top-level task ID while preserving its Universal ID, title, description, status, timestamps, and child-number counters.
- Move an item's entire descendant subtree atomically; descendant visible IDs are remapped under the new root while keeping the same hierarchy and Universal IDs.
- Moving a subtask to another list makes it a top-level task there, because each list has its own independent visible-ID namespace.
- Perform moves inside one SQLite transaction so a failed destination insert cannot leave a task half-moved.
- Bump TaskList UI, assembly, README, About text, and PWA shell-cache metadata to v1.3.16.

## v1.3.15

- Keep the Search dialog itself fixed while allowing the Search content area to scroll when the Date/Time controls need more vertical room.
- Reserve a real minimum viewport for search results so the Date/Time form can no longer squeeze the results pane down to a nearly invisible line.
- Lay out Date/Time fields in two compact rows on laptop/desktop widths while preserving the stacked mobile layout.
- Keep the Close button outside the scrolling Search content so it remains reachable at all times.
- Bump TaskList UI, assembly, README, About text, and PWA shell-cache metadata to v1.3.15.

## v1.3.14

- Add **File → Refresh** to reload the TaskList PWA/page without closing it from the app switcher; the current network-first service worker then checks the server for the latest app shell.
- Give the Search dialog a fixed viewport-aware height so adding results no longer grows the outer dialog beyond the screen.
- Keep the Search title, tabs, filters, and Close button fixed while only the results pane scrolls internally.
- Remove the old desktop/mobile result-height caps that allowed the dialog itself to become the scrolling container.
- Bump TaskList UI, assembly, README, About text, and PWA shell-cache metadata to v1.3.14.

## v1.3.13

- Make date-plus-time searches treat the date range and clock-time range as separate filters applied to every matching day.
- A search such as `9/1` through `9/27` with `10 AM` through `12 PM` now returns only tasks created between 10:00 AM and 12:00 PM on dates inside that range.
- Preserve overnight clock windows such as `9 PM` through `2 AM` for both time-only and date-plus-time searches.
- When only Start time is supplied with dates, treat it as a daily lower bound; when only End time is supplied, treat it as a daily upper bound.
- Remove the extra search-results sentence explaining that date-only imported records are excluded when a time filter is active.
- Bump TaskList UI, assembly, README, About text, and PWA shell-cache metadata to v1.3.13.

## v1.3.12

- Make both Date fields optional in Search → Date/Time.
- When both dates are blank, require Start time and End time and search that clock-time window across every date.
- Support time-only ranges that cross midnight, such as `9 PM` through `2 AM`.
- Continue to exclude imported date-only creation records whenever a clock-time filter is used, because those records have no confirmed time.
- Require either both dates or neither date so a partially specified date range cannot be misinterpreted.
- Preserve date-only and date-plus-time searches from v1.3.11.

## v1.3.11

- Renamed the Search date tab to **Date/Time** and added optional Start time and End time fields while preserving date-only searches.
- Accept common 12-hour inputs such as `9 PM`, `9PM`, `9:10 PM`, `9:10`, and case-insensitive AM/PM; a time without AM/PM is interpreted as AM.
- Treat an omitted Start time as the beginning of the start date and an omitted End time as the end of the end date.
- Exclude imported date-only creation records only when a time filter is actually used, because their clock time is unknown; date-only searches continue to include them.
- Keep Date/Time search scoped to the task creation timestamp, matching the existing Date search behavior.

## v1.3.10

- Changed the TaskList service worker from cache-first to network-first for current same-origin app assets, reducing stale mixed-version UI after upgrades while retaining cached fallback behavior when the network is unavailable.
- Kept API requests out of the service-worker cache and limited app-asset handling to same-origin GET requests.
- Removed the unused `wwwroot/icons/icon.svg` source asset; the active favicon, Apple touch icon, and PWA PNG icons remain.
- Bumped TaskList UI, assembly, About text, README, and PWA cache metadata to v1.3.10.

## v1.3.9

- Made the TaskList application window fill the full browser viewport on desktop and mobile.
- Removed the outer page gutter/background so the TaskList window itself reaches every edge of the viewport.
- Kept task scrolling inside the existing task panel and preserved the current mobile task-card layout.

## v1.3.8

- Replaced the browser-alert Help window with a custom TaskList retro dialog.
- Preserved the existing Help wording and `about.lehighradio.com` reference.
- Removed the old alert handler instead of leaving it behind as hidden compatibility code.

## v1.3.7

- Folded `/task/<UniversalID>` handling into the normal `app.js` startup/navigation flow.
- Deleted `deep-link.js`; there is no runtime monkey-patching of `loadTasks`, `selectList`, or `setView`.
- Reused one top-aligned task-scroll helper for both deep links and Search → View.
- Removed the old deep-link script from HTML and the service-worker shell cache.
- Removed stale documentation for the retired pre-v1.1 automatic database migration.
- Audited current source for retired query-string/open.html/branding/migration compatibility remnants.

## v1.3.6

- Removed the pre-v1.1 `tasks`/`subtasks` migration path.
- Removed automatic pre-migration database backups, migration verification, legacy-table detection, and migration-only helper methods.
- Simplified database initialization to create/use only the current `lists`, `universal_ids`, and recursive `items` schema.

Older release history remains available in the Git commit history and version tags.
