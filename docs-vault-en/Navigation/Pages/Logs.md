# Logs

The shared ZP7 history opens with the `allLogs` button on the [[ZP7]] page,
the `/?page=zp7#allLogs` link and the `Alt+4` hotkey.

- Every registered node, or one chosen machine, regardless of the selected task.
- Filters by project, level, thread and module; full-text search.
- Column sorting, the full record and copying a message.
- Execution, Errors and Critical for ZennoPoster or ProjectMaker.
- Refreshes every 3 seconds while the window is open; it can be paused.
- Errors from individual nodes are shown next to the results of the reachable ones.

Data is loaded through `GET /zp/nodes?probe=false` and `GET /zp/log`.
The current API returns up to 2000 records from the last 512 KB of the file on each
node. This is the recent history reachable over the API, not a full archive. The
project filter is applied on the node before the record limit; the remaining filters
and the sorting apply to the records already loaded. Errors and Critical carry no
project field.

Logs are read from the nodes.
