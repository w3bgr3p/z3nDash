// ══════════════════════════════════════════════════════════════════════════════
// ProxyRelay.cs — локальный SOCKS5 без авторизации поверх SOCKS5 с авторизацией.
//
// Chromium не умеет авторизацию SOCKS5: логин с паролем он из строки выкидывает,
// подключается анонимно, получает отказ и отдаёт ERR_SOCKS_CONNECTION_FAILED.
// Ограничение известное и незакрытое (issues.chromium.org/40323993), и обойти
// его настройками нельзя.
//
// Именно поэтому у ZP-шного SetProxy есть флаг useProxifier, и шаблоны передают
// его включённым: ZennoPoster поднимает локальный прокси, который и держит
// авторизацию. Здесь то же самое, только своё.
//
// Релей слушает на 127.0.0.1, принимает от браузера анонимный SOCKS5, а наверх
// ходит с логином и паролем (RFC 1928 + RFC 1929). Запрос CONNECT пересылается
// как есть — разбирать адрес нужно только чтобы понять, где он кончается.
// ══════════════════════════════════════════════════════════════════════════════

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DevDeck.Browser;

public sealed class ProxyRelay : IDisposable
{
    private readonly TcpListener       _listener;
    private readonly string            _host;
    private readonly int               _port;
    private readonly string            _user;
    private readonly string            _pass;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Адрес для браузера: анонимный SOCKS5 на локальной петле.</summary>
    public string Endpoint { get; }

    private ProxyRelay(TcpListener listener, string host, int port, string user, string pass)
    {
        _listener = listener;
        _host = host; _port = port; _user = user; _pass = pass;
        Endpoint = $"socks5://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    }

    /// <summary>
    /// Поднять релей для строки вида socks5://user:pass@host:port. Если
    /// авторизации в строке нет, релей не нужен — возвращается null, и прокси
    /// отдаётся браузеру напрямую.
    /// </summary>
    public static ProxyRelay? StartIfNeeded(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return null;

        var v = proxy.Trim();
        int s = v.IndexOf("://", StringComparison.Ordinal);
        var scheme = s > 0 ? v[..s].ToLowerInvariant() : "http";
        if (s > 0) v = v[(s + 3)..];

        // HTTP-прокси Chromium авторизует сам, обходной путь нужен только SOCKS.
        if (!scheme.StartsWith("socks")) return null;

        int at = v.LastIndexOf('@');
        if (at < 0) return null;                    // без логина релей не нужен

        var creds = v[..at].Split(':', 2);
        var hp    = v[(at + 1)..].Split(':');
        if (hp.Length < 2 || !int.TryParse(hp[1], out var port)) return null;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var relay = new ProxyRelay(listener, hp[0], port,
                                   creds[0], creds.Length > 1 ? creds[1] : "");
        _ = relay.AcceptLoopAsync();
        return relay;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var down = client.GetStream();

                // ── рукопожатие с браузером: соглашаемся без авторизации ───────
                var head = await ReadExactAsync(down, 2);
                int nMethods = head[1];
                await ReadExactAsync(down, nMethods);
                await down.WriteAsync(new byte[] { 0x05, 0x00 });

                // ── запрос CONNECT: читаем целиком, чтобы переслать как есть ──
                var request = await ReadRequestAsync(down);

                using var upstream = new TcpClient { NoDelay = true };
                await upstream.ConnectAsync(_host, _port, _cts.Token);
                var up = upstream.GetStream();

                if (!await AuthenticateAsync(up)) return;

                await up.WriteAsync(request, _cts.Token);

                // Ответ наверх идёт браузеру дословно — в нём адрес привязки.
                var reply = await ReadRequestAsync(up, isReply: true);
                await down.WriteAsync(reply, _cts.Token);

                if (reply.Length > 1 && reply[1] != 0x00) return;   // отказ наверху

                await Task.WhenAny(
                    down.CopyToAsync(up, _cts.Token),
                    up.CopyToAsync(down, _cts.Token));
            }
            catch { /* оборванное соединение — обычное дело, браузер их рвёт сам */ }
        }
    }

    /// <summary>Логин с паролем по RFC 1929.</summary>
    private async Task<bool> AuthenticateAsync(NetworkStream up)
    {
        await up.WriteAsync(new byte[] { 0x05, 0x01, 0x02 }, _cts.Token);   // метод 2
        var choice = await ReadExactAsync(up, 2);
        if (choice[1] != 0x02) return false;

        var u = Encoding.UTF8.GetBytes(_user);
        var p = Encoding.UTF8.GetBytes(_pass);

        var auth = new byte[3 + u.Length + p.Length];
        auth[0] = 0x01;
        auth[1] = (byte)u.Length;
        u.CopyTo(auth, 2);
        auth[2 + u.Length] = (byte)p.Length;
        p.CopyTo(auth, 3 + u.Length);

        await up.WriteAsync(auth, _cts.Token);
        var status = await ReadExactAsync(up, 2);
        return status[1] == 0x00;
    }

    /// <summary>
    /// Прочитать запрос или ответ SOCKS5 целиком. Разбирать адрес нужно только
    /// затем, чтобы знать, где сообщение кончается: дальше оно пересылается
    /// байт в байт.
    /// </summary>
    private static async Task<byte[]> ReadRequestAsync(NetworkStream s, bool isReply = false)
    {
        var head = await ReadExactAsync(s, 4);          // VER CMD/REP RSV ATYP
        int addrLen = head[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => -1,                                  // домен: длина в первом байте
            _    => throw new IOException($"SOCKS5: неизвестный тип адреса {head[3]}"),
        };

        byte[] addr;
        if (addrLen < 0)
        {
            var len = await ReadExactAsync(s, 1);
            var name = await ReadExactAsync(s, len[0]);
            addr = [.. len, .. name];
        }
        else addr = await ReadExactAsync(s, addrLen);

        var port = await ReadExactAsync(s, 2);
        return [.. head, .. addr, .. port];
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream s, int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, count - read));
            if (n == 0) throw new IOException("SOCKS5: соединение закрыто раньше времени");
            read += n;
        }
        return buf;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); }  catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
