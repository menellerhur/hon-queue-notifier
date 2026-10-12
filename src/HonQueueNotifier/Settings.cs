using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Settings (HonQueueNotifier.ini in %APPDATA%\HonQueueNotifier; the dialog reads and writes the same file)
    // Every setting here is also in the settings dialog. A value not worth showing there is a
    // constant in the code instead, never a setting that exists only in the file.
    // =====================================================================================
    sealed class Settings
    {
        // [General]
        public string LogPath = "";
        public bool MinimizeHotkeyEnabled = false;
        public string MinimizeHotkey = "Alt+Q";
        // [Popup]
        public bool PopupEnabled = true;
        public string PopupPosition = "TopCenter";
        public string PopupScreens = "Primary";
        public bool FlashTaskbar = true;
        // [Sound]
        public bool SoundEnabled = true;
        public string SoundFile = "";
        public int SoundRepeatSeconds = 12;
        public int SoundRepeatForSeconds = 120;
        // [Phone]
        public string PhoneProvider = "none";
        public string PhoneMode = "smart";
        public int PhoneAfterSeconds = 8;
        public int PhoneIdleSeconds = 30;
        public string NtfyServer = "https://ntfy.sh";
        public string NtfyTopic = "";
        public string NtfyPriority = "high";

        public bool PhoneEnabled
        {
            get { return string.Equals((PhoneProvider ?? "").Trim(), "ntfy", StringComparison.OrdinalIgnoreCase); }
        }

        public bool PhoneAlways
        {
            get { return string.Equals((PhoneMode ?? "").Trim(), "always", StringComparison.OrdinalIgnoreCase); }
        }

        public const string DisplayName = "HoN Queue Notifier";
        public static readonly string[] Positions = { "TopCenter", "TopLeft", "TopRight", "BottomCenter", "BottomLeft", "BottomRight", "Center" };
        public static readonly string[] ScreenModes = { "Primary", "All", "Cursor" };
        public static readonly string[] NtfyPriorities = { "min", "low", "default", "high", "max" };
        static readonly Regex TopicPattern = new Regex("^[A-Za-z0-9_-]{1,64}$");

        public static int IndexOf(string[] list, string value)
        {
            string v = (value ?? "").Trim();
            for (int i = 0; i < list.Length; i++)
                if (string.Equals(list[i], v, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public Settings Clone()
        {
            return Parse(ToIni(), null);
        }

        // Written to a temporary file first, so a crash halfway never leaves a half-written settings file.
        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToIni(), Encoding.UTF8);
            if (!File.Exists(path)) { File.Move(temp, path); return; }
            try { File.Replace(temp, path, null); }
            catch (IOException)
            {
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }

        static string CheckFile(string raw, bool mustExist, string example)
        {
            try
            {
                string path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (!Path.IsPathRooted(path) || Path.GetFileName(path).Length == 0) return "Enter the full path of a file, for example " + example + ".";
                if (mustExist) return File.Exists(path) ? null : "File not found: " + path;
                string dir = Path.GetDirectoryName(path);
                return Directory.Exists(dir) ? null : "Folder not found: " + dir;
            }
            catch (Exception) { return "This is not a valid file path."; }
        }

        static void Problem(List<SettingProblem> list, string key, string text)
        {
            list.Add(new SettingProblem(key, text));
        }

        // Everything the settings dialog refuses, keyed by the INI key it belongs to.
        public List<SettingProblem> Validate()
        {
            List<SettingProblem> p = new List<SettingProblem>();
            if (MinimizeHotkeyEnabled && HotkeyText.Normalize(MinimizeHotkey) == null)
                Problem(p, "General.MinimizeHotkey", "Press Ctrl, Alt or Shift together with one key, for example Alt+Q.");
            if (!string.IsNullOrWhiteSpace(LogPath))
            {
                string why = CheckFile(LogPath, false, "C:\\...\\console.log");
                if (why != null) Problem(p, "General.LogPath", why);
            }
            if (IndexOf(Positions, PopupPosition) < 0) Problem(p, "Popup.Position", "Choose a position from the list.");
            if (IndexOf(ScreenModes, PopupScreens) < 0) Problem(p, "Popup.Screens", "Choose Primary, All or Cursor.");
            if (!string.IsNullOrWhiteSpace(SoundFile))
            {
                string why = CheckFile(SoundFile, true, "C:\\Sounds\\alert.wav");
                if (why == null && !SoundFile.Trim().Trim('"').EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) why = "Only .wav files can be played.";
                if (why != null) Problem(p, "Sound.File", why);
            }
            if (PhoneEnabled)
            {
                string server = (NtfyServer ?? "").Trim();
                Uri uri;
                if (server.Length > 0 && !(Uri.TryCreate(server, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
                    Problem(p, "Phone.NtfyServer", "Enter a web address such as https://ntfy.sh, or leave it empty for ntfy.sh.");
                string topic = (NtfyTopic ?? "").Trim();
                if (topic.Length == 0) Problem(p, "Phone.NtfyTopic", "A topic is required.");
                else if (!TopicPattern.IsMatch(topic)) Problem(p, "Phone.NtfyTopic", "Use only letters, digits, - and _ (at most 64).");
                string priority = (NtfyPriority ?? "").Trim();
                if (priority.Length > 0 && IndexOf(NtfyPriorities, priority) < 0 && !(priority.Length == 1 && priority[0] >= '1' && priority[0] <= '5'))
                    Problem(p, "Phone.NtfyPriority", "Choose a priority from the list.");
                string mode = (PhoneMode ?? "").Trim();
                if (!string.Equals(mode, "smart", StringComparison.OrdinalIgnoreCase) && !PhoneAlways) Problem(p, "Phone.Mode", "Choose when to send.");
            }
            return p;
        }

        sealed class IniReader
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Warnings;

            void Warn(string text) { if (Warnings != null) Warnings.Add(text); }

            public void Str(string key, ref string field)
            {
                Known.Add(key);
                string v;
                if (Values.TryGetValue(key, out v)) field = v;
            }

            public void Int(string key, ref int field, int min, int max)
            {
                Known.Add(key);
                string v;
                if (!Values.TryGetValue(key, out v)) return;
                int n;
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) field = Math.Max(min, Math.Min(max, n));
                else Warn(key + ": '" + v + "' is not a whole number, keeping " + field.ToString(CultureInfo.InvariantCulture));
            }

            public void Bool(string key, ref bool field)
            {
                Known.Add(key);
                string v;
                if (!Values.TryGetValue(key, out v)) return;
                switch (v.Trim().ToLowerInvariant())
                {
                    case "true": case "yes": case "on": case "1": field = true; break;
                    case "false": case "no": case "off": case "0": field = false; break;
                    default: Warn(key + ": '" + v + "' is not true or false, keeping " + (field ? "true" : "false")); break;
                }
            }
        }

        public static Settings Parse(string text, List<string> warnings)
        {
            Settings s = new Settings();
            IniReader r = new IniReader();
            r.Warnings = warnings;
            string section = "";
            foreach (string rawLine in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']') { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) { if (warnings != null) warnings.Add("ignored line: " + line); continue; }
                r.Values[section + "." + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            r.Str("General.LogPath", ref s.LogPath);
            r.Bool("General.MinimizeHotkeyEnabled", ref s.MinimizeHotkeyEnabled);
            r.Str("General.MinimizeHotkey", ref s.MinimizeHotkey);

            r.Bool("Popup.Enabled", ref s.PopupEnabled);
            r.Str("Popup.Position", ref s.PopupPosition);
            r.Str("Popup.Screens", ref s.PopupScreens);
            r.Bool("Popup.FlashTaskbar", ref s.FlashTaskbar);

            r.Bool("Sound.Enabled", ref s.SoundEnabled);
            r.Str("Sound.File", ref s.SoundFile);
            r.Int("Sound.RepeatSeconds", ref s.SoundRepeatSeconds, 0, 3600);
            r.Int("Sound.RepeatForSeconds", ref s.SoundRepeatForSeconds, 0, 86400);

            r.Str("Phone.Provider", ref s.PhoneProvider);
            r.Str("Phone.Mode", ref s.PhoneMode);
            r.Int("Phone.AfterSeconds", ref s.PhoneAfterSeconds, 0, 3600);
            r.Int("Phone.IdleSeconds", ref s.PhoneIdleSeconds, 1, 86400);
            r.Str("Phone.NtfyServer", ref s.NtfyServer);
            r.Str("Phone.NtfyTopic", ref s.NtfyTopic);
            r.Str("Phone.NtfyPriority", ref s.NtfyPriority);

            if (warnings != null)
            {
                foreach (string key in r.Values.Keys)
                    if (!r.Known.Contains(key)) warnings.Add("unknown setting: " + key);
            }
            return s;
        }

        public static Settings Load(string path, List<string> warnings)
        {
            return Parse(File.ReadAllText(path), warnings);
        }

        static string B(bool v) { return v ? "true" : "false"; }
        static string N(int v) { return v.ToString(CultureInfo.InvariantCulture); }

        public string ToIni()
        {
            StringBuilder b = new StringBuilder();
            string nl = "\r\n";
            b.Append("; HoN Queue Notifier settings.").Append(nl);
            b.Append("; Every setting is also in the settings dialog: double-click the tray icon.").Append(nl);
            b.Append("; This file can also be edited by hand; changes are picked up within a few seconds.").Append(nl);
            b.Append("; Lines starting with ; are comments. The dialog rewrites this file and does not keep your own comments.").Append(nl);
            b.Append(nl);
            b.Append("[General]").Append(nl);
            b.Append("; Leave empty to watch every console.log under Documents\\Juvio. That covers the default").Append(nl);
            b.Append("; launch as well as -mod launches. Or the full path of the one console.log to watch.").Append(nl);
            b.Append("LogPath=").Append(LogPath).Append(nl);
            b.Append("; Minimize the game with a key combination while it has focus (a fullscreen game cannot be minimized otherwise).").Append(nl);
            b.Append("MinimizeHotkeyEnabled=").Append(B(MinimizeHotkeyEnabled)).Append(nl);
            b.Append("; Ctrl, Alt or Shift plus one key, for example Alt+Q or Ctrl+Alt+M.").Append(nl);
            b.Append("MinimizeHotkey=").Append(MinimizeHotkey).Append(nl);
            b.Append(nl);
            b.Append("[Popup]").Append(nl);
            b.Append("; The popup stays until you click it or the game gets focus.").Append(nl);
            b.Append("Enabled=").Append(B(PopupEnabled)).Append(nl);
            b.Append("; TopCenter, TopLeft, TopRight, BottomCenter, BottomLeft, BottomRight or Center").Append(nl);
            b.Append("Position=").Append(PopupPosition).Append(nl);
            b.Append("; Primary (your main display), All (every display), or Cursor (the display the mouse pointer is on)").Append(nl);
            b.Append("Screens=").Append(PopupScreens).Append(nl);
            b.Append("; Also flash the game's taskbar button").Append(nl);
            b.Append("FlashTaskbar=").Append(B(FlashTaskbar)).Append(nl);
            b.Append(nl);
            b.Append("[Sound]").Append(nl);
            b.Append("Enabled=").Append(B(SoundEnabled)).Append(nl);
            b.Append("; Empty = the built-in sound. Or the full path of a .wav file.").Append(nl);
            b.Append("File=").Append(SoundFile).Append(nl);
            b.Append("; Repeat the sound every N seconds until you react (0 = play once) ...").Append(nl);
            b.Append("RepeatSeconds=").Append(N(SoundRepeatSeconds)).Append(nl);
            b.Append("; ... but stop repeating after this many seconds").Append(nl);
            b.Append("RepeatForSeconds=").Append(N(SoundRepeatForSeconds)).Append(nl);
            b.Append(nl);
            b.Append("[Phone]").Append(nl);
            b.Append("; none or ntfy").Append(nl);
            b.Append("Provider=").Append(PhoneProvider).Append(nl);
            b.Append("; smart  = push only if you have not reacted after AfterSeconds, or at once when the PC").Append(nl);
            b.Append(";          has seen no keyboard or mouse input for IdleSeconds (you are probably away)").Append(nl);
            b.Append("; always = push on every match found").Append(nl);
            b.Append("Mode=").Append(PhoneMode).Append(nl);
            b.Append("AfterSeconds=").Append(N(PhoneAfterSeconds)).Append(nl);
            b.Append("IdleSeconds=").Append(N(PhoneIdleSeconds)).Append(nl);
            b.Append("; ntfy is free: install the ntfy app (https://ntfy.sh), choose a topic name only you would use").Append(nl);
            b.Append("; and subscribe to it in the app. Anyone who uses the same topic name gets the alerts.").Append(nl);
            b.Append("NtfyServer=").Append(NtfyServer).Append(nl);
            b.Append("NtfyTopic=").Append(NtfyTopic).Append(nl);
            b.Append("; min, low, default, high or max").Append(nl);
            b.Append("NtfyPriority=").Append(NtfyPriority).Append(nl);
            return b.ToString();
        }
    }

    sealed class SettingProblem
    {
        public readonly string Key;
        public readonly string Text;
        public SettingProblem(string key, string text) { Key = key; Text = text; }
    }

    // Notices when a file was changed by someone else (time and length, read from the file system).
    sealed class FileStamp
    {
        readonly string _path;
        DateTime _time;
        long _length;

        public FileStamp(string path) { _path = path; Take(); }

        void Read(out DateTime time, out long length)
        {
            time = DateTime.MinValue;
            length = -1;
            try
            {
                FileInfo fi = new FileInfo(_path);
                if (fi.Exists) { time = fi.LastWriteTimeUtc; length = fi.Length; }
            }
            catch (Exception) { }
        }

        public void Take() { Read(out _time, out _length); }

        public bool Changed()
        {
            DateTime time;
            long length;
            Read(out time, out length);
            if (time == _time && length == _length) return false;
            _time = time;
            _length = length;
            return true;
        }
    }
}
