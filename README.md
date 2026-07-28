# DevDeck

DevDeck is a local Windows control center for automation workflows. It combines
task scheduling, ZennoPoster and ZennoBrowser operations, logs, HTTP inspection,
system snapshots, Web3 tools, and day-to-day utilities in one embedded dashboard.

The desktop application is built with .NET 10, WinForms, and WebView2. Its
dashboard and local API are served by the built-in HTTP server.

## What is included

- Task scheduler with manual runs, recurring schedules, process control, live
  output, payloads, and several script/executable runners.
- ZP7 worker overview and control for ZennoPoster jobs on the local network.
- ZennoBrowser profile and process integration.
- Application logs, HTTP request inspection, and request replay.
- JSON, text, clipboard-template, code-graph, and SQLite file-viewer tools.
- System snapshots and a configurable process-memory watchdog.
- Web3 treasury views and supporting blockchain utilities.
- Built-in documentation available from the dashboard.
- OmniRoute integration for AI-assisted features.

## Requirements

- Windows 10 or Windows 11, x64.
- PostgreSQL and a database user allowed to create and use the selected schema.
- Microsoft Edge WebView2 Runtime.
- .NET 10 SDK when building from source. The repository pins SDK `10.0.103`
  and allows later .NET 10 feature-band versions.

ZennoPoster, ZennoBrowser, and OmniRoute are required only for the dashboard
features that integrate with those services.

PostgreSQL stores DevDeck application data. The SQLite page in the dashboard is
a standalone viewer for local SQLite files; it is not the default DevDeck
datastore.

## Build from source

```powershell
git clone https://github.com/w3bgr3p/DevDeck.git
cd DevDeck
dotnet restore DevDeck.sln
dotnet build DevDeck.csproj -c Release -f net10.0-windows
```

To create a self-contained Windows build:

```powershell
dotnet publish DevDeck.csproj `
  -c Release `
  -f net10.0-windows `
  -r win-x64 `
  --self-contained true `
  -o publish/DevDeck
```

Build output and installers are intentionally excluded from Git.

## First launch

The embedded server uses port `10993` by default. Because DevDeck registers an
`HttpListener` wildcard prefix, reserve the URL once from an elevated terminal:

```powershell
netsh http add urlacl url=http://*:10993/ user=Everyone
```

Start the application:

```powershell
dotnet run --project DevDeck.csproj -f net10.0-windows
```

When no valid configuration exists, DevDeck opens:

```text
http://localhost:10993/?page=config
```

In **Config**:

1. Select `PostgreSQL`.
2. Enter the complete PostgreSQL connection string.
3. Set the dashboard port and storage folders if the defaults are unsuitable.
4. Add ZennoBrowser and OmniRoute endpoints when those integrations are used.
5. Save the configuration.

Example connection string:

```text
Host=127.0.0.1;Port=5432;Database=devdeck;Username=devdeck;Password=change-me;Search Path=devdeck
```

`Search Path` selects the PostgreSQL schema. If it is omitted, DevDeck uses
`public`.

After configuration, the application connects to PostgreSQL and opens the
Scheduler. Startup failures are written to `crash.log` next to the executable.

## Configuration and secrets

The Config page writes `appsettings.secrets.json` next to the executable and
reloads it without requiring manual JSON edits. The file contains the database
connection string and integration settings.

`appsettings.secrets.json`, its backups, local environment files, IDE settings,
logs, databases, build output, and installers are ignored by Git. Never commit a
real configuration file or paste its contents into an issue.

## Dashboard

The default dashboard address is `http://localhost:10993`.

| Section | Purpose |
|---|---|
| Scheduler | Configure, run, stop, and monitor scheduled tasks |
| ZP7 | Manage ZennoPoster workers and jobs |
| ZB | Work with ZennoBrowser profiles and processes |
| Logs | Inspect application logs |
| HTTP | Inspect and replay HTTP requests |
| JSON / Text | Transform and analyze structured or plain text |
| Clips | Store and copy reusable templates |
| Treasury | Inspect Web3 assets |
| System | Capture and compare system state |
| Graph | Explore C# source relationships |
| Config | Configure PostgreSQL, services, storage, and watchdog |
| Docs | Open the bundled DevDeck documentation |

The navigation dock also shows the current keyboard shortcuts for these pages.

## Network safety

DevDeck is designed for a trusted local machine or private network. Its embedded
server listens on all interfaces and exposes operational endpoints. Do not
publish the dashboard port directly to the internet. Restrict access with the
Windows firewall or place it behind an authenticated reverse proxy.

## Repository layout

```text
App/Config/   configuration models and loader
Controllers/  embedded server, scheduler, and runtime services
Handlers/     dashboard and local API handlers
Sql/          PostgreSQL access and schema helpers
Csx/          C# script execution
Browser/      browser automation abstractions
Api/          external service integrations
Web3/         blockchain and wallet utilities
wwwroot/      dashboard HTML, CSS, and JavaScript
docs-vault/   bundled product documentation
templates/    dashboard and report templates
```

## License

DevDeck is distributed under the
[GNU Affero General Public License v3.0](LICENSE).
