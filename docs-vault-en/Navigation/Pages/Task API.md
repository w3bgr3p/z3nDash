# Controlling a task from a script

Every run gets its own context. The API address is shared; the task and run IDs are
determined automatically. One file can serve several tasks. There is no need to look a
task up by name or read its ID from the database.

## Python

The `z3ndash` module ships with the application. The scheduler adds it to `PYTHONPATH`
automatically, including runs inside a venv. Installing the package with pip is not
needed.

```python
from z3ndash import current_task

try:
    do_work()
except ServiceUnavailable:
    current_task.defer(seconds=3600, reason="Service unavailable")
    raise
```

`defer()` does not end the script and does not swallow errors. Once the deferral is
confirmed, the script decides for itself whether to finish, release resources or raise.
If the API is unreachable the helper raises: a deferral must not be assumed to be set.

```python
state = current_task.get()       # task_id, run_id, name and the schedule state
current_task.pause()             # Pause automatic runs indefinitely
current_task.resume()            # Lift the pause and any temporary deferral
current_task.defer(until="2026-12-01T12:00:00Z", reason="Until a given time")
```

## C# and XML OwnCode

In `csx-internal` and in XML OwnCode blocks the `current_task` object is available:

```csharp
await current_task.DeferAsync(3600, "Service unavailable");
var state = await current_task.GetAsync();
```

In a synchronous block: `current_task.DeferAsync(3600).GetAwaiter().GetResult();`.
Inside the application's own handlers the same context is available as
`TaskRunContext.Current`. The context is isolated between parallel runs.

## HTTP API

External processes receive `Z3NDASH_API_URL`, `Z3NDASH_RUN_TOKEN`, `Z3NDASH_TASK_ID`
and `Z3NDASH_RUN_ID` through the environment. The address carries the actual port of the
running application. For `/self` the address and the token are enough.
Every request carries the `Authorization: Bearer <Z3NDASH_RUN_TOKEN>` header.
The token is valid only until the run ends. Do not print it to logs.

| Method | Path | Body |
|---|---|---|
| GET | `/api/v1/self` | — |
| POST | `/api/v1/self/defer` | `{"delay_seconds":3600,"reason":"Service error"}` |
| POST | `/api/v1/self/defer` | `{"until":"2026-12-01T12:00:00Z"}` |
| POST | `/api/v1/self/pause` | `{}` |
| POST | `/api/v1/self/resume` | `{}` |

Every command answers with the state: `task_id`, `run_id`, `name`, `enabled`, `paused`,
`deferred_until` (UTC or null), `reason`, `schedule_mode`. The token is not part of the
answer. `delay_seconds` is a positive number of seconds from the moment of the request.
`until` is a future time with `Z` or a UTC offset. The two fields cannot be sent together.
A reason is optional, 2000 characters at most. Errors: 400 — bad parameters,
401 — missing or finished context, 404 — task or command not found,
503 — database not connected, 500 — internal error. The error body is `{"error":"..."}`.

A Node.js example:

```javascript
const response = await fetch(process.env.Z3NDASH_API_URL + '/api/v1/self/defer', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json',
             Authorization: 'Bearer ' + process.env.Z3NDASH_RUN_TOKEN },
  body: JSON.stringify({ delay_seconds: 3600, reason: 'Service unavailable' })
});
if (!response.ok) throw new Error(await response.text());
```

## Controlling other tasks by ID

A watcher script can pause and defer other tasks. It uses the same run token as
`/self`; the target task's ID goes in the path.

```python
from z3ndash import task, list_tasks

for state in list_tasks(name="direction_1"):    # exact name match
    task(state["task_id"]).defer(seconds=1800, reason="watcher: no new records")

task("a1b2c3").pause()
task("a1b2c3").resume()
```

| Method | Path | Body |
|---|---|---|
| GET | `/api/v1/tasks` | — (optional `?name=` — exact name) |
| GET | `/api/v1/tasks/{id}` | — |
| POST | `/api/v1/tasks/{id}/defer` | same as `/self/defer` |
| POST | `/api/v1/tasks/{id}/pause` | `{}` |
| POST | `/api/v1/tasks/{id}/resume` | `{}` |

The response is the same state as for `/self` (a list of states for `GET /api/v1/tasks`),
with `run_id` set to `null`. Error codes are the same; 404 means no task has that ID.
Deferral rules are shared: competing deferrals keep the latest time, and `resume`
clears both the pause and the deferral.

## How the schedule behaves

- A deferral applies to the whole task. Instances already admitted keep running; new
  automatic runs and the queue wait.
- Reserved instances waiting on a start delay check the pause as well.
- Deferrals and indefinite pauses are stored in the database and survive a restart.
- Competing deferrals pick the latest time. Shortening it takes an explicit `resume`
  followed by a new `defer`.
- Once a deferral expires the previous schedule and its limits apply again. It is a ban
  on starting before the given time, not a promise to start exactly at that second. The
  scheduler checks readiness once a minute.
- A manual `Run` starts the task at once and does not lift the deferral. A manual run
  that lands in the queue because threads are busy waits along with the queue.
- `resume` lifts the pause and the deferral but changes neither `enabled` nor the
  schedule mode. A disabled task has to be enabled separately.
- Tasker shows the deferral deadline and a `Resume schedule` button to lift it.

## Driving the application from outside

The browser interface API works at the same address:
`GET /tasker/list`, `POST /tasker/save`, `/tasker/run`, `/tasker/stop`,
`/tasker/delete`, `GET /tasker/instances?id=...`, `/tasker/queue?id=...`.
The run, stop and delete commands take the JSON `{"id":"task ID"}`.
`save` accepts an `id` and the task fields to change, for example `enabled`,
`schedule_mode`, `cron`, `schedule_json`. Without an `id` it creates a new task.

`/tasker/run` also takes `count` (how many runs to queue) and an optional string `args`:
it goes into the queue items of that burst instead of the task's `args`, and the task
itself is not changed. This lets an orchestrating script launch a burst with a different
country without touching the schedule or settings. Payload flags are still appended,
except those already present in `args`. A non-string or empty `args` gets 400
`args must be a non-empty string` (checked before `count` and before the task lookup).
The response includes the burst's `args`.

There are also operator commands: `POST /tasker/pause` and `/tasker/resume` with the
body `{"id":"..."}`, and `POST /tasker/defer?id=...` with the same body as
`/self/defer`. They use the existing access to the dashboard API.
To control its own task from a script, use `/self`: no ID is needed there.
