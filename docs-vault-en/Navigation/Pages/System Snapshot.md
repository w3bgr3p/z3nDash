# System Snapshot

System Snapshot collects a diagnostic snapshot of the current Windows system.

## Refresh

Opening the page starts a capture automatically on the machine z3nDash runs on. `Refresh` collects current data again. The page includes the sections the backend actually managed to gather: processes, network and system data.

The page data lives in memory only.

## AI audit

An optional audit sends the current page data to OmniRoute. The cache lives in `system_snapshot_ai_cache`; a cached report is not loaded automatically when the page opens.

## API

- `POST /system-snapshot/capture`
- `POST /system-snapshot/ai-audit`
- `GET /system-snapshot/ai-cache`
- `DELETE /system-snapshot/ai-cache`
