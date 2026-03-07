using System.Text;
using Newtonsoft.Json.Linq;
using Nethereum.Web3.Accounts;
using Nethereum.Signer;
using z3n8;


    public class Unlock
    {
        private static HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly Db _db;
        private const string AUTH_BASE = "https://locksmith.unlock-protocol.com/v2/auth";
        private const string BASE_URL = "https://locksmith.unlock-protocol.com/v2/api/metadata";
        private string _bearerToken;

        public Unlock( Logger log = null, Db db = null, HttpClient httpClient = null)
        {
            _httpClient = httpClient;
            _db = db;
            _logger = log;
        }
        
        
        public async Task<string>  GetKeyMetadata(int chainId, string lockAddress, int tokenId)
        {
            string url = $"{BASE_URL}/{chainId}/locks/{lockAddress}/keys/{tokenId}";
            _logger.Send($"Fetching metadata for key {tokenId} on lock {lockAddress}");

            string response = await _httpClient.GET(url);
            return response;
        }
        public async Task<string> GetUserMetadataAuthenticated(int chainId, string lockAddress, string userAddress)
        {
            if (string.IsNullOrEmpty(_bearerToken))
                throw new Exception("Not authenticated. Call Login() first.");
        
            string url = $"{BASE_URL}/{chainId}/locks/{lockAddress}/users/{userAddress}";
        
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {_bearerToken}");
            request.Headers.Add("Accept", "application/json");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        public async Task<string> GetKeyMetadataAuthenticated(int chainId, string lockAddress, int tokenId)
        {
            if (string.IsNullOrEmpty(_bearerToken))
                throw new Exception("Not authenticated. Call Login() first.");
        
            string url = $"{BASE_URL}/{chainId}/locks/{lockAddress}/keys/{tokenId}";
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {_bearerToken}");
            request.Headers.Add("Accept", "application/json");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        public async Task CollectSubscribersToDb(int chainId, string[] locks, int maxTokenId, string privateKey, string tableName)
        {
            // Login first
            Login(privateKey);
            
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            int dbIndex = 1;
            
            foreach (string lockAddress in locks)
            {
                _logger.Send($"Processing lock: {lockAddress}");
                
                for (int tokenId = 1; tokenId <= maxTokenId; tokenId++)
                {
                    
                    try
                    {
                        string json = await GetKeyMetadataAuthenticated(chainId, lockAddress, tokenId);
                        _logger.Send(json);
                        var data = JObject.Parse(json);
                        string owner = data["owner"]?.ToString();
                        if (string.IsNullOrEmpty(owner))
                        {
                            _logger.Send($"Lock {lockAddress}, Token {tokenId}: no owner, stopping");
                            break;
                        }
                        // Безопасное извлечение всех полей с fallback на пустую строку
                        long expiration = long.Parse(data["expiration"]?.ToString() ?? "0");
                        string github = data["userMetadata"]?["protected"]?["github"]?.ToString() ?? "";
                        string email = data["userMetadata"]?["protected"]?["email"]?.ToString() ?? "";
                        string telegram = data["userMetadata"]?["protected"]?["telegram"]?.ToString() ?? "";
                        string lockName = data["name"]?.ToString() ?? "";

                        bool expired = expiration <= now;
                        
                        var d = new Dictionary<string, string>
                        {
                            { "lock_address", lockAddress },
                            { "lock_name", lockName },
                            { "token_id", tokenId.ToString() },
                            { "owner", owner },
                            { "expired", expired.ToString() },
                            { "expiration", expiration.ToString() },
                            { "github", github },
                            { "email", email },
                            { "telegram", telegram }
                        };
                        _db.DicToDb(d, tableName, log: true, where: $"id = {dbIndex}");
                        _logger.Send($"Lock {lockAddress}, Token {tokenId}: saved (github={github}, expired={expired})");
                        dbIndex++;
                    }
                    catch (Exception ex)
                    {
                        _logger.Send($"!W Lock {lockAddress}, Token {tokenId}: {ex.Message}");
                        // Сохранить хотя бы token_id с ошибкой
                        try
                        {
                            var errorDic = new Dictionary<string, string>
                            {
                                { "lock_address", lockAddress },
                                { "token_id", tokenId.ToString() },
                                { "owner", "" },
                                { "expired", "" },
                                { "expiration", "0" },
                                { "github", "" },
                                { "email", "" },
                                { "telegram", "" }
                            };
                            _db.DicToDb(errorDic, tableName, log: false);
                            
                        }
                        catch { }
                        // Не останавливаться на ошибке, продолжить
                        continue;
                    }
                }
                
                _logger.Send($"Completed lock {lockAddress}");
            }
            
            _logger.Send($"Collected metadata for {locks.Length} locks with max {maxTokenId} tokens each");
        }
        public async Task<Dictionary<string, string>> GetActiveEmails(int chainId, string lockAddress, int maxTokenId = 1000)
        {
            var emails = new Dictionary<string, string>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    
            for (int tokenId = 1; tokenId <= maxTokenId; tokenId++)
            {
                try
                {
                    //string json = GetKeyMetadata(chainId, lockAddress, tokenId);
                    string json = await GetKeyMetadataAuthenticated(chainId, lockAddress, tokenId);
                    
                    var data = JObject.Parse(json);
            
                    string owner = data["owner"]?.ToString();
                    if (string.IsNullOrEmpty(owner))
                    {
                        _logger.Send($"Token {tokenId} has no owner, stopping");
                        break;
                    }
                    
                    //_project.ToJson(json);
                    string email = data["userMetadata"]?["protected"]?["email"]?.ToString();
                    if (string.IsNullOrEmpty(email))
                    {
                        _logger.Send($"Token {tokenId}: no email");
                        continue;
                    }
            
                    string expirationStr = data["expiration"]?.ToString();
                    if (string.IsNullOrEmpty(expirationStr))
                    {
                        _logger.Send($"Token {tokenId}: no expiration");
                        continue;
                    }
            
                    long expiration = long.Parse(expirationStr);
                    if (expiration <= now)
                    {
                        _logger.Send($"Token {tokenId}: expired");
                        continue;
                    }
            
                    emails[owner.ToLower()] = email;
                    _logger.Send($"Token {tokenId}: {email} active");
                }
                catch (Exception ex)
                {
                    _logger.Send($"Token {tokenId}: {ex.Message}, stopping");
                    break;
                }
            }
    
            return emails;
        }
        public async Task<string> GetEmail(int chainId, string lockAddress, int tokenId)
        {
            try
            {
                string json = await GetKeyMetadata(chainId, lockAddress, tokenId);
                var data = JObject.Parse(json);

                string email = data["userMetadata"]?["protected"]?["email"]?.ToString();

                if (string.IsNullOrEmpty(email))
                {
                    _logger.Send($"No email found for token {tokenId}");
                    return null;
                }

                return email;
            }
            catch (Exception ex)
            {
                _logger.Send($"!W Failed to get email: {ex.Message}");
                return null;
            }
        }
        public async Task<Dictionary<string, string>> GetAllEmails(int chainId, string lockAddress, int maxTokenId = 1000)
        {
            var emails = new Dictionary<string, string>();
            
            for (int tokenId = 1; tokenId <= maxTokenId; tokenId++)
            {
                try
                {
                    string json = await GetKeyMetadata(chainId, lockAddress, tokenId);
                    var data = JObject.Parse(json);
                    
                    string email = data["userMetadata"]?["protected"]?["email"]?.ToString();
                    if (string.IsNullOrEmpty(email)) continue;
                    
                    string owner = data["owner"]?.ToString();
                    if (string.IsNullOrEmpty(owner)) continue;
                    
                    emails[owner.ToLower()] = email;
                    _logger.Send($"Token {tokenId}: {owner} -> {email}");
                }
                catch
                {
                    break;
                }
            }
            
            return emails;
        }
        public async Task<string> GetOwner(int chainId, string lockAddress, int tokenId)
        {
            try
            {
                string json = await GetKeyMetadata(chainId, lockAddress, tokenId);
                var data = JObject.Parse(json);
                return data["owner"]?.ToString();
            }
            catch (Exception ex)
            {
                _logger.Send($"!W Failed to get owner: {ex.Message}");
                return null;
            }
        }
        public async Task<string> GetName(int chainId, string lockAddress, int tokenId)
        {
            try
            {
                string json = await GetKeyMetadata(chainId, lockAddress, tokenId);
                var data = JObject.Parse(json);
                return data["name"]?.ToString();
            }
            catch (Exception ex)
            {
                _logger.Send($"!W Failed to get name: {ex.Message}");
                return null;
            }
        }
        

        
       
        #region Authentication
        
        public async Task<string> Login(string privateKey)
        {
            try
            {
                // 1. Get nonce
                string nonce = await GetNonce();
                _logger.Send($"Got nonce: {nonce}");
                
                // 2. Create SIWE message
                var account = new Account(privateKey);
                string address = account.Address;
                string siweMessage = await CreateSiweMessage(address, nonce);
                
                // 3. Sign message
                var signer = new EthereumMessageSigner();
                string signature = signer.EncodeUTF8AndSign(siweMessage, new EthECKey(privateKey));
                
                _logger.Send($"Signed message for {address}");
                
                // 4. Login and get token
                string token = await PerformLogin(siweMessage, signature);
                _bearerToken = token;
                
                _logger.Send($"✅ Authenticated successfully");
                return token;
            }
            catch (Exception ex)
            {
                _logger.Send($"!W Login failed: {ex.Message}");
                throw;
            }
        }
        
        private async Task<string> GetNonce()
        {
            var response = _httpClient.GetAsync($"{AUTH_BASE}/nonce").Result;
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().Result.Trim();
        }
        
        private async Task<string> CreateSiweMessage(string address, string nonce)
        {
            string issuedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    
            return "app.unlock-protocol.com wants you to sign in with your Ethereum account:\n" +
                   $"{address}\n\n" +
                   "By signing, you are proving you own this wallet and logging in. This does not initiate a transaction or cost any fees.\n\n" +
                   "URI: https://app.unlock-protocol.com\n" +
                   "Version: 1\n" +
                   "Chain ID: 8453\n" +
                   $"Nonce: {nonce}\n" +
                   $"Issued At: {issuedAt}\n" +
                   "Resources:\n" +
                   "- https://privy.io";
        }
        
        private async Task<string> PerformLogin(string message, string signature)
        {
            var payload = new
            {
                message = message,
                signature = signature
            };
			
            var body = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
			_logger.Send(body);
            var jsonResponse = await _httpClient.POST ($"{AUTH_BASE}/login", body);
			_logger.Send(jsonResponse);
            var data = JObject.Parse(jsonResponse);
                
            string accessToken = data["accessToken"]?.ToString();
            if (string.IsNullOrEmpty(accessToken))
                throw new Exception("No accessToken in response");
                    
            return accessToken;
            
        }
    
        #endregion
        
        #region Sync
        public Dictionary<string, string> GetActiveGitHubFromDb(string tableName)
        {
            var users = new Dictionary<string, string>(); 
           
            try
            {
                var activeRecords = _db.GetLines(
                    "owner, github", 
                    tableName: tableName, 
                    where: "expired = 'False' AND github != ''",
                    log: true
                );
        
                foreach (var record in activeRecords)
                {
                    var parts = record.Split('¦');
                    if (parts.Length < 2) continue;
            
                    string owner = parts[0].Trim().ToLower();
                    string github = parts[1].Trim();
            
                    if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(github))
                    {
                        users[owner] = github;
                        _logger.Send($"Active: {owner} → {github}");
                    }
                }
        
                _logger.Send($"Found {users.Count} active subscribers with GitHub");
            }
            catch (Exception ex)
            {
                _logger.Send($"!W Failed to get active users from DB: {ex.Message}");
            }
    
            return users;
        }
        #endregion
        
    }
