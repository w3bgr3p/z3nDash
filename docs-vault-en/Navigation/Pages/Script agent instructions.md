# Instructions for an agent writing z3nDash scripts

You are writing a script run by the z3nDash scheduler. Use the built-in API to control
the schedule of your own task. Implement the user's business logic and add a deferral
only where the situation calls for one.

## Run context

- The API address is shared by the application. Every run gets its own context automatically.
- Do not look the task up by name, script path or task list. One file can be run by
  several tasks at once. Do not read or edit the scheduler database.
- Do not ask the user to supply an ID, a token or a port by hand. Do not hardcode them.
- External processes get the environment variables `Z3NDASH_API_URL`,
  `Z3NDASH_RUN_TOKEN`, `Z3NDASH_TASK_ID`, `Z3NDASH_RUN_ID`.
- To control yourself use `/api/v1/self`: no ID is needed in the request.
- The token belongs to a single run and stops working once that run ends.
  Do not keep it for later runs and do not print it to logs.
- Outside a z3nDash run there is no context. Say so plainly; do not invent values and do
  not claim that schedule control worked.

## What the commands mean

- `defer` blocks new automatic runs of the whole current task until the given time,
  the queue included. Other tasks and already running instances carry on.
- `defer` by itself neither ends nor stops the current script. Wait for the answer,
  release your resources and finish normally, keeping the error status on failure.
- Do not replace a deferral with an hour-long `sleep`: that holds a running instance.
- A deferral survives an application restart. The normal schedule is kept.
- A second deferral can extend the pause but cannot shorten one already set.
  Use the returned `deferred_until` if you need to show the actual deadline.
- Once a deferral expires the normal schedule mechanism takes over: a start at exactly
  that second is not guaranteed, and readiness is checked once a minute.
- `pause` suspends automatic runs indefinitely.
- `resume` lifts both an indefinite pause and a temporary deferral. Do not call it
  automatically on start-up or in `finally`: that can lift a pause set by another thread.
- `resume` changes neither `enabled` nor the schedule mode. A manual Run can bypass a
  deferral; a script must not use it to get around its own pause.

## Python

Use the module that ships with the application. z3nDash adds it to `PYTHONPATH`,
venv included. Do not install a like-named package from PyPI and do not create your own
`z3ndash.py`.

```python
from z3ndash import current_task

state = current_task.get()
state = current_task.defer(seconds=3600, reason="Service temporarily unavailable")
```

Also available: `current_task.pause()`, `current_task.resume()` and
`current_task.defer(until=aware_datetime_or_iso_string, reason="...")`.
Pass exactly one of `seconds` or `until`.

When wiring this into an error handler, use this shape. `perform_work` and
`TemporaryServiceError` stand for the real function and the real transient error of
your script: replace them with fitting names, do not leave the placeholders.

```python
import logging
from z3ndash import current_task

try:
    perform_work()
except TemporaryServiceError:
    try:
        state = current_task.defer(seconds=3600, reason="Service temporarily unavailable")
        logging.warning("Next runs deferred until %s", state["deferred_until"])
    except Exception:
        logging.exception("Could not confirm the deferral with z3nDash")
    raise  # Keep the original error and do not report success.
```

Do not turn every exception into a transient one: code errors, cancellation and an
expected absence of data must be handled as that particular script requires.

## C# / CSX / XML OwnCode

In `csx-internal` and XML OwnCode executed by z3nDash itself, the `current_task` object
is provided by the environment. Do not construct it yourself.

```csharp
var state = await current_task.DeferAsync(3600, "Service temporarily unavailable");
var actualDeadline = state.DeferredUntil;
```

The other methods: `GetAsync()`, `PauseAsync()`, `ResumeAsync()`,
`DeferUntilAsync(DateTimeOffset until, string reason = "")`.
In a synchronous block: `current_task.DeferAsync(3600).GetAwaiter().GetResult();`.
Wait for the call to finish, then end the script; do not leave the request as a
background task. When handling an error, do not mask the original exception with an API
call failure. Inside the application's own handlers `TaskRunContext.Current` is available.
For a separate C# process use HTTP and the environment variables, as in other languages.

## HTTP for Node.js, PowerShell and other languages

Base address: the value of `Z3NDASH_API_URL` without a trailing `/`.
Header on every request: `Authorization: Bearer <value of Z3NDASH_RUN_TOKEN>`.
POST sends JSON with `Content-Type: application/json`.

| Method | Path | Body |
|---|---|---|
| GET | `/api/v1/self` | None |
| POST | `/api/v1/self/defer` | `{"delay_seconds":3600,"reason":"Service unavailable"}` |
| POST | `/api/v1/self/defer` | `{"until":"2030-01-01T12:00:00Z","reason":"Until a given time"}` |
| POST | `/api/v1/self/pause` | `{}` |
| POST | `/api/v1/self/resume` | `{}` |

The `until` date in the example is illustrative: compute the future date your task
actually needs. Always include `Z` or a UTC offset. `delay_seconds` is a positive finite
number of seconds from the moment of the request. Do not send both fields at once.
A reason is optional, up to 2000 characters; do not put secrets in it.

Every command answers with a JSON object carrying `task_id`, `run_id`, `name`,
`enabled`, `paused`, `deferred_until`, `reason`, `schedule_mode`. `deferred_until` can be
null. In Python this is a dictionary; in C# an object with the properties `TaskId`,
`RunId`, `Name`, `Enabled`, `Paused`, `DeferredUntil`, `Reason`, `ScheduleMode`.

Check the HTTP status before handling a successful answer. An error carries the body
`{"error":"..."}`. 400 means bad parameters, 401 a missing or expired token,
404 a missing task or command, 503 a database that is not connected, 500 an internal
error. Do not retry 400 or 401 forever. On a timeout the outcome is unknown: do not claim
the pause is set; check the state with GET `/self` if it matters.
Use a finite network timeout. The local API must not go through the script's working
proxy. The built-in Python and C# helpers already bypass the proxy and use a 15-second
timeout.

## Before handing the script over

- No hardcoded address, port or ID, and no searching for yourself among the tasks.
- The deferral is called in the right error branch and awaited before the script ends.
- No false claim that a pause was set when the request failed.
- No automatic `resume`, no long wait instead of a deferral, no stopping other threads.
- Resources are released, the original error is not lost, the token stays out of the output.
- State whether the script was verified by a real run through z3nDash. An ordinary run
  from a terminal, without the context, does not confirm the integration works.

The application API reference: [[Task API]]. Do not invent SDK methods or endpoints. For
actions outside the current task, first agree on how much control is required.
