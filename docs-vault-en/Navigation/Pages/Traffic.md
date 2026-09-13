# Traffic

Traffic is a modal HTTP traffic window. It opens with the `traffic` button on the
[[ZP7]] page, the `/?page=zp7#traffic` link and the `Alt+5` hotkey.

Nodes and the local runtime write traffic to a file, and the window reads its tail —
the same way [[Logs|allLogs]] reads logs.

## Sources

The window polls two kinds of source at once and merges the records into one list:

| Source | Written by | File | Read through |
|---|---|---|---|
| z3nDash | ZpRuntime (`Rqst`, `HttpDebugHandler`) | `<logsFolder>/trafficLog.jsonl` | `GET /traffic` |
| ZP node | z3n7 (`Rqst`) | `<ZennoPoster log folder>/trafficLog.jsonl` | `GET /zp/traffic?machine=...` |

Node addresses come from `GET /zp/nodes?probe=false`. An unreachable node does not get
in the way of the others: its error is shown as a line above the list, and records from
the reachable sources are displayed as usual.

Each side keeps one file per machine, written under a named mutex and rotated at 50 MB
with the five most recent files kept. Reading goes from the end: the last N records are
requested, and rotated files are pulled in when the current one is not enough. The scan
ceiling is 64 MB; once it is used up the answer is marked `truncated`.

## List

Filters: machine, project, method, status, a URL substring, and the number of records
per source. The project goes into the request and is filtered while the file is read;
the remaining filters apply to the records already loaded. The window refreshes every
3 seconds while open, and the `Auto` button stops that. `Clear view` resets the window
buffer — the files themselves are untouched.

## Details

The panel shows the URL, method, status, duration, machine, project, account, session,
proxy and `task_id`, plus collapsible Request and Response sections with headers,
cookies and body. Clicking a value copies it.

## Replay

`Play` opens the request in an editor: method, URL, headers and body can be changed
before sending. The request goes through a separate replay listener — `POST /http-replay`,
the same one [[HAR]] uses. The response can be copied, and there is a cURL button next
to it built from the current field contents.

The listener port is always the dashboard port + 1, both on the server and on the front
end; there is no setting for it. It listens on `localhost` only.

## Code

For a selected record, ready-made calls are generated: HttpClient, ZP7, Hybrid, Python,
TypeScript, cURL. `API Skeleton` and `API Example` fold every displayed request into an
endpoint description — either field types or the last real values.

`cURL Import` parses a pasted `curl` command and opens it in the details panel as an
ordinary record: it can be replayed and converted, and it does not appear in the list.

## Record format

```json
{
  "timestamp": "2026-09-12 17:40:11.325",
  "startedDateTime": "2026-09-12T22:40:11.1000000Z",
  "method": "POST",
  "url": "https://example.com/api",
  "statusCode": 200,
  "durationMs": 214,
  "proxy": "",
  "request":  { "headers": [], "contentType": "", "userAgent": "", "cookies": "", "cookiesSource": "", "body": "" },
  "response": { "headers": "", "body": "" },
  "machine": "PC", "project": "", "account": "", "session": "",
  "task_id": "", "source": "z3n7"
}
```

The format is shared by the node and the runtime, so the window parses both sources with
the same code.

## API

- `GET /traffic?tail=N&project=&task_id=` — the local z3nDash file;
- `GET /zp/traffic?machine=&tail=N&project=&task_id=` — forwarded to a node;
- `GET /zp/nodes?probe=false` — the node list;
- `POST /http-replay` — replaying a request (dashboard port + 1).

## Node requirement

The tail of the file is served by the `tail` parameter of `GET /traffic` on the z3n7
side. If a node returns the **first** N records instead of the last ones, it is running
a `z3n7.dll` build without `tail` support.
