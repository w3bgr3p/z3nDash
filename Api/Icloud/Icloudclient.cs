using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace iCloudApi
{
    public class iCloudClient
    {
        private readonly HttpClient _httpClient;
        private iCloudSession _session;

        public string Username { get; private set; }
        public string Password { get; private set; }
        public bool LoggedIn { get; private set; }
        public bool TwoFactorAuthenticationRequired { get; private set; }
        
        // API сервисы
        public MailService Mail { get; private set; }

        public iCloudClient(string username = null, string password = null, iCloudSession session = null)
        {
            _httpClient = new HttpClient(new HttpClientHandler
            {
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });

            Username = username;
            Password = password;
            
            _session = session ?? new iCloudSession
            {
                ClientSettings = new ClientSettings(),
                Auth = new AuthInfo(),
                Push = new PushInfo(),
                ClientId = GenerateClientId()
            };

            Mail = new MailService(this);
        }

        public async Task<bool> LoginAsync()
        {
            try
            {
                // Шаг 1: Получение токена аутентификации
                var authToken = await GetAuthTokenAsync(Username, Password);
                
                if (authToken == null)
                {
                    throw new Exception("Failed to get authentication token");
                }

                _session.Auth.Token = authToken.Token;
                _session.ClientSettings.XAppleIDSessionId = authToken.SessionId;
                _session.ClientSettings.Scnt = authToken.Scnt;

                // Проверка на 2FA
                if (authToken.AuthType == "hsa2")
                {
                    TwoFactorAuthenticationRequired = true;
                    return false;
                }

                // Шаг 2: Логин в аккаунт
                var accountInfo = await AccountLoginAsync();
                
                if (accountInfo == null)
                {
                    throw new Exception("Account login failed");
                }

                _session.Account = accountInfo;
                LoggedIn = true;

                // Шаг 3: Получение push токена (опционально)
                try
                {
                    await GetPushTokenAsync();
                }
                catch
                {
                    // Push token не критичен для работы Mail API
                }

                return true;
            }
            catch (Exception ex)
            {
                throw new Exception($"Login failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> EnterSecurityCodeAsync(string code)
        {
            if (!TwoFactorAuthenticationRequired)
            {
                return true;
            }

            try
            {
                var host = "idmsa.apple.com";
                var signInReferer = $"https://{host}/appleauth/auth/signin?widgetKey={_session.ClientSettings.XAppleWidgetKey}&locale={_session.ClientSettings.Locale}&font=sf";

                // Отправка кода безопасности
                var codeRequest = new
                {
                    securityCode = new { code = code }
                };

                var codeResponse = await SendRequestAsync(
                    $"https://{host}/appleauth/auth/verify/trusteddevice/securitycode",
                    HttpMethod.Post,
                    JsonConvert.SerializeObject(codeRequest),
                    new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/json",
                        ["Referer"] = signInReferer,
                        ["Host"] = host,
                        ["X-Apple-Widget-Key"] = _session.ClientSettings.XAppleWidgetKey,
                        ["X-Apple-I-FD-Client-Info"] = JsonConvert.SerializeObject(_session.ClientSettings.XAppleIFDClientInfo),
                        ["X-Apple-ID-Session-Id"] = _session.ClientSettings.XAppleIDSessionId,
                        ["scnt"] = _session.ClientSettings.Scnt
                    }
                );

                // Доверие устройству
                var trustResponse = await SendRequestAsync(
                    $"https://{host}/appleauth/auth/2sv/trust",
                    HttpMethod.Post,
                    null,
                    new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/json",
                        ["Referer"] = signInReferer,
                        ["Host"] = host,
                        ["X-Apple-Widget-Key"] = _session.ClientSettings.XAppleWidgetKey,
                        ["X-Apple-I-FD-Client-Info"] = JsonConvert.SerializeObject(_session.ClientSettings.XAppleIFDClientInfo),
                        ["X-Apple-ID-Session-Id"] = _session.ClientSettings.XAppleIDSessionId,
                        ["scnt"] = _session.ClientSettings.Scnt
                    }
                );

                // Обновление токенов из заголовков ответа
                if (trustResponse.Headers.TryGetValues("x-apple-session-token", out var sessionTokens))
                {
                    _session.Auth.Token = sessionTokens.FirstOrDefault();
                }

                if (trustResponse.Headers.TryGetValues("x-apple-twosv-trust-token", out var trustTokens))
                {
                    _session.Auth.XAppleTwosvTrustToken = trustTokens.FirstOrDefault();
                }

                // Финальный логин с trust token
                var accountInfo = await AccountLoginAsync(_session.Auth.XAppleTwosvTrustToken);
                
                if (accountInfo == null)
                {
                    return false;
                }

                _session.Account = accountInfo;
                LoggedIn = true;
                TwoFactorAuthenticationRequired = false;

                return true;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to enter security code: {ex.Message}", ex);
            }
        }

        private async Task<AuthTokenResponse> GetAuthTokenAsync(string username, string password)
        {
            var xAppleIFDClientInfo = new
            {
                U = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/603.3.1 (KHTML, like Gecko) Version/10.1.2 Safari/603.3.1",
                L = _session.ClientSettings.Locale,
                Z = "GMT+02:00",
                V = "1.1",
                F = ""
            };

            var loginData = new
            {
                accountName = username,
                password = password,
                rememberMe = true,
                trustTokens = new string[] { }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://idmsa.apple.com/appleauth/auth/signin")
            {
                Content = new StringContent(JsonConvert.SerializeObject(loginData), Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Accept", "application/json, text/javascript, */*; q=0.01");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/603.3.1 (KHTML, like Gecko) Version/10.1.2 Safari/603.3.1");
            request.Headers.Add("Origin", "https://idmsa.apple.com");
            request.Headers.Add("Referer", "https://idmsa.apple.com/appleauth/auth/signin");
            request.Headers.Add("X-Apple-Widget-Key", _session.ClientSettings.XAppleWidgetKey);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Add("X-Apple-I-FD-Client-Info", JsonConvert.SerializeObject(xAppleIFDClientInfo));

            var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Authentication failed: {content}");
            }

            // Парсинг ответа
            var result = JObject.Parse(content);

            // Извлечение токена из заголовков
            var sessionToken = response.Headers.TryGetValues("x-apple-session-token", out var tokens) 
                ? tokens.FirstOrDefault() 
                : null;
                
            var sessionId = response.Headers.TryGetValues("x-apple-id-session-id", out var ids)
                ? ids.FirstOrDefault()
                : null;
                
            var scnt = response.Headers.TryGetValues("scnt", out var scnts)
                ? scnts.FirstOrDefault()
                : null;

            // Обработка cookies
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                ParseAndStoreCookies(setCookies);
            }

            var authType = result["authType"]?.ToString();

            return new AuthTokenResponse
            {
                Token = sessionToken,
                SessionId = sessionId,
                Scnt = scnt,
                AuthType = authType
            };
        }

        private async Task<AccountInfo> AccountLoginAsync(string trustToken = null)
        {
            var authData = new
            {
                dsWebAuthToken = _session.Auth.Token,
                extended_login = true,
                trustToken = trustToken
            };

            var url = $"https://setup.icloud.com/setup/ws/1/accountLogin?clientBuildNumber={_session.ClientSettings.ClientBuildNumber}&clientId={_session.ClientId}&clientMasteringNumber={_session.ClientSettings.ClientMasteringNumber}";

            var response = await SendRequestAsync(
                url,
                HttpMethod.Post,
                JsonConvert.SerializeObject(authData),
                new Dictionary<string, string>
                {
                    ["Content-Type"] = "text/plain",
                    ["Referer"] = "https://www.icloud.com/",
                    ["Accept"] = "*/*",
                    ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/603.3.1 (KHTML, like Gecko) Version/10.1.2 Safari/603.3.1",
                    ["Origin"] = "https://www.icloud.com"
                }
            );

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<AccountInfo>(content);

            // Обработка cookies
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                ParseAndStoreCookies(setCookies);
            }

            return result;
        }

        private async Task GetPushTokenAsync()
        {
            if (_session.Account?.Webservices?.Push == null)
            {
                return;
            }

            var host = GetHostFromWebservice(_session.Account.Webservices.Push);
            var tokenData = new
            {
                pushTopics = _session.Push.Topics,
                pushTokenTTL = _session.Push.Ttl
            };

            var url = $"https://{host}/getToken?attempt=1&clientBuildNumber={_session.ClientSettings.ClientBuildNumber}&clientId={_session.ClientId}&clientMasteringNumber={_session.ClientSettings.ClientMasteringNumber}&dsid={_session.Account.DsInfo.Dsid}";

            var response = await SendRequestAsync(
                url,
                HttpMethod.Post,
                JsonConvert.SerializeObject(tokenData),
                new Dictionary<string, string>
                {
                    ["Host"] = host,
                    ["Content-Type"] = "text/plain"
                }
            );

            var content = await response.Content.ReadAsStringAsync();
            var result = JObject.Parse(content);

            if (result["pushToken"] != null)
            {
                _session.Push.Token = result["pushToken"].ToString();
            }

            if (result["webCourierURL"] != null)
            {
                _session.Push.CourierUrl = result["webCourierURL"].ToString();
            }
        }

        internal async Task<HttpResponseMessage> SendRequestAsync(
            string url, 
            HttpMethod method, 
            string body = null,
            Dictionary<string, string> additionalHeaders = null)
        {
            var request = new HttpRequestMessage(method, url);

            // Добавление cookies
            if (_session.Auth.Cookies.Any())
            {
                request.Headers.Add("Cookie", CookiesToString(_session.Auth.Cookies));
            }

            // Добавление дополнительных заголовков
            if (additionalHeaders != null)
            {
                foreach (var header in additionalHeaders)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            // Добавление тела запроса
            if (!string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                if (additionalHeaders != null && additionalHeaders.ContainsKey("Content-Type"))
                {
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(additionalHeaders["Content-Type"]);
                }
            }

            var response = await _httpClient.SendAsync(request);

            // Обработка новых cookies
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                ParseAndStoreCookies(setCookies);
            }

            return response;
        }

        private void ParseAndStoreCookies(IEnumerable<string> setCookies)
        {
            foreach (var cookieStr in setCookies)
            {
                var parts = cookieStr.Split(';');
                if (parts.Length > 0)
                {
                    var mainPart = parts[0].Trim();
                    var keyValue = mainPart.Split('=', 2);
                    
                    if (keyValue.Length == 2)
                    {
                        var cookieName = keyValue[0].Trim();
                        var cookieValue = keyValue[1].Trim();

                        // Удаление старой cookie с таким же именем
                        _session.Auth.Cookies.RemoveAll(c => c.ContainsKey(cookieName));
                        
                        // Добавление новой cookie
                        var cookieDict = new Dictionary<string, string> { [cookieName] = cookieValue };
                        
                        // Парсинг дополнительных атрибутов
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var attr = parts[i].Trim();
                            var attrParts = attr.Split('=', 2);
                            if (attrParts.Length == 2)
                            {
                                cookieDict[attrParts[0].Trim()] = attrParts[1].Trim();
                            }
                            else if (attrParts.Length == 1)
                            {
                                cookieDict[attrParts[0].Trim()] = "true";
                            }
                        }
                        
                        _session.Auth.Cookies.Add(cookieDict);
                    }
                }
            }
        }

        private string CookiesToString(List<Dictionary<string, string>> cookies)
        {
            return string.Join("; ", cookies.Select(c => 
            {
                var mainKey = c.Keys.FirstOrDefault(k => 
                    k != "Path" && k != "Domain" && k != "Expires" && 
                    k != "Secure" && k != "HttpOnly" && k != "SameSite" && k != "Max-Age");
                return mainKey != null ? $"{mainKey}=\"{c[mainKey]}\"" : string.Empty;
            }).Where(s => !string.IsNullOrEmpty(s)));
        }

        private string GetHostFromWebservice(WebserviceInfo webservice)
        {
            return webservice?.Url?
                .Replace(":443", "")
                .Replace("https://", "")
                .Replace("http://", "") ?? string.Empty;
        }

        private static string GenerateClientId()
        {
            var structure = new[] { 8, 4, 4, 4, 12 };
            var chars = "0123456789ABCDEF";
            var random = new Random();

            return string.Join("-", structure.Select(length =>
                new string(Enumerable.Range(0, length)
                    .Select(_ => chars[random.Next(chars.Length)])
                    .ToArray())
            ));
        }

        public iCloudSession ExportSession()
        {
            return _session;
        }

        public void ImportSession(iCloudSession session)
        {
            _session = session;
            LoggedIn = session.Auth?.Token != null && session.Account != null;
        }

        internal iCloudSession GetSession()
        {
            return _session;
        }
    }
}