# ZP7

ZP7 shows task state taken directly from the registered ZP node services.

## Data source

z3nDash stores only the node addresses and their tokens, in the `zp_nodes` table. Task state is requested from each node:

```text
GET http://HOST:PORT/state
Authorization: Bearer <token>
```

The token comes from the node registration line (see [[02. Adding a ZennoPoster worker on the LAN]])
and is attached on the z3nDash side: it never reaches the browser. A node that
answers `401` is marked **Unauthorized** in the list — paste the registration line
again.

The node list in the **NODES** window comes from `GET /zp/nodes` with a probe:
alongside availability it carries the node's `z3n7` and ZennoPoster versions, taken
from `GET /version`. The same data is served on its own by
`GET /zp/version?machine=...`.

The page gets the addresses through `GET /zp/nodes?probe=false`, then polls each node
independently through `GET /zp/state?machine=...` every 3 seconds. Tasks appear as the
answers arrive: a slow or unreachable node does not hold up loading and refreshing the
rest. At most one state request per node runs at a time; the node request timeout is
10 seconds.

## Task list

Filters are available by:

- name;
- state;
- run mode;
- tags.

The chosen filters are kept in the UI state.

## Control

Commands go straight to the selected node:

```text
POST http://HOST:PORT/command
```

The action bar is icons only; each label lives in the tooltip:

| Icon | Action | Command |
|---|---|---|
| green ▶ | start | `start` |
| yellow ❚❚ | pause (the task stops but is not interrupted) | `stop` |
| red ✕ | interrupt | `interrupt` |
| node with two branches | Input Settings | `set_input_settings` |
| ± | tries: a modal with a Set/Add toggle, a value field and +1 / +10 / +100 (they edit the field; Apply sends the command) | `set_tries`, `add_tries` |
| Clear ✓ / ✗ | clear done / clear fails | `clear_success`, `clear_fails` |
| terminal | Execution Settings | `set_execution_settings` |
| clock | Scheduler Settings | `set_scheduler_settings` |

Apply/update settings and set threads come from the Limits panel.

## Panels

- Task Detail — parameters and actions of the selected task;
- Logs — logs of the selected task from its node (`GET /zp/log`);
- allLogs — a modal window with the [[Logs|shared history]] from every node, with filters and sorting;
- traffic — a modal window with [[Traffic|HTTP traffic]] from every node and from z3nDash itself;
- HTTP — traffic of the selected task: `GET /zp/traffic?machine=...&task_id=...`, polled every 3 seconds;
- Projects heatmap — project data from the report API.

## Input Settings

`⚙ Input Settings` opens the project variables. Values come from the node as
`GET /zp/task/settings?machine=&task_id=` — a base64 pair: the original `InputSettings`
XML and the current values.

Two views, switched with the button in the window footer:

- **Form** (default) — widgets by the types in the XML:

  | Type | Widget |
  |---|---|
  | `Tab` | a tab |
  | `Comment` | a section separator |
  | `Label` | a caption with no input |
  | `Boolean` | a checkbox |
  | `Number` | a numeric input |
  | `DropDown`, `Select` | a dropdown |
  | `DropDownMultiSelect` | a set of checkboxes |
  | `Password` | a hidden input |
  | everything else (`Text`, `FileName`, `CaptchaModules`, …) | a text field; longer than 60 characters becomes a textarea |

  Options for the lists live in the caption as a `{a|b|c}` block and are stripped from
  the caption shown. The variable key sits next to the caption, and `Help` is in the
  tooltip.
- **JSON** — a flat object of `{key: string}`.

Switching carries the current values over, so edits are not lost. The chosen view is
remembered. If the XML fails to parse, the window stays in JSON and shows why.

Variables the form does not draw are not lost on save: `Tab` and `Comment` can also be
bound to a variable, so the values are taken from the node whole and only those present
in the form are laid over them.

`📋 Copy Payload` puts a payload for the [[Tasker|scheduler]] on the clipboard: a
`schema` describing the fields and `values` with the current values.

Saving is the same in both views — the `set_input_settings` command with the original
XML and the collected values; the node reassembles the XML itself.

## API

- `GET /zp/nodes`
- `GET /zp/state`
- `GET /zp/state/all`
- `GET /zp/log`
- `GET /zp/traffic`
- `POST /zp/commands`
