# Task List v0.8

A small self-hosted task-list application for a Windows NAS and web/PWA clients.

## Stack

- ASP.NET Core / C# minimal API
- SQLite via Microsoft.Data.Sqlite
- Plain HTML, CSS, and JavaScript
- No React, Node.js, npm, Electron, Bootstrap, or Entity Framework

## Run

```powershell
dotnet restore
dotnet run --urls "http://0.0.0.0:8711"
```

Then open `http://localhost:8711` on the NAS or `http://NAS-IP:8711` from another device on the LAN/VPN.

## Data

The database is stored at:

```text
data\task-list.db
```

When upgrading from v0.4 or earlier, an existing `data\todo.db` is automatically renamed to `data\task-list.db` on first launch.

Existing tasks are migrated into a default list named `Tasks`. Their visible task numbers are preserved. New lists each maintain their own task-number sequence starting at `#1`.

Every task and subtask also receives a global Universal ID. Universal IDs increment across every list and are never reused; they are visible only in Task Information.

## Lists

Use **File** to switch lists, create a list, or open **Manage Lists...**. Each list has a name and optional description. Deleting a list deletes the tasks and subtasks inside it. The final remaining list cannot be deleted.

## Markdown import

Import always targets the currently selected list. Basic nested checklists are supported:

```markdown
- [ ] Parent task
  - [x] Completed subtask
  - [ ] Open subtask
```

One displayed subtask level is currently supported; deeper nested checklist items are treated as subtasks of the current parent.


## Keyboard behavior

In task/subtask/list description boxes, **Enter** saves the form and **Shift+Enter** inserts a new line.


## v0.8 changes

- Obsidian import now reads `➕ YYYY-MM-DD` as the creation date and `✅ YYYY-MM-DD` as the completion date.
- Missing creation dates import as `Unknown`; completed items with no completion date import as `Unknown`.
- Date-only imports display as dates without timezone shifting.
- **Manage Lists...** now shows the highest allocated Universal ID as `(UID) total entries` at the far right of its title bar.
- PWA cache bumped to v8.


## v0.8 Markdown import

Obsidian checklist dates are supported: `➕ YYYY-MM-DD` is imported as Created and `✅ YYYY-MM-DD` as Completed. Missing creation dates are stored as `Unknown`; completed checklist items with no completion date get `Unknown` for Completed. Nested checklist items import as subtasks.
