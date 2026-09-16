# RetroTodo v0.1

Tiny self-hosted todo app with a retro Windows-style UI.

## Stack

- ASP.NET Core Minimal API (.NET 10)
- Microsoft.Data.Sqlite
- SQLite
- Plain HTML
- Plain CSS
- Plain JavaScript
- PWA manifest + service worker

No React, Node.js, npm, Entity Framework, Electron, Bootstrap, or frontend framework.

## Run on Windows

1. Install the .NET 10 SDK if you want to run from source.
2. Open PowerShell in this folder.
3. Run:

   dotnet restore
   dotnet run --urls "http://0.0.0.0:8711"

4. Browse to:

   http://SERVER-IP:8711

The database is created automatically at:

   data/todo.db

## Current features

- Permanent numeric task IDs (`#1`, `#2`, ...)
- Add tasks
- Complete/reopen tasks
- Edit tasks
- Delete tasks
- Basic Markdown checkbox import (`- [ ]` / `- [x]`)
- Responsive phone/desktop UI
- PWA manifest
- Small offline cache for the application shell only

## Important PWA note

Service workers require a secure context in normal deployment. For iPhone/Home Screen use, configure HTTPS for the server when we deploy it. The API intentionally is not cached, so the app always reads/writes the authoritative SQLite database on the NAS.

## Next design decisions

The Markdown importer is deliberately simple right now. Before importing the real long-running file, define how headings, nested tasks, notes, completed formatting, dates, tags, and IDs should map into RetroTodo.
