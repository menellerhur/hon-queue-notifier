using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace HonQueueNotifier
{
    // =====================================================================================
    // The minimize hotkey: a key combination (default Alt+Q, off by default) that minimizes the game
    // window. A fullscreen game cannot be minimized otherwise. It is registered with Windows only while
    // the game has focus, so everywhere else the combination works as usual. Nothing is sent to the
    // game: Windows tells this program about the key press, and the program asks Windows to minimize
    // the game's window, as the minimize button would.
    // =====================================================================================

    // "Alt+Q", "Ctrl+Alt+M" and the like, to and from what RegisterHotKey wants.
    static class HotkeyText
    {
        public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

        public static bool TryParse(string text, out uint modifiers, out Keys key)
        {
            modifiers = 0;
            key = Keys.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                bool last = i == parts.Length - 1;
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= Ctrl; if (last) return false; continue;
                    case "alt": modifiers |= Alt; if (last) return false; continue;
                    case "shift": modifiers |= Shift; if (last) return false; continue;
                    case "win": case "windows": modifiers |= Win; if (last) return false; continue;
                }
                if (!last) return false;
                if (p.Length == 1 && p[0] >= '0' && p[0] <= '9') p = "D" + p;
                Keys k;
                if (!Enum.TryParse(p, true, out k) || IsModifierKey(k) || k == Keys.None) return false;
                key = k;
            }
            return modifiers != 0 && key != Keys.None;   // a bare key would be taken from every program
        }

        static bool IsModifierKey(Keys k)
        {
            return k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.Menu || k == Keys.LMenu
                || k == Keys.RMenu || k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey || k == Keys.LWin || k == Keys.RWin
                || k == Keys.Control || k == Keys.Alt || k == Keys.Shift;
        }

        public static string Format(uint modifiers, Keys key)
        {
            List<string> parts = new List<string>();
            if ((modifiers & Ctrl) != 0) parts.Add("Ctrl");
            if ((modifiers & Alt) != 0) parts.Add("Alt");
            if ((modifiers & Shift) != 0) parts.Add("Shift");
            if ((modifiers & Win) != 0) parts.Add("Win");
            string k = key.ToString();
            if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k.Substring(1);
            parts.Add(k);
            return string.Join("+", parts.ToArray());
        }

        // The same combination written the same way ("alt + q" becomes "Alt+Q"), or null if it is not one.
        public static string Normalize(string text)
        {
            uint m;
            Keys k;
            return TryParse(text, out m, out k) ? Format(m, k) : null;
        }
    }

    // When the combination should be registered: turned on, valid, and the game in front.
    sealed class HotkeyState
    {
        public bool Registered { get; private set; }

        public static bool Wanted(bool enabled, bool valid, bool gameForeground) { return enabled && valid && gameForeground; }

        // Returns +1 to register now, -1 to release now, 0 to leave it as it is.
        public int Step(bool enabled, bool valid, bool gameForeground)
        {
            bool want = Wanted(enabled, valid, gameForeground);
            if (want == Registered) return 0;
            return want ? 1 : -1;
        }

        public void Done(bool registered) { Registered = registered; }
    }

    // An invisible window that only receives the hotkey message from Windows.
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312, Id = 0x4851;   // any id unique within this program
        const uint MOD_NOREPEAT = 0x4000;
        public event EventHandler Pressed;
        public bool Registered { get; private set; }

        public HotkeyWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Parent = new IntPtr(-3);   // HWND_MESSAGE: a message-only window, never shown
            CreateHandle(cp);
        }

        public bool Register(uint modifiers, Keys key)
        {
            Unregister();
            Registered = Native.RegisterHotKey(Handle, Id, modifiers | MOD_NOREPEAT, (uint)key);
            return Registered;
        }

        public void Unregister()
        {
            if (Registered) Native.UnregisterHotKey(Handle, Id);
            Registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == Id && Pressed != null) Pressed(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }
}
