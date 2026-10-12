using System;
using System.Collections.Generic;

namespace HonQueueNotifier
{
    // Glue shared by the tray app and the console mode: follow the logs, feed the engine.
    sealed class Core
    {
        // How often the logs are checked. A match is noticed within this time; there is nothing to tune.
        public const int PollMilliseconds = 250;

        public readonly Settings Settings;
        public readonly AlertEngine Engine;
        public readonly LogWatcher Watcher;

        readonly List<LogEvent> _events = new List<LogEvent>();

        public Core(Settings settings, IEnvironmentProbe probe, IAlertSink sink)
        {
            Settings = settings;
            Engine = new AlertEngine(settings, probe, sink);
            Watcher = new LogWatcher(settings);
        }

        public void Step(DateTime nowUtc)
        {
            Watcher.RescanIfDue(nowUtc);
            IList<LogTailer> tailers = Watcher.Tailers;
            for (int i = 0; i < tailers.Count; i++)
            {
                LogTailer t = tailers[i];
                _events.Clear();
                PollResult r = t.Poll(nowUtc, _events);
                if (r == PollResult.Bootstrap)
                {
                    Engine.Bootstrap(t.Tracker, _events, nowUtc);
                    AppLog.Write("attached to {0} at {1} bytes, state: {2}", t.FilePath, t.Position, t.Tracker.Phase);
                }
                else if (r == PollResult.Live && _events.Count > 0) Engine.Apply(t.Tracker, _events, nowUtc);
            }
            Engine.Tick(nowUtc);
        }
    }
}
