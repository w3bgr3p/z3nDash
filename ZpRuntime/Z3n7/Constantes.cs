// Перенесено из z3n7/Essentials/Vars.cs, класс Constantes.
//
// ШАГ 2 из 3 переноса Safu8/Constantes — цикл описан в шапке Safu8.cs.
// SecureVar здесь опирается на SAFU.DecryptHWID, перенесённый шагом 1.
//
// Два отступления от дословности, оба помечены на месте:
//   SecureVar под #if WINDOWS — он тянет SAFU.DecryptHWID, а тот собирает HWID
//   через WMI. Остальные методы класса от этого не зависят и доступны всегда.
//
//   FromBase64 вызван статически через DevDeck.StringExtensions, а не как
//   extension. Эталонная версия лежит в StringExtentions.cs, который ещё не
//   перенесён; писать using DevDeck нельзя — при переносе Rqst.cs в обоих
//   namespace окажется GET/POST и вызовы станут неоднозначными.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3n7
{
    public static class Constantes
    {
        private static readonly object LockObject = new object();

        public static string ProjectName(this IZennoPosterProjectModel project)
        {
            var path = "";

            var pathToFolder = project.Path;
            var filename = project.Name;

            var actualFiles = Directory.GetFiles(pathToFolder, filename, SearchOption.TopDirectoryOnly);

            if (actualFiles.Length > 0)
            {
                path = Path.GetFileName(actualFiles[0]);
            }
            else
            {
                path = project.Name;
            }

            string name = ProjectName(path);
            project.Var("projectName", name);
            return name;
        }

        private static string ProjectName(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) throw new ArgumentNullException(nameof(projectPath));
            return System.IO.Path.GetFileName(projectPath).Split('.')[0];
        }

        public static string ProjectTable(this IZennoPosterProjectModel project)
        {
            string table = "__" + ProjectName(project);
            project.Var("projectTable", table);
            return table;
        }

        //pathes
        public static string PathProfiles(this IZennoPosterProjectModel project)
        {
            string pathLocal = project.Var("profiles_folder");
            string pathGlobal = project.GVar("profiles_folder");

            if (!string.IsNullOrEmpty(pathLocal))
            {
                if (string.IsNullOrEmpty(pathGlobal))
                    project.GVar("profiles_folder", pathLocal);
                return pathLocal;
            }

            if (!string.IsNullOrEmpty(pathGlobal))
            {
                project.Var("profiles_folder", pathGlobal);
                return pathGlobal;
            }

            throw new Exception("No profiles folder defined");

        }

        public static string PathCookies(this IZennoPosterProjectModel project)
        {
            string acc0 = project.Var("acc0");
            if (string.IsNullOrEmpty(acc0))
            {
                project.SendWarningToLog("acc0 isNullOrEmpty");
                return "";
            }
            return Path.Combine(project.PathProfiles(), "accounts", "cookies", $"{acc0}.json");
        }

        public static string PathProfileFolder(this IZennoPosterProjectModel project)
        {
            string acc0 = project.Var("acc0");
            if (string.IsNullOrEmpty(acc0))
            {
                project.SendWarningToLog("acc0 isNullOrEmpty");
                return "";
            }
            return Path.Combine(project.PathProfiles(), "accounts", "profilesFolder", acc0);
        }

#if WINDOWS
        // Только Windows: SAFU.DecryptHWID собирает HWID через WMI. См. Safu8.cs.
        public static string SecureVar(this IZennoPosterProjectModel project, string key)
        {
            string encrypted = project.Var("jVars");

            if (string.IsNullOrEmpty(encrypted)) return string.Empty;

            string decrypted = SAFU.DecryptHWID(project, encrypted);
            if (string.IsNullOrEmpty(decrypted)) return string.Empty;

            // Эталон: decrypted.FromBase64() — см. шапку файла.
            var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                DevDeck.StringExtensions.FromBase64(decrypted));
            return dict.TryGetValue(key, out var val) ? val : string.Empty;
        }
#endif
    }
}
