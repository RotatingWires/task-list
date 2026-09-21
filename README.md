# Task List v1.1.4

A small self-hosted task-list application for a Windows NAS and web/PWA clients.

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

On first launch, Task List prints a random setup token in the server console. The setup screen requires that token plus a new password of at least 8 characters and password confirmation. The plaintext password is never stored; `auth.json` contains a random salt and a PBKDF2-SHA256 password hash.

After login, ASP.NET Core issues an HttpOnly, SameSite=Strict authentication cookie. Login and first-run setup are rate-limited per source IP to 5 attempts per minute.

## v1.1.0 data model

Tasks and subtasks now live in one recursive `items` table. The visible task ID is stored directly in SQLite as `display_id`, so the database contains the same ID shown in the UI:

```text
174
174.1
174.1.1
174.1.1.1
```

`parent_display_id` points to the visible ID of the immediate parent in the same list. A composite primary key of `(list_id, display_id)` allows different lists to each have their own `#1`, `#1.1`, and so on.

Every item also keeps its global Universal ID. Universal IDs continue incrementing across all lists and are never reused.

Each item has its own `next_child_number`, so any task or subtask can receive more children without a fixed nesting limit.

## Automatic upgrade from v1.0.x

When v1.1.0 starts on a v1.0.x database, it automatically detects the old `tasks`/`subtasks` schema. Before changing anything, it copies the database to a timestamped backup such as:

```text
data\task-list-pre-v1.1.0-20260919-153000.db
```

The upgrade then copies parent tasks into `items` using IDs like `174`, copies existing subtasks using IDs like `174.1`, verifies the item and Universal-ID counts, and only then removes the old tables. The conversion runs inside a SQLite transaction, so a failed conversion rolls back instead of leaving a half-migrated database.

## Nested subtasks

Every item can now have subtasks. For example:

```text
#174
├─ #174.1
│  ├─ #174.1.1
│  │  └─ #174.1.1.1
│  └─ #174.1.2
└─ #174.2
```

Deleting an item also deletes all of its descendants. Parent/root status still controls which Open/Done/All view the entire tree appears in.

## Markdown import

Import preserves arbitrary checklist nesting instead of flattening levels deeper than one. Root groups are still imported bottom-up so older roots receive lower visible IDs and Universal IDs, while descendants keep their original source order at every level.

`➕ YYYY-MM-DD` becomes Created and `✅ YYYY-MM-DD` becomes Completed. Missing source dates are stored as `Unknown` where applicable. Imported items use `Unknown` for Last updated because the source does not contain that information.

## Keyboard behavior

In task, subtask, and list description boxes, **Enter** saves and **Shift+Enter** inserts a new line.

## v1.1.4

- Root/parent task ID numbers are larger than subtask ID numbers for easier visual scanning.
- The larger root ID applies even when the task has no subtasks.
- Preserves the preferred hierarchy/status colors: Open `#e02d04`; Done/Cancelled `#02bd34`.
- Windows file/product/assembly version is `1.1.4`.
- PWA cache is v1.1.4.

## v1.1.3

- Normal login no longer applies the 8-character browser `minlength` validation.
- The 8-character minimum is applied only while creating the first-run password.
- Server-side first-run password validation remains unchanged.
- Windows file/product/assembly version is `1.1.3`.
- PWA cache is v1.1.3.

## v1.1.2

- Subtask hierarchy/status line now appears on desktop as well as mobile.
- Open subtasks use `#800e06`; Done and Cancelled subtasks use `#06801b`.
- Desktop applies the line to the first table cell so it renders reliably with the existing table layout.
- Windows file/product/assembly version is `1.1.2`.
- PWA cache is v1.1.2.

## v1.1.1

- Subtask hierarchy line is now `#800e06` for Open subtasks.
- Subtask hierarchy line is now `#06801b` for Done and Cancelled subtasks.
- Windows file/product/assembly version is `1.1.1`.
- PWA cache is v1.1.1.

## v1.1.0

- Added unlimited nested subtasks.
- Replaced separate `tasks` and `subtasks` tables with one recursive `items` table.
- Visible task IDs are now stored directly in SQLite as `display_id`.
- Added automatic one-time migration and timestamped pre-migration database backup for v1.0.x databases.
- Added **Add Subtask** to every task level.
- Add Subtask dialog now shows `Parent Task #ID - task name`.
- Markdown import now preserves arbitrary nesting.
- Recursive delete removes all descendants and warns with the descendant count.
- Preserved login rate limiting, first-run setup token, list-count title, comma-formatted lifetime UID counter, and existing UI behavior.
- Windows file/product/assembly version is `1.1.0`.
- PWA cache is v1.1.0.
