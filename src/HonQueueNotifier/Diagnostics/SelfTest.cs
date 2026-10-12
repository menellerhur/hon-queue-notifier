using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HonQueueNotifier
{
    static class SelfTest
    {
        // The dialog and the popup are checked as the app shows them: with Windows' visual styles and GDI
        // text (not WinForms' default GDI+ text), which the app turns on at start and which change how
        // controls look and measure. The second call fails once a window exists; the first caller wins.
        static SelfTest()
        {
            try { Application.EnableVisualStyles(); } catch (Exception) { }
            try { Application.SetCompatibleTextRenderingDefault(false); } catch (Exception) { }
        }

        static int _passed, _failed;
        static Rectangle _lastLink;
        static readonly StringBuilder Out = new StringBuilder();
        static List<string> _failures = new List<string>();

        static void Line(string s)
        {
            Out.Append(s).Append("\r\n");
            try { Console.WriteLine(s); } catch (Exception) { }
        }

        static void Check(bool ok, string name)
        {
            if (ok) _passed++; else { _failed++; _failures.Add(name); }
            Line((ok ? "  ok    " : "  FAIL  ") + name);
        }

        static void Section(string name, Action body)
        {
            Line(name);
            try { body(); }
            catch (Exception ex)
            {
                _failed++;
                _failures.Add("threw " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
                Line("  FAIL  threw " + ex.GetType().Name + ": " + ex.Message);
                Line(ex.StackTrace ?? "");
            }
        }

        // The areas, in the order --selftest runs them. The test project runs each as its own test.
        static readonly KeyValuePair<string, Action>[] Sections =
        {
            new KeyValuePair<string, Action>("Reading log text", ScannerTests),
            new KeyValuePair<string, Action>("Following what the client is doing", TrackerTests),
            new KeyValuePair<string, Action>("Deciding when to alert", EngineTests),
            new KeyValuePair<string, Action>("Following a log file on this disk", TailerTests),
            new KeyValuePair<string, Action>("Following a UTF-16 log, as the game writes it", Utf16TailerTests),
            new KeyValuePair<string, Action>("Settings", SettingsTests),
            new KeyValuePair<string, Action>("Phone requests", PushTests),
            new KeyValuePair<string, Action>("Settings file and validation", SettingsFileTests),
            new KeyValuePair<string, Action>("Settings dialog", DialogTests),
            new KeyValuePair<string, Action>("The popup", PopupTests),
            new KeyValuePair<string, Action>("The minimize hotkey", HotkeyTests),
            new KeyValuePair<string, Action>("The program's icon", IconTests),
            new KeyValuePair<string, Action>("Odds and ends", MiscTests),
        };

        public static IEnumerable<string> SectionNames()
        {
            foreach (KeyValuePair<string, Action> s in Sections) yield return s.Key;
        }

        // Runs one area and returns what failed in it (empty when everything passed).
        public static List<string> RunSection(string name)
        {
            _failures = new List<string>();
            foreach (KeyValuePair<string, Action> s in Sections)
                if (s.Key == name) { Section(s.Key, s.Value); return _failures; }
            throw new ArgumentException("no self-test section called " + name);
        }

        const string Sample =
            "Wrote 'C:\\Users\\x\\Documents\\Juvio\\extensions\\webcache\\2b39.webp'\r\n" +
            "Disconnect - CMatchmakingClient::HandleFoundMatch\r\n" +
            "Clearing all state data...\r\n" +
            "Client loading world \"midwars\"\r\n" +
            "Start loading midwars\r\n" +
            "CLIENT_STATE_LOADING\r\n" +
            "Client phase MainMenu -> Loading, 0ms since leaving the menu\r\n" +
            "Loading Material Loading Material '/world/water/materials/default.material'\r\n" +
            "Disconnect - CHostClient::Connect\r\n" +
            "Client phase Loading -> Lobby after 2324ms, 2324ms since leaving the menu\r\n" +
            "Client phase Lobby -> InGame after 144368ms, 146692ms since leaving the menu\r\n" +
            "Disconnect - UI_VOID_CMD(Disconnect)\r\n" +
            "Client phase InGame -> MainMenu after 1053684ms, 1200376ms since leaving the menu\r\n";

        const string SampleEvents = "found;world:midwars;phase:MainMenu>Loading;phase:Loading>Lobby;phase:Lobby>InGame;phase:InGame>MainMenu";

        static string Describe(List<LogEvent> events)
        {
            List<string> parts = new List<string>();
            foreach (LogEvent e in events)
            {
                switch (e.Kind)
                {
                    case LogEventKind.MatchFound: parts.Add("found"); break;
                    case LogEventKind.World: parts.Add("world:" + e.A); break;
                    case LogEventKind.Phase: parts.Add("phase:" + e.A + ">" + e.B); break;
                    case LogEventKind.EngineStart: parts.Add("engine"); break;
                    default: parts.Add("reset"); break;
                }
            }
            return string.Join(";", parts.ToArray());
        }

        static LogEvent Ev(LogEventKind kind, string a, string b)
        {
            LogEvent e = new LogEvent();
            e.Kind = kind;
            e.A = a;
            e.B = b;
            return e;
        }

        static List<LogEvent> Parse(string text)
        {
            LogScanner sc = new LogScanner();
            List<LogEvent> events = new List<LogEvent>();
            sc.Feed(text, events);
            return events;
        }

        static void ScannerTests()
        {
            Check(Describe(Parse(Sample)) == SampleEvents, "one piece: finds match, map and all four phase changes");

            LogScanner sc = new LogScanner();
            List<LogEvent> events = new List<LogEvent>();
            for (int i = 0; i < Sample.Length; i++) sc.Feed(Sample.Substring(i, 1), events);
            Check(Describe(events) == SampleEvents, "one character at a time: same result");

            bool allSplits = true;
            for (int cut = 1; cut < Sample.Length; cut++)
            {
                sc = new LogScanner();
                events = new List<LogEvent>();
                sc.Feed(Sample.Substring(0, cut), events);
                sc.Feed(Sample.Substring(cut), events);
                if (Describe(events) != SampleEvents) { allSplits = false; Line("        differs when cut at " + cut); break; }
            }
            Check(allSplits, "cut in two at every possible position: same result, nothing lost or doubled");

            Check(Describe(Parse("Loading ^gTexture^* Disconnect - CMatchmakingClient::HandleFoundMatch\r\n")) == "found",
                "trigger glued onto another thread's text is still found");

            sc = new LogScanner();
            events = new List<LogEvent>();
            sc.Feed("Disconnect - CMatchmakingClient::HandleFoundMatch", events);
            bool heldBack = events.Count == 0;
            sc.Feed("\r\nnext line\r\n", events);
            Check(heldBack && Describe(events) == "found", "unfinished last line waits for its line ending, then counts once");

            sc = new LogScanner();
            events = new List<LogEvent>();
            sc.Feed(new string('x', 3000) + "CMatchmakingClient::HandleFoundMatch" + new string('y', 3000), events);
            int afterLong = events.Count;
            sc.Feed("zzz\r\n", events);
            Check(afterLong == 1 && events.Count == 1, "very long line without an ending: found once, not repeated");

            sc = new LogScanner();
            events = new List<LogEvent>();
            sc.Feed(new string('x', 5000) + "CMatchmakingClient::HandleFoundMatch" + new string('y', 10), events);
            int early = events.Count;
            sc.Feed("\r\n", events);
            Check(early == 0 && events.Count == 1, "trigger at the very end of a long unfinished line: found once when the line completes");

            Check(Describe(Parse("Engine start up...\r\n[2026/10/11]\r\n")) == "engine", "engine start is recognised");
            Check(Parse("Disconnect - CHostClient::Connect\r\nLoading Effect /ui/x/inqueue.effect\r\n").Count == 0, "unrelated lines produce nothing");
        }

        static void TrackerTests()
        {
            DateTime t0 = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
            MatchTracker t = new MatchTracker();
            foreach (LogEvent e in Parse(Sample)) t.Apply(e, t0);
            Check(t.MatchSeq == 1 && t.Phase == GamePhase.Menu && t.ExplicitFound, "full match: counted once, ends in the menu");

            foreach (LogEvent e in Parse(Sample)) t.Apply(e, t0.AddMinutes(30));
            Check(t.MatchSeq == 2, "second match is counted separately");

            t = new MatchTracker();
            t.Apply(Ev(LogEventKind.World, "midwars", null), t0);
            t.Apply(Ev(LogEventKind.Phase, "MainMenu", "Loading"), t0);
            Check(t.MatchSeq == 1 && t.Phase == GamePhase.MatchFound && !t.ExplicitFound && t.World == "midwars",
                "without the matchmaking line, the map load still counts as one match");

            t.Apply(Ev(LogEventKind.MatchFound, null, null), t0.AddSeconds(1));
            Check(t.MatchSeq == 1 && t.ExplicitFound, "matchmaking line arriving after its side effects is not counted twice");

            t = new MatchTracker();
            t.Apply(Ev(LogEventKind.Phase, "MainMenu", "Loading"), t0);
            Check(t.MatchSeq == 1 && t.Phase == GamePhase.MatchFound, "phase change alone is enough");

            t = new MatchTracker();
            t.Apply(Ev(LogEventKind.Phase, "Loading", "Lobby"), t0);
            Check(t.MatchSeq == 1 && t.Phase == GamePhase.Lobby, "lobby line alone is enough");
            t.Apply(Ev(LogEventKind.Phase, "Lobby", "Loading"), t0);
            t.Apply(Ev(LogEventKind.World, "caldavar", null), t0);
            Check(t.MatchSeq == 1, "lobby that loads a map afterwards is still the same match");

            t = new MatchTracker();
            foreach (LogEvent e in Parse(Sample.Substring(0, Sample.IndexOf("Disconnect - UI_VOID_CMD")))) t.Apply(e, t0);
            t.Apply(Ev(LogEventKind.Reset, null, null), t0);
            Check(t.Phase == GamePhase.Menu, "a new log (game restarted) puts the state back to the menu");
        }

        sealed class Rig
        {
            public readonly Settings S = new Settings();
            public readonly FakeProbe Probe = new FakeProbe();
            public readonly RecordingSink Sink = new RecordingSink();
            public readonly MatchTracker Tracker = new MatchTracker();
            public AlertEngine Engine;
            public DateTime Now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

            public Rig(bool phone)
            {
                S.SoundEnabled = true;
                if (phone) { S.PhoneProvider = "ntfy"; S.NtfyTopic = "t"; }
                Engine = new AlertEngine(S, Probe, Sink);
            }

            public void Feed(params LogEvent[] events) { Engine.Apply(Tracker, new List<LogEvent>(events), Now); }
            public void Advance(double seconds) { Now = Now.AddSeconds(seconds); Engine.Tick(Now); }
        }

        static LogEvent Found() { return Ev(LogEventKind.MatchFound, null, null); }
        static LogEvent World() { return Ev(LogEventKind.World, "midwars", null); }
        static LogEvent Phase(string a, string b) { return Ev(LogEventKind.Phase, a, b); }

        static void EngineTests()
        {
            bool echo = AppLog.Echo;
            AppLog.Echo = false;
            try
            {
                Rig r = new Rig(true);
                r.Feed(Found(), World(), Phase("MainMenu", "Loading"));
                Check(r.Sink.Joined() == "popup MATCH FOUND [Mid Wars] | sound | flash", "tabbed out: popup with the map name, sound and taskbar flash, once");
                r.Advance(5);
                Check(r.Sink.Count("push") == 0, "phone: nothing yet after 5 s");
                r.Advance(4);
                Check(r.Sink.Count("push") == 1, "phone: pushed after 8 s without a reaction");
                r.Advance(4);
                Check(r.Sink.Count("sound") == 2, "sound repeats after 12 s");
                r.Probe.Foreground = true;
                r.Advance(0.25);
                Check(r.Engine.Active == AlertStage.None && r.Sink.Count("hide") == 1, "alert goes away when the game gets focus");

                r = new Rig(false);
                r.S.SoundEnabled = false;
                r.Feed(Found(), World(), Phase("MainMenu", "Loading"));
                r.Advance(30);
                Check(r.Sink.Joined() == "popup MATCH FOUND [Mid Wars] | flash", "with sound turned off: only the popup and the flash");

                r = new Rig(true);
                r.Probe.Foreground = true;
                r.Feed(Found(), World());
                Check(r.Sink.Actions.Count == 0, "game focused and you are active: nothing at all");

                r = new Rig(true);
                r.Probe.Foreground = true;
                r.Probe.Idle = 60;
                r.Feed(Found(), World());
                Check(r.Sink.Joined() == "push HoN: match found", "game focused but PC idle for a minute: phone only");

                r = new Rig(true);
                r.Probe.Idle = 60;
                r.Feed(Found());
                Check(r.Sink.Count("popup") == 1 && r.Sink.Count("push") == 1, "tabbed out and idle: popup and an immediate push");

                r = new Rig(true);
                r.Feed(Found());
                r.Advance(3);
                r.Probe.Foreground = true;
                r.Advance(0.25);
                r.Probe.Foreground = false;
                r.Advance(10);
                Check(r.Sink.Count("push") == 0, "back in the game within 8 s: the push is cancelled");

                r = new Rig(true);
                r.Feed(Found(), World());
                r.Feed(Phase("Loading", "Lobby"));
                int before = r.Sink.Actions.Count;
                r.Feed(Phase("Lobby", "InGame"));
                Check(r.Sink.Joined().EndsWith(" | popup MATCH FOUND [Mid Wars] | popup GAME HAS STARTED [Mid Wars]", StringComparison.Ordinal)
                    && r.Sink.Actions.Count == before + 1 && r.Engine.Active == AlertStage.Started,
                    "still tabbed out when the game starts: the open popup changes to GAME HAS STARTED, with no sound, flash or push");
                r.Advance(9);
                Check(r.Sink.Count("push") == 1 && r.Sink.Count("push HoN: game has started") == 1,
                    "the match's one push still goes out when due, and says the game has started");

                r = new Rig(false);
                r.Feed(Found());
                for (int i = 0; i < 100; i++) r.Advance(1);
                r.Feed(Phase("Loading", "Lobby"), Phase("Lobby", "InGame"));
                for (int i = 0; i < 200; i++) r.Advance(1);
                Check(r.Engine.Active == AlertStage.Started && r.Sink.Count("sound") == 11,
                    "the game starting does not start the sound repeats over (still 1 + 10 in two minutes)");

                r = new Rig(false);
                r.Feed(Found());
                r.Engine.Acknowledge("closed");
                r.Feed(Phase("Loading", "Lobby"));
                before = r.Sink.Actions.Count;
                r.Feed(Phase("Lobby", "InGame"));
                Check(r.Sink.Actions.Count == before && r.Engine.Active == AlertStage.None,
                    "closed the popup, then the game starts: nothing new, it only changes an alert that is up");

                r = new Rig(true);
                r.Probe.Idle = 60;
                r.Feed(Found(), World(), Phase("MainMenu", "Loading"), Phase("Loading", "Lobby"), Phase("Lobby", "InGame"));
                Check(r.Sink.Joined() == "popup GAME HAS STARTED [Mid Wars] | sound | flash | push HoN: game has started",
                    "fell behind until the game was running: one full alert that says the game has started");

                r = new Rig(true);
                r.Feed(Found(), World());
                r.Feed(Phase("Loading", "Lobby"));
                r.Feed(Phase("Lobby", "MainMenu"));
                Check(r.Engine.Active == AlertStage.Over && r.Engine.Notice == NoticeReason.Cancelled && r.Sink.Count("hide") == 0
                    && r.Sink.Actions[r.Sink.Actions.Count - 1] == "popup MATCH CANCELLED [Mid Wars]",
                    "match cancelled while you were away: the popup stays and says so");
                int soundsBefore = r.Sink.Count("sound");
                for (int i = 0; i < 300; i++) r.Advance(1);
                Check(r.Engine.Active == AlertStage.Over && r.Sink.Count("sound") == soundsBefore && r.Sink.Count("push") == 0,
                    "that notice makes no more sound and sends nothing to the phone, however long it stays");
                r.Feed(Found());
                Check(r.Engine.Active == AlertStage.Found && r.Sink.Count("popup MATCH FOUND") == 3 && r.Sink.Count("sound") == soundsBefore + 1,
                    "a new match replaces the notice with a fresh alert");

                r = new Rig(false);
                r.Feed(Found());
                r.Feed(Phase("Loading", "Lobby"));
                r.Feed(Phase("Lobby", "InGame"));
                r.Feed(Phase("InGame", "MainMenu"));
                bool ended = r.Engine.Active == AlertStage.Over && r.Engine.Title == "MATCH ENDED" && r.Engine.Notice == NoticeReason.Ended;
                r.Probe.Foreground = true;
                r.Advance(0.25);
                Check(ended && r.Engine.Active == AlertStage.None && r.Sink.Count("hide") == 1,
                    "game played out without you: the popup says MATCH ENDED and leaves when the game gets focus");

                r = new Rig(false);
                r.S.PopupEnabled = false;
                r.Feed(Found());
                r.Feed(Phase("Lobby", "MainMenu"));
                Check(r.Engine.Active == AlertStage.None && r.Sink.Count("popup") == 0,
                    "with the popup switched off there is nothing to keep: the alert simply ends");

                r = new Rig(true);
                r.Feed(Found(), World(), Phase("MainMenu", "Loading"), Phase("Loading", "Lobby"), Phase("Lobby", "InGame"), Phase("InGame", "MainMenu"));
                Check(r.Sink.Actions.Count == 0, "a whole finished match read in one go (we fell behind): no stale alert");
                r.Feed(Found());
                Check(r.Sink.Count("popup") == 1, "the next real match still alerts");

                r = new Rig(false);
                r.Engine.Bootstrap(r.Tracker, new List<LogEvent>(new LogEvent[] { Found(), World(), Phase("MainMenu", "Loading"), Phase("Loading", "Lobby") }), r.Now);
                Check(r.Sink.Actions.Count == 0, "history already in the log at start-up is not announced as a new match");
                r.Feed(Phase("Lobby", "InGame"));
                Check(r.Sink.Actions.Count == 0, "started during hero pick: the game starting is not announced either (no alert is up)");
                r.Feed(Phase("InGame", "MainMenu"));
                r.Feed(Found());
                Check(r.Sink.Count("popup MATCH FOUND") == 1, "the next match is");

                r = new Rig(false);
                r.Engine.Bootstrap(r.Tracker, new List<LogEvent>(new LogEvent[] { Found(), Phase("MainMenu", "Loading"), Phase("Loading", "Lobby"), Phase("Lobby", "InGame") }), r.Now);
                r.Feed(Phase("InGame", "MainMenu"));
                Check(r.Sink.Actions.Count == 0, "started in the middle of a game: nothing is announced when it ends");

                r = new Rig(false);
                r.Feed(Found());
                r.Probe.Running = false;
                r.Advance(1);
                bool closedNotice = r.Engine.Active == AlertStage.Over && r.Engine.Title == "GAME WAS CLOSED" && r.Engine.Notice == NoticeReason.GameClosed;
                r.Advance(600);
                bool stillThere = r.Engine.Active == AlertStage.Over;
                r.Engine.Acknowledge("popup clicked");
                Check(closedNotice && stillThere && r.Sink.Count("hide") == 1 && r.Engine.Active == AlertStage.None,
                    "game closed while the alert is up: the popup says so and waits for a click");

                r = new Rig(false);
                r.Probe.Running = false;
                r.Feed(Found());
                r.Advance(30);
                Check(r.Engine.Active == AlertStage.Found, "game process not recognised at all: alert stays up rather than vanishing");

                r = new Rig(true);
                r.S.PhoneMode = "always";
                r.Probe.Foreground = true;
                r.Feed(Found());
                Check(r.Sink.Joined() == "push HoN: match found", "phone mode 'always': pushed even with the game focused");

                r = new Rig(false);
                r.Feed(Found());
                for (int i = 0; i < 24 * 60; i++) r.Advance(60);
                Check(r.Engine.Active == AlertStage.Found && r.Sink.Count("hide") == 0, "the popup never leaves by itself: still up after a day away");

                r = new Rig(false);
                r.Feed(Found());
                for (int i = 0; i < 400; i++) r.Advance(1);
                Check(r.Sink.Count("sound") == 11, "sound stops repeating after two minutes (1 + 10 repeats)");

                r = new Rig(true);
                r.Engine.StartTest(r.Now);
                r.Probe.Foreground = true;
                for (int i = 0; i < 3600; i++) r.Advance(1);
                bool testUp = r.Engine.Active == AlertStage.Found && r.Engine.IsTest;
                r.Engine.Acknowledge("popup clicked");
                Check(testUp && r.Engine.Active == AlertStage.None && r.Sink.Count("push") == 0 && r.Sink.Count("sound") == 1,
                    "test alert stays until it is clicked, whatever is focused: one sound, nothing to the phone");

                r = new Rig(false);
                r.Engine.StartTest(r.Now);
                r.Advance(2);
                r.Engine.StartTest(r.Now);
                Check(r.Sink.Count("popup") == 2 && r.Sink.Count("hide") == 1
                    && r.Sink.Joined().Contains("hide | popup MATCH FOUND [Mid Wars]") && r.Engine.IsTest,
                    "a second test alert replaces the first popup, so it appears afresh");

                r = new Rig(false);
                r.Feed(Found());
                for (int i = 0; i < 3600; i++) r.Advance(1);
                Check(r.Engine.Active == AlertStage.Found && r.Sink.Count("hide") == 0, "a real alert is still there an hour later if nobody reacts");
            }
            finally { AppLog.Echo = echo; }
        }

        static void Append(string path, string text, ref FileStream writer)
        {
            if (writer == null) writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            writer.Write(bytes, 0, bytes.Length);
            writer.Flush();
        }

        // Polls like the real loop does, with a pretend clock, until something is read or 12 pretend seconds pass.
        static string PollUntil(LogTailer t, ref DateTime now, List<LogEvent> events, out bool immediately)
        {
            immediately = false;
            for (int i = 0; i < 48; i++)
            {
                now = now.AddMilliseconds(250);
                PollResult r = t.Poll(now, events);
                if (r == PollResult.Live) { immediately = i == 0; return Describe(events); }
            }
            return "(nothing read)";
        }

        static void AppendBytes(string path, byte[] bytes, int offset, int count, ref FileStream writer)
        {
            if (writer == null) writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            writer.Write(bytes, offset, count);
            writer.Flush();
        }

        static byte[] Utf16(string text, bool mark)
        {
            byte[] body = Encoding.Unicode.GetBytes(text);
            if (!mark) return body;
            byte[] all = new byte[body.Length + 2];
            all[0] = 0xFF;
            all[1] = 0xFE;
            Array.Copy(body, 0, all, 2, body.Length);
            return all;
        }

        static void Utf16TailerTests()
        {
            Check(LogText.Detect(new byte[] { 0xFF, 0xFE, 0x2A, 0 }) == Encoding.Unicode
                && LogText.Detect(new byte[] { 0x2A, 0, 0x2A, 0 }) == Encoding.Unicode
                && LogText.Detect(Encoding.ASCII.GetBytes("****")).CodePage == 28591,
                "encoding is told from the first bytes: FF FE and zero high bytes mean UTF-16, plain bytes mean one char each");

            string dir = Path.Combine(Path.GetTempPath(), "HonQueueNotifier-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "console.log");
            FileStream writer = null;
            try
            {
                DateTime now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
                File.WriteAllBytes(path, Utf16("************\r\nEngine start up...\r\n[2026/10/11]\r\n" + Sample, true));
                LogTailer t = new LogTailer(path, true);
                List<LogEvent> events = new List<LogEvent>();
                PollResult first = t.Poll(now, events);
                Check(first == PollResult.Bootstrap && Describe(events) == "engine;" + SampleEvents,
                    "existing UTF-16 log with byte order mark: every event is found");

                // The game holds the file open and writes blocks that can end in the middle of a character.
                byte[] line = Utf16("Disconnect - CMatchmakingClient::HandleFoundMatch\r\nClient loading world \"midwars\"\r\n", false);
                int cut = 41;   // odd: splits one two-byte character between two writes
                AppendBytes(path, line, 0, cut, ref writer);
                bool quick;
                events.Clear();
                string got = PollUntil(t, ref now, events, out quick);
                Check(got == "", "half a UTF-16 line, ending mid-character: read, but held back");
                AppendBytes(path, line, cut, line.Length - cut, ref writer);
                events.Clear();
                got = PollUntil(t, ref now, events, out quick);
                Check(got == "found;world:midwars", "rest of the UTF-16 line: match found");

                // Game restart: truncated and rewritten with a new byte order mark.
                writer.Dispose();
                writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                byte[] fresh = Utf16("************\r\nEngine start up...\r\nDisconnect - CMatchmakingClient::HandleFoundMatch\r\n", true);
                AppendBytes(path, fresh, 0, fresh.Length, ref writer);
                events.Clear();
                got = PollUntil(t, ref now, events, out quick);
                Check(got == "reset;engine;found", "UTF-16 log truncated by a game restart: read from the top again");

                // A log that starts empty and gets its first bytes later: the encoding is decided only then.
                string path2 = Path.Combine(dir, "late.log");
                File.WriteAllBytes(path2, new byte[0]);
                LogTailer t2 = new LogTailer(path2, true);
                events.Clear();
                t2.Poll(now, events);
                FileStream w2 = null;
                byte[] late = Utf16("Engine start up...\r\nDisconnect - CMatchmakingClient::HandleFoundMatch\r\n", true);
                try
                {
                    AppendBytes(path2, late, 0, 1, ref w2);
                    events.Clear();
                    now = now.AddSeconds(2);
                    t2.Poll(now, events);
                    AppendBytes(path2, late, 1, late.Length - 1, ref w2);
                    events.Clear();
                    got = PollUntil(t2, ref now, events, out quick);
                }
                finally { if (w2 != null) w2.Dispose(); }
                Check(got == "engine;found", "empty log that is written later, one byte first: encoding still told correctly");
            }
            finally
            {
                if (writer != null) writer.Dispose();
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static void TailerTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "HonQueueNotifier-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "console.log");
            FileStream writer = null;
            try
            {
                DateTime now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
                string header = "************\r\nEngine start up...\r\n[2026/10/11]\r\n[12:06:48]\r\n[0.13.0.0]\r\n";
                File.WriteAllText(path, header + Sample, Encoding.ASCII);

                // The game keeps its log open for writing the whole time, like this writer does.
                Append(path, "Loading ^gTexture^* '/ui/x.tga'\r\n", ref writer);

                LogTailer t = new LogTailer(path, true);
                List<LogEvent> events = new List<LogEvent>();
                PollResult first = t.Poll(now, events);
                AlertEngine engine = new AlertEngine(new Settings(), new FakeProbe(), new RecordingSink());
                engine.Bootstrap(t.Tracker, events, now);
                Check(first == PollResult.Bootstrap && Describe(events) == "engine;" + SampleEvents && t.Tracker.Phase == GamePhase.Menu
                    && t.Tracker.HandledSeq == t.Tracker.MatchSeq, "existing log is read for state only (while the game holds it open)");

                events.Clear();
                now = now.AddMilliseconds(250);
                Check(t.Poll(now, events) == PollResult.Nothing, "nothing new: nothing read");

                Append(path, "Disconnect - CMatchmakingClient::Handle", ref writer);
                bool quick1;
                events.Clear();
                string got = PollUntil(t, ref now, events, out quick1);
                Check(got == "", "half a line appended: read, but held back");

                Append(path, "FoundMatch\r\nClient loading world \"midwars\"\r\n", ref writer);
                bool quick2;
                events.Clear();
                got = PollUntil(t, ref now, events, out quick2);
                Check(got == "found;world:midwars", "rest of the line appended: match found");
                Line("        new data noticed on the very next check: " + (quick1 && quick2 ? "yes" : "NO - only via the periodic re-open (slower, still works)"));
                Line("        size-change hits " + t.StatHits + ", periodic re-open hits " + t.ForcedOpens);

                // Game restarted: same file, truncated and rewritten from the start.
                writer.Dispose();
                writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                Append(path, "************\r\nEngine start up...\r\n[2026/10/11]\r\n[14:30:02]\r\n", ref writer);
                bool quick3;
                events.Clear();
                got = PollUntil(t, ref now, events, out quick3);
                Check(got == "reset;engine", "log truncated by a game restart: detected, read from the top");

                Append(path, "Disconnect - CMatchmakingClient::HandleFoundMatch\r\n", ref writer);
                events.Clear();
                got = PollUntil(t, ref now, events, out quick3);
                Check(got == "found", "and the next match in the new log is found");

                // Replaced by a longer file with a different first line between two checks.
                writer.Dispose();
                writer = null;
                File.WriteAllText(path, "************\r\nEngine start up...\r\n[2026/10/12]\r\n[09:00:00]\r\n" + new string('.', 4000) + "\r\n" + Sample, Encoding.ASCII);
                events.Clear();
                got = PollUntil(t, ref now, events, out quick3);
                Check(got == "reset;engine;" + SampleEvents, "log replaced by a longer one: detected by its first lines");

                // Deleted and created again.
                File.Delete(path);
                events.Clear();
                now = now.AddMilliseconds(250);
                bool quietWhileGone = t.Poll(now, events) == PollResult.Nothing && events.Count == 0;
                File.WriteAllText(path, "************\r\nEngine start up...\r\n[2026/10/13]\r\n[10:00:00]\r\nDisconnect - CMatchmakingClient::HandleFoundMatch\r\n", Encoding.ASCII);
                events.Clear();
                got = PollUntil(t, ref now, events, out quick3);
                Check(quietWhileGone && got == "reset;engine;found", "log deleted and recreated: picked up again");

                // A log that appears after start-up is all new.
                string path2 = Path.Combine(dir, "second.log");
                LogTailer t2 = new LogTailer(path2, false);
                events.Clear();
                bool quietBefore = t2.Poll(now, events) == PollResult.Nothing;
                File.WriteAllText(path2, "Engine start up...\r\nDisconnect - CMatchmakingClient::HandleFoundMatch\r\n", Encoding.ASCII);
                events.Clear();
                got = PollUntil(t2, ref now, events, out quick3);
                Check(quietBefore && got == "engine;found", "log that appears later is treated as live from its first byte");

                // The writer must never be blocked by us: reopen for writing right after a poll.
                bool writerOk = true;
                try
                {
                    for (int i = 0; i < 50; i++)
                    {
                        using (FileStream w = new FileStream(path2, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            byte[] b = Encoding.ASCII.GetBytes("Engine start up...\r\n");
                            w.Write(b, 0, b.Length);
                        }
                        events.Clear();
                        now = now.AddSeconds(2);
                        t2.Poll(now, events);
                    }
                }
                catch (IOException) { writerOk = false; }
                Check(writerOk, "the game can always reopen and truncate its log between our reads");
            }
            finally
            {
                if (writer != null) writer.Dispose();
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static void SettingsTests()
        {
            Settings d = new Settings();
            List<string> warnings = new List<string>();
            Settings back = Settings.Parse(d.ToIni(), warnings);
            Check(warnings.Count == 0 && back.ToIni() == d.ToIni(), "default settings file is read back unchanged, without warnings");

            warnings.Clear();
            Settings s = Settings.Parse("[General]\nMinimizeHotkey = Ctrl+Alt+M \n[Popup]\nPosition=BottomRight\nEnabled=no\n[Phone]\nProvider= NTFY \nAfterSeconds=99999\nIdleSeconds=0\nBogus=1\n[Sound]\nRepeatSeconds=abc\n", warnings);
            Check(s.MinimizeHotkey == "Ctrl+Alt+M" && s.PopupPosition == "BottomRight"
                && !s.PopupEnabled && s.PhoneEnabled && s.PhoneAfterSeconds == 3600 && s.PhoneIdleSeconds == 1 && s.SoundRepeatSeconds == 12,
                "values are trimmed, clamped to sane ranges, and bad numbers keep the default");
            Check(warnings.Count == 2, "an unknown key and a bad number are both reported");

            Check(!new Settings().ToIni().Contains("next to"), "the settings file template does not say the file is next to the exe (it is in %APPDATA%)");
        }

        static void PushTests()
        {
            string error;
            Settings s = new Settings();
            Check(Pusher.Build(s, "t", "m", out error) == null && error != null, "provider 'none' builds no request");
            s.PhoneProvider = "telegram";
            s.NtfyTopic = "t";
            Check(Pusher.Build(s, "t", "m", out error) == null && error.Contains("use ntfy"), "a provider other than ntfy builds no request");

            s = new Settings();
            s.PhoneProvider = "ntfy";
            Check(Pusher.Build(s, "t", "m", out error) == null && error.Contains("NtfyTopic"), "ntfy without a topic is refused");
            s.NtfyTopic = "my topic";
            s.NtfyServer = "https://ntfy.example.org/";
            PushRequest r = Pusher.Build(s, "HoN: match found", "body", out error);
            string headers = "";
            foreach (KeyValuePair<string, string> h in r.Headers) headers += h.Key + "=" + h.Value + ";";
            Check(r.Url == "https://ntfy.example.org/my%20topic" && r.Body == "body"
                && headers == "Title=HoN: match found;Priority=high;Tags=video_game;",
                "ntfy request posts the message to the topic with title and priority headers");
            s.NtfyServer = "";
            s.NtfyTopic = " hqn-abc ";
            r = Pusher.Build(s, "t", "Mid Wars. Hero pick is starting.", out error);
            Check(r.Url == "https://ntfy.sh/hqn-abc" && r.ContentType == "text/plain; charset=utf-8",
                "ntfy defaults: the public ntfy.sh server and a plain text body");
        }

        static T FindControl<T>(Control root, Func<T, bool> match) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                T t = c as T;
                if (t != null && match(t)) return t;
                T inner = FindControl<T>(c, match);
                if (inner != null) return inner;
            }
            return null;
        }

        static void FindAll<T>(Control root, Func<T, bool> match, List<T> found) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                T t = c as T;
                if (t != null && match(t)) found.Add(t);
                FindAll(c, match, found);
            }
        }

        // The darkest pixel of an image (its text), optionally only from column x0 on (past a check box's box).
        static Color Darkest(Bitmap b, int x0 = 0)
        {
            Color best = Color.White;
            for (int x = x0; x < b.Width; x++)
                for (int y = 0; y < b.Height; y++)
                {
                    Color c = b.GetPixel(x, y);
                    if (c.A > 0 && c.R + c.G + c.B < best.R + best.G + best.B) best = c;
                }
            return best;
        }

        // Where a control starts, counted from the left of the given container.
        static int X(Control c, Control container)
        {
            int x = 0;
            for (Control p = c; p != null && p != container; p = p.Parent) x += p.Left;
            return x;
        }

        static int Y(Control c, Control container)
        {
            int y = 0;
            for (Control p = c; p != null && p != container; p = p.Parent) y += p.Top;
            return y;
        }

        // What a user sees on a page: not the layout tables, the empty spacers or the hidden red problem
        // lines; a number box counts as one control.
        static List<Control> Seen(Control root)
        {
            List<Control> list = new List<Control>();
            foreach (Control c in root.Controls)
            {
                if (c is TableLayoutPanel) { list.AddRange(Seen(c)); continue; }
                Label l = c as Label;
                if (l != null && (l.Text.Length == 0 || l.ForeColor == Color.Firebrick)) continue;
                list.Add(c);
            }
            return list;
        }

        // The greyed-out controls on a page, in order: texts by their words, fields by their kind.
        static string Greyed(Control page)
        {
            List<string> names = new List<string>();
            foreach (Control c in Seen(page))
                if (!c.Enabled) names.Add(c is TextBox ? "box" : c is ComboBox ? "list" : c is NumericUpDown ? "number" : c.Text);
            return string.Join("|", names.ToArray());
        }

        static int WindowsBuild()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    int build;
                    return k != null && int.TryParse(k.GetValue("CurrentBuildNumber") as string, out build) ? build : 0;
                }
            }
            catch (Exception) { return 0; }
        }

        // The values in a settings file, keyed Section.Key, without its comments.
        static Dictionary<string, string> IniValues(string ini)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            string section = "";
            foreach (string raw in ini.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[') { section = line.Trim('[', ']'); continue; }
                int eq = line.IndexOf('=');
                if (eq > 0) values[section + "." + line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return values;
        }

        static bool HasProblem(Settings s, string key)
        {
            foreach (SettingProblem p in s.Validate()) if (p.Key == key) return true;
            return false;
        }

        static void SettingsFileTests()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HonQueueNotifier");
            string folder = AppFiles.Folder();
            Check(string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(appData).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(folder)
                && AppFiles.SettingsName == "HonQueueNotifier.ini" && AppFiles.LogName == "HonQueueNotifier.log",
                "settings and log always go to %APPDATA%\\HonQueueNotifier, never next to the exe");

            string dir = Path.Combine(Path.GetTempPath(), "hqn-selftest-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(Path.Combine(dir, "sub"), "settings.ini");
            try
            {
                Settings a = new Settings();
                a.PopupPosition = "BottomLeft";
                a.NtfyTopic = "hqn-x";
                a.Save(path);
                List<string> warnings = new List<string>();
                Settings b = Settings.Load(path, warnings);
                a.SoundRepeatSeconds = 3;
                a.Save(path);
                Settings c = Settings.Load(path, warnings);
                Check(b.PopupPosition == "BottomLeft" && b.NtfyTopic == "hqn-x" && c.SoundRepeatSeconds == 3 && warnings.Count == 0
                    && !File.Exists(path + ".tmp"),
                    "saving creates the folder, reads back the same, and saving again replaces the file");

                Settings d = a.Clone();
                d.NtfyTopic = "other";
                Check(a.NtfyTopic == "hqn-x" && d.ToIni() != a.ToIni() && a.Clone().ToIni() == a.ToIni(), "a copy of the settings is independent and complete");

                FileStamp stamp = new FileStamp(path);
                bool before = stamp.Changed();
                File.AppendAllText(path, "; edited by hand\r\n");
                bool after = stamp.Changed();
                bool again = stamp.Changed();
                Check(!before && after && !again, "a change made to the settings file by hand is noticed once");

                Settings s = new Settings();
                Check(s.Validate().Count == 0, "the defaults pass validation");
                s.LogPath = "relative\\console.log";
                s.PopupPosition = "Middle";
                Check(HasProblem(s, "General.LogPath") && HasProblem(s, "Popup.Position") && s.Validate().Count == 2,
                    "refused: a log path that is not a full path, an unknown position");
                s = new Settings();
                s.LogPath = Path.Combine(Path.Combine(dir, "missing"), "console.log");
                bool missingFolder = HasProblem(s, "General.LogPath");
                s.LogPath = "%TEMP%\\console.log";
                Check(missingFolder && !HasProblem(s, "General.LogPath"), "a chosen log file may not exist yet, but its folder must; %VARIABLES% are expanded");

                string wav = Path.Combine(dir, "a.wav");
                File.WriteAllBytes(wav, Chime.BuiltInWav());
                s = new Settings();
                s.SoundFile = Path.Combine(dir, "nothing.wav");
                bool missing = HasProblem(s, "Sound.File");
                s.SoundFile = path;
                bool notWav = HasProblem(s, "Sound.File");
                s.SoundFile = "\"" + wav + "\"";
                Check(missing && notWav && !HasProblem(s, "Sound.File"), "a sound file must exist and be a .wav");

                Settings off = Settings.Parse("[Sound]\nEnabled=false\n", null);
                Check(new Settings().SoundEnabled && new Settings().ToIni().Contains("\r\n[Sound]\r\nEnabled=true\r\n") && !off.SoundEnabled,
                    "sound is on by default, and a settings file that turns it off is honoured");

                s = new Settings();
                s.NtfyTopic = "";
                bool offIsFine = s.Validate().Count == 0;
                s.PhoneProvider = "ntfy";
                bool noTopic = HasProblem(s, "Phone.NtfyTopic");
                s.NtfyTopic = "my topic";
                bool spaceInTopic = HasProblem(s, "Phone.NtfyTopic");
                s.NtfyTopic = "hqn-ok_1";
                s.NtfyServer = "ftp://example.org";
                bool badServer = HasProblem(s, "Phone.NtfyServer");
                s.NtfyServer = "";
                s.NtfyPriority = "urgent";
                bool badPriority = HasProblem(s, "Phone.NtfyPriority");
                s.NtfyPriority = "4";
                Check(offIsFine && noTopic && spaceInTopic && badServer && badPriority && s.Validate().Count == 0,
                    "phone settings are checked only when phone push is on: topic, server and priority");

                // The message for a missing topic must not send people to a button the dialog does not have.
                Settings blank = new Settings();
                blank.PhoneProvider = "ntfy";
                string topicText = null;
                foreach (SettingProblem p in blank.Validate()) if (p.Key == "Phone.NtfyTopic") topicText = p.Text;
                Check(topicText != null && !topicText.Contains("Generate") && !topicText.Contains("press"),
                    "the message for a missing ntfy topic names no button");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static void DialogTests()
        {
            // Building the dialog lays it out once, not once per control added: each layout asks every
            // label for its size again, which took 20000 measurements and 1.5 s to open the dialog (about
            // 30 requests per label when done right).
            DialogLabel.Asked = 0;
            using (SettingsForm f = new SettingsForm(new Settings(), null, null, null))
            {
                int labels = 0;
                Stack<Control> todo = new Stack<Control>();
                todo.Push(f);
                while (todo.Count > 0)
                {
                    Control c = todo.Pop();
                    if (c is DialogLabel) labels++;
                    foreach (Control child in c.Controls) todo.Push(child);
                }
                Check(labels > 20 && DialogLabel.Asked <= 45 * labels,
                    "settings dialog: building it lays each label out a few dozen times at most, not hundreds (it opens at once)");
            }

            // A label measures its text again only when something its size depends on has changed.
            using (Form host = new Form())
            using (Font big = new Font(host.Font.FontFamily, host.Font.Size * 2))
            {
                DialogLabel l = new DialogLabel();
                l.Text = "Pick a name only you would use.";
                host.Controls.Add(l);
                Size first = l.GetPreferredSize(Size.Empty);
                DialogLabel.Measured = 0;
                for (int i = 0; i < 10; i++) l.GetPreferredSize(Size.Empty);
                bool cached = DialogLabel.Measured == 0;
                l.Text = "Pick a name only you would use. Anyone with the same topic gets your alerts.";
                Size longer = l.GetPreferredSize(Size.Empty);
                l.MaximumSize = new Size(first.Width, 0);
                Size wrapped = l.GetPreferredSize(Size.Empty);
                l.Font = big;
                Size bigger = l.GetPreferredSize(Size.Empty);
                l.Padding = new Padding(5);
                Size padded = l.GetPreferredSize(Size.Empty);
                Check(cached && DialogLabel.Measured == 3 && longer.Width > first.Width && wrapped.Width <= first.Width
                        && wrapped.Height > first.Height && bigger.Height > wrapped.Height
                        && padded.Width == bigger.Width + 10 && padded.Height == bigger.Height + 10,
                    "settings dialog: a label measures its text once, and again only when its text, width limit or font changes");
            }

            using (SettingsForm f = new SettingsForm(new Settings(), null, null, null))
            {
                bool exclusive = true;
                string[][] groups =
                {
                    new[] { "When I have not reacted to the popup", "On every match found, even when the game is focused" },
                    new[] { "Find it automatically (recommended)", "Use this file:" },
                    new[] { "Built-in sound", "This .wav file:" },
                };
                foreach (string[] group in groups)
                {
                    RadioButton a = FindControl<RadioButton>(f, delegate(RadioButton r) { return r.Text == group[0]; });
                    RadioButton b = FindControl<RadioButton>(f, delegate(RadioButton r) { return r.Text == group[1]; });
                    if (a == null || b == null) { exclusive = false; continue; }
                    a.Checked = true; b.Checked = true;
                    bool first = !a.Checked && b.Checked;
                    a.Checked = true;
                    bool second = a.Checked && !b.Checked;
                    if (!first || !second) exclusive = false;
                }
                Check(exclusive, "settings dialog: in each group of options only one can be chosen (when to send, game log, sound)");
            }

            using (SettingsForm f = new SettingsForm(new Settings(), null, null, null, "C:\\Games\\HoN Queue Notifier\\HonQueueNotifier.ini", "C:\\Games\\HoN Queue Notifier\\HonQueueNotifier.log"))
            {
                TextBox folder = FindControl<TextBox>(f, delegate(TextBox t) { return t.ReadOnly && t.Text == "C:\\Games\\HoN Queue Notifier"; });
                Button openFolder = FindControl<Button>(f, delegate(Button b) { return b.Text == "Open folder"; });
                Button openLog = FindControl<Button>(f, delegate(Button b) { return b.Text == "Open log"; });
                Check(folder != null && openFolder != null && openLog != null && openLog.Enabled,
                    "settings dialog: shows the folder with the settings and the log, with buttons to open it and the log");
                Check(FindControl<Label>(f, delegate(Label l) { return l.Text == "Version:"; }) != null
                    && FindControl<Label>(f, delegate(Label l) { return l.Text == Program.Version; }) != null,
                    "settings dialog: the General tab shows the program's version");
                DialogLabel source = FindControl<DialogLabel>(f, delegate(DialogLabel l) { return l.LinkUrl != null && l.LinkUrl.Contains("github.com"); });
                Check(FindControl<Label>(f, delegate(Label l) { return l.Text == "Source code:"; }) != null
                    && source != null && source.LinkUrl == Program.RepoUrl && Program.RepoUrl.EndsWith("/" + source.Text, StringComparison.Ordinal),
                    "settings dialog: the General tab links to the program's repository, showing its address");

                TabControl tabs = FindControl<TabControl>(f, delegate(TabControl c) { return true; });
                bool white = tabs.TabPages.Count == 4;
                foreach (TabPage p in tabs.TabPages) if (p.UseVisualStyleBackColor || p.BackColor != SystemColors.Window) white = false;
                Check(white, "settings dialog: the pages are white, so each stands out from the dialog around it");

                List<Label> notes = new List<Label>();
                FindAll(f, delegate(Label l) { return l.ForeColor == SystemColors.GrayText; }, notes);
                bool oneLine = notes.Count >= 2;
                foreach (Label l in notes)
                    if (l.PreferredSize.Height >= TextRenderer.MeasureText("Ag", l.Font).Height * 3 / 2) oneLine = false;
                Check(oneLine, "settings dialog: every note is one line");

                // Headings, labels, check boxes and radio buttons start on one line. The dialog's labels draw
                // their text right at their left edge, the line with the ntfy.sh link too.
                Control general = tabs.TabPages[0].Controls[0], phone = tabs.TabPages[3].Controls[0];
                general.PerformLayout();
                phone.PerformLayout();
                int edge = X(FindControl<CheckBox>(general, delegate(CheckBox c) { return c.Text == "Start with Windows"; }), general);
                int phoneEdge = X(FindControl<RadioButton>(phone, delegate(RadioButton r) { return r.Text.StartsWith("When I have not reacted"); }), phone);
                Check(X(FindControl<Label>(general, delegate(Label l) { return l.Text == "Game log"; }), general) == edge
                    && X(FindControl<RadioButton>(general, delegate(RadioButton r) { return r.Text.StartsWith("Find it"); }), general) == edge
                    && X(FindControl<Label>(general, delegate(Label l) { return l.Text == "Folder:"; }), general) == edge
                    && X(FindControl<Label>(phone, delegate(Label l) { return l.Text == "When to send"; }), phone) == phoneEdge
                    && X(FindControl<Label>(phone, delegate(Label l) { return l.Text == "Topic:"; }), phone) == phoneEdge
                    && X(FindControl<Label>(phone, delegate(Label l) { return l.Text.StartsWith("1. Install"); }), phone) == phoneEdge
                    && phoneEdge == edge && FindControl<Label>(general, delegate(Label l) { return l.Text == "Game log"; }) is DialogLabel,
                    "settings dialog: headings, labels, check boxes and radio buttons start on one line");

                // Step 3's button starts where the words of steps 1 and 2 do, not after a gap (it was 7 px).
                // Where the words start is read from the drawn line: its first ink after the last ink of "1."
                // drawn alone. A label drawn on its own may land a pixel off from the dialog, hence the 1 px.
                Label install = FindControl<Label>(phone, delegate(Label l) { return l.Text.StartsWith("1. Install"); });
                Button testPush = FindControl<Button>(phone, delegate(Button b) { return b.Text.StartsWith("Send a test"); });
                Func<string, bool[]> inkColumns = delegate(string text)
                {
                    using (DialogLabel l = new DialogLabel())
                    {
                        l.Text = text;
                        l.Font = install.Font;
                        l.AutoSize = true;
                        l.Size = l.PreferredSize;
                        using (Bitmap b = new Bitmap(l.Width, l.Height, PixelFormat.Format24bppRgb))
                        {
                            using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.White);
                            l.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                            bool[] ink = new bool[b.Width];
                            for (int x = 0; x < b.Width; x++)
                                for (int y = 0; y < b.Height; y++)
                                    if (b.GetPixel(x, y).R < 200) ink[x] = true;
                            return ink;
                        }
                    }
                };
                bool[] number = inkColumns("1."), line = inkColumns(install.Text);
                int words = -1;
                for (int x = Array.LastIndexOf(number, true) + 1; x < line.Length && words < 0; x++)
                    if (line[x]) words = x;
                Check(words > 0 && Math.Abs(X(testPush, phone) - X(install, phone) - words) <= 1,
                    "settings dialog: the test notification button starts where the words of the setup steps above it do");

                // The numbers that belong to one option stand one per line, their boxes in one column, never
                // inside a sentence that wraps.
                Control sound = tabs.TabPages[2].Controls[0];
                sound.PerformLayout();
                List<NumericUpDown> soundBoxes = new List<NumericUpDown>(), phoneBoxes = new List<NumericUpDown>();
                FindAll(sound, delegate(NumericUpDown n) { return true; }, soundBoxes);
                FindAll(phone, delegate(NumericUpDown n) { return true; }, phoneBoxes);
                Check(soundBoxes.Count == 2 && phoneBoxes.Count == 2
                    && X(soundBoxes[0], sound) == X(soundBoxes[1], sound) && Y(soundBoxes[0], sound) < Y(soundBoxes[1], sound)
                    && X(phoneBoxes[0], phone) == X(phoneBoxes[1], phone) && Y(phoneBoxes[0], phone) < Y(phoneBoxes[1], phone),
                    "settings dialog: an option's numbers stand one per line, their boxes in one column (sound repeats, when to send)");

                // Laid out as on screen, at this PC's display scale. The tabs only know their page area once
                // they exist as a window (an invisible one here).
                f.PerformAutoScale();
                bool created = f.Handle != IntPtr.Zero;
                Rectangle area = tabs.DisplayRectangle;
                bool fits = created && area.Height > 0;
                foreach (TabPage p in tabs.TabPages)
                    if (p.Controls[0].GetPreferredSize(new Size(area.Width, 0)).Height > area.Height) fits = false;
                Check(fits, "settings dialog: every page fits without a scroll bar");
            }

            // One rule on every tab: with an option off, everything that belongs to it is greyed out, its texts
            // included, and nothing else; with it on, all of it is back. A part that belongs to a choice
            // inside the option (a file, or "when I have not reacted") follows that choice too.
            using (SettingsForm f = new SettingsForm(new Settings(), null, null, null, "C:\\Games\\HoN Queue Notifier\\HonQueueNotifier.ini", "C:\\Games\\HoN Queue Notifier\\HonQueueNotifier.log"))
            {
                TabControl tabs = FindControl<TabControl>(f, delegate(TabControl c) { return true; });
                TabPage general = tabs.TabPages[0], popupPage = tabs.TabPages[1], soundPage = tabs.TabPages[2], phonePage = tabs.TabPages[3];
                CheckBox minimize = FindControl<CheckBox>(f, delegate(CheckBox c) { return c.Text == "Minimize the game with"; });
                CheckBox popup = FindControl<CheckBox>(f, delegate(CheckBox c) { return c.Text.StartsWith("Show a popup"); });
                CheckBox sound = FindControl<CheckBox>(f, delegate(CheckBox c) { return c.Text.StartsWith("Play a sound"); });
                CheckBox phone = FindControl<CheckBox>(f, delegate(CheckBox c) { return c.Text.StartsWith("Send match alerts"); });
                const string logFile = "box|Browse...";   // the chosen-file box and its button: off while the log is found automatically
                minimize.Checked = popup.Checked = sound.Checked = phone.Checked = false;
                bool off = Greyed(general) == logFile + "|box|Click the box and press new keys. Only active while the game has focus."
                    && Greyed(popupPage) == "Position:|list|Show it on:|list"
                    && Greyed(soundPage).Split('|').Length == Seen(soundPage).Count - 1 && sound.Enabled
                    && Greyed(phonePage).Split('|').Length == Seen(phonePage).Count - 1 && phone.Enabled;
                minimize.Checked = popup.Checked = sound.Checked = phone.Checked = true;
                bool on = Greyed(general) == logFile && Greyed(popupPage) == "" && Greyed(soundPage) == "box|Browse..." && Greyed(phonePage) == "";
                FindControl<RadioButton>(phonePage, delegate(RadioButton r) { return r.Text.StartsWith("On every match"); }).Checked = true;
                bool always = Greyed(phonePage) == "number|number";   // the unpicked choice keeps its words, only its fields grey out
                Check(off && on && always, "settings dialog: an option that is off greys out everything that belongs to it, texts included, on every tab");

                phone.Checked = false;
                sound.Checked = false;
                FindControl<RadioButton>(soundPage, delegate(RadioButton r) { return r.Text == "This .wav file:"; }).Checked = true;
                bool quiet = f.Problems(f.Collect()).Count == 0;
                sound.Checked = true;
                Check(quiet && f.Problems(f.Collect()).Count == 1,
                    "settings dialog: a greyed-out part is not checked for problems (sound off, a .wav file chosen but not given)");
            }

            // A greyed-out label is close to a greyed-out check box's text (Windows draws that a little
            // darker, which was judged fine); a plain label would be lighter still.
            using (Label label = new DialogLabel())
            using (CheckBox box = new CheckBox())
            using (Bitmap a = new Bitmap(160, 24))
            using (Bitmap c = new Bitmap(160, 24))
            {
                label.Text = box.Text = "Repeat every";
                label.AutoSize = box.AutoSize = true;
                label.BackColor = box.BackColor = SystemColors.Window;
                label.Enabled = box.Enabled = false;
                label.DrawToBitmap(a, new Rectangle(0, 0, label.PreferredSize.Width, label.PreferredSize.Height));
                box.DrawToBitmap(c, new Rectangle(0, 0, box.PreferredSize.Width, box.PreferredSize.Height));
                Color la = Darkest(a), cb = Darkest(c, 16);
                Check(la.R < 0x90 && Math.Abs(la.R - cb.R) <= 24 && Math.Abs(la.G - cb.G) <= 24 && Math.Abs(la.B - cb.B) <= 24,
                    "settings dialog: a greyed-out label is close to the grey of a greyed-out check box's text");
            }

            // A line with a link in it (ntfy.sh) is drawn like every other label: the same words, weight and
            // spacing outside the link, the link in Windows' link colour, and greyed out the same as a label
            // without a link. A LinkLabel drew the whole line differently.
            using (Font font = new Font("Segoe UI", 9f))
            {
                const string text = "1. Install the free ntfy app from the App Store or Google Play (ntfy.sh).";
                Func<bool, bool, Bitmap> draw = delegate(bool link, bool enabled)
                {
                    using (DialogLabel l = new DialogLabel())
                    {
                        l.Text = text;
                        l.Font = font;
                        l.AutoSize = true;
                        l.BackColor = Color.White;
                        l.Enabled = enabled;
                        if (link) l.SetLink("ntfy.sh", "https://ntfy.sh");
                        l.Size = l.PreferredSize;
                        Bitmap b = new Bitmap(l.Width, l.Height, PixelFormat.Format24bppRgb);
                        l.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                        if (link && enabled) _lastLink = l.LinkBounds;
                        return b;
                    }
                };
                using (Bitmap noLink = draw(false, true))
                using (Bitmap linked = draw(true, true))
                using (Bitmap noLinkOff = draw(false, false))
                using (Bitmap linkedOff = draw(true, false))
                {
                    Rectangle link = _lastLink;
                    int differ = 0, blue = 0, differOff = 0;
                    for (int y = 0; y < noLink.Height; y++)
                        for (int x = 0; x < noLink.Width; x++)
                        {
                            Color a = noLink.GetPixel(x, y), b = linked.GetPixel(x, y);
                            if (link.Contains(x, y)) { if (b.B > b.R + 60) blue++; }
                            else if (a != b) differ++;
                            if (noLinkOff.GetPixel(x, y) != linkedOff.GetPixel(x, y)) differOff++;
                        }
                    Check(link.Width > 0 && blue > 20 && differ == 0 && differOff == 0,
                        "settings dialog: a line with a link draws its other words exactly like a plain label, the link in the link colour, and greys out like a plain label");
                }
            }

            // The selected tab has the page's white (Windows 11 fills it with a pale grey of its own).
            using (SettingsForm f = new SettingsForm(new Settings(), null, null, null))
            {
                TabControl tabs = FindControl<TabControl>(f, delegate(TabControl t) { return true; });
                if (Application.RenderWithVisualStyles && !SystemInformation.HighContrast)
                    using (Bitmap b = new Bitmap(tabs.Width, tabs.Height))
                    {
                        tabs.DrawToBitmap(b, new Rectangle(0, 0, tabs.Width, tabs.Height));
                        Rectangle r = tabs.GetTabRect(tabs.SelectedIndex);
                        Color inside = b.GetPixel(r.Left + 3, r.Top + 4), page = tabs.SelectedTab.BackColor;
                        Check(inside.R == page.R && inside.G == page.G && inside.B == page.B,
                            "settings dialog: the selected tab has the page's colour, so tab and page are one sheet");
                    }
                else Line("  skip  the selected tab has the page's colour (only with Windows' visual styles)");
            }

            // Windows 11 draws a classic dialog's title bar a shade lighter than the dialog, which shows as a
            // seam; the dialog gives it its own colour, unless title bars are to show the accent colour.
            using (SettingsForm f = new SettingsForm(new Settings(), null, null, null))
            {
                bool created = f.Handle != IntPtr.Zero;
                if (WindowsBuild() >= 22000 && !SettingsForm.AccentTitleBars())
                    Check(created && f.CaptionColored, "settings dialog: its title bar has the dialog's own colour (Windows 11)");
                else Line("  skip  the title bar has the dialog's own colour (only on Windows 11 without accent-coloured title bars)");
            }

            // Every setting in the file is in the dialog. Loaded into a dialog that started from the defaults,
            // a file with every value changed must come back whole: a setting the dialog does not show would
            // come back as its default. A setting added to the file but not here fails the first half.
            Settings s = new Settings();
            s.LogPath = "C:\\Games\\console.log";
            s.MinimizeHotkeyEnabled = true;
            s.MinimizeHotkey = "Ctrl+Alt+M";
            s.PopupEnabled = false;
            s.PopupPosition = "BottomRight";
            s.PopupScreens = "Cursor";
            s.FlashTaskbar = false;
            s.SoundEnabled = false;
            s.SoundFile = "C:\\Sounds\\x.wav";
            s.SoundRepeatSeconds = 5;
            s.SoundRepeatForSeconds = 50;
            s.PhoneProvider = "ntfy";
            s.PhoneMode = "always";
            s.PhoneAfterSeconds = 20;
            s.PhoneIdleSeconds = 90;
            s.NtfyServer = "https://ntfy.example.org";
            s.NtfyTopic = "hqn-abc";
            s.NtfyPriority = "max";
            Dictionary<string, string> mine = IniValues(s.ToIni()), plain = IniValues(new Settings().ToIni());
            bool allChanged = mine.Count == plain.Count;
            foreach (KeyValuePair<string, string> kv in plain)
            {
                string v;
                if (!mine.TryGetValue(kv.Key, out v) || v == kv.Value) allChanged = false;
            }
            using (SettingsForm f = new SettingsForm(new Settings(), null, null, null))
            {
                f.LoadFrom(s);
                Check(allChanged && f.Collect().ToIni() == s.ToIni(),
                    "settings dialog: every setting in the file is in the dialog (a file with every value changed comes back whole)");

                f.LoadFrom(s);
                bool fresh = !f.HasChanges() && !f.ApplyEnabled;
                CheckBox sound = FindControl<CheckBox>(f, delegate(CheckBox c) { return c.Text.StartsWith("Play a sound"); });
                sound.Checked = !sound.Checked;
                bool changed = f.HasChanges() && f.ApplyEnabled;
                sound.Checked = !sound.Checked;
                bool back = !f.HasChanges() && !f.ApplyEnabled;
                NumericUpDown repeat = FindControl<NumericUpDown>(f, delegate(NumericUpDown n) { return n.Value == 5; });
                repeat.Text = "7";
                bool typed = f.ApplyEnabled;
                repeat.Value = 5;
                Check(fresh && changed && back && typed && !f.ApplyEnabled,
                    "settings dialog: Apply is off until something is changed, and off again when it is changed back");

                // Windows takes typed digits as the value only when the box is left. Typing goes into the box's
                // inner edit field; setting NumericUpDown.Text from code would commit the value at once.
                TextBox typing = FindControl<TextBox>(repeat, delegate(TextBox t) { return true; });
                typing.Text = "99999";
                Check(f.HasChanges() && repeat.Text == "99999", "settings dialog: a number being typed turns Apply on without being cut short");
                repeat.Value = 5;

                Settings odd = s.Clone();
                odd.NtfyPriority = "4";
                odd.PopupPosition = "bottomright";
                f.LoadFrom(odd);
                Check(!f.ApplyEnabled, "settings dialog: a hand-written value shown in the dialog's own words does not count as a change");
            }
        }

        static void IconTests()
        {
            // The phoenix glyph, in one colour, filling its square, with its cuts showing at tray sizes: across
            // the middle it is three parts, left wing, neck and right wing (a plain silhouette would be one).
            bool glyph = true;
            foreach (int size in new[] { 16, 20, 24, 32 })
                foreach (Color colour in new[] { Color.White, Color.Black })
                    using (Bitmap b = AppIcon.Draw(size, colour))
                    {
                        int ink = 0, left = size, right = -1, top = size, bottom = -1;
                        for (int x = 0; x < size; x++)
                            for (int y = 0; y < size; y++)
                            {
                                Color c = b.GetPixel(x, y);
                                if (c.A > 0 && (c.R != colour.R || c.G != colour.G || c.B != colour.B)) glyph = false;
                                if (c.A < 128) continue;
                                ink++;
                                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                            }
                        int parts = 0;
                        bool inside = false;
                        for (int x = 0; x < size; x++)
                        {
                            bool on = b.GetPixel(x, size / 2).A >= 128;
                            if (on && !inside) parts++;
                            inside = on;
                        }
                        double share = ink / (double)(size * size);
                        if (share < 0.25 || share > 0.5 || right - left < size - 4 || bottom - top < size - 4 || parts != 3) glyph = false;
                    }
            Check(glyph, "tray icon: the phoenix in one colour, filling its square, its wings and neck apart at 16 to 32 px");

            Check(AppIcon.IsLight(1) && !AppIcon.IsLight(0) && !AppIcon.IsLight(null) && !AppIcon.IsLight("1"),
                "tray icon: black only when Windows says the taskbar is light; white when it is dark or Windows does not say");

            using (Icon window = AppIcon.Window())
            using (Icon small = new Icon(window, 16, 16))
            using (Icon large = new Icon(window, 32, 32))
            using (Bitmap b = large.ToBitmap())
            {
                bool orange = false, other = false;
                for (int x = 0; x < b.Width; x++)
                    for (int y = 0; y < b.Height; y++)
                    {
                        Color c = b.GetPixel(x, y);
                        if (c.A == 0) continue;
                        if (c.R == AppIcon.Orange.R && c.G == AppIcon.Orange.G && c.B == AppIcon.Orange.B) orange = true;
                        else other = true;
                    }
                Check(small.Width == 16 && large.Width == 32 && orange && !other,
                    "window icon: the same drawing in orange, with its own image for each size Windows asks for");
            }

            // The exe file's icon (assets/app-icon.ico, put in by the build) is the same drawing: it is
            // there, and it was remade after the last change to the glyph (tools/assets/make_exe_icon.ps1).
            if (Native.IsWindows)
            {
                bool same = false;
                using (Icon file = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                using (Bitmap got = file.ToBitmap())
                using (Bitmap want = AppIcon.Draw(got.Width, AppIcon.Orange))
                {
                    same = got.Height == got.Width;
                    for (int x = 0; x < got.Width && same; x++)
                        for (int y = 0; y < got.Height && same; y++)
                        {
                            Color g = got.GetPixel(x, y), w = want.GetPixel(x, y);
                            if (Math.Abs(g.A - w.A) > 2) same = false;
                            else if (g.A >= 64 && (Math.Abs(g.R - w.R) > 8 || Math.Abs(g.G - w.G) > 8 || Math.Abs(g.B - w.B) > 8)) same = false;
                        }
                }
                Check(same, "exe file icon: the phoenix in orange, the same drawing as the window icon");
            }
            else Line("  skip  exe file icon matches the drawing (Windows only)");
        }

        static Bitmap RenderPopup(ForgePainter p, double ms)
        {
            Bitmap b = new Bitmap(p.Size.Width, p.Size.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b)) p.Draw(g, ms, TimeSpan.FromSeconds(151));
            return b;
        }

        static bool Near(Color c, Color want)
        {
            return c.A == 255 && Math.Abs(c.R - want.R) <= 12 && Math.Abs(c.G - want.G) <= 12 && Math.Abs(c.B - want.B) <= 12;
        }

        // How many pixels deep the border is at the middle of one side (0 top, 1 right, 2 bottom, 3 left).
        static int BorderDepth(Bitmap b, Rectangle frame, int side, Color edge)
        {
            int n = 0;
            for (int k = 0; k < 20; k++)
            {
                int x = frame.X + frame.Width / 2, y = frame.Y + frame.Height / 2;
                if (side == 0) y = frame.Y + k;
                else if (side == 1) x = frame.Right - 1 - k;
                else if (side == 2) y = frame.Bottom - 1 - k;
                else x = frame.X + k;
                if (!Near(b.GetPixel(x, y), edge)) break;
                n++;
            }
            return n;
        }

        static void PopupTests()
        {
            string[] found = ForgePainter.Lines(AlertStage.Found, false, "Mid Wars", TimeSpan.FromSeconds(7));
            string[] started = ForgePainter.Lines(AlertStage.Started, false, "Mid Wars", TimeSpan.FromSeconds(12));
            string[] over = ForgePainter.Lines(AlertStage.Over, false, "Mid Wars", TimeSpan.FromSeconds(151));
            string[] closed = ForgePainter.Lines(AlertStage.Over, false, null, TimeSpan.FromSeconds(5));
            string[] test = ForgePainter.Lines(AlertStage.Found, true, null, TimeSpan.FromSeconds(3));
            Check(string.Join("|", found) == "Mid Wars|  \u00B7  click to open the game|WAITING|0:07"
                && string.Join("|", started) == "Mid Wars|  \u00B7  click to open the game|RUNNING|0:12"
                && over[0] == "Mid Wars" && over[1] == "  \u00B7  2:31 ago  \u00B7  click to dismiss" && over[2] == null && over[3] == null
                && closed[0] == "" && closed[1] == "0:05 ago  \u00B7  click to dismiss"
                && test[1].StartsWith("This is a test alert", StringComparison.Ordinal) && test[2] == "WAITING" && test[3] == "0:03",
                "popup texts: map, hint and clock for each state; a notice tells how long ago");

            // Overlapping contours would make the two fill rules differ (GDI+ fills overlaps as holes under Alternate).
            bool noOverlaps = true;
            using (Font f = ForgeFont.Create(40))
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                // One character at a time: neighbouring letters may touch, which is why text paths use winding fill.
                foreach (char ch in "0123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZ")
                {
                    Bitmap[] fills = new Bitmap[2];
                    for (int k = 0; k < 2; k++)
                    {
                        fills[k] = new Bitmap(60, 60, PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(fills[k]))
                        using (GraphicsPath p = new GraphicsPath(k == 0 ? FillMode.Alternate : FillMode.Winding))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            p.AddString(ch.ToString(), f.FontFamily, (int)f.Style, f.Size, new PointF(4, 4), sf);
                            g.FillPath(Brushes.White, p);
                        }
                    }
                    for (int y = 0; y < 60 && noOverlaps; y++)
                        for (int x = 0; x < 60; x++)
                            if (fills[0].GetPixel(x, y) != fills[1].GetPixel(x, y)) { noOverlaps = false; break; }
                    fills[0].Dispose();
                    fills[1].Dispose();
                }
            }
            Check(noOverlaps, "popup font: digits and capitals have no overlapping outlines (4 and 8 draw without holes)");

            FontFamily cinzel = ForgeFont.Family;
            Check(cinzel != null && cinzel.Name == "Cinzel" && cinzel.IsStyleAvailable(FontStyle.Bold), "popup title font: the embedded Cinzel Bold loads");

            double e = ForgePainter.EntryMs;
            Check(ForgePainter.EntryOpacity(0) == 0 && ForgePainter.EntryOpacity(e) == 1 && ForgePainter.EntryOffset(0) == -16
                && Math.Abs(ForgePainter.EntryOffset(e)) < 1e-9 && Math.Abs(ForgePainter.EntryBrightness(0) - 2.2) < 1e-9
                && Math.Abs(ForgePainter.EntryBrightness(e) - 1) < 1e-9 && ForgePainter.Moving(e - 1) && !ForgePainter.Moving(e),
                "popup motion: drops and fades in over 0.52 s with a flash");

            // The fire: every ember stays in 0..1, moves smoothly (no visible jump from one frame to the next),
            // never repeats like a wave would, and the embers flicker independently of each other.
            bool bounded = true, smoothFire = true;
            double[] a0 = new double[600], a1 = new double[600];
            for (int k = 0; k < 600; k++)
            {
                double t = 20000 + k * 33;
                a0[k] = ForgePainter.Flame(0, t, false);
                a1[k] = ForgePainter.Flame(1, t, false);
                for (int ember = 0; ember < ForgePainter.Embers; ember++)
                    foreach (bool hot in new[] { false, true })
                    {
                        double v = ForgePainter.Flame(ember, t, hot), next = ForgePainter.Flame(ember, t + 16, hot);
                        if (v < 0 || v > 1) bounded = false;
                        if (Math.Abs(next - v) > 0.08) smoothFire = false;
                    }
            }
            Func<double[], double[], int, double> corr = (x, y, lag) =>
            {
                int n = x.Length - lag;
                double mx = 0, my = 0;
                for (int k = 0; k < n; k++) { mx += x[k]; my += y[k + lag]; }
                mx /= n; my /= n;
                double sxy = 0, sxx = 0, syy = 0;
                for (int k = 0; k < n; k++) { sxy += (x[k] - mx) * (y[k + lag] - my); sxx += (x[k] - mx) * (x[k] - mx); syy += (y[k + lag] - my) * (y[k + lag] - my); }
                return sxy / Math.Sqrt(sxx * syy);
            };
            double repeat = 0;
            for (int lag = 30; lag < 200; lag++) repeat = Math.Max(repeat, corr(a0, a0, lag));   // 1 to 6.6 s later
            Check(bounded && smoothFire, "popup fire: every ember's flicker stays in range and moves smoothly from frame to frame");
            Check(repeat < 0.6 && Math.Abs(corr(a0, a1, 0)) < 0.5,
                "popup fire: the flicker is irregular (no repeating rhythm) and each ember flickers on its own");

            // No shared beat: the moments where flames turn (stop rising and start falling, or the other way)
            // must not gather at the same times. Count how many turns fall within 30 ms of a whole multiple of
            // the quick flicker's average step; spread at random that is about 10 %, all on one beat near 100 %.
            int turns = 0, onBeat = 0;
            for (int ember = 0; ember < ForgePainter.Embers; ember++)
            {
                double prev = ForgePainter.Flame(ember, 20000, false), prevSlope = 0;
                for (double t = 20005; t < 40000; t += 5)
                {
                    double v = ForgePainter.Flame(ember, t, false), slope = v - prev;
                    if (prevSlope != 0 && slope != 0 && Math.Sign(slope) != Math.Sign(prevSlope))
                    {
                        turns++;
                        double off = t % ForgePainter.FlickerMs;
                        if (off < 30 || off > ForgePainter.FlickerMs - 30) onBeat++;
                    }
                    if (slope != 0) prevSlope = slope;
                    prev = v;
                }
            }
            Check(turns > 200 && onBeat < turns * 0.25,
                "popup fire: the flames have no shared beat; they rise and fall at their own moments (" + onBeat + " of " + turns + " turns on the flicker's beat)");

            string[] titles = { "MATCH FOUND", "GAME HAS STARTED", "MATCH CANCELLED", "MATCH ENDED", "GAME WAS CLOSED", "MATCH FOUND" };
            AlertStage[] stages = { AlertStage.Found, AlertStage.Started, AlertStage.Over, AlertStage.Over, AlertStage.Over, AlertStage.Found };
            NoticeReason[] notices = { NoticeReason.None, NoticeReason.None, NoticeReason.Cancelled, NoticeReason.Ended, NoticeReason.GameClosed, NoticeReason.None };
            Color[] edges = { Color.FromArgb(140, 106, 60), Color.FromArgb(176, 74, 42), Color.FromArgb(95, 91, 85), Color.FromArgb(95, 91, 85), Color.FromArgb(95, 91, 85), Color.FromArgb(140, 106, 60) };
            bool borders = true, fits = true, clear = true, corners = true;
            string where = "";
            foreach (float scale in new float[] { 1f, 1.5f })
            {
                int bw = Math.Max(2, (int)Math.Round(2 * scale));
                for (int i = 0; i < titles.Length; i++)
                {
                    using (ForgePainter p = new ForgePainter(scale, (int)Math.Round(580 * scale)))
                    {
                        p.Title = titles[i];
                        p.Stage = stages[i];
                        p.Notice = notices[i];
                        p.Test = i == 5;
                        p.Map = i == 4 || i == 5 ? null : "Forests of Caldavar";
                        using (Bitmap b = RenderPopup(p, 2000))
                        {
                            for (int side = 0; side < 4; side++)
                                if (BorderDepth(b, p.Frame, side, edges[i]) != bw) { borders = false; where = titles[i] + " x" + scale + " side " + side; }
                            RectangleF inner = p.Frame;
                            inner.Inflate(-bw, -bw);
                            bool inside = inner.Contains(p.TitleBounds) && inner.Contains(p.SubBounds);
                            bool apart = p.ClockBounds.IsEmpty || (p.TitleBounds.Right < p.ClockBounds.Left && p.SubBounds.Right < p.ClockBounds.Left);
                            if (!inside || !apart) { fits = false; where = titles[i] + " x" + scale; }
                            if (b.GetPixel(0, 0).A != 0 || b.GetPixel(b.Width - 1, b.Height - 1).A != 0) clear = false;
                            if (b.GetPixel(p.Frame.Left, p.Frame.Top).A != 255 || b.GetPixel(p.Frame.Right, p.Frame.Bottom).A != 255) corners = false;
                        }
                    }
                }
            }
            Check(borders, "popup: the border is equally wide on all four sides, in every state, at 100 % and 150 %" + (borders ? "" : " (" + where + ")"));
            Check(fits, "popup: title, map line and clock stay inside the frame and apart, in every state, at 100 % and 150 %" + (fits ? "" : " (" + where + ")"));
            Check(clear && corners, "popup: transparent around the glow, with a solid diamond on each corner");

            using (Bitmap b = new Bitmap(400, 40, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            using (Font f = new Font("Segoe UI", 15f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                float click = g.MeasureString("click", f, PointF.Empty, sf).Width, to = g.MeasureString("to", f, PointF.Empty, sf).Width;
                float end = ForgePainter.DrawWords(g, "click to", f, Brushes.White, 0, 0, sf);
                Check(Math.Abs(end - (click + to + 4.5f)) < 0.01f, "popup: the line under the title spaces its words 0.3 em apart");
            }

            using (ForgePainter p = new ForgePainter(1.5f, 870))
            {
                p.Title = "MATCH FOUND";
                p.Map = "Forests of Caldavar";
                bool solid = true;
                using (Bitmap b = RenderPopup(p, 2000))
                    for (int y = p.Frame.Top; y < p.Frame.Bottom; y++)
                        for (int x = p.Frame.Left; x < p.Frame.Right; x++)
                            if (b.GetPixel(x, y).A != 255) solid = false;
                Check(solid, "popup: every pixel inside the frame is opaque, text included (no see-through holes)");
            }

            using (ForgePainter waiting = new ForgePainter(1f, 580))
            using (ForgePainter notice = new ForgePainter(1f, 580))
            {
                waiting.Title = "MATCH FOUND";
                notice.Title = "MATCH ENDED";
                notice.Stage = AlertStage.Over;
                notice.Notice = NoticeReason.Ended;
                int x = waiting.Frame.X + waiting.Frame.Width / 2, y = waiting.Frame.Y - 6;
                // The fire is one continuous glow: just above the frame, all along the top edge, the glow
                // changes only gradually from pixel to pixel and never drops into a gap between flames.
                bool melted = true;
                string gapAt = "";
                foreach (double t in new double[] { 3000, 3700, 4400, 5100 })
                    using (Bitmap b = RenderPopup(waiting, t))
                    {
                        int yy = waiting.Frame.Y - 6, lo = 255, hi = 0, prev = -1, jump = 0;
                        for (int xx = waiting.Frame.X + 30; xx < waiting.Frame.Right - 30; xx++)
                        {
                            int a = b.GetPixel(xx, yy).A;
                            lo = Math.Min(lo, a); hi = Math.Max(hi, a);
                            if (prev >= 0) jump = Math.Max(jump, Math.Abs(a - prev));
                            prev = a;
                        }
                        if (jump > 2 || lo < hi * 0.45) { melted = false; gapAt = "t=" + t + " jump " + jump + " lo " + lo + " hi " + hi; }
                    }
                Check(melted, "popup: the fire is one continuous glow along the frame, flames merging into each other with no spots or gaps" + (melted ? "" : " (" + gapAt + ")"));

                // Left and right of the frame, at several moments: both sides flicker, but not in step.
                int lx = waiting.Frame.X - 10, rx = waiting.Frame.Right + 10, my = waiting.Frame.Y + waiting.Frame.Height / 2;
                int minL = 255, maxL = 0, minR = 255, maxR = 0, sameStep = 0, still = -1;
                bool noticeStill = true;
                int lastL = -1, lastR = -1;
                for (int k = 0; k < 12; k++)
                {
                    double t = 3000 + k * 250;
                    using (Bitmap b = RenderPopup(waiting, t))
                    {
                        int l = b.GetPixel(lx, my).A, r = b.GetPixel(rx, my).A;
                        minL = Math.Min(minL, l); maxL = Math.Max(maxL, l); minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                        if (lastL >= 0 && Math.Sign(l - lastL) == Math.Sign(r - lastR)) sameStep++;
                        lastL = l; lastR = r;
                    }
                    using (Bitmap b = RenderPopup(notice, t))
                    {
                        int a = b.GetPixel(lx, my).A;
                        if (still >= 0 && a != still) noticeStill = false;
                        still = a;
                    }
                }
                Check(maxL - minL > 8 && maxR - minR > 8 && sameStep < 9 && minL > still + 3 && noticeStill,
                    "popup: a waiting alert's glow flickers around the frame, different on each side; a grey notice has none and does not move");

                // Going straight up from the frame, glow and shadow fade out smoothly: no step in the edge.
                bool smooth = true;
                int last = -1, lastVisible = 0;
                using (Bitmap b = RenderPopup(waiting, 50))
                    for (int yy = waiting.Frame.Y - 1; yy >= 0; yy--)
                    {
                        int a = b.GetPixel(x, yy).A;
                        if (last >= 0 && (a > last || last - a > 20)) smooth = false;
                        if (a > 0) lastVisible = a;
                        last = a;
                    }
                Check(smooth && last == 0 && lastVisible <= 4, "popup: the glow and shadow fade out smoothly to nothing, with no hard outer edge");
            }

            // The timer stands on the same baseline as the line under the title: the lowest row of 0:11 is the
            // lowest row of "Mid Wars". (Cinzel's 3, 5 and 9 dip below the baseline by design, like a "g".)
            bool aligned = true;
            string offBy = "";
            foreach (float scale in new float[] { 1f, 1.5f })
                using (ForgePainter line = new ForgePainter(scale, (int)Math.Round(580 * scale)))
                {
                    line.Title = "MATCH FOUND";
                    line.Map = "Mid Wars";
                    using (Bitmap b = new Bitmap(line.Size.Width, line.Size.Height, PixelFormat.Format32bppPArgb))
                    {
                        using (Graphics g = Graphics.FromImage(b)) line.Draw(g, 2000, TimeSpan.FromSeconds(11));
                        Func<int, int, int, int, int> lowestInk = (x0, x1, y0, y1) =>
                        {
                            int lowest = -1;
                            for (int yy = y0; yy < y1; yy++)
                                for (int xx = x0; xx < x1; xx++)
                                    if (b.GetPixel(xx, yy).R > 150) { lowest = yy; break; }
                            return lowest;
                        };
                        int top = (int)line.TitleBounds.Bottom + 2, bottom = line.Frame.Bottom - 6;
                        int map = lowestInk((int)line.SubBounds.X, (int)line.SubBounds.X + (int)(45 * scale), top, bottom);
                        int clock = lowestInk((int)line.ClockBounds.X, (int)line.ClockBounds.Right, top, bottom);
                        if (map < 0 || clock < 0 || Math.Abs(map - clock) > 1) { aligned = false; offBy += " x" + scale + ": text " + map + ", timer " + clock; }
                    }
                }
            Check(aligned, "popup: the timer's digits stand on the same baseline as the line under the title" + (aligned ? "" : " (" + offBy + ")"));

            // The clock: going from 0:11 to 0:14 (a narrow digit to a wide one) only the last digit's cell changes; "0", ":" and "1" stay put.
            using (ForgePainter clock = new ForgePainter(1f, 580))
            {
                clock.Title = "MATCH FOUND";
                Bitmap a = new Bitmap(clock.Size.Width, clock.Size.Height, PixelFormat.Format32bppPArgb);
                Bitmap c = new Bitmap(clock.Size.Width, clock.Size.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(a)) clock.Draw(g, 2000, TimeSpan.FromSeconds(11));
                using (Graphics g = Graphics.FromImage(c)) clock.Draw(g, 2000, TimeSpan.FromSeconds(14));
                int right = (int)(clock.Frame.Right - 48 - clock.DigitCell) - 1, left = right - (int)(clock.DigitCell * 3) - 4;
                bool same = clock.DigitCell > 0, inked = false;
                for (int yy = (int)clock.ClockBounds.Top; yy < (int)clock.ClockBounds.Bottom + 6; yy++)
                    for (int xx = left; xx < right; xx++)
                    {
                        if (a.GetPixel(xx, yy) != c.GetPixel(xx, yy)) same = false;
                        if (a.GetPixel(xx, yy).R > 200) inked = true;
                    }
                a.Dispose();
                c.Dispose();
                Check(same && inked, "popup clock: when the seconds change, the other digits do not move");
            }

            using (AlertForm f = new AlertForm(new Rectangle(-30000, -30000, 1920, 1040), "TopCenter", 1f))
            {
                f.SetContent("MATCH FOUND", AlertStage.Found, NoticeReason.None, true, null, DateTime.UtcNow);
                f.Show();
                bool running = f.Animating;
                f.Hide();
                Check(running && !f.Animating, "popup: its one timer runs while it is shown and stops when it is hidden");
            }

            // A notice's crest follows why the alert ended, not its title: each reason has its own crest,
            // and the same reason with other words in the title draws the same crest.
            {
                Func<NoticeReason, string, Bitmap> crest = delegate(NoticeReason reason, string title)
                {
                    using (ForgePainter p = new ForgePainter(1f, 580))
                    {
                        p.Stage = AlertStage.Over;
                        p.Notice = reason;
                        p.Title = title;
                        using (Bitmap b = RenderPopup(p, 2000))
                        {
                            Rectangle box = new Rectangle(p.Frame.X + 20, p.Frame.Y + (p.Frame.Height - 64) / 2, 64, 64);
                            return b.Clone(box, b.PixelFormat);
                        }
                    }
                };
                Func<Bitmap, Bitmap, bool> same = delegate(Bitmap a, Bitmap b)
                {
                    for (int y = 0; y < a.Height; y++)
                        for (int x = 0; x < a.Width; x++)
                            if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
                    return true;
                };
                using (Bitmap cancelled = crest(NoticeReason.Cancelled, "MATCH CANCELLED"))
                using (Bitmap ended = crest(NoticeReason.Ended, "MATCH ENDED"))
                using (Bitmap gameClosed = crest(NoticeReason.GameClosed, "GAME WAS CLOSED"))
                using (Bitmap renamed = crest(NoticeReason.Cancelled, "SOMETHING ELSE"))
                    Check(!same(cancelled, ended) && !same(cancelled, gameClosed) && !same(ended, gameClosed) && same(cancelled, renamed),
                        "popup: a notice's crest shows why it ended (cancelled, ended, game closed), whatever its title says");
            }

            // Redrawing a waiting alert or a notice (30 and 4 times a second) makes no new fonts.
            using (ForgePainter waitingPainter = new ForgePainter(1f, 580))
            using (ForgePainter noticePainter = new ForgePainter(1f, 580))
            using (Bitmap frame = new Bitmap(waitingPainter.Size.Width, waitingPainter.Size.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(frame))
            {
                waitingPainter.Title = "MATCH FOUND";
                noticePainter.Title = "MATCH ENDED";
                noticePainter.Stage = AlertStage.Over;
                noticePainter.Notice = NoticeReason.Ended;
                waitingPainter.Draw(g, 1000, TimeSpan.FromSeconds(10));
                noticePainter.Draw(g, 1000, TimeSpan.FromSeconds(10));
                int before = ForgeFont.Created;
                for (int k = 1; k <= 30; k++)
                {
                    waitingPainter.Draw(g, 1000 + 33 * k, TimeSpan.FromSeconds(10 + k));
                    noticePainter.Draw(g, 1000 + 250 * k, TimeSpan.FromSeconds(10 + k));
                }
                Check(ForgeFont.Created == before, "popup: redrawing a waiting alert or a notice makes no new fonts (" + (ForgeFont.Created - before) + " in 30 frames each)");
            }
        }

        static void HotkeyTests()
        {
            uint m;
            Keys k;
            Check(HotkeyText.TryParse("Alt+Q", out m, out k) && m == HotkeyText.Alt && k == Keys.Q
                && HotkeyText.Normalize(" ctrl + alt + m ") == "Ctrl+Alt+M" && HotkeyText.Normalize("Shift+F1") == "Shift+F1"
                && HotkeyText.Normalize("Alt+5") == "Alt+5" && HotkeyText.Normalize("Q") == null && HotkeyText.Normalize("Alt+Ctrl") == null
                && HotkeyText.Normalize("Alt+Nonsense") == null && HotkeyText.Normalize("") == null,
                "minimize hotkey: key combinations are read and written consistently; a bare key or modifiers alone are refused");

            Settings d = new Settings();
            Settings on = Settings.Parse("[General]\nMinimizeHotkeyEnabled=true\nMinimizeHotkey=Ctrl+Shift+Q\n", null);
            Settings bad = on.Clone();
            bad.MinimizeHotkey = "Q";
            Check(!d.MinimizeHotkeyEnabled && d.MinimizeHotkey == "Alt+Q" && on.MinimizeHotkeyEnabled && on.MinimizeHotkey == "Ctrl+Shift+Q"
                && HasProblem(bad, "General.MinimizeHotkey") && !HasProblem(on, "General.MinimizeHotkey"),
                "minimize hotkey: off by default with Alt+Q; read from the settings file; an invalid combination is refused");

            HotkeyState st = new HotkeyState();
            int idle = st.Step(true, true, false);
            int focused = st.Step(true, true, true);
            st.Done(true);
            int stays = st.Step(true, true, true);
            int left = st.Step(true, true, false);
            st.Done(false);
            int off = st.Step(false, true, true);
            int invalid = st.Step(true, false, true);
            Check(idle == 0 && focused == 1 && stays == 0 && left == -1 && off == 0 && invalid == 0,
                "minimize hotkey: registered only while it is on, valid and the game has focus; released when the game loses focus");

            using (HotkeyWindow w = new HotkeyWindow())
            {
                bool ok = w.Register(HotkeyText.Ctrl | HotkeyText.Alt | HotkeyText.Shift, Keys.F12);
                w.Unregister();
                Check(ok && !w.Registered, "minimize hotkey: Windows accepts the registration, and it is released again");
            }

            // A stand-in for the game: a window of this process, found the same way as the game's window.
            using (Form stand = new Form())
            {
                stand.StartPosition = FormStartPosition.Manual;
                stand.Location = new Point(-20000, -20000);
                stand.Size = new Size(300, 200);
                stand.Show();
                Application.DoEvents();
                Win32Probe probe = new Win32Probe(Process.GetCurrentProcess().Id);
                bool minimized = probe.MinimizeGame();
                Application.DoEvents();
                Check(minimized && stand.WindowState == FormWindowState.Minimized,
                    "minimize hotkey: the game's window (found as for flashing and focus) is minimized");
                stand.Close();
            }
        }

        static void MiscTests()
        {
            using (ContextMenuStrip menu = TrayApp.BuildMenu(null, null, null, null, null, null))
            {
                bool status = false;
                foreach (ToolStripItem item in menu.Items) if (item.Text != null && item.Text.StartsWith("Status", StringComparison.OrdinalIgnoreCase)) status = true;
                Check(menu.Items[0].Text == "Show a test alert" && menu.Items[1].Text.StartsWith("Send a test notification", StringComparison.Ordinal)
                    && menu.Items[5].Text == "Start with Windows" && !status,
                    "tray menu: starts with the test alert; no status line");
            }
            Check(Regex.IsMatch(Program.Version, @"^\d+\.\d+\.\d+$") && Program.Version == typeof(Program).Assembly.GetName().Version.ToString(3)
                && TrayApp.TooltipText == "HoN Queue Notifier " + Program.Version && TrayApp.TooltipText.Length <= 63,
                "tray tooltip: the program's name and its version (three numbers, from the exe), within Windows' 63 characters");
            byte[] wav = Chime.BuiltInWav();
            int fmtLength = BitConverter.ToInt32(wav, 16);
            byte[] fmt = new byte[fmtLength];
            Array.Copy(wav, 20, fmt, 0, fmtLength);
            byte[] pcm = new byte[18];
            BitConverter.GetBytes((short)1).CopyTo(pcm, 0);            // PCM
            BitConverter.GetBytes((short)1).CopyTo(pcm, 2);            // mono
            BitConverter.GetBytes(44100).CopyTo(pcm, 4);
            BitConverter.GetBytes(88200).CopyTo(pcm, 8);
            BitConverter.GetBytes((short)2).CopyTo(pcm, 12);
            BitConverter.GetBytes((short)16).CopyTo(pcm, 14);
            IntPtr none;
            int decodable = Native.acmStreamOpen(out none, IntPtr.Zero, fmt, pcm, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1);   // 1 = only ask
            Check(wav.Length > 20000 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wav, 8, 8) == "WAVEfmt "
                && BitConverter.ToInt32(wav, 4) == wav.Length - 8 && BitConverter.ToInt16(fmt, 0) == 0x11 && BitConverter.ToInt16(fmt, 2) == 1
                && BitConverter.ToInt32(fmt, 4) == 44100 && decodable == 0,
                "built-in sound: a mono 44.1 kHz IMA ADPCM WAV that Windows can decode");
            Check(Texts.Elapsed(TimeSpan.FromSeconds(7)) == "0:07" && Texts.Elapsed(TimeSpan.FromSeconds(754)) == "12:34"
                && Texts.Elapsed(TimeSpan.FromSeconds(3725)) == "1:02:05" && Texts.Elapsed(TimeSpan.FromSeconds(-3)) == "0:00", "elapsed time formatting");
            Check(Texts.MapName("midwars") == "Mid Wars" && Texts.MapName("somethingnew") == "somethingnew" && Texts.MapName(null) == null, "map names");
            Check(new Settings().PopupScreens == "Primary", "defaults: popup on the main display only");
            Rectangle area = new Rectangle(100, 50, 1920, 1040);
            Size size = new Size(580, 124);
            Check(AlertForm.Place(area, size, "TopCenter", 28) == new Point(100 + (1920 - 580) / 2, 78)
                && AlertForm.Place(area, size, "BottomRight", 28) == new Point(100 + 1920 - 580 - 28, 50 + 1040 - 124 - 28)
                && AlertForm.Place(area, size, "nonsense", 28) == new Point(100 + (1920 - 580) / 2, 78),
                "popup placement on a screen with an offset working area");
        }

        public static int Run(string outFile)
        {
            Line("HoN Queue Notifier self-test, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + ", " + Environment.OSVersion + ", runtime " + Environment.Version);
            foreach (KeyValuePair<string, Action> s in Sections) Section(s.Key, s.Value);
            Line("");
            Line(_failed == 0 ? "ALL " + _passed + " CHECKS PASSED" : _failed + " CHECK(S) FAILED, " + _passed + " passed");
            if (outFile != null) { try { File.WriteAllText(outFile, Out.ToString(), new UTF8Encoding(false)); } catch (Exception) { } }
            return _failed == 0 ? 0 : 1;
        }
    }
}
