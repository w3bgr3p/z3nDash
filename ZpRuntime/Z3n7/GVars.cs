// Перенесено из z3n7/Essentials/Vars.cs, класс GVars. Копия дословная.
// GSetAcc и GClean из этого же класса пока не перенесены — они идут следующими.

using System;
using System.Collections.Generic;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3n7
{
    public static class GVars
    {
        private static readonly object LockObject = new object();

        public static string GVar(this IZennoPosterProjectModel project, string var)
        {
            string nameSpase = project.ExecuteMacro("{-Environment.CurrentUser-}");
            string value = string.Empty;
            lock (LockObject)
            {
                try
                {
                    value = project.GlobalVariables[nameSpase, var].Value;
                }
                catch { }
            }
            return value;
        }

        public static string GVar(this IZennoPosterProjectModel project, string var, object value)
        {
            string nameSpase = project.ExecuteMacro("{-Environment.CurrentUser-}");
            lock (LockObject)
            {
                try
                {
                    project.GlobalVariables[nameSpase, var].Value = value.ToString();
                }
                catch
                {
                    try
                    {
                        project.GlobalVariables.SetVariable(nameSpase, var, value.ToString());
                    }
                    catch { }

                }
            }
            return string.Empty;
        }

        public static List<string> GGetBusyList(this IZennoPosterProjectModel project, bool log = false)
        {
            string nameSpase = project.ExecuteMacro("{-Environment.CurrentUser-}");
            var busyAccounts = new List<string>();

            lock (LockObject)
            {
                try
                {
                    for (int i = 1; i <= int.Parse(project.Variables["rangeEnd"].Value); i++)
                    {
                        string threadKey = $"acc{i}";
                        try
                        {
                            var globalVar = project.GlobalVariables[nameSpase, threadKey];
                            if (globalVar != null && !string.IsNullOrEmpty(globalVar.Value))
                            {
                                busyAccounts.Add($"{i}:{globalVar.Value}");
                            }
                        }
                        catch { }
                    }

                    if (log)
                    {
                        project.SendInfoToLog($"busy Accounts: [{string.Join(" | ", busyAccounts)}]");
                    }

                    return busyAccounts;
                }
                catch (Exception ex)
                {
                    if (log) project.SendInfoToLog($"⚙ GGet: {ex.Message}");
                    throw;
                }
            }
        }
    }
}
