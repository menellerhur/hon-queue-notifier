using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace HonQueueNotifier
{
    sealed class ForgePainter : IDisposable
    {
        public const int FrameWidth = 580, FrameHeight = 124, MarginSize = 48;
        public const double EntryMs = 520;
        // While an alert waits, the glow burns like a fire: a low steady base (Glow) plus Embers soft
        // flames spread around the frame, each flickering on its own irregular curve (Flame) and drifting
        // a little along the edge. Grey notices have neither.
        public const double Glow = 0.28;
        public const int Embers = 14;
        public const double FlickerMs = 600;   // the quick flicker's average step; slow swells take 2.2 s

        static readonly Color Bronze = Color.FromArgb(140, 106, 60);
        static readonly Color Gold = Color.FromArgb(217, 169, 87);
        static readonly Color Ember = Color.FromArgb(176, 74, 42);
        static readonly Color EmberBright = Color.FromArgb(255, 138, 74);
        static readonly Color Pewter = Color.FromArgb(95, 91, 85);
        static readonly Color PewterLight = Color.FromArgb(122, 118, 111);

        readonly float _scale;
        public readonly Size Size;
        public readonly Rectangle Frame;
        Bitmap _base, _glow;
        string _baseKey, _glowKey;

        public string Title = "";
        public AlertStage Stage = AlertStage.Found;
        public NoticeReason Notice;   // a notice's crest shows why: a cross, a line, or a square for a closed game
        public bool Test;
        public string Map;
        public bool HoverClose;
        public Rectangle CloseRect;
        public RectangleF TitleBounds, SubBounds, ClockBounds;
        public float SubBaseline;   // where the letters of the line under the title stand (the bottom of "a", not of "g")

        // How far below the top of a text line its letters stand, measured on a flat-bottomed "H" drawn
        // as an outline (the fonts' own ascent values do not match where the letters are drawn).
        static float Ascent(Font f)
        {
            using (GraphicsPath path = new GraphicsPath())
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                path.AddString("H", f.FontFamily, (int)f.Style, f.Size, PointF.Empty, sf);
                return path.GetBounds().Bottom;
            }
        }

        // The same for text drawn with ClearType, which snaps letters to the pixel grid and so stands a
        // little higher than its outline: draw an "H" at a whole-pixel y and find the row below its ink.
        static float DrawnBaseline(Font f)
        {
            int size = (int)Math.Ceiling(f.Size * 2) + 4;
            using (Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.DrawString("H", f, Brushes.White, 0, 0, sf);
                for (int y = size - 1; y >= 0; y--)
                    for (int x = 0; x < size; x++)
                        if (b.GetPixel(x, y).R > 128) return y + 1;
            }
            return Ascent(f);
        }

        public ForgePainter(float scale, int frameWidth)
        {
            _scale = scale <= 0 ? 1f : scale;
            int m = S(MarginSize);
            Frame = new Rectangle(m, m, frameWidth, S(FrameHeight));
            Size = new Size(frameWidth + 2 * m, Frame.Height + 2 * m);
            CloseRect = new Rectangle(Frame.Right - S(7) - S(28), Frame.Top + S(7), S(28), S(28));
        }

        int S(float v) { return (int)Math.Round(v * _scale); }
        float F(float v) { return v * _scale; }

        bool Live { get { return Stage != AlertStage.Over; } }
        bool Hot { get { return Stage == AlertStage.Started; } }

        // ---- motion: the entry, a pure function of the time since the popup appeared -----------

        static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - Math.Pow(1 - t, 3); }

        public static double EntryOpacity(double ms) { return Math.Max(0, Math.Min(1, ms / (EntryMs * 0.55))); }

        public static double EntryOffset(double ms) { return -16 * (1 - Ease(ms / EntryMs)); }

        // A bright flash as it lands: 2.2 times as bright at first, back to normal when the entry ends.
        public static double EntryBrightness(double ms)
        {
            double t = Math.Max(0, Math.Min(1, ms / EntryMs));
            return t < 0.55 ? 2.2 - (2.2 - 1.35) * (t / 0.55) : 1.35 - 0.35 * ((t - 0.55) / 0.45);
        }

        // True while the popup is still sliding in.
        public static bool Moving(double ms) { return ms < EntryMs; }

        // ---- fire: smooth random flicker ---------------------------------------------------------

        // A random number in 0..1 for (seed, step), the same every time it is asked for.
        static double Hash(int seed, long step)
        {
            unchecked
            {
                ulong h = (ulong)step * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)seed * 0xBF58476D1CE4E5B9UL;
                h ^= h >> 31;
                h *= 0x94D049BB133111EBUL;
                h ^= h >> 29;
                return (h & 0xFFFFFF) / (double)0xFFFFFF;
            }
        }

        // Value noise: random values at whole steps of x, joined by a Catmull-Rom curve, which flows
        // through each value without stopping there (a cosine join pauses at every step, and flames
        // pausing together read as changes "at set timings"). Clamped to 0..1.
        static double Noise(int seed, double x)
        {
            double i = Math.Floor(x), f = x - i;
            long n = (long)i;
            double p0 = Hash(seed, n - 1), p1 = Hash(seed, n), p2 = Hash(seed, n + 1), p3 = Hash(seed, n + 2);
            double v = 0.5 * (2 * p1 + (p2 - p0) * f + (2 * p0 - 5 * p1 + 4 * p2 - p3) * f * f + (3 * p1 - p0 - 3 * p2 + p3) * f * f * f);
            return Math.Max(0, Math.Min(1, v));
        }

        // A random number in 0..1 that belongs to one ember for good: its own speed and starting point.
        static double Trait(int ember, int which) { return Hash(5000 + 16 * ember + which, 0); }

        // How strongly ember 'ember' burns at time ms, 0..1: quick flicker (about every 0.6 s, 0.45 s once
        // the game has started) mixed with slower swells (2.2 s, 1.65 s). Each ember runs at its own speed
        // (plus or minus about 25 %) from its own starting point, so no two flames turn at the same moment.
        public static double Flame(int ember, double ms, bool hot)
        {
            double fast = (hot ? 450 : FlickerMs) * (0.78 + 0.45 * Trait(ember, 0));
            double slow = (hot ? 1650 : 2200) * (0.78 + 0.45 * Trait(ember, 1));
            return 0.6 * Noise(2 * ember, ms / fast + 50 * Trait(ember, 2)) + 0.4 * Noise(2 * ember + 1, ms / slow + 50 * Trait(ember, 3));
        }

        // ---- fire: one continuous glow whose strength varies along the frame -------------------
        // Every glow pixel outside the frame knows how far it is from the frame and where along the
        // frame's outline it is nearest. Each frame a "heat" curve is made along the outline: the steady
        // base plus every ember as a wide, overlapping bump, so neighbouring flames merge. Each pixel then
        // takes the heat at its place on the outline: the same soft falloff as the halo, reaching a little
        // further and turning from orange towards yellow where the fire is hotter.

        public const double FireAmp = 0.55;   // how much a fully flaring ember adds to the heat
        const double FirePeak = 0.5;          // alpha at the frame edge for heat 1

        int[] _firePixel;     // index of each glow-ring pixel in the bitmap
        float[] _fireDist;    // its distance from the frame
        int[] _fireAlong;     // where on the outline it is nearest, in pixels from the top-left corner
        int[] _fireBuffer;    // premultiplied ARGB, the whole bitmap
        double[] _heat;       // heat per outline pixel, this frame
        int _outline;
        static readonly double[] Falloff = MakeFalloff();

        static double[] MakeFalloff()
        {
            double[] t = new double[1025];
            for (int i = 0; i <= 1024; i++) { double x = i / 1024.0; t[i] = Math.Exp(-6.5 * x * x); }   // HaloProfile's curve
            return t;
        }

        int FireReach { get { return S(40); } }

        void BuildFireRing()
        {
            int w = Size.Width, h = Size.Height;
            double fw = Frame.Width, fh = Frame.Height;
            _outline = (int)Math.Round(2 * (fw + fh));
            int reach = (int)Math.Ceiling(FireReach * 1.15) + 1;
            List<int> pixel = new List<int>();
            List<float> dist = new List<float>();
            List<int> along = new List<int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    if (px >= Frame.Left && px <= Frame.Right && py >= Frame.Top && py <= Frame.Bottom) continue;
                    double nx = Math.Max(Frame.Left, Math.Min(Frame.Right, px)), ny = Math.Max(Frame.Top, Math.Min(Frame.Bottom, py));
                    double d = Math.Sqrt((px - nx) * (px - nx) + (py - ny) * (py - ny));
                    if (d >= reach) continue;
                    double a;
                    if (ny <= Frame.Top && nx < Frame.Right) a = nx - Frame.Left;
                    else if (nx >= Frame.Right && ny < Frame.Bottom) a = fw + (ny - Frame.Top);
                    else if (ny >= Frame.Bottom && nx > Frame.Left) a = fw + fh + (Frame.Right - nx);
                    else a = 2 * fw + fh + (Frame.Bottom - ny);
                    pixel.Add(y * w + x);
                    dist.Add((float)d);
                    along.Add(((int)Math.Round(a) % _outline + _outline) % _outline);
                }
            _firePixel = pixel.ToArray();
            _fireDist = dist.ToArray();
            _fireAlong = along.ToArray();
            _fireBuffer = new int[w * h];
            _heat = new double[_outline];
        }

        // The heat along the outline at time ms: the steady base plus every ember's bump.
        public double[] Heat(double ms)
        {
            if (_firePixel == null) BuildFireRing();
            int n = _outline;
            double sigma = n / (double)Embers * 0.6, twoSigma2 = 2 * sigma * sigma;
            double[] centre = new double[Embers], strength = new double[Embers];
            for (int k = 0; k < Embers; k++)
            {
                centre[k] = (k + 0.5) / Embers * n + (Noise(1000 + k, ms / (3000 * (0.78 + 0.45 * Trait(k, 4))) + 50 * Trait(k, 5)) - 0.5) * F(36);
                double at = ((centre[k] % n) + n) % n;
                double rise = at < Frame.Width ? 1.15 : (at >= Frame.Width + Frame.Height && at < 2 * Frame.Width + Frame.Height) ? 0.8 : 1.0;   // fire rises
                strength[k] = FireAmp * Math.Pow(Flame(k, ms, Hot), 1.6) * rise;
            }
            for (int s = 0; s < n; s++)
            {
                double heat = Glow;
                for (int k = 0; k < Embers; k++)
                {
                    double d = Math.Abs(s - centre[k]) % n;
                    if (d > n / 2.0) d = n - d;
                    if (d < 3 * sigma) heat += strength[k] * Math.Exp(-d * d / twoSigma2);
                }
                _heat[s] = heat;
            }
            return _heat;
        }

        void UpdateFire(double ms)
        {
            double[] heat = Heat(ms);
            Color cool = Hot ? Color.FromArgb(255, 70, 25) : Color.FromArgb(255, 140, 40);
            Color warm = Hot ? Color.FromArgb(255, 150, 60) : Color.FromArgb(255, 205, 95);
            double reach = FireReach;
            for (int i = 0; i < _firePixel.Length; i++)
            {
                double hval = heat[_fireAlong[i]];
                double flare = Math.Max(0, Math.Min(1, (hval - Glow) / FireAmp));
                double x = _fireDist[i] / (reach * (0.9 + 0.25 * flare));
                int argb = 0;
                if (x < 1)
                {
                    double a = Math.Min(1, FirePeak * hval * Falloff[(int)(x * 1024)]);
                    int alpha = (int)(a * 255 + 0.5);
                    if (alpha > 0)
                    {
                        int r = cool.R + (int)((warm.R - cool.R) * flare), g = cool.G + (int)((warm.G - cool.G) * flare), b = cool.B + (int)((warm.B - cool.B) * flare);
                        argb = (alpha << 24) | ((r * alpha / 255) << 16) | ((g * alpha / 255) << 8) | (b * alpha / 255);
                    }
                }
                _fireBuffer[_firePixel[i]] = argb;
            }
            BitmapData data = _glow.LockBits(new Rectangle(0, 0, _glow.Width, _glow.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(_fireBuffer, 0, data.Scan0, _fireBuffer.Length); }
            finally { _glow.UnlockBits(data); }
        }

        // ---- texts ----------------------------------------------------------------------------

        // [map, rest of the line under the title, clock caption, clock]; the last two are null for a notice.
        public static string[] Lines(AlertStage stage, bool test, string map, TimeSpan elapsed)
        {
            string sep = "  \u00B7  ";
            string clock = Texts.Elapsed(elapsed);
            if (test) return new string[] { "", "This is a test alert" + sep + "click to open the game", "WAITING", clock };
            string lead = string.IsNullOrEmpty(map) ? "" : map;
            string pre = lead.Length > 0 ? sep : "";
            if (stage == AlertStage.Over) return new string[] { lead, pre + clock + " ago" + sep + "click to dismiss", null, null };
            return new string[] { lead, pre + "click to open the game", stage == AlertStage.Started ? "RUNNING" : "WAITING", clock };
        }

        // ---- drawing --------------------------------------------------------------------------

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Max(0.1f, radius * 2);
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // How strong a halo is at distance d from the frame: a Gaussian falloff with a long faint tail,
        // below 1/255 by 'spread', so it melts into whatever is behind it instead of ending at a line.
        public static double HaloProfile(double d, double spread, double strength)
        {
            if (d >= spread) return 0;
            double t = d / spread;
            return strength * Math.Exp(-6.5 * t * t);
        }

        // Soft light or shade around the frame, built from rings drawn outside in. Each ring's own alpha
        // is chosen so that the layers add up to HaloProfile at every distance.
        void Halo(Graphics g, Color c, int spread, int dy, double strength)
        {
            for (int d = spread; d >= 1; d--)
            {
                double inner = HaloProfile(d - 1, spread, strength), outer = HaloProfile(d, spread, strength);
                double a = 1 - (1 - inner) / (1 - outer);
                int alpha = (int)Math.Round(255 * a);
                if (alpha <= 0) continue;
                RectangleF r = new RectangleF(Frame.X - d, Frame.Y - d + dy, Frame.Width + 2 * d, Frame.Height + 2 * d);
                using (GraphicsPath p = Rounded(r, d))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, c)))
                    g.FillPath(b, p);
            }
        }

        // Text with extra space between the letters, as a path (so it can be filled with a gradient).
        static GraphicsPath Tracked(Graphics g, string text, Font font, float x, float spacing)
        {
            GraphicsPath path = new GraphicsPath(FillMode.Winding);
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                float pos = x;
                foreach (char c in text)
                {
                    string s = c.ToString();
                    path.AddString(s, font.FontFamily, (int)font.Style, font.Size, new PointF(pos, 0), sf);
                    pos += g.MeasureString(s, font, PointF.Empty, sf).Width + spacing;
                }
            }
            return path;
        }

        static void MoveTo(GraphicsPath p, float left, float top)
        {
            RectangleF b = p.GetBounds();
            using (Matrix m = new Matrix())
            {
                m.Translate(left - b.Left, top - b.Top);
                p.Transform(m);
            }
        }

        PointF[] Shape(float x0, float y0, params float[] xy)
        {
            PointF[] pts = new PointF[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(x0 + F(xy[2 * i]), y0 + F(xy[2 * i + 1]));
            return pts;
        }

        RectangleF CrestBox { get { return new RectangleF(Frame.X + F(20), Frame.Y + (Frame.Height - F(64)) / 2f, F(64), F(64)); } }

        void DrawCrest(Graphics g)
        {
            RectangleF c = CrestBox;
            Color fill = !Live ? Color.FromArgb(30, 29, 27) : Hot ? Color.FromArgb(31, 19, 14) : Color.FromArgb(29, 23, 18);
            Color outer = !Live ? PewterLight : Hot ? EmberBright : Gold;
            Color inner = !Live ? Pewter : Hot ? Ember : Bronze;
            PointF[] big = Shape(c.X, c.Y, 32, 3, 61, 32, 32, 61, 3, 32);
            PointF[] small = Shape(c.X, c.Y, 32, 11, 53, 32, 32, 53, 11, 32);
            if (Live)
                using (Pen glow = new Pen(Color.FromArgb(50, Hot ? EmberBright : Gold), F(6)))
                    g.DrawPolygon(glow, big);
            using (SolidBrush b = new SolidBrush(fill)) g.FillPolygon(b, big);
            using (Pen p = new Pen(outer, F(2))) g.DrawPolygon(p, big);
            using (Pen p = new Pen(inner, F(1.5f))) g.DrawPolygon(p, small);

            if (Hot)
            {
                using (GraphicsPath flame = new GraphicsPath())
                {
                    flame.AddBeziers(Shape(c.X, c.Y, 32, 16, 38, 24, 42, 28, 40, 36, 39, 42, 35, 46, 32, 46, 29, 46, 25, 42, 24, 36,
                        23, 30, 27, 27, 29, 22, 30, 27, 32, 29, 34, 29, 35, 25, 34, 20, 32, 16));
                    using (SolidBrush b = new SolidBrush(EmberBright)) g.FillPath(b, flame);
                }
            }
            else if (!Live)
            {
                using (Pen p = new Pen(Color.FromArgb(167, 162, 153), F(3)))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    if (Notice == NoticeReason.Cancelled) { g.DrawLines(p, Shape(c.X, c.Y, 24, 24, 40, 40)); g.DrawLines(p, Shape(c.X, c.Y, 40, 24, 24, 40)); }
                    else if (Notice == NoticeReason.Ended) g.DrawLines(p, Shape(c.X, c.Y, 22, 32, 44, 32));
                    else g.DrawPolygon(p, Shape(c.X, c.Y, 25, 25, 39, 25, 39, 39, 25, 39));
                }
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(243, 201, 110)))
                    g.FillPolygon(b, Shape(c.X, c.Y, 32, 18, 35.5f, 28.5f, 46, 32, 35.5f, 35.5f, 32, 46, 28.5f, 35.5f, 18, 32, 28.5f, 28.5f));
            }
        }

        static float DrawTrackedText(Graphics g, string text, Font font, Brush brush, float x, float y, float spacing)
        {
            float pos = x;
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                foreach (char c in text)
                {
                    string s = c.ToString();
                    g.DrawString(s, font, brush, pos, y, sf);
                    pos += g.MeasureString(s, font, PointF.Empty, sf).Width + spacing;
                }
            }
            return pos - spacing - x;
        }

        static float TrackedWidth(Graphics g, string text, Font font, float spacing)
        {
            float w = 0;
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                foreach (char c in text) w += g.MeasureString(c.ToString(), font, PointF.Empty, sf).Width + spacing;
            }
            return Math.Max(0, w - spacing);
        }

        static Font Ui(float pixels, bool bold)
        {
            return new Font("Segoe UI", pixels, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }

        // The fonts drawn every frame (the clock, and a notice's line with its running time), made once.
        Font _digitsFont, _mapFont, _restFont;
        float _digitsAscent = -1, _restBaseline = -1;

        Font DigitsFont { get { return _digitsFont ?? (_digitsFont = ForgeFont.Create(F(28))); } }
        Font MapFont { get { return _mapFont ?? (_mapFont = new Font("Segoe UI Semibold", F(15), FontStyle.Regular, GraphicsUnit.Pixel)); } }
        Font RestFont { get { return _restFont ?? (_restFont = Ui(F(15), false)); } }
        float DigitsAscent { get { if (_digitsAscent < 0) _digitsAscent = Ascent(DigitsFont); return _digitsAscent; } }
        float RestBaseline { get { if (_restBaseline < 0) _restBaseline = DrawnBaseline(RestFont); return _restBaseline; } }

        // The clock: every digit gets a cell as wide as the widest digit, and the colon its own cell, so
        // no character moves when another changes. Drawn as a path, anti-aliased like the title.
        float _digitCell, _colonCell;

        public float DigitCell { get { return _digitCell; } }

        void MeasureClock(Graphics g, Font f)
        {
            if (_digitCell > 0) return;
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                for (char c = '0'; c <= '9'; c++) _digitCell = Math.Max(_digitCell, g.MeasureString(c.ToString(), f, PointF.Empty, sf).Width);
                _colonCell = g.MeasureString(":", f, PointF.Empty, sf).Width + F(2);
            }
        }

        float ClockWidth(string text)
        {
            float w = 0;
            foreach (char c in text) w += c == ':' ? _colonCell : _digitCell;
            return w;
        }

        GraphicsPath ClockPath(Graphics g, string text, Font f, float right, float top)
        {
            GraphicsPath path = new GraphicsPath(FillMode.Winding);
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                float x = right - ClockWidth(text);
                foreach (char c in text)
                {
                    float cell = c == ':' ? _colonCell : _digitCell;
                    string ch = c.ToString();
                    float w = g.MeasureString(ch, f, PointF.Empty, sf).Width;
                    path.AddString(ch, f.FontFamily, (int)f.Style, f.Size, new PointF(x + (cell - w) / 2f, top), sf);
                    x += cell;
                }
            }
            return path;
        }

        // From the top of the caption to the top of the digits' line.
        float ClockDigitsTop { get { return F(14); } }

        // Where the clock ends; the title must stay left of it.
        float ClockRight { get { return Frame.Right - F(48); } }

        int BorderWidth { get { return Math.Max(2, S(2)); } }

        void DrawBase(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // ClearType: all text sits on the opaque frame, so its pixels keep full alpha.
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            Halo(g, Color.Black, S(34), S(6), 0.5);

            // Whole-pixel rectangles: without anti-aliasing, so every edge falls exactly on a pixel.
            g.SmoothingMode = SmoothingMode.None;
            Color top = !Live ? Color.FromArgb(37, 36, 34) : Color.FromArgb(43, 37, 32);
            Color mid = !Live ? Color.FromArgb(27, 26, 24) : Color.FromArgb(26, 22, 18);
            Color bottom = !Live ? Color.FromArgb(27, 26, 24) : Color.FromArgb(20, 17, 14);
            using (LinearGradientBrush bg = new LinearGradientBrush(Frame, top, bottom, LinearGradientMode.Vertical))
            {
                ColorBlend blend = new ColorBlend(3);
                blend.Colors = new Color[] { top, mid, bottom };
                blend.Positions = new float[] { 0f, 0.55f, 1f };
                bg.InterpolationColors = blend;
                g.FillRectangle(bg, Frame);
            }

            // The border: the same width on all four sides.
            int bw = BorderWidth;
            Color edge = !Live ? Pewter : Hot ? Ember : Bronze;
            using (SolidBrush b = new SolidBrush(edge))
            {
                g.FillRectangle(b, Frame.X, Frame.Y, Frame.Width, bw);
                g.FillRectangle(b, Frame.X, Frame.Bottom - bw, Frame.Width, bw);
                g.FillRectangle(b, Frame.X, Frame.Y, bw, Frame.Height);
                g.FillRectangle(b, Frame.Right - bw, Frame.Y, bw, Frame.Height);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawInnerLine(g, 0);

            // A small diamond on each corner.
            Color corner = !Live ? PewterLight : Hot ? EmberBright : Gold;
            float h = F(6.4f);
            foreach (Point p in new Point[] { new Point(Frame.Left, Frame.Top), new Point(Frame.Right, Frame.Top), new Point(Frame.Left, Frame.Bottom), new Point(Frame.Right, Frame.Bottom) })
            {
                PointF[] diamond = { new PointF(p.X, p.Y - h), new PointF(p.X + h, p.Y), new PointF(p.X, p.Y + h), new PointF(p.X - h, p.Y) };
                if (Live) using (Pen glow = new Pen(Color.FromArgb(70, corner), F(5))) g.DrawPolygon(glow, diamond);
                using (SolidBrush b = new SolidBrush(corner)) g.FillPolygon(b, diamond);
            }

            DrawCrest(g);

            string[] lines = Lines(Stage, Test, Map, TimeSpan.Zero);
            float textLeft = CrestBox.Right + F(18);

            // The clock's width first (the title must stay left of it); it is placed once the line under
            // the title is laid out, so its digits can stand on that line's baseline.
            ClockBounds = RectangleF.Empty;
            float clockLeft = ClockRight, clockWidth = 0, captionWidth = 0;
            if (lines[2] != null)
            {
                using (Font cap = Ui(F(10), true))
                {
                    MeasureClock(g, DigitsFont);
                    captionWidth = TrackedWidth(g, lines[2], cap, F(2.4f));
                    clockWidth = Math.Max(captionWidth, ClockWidth("00:00"));
                    clockLeft = ClockRight - clockWidth;
                }
            }

            // Title: gold (ember once the game has started, grey for a notice), with a warm glow.
            float size = 31;
            GraphicsPath title;
            float maxWidth = clockLeft - F(16) - textLeft - (Test ? F(64) : 0);
            while (true)
            {
                using (Font tf = ForgeFont.Create(F(size)))
                    title = Tracked(g, Title, tf, 0, F(size) * 0.07f);
                if (title.GetBounds().Width <= maxWidth || size <= 16) break;
                title.Dispose();
                size -= 1;
            }
            float capHeight = title.GetBounds().Height;
            float blockTop = Frame.Y + (Frame.Height - (capHeight + F(12) + F(20))) / 2f;
            MoveTo(title, textLeft, blockTop);
            TitleBounds = title.GetBounds();
            if (Live)
            {
                Color glow = Hot ? Color.FromArgb(255, 100, 40) : Color.FromArgb(255, 170, 60);
                float[] widths = { 9, 6, 3 };
                int[] alphas = { 14, 24, 40 };
                for (int i = 0; i < widths.Length; i++)
                    using (Pen p = new Pen(Color.FromArgb(alphas[i], glow), F(widths[i])))
                    {
                        p.LineJoin = LineJoin.Round;
                        g.DrawPath(p, title);
                    }
            }
            using (Matrix down = new Matrix())
            {
                down.Translate(0, F(1));
                title.Transform(down);
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillPath(shadow, title);
                down.Reset();
                down.Translate(0, -F(1));
                title.Transform(down);
            }
            if (Live)
            {
                RectangleF tb = TitleBounds;
                tb.Inflate(0, 1);
                using (LinearGradientBrush gold = new LinearGradientBrush(tb, Color.White, Color.Black, LinearGradientMode.Vertical))
                {
                    ColorBlend blend = new ColorBlend(3);
                    blend.Colors = Hot
                        ? new Color[] { Color.FromArgb(255, 226, 196), Color.FromArgb(255, 145, 80), Color.FromArgb(194, 56, 28) }
                        : new Color[] { Color.FromArgb(255, 243, 209), Color.FromArgb(243, 201, 110), Color.FromArgb(183, 127, 48) };
                    blend.Positions = new float[] { 0f, 0.52f, 1f };
                    gold.InterpolationColors = blend;
                    g.FillPath(gold, title);
                }
            }
            else
                using (SolidBrush b = new SolidBrush(Color.FromArgb(201, 196, 187))) g.FillPath(b, title);
            title.Dispose();

            // The TEST tag beside the title of a test alert.
            if (Test)
            {
                using (Font tagFont = Ui(F(11), true))
                using (SolidBrush text = new SolidBrush(Color.FromArgb(242, 207, 134)))
                using (SolidBrush back = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                using (Pen border = new Pen(Color.FromArgb(184, 138, 72), Math.Max(1f, F(1))))
                {
                    float tw = TrackedWidth(g, "TEST", tagFont, F(2.4f));
                    RectangleF tag = new RectangleF(TitleBounds.Right + F(12), TitleBounds.Top + (TitleBounds.Height - F(19)) / 2f, tw + F(16), F(19));
                    g.FillRectangle(back, tag);
                    g.DrawRectangle(border, tag.X, tag.Y, tag.Width, tag.Height);
                    DrawTrackedText(g, "TEST", tagFont, text, tag.X + F(8), tag.Y + (tag.Height - tagFont.GetHeight(g)) / 2f + F(0.5f), F(2.4f));
                }
            }

            // The line under the title. A notice's line holds a running time, so it is drawn per frame there.
            if (Live) DrawSubLine(g, lines, textLeft);

            // The clock: its digits stand on the same baseline as the line under the title, and the caption
            // keeps its distance above them. ClockBounds.Y + ClockDigitsTop is where the digits' line starts.
            if (lines[2] != null)
                using (Font cap = Ui(F(10), true))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(180, 154, 112)))
                {
                    // Half a pixel up: GDI+ puts whole coordinates at pixel centres when it fills an outline,
                    // so an outline ending exactly on the baseline would still half-cover the row below it.
                    float digitsTop = SubBaseline - DigitsAscent - 0.5f;
                    float capTop = digitsTop - ClockDigitsTop;
                    DrawTrackedText(g, lines[2], cap, b, ClockRight - captionWidth, capTop, F(2.4f));
                    ClockBounds = new RectangleF(ClockRight - clockWidth, capTop, clockWidth, F(40));
                }

            // Close button.
            Color x = HoverClose ? Color.FromArgb(242, 207, 134) : !Live ? Color.FromArgb(143, 138, 130) : Color.FromArgb(179, 155, 116);
            using (Pen p = new Pen(x, F(2)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                float pad = F(7);
                g.DrawLine(p, CloseRect.Left + pad, CloseRect.Top + pad, CloseRect.Right - pad, CloseRect.Bottom - pad);
                g.DrawLine(p, CloseRect.Right - pad, CloseRect.Top + pad, CloseRect.Left + pad, CloseRect.Bottom - pad);
            }
        }

        void DrawSubLine(Graphics g, string[] lines, float left)
        {
            float y = (float)Math.Round(TitleBounds.Bottom + F(8));   // a whole pixel, so DrawnBaseline holds
            Color mapColor = Live ? Color.FromArgb(248, 230, 196) : Color.FromArgb(214, 209, 200);
            Color restColor = Live ? Color.FromArgb(226, 214, 193) : Color.FromArgb(182, 177, 168);
            Font map = MapFont, rest = RestFont;
            using (SolidBrush mb = new SolidBrush(mapColor))
            using (SolidBrush rb = new SolidBrush(restColor))
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                float x = left;
                if (lines[0].Length > 0) x = DrawWords(g, lines[0], map, mb, x, y, sf);
                x = DrawWords(g, lines[1], rest, rb, x, y, sf);
                SubBounds = new RectangleF(left, y, x - left, rest.GetHeight(g));
                SubBaseline = y + RestBaseline;
            }
        }

        // Word by word with a space of 0.3 em: grid-fitted text squeezes a small space to 2 or 3 pixels.
        // Each space in the text, doubled ones included, becomes one such gap.
        public static float DrawWords(Graphics g, string text, Font font, Brush brush, float x, float y, StringFormat sf)
        {
            float space = font.Size * 0.3f;
            string[] words = text.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (i > 0) x += space;
                if (words[i].Length == 0) continue;
                g.DrawString(words[i], font, brush, x, y, sf);
                x += g.MeasureString(words[i], font, PointF.Empty, sf).Width;
            }
            return x;
        }

        void DrawInnerLine(Graphics g, double strength)
        {
            float inset = BorderWidth + F(4) + F(0.5f);
            Color c;
            if (!Live) c = Color.FromArgb(64, 160, 155, 146);
            else if (Hot) c = Color.FromArgb((int)(102 + 102 * strength), 255, (int)(120 + 30 * strength), (int)(60 + 30 * strength));
            else c = Color.FromArgb((int)(89 + 90 * strength), (int)(217 + 23 * strength), (int)(169 + 31 * strength), (int)(87 + 33 * strength));
            using (Pen p = new Pen(c, Math.Max(1f, F(1))))
                g.DrawRectangle(p, Frame.X + inset, Frame.Y + inset, Frame.Width - 2 * inset, Frame.Height - 2 * inset);
        }

        void EnsureLayers()
        {
            string key = Title + "|" + Stage + "|" + Notice + "|" + Test + "|" + Map + "|" + HoverClose;
            if (_base == null || key != _baseKey)
            {
                if (_base == null) _base = new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(_base)) DrawBase(g);
                _baseKey = key;
            }
            string glowKey = Hot ? "hot" : "warm";
            if (Live && (_glow == null || glowKey != _glowKey))
            {
                if (_glow == null) _glow = new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppPArgb);
                _glowKey = glowKey;
            }
        }

        static void DrawFaded(Graphics g, Bitmap image, float alpha, float brightness)
        {
            ColorMatrix m = new ColorMatrix();
            m.Matrix00 = m.Matrix11 = m.Matrix22 = brightness;
            m.Matrix33 = alpha;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(m);
                g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, ia);
            }
        }

        // One frame. ms: time since the popup appeared; elapsed: what the clock shows.
        public void Draw(Graphics g, double ms, TimeSpan elapsed)
        {
            EnsureLayers();
            g.CompositingMode = CompositingMode.SourceCopy;
            using (SolidBrush clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, 0, 0, Size.Width, Size.Height);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            double glow = Live ? Glow : 0;
            if (Live)
            {
                UpdateFire(ms);
                g.DrawImage(_glow, new Rectangle(0, 0, _glow.Width, _glow.Height));
            }
            if (ms < EntryMs) DrawFaded(g, _base, 1f, (float)EntryBrightness(ms));
            else g.DrawImage(_base, new Rectangle(0, 0, _base.Width, _base.Height));
            if (Live) DrawInnerLine(g, glow);

            string[] lines = Lines(Stage, Test, Map, elapsed);
            if (lines[3] != null)
            {
                Font digits = DigitsFont;
                using (SolidBrush b = new SolidBrush(Hot ? Color.FromArgb(255, 176, 122) : Color.FromArgb(242, 207, 134)))
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                {
                    MeasureClock(g, digits);
                    using (GraphicsPath p = ClockPath(g, lines[3], digits, ClockRight, ClockBounds.Y + ClockDigitsTop))
                    {
                        using (Matrix m = new Matrix())
                        {
                            m.Translate(0, F(1));
                            p.Transform(m);
                            g.FillPath(shadow, p);
                            m.Reset();
                            m.Translate(0, -F(1));
                            p.Transform(m);
                        }
                        g.FillPath(b, p);
                    }
                }
            }
            if (!Live) DrawSubLine(g, lines, CrestBox.Right + F(18));
        }

        public void Dispose()
        {
            if (_base != null) { _base.Dispose(); _base = null; }
            if (_glow != null) { _glow.Dispose(); _glow = null; }
            if (_digitsFont != null) { _digitsFont.Dispose(); _digitsFont = null; }
            if (_mapFont != null) { _mapFont.Dispose(); _mapFont = null; }
            if (_restFont != null) { _restFont.Dispose(); _restFont = null; }
        }
    }
}
