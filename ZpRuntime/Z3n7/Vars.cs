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
    }
}
