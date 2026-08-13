// Первый срез z3n7/Browser/Canvas.cs — только то, что нужно js.cs.
//
// В эталоне GetCenter и GetViewportSize лежат в Canvas.cs, в том же partial-классе
// InstanceExtensions, что и поиск картинок через OpenCV. Тащить сюда все 677 строк
// ради двух методов незачем, а JsExtensions.CenterMouse без GetCenter не собирается.
//
// Когда Canvas.cs поедет целиком, этот файл заменяется им — не дополняется.

using System;
using System.Text.RegularExpressions;
using ZennoLab.CommandCenter;

namespace z3n7
{
    public static partial class InstanceExtensions
    {
        private static int[] GetViewportSize(Instance instance)
        {
            string js = @"
                return JSON.stringify({
                    width: window.innerWidth,
                    height: window.innerHeight
                });
            ";
            string result = instance.ActiveTab.MainDocument.EvaluateScript(js);

            var match = Regex.Match(result, @"""width"":(\d+),""height"":(\d+)");
            int width = int.Parse(match.Groups[1].Value);
            int height = int.Parse(match.Groups[2].Value);

            return new int[] { width, height };
        }
        public static int[] GetCenter(this Instance instance)
        {
            int[] viewport = GetViewportSize(instance);
            return new int[] { viewport[0] / 2, viewport[1] / 2 };
        }
    }
}
