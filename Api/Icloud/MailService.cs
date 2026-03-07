using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace iCloudApi
{
    public class MailService
    {
        private readonly iCloudClient _client;
        private const string MailHost = "p44-mailws.icloud.com";
        private List<MailPreference> _preferences;

        public MailService(iCloudClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Получить список папок почты
        /// </summary>
        public async Task<List<MailFolder>> GetFoldersAsync()
        {
            var result = await FolderOperationAsync("list", null);
            return result.ToObject<List<MailFolder>>();
        }

        /// <summary>
        /// Получить список сообщений из папки
        /// </summary>
        /// <param name="folder">Папка для получения сообщений</param>
        /// <param name="selected">Начальная позиция (по умолчанию 1)</param>
        /// <param name="count">Количество сообщений (по умолчанию 50)</param>
        public async Task<MailMessageList> ListMessagesAsync(MailFolder folder, int selected = 1, int count = 50)
        {
            var content = new
            {
                jsonrpc = "2.0",
                id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/1",
                method = "list",
                @params = new
                {
                    guid = folder.Guid,
                    sorttype = "Date",
                    sortorder = "descending",
                    searchtype = (string)null,
                    searchtext = (string)null,
                    requesttype = "index",
                    selected = selected,
                    count = count,
                    rollbackslot = "0.0"
                },
                userStats = new { },
                systemStats = new[] { 0, 0, 0, 0 }
            };

            var response = await MessageOperationAsync(JsonConvert.SerializeObject(content));

            if (response["result"] != null)
            {
                var resultArray = response["result"].ToArray();
                
                var meta = resultArray.FirstOrDefault(m => 
                    m["type"]?.ToString() == "CoreMail.MessageListMetaData");
                
                var messages = resultArray
                    .Where(m => m["type"]?.ToString() == "CoreMail.Message")
                    .Select(m => m.ToObject<MailMessage>())
                    .ToList();

                return new MailMessageList
                {
                    Meta = meta != null ? meta.ToObject<MailMessageListMeta>() : null,
                    Messages = messages
                };
            }

            throw new Exception("Failed to list messages");
        }

        /// <summary>
        /// Получить полное содержимое сообщения
        /// </summary>
        public async Task<MailMessageDetail> GetMessageAsync(MailMessage mail)
        {
            var content = new
            {
                jsonrpc = "2.0",
                id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/1",
                method = "get",
                @params = new
                {
                    guid = mail.Guid,
                    parts = mail.Parts?.Select(p => p.Guid).ToArray() ?? Array.Empty<string>()
                },
                userStats = new { },
                systemStats = new[] { 0, 0, 0, 0 }
            };

            var response = await MessageOperationAsync(JsonConvert.SerializeObject(content));

            if (response["result"] != null)
            {
                var resultArray = response["result"].ToArray();
                var detailElement = resultArray.FirstOrDefault(r =>
                    r["recordType"]?.ToString() == "CoreMail.MessageDetail");

                if (detailElement != null)
                {
                    var detail = detailElement.ToObject<MailMessageDetail>();

                    // Объединение частей сообщения
                    if (detail.Parts != null && detail.Parts.Any())
                    {
                        detail.Data = string.Join("", detail.Parts.Select(p => p.Content ?? ""));
                        detail.Parts = null;
                    }

                    return detail;
                }
            }

            throw new Exception("Failed to get message details");
        }

        /// <summary>
        /// Переместить сообщения в другую папку
        /// </summary>
        public async Task<JObject> MoveMessagesAsync(List<MailMessage> messages, MailFolder destination)
        {
            if (messages == null || !messages.Any())
            {
                throw new ArgumentException("Messages list cannot be empty");
            }

            var folder = messages.First().Folder;
            if (messages.Any(m => m.Folder != folder))
            {
                throw new Exception("All messages must be in the same folder");
            }

            var content = new
            {
                jsonrpc = "2.0",
                id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/1",
                method = "move",
                @params = new
                {
                    folder = folder,
                    dest = destination.Guid,
                    uids = messages.Select(m => m.Uid).ToArray(),
                    rollbackslot = "0.0"
                },
                userStats = new
                {
                    tm = 1,
                    ae = 1
                },
                systemStats = new[] { 0, 0, 0, 0 }
            };

            return await MessageOperationAsync(JsonConvert.SerializeObject(content));
        }

        /// <summary>
        /// Пометить сообщения флагом
        /// </summary>
        /// <param name="messages">Список сообщений</param>
        /// <param name="flag">Флаг (flagged, unread)</param>
        public async Task<JObject> FlagMessagesAsync(List<MailMessage> messages, string flag = "flagged")
        {
            return await FlagOperationAsync("setflag", flag, messages);
        }

        /// <summary>
        /// Снять флаг с сообщений
        /// </summary>
        /// <param name="messages">Список сообщений</param>
        /// <param name="flag">Флаг (flagged, unread)</param>
        public async Task<JObject> UnflagMessagesAsync(List<MailMessage> messages, string flag = "flagged")
        {
            return await FlagOperationAsync("clrflag", flag, messages);
        }

        /// <summary>
        /// Удалить сообщения
        /// </summary>
        public async Task<JObject> DeleteMessagesAsync(List<MailMessage> messages)
        {
            if (messages == null || !messages.Any())
            {
                throw new ArgumentException("Messages list cannot be empty");
            }

            var folder = messages.First().Folder;
            if (messages.Any(m => m.Folder != folder))
            {
                throw new Exception("All messages must be in the same folder");
            }

            var content = new
            {
                jsonrpc = "2.0",
                id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/1",
                method = "delete",
                @params = new
                {
                    folder = folder,
                    uids = messages.Select(m => m.Uid).ToArray(),
                    rollbackslot = "0.0"
                },
                userStats = new { },
                systemStats = new[] { 0, 0, 0, 0 }
            };

            return await MessageOperationAsync(JsonConvert.SerializeObject(content));
        }

        /// <summary>
        /// Отправить письмо
        /// </summary>
        public async Task<JObject> SendMailAsync(MailOutgoingMessage message)
        {
            // Получение адреса отправителя, если не указан
            if (string.IsNullOrEmpty(message.From))
            {
                if (_preferences == null)
                {
                    _preferences = await GetPreferencesAsync();
                }

                var defaultEmail = _preferences
                    .Skip(1)
                    .FirstOrDefault()?
                    .Emails?
                    .FirstOrDefault(e => e.CanSendFrom);

                if (defaultEmail != null)
                {
                    var fullName = _preferences.Skip(1).FirstOrDefault()?.FullName ?? "";
                    message.From = $"{fullName}<{defaultEmail.Address}>";
                }
            }

            // Установка значений по умолчанию
            message.Date ??= DateTime.UtcNow.ToString("R");
            message.TextBody ??= System.Text.RegularExpressions.Regex.Replace(
                message.Body ?? "", 
                "<[^<>]*>", 
                "");
            message.WebmailClientBuild ??= _client.GetSession().ClientSettings.ClientBuildNumber;
            message.Attachments ??= new List<object>();

            var content = new
            {
                jsonrpc = "2.0",
                id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/1",
                method = "send",
                @params = message,
                userStats = new
                {
                    biuc = 1
                },
                systemStats = new[] { 0, 0, 0, 0 }
            };

            return await MessageOperationAsync(JsonConvert.SerializeObject(content));
        }

        /// <summary>
        /// Создать папку
        /// </summary>
        public async Task<JToken> CreateFolderAsync(string name, MailFolder parent = null)
        {
            var parameters = new Dictionary<string, object>
            {
                ["name"] = name,
                ["parent"] = parent?.Guid
            };

            return await FolderOperationAsync("put", parameters);
        }

        /// <summary>
        /// Переименовать папку
        /// </summary>
        public async Task<JToken> RenameFolderAsync(MailFolder folder, string newName)
        {
            folder.Name = newName;
            return await FolderOperationAsync("rename", folder);
        }

        /// <summary>
        /// Переместить папку
        /// </summary>
        public async Task<JToken> MoveFolderAsync(MailFolder folder, MailFolder target = null)
        {
            if (target != null)
            {
                folder.Parent = target.Guid;
            }
            return await FolderOperationAsync("move", folder);
        }

        /// <summary>
        /// Удалить папку
        /// </summary>
        public async Task<JToken> DeleteFolderAsync(MailFolder folder)
        {
            return await FolderOperationAsync("delete", new { guid = folder.Guid });
        }

        // Приватные вспомогательные методы

        private async Task<JObject> FlagOperationAsync(string method, string flag, List<MailMessage> messages)
        {
            if (messages == null || !messages.Any())
            {
                throw new ArgumentException("Messages list cannot be empty");
            }

            var folder = messages.First().Folder;
            if (messages.Any(m => m.Folder != folder))
            {
                throw new Exception("All messages must be in the same folder");
            }

            var content = new
            {
                jsonrpc = "2.0",
                id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/1",
                method = method,
                @params = new
                {
                    folder = folder,
                    uids = messages.Select(m => m.Uid).ToArray(),
                    flag = flag,
                    rollbackslot = "0.0"
                },
                userStats = new { },
                systemStats = new[] { 0, 0, 0, 0 }
            };

            return await MessageOperationAsync(JsonConvert.SerializeObject(content));
        }

        private async Task<JObject> MessageOperationAsync(string content)
        {
            var session = _client.GetSession();
            var url = $"https://{MailHost}/wm/message?clientBuildNumber={session.ClientSettings.ClientBuildNumber}&clientId={session.ClientId}&clientMasteringNumber={session.ClientSettings.ClientMasteringNumber}&dsid={session.Account.DsInfo.Dsid}";

            var headers = new Dictionary<string, string>
            {
                ["Host"] = MailHost,
                ["Content-Type"] = "text/plain",
                ["Referer"] = "https://www.icloud.com/",
                ["Accept"] = "*/*",
                ["Origin"] = "https://www.icloud.com",
                ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/604.1.25 (KHTML, like Gecko) Version/11.0 Safari/604.1.25",
                ["Connection"] = "keep-alive",
                ["X-Requested-With"] = "XMLHttpRequest",
                ["Content-Length"] = content.Length.ToString()
            };

            var response = await _client.SendRequestAsync(url, HttpMethod.Post, content, headers);
            var responseContent = await response.Content.ReadAsStringAsync();

            try
            {
                return JObject.Parse(responseContent);
            }
            catch
            {
                // Если парсинг не удался, может быть требуется обновление cookies
                // Повторяем запрос
                response = await _client.SendRequestAsync(url, HttpMethod.Post, content, headers);
                responseContent = await response.Content.ReadAsStringAsync();
                return JObject.Parse(responseContent);
            }
        }

        private async Task<JToken> FolderOperationAsync(string method, object parameters)
        {
            var session = _client.GetSession();
            var url = $"https://{MailHost}/wm/folder?clientBuildNumber={session.ClientSettings.ClientBuildNumber}&clientId={session.ClientId}&clientMasteringNumber={session.ClientSettings.ClientMasteringNumber}&dsid={session.Account.DsInfo.Dsid}";

            var content = new
            {
                jsonrpc = "2.0",
                id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/1",
                method = method,
                @params = parameters,
                userStats = new { },
                systemStats = new[] { 0, 0, 0, 0 }
            };

            var contentStr = JsonConvert.SerializeObject(content);

            var headers = new Dictionary<string, string>
            {
                ["Host"] = MailHost,
                ["Content-Type"] = "text/plain",
                ["Referer"] = "https://www.icloud.com/",
                ["Accept"] = "*/*",
                ["Origin"] = "https://www.icloud.com",
                ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/604.1.25 (KHTML, like Gecko) Version/11.0 Safari/604.1.25",
                ["Connection"] = "keep-alive",
                ["X-Requested-With"] = "XMLHttpRequest",
                ["Content-Length"] = contentStr.Length.ToString()
            };

            var response = await _client.SendRequestAsync(url, HttpMethod.Post, contentStr, headers);
            var responseContent = await response.Content.ReadAsStringAsync();

            var result = JObject.Parse(responseContent);

            if (result["result"] != null)
            {
                return result["result"];
            }
            else if (result["error"] != null)
            {
                throw new Exception($"Folder operation failed: {result["error"]}");
            }

            return result;
        }

        private async Task<List<MailPreference>> GetPreferencesAsync()
        {
            var session = _client.GetSession();
            var url = $"https://{MailHost}/wm/preference?clientBuildNumber={session.ClientSettings.ClientBuildNumber}&clientId={session.ClientId}&clientMasteringNumber={session.ClientSettings.ClientMasteringNumber}&dsid={session.Account.DsInfo.Dsid}";

            var content = new
            {
                jsonrpc = "2.0",
                id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/1",
                method = "list",
                @params = new
                {
                    locale = session.ClientSettings.Locale,
                    timeZone = session.ClientSettings.Timezone
                },
                userStats = new
                {
                    dm = "Widescreen",
                    ost = "Date",
                    osv = "Descending",
                    al = 0,
                    vro = 2,
                    so = 2
                },
                systemStats = new[] { 0, 0, 0, 0 }
            };

            var contentStr = JsonConvert.SerializeObject(content);

            var headers = new Dictionary<string, string>
            {
                ["Host"] = MailHost,
                ["Content-Type"] = "text/plain",
                ["Content-Length"] = contentStr.Length.ToString()
            };

            var response = await _client.SendRequestAsync(url, HttpMethod.Post, contentStr, headers);
            var responseContent = await response.Content.ReadAsStringAsync();

            var result = JObject.Parse(responseContent);

            if (result["result"] != null)
            {
                return result["result"].ToObject<List<MailPreference>>();
            }

            throw new Exception("Failed to get preferences");
        }
    }
}