# z3nDash

z3nDash is a dashboard for schedules, ZennoPoster nodes, logs, HTTP traffic and a set of supporting tools.

The dashboard itself runs on the machine that started it: the embedded server listens on `localhost` only. The nodes it drives can be anywhere on the network.

The windowed build targets Windows. The `net10.0` target builds without WinForms: there is no window, the dashboard address is printed to the console and opened in the system browser. Off Windows only [[System Snapshot]] is unavailable — it reads Windows counters and services.

## Start-up

1. The application loads `appsettings.secrets.json`.
2. If a database is configured, z3nDash connects to it and opens Tasker.
3. If there is no configuration, Config opens instead.
4. The embedded HTTP server listens on port `33333` by default.

## Pages

| Page | Purpose | Hotkey |
|---|---|---|
| [[Tasker]] | Schedules and manual task runs | `Alt+1` |
| [[ZP7]] | Task state and control through ZP nodes | `Alt+2` |
| [[ZB]] | ZennoBoxer API data and processes | `Alt+3` |
| [[Logs]] | Shared ZP7 log history, a window on [[ZP7]] | `Alt+4` |
| [[Traffic]] | HTTP traffic and replay, a window on [[ZP7]] | `Alt+5` |
| [[HAR]] | HAR archive viewer and replay | — |
| [[JSON]] | JSON viewing and analysis | `Alt+6` |
| [[Text Tools]] | Text transforms | `Alt+7` |
| [[Treasury]] | Web3 asset overview | — |
| [[Config]] | Database, server, OmniRoute and watchdog | `Alt+0` |
| [[Clips]] | Reusable clipboard templates | `Alt+C` |
| [[System Snapshot]] | Windows state snapshots | — |
| [[dllGraph]] | C# code graph | `Alt+G` |
| [[docsVault]] | This vault | `Alt+H` |
| [[SQLite]] | Viewer for a chosen SQLite file | — |

## Storage

z3nDash supports:

- PostgreSQL through a single connection string;
- SQLite through a path to a file.

For PostgreSQL the schema comes from `Search Path` in the connection string. Tables required by the active handlers are created automatically.

## AI

AI requests go through OmniRoute. Its address is set in [[Config]].

## Navigation

The shared dock is described in [[Nav Dock]]. The list in this document mirrors `wwwroot/js/navpath.js`.

## First run

- [[01. Setting up the server]]
- [[02. Adding a ZennoPoster worker on the LAN]]
