# Browser

Browser manages anti-detect browser profiles. The switch in the header picks
which browser to work with: **ZennoBrowser** or **ShardX**. The choice is kept
in the browser.

## Connection

The address and token come from the Browsers API section of [[Config]]:

| Browser | Setting | Default address | Authorization |
|---|---|---|---|
| ZennoBrowser | `BrowsersApi.ZennoBrowser` | `http://localhost:8160` | `Api-Token` header |
| ShardX | `BrowsersApi.ShardX` | `http://127.0.0.1:40325` | `Authorization: Bearer <token>` |

The token never reaches the page: the z3nDash server adds it while proxying.

## Profiles

The profile table shows name, folder, proxy, tags (ZennoBrowser only), status
and last start time. A running profile shows its PID and uptime instead of the
time.

- **＋ New profile** — create a profile: name, platform, folder, proxy, tags.
  ZennoBrowser: Auto (host OS), Windows 10 or Windows 11. ShardX: Windows,
  macOS or Linux; a fresh fingerprint comes from `/fingerprint/new/{platform}`.
  ShardX accepts a new folder name typed in. ZennoBrowser's built-in
  "without folder" folders are hidden from the form, cloud folders are marked
  `(cloud)`.
- **✎ in the Proxy column** — change the profile's proxy: pick a stored one,
  type a new one or remove it.
- **Start / Stop** — start and stop through the browser API.
- **Kill** — terminate the process by PID, bypassing the API.
- **WS** — copy the CDP endpoint of a running profile. ShardX returns it only
  for profiles started through the API.
- **Stop All** — stop every running profile.

Filters: search by name, proxy and tags, folder, running only.

## Proxies

Stored proxies. ZennoBrowser: name, URI, check status, check time. ShardX:
name, `kind://host:port`, country, id.

## Threads

ZennoBrowser only: busy threads, freed one by one or all at once.

## ShardX specifics

Verified on ShardX Launcher 2.0.3:

- times arrive as `@<unix-seconds>`, not ISO;
- after `stop` a profile stays in `/running` for about 7 seconds, so the page
  re-reads the list until it is gone (15 seconds at most);
- a new proxy string is checked for about 10 seconds, but it is bound even when
  it does not work;
- deleted profiles go to the launcher's trash.

## ZennoBrowser specifics

- a new proxy string is matched against stored proxies by exact URI; when none
  matches, it is created with the name `host:port`;
- proxy changes go through `update_proxy_bulk`, which touches only the proxy;
- a profile without a folder is stored locally in "Profiles without folder".

## API

- `/zb/api/*` — proxy to the ZennoBrowser API;
- `/shardx/api/*` — proxy to the ShardX Launcher API;
- `GET /zb/process/uptime?pids=...` — process uptime by PID;
- `POST /zb/process/kill` — terminate a process tree by PID.
