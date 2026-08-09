// ══════════════════════════════════════════════════════════════════════════════
// Emulation.cs — ZennoLab.Emulation.
//
// В ZP это эмуляция ввода через оконные сообщения: методы принимают HWND. У нас
// браузер управляется по CDP, окон нет, поэтому handle игнорируется, а ввод идёт
// через клавиатуру активной страницы. Сигнатуры сохранены, чтобы код под ZP
// компилировался без правок.
//
// Только под Windows: SendKey принимает System.Windows.Forms.Keys, а ZpRuntime
// собирается ещё и под net10.0 без Windows, где этого типа нет.
// ══════════════════════════════════════════════════════════════════════════════

#if WINDOWS

using System;
using System.Windows.Forms;
using DevDeck.Browser;

namespace ZennoLab.Emulation
{
    public enum KeyboardEvent    { Down, Up, Press }
    public enum MouseButton      { Left, Right }
    public enum MouseButtonEvent { Down, Up, Click }

    /// <summary>
    /// ZP-шный Emulator. Работает только если ему заранее отдали активный
    /// браузер через <see cref="Attach"/> — сам он его не поднимает.
    /// </summary>
    public static class Emulator
    {
        private static IBrowserInstance? _browser;

        public static bool ErrorDetected { get; set; }

        /// <summary>Привязать браузер, на который пойдёт эмуляция ввода.</summary>
        public static void Attach(IBrowserInstance browser) => _browser = browser;

        private static IBrowserInstance Br => _browser ?? throw new NotSupportedException(
            "Emulator: браузер не привязан. Вызовите Emulator.Attach(instance.Browser) " +
            "перед эмуляцией ввода — оконных хендлов в ZpRuntime нет.");

        /// <summary>
        /// handle игнорируется: в ZP это HWND вкладки, у CDP-страницы окна нет.
        /// Нажатие уходит в клавиатуру активной вкладки.
        /// </summary>
        public static string SendKey(int handle, Keys key, KeyboardEvent keyEvent)
        {
            string name = Translate(key);
            string type = keyEvent switch
            {
                KeyboardEvent.Down => "keydown",
                KeyboardEvent.Up   => "keyup",
                _                  => "press",
            };
            Br.ActiveTab.KeyEvent(name, type);
            return "";
        }

        public static string SendText(int handle, string text)
        {
            foreach (char c in text ?? "")
                Br.ActiveTab.KeyEvent(c.ToString(), "press");
            return "";
        }

        /// <summary>WinForms Keys → имена клавиш Playwright.</summary>
        private static string Translate(Keys key) => key switch
        {
            Keys.Enter    => "Enter",
            Keys.Tab      => "Tab",
            Keys.Escape   => "Escape",
            Keys.Back     => "Backspace",
            Keys.Delete   => "Delete",
            Keys.Space    => "Space",
            Keys.Up       => "ArrowUp",
            Keys.Down     => "ArrowDown",
            Keys.Left     => "ArrowLeft",
            Keys.Right    => "ArrowRight",
            Keys.Home     => "Home",
            Keys.End      => "End",
            Keys.PageUp   => "PageUp",
            Keys.PageDown => "PageDown",
            Keys.Insert   => "Insert",
            _             => key.ToString(),
        };
    }
}

#endif
