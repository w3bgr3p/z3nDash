# System Snapshot

System Snapshot collects a diagnostic snapshot of the current Windows system.

## Refresh

Opening the page starts a capture automatically on the machine z3nDash runs on. `Refresh` collects current data again. The page includes the sections the backend actually managed to gather: processes, network and system data.

The page data lives in memory only.

## Memory allocation

Separate charts show physical RAM and Windows commit. Commit includes the current
allocation, limit, remaining headroom and peak since boot; paged and nonpaged
kernel pools are shown alongside it. Commit usage warns at 90% and becomes
critical at 97%, even when physical RAM is still available.

Process groups show private allocation and resident RAM separately, summed across
instances. Unreadable private-memory measurements are counted so incomplete totals
remain visible. The AI audit receives the same distinction.

## Environment

The first item in the left navigation, not part of the snapshot. It lists the tools
this machine has for running tasks, showing what the command, registry or connection
actually returned. A check takes a few seconds, so it runs on its own only the first
time the section opens; after that, use `Check`. A missing tool has an `Install`
button: the installer opens in a separate console window; press `Check` when it finishes.

An open Environment section survives `Refresh` and the next visit to the page.

## Diagnostic recording

On instrumented dashboard pages, `Ctrl+Shift+D` starts or stops recording.
The recording badge also offers **Report** and **Stop**. Correlated browser and
server events help separate browser waiting, server queueing and handler time,
including database, scheduler-lock and console timings.

Recordings are saved as JSONL in the configured logs folder's `diag` directory.
Stopping writes a readable `.txt` report beside the dump. Reports include slow
requests, route timings and browser stalls; SQL events keep operation and table
names instead of query values.

## AI audit

An optional audit sends the current page data to OmniRoute. The cache lives in `system_snapshot_ai_cache`; a cached report is not loaded automatically when the page opens.

## API

- `POST /system-snapshot/capture`
- `POST /system-snapshot/ai-audit`
- `GET /system-snapshot/ai-cache`
- `DELETE /system-snapshot/ai-cache`
- `GET /config/environment`
- `POST /config/environment/install`
