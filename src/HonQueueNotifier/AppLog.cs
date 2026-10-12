using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Own diagnostic log (HonQueueNotifier.log in %APPDATA%\HonQueueNotifier)
    // =====================================================================================
    static class AppLog
    {
        static readonly object Gate = new object();
        static string _path;
        public static bool Echo;

        public static string FilePath { get { return _path; } }

        public static void Init(string path)
        {
            _path = path;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    string old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
            }
            catch (Exception) { }
        }

        public static void Write(string format, params object[] args)
        {
            string msg;
            try { msg = (args == null || args.Length == 0) ? format : string.Format(CultureInfo.InvariantCulture, format, args); }
            catch (FormatException) { msg = format; }
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg;
            lock (Gate)
            {
                if (Echo) { try { Console.WriteLine(line); } catch (Exception) { } }
                if (_path != null) { try { File.AppendAllText(_path, line + "\r\n", Encoding.UTF8); } catch (Exception) { } }
            }
        }
    }
}
