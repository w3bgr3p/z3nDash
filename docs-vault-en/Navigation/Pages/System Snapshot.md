# System Snapshot

System Snapshot collects a diagnostic snapshot of the current Windows system.

## Refresh

Opening the page starts a capture automatically on the machine z3nDash runs on. `Refresh` collects current data again. The page includes the sections the backend actually managed to gather: processes, network and system data.

The page data lives in memory only.

## Environment

The first item in the left navigation, not part of the snapshot. It lists the tools
this machine has for running tasks, showing what the command, registry or connection
actually returned. A check takes a few seconds, so it runs on its own only the first
time the section opens; after that, use `Check`. A missing tool has an `Install`
button: the installer opens in a separate console window; press `Check` when it finishes.

An open Environment section survives `Refresh` and the next visit to the page.

## AI audit

An optional audit sends the current page data to OmniRoute. The cache lives in `system_snapshot_ai_cache`; a cached report is not loaded automatically when the page opens.

## API

- `POST /system-snapshot/capture`
- `POST /system-snapshot/ai-audit`
- `GET /system-snapshot/ai-cache`
- `DELETE /system-snapshot/ai-cache`
- `GET /config/environment`
- `POST /config/environment/install`
