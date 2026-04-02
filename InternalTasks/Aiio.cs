using System;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using z3nIO.Api.Captcha;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nIO;

public class IoNetAuth
{
    private readonly IZennoPosterProjectModel _project;
    private readonly Logger _logger;

    private const string SITE_KEY   = "6LeJO8IpAAAAAAVEbi1p8k98zPCyErzsMwrXTNil";
    private const string CAPTCHA_URL = "https://id.io.net/login?redirect=/";

    public IoNetAuth(IZennoPosterProjectModel project, Logger log = null)
    {
        _project = project;
        _logger  = log;
    }

    public void Run()
    {
        var mail   = _project.DbGet("icloud", "_mail");
        var capKey = _project.DbGet("apikey", "_api", where: "id = 'capmonster'");

        // 1. Капча
        var solver = new CapMonsterSolver();
        var token  = solver.Solve(SITE_KEY, CAPTCHA_URL, capKey, "reV3");

        if (string.IsNullOrEmpty(token))
            throw new Exception("captcha: no token");

        _logger?.Send("captcha solved");

        // 2. magic-auth/start
        var startRaw = _project.POST(
            "https://id.io.net/api/workos/magic-auth/start",
            JsonConvert.SerializeObject(new { email = mail, captcha = token, invitationToken = "" }),
            "+",
            parse: true
        );

        var authId = _project.Json.id?.ToString();
        if (string.IsNullOrEmpty(authId))
            throw new Exception($"magic-auth/start: no id. raw={startRaw}");

        _logger?.Send($"magic-auth started id={authId}");

        // 3. OTP
        var otp      = (string)null;
        var fm       = new GmailClient(_project, _logger);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);

        while (DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(5000);
            try   { otp = fm.Otp(mail); break; }
            catch { /* ещё не пришло */ }
        }

        if (string.IsNullOrEmpty(otp))
            throw new Exception("OTP: timeout 60s");

        _logger?.Send($"otp={otp}");

        // 4. magic-auth/verify
        var verifyRaw = _project.POST(
            "https://id.io.net/api/workos/magic-auth/verify",
            JsonConvert.SerializeObject(new { email = mail, code = otp, captcha = token }),
            "+",
            parse: true
        );

        var accessToken = _project.Json.accessToken?.ToString();
        if (string.IsNullOrEmpty(accessToken))
            throw new Exception($"magic-auth/verify: no accessToken. raw={verifyRaw}");

        _logger?.Send("access token received");

        // 5. Создать API-ключ
        var expiresAt = DateTime.UtcNow.AddDays(180).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        string[] apiHeaders =
        {
            $"User-Agent: {_project.Profile.UserAgent}",
            "accept: */*",
            "accept-language: en-US,en;q=0.9",
            "content-type: application/json",
            "sec-ch-ua: \"Google Chrome\";v=\"143\", \"Chromium\";v=\"143\", \"Not A(Brand\";v=\"24\"",
            "sec-ch-ua-mobile: ?0",
            "sec-ch-ua-platform: \"Windows\"",
            $"token: {accessToken}",
            "origin: https://ai.io.net/",
            "referer: https://ai.io.net/"
        };

        var newApiRaw = _project.POST(
            "https://api.io.solutions/v1/api-keys/",
            JsonConvert.SerializeObject(new {
                description = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                project     = "io-intelligence",
                scopes      = new[] { "all" },
                expires_at  = expiresAt
            }),
            "+",
            apiHeaders,
            parse: true
        );

        var apiValue = JObject.Parse(newApiRaw)["value"]?.ToString();
        if (string.IsNullOrEmpty(apiValue))
            throw new Exception($"api-keys: no value. raw={newApiRaw}");

        // 6. Сохранить
        var today = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        _project.DbUpd($"api = '{apiValue}', expire = '{expiresAt}', daily = '{today}'", "__aiio", log: true);

        _logger?.Send($"done api={apiValue[..12]}...");
    }
}

public static partial class InternalTasks
{
    private static readonly bool _ioNetRegistered = RegisterSelf(
        (scheduler, dbService, logsConfig) => RegisterIoNet(scheduler, dbService, logsConfig)
    );

    private static void RegisterIoNet(SchedulerService scheduler, DbConnectionService dbService, LogsConfig logsConfig)
    {
        scheduler.RegisterTask("IoNet.UpdKeys", async (payload, ct, log) =>
        {
            var ctx = await PrepareTaskContext(scheduler, dbService, logsConfig, payload, "__aiio", log);
            if (ctx == null) return "no account available";

            var task = new IoNetAuth(ctx.Project, ctx.Logger);
            try
            {
                task.Run();
                ctx.Release("idle");
                return "ok";
            }
            catch
            {
                ctx.Release("fail");
                throw;
            }
        });
    }
}