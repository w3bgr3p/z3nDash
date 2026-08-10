// Перенесено из z3n7/Essentials/Vars.cs. Копия дословная — z3n7 эталон
// поведения, и расхождения должны быть видны как diff, а не как разбор двух
// реализаций. Namespace сохранён: ProjectExtensions и соседи объявлены в z3n7.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3n7
{
    public static class Vars
    {
        private static readonly object LockObject = new object();

        public static string Var(this IZennoPosterProjectModel project, string var)
        {
            string value = string.Empty;
            try
            {
                value = project.Variables[var].Value;
            }
            catch (Exception e)
            {
                project.SendInfoToLog(e.Message);
            }
            if (value == string.Empty)
            { }

            return value;
        }

        public static string Var(this IZennoPosterProjectModel project, string var, object value)
        {
            if (value == null) return string.Empty;
            try
            {
                project.Variables[var].Value = value.ToString();
            }
            catch (Exception e)
            {
                project.SendInfoToLog(e.Message);
            }
            return string.Empty;
        }

        public static int Int(this IZennoPosterProjectModel project, string var)
        {
            int value = 0;
            try
            {
                value = int.Parse(project.Var(var));
            }
            catch
            {
            }
            return value;
        }

        public static int Int(this IZennoPosterProjectModel project, string varName, int input)
        {
            var counter = project.Int(varName) + input;
            project.Var(varName, counter);
            return counter;
        }

        public static decimal Decimal(this IZennoPosterProjectModel project, string var)
        {
            decimal value = 0;
            try
            {
                value = decimal.Parse(project.Var(var));
            }
            catch
            {
            }
            return value;
        }

        public static bool Bool(this IZennoPosterProjectModel project, string var)
        {
            bool value = project.Var(var) == "True";
            return value;
        }

        public static void MaxErr(this IZennoPosterProjectModel project, int maxAttempts, Exception ex = null)
        {
            var errCounter = project.Int("ErrCounter");
            var message = ex != null ? ex.Message : project.LastErrorComment;
            project.Var("err", message);

            if (errCounter > maxAttempts)
            {
                project.SendWarningToLog($"max errors reached: {message}");
                throw new Exception(message);
            }
            else
            {
                project.Int("ErrCounter", 1);
            }
        }

        public static string VarRnd(this IZennoPosterProjectModel project, string var)
        {
            string value = string.Empty;
            try
            {
                value = project.Variables[var].Value;
            }
            catch (Exception e)
            {
                project.SendInfoToLog(e.Message);
            }
            if (value == string.Empty) project.SendInfoToLog($"no Value from [{var}] `w");

            if (value.Contains("-"))
            {
                var min = int.Parse(value.Split('-')[0].Trim());
                var max = int.Parse(value.Split('-')[1].Trim());
                return new Random().Next(min, max).ToString();
            }
            return value.Trim();
        }

        public static int VarCounter(this IZennoPosterProjectModel project, string varName, int input)
        {
            var counter = project.Int(varName) + input;
            project.Var(varName, counter);
            return counter;
        }

        public static decimal VarsMath(this IZennoPosterProjectModel project, string varA, string operation, string varB, string resultVar = null)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            decimal a = decimal.Parse(project.Var(varA));
            decimal b = decimal.Parse(project.Var(varB));
            decimal result;
            switch (operation)
            {
                case "+":
                    result = a + b;
                    break;
                case "-":
                    result = a - b;
                    break;
                case "*":
                    result = a * b;
                    break;
                case "/":
                    result = a / b;
                    break;
                default:
                    throw new Exception($"unsupported operation {operation}");
            }
            // Условие в эталоне инвертировано: запись идёт при ПУСТОМ resultVar,
            // из-за чего Var(null, ..) уходит в catch и результат не сохраняется.
            // Оставлено как есть — z3n7 эталон, расхождение чинится на его стороне.
            if (string.IsNullOrEmpty(resultVar))
                try { project.Var(resultVar, $"{result}"); } catch { }
            return result;
        }

        public static void VarsFromDict(this IZennoPosterProjectModel project, Dictionary<string, string> dict)
        {
            foreach (var pair in dict)
            {
                project.Var(pair.Key, pair.Value);
            }
        }

        public static void VarsFromJson(this IZennoPosterProjectModel project, string json = "jVars")
        {
            if (json == "jVars") json = project.Var("jVars");
            var jVar = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            project.VarsFromDict(jVar);
        }

        public static List<string> Range(this IZennoPosterProjectModel project, string accRange = null,
            string output = null, bool log = false)
        {
            if (string.IsNullOrEmpty(accRange)) accRange = project.Var("cfgAccRange");
            if (string.IsNullOrEmpty(accRange))
            {
                project.warn("range is not provided by input or project setting [cfgAccRange]");
                return null;
            }
            
            if (accRange.Contains(":"))
            {
                accRange = accRange.Split(':')[0];
            }
            
            int rangeS, rangeE;
            string range;

            if (accRange.Contains(","))
            {
                range = accRange;
                var rangeParts = accRange.Split(',').Select(int.Parse).ToArray();
                rangeS = rangeParts.Min();
                rangeE = rangeParts.Max();
            }
            else if (accRange.Contains("-"))
            {
                var rangeParts = accRange.Split('-').Select(int.Parse).ToArray();
                rangeS = rangeParts[0];
                rangeE = rangeParts[1];
                range = string.Join(",", Enumerable.Range(rangeS, rangeE - rangeS + 1));
            }
            else
            {
                rangeE = int.Parse(accRange);
                rangeS = int.Parse(accRange);
                range = accRange;
            }

            project.Variables["rangeStart"].Value = $"{rangeS}";
            project.Variables["rangeEnd"].Value = $"{rangeE}";
            project.Variables["range"].Value = range;

            return range.Split(',').ToList();
            //project.L0g($"{rangeS}-{rangeE}\n{range}");
        }
    }
}
