// InternalTasks.GenerateClientBundle.cs
// Payload: { "clientHwid": "...", "clientName": "worker01", "outputFolder": "C:\bundles" }
// outputFolder опционален, fallback: AppContext.BaseDirectory/clients/clientName

using Newtonsoft.Json;

namespace z3nIO;

public static partial class InternalTasks
{
    private static readonly bool _generateClientBundleRegistered = RegisterSelf(
        (scheduler, dbService, logsConfig) => RegisterGenerateClientBundle(scheduler, dbService, logsConfig)
    );

    private static void RegisterGenerateClientBundle(SchedulerService scheduler, DbConnectionService dbService, LogsConfig logsConfig)
    {
        scheduler.RegisterTask("GenerateClientBundle", async (payload, ct, log) =>
        {
            var clientHwid   = payload.GetValueOrDefault("clientHwid", "").Trim();
            var clientName   = payload.GetValueOrDefault("clientName", "").Trim();
            var outputFolder = payload.GetValueOrDefault("outputFolder", "").Trim();

            if (string.IsNullOrEmpty(clientHwid))
                throw new Exception("payload.clientHwid is required");
            if (string.IsNullOrEmpty(clientName))
                throw new Exception("payload.clientName is required");

            log($"[DBG] _jVarsPath={_jVarsPath}");
            log($"[DBG] _jVars length={_jVars.Length} starts={(_jVars.Length > 20 ? _jVars[..20] : _jVars)}");

            var jVarsJson = SAFU.DecryptHWIDOnly(_jVars);
            log($"[DBG] DecryptHWIDOnly result length={jVarsJson.Length}");

            if (string.IsNullOrEmpty(jVarsJson))
                throw new Exception("DecryptHWIDOnly failed: jVars not loaded or corrupted");

            if (!jVarsJson.TrimStart().StartsWith("{"))
                jVarsJson = jVarsJson.FromBase64();

            // Добавляем serverHwid — клиент использует его для расшифровки ключей из _wlt
            var vars = JsonConvert.DeserializeObject<Dictionary<string, string>>(jVarsJson)!;
            vars["serverHwid"] = SAFU.GetStableHWId()
                ?? throw new Exception("HWID resolution failed on server");
            jVarsJson = JsonConvert.SerializeObject(vars);

            log($"[DBG] jVars keys=[{string.Join(", ", vars.Keys)}]");

            var clientJVarsBlob = SAFU.EncryptHWIDOnly(jVarsJson, clientHwid);

            var outDir = !string.IsNullOrEmpty(outputFolder)
                ? Path.Combine(outputFolder, clientName)
                : Path.Combine(AppContext.BaseDirectory, "clients", clientName);
            Directory.CreateDirectory(outDir);

            var jVarsOutPath   = Path.Combine(outDir, "jvars.dat");
            var safuKeyOutPath = Path.Combine(outDir, "safu.key");

            File.WriteAllText(jVarsOutPath, clientJVarsBlob);
            File.WriteAllBytes(safuKeyOutPath, SAFU.LoadOrCreateFileKey());

            log($"[GenerateClientBundle] client={clientName}");
            log($"[GenerateClientBundle] jvars.dat → {jVarsOutPath}");
            log($"[GenerateClientBundle] safu.key  → {safuKeyOutPath}");

            return $"bundle ready: {outDir}";
        });
    }
}