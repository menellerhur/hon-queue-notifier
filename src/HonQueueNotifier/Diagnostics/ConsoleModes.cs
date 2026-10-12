using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace HonQueueNotifier
{
    static class ConsoleModes
    {
        public static int Replay(string file, int chunk, bool focused)
        {
            byte[] data = File.ReadAllBytes(file);
            Decoder decoder = LogText.DecoderFor(data);
            Settings s = new Settings();   // phone stays off: a replay has no real clock, so only the on-screen decisions are shown
            FakeProbe probe = new FakeProbe();
            probe.Foreground = focused;
            RecordingSink sink = new RecordingSink();
            sink.Echo = true;
            AlertEngine engine = new AlertEngine(s, probe, sink);
            LogScanner scanner = new LogScanner();
            MatchTracker tracker = new MatchTracker();
            List<LogEvent> events = new List<LogEvent>();
            Random random = new Random(20261011);
            DateTime now = DateTime.UtcNow;
            int pos = 0, total = 0, feeds = 0;
            Console.WriteLine("Replaying {0} ({1} bytes), {2}, game {3}", file, data.Length,
                chunk > 0 ? chunk.ToString(CultureInfo.InvariantCulture) + " bytes at a time" : "in random-sized pieces",
                focused ? "focused" : "not focused");
            while (pos < data.Length)
            {
                int n = chunk > 0 ? chunk : random.Next(1, 8192);
                n = Math.Min(n, data.Length - pos);
                events.Clear();
                char[] chars = new char[n + 4];
                int c = decoder.GetChars(data, pos, n, chars, 0);
                scanner.Feed(new string(chars, 0, c), events);
                pos += n;
                feeds++;
                for (int i = 0; i < events.Count; i++)
                {
                    total++;
                    Console.WriteLine("  by byte {0,9}: {1}", pos, events[i]);
                }
                if (events.Count > 0) engine.Apply(tracker, events, now);
                now = now.AddMilliseconds(250);
            }
            Console.WriteLine("{0} pieces, {1} events, {2} match(es), final state {3}", feeds, total, tracker.MatchSeq, tracker.Phase);
            return 0;
        }

        public static int Watch(Settings s)
        {
            AppLog.Echo = true;
            Win32Probe probe = new Win32Probe();
            RecordingSink sink = new RecordingSink();
            sink.Echo = true;
            Core core = new Core(s, probe, sink);
            Console.WriteLine("Watching for matches. Nothing is shown or sent in this mode; decisions are printed. Ctrl+C to stop.");
            while (true)
            {
                try { core.Step(DateTime.UtcNow); }
                catch (Exception ex) { AppLog.Write("step failed: {0}", ex.Message); }
                Thread.Sleep(Core.PollMilliseconds);
            }
        }
    }
}
