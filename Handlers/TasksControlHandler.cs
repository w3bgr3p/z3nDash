using System.Net;

namespace z3nDash;

/// <summary>
/// Control of any task by ID from a running script (e.g. a watcher pausing other tasks):
///   GET  /api/v1/tasks[?name=...]   — states of all tasks, optionally filtered by exact name
///   GET  /api/v1/tasks/{id}          — state of one task
///   POST /api/v1/tasks/{id}/defer    — same body as /api/v1/self/defer
///   POST /api/v1/tasks/{id}/pause
///   POST /api/v1/tasks/{id}/resume
/// Requires the caller's own run token, same as /api/v1/self.
/// </summary>
public sealed class TasksControlHandler(SchedulerService scheduler) : IScriptHandler
{
    public string PathPrefix => "/api/v1/tasks";
    public void Init() { }

    public async Task<bool> HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        try
        {
            if (await TaskControlHandler.Authorize(scheduler, context) is null) return true;
            var path = request.Url!.AbsolutePath.TrimEnd('/');
            string[]? parts = path == PathPrefix ? [] : path.StartsWith(PathPrefix + "/")
                ? path[(PathPrefix.Length + 1)..].Split('/').Select(Uri.UnescapeDataString).ToArray()
                : null;
            object? result = (parts, request.HttpMethod) switch
            {
                ({ Length: 0 }, "GET") => List(request.QueryString["name"]),
                ({ Length: 1 }, "GET") => scheduler.GetControlState(parts[0]),
                ({ Length: 2 }, "POST") when parts[1] == "defer" => await Defer(parts[0], request),
                ({ Length: 2 }, "POST") when parts[1] == "pause" => scheduler.PauseTask(parts[0], true),
                ({ Length: 2 }, "POST") when parts[1] == "resume" => scheduler.PauseTask(parts[0], false),
                _ => null
            };
            if (result == null)
            {
                response.StatusCode = 404;
                await HttpHelpers.WriteJson(response, new { error = "Unknown tasks endpoint or method" });
                return true;
            }
            await HttpHelpers.WriteJson(response, result);
        }
        catch (Exception ex)
        {
            await TaskControlHandler.WriteError(response, ex);
        }
        return true;
    }

    private List<TaskControlState> List(string? name)
    {
        var states = scheduler.ListControlStates();
        return name == null ? states : states.Where(s => s.Name == name).ToList();
    }

    private async Task<TaskControlState> Defer(string id, HttpListenerRequest request)
    {
        var (until, reason) = await TaskControlHandler.ReadDefer(request);
        return scheduler.DeferTask(id, until, reason);
    }
}
