using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace z3nIO;

public class SAFU
{
    private static byte[]? _fileKey;
    private static readonly object _fileKeyLock = new();
    private static readonly string KeyFilePath = Path.Combine(AppContext.BaseDirectory, "safu.key");

    public static byte[] LoadOrCreateFileKey()
    {
        

        if (_fileKey != null) return _fileKey;
        lock (_fileKeyLock)
        {
            if (_fileKey != null) return _fileKey;
            if (File.Exists(KeyFilePath))
            {
                _fileKey = File.ReadAllBytes(KeyFilePath);
                if (_fileKey.Length != 32)
                    throw new InvalidOperationException($"safu.key must be 32 bytes, got {_fileKey.Length}");
            }
            else
            {
                _fileKey = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(KeyFilePath, _fileKey);
            }
            return _fileKey;
        }
    }

    public static string? GetStableHWId()
    {
        var components = new List<string>();
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
                foreach (ManagementObject mo in s.Get())
                {
                    var id = mo["ProcessorId"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) { components.Add(id); break; }
                }

            using (var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
                foreach (ManagementObject mo in s.Get())
                {
                    var serial = mo["SerialNumber"]?.ToString();
                    if (!string.IsNullOrEmpty(serial)) { components.Add(serial); break; }
                }

            try
            {
                string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))!;
                string driveLetter = sysRoot.Replace("\\", "");
                using var logDiskSearcher = new ManagementObjectSearcher(
                    $"SELECT DeviceID FROM Win32_LogicalDisk WHERE DeviceID = '{driveLetter}'");
                foreach (ManagementObject logDisk in logDiskSearcher.Get())
                    using (var partSearcher = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{logDisk["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition"))
                        foreach (ManagementObject partition in partSearcher.Get())
                            using (var driveSearcher = new ManagementObjectSearcher(
                                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition"))
                                foreach (ManagementObject drive in driveSearcher.Get())
                                {
                                    var serial = drive["SerialNumber"]?.ToString();
                                    if (!string.IsNullOrEmpty(serial)) { components.Add(serial); break; }
                                }
            }
            catch
            {
                using var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive");
                foreach (ManagementObject mo in s.Get()) { components.Add(mo["SerialNumber"]?.ToString()!); break; }
            }

            if (components.Count == 0) throw new Exception("No hardware components found");

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(string.Join(":", components)));
            return Convert.ToBase64String(hashBytes);
        }
        catch
        {
            return null;
        }
    }

    static byte[] DeriveSalt(string domain)
    {
        var fileKey = LoadOrCreateFileKey();
        using var hmac = new HMACSHA256(fileKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(domain));
        var salt = new byte[16];
        Array.Copy(hash, salt, 16);
        return salt;
    }

    static byte[] DeriveSecureKey(string pin, string hardwareId, string accountId)
    {
        if (string.IsNullOrEmpty(hardwareId)) return null!;
        if (string.IsNullOrEmpty(pin)) pin = "UNPROTECTED";
        var salt = DeriveSalt($"{hardwareId}:{accountId}");
        using var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, 100000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    static byte[] DeriveKeyFromHWID(string hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId)) return null!;
        var salt = DeriveSalt("hwid-only");
        using var pbkdf2 = new Rfc2898DeriveBytes(hardwareId, salt, 100000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    static string AesEncrypt(string plaintext, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        var combined = new byte[aes.IV.Length + cipherBytes.Length];
        Array.Copy(aes.IV, 0, combined, 0, aes.IV.Length);
        Array.Copy(cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length);

        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(combined);

        var final = new byte[combined.Length + hash.Length];
        Array.Copy(combined, 0, final, 0, combined.Length);
        Array.Copy(hash, 0, final, combined.Length, hash.Length);

        return Convert.ToBase64String(final);
    }

    static string? AesDecrypt(string ciphertext, byte[] key)
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
                    {
                        return string.Empty;
                    }
            }

            var iv = new byte[16];
            var encrypted = new byte[payload.Length - 16];
            Array.Copy(payload, 0, iv, 0, 16);
            Array.Copy(payload, 16, encrypted, 0, encrypted.Length);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length));
        }
        catch
        {
            return null;
        }
    }

    public static string HWPass(string pin, string acc)
    {
        string hwId = GetStableHWId() ?? throw new InvalidOperationException("HWID resolution failed");
        var secureKey = DeriveSecureKey(pin, hwId, acc);

        using var hmac = new HMACSHA256(secureKey);
        var seedBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes("PASSWORD_SEED"));

        var password = new StringBuilder();
        string lower   = "abcdefghijklmnopqrstuvwxyz";
        string upper   = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        string digits  = "0123456789";
        string special = "!@#$%^&*()_+-=[]{}|;:,.<>?";
        var charSets = new[] { lower, upper, digits, special };

        for (int i = 0; i < 4; i++)
            password.Append(charSets[i][seedBytes[i] % charSets[i].Length]);

        string all = lower + upper + digits + special;
        for (int i = 4; i < 24; i++)
        {
            int si = (i * 2) % seedBytes.Length;
            int idx = Math.Abs((seedBytes[si] << 8) | seedBytes[(si + 1) % seedBytes.Length]) % all.Length;
            password.Append(all[idx]);
        }

        var chars = password.ToString().ToCharArray();
        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = Math.Abs(BitConverter.ToInt32(seedBytes, (i * 4) % (seedBytes.Length - 3))) % (i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    // Шифрует HWID локальной машины
    public static string EncryptHWIDOnly(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var hwId = GetStableHWId() ?? throw new InvalidOperationException("HWID resolution failed");
        return AesEncrypt(plaintext, DeriveKeyFromHWID(hwId));
    }

    // Шифрует с явным HWID (для генерации бандла клиента)
    public static string EncryptHWIDOnly(string plaintext, string hwid)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (string.IsNullOrEmpty(hwid)) throw new ArgumentException("hwid cannot be empty");
        return AesEncrypt(plaintext, DeriveKeyFromHWID(hwid));
    }

    public static string DecryptHWIDOnly_(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
        var hwId = GetStableHWId() ?? throw new InvalidOperationException("HWID resolution failed");
        return AesDecrypt(ciphertext, DeriveKeyFromHWID(hwId)) ?? string.Empty;
    }
    public static string DecryptHWIDOnly(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
        var hwId = GetStableHWId() ?? throw new InvalidOperationException("HWID resolution failed");
        var key  = DeriveKeyFromHWID(hwId);
    
    
        var result = AesDecrypt(ciphertext, key);
        return result ?? string.Empty;
    }

    public static string Encode(string toEncrypt, string pin, string acc)
    {
        if (string.IsNullOrEmpty(toEncrypt)) return string.Empty;
        var key = DeriveSecureKey(pin, GetStableHWId() ?? throw new InvalidOperationException("HWID resolution failed"), acc);
        return AesEncrypt(toEncrypt, key);
    }

    public static string Decode(string toDecode, string pin, string acc)
    {
        if (string.IsNullOrEmpty(toDecode)) return string.Empty;
        var key = DeriveSecureKey(pin, GetStableHWId() ?? throw new InvalidOperationException("HWID resolution failed"), acc);
        return AesDecrypt(toDecode, key) ?? string.Empty;
    }
}