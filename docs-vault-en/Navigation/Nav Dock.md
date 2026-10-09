# Nav Dock

The dock is pulled in by every page through `wwwroot/js/nav.js`. Its items and hotkeys live in `wwwroot/js/navpath.js`.

Which of the items to show is chosen in [[Config]] → Interface → Dock pages.
The theme and the screen edge are set there too; the dock has no theme button. `config` cannot be hidden.

## Items

| ID | Page | Hotkey |
|---|---|---|
| `tasker` | [[Tasker]] | `Alt+1` |
| `zp7` | [[ZP7]] | `Alt+2` |
| `browser` | [[Browser]] | `Alt+3` |
| `har` | [[HAR]] | — |
| `zpXml` | [[zpXml]] | — |
| `xml` | [[XML]] | `Alt+9` |
| `json` | [[JSON]] | `Alt+6` |
| `text` | [[Text Tools]] | `Alt+7` |
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
| `Alt+T` | cycles through Graphite, Aluminium, Hyper and Paper |
| `Alt+P` | moves the dock to the next edge |

## Also in the dock

The dock carries an OTP generator. The secret stays on the current page and is used only to compute a one-time code.
