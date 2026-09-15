using System.Text.Json;

namespace z3nDash;

public sealed record ZpNodeRegistration(
    string Machine,
    string Host,
    string External,
    int Port,
    string Token,
    string Firewall,
    string PortRule);

public sealed record ZpNodeRow(string Machine, string Host, int Port, string Token, string UpdatedAt);

/// <summary>Версии, которые узел отдаёт на GET /version. Пустая строка — узел значение не прочитал.</summary>
public sealed record ZpNodeVersion(string Z3n7, string ZennoPoster, string Framework);

/// <summary>Чем ответил узел на пробу: это наблюдение, а не вывод о причине.</summary>
public enum ZpNodeProbe
{
    /// <summary>Соединение не установилось либо ответ не 2xx и не 401.</summary>
    Unreachable,

    /// <summary>Узел ответил 401: токена нет либо он не тот.</summary>
    Unauthorized,

    /// <summary>Узел ответил успешно.</summary>
    Ok,
}

public sealed class ZpNodeStore
{
    private const char ColumnSeparator = '\u00A6';
    private readonly Db _db;

    public ZpNodeStore(Db db) => _db = db;

    public static bool TryParse(string raw, out ZpNodeRegistration node, out string error)
    {
        node = new ZpNodeRegistration("", "", "", 0, "", "", "");
        error = "";

        var json = raw.Trim();
        if (json.EndsWith('\'')) json = json[..^1].TrimEnd();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "invalid json"; return false; }
            var machine = ReadString(root, "machine");
            var host = ReadString(root, "host");
            var external = ReadString(root, "external");
            var token = ReadString(root, "token");
            var firewall = ReadString(root, "firewall");
            var portRule = ReadString(root, "portRule");
            if (string.IsNullOrEmpty(portRule)
                && root.TryGetProperty("urlacl", out var urlAclElement)
                && urlAclElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                portRule = urlAclElement.GetBoolean() ? "yes" : "no";

            if (string.IsNullOrWhiteSpace(machine)) { error = "machine required"; return false; }
            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(external))
            {
                error = "host or external required";
                return false;
            }
            if (!root.TryGetProperty("port", out var portElement) || !TryReadPort(portElement, out var port))
            {
                error = "port must be between 1 and 65535";
                return false;
            }

            node = new ZpNodeRegistration(
                machine.Trim(),
                host.Trim(),
                external.Trim(),
                port,
                token.Trim(),
                firewall.Trim(),
                portRule.Trim());
            return true;
        }
        catch (JsonException)
        {
            error = "invalid json";
            return false;
        }
    }

    public IReadOnlyList<ZpNodeRow> GetAll()
    {
        var rows = _db.GetLines(
            "machine,host,port,token,updated_at",
            DbSchema.ZpNodes.Name,
            where: "1=1");

        return rows
            .Select(ParseRow)
            .Where(row => row != null)
            .Cast<ZpNodeRow>()
            .OrderBy(row => row.Machine, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Upsert(ZpNodeRegistration node)
    {
        var machine = Escape(node.Machine);
        var host = Escape(node.Host);
        var token = Escape(node.Token);
        var updatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        _db.Query(
            $"INSERT INTO \"{DbSchema.ZpNodes.Name}\" (\"machine\", \"host\", \"port\", \"token\", \"updated_at\") " +
            $"VALUES ('{machine}', '{host}', '{node.Port}', '{token}', '{updatedAt}') " +
            "ON CONFLICT (\"machine\") DO UPDATE SET " +
            "\"host\" = excluded.\"host\", \"port\" = excluded.\"port\", " +
            "\"token\" = excluded.\"token\", \"updated_at\" = excluded.\"updated_at\"",
            thrw: true);
    }

    public void Delete(string machine)
        => _db.Del(DbSchema.ZpNodes.Name, thrw: true, where: $"\"machine\" = '{Escape(machine)}'");

    private static ZpNodeRow? ParseRow(string raw)
    {
        var parts = raw.Split(ColumnSeparator);
        if (parts.Length < 5 || string.IsNullOrWhiteSpace(parts[0]) || !int.TryParse(parts[2], out var port))
            return null;

        return new ZpNodeRow(parts[0], parts[1], port, parts[3], parts[4]);
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool TryReadPort(JsonElement element, out int port)
    {
        var parsed = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var value) ? value : 0,
            JsonValueKind.String => int.TryParse(element.GetString(), out var value) ? value : 0,
            _ => 0,
        };

        port = parsed;
        return port is >= 1 and <= 65535;
    }

    private static string Escape(string value) => value.Replace("'", "''");
}

public static class ZpNodeAddressResolver
{
    /// <summary>
    /// Первый адрес, с которого узел ответил успешно. Если ни один не ответил,
    /// но хотя бы один вернул 401 — отдаём Unauthorized: адрес рабочий, дело
    /// в токене, и сообщение об этом читается иначе, чем «узел недоступен».
    /// </summary>
    public static async Task<(string? Host, ZpNodeProbe Result)> ResolveAsync(
        ZpNodeRegistration node,
        string localMachine,
        Func<string, int, string, Task<ZpNodeProbe>> probe)
    {
        var candidates = new List<string>();
        if (string.Equals(node.Machine, localMachine, StringComparison.OrdinalIgnoreCase))
            candidates.Add("127.0.0.1");
        if (!string.IsNullOrWhiteSpace(node.Host)) candidates.Add(node.Host);
        if (!string.IsNullOrWhiteSpace(node.External)) candidates.Add(node.External);

        var seen = ZpNodeProbe.Unreachable;

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var result = await probe(candidate, node.Port, node.Token);
                if (result == ZpNodeProbe.Ok) return (candidate, ZpNodeProbe.Ok);
                if (result == ZpNodeProbe.Unauthorized) seen = ZpNodeProbe.Unauthorized;
            }
            catch
            {
                // An unreachable candidate must not prevent trying the next priority.
            }
        }

        return (null, seen);
    }
}

public sealed class ZpNodeRegistrar
{
    private readonly ZpNodeStore _store;

    public ZpNodeRegistrar(ZpNodeStore store) => _store = store;

    public async Task<(string? Host, ZpNodeProbe Result)> RegisterAsync(
        ZpNodeRegistration node,
        string localMachine,
        Func<string, int, string, Task<ZpNodeProbe>> probe)
    {
        var (reachableHost, result) = await ZpNodeAddressResolver.ResolveAsync(node, localMachine, probe);
        if (reachableHost == null) return (null, result);

        _store.Upsert(node with { Host = reachableHost });
        return (reachableHost, result);
    }
}
