// Срез z3n7/Accounts/InstanceManager.cs — пока только ProxySet.
//
// Эталонный файл на 602 строки: сам InstanceManager, Disposer, RunBrowser,
// Finish, ReportError/ReportSuccess. Всё это про жизненный цикл инстанса ZP,
// который у нас устроен иначе — браузер поднимает BrowserSession, а задачами
// заведует планировщик DevDeck. Переносить его целиком до того, как решено, чем
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
    }
}
