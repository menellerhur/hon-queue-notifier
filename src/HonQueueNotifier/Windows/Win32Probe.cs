using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace HonQueueNotifier
{
    // Everything here goes through the process list and the window manager. The game process itself
    // is never opened: no handles to it, no memory access.
    sealed class Win32Probe : IEnvironmentProbe
    {
        readonly string[] _names;
        readonly int _onlyId;
        HashSet<int> _pids;
        int _pidsTick;
        IntPtr _flashing = IntPtr.Zero;

        // The game's process: %LOCALAPPDATA%\Juvio\bin\juvio.exe.
        public const string GameProcess = "juvio";

        public Win32Probe() { _names = new[] { GameProcess }; }

        // Only for the self-test: a stand-in window of its own process, and nothing else. (By name it would
        // also find a copy of this program that is running in the tray, and minimize that one's window.)
        public Win32Probe(int processId) { _names = new string[0]; _onlyId = processId; }

        HashSet<int> GamePids()
        {
            int tick = Environment.TickCount;
            int age = unchecked(tick - _pidsTick);
            if (_onlyId != 0) return new HashSet<int> { _onlyId };
            if (_pids != null && age >= 0 && age < 1000) return _pids;
            HashSet<int> set = new HashSet<int>();
            for (int i = 0; i < _names.Length; i++)
            {
                try
                {
                    Process[] found = Process.GetProcessesByName(_names[i]);
                    for (int j = 0; j < found.Length; j++)
                    {
                        try { set.Add(found[j].Id); } catch (Exception) { }
                        found[j].Dispose();
                    }
                }
                catch (Exception) { }
            }
            _pids = set;
            _pidsTick = tick;
            return set;
        }

        public bool IsGameRunning() { return GamePids().Count > 0; }

        public bool IsGameForeground()
        {
            if (!Native.IsWindows) return false;
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            return pid != 0 && GamePids().Contains((int)pid);
        }

        public int IdleSeconds()
        {
            if (!Native.IsWindows) return 0;
            Native.LASTINPUTINFO info = new Native.LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO));
            if (!Native.GetLastInputInfo(ref info)) return 0;
            uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return (int)(idleMs / 1000);
        }

        public string DescribeForeground()
        {
            if (!Native.IsWindows) return "n/a";
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return "none";
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                using (Process p = Process.GetProcessById((int)pid))
                    return p.ProcessName + " (pid " + pid.ToString(CultureInfo.InvariantCulture) + ")";
            }
            catch (Exception) { return "unknown"; }
        }

        public IntPtr FindGameWindow()
        {
            if (!Native.IsWindows) return IntPtr.Zero;
            HashSet<int> pids = GamePids();
            if (pids.Count == 0) return IntPtr.Zero;
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            Native.EnumWindowsProc callback = delegate(IntPtr h, IntPtr unused)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, 4) != IntPtr.Zero) return true;   // GW_OWNER: skip owned dialogs
                long area = 0;
                Native.RECT r;
                if (Native.GetWindowRect(h, out r)) area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            };
            Native.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return best;
        }

        // For the minimize hotkey (off unless turned on): the game's window, found as for focus and flashing,
        // minimized as its minimize button would (SW_MINIMIZE). Nothing is sent to the game.
        public bool MinimizeGame()
        {
            IntPtr h = FindGameWindow();
            if (h == IntPtr.Zero) return false;
            Native.ShowWindow(h, 6);
            return Native.IsIconic(h);
        }

        public bool BringGameToFront()
        {
            IntPtr h = FindGameWindow();
            if (h == IntPtr.Zero) return false;
            if (Native.IsIconic(h)) Native.ShowWindow(h, 9);   // SW_RESTORE
            return Native.SetForegroundWindow(h);
        }

        public void FlashGame(bool on)
        {
            if (!Native.IsWindows) return;
            IntPtr h = on ? FindGameWindow() : _flashing;
            if (h == IntPtr.Zero) return;
            Native.FLASHWINFO info = new Native.FLASHWINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(Native.FLASHWINFO));
            info.hwnd = h;
            info.dwFlags = on ? (uint)(3 | 12) : 0u;   // FLASHW_ALL | FLASHW_TIMERNOFG, or FLASHW_STOP
            info.uCount = on ? uint.MaxValue : 0u;
            info.dwTimeout = 0;
            Native.FlashWindowEx(ref info);
            _flashing = on ? h : IntPtr.Zero;
        }
    }
}
