# Task List v0.8.3

A small self-hosted task-list application for a Windows NAS and web/PWA clients.

## Stack

- ASP.NET Core / C# minimal API
- SQLite via Microsoft.Data.Sqlite
- Plain HTML, CSS, and JavaScript
- No React, Node.js, npm, Electron, Bootstrap, Entity Framework, or ORM

## Run

```powershell
dotnet restore
dotnet run --urls "http://0.0.0.0:8711"
```

Open `http://localhost:8711` on the NAS or `http://NAS-IP:8711` from another device on the LAN/VPN.

The database is stored at `data\task-list.db`.

## Data model

Each list has its own visible task-number sequence starting at `#1`. Subtasks use the parent number plus a decimal, such as `#12.1`.

Every task and subtask also receives a global Universal ID. Universal IDs increment across every list and are never reused; they are shown only in Task Information.

The current database schema is created directly on first run. This build contains no legacy schema migrations or backwards-compatibility conversion code.

## Markdown import

Import targets the currently selected list. Nested checklist items become subtasks:

```markdown
- [ ] Parent task ➕ 2026-09-12
  - [x] Completed subtask ➕ 2026-09-12 ✅ 2026-09-14
```

`➕ YYYY-MM-DD` becomes Created and `✅ YYYY-MM-DD` becomes Completed. Missing source dates are stored as `Unknown` where applicable. Imported items use `Unknown` for Last updated because the source does not contain that information.

One displayed subtask level is supported; deeper nested checklist items are flattened under the current parent.

## Keyboard behavior

In task, subtask, and list description boxes, **Enter** saves and **Shift+Enter** inserts a new line.

## v0.8.3

- Removed legacy database migration and compatibility code.
- Removed the obsolete `completed` boolean from the current schema and all application logic; `status` is the single source of truth.
- Consolidated SQLite command creation, parameter binding, scalar execution, and non-query execution into shared helpers.
- Consolidated repeated task/subtask status buttons into one shared status-action definition.
- Windows file/product/assembly version is `0.8.3`.
- Targets `win-x64`, framework-dependent.
- PWA cache is v8.3.
