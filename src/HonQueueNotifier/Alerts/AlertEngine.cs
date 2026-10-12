using System;
using System.Collections.Generic;
using System.Globalization;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Deciding when to alert
    // =====================================================================================
    enum AlertStage { None, Found, Started, Over }

    // Why an alert became a grey notice. The popup's title and its crest both follow from it.
    enum NoticeReason { None, Cancelled, Ended, GameClosed }

    interface IEnvironmentProbe
    {
        bool IsGameForeground();
        bool IsGameRunning();
        int IdleSeconds();
        string DescribeForeground();
    }

    interface IAlertSink
    {
        void ShowAlert(string title, AlertStage stage, MatchTracker tracker, DateTime sinceUtc);
        void HideAlert();
        void PlaySound();
        void FlashGame();
        void SendPush(AlertStage stage, MatchTracker tracker);
    }

    static class Texts
    {
        public static string MapName(string world)
        {
            if (string.IsNullOrEmpty(world)) return null;
            switch (world.ToLowerInvariant())
            {
                case "midwars": return "Mid Wars";
                case "caldavar": return "Forests of Caldavar";
            }
            return world;
        }

        public static string Elapsed(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            int total = (int)t.TotalSeconds;
            if (total >= 3600) return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", total / 3600, (total / 60) % 60, total % 60);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", total / 60, total % 60);
        }

        public static string PopupTitle(AlertStage stage)
        {
            return stage == AlertStage.Started ? "GAME HAS STARTED" : "MATCH FOUND";
        }

        public static string NoticeTitle(NoticeReason reason)
        {
            switch (reason)
            {
                case NoticeReason.Cancelled: return "MATCH CANCELLED";
                case NoticeReason.Ended: return "MATCH ENDED";
                case NoticeReason.GameClosed: return "GAME WAS CLOSED";
            }
            return "";
        }

        public static string PushTitle(AlertStage stage)
        {
            return stage == AlertStage.Started ? "HoN: game has started" : "HoN: match found";
        }

        // The test notification, from the tray menu and from the settings dialog.
        public const string TestPushTitle = "HoN Queue Notifier test";
        public const string TestPushBody = "If you can read this on your phone, match notifications will arrive too.";

        public static string PushBody(AlertStage stage, MatchTracker t)
        {
            string map = t == null ? null : MapName(t.World);
            string lead = map == null ? "" : map + ". ";
            return stage == AlertStage.Started
                ? lead + "The game is running and you are not in it."
                : lead + "Hero pick is starting.";
        }
    }

    sealed class AlertEngine
    {
        readonly Settings _s;
        readonly IEnvironmentProbe _probe;
        readonly IAlertSink _sink;

        public AlertStage Active = AlertStage.None;
        public MatchTracker ActiveTracker;
        public DateTime StageSinceUtc;
        public bool IsTest;
        public string Title = "";           // what the popup says right now
        public NoticeReason Notice;         // why it became a notice (Active == Over)

        DateTime _raisedUtc;                // when the alert came up; the sound repeats count from here
        DateTime _lastSoundUtc;
        bool _pushPending;
        DateTime _pushDueUtc;
        bool _gameSeenRunning;

        public AlertEngine(Settings settings, IEnvironmentProbe probe, IAlertSink sink)
        {
            _s = settings;
            _probe = probe;
            _sink = sink;
        }

        // History that was already in the log when we started: learn the state, alert on nothing.
        public void Bootstrap(MatchTracker t, IList<LogEvent> events, DateTime nowUtc)
        {
            for (int i = 0; i < events.Count; i++) t.Apply(events[i], nowUtc);
            t.HandledSeq = t.MatchSeq;
        }

        // Everything read in one go is applied first and judged once, on the state it ends in. If we
        // fell behind (PC asleep, say) and the log already shows the match over, nothing is raised.
        public void Apply(MatchTracker t, IList<LogEvent> events, DateTime nowUtc)
        {
            GamePhase before = t.Phase;
            for (int i = 0; i < events.Count; i++)
            {
                LogEvent e = events[i];
                if (e.Kind != LogEventKind.World) AppLog.Write("log: {0}", e);
                t.Apply(e, nowUtc);
            }
            if (t.Phase != before) AppLog.Write("state: {0} -> {1}", before, t.Phase);
            Evaluate(t, nowUtc);
        }

        void Evaluate(MatchTracker t, DateTime nowUtc)
        {
            bool inMatch = t.Phase == GamePhase.MatchFound || t.Phase == GamePhase.Lobby || t.Phase == GamePhase.InGame;
            if (!inMatch)
            {
                // The match is gone (cancelled during hero pick, or finished) and you never came back to it.
                // The popup is not taken away behind your back: it says what happened and stays.
                if (Active != AlertStage.None && Active != AlertStage.Over && !IsTest && ActiveTracker == t)
                    MarkOver(Active == AlertStage.Started ? NoticeReason.Ended : NoticeReason.Cancelled, nowUtc);
                return;
            }
            if (t.MatchSeq != t.HandledSeq)
            {
                t.HandledSeq = t.MatchSeq;
                Raise(t.Phase == GamePhase.InGame ? AlertStage.Started : AlertStage.Found, t, nowUtc);
                return;
            }
            if (t.Phase == GamePhase.InGame && Active == AlertStage.Found && !IsTest && ActiveTracker == t)
            {
                // The game started while the alert is still up. The same alert changes to say so (the popup
                // turns ember red, its clock now counts from the start of the game) and nothing new
                // happens: no sound, no flash, no push. With no alert up, the game starting is not news.
                AppLog.Write("GAME STARTED while the alert is up: it now says so; no sound, no flash, no push");
                Active = AlertStage.Started;
                Title = Texts.PopupTitle(Active);
                StageSinceUtc = nowUtc;
                if (_s.PopupEnabled) _sink.ShowAlert(Title, Active, t, nowUtc);
                return;
            }
            if (Active != AlertStage.None && Active != AlertStage.Over && !IsTest && ActiveTracker == t && _s.PopupEnabled)
                _sink.ShowAlert(Title, Active, t, Active == AlertStage.Found ? t.FoundUtc : StageSinceUtc);   // refresh the text
        }

        // A new match. Usually it is just found; if we fell behind, its game may already be running.
        void Raise(AlertStage stage, MatchTracker t, DateTime nowUtc)
        {
            bool focused = _probe.IsGameForeground();
            int idle = _probe.IdleSeconds();
            bool phone = _s.PhoneEnabled;
            AppLog.Write("{0}: map={1}, trigger={2}, game focused={3}, idle={4}s, foreground={5}",
                stage == AlertStage.Started ? "GAME STARTED" : "MATCH FOUND",
                t.World ?? "?", t.ExplicitFound ? "matchmaking line" : "fallback", focused, idle, _probe.DescribeForeground());

            if (focused)
            {
                // You are looking at the game, so no popup. If the PC has been idle you have probably
                // walked away with the game on screen, and the phone is the only way to reach you.
                if (Active != AlertStage.None) Dismiss("the game is focused");
                if (phone && (_s.PhoneAlways || idle >= _s.PhoneIdleSeconds)) _sink.SendPush(stage, t);
                return;
            }

            IsTest = false;
            Active = stage;
            Notice = NoticeReason.None;
            ActiveTracker = t;
            StageSinceUtc = nowUtc;
            _raisedUtc = nowUtc;
            _lastSoundUtc = nowUtc;
            Title = Texts.PopupTitle(stage);
            _gameSeenRunning = _probe.IsGameRunning();
            if (_s.PopupEnabled) _sink.ShowAlert(Title, stage, t, stage == AlertStage.Found ? t.FoundUtc : nowUtc);
            if (_s.SoundEnabled) _sink.PlaySound();
            if (_s.FlashTaskbar) _sink.FlashGame();

            _pushPending = false;
            if (phone)
            {
                if (_s.PhoneAlways || _s.PhoneAfterSeconds <= 0 || idle >= _s.PhoneIdleSeconds) _sink.SendPush(stage, t);
                else
                {
                    _pushPending = true;
                    _pushDueUtc = nowUtc.AddSeconds(_s.PhoneAfterSeconds);
                }
            }
        }

        // The popup never goes away on its own. It leaves when you click it or when the game window
        // gets focus (you are looking at the game).
        public void Tick(DateTime nowUtc)
        {
            if (Active == AlertStage.None || IsTest) return;   // a test alert simply waits for a click
            if (_probe.IsGameForeground()) { Dismiss("the game got focus"); return; }
            if (Active == AlertStage.Over) return;             // a notice: no sound, no phone, nothing left to watch
            if (_probe.IsGameRunning()) _gameSeenRunning = true;
            else if (_gameSeenRunning) { MarkOver(NoticeReason.GameClosed, nowUtc); return; }
            if (_pushPending && nowUtc >= _pushDueUtc)
            {
                // The one push for this match. If the game has started meanwhile, it says so.
                _pushPending = false;
                _sink.SendPush(Active, ActiveTracker);
            }
            if (_s.SoundEnabled && _s.SoundRepeatSeconds > 0
                && (nowUtc - _lastSoundUtc).TotalSeconds >= _s.SoundRepeatSeconds
                && (nowUtc - _raisedUtc).TotalSeconds <= _s.SoundRepeatForSeconds)
            {
                _lastSoundUtc = nowUtc;
                _sink.PlaySound();
            }
        }

        void MarkOver(NoticeReason reason, DateTime nowUtc)
        {
            string title = Texts.NoticeTitle(reason);
            if (!_s.PopupEnabled) { Dismiss(title.ToLowerInvariant()); return; }   // nothing on screen to keep
            AppLog.Write("alert kept on screen as a notice: {0}", title);
            Active = AlertStage.Over;
            Notice = reason;
            Title = title;
            StageSinceUtc = nowUtc;
            _pushPending = false;
            _sink.ShowAlert(title, AlertStage.Over, ActiveTracker, nowUtc);
        }

        public void StartTest(DateTime nowUtc)
        {
            if (Active != AlertStage.None && Active != AlertStage.Over && !IsTest) return;   // a real alert is showing
            if (Active != AlertStage.None) _sink.HideAlert();   // a fresh popup, so it appears again from the start
            MatchTracker fake = new MatchTracker();
            fake.World = "midwars";
            fake.FoundUtc = nowUtc;
            IsTest = true;
            Active = AlertStage.Found;
            Notice = NoticeReason.None;
            ActiveTracker = fake;
            StageSinceUtc = nowUtc;
            _raisedUtc = nowUtc;
            Title = Texts.PopupTitle(AlertStage.Found);
            _pushPending = false;
            _sink.ShowAlert(Title, AlertStage.Found, fake, nowUtc);
            if (_s.SoundEnabled) _sink.PlaySound();
        }

        public void Acknowledge(string reason)
        {
            if (Active != AlertStage.None) Dismiss(reason);
        }

        void Dismiss(string reason)
        {
            AppLog.Write("alert dismissed: {0}", reason);
            Active = AlertStage.None;
            Notice = NoticeReason.None;
            ActiveTracker = null;
            IsTest = false;
            Title = "";
            _pushPending = false;
            _sink.HideAlert();
        }
    }
}
