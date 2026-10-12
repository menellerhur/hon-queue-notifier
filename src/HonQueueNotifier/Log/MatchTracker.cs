using System;

namespace HonQueueNotifier
{
    enum GamePhase { Unknown, Menu, MatchFound, Lobby, InGame }

    // What the log says the client is doing. One per log file.
    sealed class MatchTracker
    {
        public GamePhase Phase = GamePhase.Unknown;
        public int MatchSeq;            // goes up by one for every match
        public string World;            // e.g. "midwars"
        public DateTime FoundUtc;
        public bool ExplicitFound;      // saw the matchmaking line itself, not just its side effects

        // bookkeeping for AlertEngine
        public int HandledSeq;

        void NewMatch(DateTime nowUtc, bool isExplicit, GamePhase phase)
        {
            MatchSeq++;
            Phase = phase;
            FoundUtc = nowUtc;
            ExplicitFound = isExplicit;
            World = null;
        }

        bool NotInMatch { get { return Phase == GamePhase.Unknown || Phase == GamePhase.Menu; } }

        public void Apply(LogEvent e, DateTime nowUtc)
        {
            switch (e.Kind)
            {
                case LogEventKind.Reset:
                case LogEventKind.EngineStart:
                    Phase = GamePhase.Menu;
                    World = null;
                    break;

                case LogEventKind.MatchFound:
                    // The normal trigger. The side effects of the same match (world load, phase change)
                    // may already have been seen; in that case do not count it twice.
                    if (Phase == GamePhase.MatchFound && (nowUtc - FoundUtc).TotalSeconds < 15) ExplicitFound = true;
                    else NewMatch(nowUtc, true, GamePhase.MatchFound);
                    break;

                case LogEventKind.World:
                    // Fallback trigger: the client starts loading a map while it was in the menu.
                    if (NotInMatch) NewMatch(nowUtc, false, GamePhase.MatchFound);
                    World = e.A;
                    break;

                case LogEventKind.Phase:
                    if (e.B == "MainMenu") Phase = GamePhase.Menu;   // World is kept: a notice may still name the map
                    else if (e.B == "Loading")
                    {
                        // Second fallback trigger: leaving the menu to load a game.
                        if (e.A == "MainMenu" && Phase != GamePhase.MatchFound) NewMatch(nowUtc, false, GamePhase.MatchFound);
                    }
                    else if (e.B == "Lobby")
                    {
                        if (NotInMatch) NewMatch(nowUtc, false, GamePhase.Lobby); else Phase = GamePhase.Lobby;
                    }
                    else if (e.B == "InGame")
                    {
                        if (NotInMatch) NewMatch(nowUtc, false, GamePhase.InGame); else Phase = GamePhase.InGame;
                    }
                    break;
            }
        }
    }
}
