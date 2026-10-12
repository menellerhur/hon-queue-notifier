using System;
using System.Collections.Generic;
using System.IO;

namespace HonQueueNotifier
{
    // Finds the console.log files to follow. The log lives in the profile folder of the last mod on the
    // game's command line: Documents\Juvio\Heroes of Newerth for a default launch, Documents\Juvio\extensions
    // for a -mod "heroes of newerth;extensions" launch. Every console.log under Documents\Juvio is followed.
    sealed class LogWatcher
    {
        readonly Settings _settings;
        readonly Dictionary<string, LogTailer> _byPath = new Dictionary<string, LogTailer>(StringComparer.OrdinalIgnoreCase);
        readonly List<LogTailer> _tailers = new List<LogTailer>();
        bool _firstScan = true;
        DateTime _lastScanUtc = DateTime.MinValue;

        public LogWatcher(Settings settings) { _settings = settings; }

        public IList<LogTailer> Tailers { get { return _tailers; } }

        public static string JuvioDocumentsRoot()
        {
            string docs = "";
            try { docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch (Exception) { }
            if (string.IsNullOrEmpty(docs))
            {
                string home = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
                docs = Path.Combine(home, "Documents");
            }
            return Path.Combine(docs, "Juvio");
        }

        static void AddIfFile(List<string> list, string path)
        {
            try { if (File.Exists(path)) list.Add(path); } catch (Exception) { }
        }

        public static List<string> FindCandidates(Settings s)
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.LogPath))
            {
                list.Add(Environment.ExpandEnvironmentVariables(s.LogPath.Trim().Trim('"')));
                return list;
            }
            string root = JuvioDocumentsRoot();
            try
            {
                if (Directory.Exists(root))
                {
                    AddIfFile(list, Path.Combine(root, "console.log"));
                    foreach (string d1 in Directory.GetDirectories(root))
                    {
                        AddIfFile(list, Path.Combine(d1, "console.log"));
                        try
                        {
                            foreach (string d2 in Directory.GetDirectories(d1)) AddIfFile(list, Path.Combine(d2, "console.log"));
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        public void RescanIfDue(DateTime nowUtc)
        {
            double since = (nowUtc - _lastScanUtc).TotalSeconds;
            if (since >= 5 || since < 0) Rescan(nowUtc);
        }

        public void Rescan(DateTime nowUtc)
        {
            _lastScanUtc = nowUtc;
            List<string> found = FindCandidates(_settings);
            for (int i = 0; i < found.Count; i++)
            {
                string p = found[i];
                if (_byPath.ContainsKey(p)) continue;
                bool exists = false;
                try { exists = File.Exists(p); } catch (Exception) { }
                LogTailer t = new LogTailer(p, _firstScan && exists);
                _byPath[p] = t;
                _tailers.Add(t);
                AppLog.Write("watching {0}{1}", p, exists ? "" : " (not there yet)");
            }
            _firstScan = false;
        }
    }
}
