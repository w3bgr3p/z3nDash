// InternalTasks.Migrate.cs
// После успешной миграции удалить этот файл и SafuMigrate.cs.

using Newtonsoft.Json;

namespace z3nDash;

public static partial class InternalTasks
{
    
}


// InternalTasks.UpdateTemplates.cs




public static partial class InternalTasks
{
    /// <summary>
    /// Утилита разработки: снимает с текущей БД структуру таблиц и содержимое _api
    /// в каталог templates. Каталог передаётся явно — результат едет клиенту
    /// обновлением, а не генерируется у него. Вызов: POST /config/update-templates.
    /// </summary>
    public static string UpdateTemplates(Db db, string outDir, Action<string>? sink = null)
    {
        var log = sink ?? (_ => { });
        {
            Directory.CreateDirectory(outDir);

            // ── db_template.json ─────────────────────────────────────────────

            var tables = db.GetTables();
            var fullStructure = new Dictionary<string, List<string>>();

            foreach (var table in tables)
            {
                if (table.StartsWith("__")) continue;
                var columns = db.GetTableColumns(table);
                fullStructure[table] = columns;
            }

            string dbTemplatePath = Path.Combine(outDir, "db_template.json");
            File.WriteAllText(dbTemplatePath, JsonConvert.SerializeObject(fullStructure));
            log($"[UpdateTemplates] db_template.json → {tables.Count} tables → {dbTemplatePath}");

            // ── api_template.json ─────────────────────────────────────────────

            const string apiTable = "_api";
            if (!db.TableExists(apiTable))
            {
                log($"[UpdateTemplates] {apiTable} not found, skipping api_template");
                return $"db_template: {tables.Count} tables, api_template: skipped";
            }

            var serviceColumns  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "_json_structure" };
            var allColumns      = db.GetTableColumns(apiTable);
            var dataColumns     = allColumns.Where(c => !serviceColumns.Contains(c)).ToList();

            var idsRaw = db.Query($"SELECT string_agg(id::text, '·') FROM \"{apiTable}\"");
            if (string.IsNullOrWhiteSpace(idsRaw))
            {
                log($"[UpdateTemplates] {apiTable} is empty, skipping api_template");
                return $"db_template: {tables.Count} tables, api_template: empty";
            }

            var ids             = idsRaw.Split('·').Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var resultTemplate  = new Dictionary<string, List<string>>();
            string colsCsv      = string.Join(",", dataColumns);

            foreach (var id in ids)
            {
                var row = db.GetColumns(colsCsv, apiTable, where: $"\"id\" = '{id.Trim().Replace("'", "''")}'");
                if (row == null || row.Count == 0) continue;

                var values  = new List<string>();
                bool hasAny = false;

                foreach (var col in dataColumns)
                {
                    if (row.TryGetValue(col, out var val) && !string.IsNullOrWhiteSpace(val))
                    {
                        values.Add("REQUIRED");
                        hasAny = true;
                    }
                    else
                    {
                        values.Add("");
                    }
                }

                if (hasAny)
                    resultTemplate[id.Trim()] = values;
            }

            string apiTemplatePath = Path.Combine(outDir, "api_template.json");
            File.WriteAllText(apiTemplatePath, JsonConvert.SerializeObject(resultTemplate, Formatting.Indented));
            log($"[UpdateTemplates] api_template.json → {resultTemplate.Count} entries → {apiTemplatePath}");

            return $"db_template: {tables.Count} tables, api_template: {resultTemplate.Count} entries";
        }
    }
}