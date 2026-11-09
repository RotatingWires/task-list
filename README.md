> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# TaskList v1.7.41

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

If you have a backup, use **File > Import/Export...** before creating tasks: restoring a ZIP replaces all current lists, tasks, and history. Otherwise choose **Start Fresh**. Create a list (**File > Manage Lists... > Create a List...**) and start adding tasks.

Runtime data lives in `data/tasklist.db`; the password hash is in `data/auth.json`. Use **File > Import/Export...** to download a backup.

To free disk space later, **File > Fresh Start...** clears only what you pick (finished tasks, archived lists, everything, old history, or pre-restore recovery backups) and compacts the database. Download a backup first if you might want that data back.

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

See the manual for [authentication](MANUAL.md#authentication), [backup and restore](MANUAL.md#import-and-export), [Fresh start](MANUAL.md#fresh-start), and [all features](MANUAL.md#nested-subtasks).

## Sibling apps

TaskList, TaskList Stats, NewsReview, and SpendTracker are four small self-hosted apps with the same Windows 95 look, sign-in, backups, and keyboard use. Each runs on its own and keeps its own data.

- [TaskList](https://github.com/RotatingWires/task-list): tasks, nested subtasks, and lists.
- [TaskList Stats](https://github.com/RotatingWires/task-list-stats): statistics and history for TaskList, read without changing it.
- [NewsReview](https://github.com/RotatingWires/news-review): a personal news reader with AI review of your sources.
- [SpendTracker](https://github.com/RotatingWires/spend-tracker): purchases, refunds, shared costs, and paybacks.

## Credits

**View > Use Retro Font** uses [W95FA](https://fontsarena.com/w95fa-by-alina-sava/) by Alina Sava, bundled in `wwwroot/fonts/` under the SIL Open Font License 1.1 (`wwwroot/fonts/OFL.txt`).
