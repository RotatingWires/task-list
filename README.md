# TaskList v1.3.8

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
