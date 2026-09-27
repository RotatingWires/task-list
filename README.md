# TaskList v1.3.5

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

## v1.3.5

- Standardized the TaskList product name directly in source instead of relying on a runtime branding compatibility layer.
- Removed legacy query-string deep links and the old open.html/open.js redirect path; deep links are now only `/task/<UniversalID>`.
- Removed branding.js because the canonical source now contains the current TaskList name.
- Preserved login return-to-task behavior through the authenticated `/task/<UniversalID>` route.
- Bumped Windows file/product/assembly version and the PWA cache to v1.3.5.

## v1.3.4

- Standardized the product name to **TaskList** throughout the current browser/PWA UI, login flow, deep-link opening page, project product metadata, service-worker cache, and documentation.
- Updated browser, Apple PWA, manifest, titlebar, login, and Help/About branding.
- Windows file/product/assembly version and PWA cache are v1.3.4.

## v1.3.3

- Added clean `/task/<UniversalID>` routes for external task links while preserving the old query-string deep-link format for compatibility.
- TaskList resolves the correct list and visible task ID from the Universal ID.
- Deep-link scrolling matches Search → View and aligns the target task below the sticky header.
- Deep links survive authentication redirects.
- Windows file/product/assembly version and PWA cache are v1.3.3.

## v1.3.2

- Added external task deep links using list/view/task targeting.
- Deep links preserve themselves through login, select the requested list, switch to **All**, scroll to the task, and briefly highlight it.
- Universal ID is the primary lookup key with visible ID as a fallback.
- Normal list/view navigation clears stale deep-link parameters.
- Windows file/product/assembly version and the PWA cache are v1.3.2.

## v1.3.1

- Search results use the list/task-number line as the clickable Task Information link, matching the main task table's clickable task-ID convention.
- Search result task titles are plain text again.
- **View** switches to the result's list and **All** view and offsets the scroll position below the sticky table header.
- Windows file/product/assembly version and PWA cache are v1.3.1.

## v1.3

- Search fields use browser-native required-field validation.
- Search result task titles open Task Information only and do not change the current list or view.
- Restored the **View** button in search results. View switches to the result's list, changes the main view to **All**, and scrolls the matching task to the top without opening Task Information.
- Windows file/product/assembly version and PWA cache are v1.3.

## v1.2.10

- Search results no longer use a separate View button. Clicking the task title performs the same navigation/info action.
- Search result titles use the same retro blue underlined clickable treatment as task IDs.

## v1.2.9

- Task Information status text is selectable without changing main task-table selection behavior.
- Parent-task labels use an em dash (`—`).
- Task Information shows the immediate parent task directly under the task ID when viewing a subtask.

## v1.2.8

- Added immediate-parent task number/title information for subtasks in Task Information.

## v1.2.7

- Fixed one-, two-, and three-character Keyword Searches so literal substring matches work as expected.
- Fuzzy typo matching remains for longer terms.
- Menu selection dots use the preferred 8px size.

## v1.2.6

- Enlarged the selected-item radio marker used by File, View, and Search list menus.
- Replaced the tiny text bullet with a CSS selection dot.

## v1.2.5

- Deepened the Win95-style bevel and pressed state on the Search list dropdown.

## v1.2.4

- Replaced Search's native List selector with a custom Win95-style dropdown matching the app's View/File menus.
- Date Search accepts `m/d` or `mm/dd` without a year and uses the browser/device's current year.

## v1.2.3

- Added a **List** filter directly below the Search summary.
- Search still runs across every list first; the dropdown filters the displayed result set afterward.

## v1.2.2

- Date-search fields use the normal text keyboard on iOS so `/` can be entered.
- Search-result action button is labeled **View** instead of **Open**.

## v1.2.1

- Added **File → Search...**.
- Search covers every list, nested subtasks, and all statuses.
- Keyword search supports substring and lightweight fuzzy matching.
- Date search filters Created dates inclusively.

## v1.1.9

- On mobile, the main app window starts below a minimum 20px top safe zone so newer iPhones do not blur the blue TaskList titlebar into the system status-bar area.
- The reserved area above the app is the normal gray window background.

## v1.1.8

- Mobile uses a full-width divider when a subtask group ends and the next row is a root task.

## v1.1.7

- Desktop subtask dividers stay inside the inset subtask block.

## v1.1.6

- Desktop subtasks use the same depth-based left inset treatment as mobile.

## v1.1.5

- Subtasks use a slightly darker inset background and tighter vertical spacing.
- Nested subtasks are increasingly indented by depth.

## v1.1.3

- Normal login no longer applies the 8-character browser `minlength` validation; that minimum is only applied while creating the first-run password.

## v1.1.2

- Subtask hierarchy/status line appears on desktop as well as mobile.

## v1.1.1

- Updated subtask hierarchy colors for Open vs Done/Cancelled states.

## v1.1.0

- Added unlimited nested subtasks.
- Replaced separate `tasks` and `subtasks` tables with one recursive `items` table.
- Visible task IDs are stored directly in SQLite as `display_id`.
- Added automatic one-time migration and timestamped pre-migration database backup for v1.0.x databases.
- Added **Add Subtask** to every task level.
- Markdown import preserves arbitrary nesting.
- Recursive delete removes all descendants and warns with the descendant count.
