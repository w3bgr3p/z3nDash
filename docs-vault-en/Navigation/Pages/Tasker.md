# Tasker

Tasker is the `/tasker.html` page that stores schedules and runs local tasks.

## List

Tasks are read from the `schedules` table. The list shows the name, the state and the main run parameters.

## Editing

A task is defined by:

- a name;
- an executor;
- a path to a script, file or task;
- arguments;
- the `enabled` state;
- a schedule;
- an overlap policy;
- a payload.

Supported executors:

| Executor | What it runs |
|---|---|
| `python`, `node`, `ts-node`, `npm` | scripts of the matching runtimes |
| `exe`, `cmd`, `bat`, `bash`, `ps1` | executables and shell scripts |
| `csx` | a C# script |
| `csx-internal` | a C# script with access to z3nDash internals; the task list carries a build button for it |
| `xml` | a ZennoPoster template: the built-in runtime plays the XML graph, and the browser is chosen on the task card |
| `internal` | a built-in z3nDash task |

## Schedule

The UI can build:

- on-demand runs;
- daily runs;
- runs on days of the week;
- runs on a day of the month;
- an interval in minutes.

The result is stored in `cron`, `interval_minutes` and `fixed_time`.

## Overlap

- `skip` — do not start a new instance while one is active;
- `parallel` — allow several instances up to `max_threads`;
- `kill_restart` — stop the active instances and start a new one.

## Control

Available actions:

- Run;
- Stop;
- Restart;
- enable or disable the schedule;
- delete and duplicate;
- open the file or its folder;
- open a terminal;
- check and install Python and Node dependencies.

## Output

Output of the active run is streamed over SSE. Parallel instances each get their own `runId`.

## Traffic

A task tab with traffic from `trafficLog.jsonl`:
`GET /traffic?tail=N&task_id=...`, polled every 3 seconds. ZpRuntime writes it;
details are in [[Traffic]]. The file rotates itself.

## Payload

A payload consists of a schema and values. It can be edited, imported, exported and passed to the executor on start.

## API

Control from a running script without looking up an ID: [[Task API]].
Deferring the next run, pausing and resuming the schedule are available.

Main routes:

- `GET /tasker/list`
- `POST /tasker/save`
- `POST /tasker/delete`
- `POST /tasker/run`
- `POST /tasker/stop`
- `GET /tasker/instances`
- `POST /tasker/kill-instance`
- `GET|POST /tasker/payload`
- `GET /tasker/output/stream`
- `GET /tasker/queue`
- `POST /tasker/clear-queue`
