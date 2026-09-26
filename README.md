# Task List v1.2.6

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

## v1.2.6

- Enlarged the selected-item radio marker used by the File list menu, View menu, and Search list filter.
- Replaced the tiny text bullet with a 10px solid CSS circle centered in the existing menu-check column for a closer Windows 95 look.
- The marker uses the menu text color, so it automatically turns white when the selected row is highlighted blue.
- Windows file/product/assembly version is `1.2.6`.
- PWA cache is v1.2.6.

## v1.2.5

- Made the Search result List filter use a deeper Win95-style bevel with a dark outer outline, inner highlight/shadow, and stronger lower-right drop shadow so the top and left edges stay visible against the gray dialog background.
- The opened/pressed state keeps the same stronger outer edge while reversing the inner bevel.
- Windows file/product/assembly version is `1.2.5`.
- PWA cache is v1.2.5.

## v1.2.4

- Replaced Search's native List selector with a custom Win95-style dropdown matching the app's View/File menus. It still filters only the already-loaded cross-list search results.
- Date Search now accepts `m/d` or `mm/dd` with no year; those forms automatically use the current year from the browser/device. Existing `m/d/yy`, `mm/dd/yy`, and four-digit-year input still works.
- Updated the date-field hint to show that the year is optional.
- Windows file/product/assembly version is `1.2.4`.
- PWA cache is v1.2.4.

## v1.2.3

- Added a **List** filter directly below the Search summary. Search still runs across every list first; the dropdown only filters the displayed result set afterward.
- The List dropdown contains **All lists** plus each list represented in the current results, with the number of matching items from that list.
- Search summaries now count the lists that actually contain matches instead of always using the total number of lists.
- Windows file/product/assembly version is `1.2.3`.
- PWA cache is v1.2.3.

## v1.2.2

- Date-search fields now use the normal text keyboard on iOS so `/` can be entered in dates such as `9/25/26`.
- Search-result action button is now labeled **View** instead of **Open**.
- Search behavior is otherwise unchanged and still runs across all lists.
- Windows file/product/assembly version is `1.2.2`.
- PWA cache is v1.2.2.

## v1.2.1

- Added **File → Search...** immediately after Manage Lists.
- Search always runs across every task list, including nested subtasks and all statuses.
- **Keyword** search checks task titles and descriptions and uses lightweight fuzzy matching for partial words and small typos. No search library or new dependency was added.
- **Date** search filters the Created date inclusively between Start date and End date. It accepts `m/d/yy`, `mm/dd/yy`, and also four-digit years.
- Search results show the list, visible task ID, title, description, status, and creation date. **Open** switches to that list in All view and opens Task Information.
- Preserved the current 20px mobile top safe-zone adjustment and solid `#c8c8c8` task separators.
- Windows file/product/assembly version is `1.2.1`.
- PWA cache is v1.2.1.

## v1.1.9

- On mobile, the main app window now starts below a minimum 20px top safe zone so newer iPhones do not blur the blue Task List titlebar into the system status-bar area.
- Removed the old blue safe-area `::before` overlay; the reserved area above the app is now the normal gray window background.
- Preserved the user's solid `#c8c8c8` base task separators instead of the older dotted separators.
- Windows file/product/assembly version is `1.1.9`.
- PWA cache is v1.1.9.

## v1.1.8

- Mobile now uses a full-width divider when a subtask group ends and the next row is a root task.
- The last subtask drops its inset bottom divider at that boundary, avoiding the left-side gap shown before the next root task.
- Desktop behavior from v1.1.7 is unchanged.
- Preserved hierarchy/status colors: Open `#e02d04`; Done/Cancelled `#02bd34`.
- Windows file/product/assembly version is `1.1.8`.
- PWA cache is v1.1.8.

## v1.1.7

- Desktop subtask dividers now stay inside the inset subtask block instead of extending left past the colored hierarchy/status line.
- A full-width divider is kept only at the boundary between a root task and its first/last subtask group.
- Mobile subtask styling is unchanged.
- Preserved hierarchy/status colors: Open `#e02d04`; Done/Cancelled `#02bd34`.
- Windows file/product/assembly version is `1.1.7`.
- PWA cache is v1.1.7.

## v1.1.6

- Desktop subtasks now use the same depth-based left inset treatment as mobile.
- Desktop hierarchy/status lines move inward with the subtask block instead of staying at the outer table edge.
- Subtask blocks now have a top divider as well as a bottom divider, making their boundaries clearer beside neighboring tasks.
- Preserved hierarchy/status colors: Open `#e02d04`; Done/Cancelled `#02bd34`.
- Windows file/product/assembly version is `1.1.6`.
- PWA cache is v1.1.6.

## v1.1.5

- Based on v1.1.3; does not include the abandoned parent-ID enlargement experiment.
- Subtasks now use a slightly darker inset background and tighter vertical spacing so they read as children of the parent task.
- Nested subtasks are increasingly indented by depth on both desktop and mobile.
- Mobile subtask rows are inset as attached blocks instead of occupying the full top-level row width.
- Preserved hierarchy/status colors: Open `#e02d04`; Done/Cancelled `#02bd34`.
- Windows file/product/assembly version is `1.1.5`.
- PWA cache is v1.1.5.

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
