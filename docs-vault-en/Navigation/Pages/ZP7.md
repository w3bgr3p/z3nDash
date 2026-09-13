# ZP7

ZP7 shows task state taken directly from the registered ZP node services.

## Data source

z3nDash stores only the node addresses, in the `zp_nodes` table. Task state is requested from each node:

```text
GET http://HOST:PORT/state
```

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

Actions offered by the interface:

- start;
- stop;
- interrupt;
- apply/update settings;
- add/set tries;
- set threads;
- clear done;
- clear fails;
- execution settings;
- scheduler settings.

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
