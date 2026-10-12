// HoN Queue Notifier
//
// Shows a popup (and optionally pushes to your phone) when Heroes of Newerth: Reborn
// finds a match while the game is not the focused window.
//
// How it works: the game client writes a line to its own console.log the moment
// matchmaking hands you a game. This program tails that file. It never opens, reads
// or modifies the game process and it never sends input to the game. The only things
// it does with the game window are ordinary window-manager calls: asking Windows which
// window is focused, flashing the taskbar button, bringing the window to the front
// when you click the popup, and minimizing it with the optional hotkey.
//
// Build: double-click build.cmd (needs the .NET SDK). It builds HonQueueNotifier.sln, runs the
// tests and leaves HonQueueNotifier.exe in the repository folder. The app targets .NET Framework
// 4.8, which is built into Windows 10 and 11, so players need nothing but the exe.
//
// Command line:
//   HonQueueNotifier.exe                    run in the tray (normal use)
//   HonQueueNotifier.exe --test             run in the tray and show a test alert at once (it stays until clicked)
//   HonQueueNotifier.exe --selftest         run the built-in checks, write selftest-result.txt to %APPDATA%\HonQueueNotifier
//   HonQueueNotifier.exe --replay <log>     show what would have been detected in a saved console.log
//   HonQueueNotifier.exe --watch            console mode: print what is detected, live, without a popup
// From a command prompt, start the last two with "start /wait" in front so the prompt waits for them.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Entry point
    // =====================================================================================
    static class Program
    {
        // The program's version as players see it ("1.0.0"), from <Version> in the .csproj.
        internal static string Version { get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } }

        // Where the program lives: source code, releases, issues.
        internal const string RepoUrl = "https://github.com/menellerhur/hon-queue-notifier";

        static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static bool Has(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void UseParentConsole()
        {
            // This is a windowed program, so it has no console of its own; borrow the one it was started from.
            if (Native.IsWindows) { try { Native.AttachConsole(-1); } catch (Exception) { } }
        }

        static Settings LoadOrCreate(string iniPath, out bool created)
        {
            created = false;
            if (!File.Exists(iniPath))
            {
                new Settings().Save(iniPath);
                created = true;
            }
            List<string> warnings = new List<string>();
            Settings s = Settings.Load(iniPath, warnings);
            for (int i = 0; i < warnings.Count; i++) AppLog.Write("settings: {0}", warnings[i]);
            return s;
        }

        [STAThread]
        static int Main(string[] args)
        {
            bool gui = !(Has(args, "--selftest") || Has(args, "--replay") || Has(args, "--watch") || Has(args, "--help") || Has(args, "/?"));
            try
            {
                return Run(args, gui);
            }
            catch (Exception ex)
            {
                try { AppLog.Write("fatal: {0}", ex); } catch (Exception) { }
                if (gui) { try { MessageBox.Show(ex.Message, "HoN Queue Notifier", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch (Exception) { } }
                else { try { Console.WriteLine("Error: " + ex.Message); } catch (Exception) { } }
                return 1;
            }
        }

        static int Run(string[] args, bool gui)
        {
            string dir = AppFiles.Folder();
            string iniPath = Arg(args, "--settings") ?? Path.Combine(dir, AppFiles.SettingsName);

            if (Has(args, "--selftest"))
            {
                // Writes its report to a file, nothing to the console (dotnet test runs the same checks).
                return SelfTest.Run(Arg(args, "--out") ?? Path.Combine(dir, "selftest-result.txt"));
            }
            if (Has(args, "--help") || Has(args, "/?"))
            {
                UseParentConsole();
                Console.WriteLine("HoN Queue Notifier");
                Console.WriteLine("  (no arguments)        run in the tray");
                Console.WriteLine("  --test                run in the tray and show a test alert at once");
                Console.WriteLine("  --selftest            run the built-in checks and write selftest-result.txt");
                Console.WriteLine("  --replay <log>        show what would have been detected in a saved console.log");
                Console.WriteLine("       [--chunk <n>]    feed it n bytes at a time (default: random-sized pieces)");
                Console.WriteLine("       [--focused]      pretend the game window was focused");
                Console.WriteLine("  --watch               console mode: print what is detected, live");
                Console.WriteLine("  --settings <file>     use this settings file");
                Console.WriteLine("  --log <file>          watch this console.log (this run only)");
                Console.WriteLine("From a command prompt, start the console modes with:  start /wait HonQueueNotifier.exe --watch");
                return 0;
            }
            if (Has(args, "--replay"))
            {
                UseParentConsole();
                string file = Arg(args, "--replay");
                if (file == null || !File.Exists(file)) { Console.WriteLine("Log file not found: " + file); return 2; }
                int chunk = 0;
                string c = Arg(args, "--chunk");
                if (c != null) int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out chunk);
                return ConsoleModes.Replay(file, chunk, Has(args, "--focused"));
            }

            AppLog.Init(Path.Combine(dir, AppFiles.LogName));
            bool created;
            Settings settings = LoadOrCreate(iniPath, out created);
            string logOverride = Arg(args, "--log");
            if (logOverride != null) settings.LogPath = logOverride;

            if (Has(args, "--watch"))
            {
                UseParentConsole();
                return ConsoleModes.Watch(settings);
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\HonQueueNotifier.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("HoN Queue Notifier is already running. Look for its icon in the tray, near the clock.",
                        "HoN Queue Notifier", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                if (Native.IsWindows) { try { Native.SetProcessDPIAware(); } catch (Exception) { } }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { AppLog.Write("unhandled: {0}", e.Exception); };
                AppLog.Write("started, version {0}, settings {1}", Version, iniPath);
                Application.Run(new TrayApp(iniPath, settings, created, Has(args, "--test")));
                AppLog.Write("exited");
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
