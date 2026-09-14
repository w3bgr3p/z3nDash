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
| `csx` | a C# script; the task list carries a compile-check button for it |
| `xml` | a ZennoPoster template: the built-in runtime plays the XML graph, and the browser is chosen on the task card |

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

## Account for a template run

The `xml` executor reserves an account only when the template actually works with one: the `acc0` variable is declared in the `<Variables>` block **and** mentioned somewhere else — in a step or in the own code. A declaration alone is not enough: ZennoPoster templates carry variables over from a donor project in bulk, and many of them are never read.

Reservation also kicks in when the task payload explicitly sets `accountTable` or `condition`. It is skipped when the payload already carries a non-empty `acc0` — the account was picked by hand.

The table comes from `payload.accountTable`, otherwise it is `__<task name up to the first dot>`. The condition is `payload.condition` (the substring `NOW` is replaced with the current UTC time), `1=1` by default.

A taken account gets `status = 'busy'`, and its columns plus the rows from `instance` and `folder_profile` go into the project variables and into the run payload — that is where the `xml` branch reads the proxy and the browser profile from, so account data overrides the task settings.

The release happens in `finally`: `idle` on success, `fail` on any other exit, including a kill by the runtime limit. A failure of the release itself is logged as a warning and never breaks the run.

Special cases:

- the account table does not exist — a configuration error: status `error`, `last_exit = -1`;
- no free account matches the condition — the run did not happen: `last_exit` stays empty and the `runs_total` / `runs_success` counters are left alone.

## Max runtime

The **Max runtime** field in the task settings (column `timeout_seconds`). `0` means no limit.

Once the limit passes, the run is aborted, a line about the limit goes into the output, and the run counts as a failure (`last_exit = -1`).

How the abort happens depends on the executor:

- an external process (`python`, `node`, `cmd`, `npm`, …) is killed together with its process tree — a real stop;
- `internal`, `csx-internal` and `xml` get their `CancellationToken` cancelled. Cancellation is cooperative: `xml` checks the token between branches, and code that never looks at the token will not stop.

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
