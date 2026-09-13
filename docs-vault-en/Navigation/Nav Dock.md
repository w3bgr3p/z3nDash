# Nav Dock

The dock is pulled in by every page through `wwwroot/js/nav.js`. Its items and hotkeys live in `wwwroot/js/navpath.js`.

## Items

| ID | Page | Hotkey |
|---|---|---|
| `tasker` | [[Tasker]] | `Alt+1` |
| `zp7` | [[ZP7]] | `Alt+2` |
| `zb` | [[ZB]] | `Alt+3` |
| `har` | [[HAR]] | — |
| `json` | [[JSON]] | `Alt+6` |
| `text` | [[Text Tools]] | `Alt+7` |
| `treasury` | [[Treasury]] | — |
| `config` | [[Config]] | `Alt+0` |
| `clips` | [[Clips]] | `Alt+C` |
| `system` | [[System Snapshot]] | — |
| `dllGraph` | [[dllGraph]] | `Alt+G` |
| `docsVault` | [[docsVault]] | `Alt+H` |
| `sql` | [[SQLite]] | — |

## Hotkeys without a dock item

These open modal windows on the [[ZP7]] page:

| Hotkey | Window | Link |
|---|---|---|
| `Alt+4` | [[Logs\|allLogs]] | `/?page=zp7#allLogs` |
| `Alt+5` | [[Traffic\|traffic]] | `/?page=zp7#traffic` |

## Dock hotkeys

These are bound in `nav.js` rather than in the item list, and they open nothing:

| Hotkey | What it does |
|---|---|
| `Alt+X` | the OTP generator |
| `Alt+T` | cycles the theme |
| `Alt+P` | moves the dock to the next edge |

## Also in the dock

The dock carries an OTP generator. The secret stays on the current page and is used only to compute a one-time code.
