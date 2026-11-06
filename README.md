> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# TaskList v1.7.6

A small self-hosted task manager with recursive subtasks, multiple lists, search, backup/restore, and a Windows 95-style interface with Light/Dark themes.

**[Full manual](MANUAL.md)** — complete setup, features, screenshots, API reference, architecture, and release notes, preserved from the original README.

## Quickstart

Requires the **.NET 10 SDK**. Clone the repository and run:

```sh
git clone https://github.com/RotatingWires/task-list.git
cd task-list
dotnet restore
dotnet run
```

TaskList listens on **http://127.0.0.1:8711** unless `--urls` (or `ASPNETCORE_URLS`) chooses another address. Open it in a browser. Use the setup token printed in the server console to choose and confirm a password of at least 8 characters, then log in.

If you have a backup, use **File → Import/Export...** before creating tasks: restoring a ZIP replaces all current lists, tasks, and history. Otherwise choose **Start Fresh**. Create a list and start adding tasks.

Runtime data lives in `data/task-list.db`; the password hash is in `data/auth.json`. Use **File → Import/Export...** to download a backup.

## Security and privacy

TaskList is a single-user application.

- Passwords are stored only as salted PBKDF2-SHA256 hashes with 600,000 iterations. A hash saved with fewer iterations is upgraded the next time you log in.
- Authentication cookies are HttpOnly and SameSite=Strict.
- Use HTTPS when the application crosses an untrusted network.
- Prefer LAN/VPN/private reverse-proxy access when possible.
- Keep `data/`, database copies, and auth files private.
- The setup/login rate limiter permits 5 attempts per source IP per minute.

The quickstart uses plain HTTP on loopback. HTTP does not encrypt passwords, session cookies, or task data in transit. For LAN access, start it with `dotnet run --urls "http://0.0.0.0:8711"` and connect using the server's actual address; use HTTPS across untrusted networks.

Exports and pre-restore recovery ZIPs are unencrypted and contain private task/history data. Store them privately. The first-run setup token is printed only to the interactive console, not the runtime log; passwords are not logged.

See the manual for [authentication](MANUAL.md#login-and-api-authentication), [backup and restore](MANUAL.md#import-and-export), and [all features](MANUAL.md#nested-subtasks).
