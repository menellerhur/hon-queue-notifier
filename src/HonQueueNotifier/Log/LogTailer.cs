using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HonQueueNotifier
{
    enum PollResult { Nothing, Bootstrap, Live }

    // The game writes console.log as UTF-16 little-endian with a byte order mark (FF FE), two bytes per
    // character. The encoding is decided from the first bytes of the file. A Decoder keeps the state
    // between reads, so a block that ends in the middle of a character is joined up on the next read.
    static class LogText
    {
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public const int BytesNeeded = 2;

        public static Encoding Detect(byte[] head)
        {
            if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) return Encoding.Unicode;
            if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF) return Encoding.BigEndianUnicode;
            if (head.Length >= 2 && head[0] != 0 && head[1] == 0) return Encoding.Unicode;   // UTF-16LE without a mark
            return Latin1;   // ASCII or UTF-8: every byte is one char, and all the text we match is ASCII
        }

        public static Decoder DecoderFor(byte[] head) { return Detect(head).GetDecoder(); }
    }

    // Follows one console.log. The file is opened only for the instant it takes to read what was
    // appended, with full sharing, so the game is never blocked from writing or replacing it.
    sealed class LogTailer
    {
        public readonly string FilePath;
        public readonly MatchTracker Tracker = new MatchTracker();
        public bool BootstrapOnAttach;                  // file already existed when we started: learn the state, alert on nothing
        public DateTime LastGrowthUtc = DateTime.MinValue;
        public int ForcedOpens;                          // times new data was only found by opening the file (diagnostics)
        public int StatHits;                             // times the file size alone revealed new data (diagnostics)

        readonly LogScanner _scanner = new LogScanner();
        long _pos = -1;
        byte[] _head = new byte[0];
        long _lastStatLen = -1;
        DateTime _lastOpenUtc = DateTime.MinValue;

        Decoder _decoder;                                // chosen from the first bytes; null until there are enough of them

        static readonly byte[] ReadBuffer = new byte[256 * 1024];
        static readonly char[] CharBuffer = new char[ReadBuffer.Length + 4];
        const int HeadSize = 192;

        public LogTailer(string path, bool bootstrapOnAttach)
        {
            FilePath = path;
            BootstrapOnAttach = bootstrapOnAttach;
        }

        public bool Attached { get { return _pos >= 0; } }
        public long Position { get { return _pos; } }

        static bool SamePrefix(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static byte[] ReadHead(FileStream fs, long len)
        {
            int want = (int)Math.Min(len, HeadSize);
            byte[] head = new byte[want];
            fs.Position = 0;
            int got = 0;
            while (got < want)
            {
                int n = fs.Read(head, got, want - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != want) Array.Resize(ref head, got);
            return head;
        }

        public PollResult Poll(DateTime nowUtc, List<LogEvent> events)
        {
            long statLen;
            try
            {
                FileInfo fi = new FileInfo(FilePath);
                if (!fi.Exists) return PollResult.Nothing;
                statLen = fi.Length;
            }
            catch (Exception) { return PollResult.Nothing; }

            // Cheap check first: has the size changed? Windows can report a stale size for a file another
            // program holds open, so the file is also opened on a timer regardless of what the size says.
            bool live = (nowUtc - LastGrowthUtc).TotalMinutes < 60;
            double sinceOpen = (nowUtc - _lastOpenUtc).TotalSeconds;
            bool sizeChanged = statLen != _lastStatLen;
            if (_pos >= 0 && !sizeChanged && sinceOpen >= 0 && sinceOpen < (live ? 1.0 : 3.0)) return PollResult.Nothing;

            bool first = _pos < 0;
            bool bootstrap = false;
            long readBytes = 0;
            try
            {
                using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                {
                    _lastOpenUtc = nowUtc;
                    _lastStatLen = statLen;
                    long len = fs.Length;
                    byte[] head = ReadHead(fs, len);

                    if (first)
                    {
                        _pos = 0;
                        _head = head;
                        bootstrap = BootstrapOnAttach;
                        BootstrapOnAttach = false;
                    }
                    else if (len < _pos || !SamePrefix(_head, head))
                    {
                        // Shorter than what we already read, or a different first line: the game was
                        // restarted and began a new log. Everything in it is new.
                        _pos = 0;
                        _head = head;
                        _decoder = null;
                        _scanner.Reset();
                        LogEvent reset = new LogEvent();
                        reset.Kind = LogEventKind.Reset;
                        events.Add(reset);
                    }
                    else if (head.Length > _head.Length) _head = head;

                    // Reading always starts at byte 0 of a file, so the decoder is chosen before the first read.
                    // Wait while the file is too short to tell the encoding.
                    if (_decoder == null && len >= LogText.BytesNeeded) _decoder = LogText.DecoderFor(head);

                    if (len > _pos && _decoder != null)
                    {
                        fs.Position = _pos;
                        while (_pos < len)
                        {
                            int want = (int)Math.Min(ReadBuffer.Length, len - _pos);
                            int n = fs.Read(ReadBuffer, 0, want);
                            if (n <= 0) break;
                            _pos += n;
                            readBytes += n;
                            int c = _decoder.GetChars(ReadBuffer, 0, n, CharBuffer, 0);
                            if (c > 0) _scanner.Feed(new string(CharBuffer, 0, c), events);
                        }
                    }
                }
            }
            catch (IOException) { _lastStatLen = -1; return PollResult.Nothing; }
            catch (UnauthorizedAccessException) { _lastStatLen = -1; return PollResult.Nothing; }

            if (bootstrap) return PollResult.Bootstrap;
            if (readBytes > 0)
            {
                LastGrowthUtc = nowUtc;
                if (!first) { if (sizeChanged) StatHits++; else ForcedOpens++; }
            }
            return (readBytes > 0 || events.Count > 0) ? PollResult.Live : PollResult.Nothing;
        }
    }
}
