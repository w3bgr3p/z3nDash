# z3nIO

Cross-platform automation orchestrator with embedded web dashboard.  
Built on .NET 10. Targets `net10.0-windows` (WinForms overlay + WebView2) and `net10.0` (headless / Linux).

---

## Requirements

| Component | Version |
|---|---|
| .NET SDK | 10.0 |
| OS | Windows 10+ / Linux (Ubuntu 22+) |
| PostgreSQL *(optional)* | 14+ |
| SQLite *(default)* | bundled |
| ZennoBrowser *(optional)* | any, with WS endpoint exposed |

---

## Build

```bash
# Windows (WinForms overlay, WebView2, System.Management)
dotnet publish -f net10.0-windows -c Release

# Cross-platform / Linux
dotnet publish -f net10.0 -c Release
```

Both targets use `PublishSingleFile=true` + `SelfContained=true`.  
Output binary is large (~150–300 MB) — that is expected.

### Windows — port registration (required for `http://*:10993/`)

Run once as Administrator before first launch:

```cmd
netsh http add urlacl url=http://*:10993/ user=Everyone
```

Replace `10993` if you changed `DashboardPort` in config.

---

## Configuration

On first launch with no config present, the dashboard opens at:

```
http://localhost:10993/?page=config
```

Fill in the config form and save. This writes `appsettings.secrets.json` next to the binary.

### `appsettings.secrets.json` — key fields

```json
{
  "LogsConfig": {
    "DashboardPort": "10993",
    "LogHost": "http://localhost:10994",
    "TrafficHost": "http://localhost:10995"
  },
  "DbConfig": {
    "Mode": "sqlite",
    "ConnectionString": "Data Source=z3nIO.db"
  }
}
```

`DbConfig.Mode` accepts `sqlite` or `postgres`.

AI provider keys and OmniRoute config are set through the config page (`/config`) and stored in the same file under `AiConfig`.

---

## Run

```bash
./z3nIO          # Linux
z3nIO.exe        # Windows
```

On Windows — opens `DashboardOverlay` (WebView2 WinForms window).  
On Linux — calls `xdg-open http://localhost:10993/`.

Exit: press any key in the terminal.

Startup crash details are written to `crash.log` next to the binary.

---

## Dashboard

Default: `http://localhost:10993`

| Page | Path |
|---|---|
| Scheduler | `/?page=scheduler` |
| Config | `/?page=config` |
| AI | `/?page=ai` |
| Logs | `/?page=logs` |
| HTTP Log | `/?page=http` |
| Treasury | `/?page=treasury` |
| System Snapshot | `/?page=system` |
| ZennoBrowser | `/?page=zb` |
| Cliplates | `/?page=clips` |
| JSON Analyzer | `/?page=json` |
| ZP Orchestrator | `/?page=zp7` |
| Reports | `/?page=report` |

---

## Scheduler

Schedules are stored in `_schedules` table. Queue in `_schedule_queue`.

### Executor types

| Executor | Description |
|---|---|
| `internal` | Registered C# delegate via `RegisterTask` |
| `csx-internal` | Roslyn `.csx` script via `CsxExecutor` |
| `python` | External process |
| `node` / `ts-node` | External process |
| `bash` / `ps1` / `bat` | Shell scripts |
| `exe` | Arbitrary executable |

### Schedule triggers

- `cron` — standard cron expression (via Cronos)
- `interval_minutes` — fixed interval
- `fixed_time` — daily at HH:mm

`on_overlap`: `skip` (default) or `queue`.  
`max_threads`: concurrent run limit per schedule.

---

## Key dependencies

| Package | Version | Purpose |
|---|---|---|
| Microsoft.Playwright | 1.58.0 | Browser automation (CDP / WS endpoint) |
| Nethereum.Web3 | 4.29.0 | EVM chains, wallet, signing |
| Npgsql | 10.0.1 | PostgreSQL driver |
| Microsoft.CodeAnalysis.CSharp.Scripting | 4.13.0 | Roslyn `.csx` executor |
| Cronos | 0.8.x | Cron parsing |
| OpenCvSharp4 | 4.9.0 | Image matching |
| Otp.NET | 1.4.0 | TOTP |
| Newtonsoft.Json | 13.0.4 | JSON serialization |
| Microsoft.Web.WebView2 | 1.0.3800.47 | *(Windows only)* Embedded browser |
| System.Management | 10.0.0-rc | *(Windows only)* WMI / HWID |

---

## Project structure

```
z3nIO/
├── Program.cs                  # Entry point
├── EmbeddedServer.cs           # HttpListener server, handler registry
├── SchedulerService.cs         # Cron/interval/queue executor
├── AiClient.cs                 # AI HTTP client (aiio / OmniRoute)
├── ConfigHandler.cs            # /config endpoints
├── DashboardOverlay.cs         # WinForms WebView2 (WINDOWS only)
├── wwwroot/                    # Static dashboard (HTML/CSS/JS)
│   └── *.html, themes.css, nav.js ...
├── Prompts/                    # AI prompt templates
├── scripts/                    # User .csx / .py / .js scripts
├── templates/                  # Report / output templates
├── appsettings.json            # Base config
└── appsettings.secrets.json    # Secrets (not committed)
```

---

## Notes

- `appsettings.secrets.json` — not committed, must be created on each machine via config UI or manually.
- `WINDOWS` compile constant is set by target framework (`net10.0-windows`), not runtime OS detection.
- Linux HWID reads from `/etc/machine-id`, `/sys/class/dmi/id/board_serial`, `/proc/cpuinfo`.
- `.csx` scripts support `#load` directives; stack traces include real file paths.
