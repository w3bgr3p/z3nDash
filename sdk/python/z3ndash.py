"""Control the current z3nDash task without hard-coded URLs or task IDs."""
import json
import os
from urllib.error import HTTPError
from urllib.request import Request, ProxyHandler, build_opener


class CurrentTask:
    def _request(self, action="", payload=None):
        url = os.environ.get("Z3NDASH_API_URL")
        token = os.environ.get("Z3NDASH_RUN_TOKEN")
        if not url or not token:
            raise RuntimeError("This script was not started by z3nDash")
        data = None if payload is None else json.dumps(payload, allow_nan=False).encode("utf-8")
        request = Request(url.rstrip("/") + "/api/v1/self" + action, data=data,
                          headers={"Authorization": "Bearer " + token,
                                   "Content-Type": "application/json"})
        try:
            # The local control API must not go through a script's HTTP proxy.
            with build_opener(ProxyHandler({})).open(request, timeout=15) as response:
                return json.load(response)
        except HTTPError as error:
            message = error.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"z3nDash API {error.code}: {message}") from error

    def get(self):
        return self._request()

    def defer(self, seconds=None, reason="", *, until=None):
        if (seconds is None) == (until is None):
            raise ValueError("Provide exactly one of seconds or until")
        payload = {"reason": reason}
        if until is not None:
            payload["until"] = until.isoformat() if hasattr(until, "isoformat") else until
        else:
            payload["delay_seconds"] = seconds
        return self._request("/defer", payload)

    def pause(self):
        return self._request("/pause", {})

    def resume(self):
        return self._request("/resume", {})


current_task = CurrentTask()
