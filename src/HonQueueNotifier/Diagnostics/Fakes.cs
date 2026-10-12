using System;
using System.Collections.Generic;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Console modes and built-in checks
    // =====================================================================================
    sealed class FakeProbe : IEnvironmentProbe
    {
        public bool Foreground;
        public bool Running = true;
        public int Idle;
        public bool IsGameForeground() { return Foreground; }
        public bool IsGameRunning() { return Running; }
        public int IdleSeconds() { return Idle; }
        public string DescribeForeground() { return Foreground ? "game" : "something else"; }
    }

    sealed class RecordingSink : IAlertSink
    {
        public readonly List<string> Actions = new List<string>();
        public bool Echo;

        void Add(string action)
        {
            Actions.Add(action);
            if (Echo) { try { Console.WriteLine("      => " + action); } catch (Exception) { } }
        }

        public void ShowAlert(string title, AlertStage stage, MatchTracker tracker, DateTime sinceUtc)
        {
            string map = tracker == null ? null : Texts.MapName(tracker.World);
            Add("popup " + title + (map == null ? "" : " [" + map + "]"));
        }
        public void HideAlert() { Add("hide"); }
        public void PlaySound() { Add("sound"); }
        public void FlashGame() { Add("flash"); }
        public void SendPush(AlertStage stage, MatchTracker tracker) { Add("push " + Texts.PushTitle(stage)); }

        public int Count(string prefix)
        {
            int n = 0;
            for (int i = 0; i < Actions.Count; i++) if (Actions[i].StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
        }

        public string Joined() { return string.Join(" | ", Actions.ToArray()); }
    }
}
