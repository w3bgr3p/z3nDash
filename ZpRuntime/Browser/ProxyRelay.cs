// ══════════════════════════════════════════════════════════════════════════════
// ProxyRelay.cs — локальный HTTP-прокси поверх SOCKS5 с авторизацией.
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
// ── Почему браузеру отдаётся HTTP, а не SOCKS5 ───────────────────────────────
//
// Раньше релей и вниз говорил на SOCKS5. На это драйвер Patchright добавляет
// браузеру ключ:
//
//     const isSocks = proxyURL.protocol === "socks5:";
//     if (isSocks && !options.socksProxyPort)
//       chromeArguments.push(`--host-resolver-rules="MAP * ~NOTFOUND , EXCLUDE …"`);
//
// Ключ нужный: он валит локальное разрешение имён, чтобы Chrome отдал домен
// прокси, а не резолвил сам мимо него. Но Chrome на него показывает плашку
// «You are using an unsupported command-line flag», а она занимает высоту окна —
// то есть сдвигает вьюпорт и попадает в скриншоты. Координаты кликов и
// DrawPartAsBitmap считаются от смещённой области.
//
// С HTTP-прокси эта развилка исчезает вместе с ключом: в CONNECT браузер пишет
// имя хоста, а не адрес, и сам ничего не резолвит. Имя доходит до нас и уходит
// наверх типом адреса 0x03 — резолвит его прокси. Утечки DNS нет по устройству
// протокола, а не по ключу командной строки.
//
// Вниз поддержаны оба вида запроса, которые шлёт браузер прокси:
//   CONNECT host:port         — весь HTTPS, дальше труба байт в байт;
//   GET http://host/path      — обычный HTTP в абсолютной форме.
// Во втором случае соединение помечается Connection: close. Иначе браузер
// оставил бы его живым и послал бы следующий запрос — возможно к другому хосту —
// в уже открытую наверх трубу, то есть не туда. Плата — лишнее соединение на
// голом HTTP, которого в шаблонах почти нет.
// ══════════════════════════════════════════════════════════════════════════════

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DevDeck.Browser;

public sealed class ProxyRelay : IDisposable
{
    private readonly TcpListener       _listener;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Куда ходить наверх. Меняется на живом релее — в этом весь смысл: ZP-шный
    /// SetProxy зовут посреди прогона, когда прокси только что получен от
    /// поставщика, а браузер уже поднят.
    /// </summary>
    private volatile Upstream _up = Upstream.Direct;

    /// <summary>
    /// Живые соединения браузера. Нужны, чтобы порвать их при смене верхнего
    /// прокси: браузер держит соединения к нам открытыми и переиспользует, и
    /// без разрыва он продолжает ходить через прежний прокси — смена
    /// логируется, а IP не меняется. Проверено: без этого SetProxy не менял
    /// ничего.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TcpClient, byte> _live = new();

    /// <summary>
    /// Верхний прокси: напрямую, SOCKS5 или HTTP. Логин поддержан обоими и в
    /// работе всегда есть — прокси без авторизации в боевых прогонах не
    /// встречаются, режим без логина оставлен только как вырожденный случай.
    /// </summary>
    private sealed record Upstream(string Kind, string Host, int Port, string User, string Pass)
    {
        public static readonly Upstream Direct = new("direct", "", 0, "", "");
        public bool HasAuth => User.Length > 0;
        public override string ToString()
            => Kind == "direct" ? "напрямую" : $"{Kind} {Host}:{Port}{(HasAuth ? " с логином" : "")}";
    }

    /// <summary>Адрес для браузера: HTTP-прокси на локальной петле.</summary>
    public string Endpoint { get; }

    /// <summary>
    /// Куда рассказывать про отказы. Без этого каждый сбой уходил в пустой
    /// catch, и наружу это выглядело как «прокси молчит»: браузер получал
    /// ERR_EMPTY_RESPONSE, а причина — отказ авторизации или отказ прокси на
    /// CONNECT — не доезжала никуда.
    /// </summary>
    public Action<string>? Log { get; set; }

    private ProxyRelay(TcpListener listener)
    {
        _listener = listener;
        Endpoint  = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    }

    /// <summary>Что сейчас наверху — для Instance.GetProxy и сверки.</summary>
    public string Current { get; private set; } = "";

    /// <summary>
    /// Поднять релей и слушать. Верхний прокси задаётся отдельно и может
    /// меняться сколько угодно раз.
    /// </summary>
    public static ProxyRelay Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var relay = new ProxyRelay(listener);
        _ = relay.AcceptLoopAsync();
        return relay;
    }

    /// <summary>
    /// Сменить верхний прокси. Пустая строка — ходить напрямую.
    ///
    /// Уже открытые соединения остаются на прежнем верхнем прокси: рвать их
    /// значит уронить страницу под руками у шаблона. Новые пойдут через новый.
    /// </summary>
    public void SetUpstream(string? proxy)
    {
        _up     = Parse(proxy);
        Current = string.IsNullOrWhiteSpace(proxy) ? "" : proxy.Trim();

        // Рвём то, что уже открыто: иначе браузер продолжит ходить через
        // прежний прокси по переиспользованным соединениям.
        var dropped = 0;
        foreach (var c in _live.Keys)
        {
            _live.TryRemove(c, out _);
            try { c.Close(); dropped++; } catch { }
        }

        Log?.Invoke($"верхний прокси: {_up}" + (dropped > 0 ? $", порвано соединений: {dropped}" : ""));
    }

    /// <summary>Разбор строки вида scheme://user:pass@host:port; схема по умолчанию http.</summary>
    private static Upstream Parse(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return Upstream.Direct;

        var v = proxy.Trim();
        int s = v.IndexOf("://", StringComparison.Ordinal);
        var scheme = s > 0 ? v[..s].ToLowerInvariant() : "http";
        if (s > 0) v = v[(s + 3)..];

        var user = ""; var pass = "";
        int at = v.LastIndexOf('@');
        if (at >= 0)
        {
            var creds = v[..at].Split(':', 2);
            user = creds[0];
            pass = creds.Length > 1 ? creds[1] : "";
            v    = v[(at + 1)..];
        }

        var hp = v.Split(':');
        if (hp.Length < 2 || !int.TryParse(hp[1], out var port)) return Upstream.Direct;

        return new Upstream(scheme.StartsWith("socks") ? "socks5" : "http", hp[0], port, user, pass);
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
            _live[client] = 0;
            try
            {
                client.NoDelay = true;
                var down = client.GetStream();

                var head = await ReadHeadAsync(down);
                if (head.Length == 0) return;

                int nl = head.IndexOf('\n');
                if (nl < 0) return;
                var parts = head[..nl].TrimEnd('\r').Split(' ');
                if (parts.Length < 3) return;

                bool isConnect = parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
                string host; int port;

                if (isConnect)
                {
                    var hp = parts[1].Split(':');
                    host = hp[0];
                    port = hp.Length > 1 && int.TryParse(hp[1], out var p1) ? p1 : 443;
                }
                else
                {
                    // Абсолютная форма: GET http://host/path HTTP/1.1
                    if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)) return;
                    host = uri.Host;
                    port = uri.IsDefaultPort ? 80 : uri.Port;
                }

                var up_ = _up;                       // снимок на время соединения

                using var upstream = new TcpClient { NoDelay = true };
                await upstream.ConnectAsync(
                    up_.Kind == "direct" ? host : up_.Host,
                    up_.Kind == "direct" ? port : up_.Port, _cts.Token);
                var up = upstream.GetStream();

                if (!await OpenTunnelAsync(up, up_, host, port, isConnect, down)) return;

                if (isConnect)
                    await WriteAsciiAsync(down, "HTTP/1.1 200 Connection Established" + "\r\n\r\n");
                else
                    await WriteAsciiAsync(up, RewriteForOrigin(head));

                await Task.WhenAny(
                    down.CopyToAsync(up, _cts.Token),
                    up.CopyToAsync(down, _cts.Token));
            }
            catch (Exception ex)
            {
                // Обрыв — обычное дело, браузер рвёт соединения сам. Но раньше
                // сюда же уходили и настоящие отказы, и наружу они выглядели
                // одинаково — молчанием.
                // ObjectDisposedException — это мы сами: порвали соединение при
                // смене прокси или закрыли релей вместе с сессией. Сообщать не о
                // чем, а после закрытия такие строки шли пачкой прямо в отчёт
                // задачи, уже после её конца.
                if (ex is not ObjectDisposedException && !_cts.IsCancellationRequested)
                    Log?.Invoke($"соединение оборвалось: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
            finally { _live.TryRemove(client, out _); }
        }
    }

    /// <summary>
    /// Заголовки запроса до пустой строки. Читаем побайтно: тело, если оно есть,
    /// должно остаться в потоке — дальше его перельёт труба.
    /// </summary>
    private static async Task<string> ReadHeadAsync(NetworkStream s)
    {
        var buf = new List<byte>(1024);
        var one = new byte[1];

        while (buf.Count < 64 * 1024)
        {
            if (await s.ReadAsync(one) == 0) break;
            buf.Add(one[0]);

            int c = buf.Count;
            if (c >= 4 && buf[c - 4] == 13 && buf[c - 3] == 10 && buf[c - 2] == 13 && buf[c - 1] == 10) break;
            if (c >= 2 && buf[c - 2] == 10 && buf[c - 1] == 10) break;
        }

        return Encoding.ASCII.GetString(buf.ToArray());
    }

    /// <summary>
    /// Перевести запрос из абсолютной формы в обычную и закрыть соединение после
    /// ответа: живым его держать нельзя, наверх у нас труба к одному хосту, а
    /// следующий запрос браузера может быть к другому.
    /// </summary>
    private static string RewriteForOrigin(string head)
    {
        var lines = head.Replace("\r\n", '\n'.ToString()).TrimEnd('\n').Split('\n');

        var first = lines[0].Split(' ');
        if (first.Length >= 3 && Uri.TryCreate(first[1], UriKind.Absolute, out var uri))
            lines[0] = $"{first[0]} {uri.PathAndQuery} {first[2]}";

        var outLines = new List<string>();
        foreach (var l in lines)
        {
            if (l.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)) continue;
            if (l.StartsWith("Connection:",       StringComparison.OrdinalIgnoreCase)) continue;
            outLines.Add(l);
        }
        outLines.Add("Connection: close");

        return string.Join("\r\n", outLines) + "\r\n\r\n";
    }

    /// <summary>
    /// CONNECT наверх по SOCKS5 с именем хоста (RFC 1928, ATYP 0x03).
    /// Возвращает код ответа прокси: 0x00 — успех.
    /// </summary>
    private async Task<byte> ConnectThroughSocksAsync(NetworkStream up, string host, int port)
    {
        var name = Encoding.ASCII.GetBytes(host);
        if (name.Length > 255) return 0xFF;

        var req = new byte[7 + name.Length];
        req[0] = 0x05;                  // версия
        req[1] = 0x01;                  // CONNECT
        req[2] = 0x00;                  // резерв
        req[3] = 0x03;                  // адрес — доменное имя
        req[4] = (byte)name.Length;
        name.CopyTo(req, 5);
        req[5 + name.Length] = (byte)(port >> 8);
        req[6 + name.Length] = (byte)(port & 0xFF);

        await up.WriteAsync(req, _cts.Token);

        var reply = await ReadReplyAsync(up);
        return reply.Length > 1 ? reply[1] : (byte)0xFF;
    }

    /// <summary>Первая строка ответа — в лог не нужен весь заголовок.</summary>
    private static string FirstLine(string text)
    {
        var i = text.IndexOf('\n');
        return (i < 0 ? text : text[..i]).Trim();
    }

    private static async Task WriteAsciiAsync(NetworkStream s, string text)
        => await s.WriteAsync(Encoding.ASCII.GetBytes(text));

    /// <summary>
    /// Довести соединение до целевого хоста через выбранный верхний прокси.
    /// Напрямую делать нечего — сокет уже соединён с целью.
    /// </summary>
    private async Task<bool> OpenTunnelAsync(NetworkStream up, Upstream cfg,
                                             string host, int port, bool isConnect, NetworkStream down)
    {
        if (cfg.Kind == "direct") return true;

        if (cfg.Kind == "socks5")
        {
            if (!await AuthenticateAsync(up, cfg))
            {
                Log?.Invoke($"прокси отклонил авторизацию ({cfg.Host}:{cfg.Port})");
                if (isConnect) await WriteAsciiAsync(down, "HTTP/1.1 502 Bad Gateway" + "\r\n\r\n");
                return false;
            }

            // Имя хоста уходит наверх как есть, типом адреса 0x03: резолвит его
            // прокси. Мы имя не разрешаем — в этом весь смысл затеи.
            var code = await ConnectThroughSocksAsync(up, host, port);
            if (code == 0x00) return true;

            Log?.Invoke($"прокси отказал в CONNECT {host}:{port}, код 0x{code:X2}");
            if (isConnect) await WriteAsciiAsync(down, "HTTP/1.1 502 Bad Gateway" + "\r\n\r\n");
            return false;
        }

        // HTTP наверху: свой CONNECT с заголовком авторизации. Имя хоста тоже
        // уходит именем — резолвит верхний прокси.
        var req = $"CONNECT {host}:{port} HTTP/1.1" + "\r\n" + $"Host: {host}:{port}" + "\r\n";
        if (cfg.HasAuth)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.User}:{cfg.Pass}"));
            req += "Proxy-Authorization: Basic " + token + "\r\n";
        }
        req += "Proxy-Connection: keep-alive" + "\r\n" + "\r\n";

        await WriteAsciiAsync(up, req);

        var head = await ReadHeadAsync(up);
        var ok   = head.Contains(" 200 ");
        if (!ok)
        {
            var line = FirstLine(head);
            Log?.Invoke($"прокси отказал в CONNECT {host}:{port}: {line}");
            if (isConnect) await WriteAsciiAsync(down, "HTTP/1.1 502 Bad Gateway" + "\r\n\r\n");
        }
        return ok;
    }

    /// <summary>
    /// Договориться о способе и, если он того просит, войти логином с паролем
    /// (RFC 1928 + RFC 1929).
    ///
    /// Предлагаем оба способа — «без авторизации» и «логин с паролем». Раньше
    /// предлагался только второй, и прокси, отвечающий 0x00 «вход не нужен»,
    /// получал от нас разрыв: браузеру это доезжало как ERR_EMPTY_RESPONSE, а
    /// причина не доезжала никуда. Выбирает способ сервер, наше дело — назвать
    /// оба, которые мы умеем.
    /// </summary>
    private async Task<bool> AuthenticateAsync(NetworkStream up, Upstream cfg)
    {
        await up.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x02 }, _cts.Token);
        var choice = await ReadExactAsync(up, 2);

        if (choice[1] == 0x00) return true;             // вход не требуется
        if (choice[1] != 0x02)
        {
            Log?.Invoke($"прокси не принял ни один способ входа (ответ 0x{choice[1]:X2})");
            return false;
        }

        var u = Encoding.UTF8.GetBytes(cfg.User);
        var p = Encoding.UTF8.GetBytes(cfg.Pass);

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
    /// Прочитать ответ SOCKS5 целиком. Адрес разбирается только затем, чтобы
    /// знать, где сообщение кончается.
    /// </summary>
    private static async Task<byte[]> ReadReplyAsync(NetworkStream s)
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
        Log = null;                       // после закрытия рассказывать некому
        try { _cts.Cancel(); }  catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
