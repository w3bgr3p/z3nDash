"""Control z3nDash tasks without hard-coded URLs: the current one, or any other by ID."""
import json
import os
from urllib.error import HTTPError
from urllib.parse import quote, urlencode
from urllib.request import Request, ProxyHandler, build_opener


def _request(path, payload=None):
    url = os.environ.get("Z3NDASH_API_URL")
    token = os.environ.get("Z3NDASH_RUN_TOKEN")
    if not url or not token:
        raise RuntimeError("This script was not started by z3nDash")
    data = None if payload is None else json.dumps(payload, allow_nan=False).encode("utf-8")
    request = Request(url.rstrip("/") + path, data=data,
                      headers={"Authorization": "Bearer " + token,
                               "Content-Type": "application/json"})
    try:
        # The local control API must not go through a script's HTTP proxy.
        with build_opener(ProxyHandler({})).open(request, timeout=15) as response:
            return json.load(response)
    except HTTPError as error:
        message = error.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"z3nDash API {error.code}: {message}") from error


def _defer_payload(seconds, reason, until):
    if (seconds is None) == (until is None):
        raise ValueError("Provide exactly one of seconds or until")
    payload = {"reason": reason}
    if until is not None:
        payload["until"] = until.isoformat() if hasattr(until, "isoformat") else until
    else:
        payload["delay_seconds"] = seconds
    return payload


class _TaskControl:
    _base = ""

    def _request(self, action="", payload=None):
        return _request(self._base + action, payload)

    def get(self):
        return self._request()

    def defer(self, seconds=None, reason="", *, until=None):
        return self._request("/defer", _defer_payload(seconds, reason, until))

    def pause(self):
        return self._request("/pause", {})

    def resume(self):
        return self._request("/resume", {})


class CurrentTask(_TaskControl):
    _base = "/api/v1/self"


class Task(_TaskControl):
    """Another task, addressed by its z3nDash ID."""

    def __init__(self, task_id):
        if not task_id:
            raise ValueError("task_id is required")
        self.id = str(task_id)
        self._base = "/api/v1/tasks/" + quote(self.id, safe="")


def task(task_id):
    return Task(task_id)


def list_tasks(name=None):
    """States of all tasks; with name, only tasks whose name matches exactly."""
    query = "" if name is None else "?" + urlencode({"name": name})
    return _request("/api/v1/tasks" + query)


current_task = CurrentTask()
