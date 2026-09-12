// Сверка HWID: наш z3nDash.SAFU против эталонного z3n7 Safu8.
//
// Логика z3n7 воспроизведена здесь дословно (Safu8.cs:102-153), потому что сам
// z3n7 собран под net48 и ссылается на ZennoLab — из net10 его не подключить.
// Компоненты печатаются по отдельности: если строки разойдутся, сразу видно, на
// каком именно компоненте.

using System.Management;
using System.Security.Cryptography;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var z3n7Components = CollectZ3n7Components(out string diskSource);

Console.WriteLine("=== компоненты, как их собирает z3n7 ===");
for (int i = 0; i < z3n7Components.Count; i++)
    Console.WriteLine($"  [{i}] {z3n7Components[i]}");
Console.WriteLine($"  источник серийника диска: {diskSource}");
Console.WriteLine();

string z3n7Hwid = z3n7Components.Count == 0
    ? "<нет компонентов>"
    : Sha256B64(string.Join(":", z3n7Components));

// Наши берут только ProcessorId и BaseBoard.SerialNumber — то есть первые два.
// Считаем хеш от них же: если он совпадёт с нашим, расхождение ровно в диске.
string firstTwoHwid = z3n7Components.Count >= 2
    ? Sha256B64(string.Join(":", z3n7Components.Take(2)))
    : "<мало компонентов>";

string ourHwid;
try   { ourHwid = z3nDash.SAFU.GetStableHWId() ?? "<null>"; }
catch (Exception ex) { ourHwid = $"<ошибка: {ex.Message}>"; }

Console.WriteLine("=== HWID ===");
Console.WriteLine($"  z3n7 (все компоненты)   {z3n7Hwid}");
Console.WriteLine($"  только первые два       {firstTwoHwid}");
Console.WriteLine($"  наш z3nDash.SAFU        {ourHwid}");
Console.WriteLine();

Console.WriteLine("=== вывод ===");
if (ourHwid == z3n7Hwid)
{
    Console.WriteLine("  СОВПАДАЕТ с z3n7. Данные читаются обеими реализациями,");
    Console.WriteLine("  править GetStableHWId не нужно.");
}
else if (ourHwid == firstTwoHwid)
{
    Console.WriteLine("  РАСХОДИТСЯ, и ровно на серийнике системного диска:");
    Console.WriteLine("  наш хеш равен хешу первых двух компонентов z3n7.");
    Console.WriteLine("  Значит всё, что зашифровал z3nDash, z3n7 прочитать не может.");
    Console.WriteLine("  Наш GetStableHWId надо привести к составу z3n7.");
}
else
{
    Console.WriteLine("  РАСХОДИТСЯ, и не только на диске — не совпало даже с хешем");
    Console.WriteLine("  первых двух компонентов. Расходится сам сбор или порядок:");
    Console.WriteLine("  сверять надо построчно с Safu8.cs:102-153.");
}

static string Sha256B64(string input)
{
    using var sha = SHA256.Create();
    return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(input)));
}

// Дословный порт z3n7/Essentials/Safu8.cs:102-153.
static List<string> CollectZ3n7Components(out string diskSource)
{
    var components = new List<string>();
    diskSource = "не найден";

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
        string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
        string driveLetter = sysRoot.Replace("\\", "");
        using var lds = new ManagementObjectSearcher(
            "SELECT DeviceID FROM Win32_LogicalDisk WHERE DeviceID = '" + driveLetter + "'");
        foreach (ManagementObject ld in lds.Get())
        {
            using var ps = new ManagementObjectSearcher(
                "ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + ld["DeviceID"] +
                "'} WHERE AssocClass = Win32_LogicalDiskToPartition");
            foreach (ManagementObject part in ps.Get())
            {
                using var ds = new ManagementObjectSearcher(
                    "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + part["DeviceID"] +
                    "'} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                foreach (ManagementObject drive in ds.Get())
                {
                    var serial = drive["SerialNumber"]?.ToString();
                    if (!string.IsNullOrEmpty(serial))
                    {
                        components.Add(serial);
                        diskSource = "ASSOCIATORS (системный диск)";
                        break;
                    }
                }
            }
        }
    }
    catch
    {
        using var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive");
        foreach (ManagementObject mo in s.Get())
        {
            components.Add(mo["SerialNumber"]?.ToString());
            diskSource = "Win32_DiskDrive (fallback после исключения)";
            break;
        }
    }

    return components;
}
