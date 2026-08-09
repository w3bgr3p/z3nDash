// Перенесено из z3n7/Essentials/Vars.cs, класс GVars. Копия дословная.

using System;
using System.Collections.Generic;
using System.Linq;
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

        // Берёт LockObject и внутри вызывает GGetBusyList, который берёт его же.
        // Работает потому, что Monitor реентрантен для одного потока.
        public static bool GSetAcc(this IZennoPosterProjectModel project, string input = null, bool force = false, bool log = false)
        {
            string nameSpase = project.ExecuteMacro("{-Environment.CurrentUser-}");

            lock (LockObject)
            {
                try
                {
                    int currentThread = int.Parse(project.Variables["acc0"].Value);
                    string currentThreadKey = $"acc{currentThread}";

                    string valueToSet = input ?? project.Variables["projectName"].Value;

                    if (!force)
                    {
                        var busyAccounts = project.GGetBusyList(false);
                        if (busyAccounts.Any(x => x.StartsWith($"{currentThread}:")))
                        {
                            if (log) project.SendInfoToLog($"{currentThreadKey} is already busy!");
                            return false;
                        }
                    }

                    try
                    {
                        project.GlobalVariables.SetVariable(nameSpase, currentThreadKey, valueToSet);
                    }
                    catch (Exception ex)
                    {
                        if (log) project.SendWarningToLog(ex.Message, true);
                        project.GlobalVariables[nameSpase, currentThreadKey].Value = valueToSet;
                    }

                    if (log)
                    {
                        string forceText = force ? " (forced)" : "";
                        project.SendInfoToLog($"{currentThreadKey} bound to {valueToSet}{forceText}");
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    if (log) project.SendInfoToLog($"⚙ GSet: {ex.Message}");
                    throw;
                }
            }
        }

        public static List<int> GClean(this IZennoPosterProjectModel project, bool log = false)
        {
            string nameSpase = project.ExecuteMacro("{-Environment.CurrentUser-}");
            var cleaned = new List<int>();

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
                            if (globalVar != null)
                            {
                                globalVar.Value = string.Empty;
                                cleaned.Add(i);
                            }
                        }
                        catch { }
                    }

                    if (log)
                    {
                        project.SendInfoToLog($"Cleaned accounts: {string.Join(",", cleaned)}");
                    }

                    return cleaned;
                }
                catch (Exception ex)
                {
                    if (log) project.SendInfoToLog($"⚙ GClean: {ex.Message}");
                    throw;
                }
            }
        }
    }
}
