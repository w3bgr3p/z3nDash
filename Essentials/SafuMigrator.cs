// SafuMigrate.cs — одноразовый скрипт миграции данных
// Запускать ОДИН раз. После успешного завершения удалить этот файл и InternalTasks.Migrate.cs.

using System.Security.Cryptography;
using System.Text;

namespace z3n8;

public static class SafuMigrate
{
    private const string SrcTable = "_wallets";
    private const string DstTable = "_wlt";

    // ── Старая схема (только для чтения) ─────────────────────────────────────

    public static string? LegacyGetHWIdPublic() => LegacyGetHWId();

    public static string? LegacyDecodeHWIDPublic(string raw, string hwId) => LegacyDecodeHWID(raw, hwId);

    static string? LegacyGetHWId()
    {
        var components = new List<string>();
        try
        {
            using (var s = new System.Management.ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
                foreach (System.Management.ManagementObject mo in s.Get())
                {
                    var id = mo["ProcessorId"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) { components.Add(id); break; }
                }

            using (var s = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
                foreach (System.Management.ManagementObject mo in s.Get())
                {
                    var serial = mo["SerialNumber"]?.ToString();
                    if (!string.IsNullOrEmpty(serial)) { components.Add(serial); break; }
                }

            try
            {
                string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))!;
                string driveLetter = sysRoot.Replace("\\", "");
                using (var lds = new System.Management.ManagementObjectSearcher($"SELECT DeviceID FROM Win32_LogicalDisk WHERE DeviceID = '{driveLetter}'"))
                    foreach (System.Management.ManagementObject ld in lds.Get())
                        using (var ps = new System.Management.ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{ld["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition"))
                            foreach (System.Management.ManagementObject p in ps.Get())
                                using (var ds = new System.Management.ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{p["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition"))
                                    foreach (System.Management.ManagementObject d in ds.Get())
                                    {
                                        var serial = d["SerialNumber"]?.ToString();
                                        if (!string.IsNullOrEmpty(serial)) { components.Add(serial); break; }
                                    }
            }
            catch
            {
                using var s = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive");
                foreach (System.Management.ManagementObject mo in s.Get()) { components.Add(mo["SerialNumber"]?.ToString()!); break; }
            }

            if (components.Count == 0) return null;

            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes($"HW_ID_V2:{string.Join(":", components)}"));
            return Convert.ToBase64String(hash);
        }
        catch { return null; }
    }

    static byte[]? LegacyDeriveSecureKey(string pin, string hwId, string accId)
    {
        if (string.IsNullOrEmpty(hwId)) return null;
        if (string.IsNullOrEmpty(pin)) pin = "UNPROTECTED";

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes($"SALT_V2:{hwId}:{accId}"));
        var salt = new byte[16];
        Array.Copy(hash, salt, 16);
        using var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, 100000);
        return pbkdf2.GetBytes(32);
    }

    static byte[]? LegacyDeriveKeyFromHWID(string hwId)
    {
        if (string.IsNullOrEmpty(hwId)) return null;

        using var sha256 = SHA256.Create();
        var salt = sha256.ComputeHash(Encoding.UTF8.GetBytes("HWID_ONLY_SALT_V1"));
        Array.Resize(ref salt, 16);
        using var pbkdf2 = new Rfc2898DeriveBytes(hwId, salt, 100000);
        return pbkdf2.GetBytes(32);
    }

    static string? LegacyAesDecrypt(string ciphertext, byte[] key)
    {
        try
        {
            var data = Convert.FromBase64String(ciphertext);
            if (data.Length < 48) return null;

            var payload      = new byte[data.Length - 32];
            var receivedHmac = new byte[32];
            Array.Copy(data, 0, payload, 0, payload.Length);
            Array.Copy(data, payload.Length, receivedHmac, 0, 32);

            using (var hmac = new HMACSHA256(key))
            {
                var computed = hmac.ComputeHash(payload);
                for (int i = 0; i < 32; i++)
                    if (receivedHmac[i] != computed[i]) return null;
            }

            var iv        = new byte[16];
            var encrypted = new byte[payload.Length - 16];
            Array.Copy(payload, 0, iv, 0, 16);
            Array.Copy(payload, 16, encrypted, 0, encrypted.Length);

            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            return Encoding.UTF8.GetString(dec.TransformFinalBlock(encrypted, 0, encrypted.Length));
        }
        catch { return null; }
    }

    static string? LegacyDecode(string raw, string pin, string hwId, string accId)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("V2:")) return null;
        var key = LegacyDeriveSecureKey(pin, hwId, accId);
        return key == null ? null : LegacyAesDecrypt(raw[3..], key);
    }

    static string? LegacyDecodeHWID(string raw, string hwId)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("HWID:")) return null;
        var key = LegacyDeriveKeyFromHWID(hwId);
        return key == null ? null : LegacyAesDecrypt(raw[5..], key);
    }

    // ── Точка входа ───────────────────────────────────────────────────────────

    public static string Run(Db db, string pin, string jVarsEncrypted)
    {
        string hwId = LegacyGetHWId()
            ?? throw new InvalidOperationException("Migration: HWID resolution failed");

        SAFU.LoadOrCreateFileKey();

        Console.WriteLine("[SafuMigrate] Preparing _wlt...");
        PrepareDestination(db);

        Console.WriteLine("[SafuMigrate] Migrating wallets...");
        int migrated = MigrateWallets(db, pin, hwId);
        Console.WriteLine($"[SafuMigrate] Wallets done: {migrated} row(s).");

        Console.WriteLine("[SafuMigrate] Migrating jVars...");
        string newJVars = MigrateJVars(jVarsEncrypted, hwId);
        Console.WriteLine("[SafuMigrate] Done.");

        return newJVars;
    }

    static void PrepareDestination(Db db)
    {
        db.PrepareTable(
            new Dictionary<string, string>
            {
                { "id",        "BIGINT PRIMARY KEY" },
                { "secp256k1", "TEXT DEFAULT ''" },
                { "base58",    "TEXT DEFAULT ''" },
                { "bip39",     "TEXT DEFAULT ''" },
            },
            DstTable);

        var rawCount = db.Query($"SELECT COUNT(*) FROM \"{SrcTable}\"");
        if (!int.TryParse(rawCount, out int count) || count == 0)
        {
            Console.WriteLine("[SafuMigrate] _wallets is empty.");
            return;
        }

        db.AddRange(DstTable, count);
        Console.WriteLine($"[SafuMigrate] _wlt prepared: {count} row(s).");
    }

    static int MigrateWallets(Db db, string pin, string hwId)
    {
        var rawIds = db.Get("id", SrcTable, where: "1=1");
        if (string.IsNullOrEmpty(rawIds)) return 0;

        var ids = rawIds.Split('·')
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        int migrated = 0;

        foreach (var id in ids)
        {
            var cols    = db.GetColumns("secp256k1,base58,bip39", SrcTable, id: id);
            var updates = new List<string>();

            foreach (var col in new[] { "secp256k1", "base58", "bip39" })
            {
                if (!cols.TryGetValue(col, out var encrypted) || string.IsNullOrEmpty(encrypted))
                    continue;

                if (!encrypted.StartsWith("V2:"))
                {
                    Console.WriteLine($"[SafuMigrate] id={id} col={col}: skipped (not V2)");
                    continue;
                }

                var plaintext = LegacyDecode(encrypted, pin, hwId, id);
                if (string.IsNullOrEmpty(plaintext))
                {
                    Console.WriteLine($"[SafuMigrate] id={id} col={col}: DECRYPT FAILED");
                    continue;
                }

                updates.Add($"{col} = '{SAFU.Encode(plaintext, pin, id)}'");
            }

            if (updates.Count == 0) continue;

            db.Upd(string.Join(", ", updates), DstTable, id: id);
            migrated++;
            if (migrated % 50 == 0)
                Console.WriteLine($"[SafuMigrate] progress: {migrated}/{ids.Count}");
        }

        return migrated;
    }

    static string MigrateJVars(string jVarsEncrypted, string hwId)
    {
        var plaintext = LegacyDecodeHWID(jVarsEncrypted, hwId)
            ?? throw new InvalidOperationException("Migration: jVars decrypt failed");

        var newEncrypted = SAFU.EncryptHWIDOnly(plaintext);
        if (string.IsNullOrEmpty(newEncrypted))
            throw new InvalidOperationException("Migration: jVars re-encrypt failed");

        return newEncrypted;
    }
}