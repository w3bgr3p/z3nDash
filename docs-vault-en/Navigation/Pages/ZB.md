# ZB

ZB surfaces data from the external ZennoBoxer API.

## Connection

The address comes from `ApiConfig.ZbHost`. When it is empty, this is used:

```text
http://localhost:8160
```

The token is passed to the upstream service in the `Api-Token` header.

## Data

The interface proxies ZB data, including profiles, proxies, instances and threads. Which fields appear depends on what the external API returns.

## Local operations

- read process uptime by PID;
- terminate a whole process tree by PID.

## API

- `/zb/api/*` — proxy to the configured ZB host;
- `GET /zb/process/uptime?pids=...`;
- `POST /zb/process/kill`.
