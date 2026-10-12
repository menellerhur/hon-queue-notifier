using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Where the app keeps its own files: always %APPDATA%\HonQueueNotifier, never next to the exe.
    // =====================================================================================
    static class AppFiles
    {
        // Files from the assets folder, compiled into the exe (EmbeddedResource in the .csproj).
        public static byte[] Resource(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new FileNotFoundException("resource missing from the exe: " + name);
                using (MemoryStream m = new MemoryStream())
                {
                    s.CopyTo(m);
                    return m.ToArray();
                }
            }
        }

        // A file or web address in the program Windows uses for it (the log in the editor, a link in the
        // browser). A failure is logged, nothing more.
        public static void Open(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            try { Process.Start(target); }
            catch (Exception ex) { AppLog.Write("could not open {0}: {1}", target, ex.Message); }
        }

        public const string SettingsName = "HonQueueNotifier.ini";
        public const string LogName = "HonQueueNotifier.log";

        public static string Folder()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HonQueueNotifier");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
