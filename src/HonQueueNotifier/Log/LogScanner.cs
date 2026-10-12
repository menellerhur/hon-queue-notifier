using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Reading the game's console.log
    // =====================================================================================
    enum LogEventKind { MatchFound, World, Phase, EngineStart, Reset }

    struct LogEvent
    {
        public LogEventKind Kind;
        public string A;
        public string B;

        public override string ToString()
        {
            switch (Kind)
            {
                case LogEventKind.MatchFound: return "match found (matchmaking line)";
                case LogEventKind.World: return "loading world \"" + A + "\"";
                case LogEventKind.Phase: return "client phase " + A + " -> " + B;
                case LogEventKind.EngineStart: return "engine start";
                default: return "log was replaced (new game session)";
            }
        }
    }

    // Turns appended log text into events. The game writes its log in buffered blocks, so text can
    // arrive cut off in the middle of a line, and during loading several threads print at once so
    // fragments from different lines get glued together. Patterns are therefore matched as
    // substrings, never as whole lines, and an unfinished last line is kept until the rest arrives.
    sealed class LogScanner
    {
        static readonly Regex Pattern = new Regex(
            "CMatchmakingClient::HandleFoundMatch"
            + "|Client loading world \"([^\"\\r\\n]{1,64})\""
            + "|Client phase ([A-Za-z]{1,24}) -> ([A-Za-z]{1,24})"
            + "|Engine start up\\.\\.\\.",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        const int MaxCarry = 4096;   // longest unfinished line we hold back before scanning it anyway
        const int KeepTail = 256;    // longer than any pattern above

        string _carry = "";

        public void Reset() { _carry = ""; }

        public void Feed(string text, List<LogEvent> output)
        {
            if (string.IsNullOrEmpty(text)) return;
            string all = _carry.Length == 0 ? text : _carry + text;
            int cut = all.LastIndexOf('\n');
            int scanEnd;
            if (cut >= 0) scanEnd = cut + 1;                              // everything up to the last complete line
            else if (all.Length > MaxCarry) scanEnd = all.Length - KeepTail; // one enormous unfinished line
            else { _carry = all; return; }

            Match m = Pattern.Match(all);
            while (m.Success && m.Index < scanEnd)
            {
                LogEvent e = new LogEvent();
                if (m.Groups[1].Success) { e.Kind = LogEventKind.World; e.A = m.Groups[1].Value; }
                else if (m.Groups[2].Success) { e.Kind = LogEventKind.Phase; e.A = m.Groups[2].Value; e.B = m.Groups[3].Value; }
                else if (m.Value[0] == 'E') e.Kind = LogEventKind.EngineStart;
                else e.Kind = LogEventKind.MatchFound;
                output.Add(e);
                m = m.NextMatch();
            }
            _carry = all.Substring(scanEnd);
        }
    }
}
