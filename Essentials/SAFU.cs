using System.Management;

using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace z3n8;

public class SAFU
{
    static string GetStableHWId(bool log = false, string forced = null)
    {
        
        var components = new List<string>();
        try
        {
            // 1. Процессор
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    var id = mo["ProcessorId"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) { components.Add(id); break; }
                }
            }

            // 2. Материнская плата (без фильтров, как в оригинале)
            using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    var serial = mo["SerialNumber"]?.ToString();
                    if (!string.IsNullOrEmpty(serial)) { components.Add(serial); break; }
                }
            }

            // 3. Физический серийник диска, на котором реально живет ОС
            try 
            {
                // Узнаем букву системного раздела (хоть C:, хоть Z:)
                string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
                string driveLetter = sysRoot.Replace("\\", ""); // Получим "C:"

                using (var logDiskSearcher = new ManagementObjectSearcher($"SELECT DeviceID FROM Win32_LogicalDisk WHERE DeviceID = '{driveLetter}'"))
                {
                    foreach (ManagementObject logDisk in logDiskSearcher.Get())
                    {
                        using (var partSearcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{logDisk["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition"))
                        {
                            foreach (ManagementObject partition in partSearcher.Get())
                            {
                                using (var driveSearcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition"))
                                {
                                    foreach (ManagementObject drive in driveSearcher.Get())
                                    {
                                        var serial = drive["SerialNumber"]?.ToString();
                                        if (!string.IsNullOrEmpty(serial)) { components.Add(serial); break; }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch 
            {
                // Если сложная цепочка не сработала — берем первый диск (legacy fallback)
                using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive"))
                    foreach (ManagementObject mo in searcher.Get()) { components.Add(mo["SerialNumber"]?.ToString()); break; }
            }

            if (components.Count == 0) throw new Exception("No hardware components found");

            using (var sha256 = SHA256.Create())
            {
                // СТРОГО ТВОЙ ПРЕФИКС И ФОРМАТ
                var rawData = $"HW_ID_V2:{string.Join(":", components)}";
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                return Convert.ToBase64String(hashBytes);
            }
        }
        catch (Exception ex)
        {
            //if (log) proj.SendWarningToLog($"HWId V2 Generation failed: {ex.Message}");
            return null;
        }
    }

    static byte[] DeriveSecureKey(string pin, string hardwareId, string accountId)
    {
        if (string.IsNullOrEmpty(hardwareId)) return null;
        if (string.IsNullOrEmpty(pin)) pin = "UNPROTECTED";
        
        using (var sha256 = SHA256.Create())
        {
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes($"SALT_V2:{hardwareId}:{accountId}"));
            byte[] salt = new byte[16];
            Array.Copy(hashBytes, salt, 16); 
            
            using (var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, 100000)) return pbkdf2.GetBytes(32);
        }
    }

    static string SecureAESDecrypt( string ciphertext, byte[] key)
    {
        try
        {
            var data = Convert.FromBase64String(ciphertext);
            if (data.Length < 48) return string.Empty;

            var hmacSize = 32;
            var payload = new byte[data.Length - hmacSize];
            var receivedHmac = new byte[hmacSize];
            Array.Copy(data, 0, payload, 0, payload.Length);
            Array.Copy(data, payload.Length, receivedHmac, 0, hmacSize);

            using (var hmac = new HMACSHA256(key))
            {
                var computedHmac = hmac.ComputeHash(payload);
                for (int i = 0; i < hmacSize; i++)
                    if (receivedHmac[i] != computedHmac[i])
                        return string.Empty;
            }

            var iv = new byte[16];
            var encrypted = new byte[payload.Length - 16];
            Array.Copy(payload, 0, iv, 0, 16);
            Array.Copy(payload, 16, encrypted, 0, encrypted.Length);

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var decryptor = aes.CreateDecryptor())
                    return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length));
            }
        }
        catch (Exception ex)
        {
            //proj.SendWarningToLog($"V2 Decrypt Error: {ex.Message}"); return string.Empty;
            return null;
        }
    }
    
    public static string HWPass(string pin, string acc)
    {
        try
        {
            string hwId = GetStableHWId();
            
            var secureKey = DeriveSecureKey(pin, hwId, acc);
            if (secureKey == null) return "fallback_password";
            
            // Создаём детерминированный seed для генерации пароля
            using (var hmac = new HMACSHA256(secureKey))
            {
                var seedBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes("PASSWORD_SEED_V2"));
                
                StringBuilder password = new StringBuilder();
                string lowerChars = "abcdefghijklmnopqrstuvwxyz";
                string upperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                string digits = "0123456789";
                string specialChars = "!@#$%^&*()_+-=[]{}|;:,.<>?";
                
                var charSets = new[] { lowerChars, upperChars, digits, specialChars };
                
                // Первые 4 символа - по одному из каждого набора
                for (int i = 0; i < 4; i++)
                {
                    var charSet = charSets[i];
                    int index = seedBytes[i] % charSet.Length;
                    password.Append(charSet[index]);
                }
                
                // Остальные 20 символов - случайно из всех наборов
                string allChars = lowerChars + upperChars + digits + specialChars;
                for (int i = 4; i < 24; i++)
                {
                    int seedIndex = (i * 2) % seedBytes.Length;
                    int index = ((seedBytes[seedIndex] << 8) | seedBytes[(seedIndex + 1) % seedBytes.Length]) % allChars.Length;
                    if (index < 0) index = -index;
                    password.Append(allChars[index]);
                }
                
                // Перемешиваем символы детерминированно
                var chars = password.ToString().ToCharArray();
                for (int i = chars.Length - 1; i > 0; i--)
                {
                    int j = Math.Abs(BitConverter.ToInt32(seedBytes, (i * 4) % (seedBytes.Length - 3))) % (i + 1);
                    var temp = chars[i];
                    chars[i] = chars[j];
                    chars[j] = temp;
                }
                
                return new string(chars);
            }
        }
        catch (Exception ex)
        {
            //proj.SendWarningToLog($"SecureHWPass error: {ex.Message}");
            return "fallback_password";
        }
    }
    static byte[] DeriveKeyFromHWID(string hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId)) return null;
        
        using (var sha256 = SHA256.Create())
        {
            // Фиксированный salt для детерминированности
            byte[] salt = sha256.ComputeHash(Encoding.UTF8.GetBytes("HWID_ONLY_SALT_V1"));
            Array.Resize(ref salt, 16); // Первые 16 байт
            
            using (var pbkdf2 = new Rfc2898DeriveBytes(hardwareId, salt, 100000))
                return pbkdf2.GetBytes(32);
        }
    }

    public static string EncryptHWIDOnly( string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        
        try
        {
            var key = DeriveKeyFromHWID(GetStableHWId());
            if (key == null) return string.Empty;
            
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();
                
                using (var encryptor = aes.CreateEncryptor())
                {
                    var cipherBytes = encryptor.TransformFinalBlock(
                        Encoding.UTF8.GetBytes(plaintext), 0, plaintext.Length);
                    
                    // IV + Шифртекст + HMAC
                    using (var hmac = new HMACSHA256(key))
                    {
                        var combined = new byte[aes.IV.Length + cipherBytes.Length];
                        Array.Copy(aes.IV, 0, combined, 0, aes.IV.Length);
                        Array.Copy(cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length);
                        
                        var hash = hmac.ComputeHash(combined);
                        var final = new byte[combined.Length + hash.Length];
                        Array.Copy(combined, 0, final, 0, combined.Length);
                        Array.Copy(hash, 0, final, combined.Length, hash.Length);
                        
                        return "HWID:" + Convert.ToBase64String(final);
                    }
                }
            }
        }
        catch { return string.Empty; }
    }

    public static string DecryptHWIDOnly( string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext) || !ciphertext.StartsWith("HWID:")) 
            return string.Empty;
        
        try
        {
            var key = DeriveKeyFromHWID(GetStableHWId());
            if (key == null) return string.Empty;
            
            var data = Convert.FromBase64String(ciphertext.Substring(5));
            if (data.Length < 48) return string.Empty;
            
            // Разделяем: payload (IV+cipher) и HMAC
            var hmacSize = 32;
            var payload = new byte[data.Length - hmacSize];
            var receivedHmac = new byte[hmacSize];
            Array.Copy(data, 0, payload, 0, payload.Length);
            Array.Copy(data, payload.Length, receivedHmac, 0, hmacSize);
            
            // Проверка HMAC
            using (var hmac = new HMACSHA256(key))
            {
                var computedHmac = hmac.ComputeHash(payload);
                for (int i = 0; i < hmacSize; i++)
                    if (receivedHmac[i] != computedHmac[i]) return string.Empty;
            }
            
            // Расшифровка
            var iv = new byte[16];
            var encrypted = new byte[payload.Length - 16];
            Array.Copy(payload, 0, iv, 0, 16);
            Array.Copy(payload, 16, encrypted, 0, encrypted.Length);
            
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                
                using (var decryptor = aes.CreateDecryptor())
                    return Encoding.UTF8.GetString(
                        decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length));
            }
        }
        catch { return string.Empty; }
    }
    
    public static string Decode( string toDecode, string pin, string acc, bool log = false)
    {
        if (string.IsNullOrEmpty(toDecode)) return string.Empty;
        try
        {
            var key = DeriveSecureKey(pin, GetStableHWId(log), acc);
            return key != null ? SecureAESDecrypt(toDecode.Substring(3), key) : string.Empty;

        }
        catch (Exception ex)
        {
            //if (log) proj.SendWarningToLog($"Decode failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Шифрует строку, используя стандарт V2.
    /// </summary>
    public static string Encode( string toEncrypt, string pin, string acc, bool log = false)
    {
        if (string.IsNullOrEmpty(toEncrypt)) return string.Empty;
        try
        {
            var key = DeriveSecureKey(pin, GetStableHWId(log), acc);
            if (key == null) return string.Empty;

            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key; 
                aes.Mode = System.Security.Cryptography.CipherMode.CBC; 
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                aes.GenerateIV();

                using (var encryptor = aes.CreateEncryptor())
                {
                    var plainBytes = System.Text.Encoding.UTF8.GetBytes(toEncrypt);
                    var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    
                    using (var hmac = new System.Security.Cryptography.HMACSHA256(key))
                    {
                        var combined = new byte[aes.IV.Length + cipherBytes.Length];
                        Array.Copy(aes.IV, 0, combined, 0, aes.IV.Length);
                        Array.Copy(cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length);
                        
                        var hash = hmac.ComputeHash(combined);
                        var final = new byte[combined.Length + hash.Length];
                        Array.Copy(combined, 0, final, 0, combined.Length);
                        Array.Copy(hash, 0, final, combined.Length, hash.Length);
                        
                        return "V2:" + Convert.ToBase64String(final);
                    }
                }
            }
        } 
        catch { return string.Empty; }
    }
        

    
}