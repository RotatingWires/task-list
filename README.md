# Task List v1.0.5

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

Open `http://localhost:8711` on the NAS or `http://NAS-IP:8711` from another device on the LAN/VPN.

The database is stored at `data\task-list.db`.
Authentication settings are stored at `data\auth.json`.

## Login and API authentication

On first launch, Task List prints a random setup token in the server console. Opening Task List displays a setup screen that requires that token plus a new password of at least 8 characters and password confirmation. The plaintext password is never stored. `auth.json` contains a random salt and a PBKDF2-SHA256 password hash.

After login, ASP.NET Core issues an HttpOnly, SameSite=Strict authentication cookie. Normal same-origin browser requests automatically send that cookie with every Task List API request. All task/list/import/stat API routes require authentication.

The cookie is persistent for up to 30 days and uses sliding expiration. Its Secure flag follows the request: it works over HTTP for the current LAN-only setup and will automatically be marked Secure when the app is accessed over HTTPS.

Login and first-run setup are rate-limited per source IP to 5 attempts per minute. Excess requests receive HTTP 429 until the fixed one-minute window resets.

Use **File > Log Out** to invalidate the browser session. If you intentionally need to reset the password, stop Task List, delete `data\auth.json`, and restart it. A new setup token will be printed in the server console. If Task List normally runs hidden under Task Scheduler, run it interactively once so you can see the token.

## Data model

Each list has its own visible task-number sequence starting at `#1`. Subtasks use the parent number plus a decimal, such as `#12.1`.

Every task and subtask also receives a global Universal ID. Universal IDs increment across every list and are never reused; they are shown only in Task Information.

The current database schema is created directly on first run. This build contains no legacy schema migrations or backwards-compatibility conversion code.

## Markdown import

Import targets the currently selected list. Parent task groups are imported from the bottom of the Markdown file upward so older source entries receive lower Task IDs and Universal IDs. Subtasks within a parent keep their original top-to-bottom order from the Markdown file. Nested checklist items become subtasks:

```markdown
- [ ] Parent task ➕ 2026-09-12
  - [x] Completed subtask ➕ 2026-09-12 ✅ 2026-09-14
```

`➕ YYYY-MM-DD` becomes Created and `✅ YYYY-MM-DD` becomes Completed. Missing source dates are stored as `Unknown` where applicable. Imported items use `Unknown` for Last updated because the source does not contain that information.

One displayed subtask level is supported; deeper nested checklist items are flattened under the current parent.

## Keyboard behavior

In task, subtask, and list description boxes, **Enter** saves and **Shift+Enter** inserts a new line.

## v0.8.4

- Added single-user password setup and login.
- Added HttpOnly ASP.NET Core cookie authentication.
- All normal `/api` task/list/stat/import endpoints now require authentication.
- Added **File > Log Out**.
- Added a dedicated retro login/setup screen.
- Passwords are stored only as PBKDF2-SHA256 hashes with random salts.
- Kept the clean current-only database schema from v0.8.3.x.
- Windows file/product/assembly version is `0.8.4`.
- Targets `win-x64`, framework-dependent.
- PWA cache is v8.4.


## v0.8.5

- Normal login now displays only the password field.
- First-run password creation still requires Password and Confirm password.
- Fixed the CSS rule that caused the confirmation field to appear even when the HTML `hidden` attribute was set.
- Windows file/product/assembly version is `0.8.5`.
- PWA cache is v8.5.


## v1.0

- Parent tasks are displayed newest-first (highest Task ID first).
- Markdown parent groups still import bottom-up, so older parent tasks receive lower Task IDs and Universal IDs.
- Imported subtasks now keep their top-to-bottom order from the Markdown file instead of being reversed.
- Normal task and subtask creation behavior is unchanged.
- Windows file/product/assembly version is `1.0`.
- PWA cache is v1.0.


## v1.0.3

- Based on v1.0 (does not include the discarded Brave visualViewport experiment).
- Fixed login/create-password window inheriting the main app full-viewport height on desktop.
- Login window now sizes to its content with a viewport max-height fallback.
- PWA cache is v1.0.3.

## v1.0.3

- Login/create-password window is forced to content height on mobile instead of inheriting the full app-shell height.
- Main PWA top safe-area strip now matches the blue title bar.
- Theme color updated to classic Windows blue.


## v1.0.5

- Manage Lists title now shows the current number of lists, for example `Manage Lists (2)`.
- The lifetime Universal ID counter now uses comma thousands separators, for example `3,141 lifetime entries counting deletions`.

## v1.0.4

- Added per-IP rate limiting to login and first-run setup: 5 attempts per one-minute fixed window.
- Added a one-time first-run setup token generated with a cryptographically secure random number generator.
- The setup token is printed only in the server console and is required before the initial password can be created.
- The setup token changes on every restart until setup is completed, and is discarded after successful setup.
- Added setup-token input to the first-run screen and a friendly HTTP 429 message for rate limits.
- Preserved the final local About text and mobile safe-area CSS changes.
- Windows file/product/assembly version is `1.0.5`.
- PWA cache is v1.0.5.
