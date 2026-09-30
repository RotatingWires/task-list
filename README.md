> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# TaskList v1.4.5

TaskList is a small self-hosted task manager with recursive subtasks, multiple lists, search, Markdown import, archives, Universal-ID deep links, a Windows 95-style interface, authentication, and PWA support.

It runs as an ASP.NET Core server with a plain HTML/CSS/JavaScript frontend. There is no Node.js build step or frontend framework.

## Stack

- ASP.NET Core / C# minimal API
- .NET 10
- SQLite via Microsoft.Data.Sqlite
- Plain HTML, CSS, and JavaScript
- Built-in ASP.NET Core cookie authentication and rate limiting
- Service worker + web manifest for PWA behavior
- No React, Node.js, npm, Electron, Bootstrap, Entity Framework, or ORM

## Project layout

TaskList intentionally keeps a small source tree. Frontend code is split by responsibility rather than by release version.

```text
TaskList.csproj        .NET project, dependencies, and authoritative release version
Program.cs             server, authentication, SQLite schema, and API
README.md              setup, architecture, behavior, security, and release notes

data/                  runtime/private data
  task-list.db         SQLite database
  auth.json            salted password hash
  siri-api.json        optional local runtime data

wwwroot/
  index.html           main application shell and dialogs
  login.html           login / first-run password setup
  version.js           generated from TaskList.csproj during build
  app.js               shared DOM/state/API/helpers
  tasks.js             task rendering, actions, edit/subtask/move/import behavior
  lists.js             list selection, creation, management, archives
  search.js            keyword/date-time search and Search list filtering
  ui.js                menus, navigation, startup, deep-link startup, PWA registration
  login.js             login/setup behavior
  style.css            general Win95-style application/login layout
  task-controls.css    task-specific Move/action control styling
  sw.js                network-first service worker
  manifest.webmanifest PWA metadata
  icons/                PWA/favicon assets
```

There are no release-specific JavaScript override files. New behavior should be implemented in the module that owns it rather than by reassigning/wrapping existing functions.

## Version handling

`TaskList.csproj` is the authoritative application version.

The `GenerateWebVersion` MSBuild target writes `wwwroot/version.js` from `$(Version)` before each build. Both the main app and login screen read that generated value, so their visible version labels do not have to be updated independently.

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

TaskList expects the current recursive `items` schema. The old pre-v1.1 `tasks`/`subtasks` migration path has been removed.

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

Open tasks use **Complete** as the primary action. Done and Cancelled tasks use **Reopen**. **Edit** stays directly accessible.

The attached More menu contains actions such as:

- Add Subtask
- Cancel
- Move...
- Delete

Moving a task preserves its Universal ID, title, description, status, timestamps, child numbering, and descendant hierarchy while assigning a new visible top-level ID in the destination list.

## Search

**File → Search...** supports:

- fuzzy keyword/title/description search across active lists
- creation-date ranges
- optional creation-time ranges
- time-only searches across all dates
- overnight time windows
- task and subtask results
- filtering the current result set by list
- **View** to open the correct list, switch to All, scroll to the item, and highlight it

The Search list filter is intentionally scoped to one open Search window. It is retained when another search is run while the dialog remains open, but resets to **All lists** after Search is closed and reopened. If the previously selected list has no matches in the next result set, the filter falls back to **All lists**.

Dates accept `m/d`, `m/d/yy`, or `m/d/yyyy`. Dates without a year use the current year.

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

The shell cache contains the login/main frontend files, generated version file, manifest, and icons. Cached files are used only as an offline fallback when a network request fails.

## Security notes

TaskList is a single-user application.

- Passwords are stored only as salted PBKDF2-SHA256 hashes.
- Authentication cookies are HttpOnly and SameSite=Strict.
- Use HTTPS when the application crosses an untrusted network.
- Prefer LAN/VPN/private reverse-proxy access when possible.
- Keep `data/`, database copies, and auth files private.
- The setup/login rate limiter permits 5 attempts per source IP per minute.

## Release history

### v1.4.5

- Removed the release-specific `search-retain-v1-4-4.js` override and integrated Search filter retention directly into `search.js`.
- Split the former 55 KB all-in-one frontend into purpose-based `app.js`, `tasks.js`, `lists.js`, `search.js`, and `ui.js` files.
- Kept Search list selection only while the Search dialog remains open, with fallback to All lists when the selected list has no matches.
- Removed release-specific inline CSS from `index.html` and moved task-control refinements into the normal stylesheet layer.
- Made `TaskList.csproj` the authoritative version source and generate `wwwroot/version.js` at build time for both main/login UI labels.
- Removed the stale hardcoded login version and eliminated duplicated main/About version strings.
- Replaced versioned service-worker cache naming with a stable network-first shell cache and updated the shell for the purpose-based frontend files.
- Preserved current task/list/search/archive/deep-link/authentication behavior and kept `Program.cs` intact as one server file.

### v1.4.4

- Keep desktop task-row divider lines continuous through the Actions column.
- Preserve the existing split Complete/Reopen control, More menu, Edit alignment, and mobile task-card layout.
- Retain the selected Search list when another keyword or Date/Time search is run while the Search dialog remains open.
- Reset Search to **All lists** after the Search dialog is closed and opened again.
- Fall back to **All lists** if the previous list is not available in the new result set.
- Bump TaskList UI, assembly metadata, About text, and PWA shell-cache metadata to v1.4.4.

### v1.4.3

- Give the Move Task destination selector a stronger raised bevel so it stands apart from the surrounding dialog background.
- Add an inner highlight/shadow and harder outer shadow to give the selector more depth.
- Strengthen the opened destination menu shadow while preserving existing Move behavior and sizing.
- Bump TaskList UI, assembly metadata, About text, and PWA shell-cache metadata to v1.4.3.

### v1.4.2

- Return Reopen to its natural compact width instead of stretching it to match Complete.
- Keep Complete at the fixed v1.4.1 width so Open-task action rows remain stable.
- Preserve the v1.4.1 vertical alignment fix and the v1.4 split-action behavior.
- Bump TaskList UI, assembly, README, About text, and PWA shell-cache metadata to v1.4.2.

### v1.4.1

- Vertically align the standalone Edit button with the split Complete/Reopen control.
- Give Complete and Reopen the same fixed primary-button width so status changes do not shift the action layout.
- Preserve the v1.4 split-action behavior and all existing task actions.
- Bump TaskList UI, assembly, README, About text, and PWA shell-cache metadata to v1.4.1.

### v1.4

- Replace the crowded per-task action row with a Windows 95-style split action: the current primary status action remains one click, with an attached arrow for less-common commands.
- Show **Complete** as the one-click primary action for Open tasks and **Reopen** for Done or Cancelled tasks.
- Keep **Edit** as a separate one-click button.
- Move **Add Subtask**, **Cancel** (when applicable), **Move...**, and **Delete** into the attached More-actions menu.
- Keep disabled Move behavior when no other active destination list exists.
- Reduce the desktop Actions column width now that each row only needs the split action plus Edit.
- Preserve all existing task status, edit, move, delete, archive, hierarchy, and Universal-ID behavior.

### v1.3.18

- Add list archiving with an **Archive** action beside Edit in Manage Lists.
- Hide archived lists from the File list picker, normal Manage Lists view, search scope, and move destinations while preserving every task and subtask.
- Add **Archives...** at the lower-left of Manage Lists with a dedicated archived-list browser.
- Allow archived lists to be opened in the normal task view and restored to the active list set without changing their IDs or task data.
- Prevent archiving or deleting the only active list, while allowing archived lists to remain safely outside the normal list picker.
- Add a schema migration for the `lists.archived` flag so existing databases upgrade in place on startup.
- Keep Universal-ID deep links able to resolve tasks inside archived lists.

### v1.3.17

- Replace the Move Task dialog's browser-native list selector with the same Windows 95-style custom menu used elsewhere in TaskList.
- Use the existing radio-dot indicator for single-choice dropdown menus.
- Move the **Move...** action after **Edit** in task and subtask action rows.
- Change task-visit highlighting to a CSS class so nested-task flashes stop at the red/green hierarchy line instead of painting to the absolute left edge.
- Keep Search → View and Universal-ID deep-link highlighting behavior otherwise unchanged.

### v1.3.16

- Make **Search → View** use the same scroll-and-highlight behavior as Universal-ID deep links.
- Add **Move...** to task and subtask actions with a destination-list dialog.
- Preserve Universal IDs and descendant hierarchy during moves.
- Remap descendant visible IDs under the new destination root.
- Make a moved subtask a top-level task in its destination list.
- Perform moves inside one SQLite transaction.

### v1.3.15

- Keep the Search dialog fixed while allowing its content area to scroll.
- Reserve a real minimum viewport for search results.
- Lay out Date/Time fields compactly on desktop while preserving the stacked mobile layout.
- Keep the Close button outside the scrolling Search content.

### v1.3.14

- Add **File → Refresh**.
- Give Search a fixed viewport-aware height.
- Keep Search title/tabs/filters/Close fixed while results scroll internally.
- Keep the network-first service worker update behavior.

### v1.3.13

- Apply date and clock-time ranges as separate filters for every date in the selected range.
- Support daily time windows and overnight clock windows.
- Support one-sided daily time bounds when dates are supplied.

### v1.3.12

- Make both Search date fields optional.
- Support time-only searches across all dates.
- Require both dates or neither date.
- Exclude date-only imported records when a clock-time filter is used.

### v1.3.11

- Rename Search's date tab to **Date/Time**.
- Add optional Start time and End time.
- Accept common 12-hour time formats.
- Keep Date/Time search scoped to task creation timestamps.

### v1.3.10

- Change the service worker from cache-first to network-first for current same-origin app assets.
- Keep API requests out of the service-worker cache.
- Remove the unused old SVG app-icon source asset.

### v1.3.9

- Make the application window fill the browser viewport on desktop and mobile.
- Keep scrolling inside the task panel.

### v1.3.8

- Replace browser-alert Help with a custom retro dialog.
- Remove the old alert handler.

### v1.3.7

- Fold `/task/<UniversalID>` handling into normal app startup/navigation.
- Delete the old `deep-link.js` runtime override.
- Reuse one scroll/highlight helper for deep links and Search → View.
- Remove stale migration/query-string/open.html compatibility remnants.

### v1.3.6

- Remove the pre-v1.1 `tasks`/`subtasks` migration path.
- Remove migration-only backups, verification, legacy-table detection, and helpers.
- Use only the current `lists`, `universal_ids`, and recursive `items` schema.

Older release history remains available in the Git commit history and version tags.
