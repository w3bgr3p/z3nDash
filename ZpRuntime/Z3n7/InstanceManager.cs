// Срез z3n7/Accounts/InstanceManager.cs — пока только ProxySet.
//
// Эталонный файл на 602 строки: сам InstanceManager, Disposer, RunBrowser,
// Finish, ReportError/ReportSuccess. Всё это про жизненный цикл инстанса ZP,
// который у нас устроен иначе — браузер поднимает BrowserSession, а задачами
// заведует планировщик z3nDash. Переносить его целиком до того, как решено, чем
// подпирать эти контракты, значит тащить чужой жизненный цикл.
//
// ProxySet зависимостей на всё это не имеет: DbGet, GET и instance.SetProxy —
// готовы. Он же и понадобился первым: его зовёт ветка шаблона simroute_test.
//
// Когда дойдёт очередь до остального файла, этот срез заменяется им, а не
// дополняется — как и Canvas.cs.

using System;
using ZennoLab.CommandCenter;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3n7
{
    public static partial class ProjectExtensions
    {
        public static bool ProxySet(this IZennoPosterProjectModel project, Instance instance,string proxyString = null)
        {
            if (string.IsNullOrWhiteSpace(proxyString))
                proxyString = project.DbGet("proxy", "_instance");

            if (string.IsNullOrWhiteSpace(proxyString))
                throw new ArgumentException("Proxy string is empty");

            var ipServices = new[] {
                "https://api.ipify.org/",
                "https://icanhazip.com/",
                "https://ifconfig.me/ip",
                "https://checkip.amazonaws.com/",
                "https://ident.me/"
            };

            string ipLocal = null;
            string ipProxified = null;

            foreach (var service in ipServices)
            {
                try
                {
                    ipLocal = project.GET(service, null)?.Trim();
                    if (!string.IsNullOrEmpty(ipLocal) && System.Net.IPAddress.TryParse(ipLocal, out _))
                    {
                        ipProxified = project.GET(service, proxyString, useNetHttp: false)?.Trim();
                        if (!string.IsNullOrEmpty(ipProxified) && System.Net.IPAddress.TryParse(ipProxified, out _))
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            if (string.IsNullOrEmpty(ipProxified) || !System.Net.IPAddress.TryParse(ipProxified, out _))
            {
                throw new Exception($"proxy check failed: proxyString=[{proxyString}]");
            }
            if (ipProxified != ipLocal)
            {
                instance.SetProxy(proxyString, true, true, true, true);
                return true;
            }
            throw new Exception($"proxy check failed: proxyString=[{proxyString}]");
        }

        // ── SaveProfile ───────────────────────────────────────────────────────
        // Перенесено из z3n7/Accounts/InstanceManager.cs. Зовёт ветка шаблона
        // simroute.bolt.lgn: project.SaveProfile(instance).
        //
        // Экспорт отпечатка профиля в JSON: простые свойства IProfile и Instance
        // собираются рефлексией, к ним добавляются настройки WebGL и куки.
        //
        // Отступление от эталона одно: там instance.WebGLPreferences.Save(), у нас
        // это свойство — строка, поэтому берётся как есть. Остальное дословно.
        public static void SaveProfile(this IZennoPosterProjectModel project, Instance instance)
        {
            var profileData = new System.Collections.Generic.Dictionary<string, object>();
            foreach (var prop in typeof(ZennoLab.InterfacesLibrary.ProjectModel.Collections.IProfile).GetProperties())
            {
                if (!prop.CanRead || prop.GetMethod?.IsPublic != true) continue;
                var t = prop.PropertyType;
                bool isSimple = t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t.IsEnum;
                if (isSimple)
                {
                    try
                    {
                        var val = prop.GetValue(project.Profile, null);
                        if (val != null) profileData[prop.Name] = val;
                    }
                    catch { }
                }
            }

            var instanceData = new System.Collections.Generic.Dictionary<string, object>();
            foreach (var prop in typeof(Instance).GetProperties())
            {
                if (!prop.CanRead || prop.GetMethod?.IsPublic != true) continue;
                var t = prop.PropertyType;
                bool isSimple = t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t.IsEnum;
                if (isSimple)
                {
                    try
                    {
                        var val = prop.GetValue(instance, null);
                        if (val != null) instanceData[prop.Name] = val;
                    }
                    catch { }
                }
            }

            string webglData = "";
            try { webglData = instance.WebGLPreferences; } catch { }

            string cookiesBase64 = "";
            try
            {
                cookiesBase64 = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(instance.GetCookie() ?? ""));
            }
            catch { }

            var exportBundle = new System.Collections.Generic.Dictionary<string, object>
            {
                { "timestamp", DateTime.UtcNow.ToString("o") },
                { "profile", profileData },
                { "instance", instanceData },
                { "_preferences", webglData },
                { "cookies", cookiesBase64 }
            };

            string profilesDir = System.IO.Path.Combine(project.Directory, "profiles");
            System.IO.Directory.CreateDirectory(profilesDir);

            string fileName = $"zenno_profile_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 6)}.json";
            string fullPath = System.IO.Path.Combine(profilesDir, fileName);

            string jsonOutput = Newtonsoft.Json.JsonConvert.SerializeObject(
                exportBundle, Newtonsoft.Json.Formatting.Indented);
            System.IO.File.WriteAllText(fullPath, jsonOutput, System.Text.Encoding.UTF8);

            project.SendInfoToLog($"[Fingerprint] Профиль успешно сохранен: {fullPath}");
        }
    }
}
