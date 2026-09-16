# RetroTodo v0.3

A deliberately small self-hosted todo/ticket app for a Windows NAS.

## Stack

- ASP.NET Core 10 Minimal API
- Microsoft.Data.Sqlite (raw SQLite; no Entity Framework)
- Plain HTML
- Plain CSS
- Vanilla JavaScript
- PWA manifest/service worker

No Node.js, npm, React, Electron, or frontend framework.

## Run

```powershell
dotnet restore
dotnet run --urls "http://0.0.0.0:8711"
```

Then open `http://localhost:8711` on the server, or `http://NAS-IP:8711` from another device on the LAN/VPN.

The SQLite database is created automatically at `data/todo.db`.

## v0.3 features

- Permanent numeric task IDs
- Open / Done / Cancelled states
- View menu: Open, Done, All
- Complete, Cancel, Reopen, Edit, Delete actions
- Title + description
- Clickable task IDs with an information dialog
- Created, last-updated, completed, cancelled, and reopened timestamps
- Automatic in-place migration from the v0.1 database schema
- Basic Markdown checkbox import
- Installable PWA shell

The Markdown importer is intentionally basic until the long-running Markdown file format is finalized.


## v0.3 changes

- Removed the toolbar Import Markdown button; import remains available from the top Import menu.
- UI chrome, buttons, headings, task IDs, and statuses are non-selectable; task titles and task information values remain selectable.
- In the desktop description editor, Enter saves while Ctrl+Enter/Command+Enter inserts a new line. On iPhone/iPad, Return inserts a new line normally.
- View menu selection indicator changed from a checkmark to a bullet point.
