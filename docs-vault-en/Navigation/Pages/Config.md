# Config

The `/config.html` page manages the local z3nDash configuration.

The side rail switches between Overview, Database, Logs & Server, Browsers API,
OmniRoute, Services, Security, Maintenance and Interface. The last open section is restored
on the next visit.

## Server Status

Shows:

- the configuration state;
- the database connection;
- the dashboard port;
- the log and report folders;
- the selected database mode.

The data is loaded through `GET /config/status`.

## Database

Two modes are supported:

- PostgreSQL — a single connection string field;
- SQLite — a path to a file.

For PostgreSQL the schema comes from `Search Path`. The connection string can be copied with the button beside the field.

## Logs & Server

The dashboard port and the working folders are set here.

The server listens on `localhost` only. Do not change that: the routes require no
authentication and allow starting processes and reading secrets.

## Browsers API

Local API address and token for two browsers:

- **ZennoBrowser** — `http://localhost:8160` by default, the token is sent in the `Api-Token` header;
- **ShardX** — `http://127.0.0.1:40325` by default, the token (the permanent JWT from the
  launcher settings) is sent as `Authorization: Bearer`.

The [[Browser]] page and xml tasks with the `shardx` browser mode use these settings.

## OmniRoute

OmniRoute is the only AI provider. The field holds the service URL, `http://localhost:20128` by default.

`Validate & Save AI` stores the address and checks that `/v1/models` answers. The result is shown in a toast.

## Security

The jVars section stores encrypted local variables and the path to the jVars file.

## Interface

- the theme (`Alt+T` still cycles through the themes);
- the screen edge for the [[Nav Dock]] (`Alt+P` moves it to the next edge);
- **Dock pages** — which pages the dock shows. Config cannot be removed from the
  dock, otherwise the dock would offer no way back to the settings.

Changes apply at once, with no save button, and affect every page. They are kept
in `ui-state.json` next to the executable: `theme`, `dockPosition` and
`dockHidden`, the list of hidden pages. A page added to `navpath.js` later shows
up in the dock on its own.

`POST /config/ui` merges the posted fields into the stored ones, so a request may
carry only the field that changed.

The machine tool check (Environment) has moved to the [[System Snapshot]] page.

## Memory Watchdog

Configured here:

- the root process name;
- whether the watchdog is on;
- a memory limit in MB;
- a check interval.

The watchdog counts the memory of the root process together with its child tree. Once a non-zero limit is exceeded, the process tree is terminated.

## Storage

Shows the size and the number of files in the log folder — including `trafficLog.jsonl`
and its rotated copies.

There is a single cleanup, `POST /clear-all-logs`, which clears the whole folder.

## Import

Available imports:

- proxies;
- EVM addresses;
- SOL addresses.

They use `POST /import/proxy` and `POST /import/addresses`.

## API

- `GET /config`
- `POST /config`
- `GET /config/status`
- `GET /config/storage`
- `POST /config/jvars`
- `POST /config/ai-validate`
- `GET /config/ai-models`
- `GET /config/ui`
- `POST /config/ui`
