# RetroTodo v0.4

A deliberately small self-hosted todo/ticket app built with ASP.NET Core, SQLite, plain HTML, CSS, and JavaScript.

## Run

```powershell
dotnet restore
dotnet run --urls "http://0.0.0.0:8711"
```

Then open `http://localhost:8711` on the server or `http://SERVER-IP:8711` from another device on the LAN/VPN.

The SQLite database is created at `data/todo.db`.

## v0.4 changes

- Long task titles wrap instead of forcing horizontal scrolling.
- New tasks leave **Last updated** blank until the task is actually changed.
- Adds one-level subtasks with permanent IDs such as `#12.1`, `#12.2`, etc. Deleted subtask numbers are not reused.
- Subtasks have their own Open/Done/Cancelled status, timestamps, Edit/Delete, and status actions.
- A parent task controls which View contains the entire group. For example, all of `#12`'s subtasks remain visible with `#12` in Done even if individual subtask statuses differ.
- Parent deletion also deletes its subtasks.
- Markdown import recognizes indented checklist items as subtasks.

Example import:

```markdown
- [ ] Main task
  - [ ] First subtask
  - [x] Finished subtask
- [x] Another main task
  - [ ] This subtask follows the Done parent into the Done view
```

The importer currently supports one displayed subtask level. Any checklist item indented beneath the current parent is imported as that parent's subtask.

## Existing database

Keep your existing `data/todo.db` when upgrading. v0.4 creates the new `subtasks` table automatically and preserves existing task IDs.
