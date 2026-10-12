using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace HonQueueNotifier
{
    // The popup ("Forge" design): dark stone, bronze trim, a gold serif title, a warm glow. ForgeFont
    // holds the title font, ForgePainter draws everything with GDI+ into a transparent bitmap,
    // LayeredSurface puts that bitmap on screen with per-pixel alpha, and AlertForm is the window
    // around them.
    sealed class AlertForm : Form
    {
        public event EventHandler OpenRequested;
        public event EventHandler CloseRequested;

        readonly float _scale;
        readonly ForgePainter _painter;
        readonly WinTimer _timer;
        readonly Point _home;
        LayeredSurface _surface;
        DateTime _shownUtc = DateTime.UtcNow;
        DateTime _sinceUtc = DateTime.UtcNow;
        bool _presentFailed;

        public AlertForm(Rectangle area, string position, float scale)
        {
            _scale = scale <= 0 ? 1f : scale;
            Text = "HoN Queue Notifier";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Cursor = Cursors.Hand;
            int width = Math.Min(S(ForgePainter.FrameWidth), Math.Max(S(200), area.Width - S(32)));
            _painter = new ForgePainter(_scale, width);
            Size = _painter.Size;
            Point frame = Place(area, _painter.Frame.Size, position, S(28));
            _home = new Point(frame.X - _painter.Frame.X, frame.Y - _painter.Frame.Y);
            Location = _home;
            _timer = new WinTimer();
            _timer.Interval = 40;
            _timer.Tick += delegate { Present(); };
        }

        int S(int v) { return (int)Math.Round(v * _scale); }

        public static Point Place(Rectangle area, Size size, string position, int margin)
        {
            string p = (position ?? "").Trim().ToLowerInvariant();
            int x = area.Left + (area.Width - size.Width) / 2;
            int y = area.Top + margin;
            if (p.Contains("left")) x = area.Left + margin;
            else if (p.Contains("right")) x = area.Right - size.Width - margin;
            if (p.StartsWith("bottom", StringComparison.Ordinal)) y = area.Bottom - size.Height - margin;
            else if (p == "center" || p == "middle") y = area.Top + (area.Height - size.Height) / 2;
            return new Point(x, y);
        }

        // Appear above everything without taking keyboard focus away from whatever you are doing.
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000008 | 0x00000080 | 0x00080000;   // WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED
                return cp;
            }
        }

        public bool Animating { get { return _timer.Enabled; } }

        public void SetContent(string title, AlertStage stage, NoticeReason notice, bool test, string map, DateTime sinceUtc)
        {
            _painter.Title = title ?? "";
            _painter.Stage = stage;
            _painter.Notice = notice;
            _painter.Test = test;
            _painter.Map = map;
            _sinceUtc = sinceUtc;
            Present();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                _shownUtc = DateTime.UtcNow;
                Present();
                _timer.Start();
            }
            else _timer.Stop();   // nothing animates while hidden
        }

        void Present()
        {
            if (!IsHandleCreated || IsDisposed || !Visible) return;
            try
            {
                double ms = (DateTime.UtcNow - _shownUtc).TotalMilliseconds;
                if (_surface == null) _surface = new LayeredSurface(Width, Height);
                using (Graphics g = Graphics.FromImage(_surface.Bitmap)) _painter.Draw(g, ms, DateTime.UtcNow - _sinceUtc);
                int dy = (int)Math.Round(ForgePainter.EntryOffset(ms) * _scale);
                byte opacity = (byte)Math.Round(255 * ForgePainter.EntryOpacity(ms));
                if (!_surface.Present(Handle, new Point(_home.X, _home.Y + dy), opacity) && !_presentFailed)
                {
                    _presentFailed = true;
                    AppLog.Write("could not draw the popup: UpdateLayeredWindow error {0}", Marshal.GetLastWin32Error());
                }
                // About 60 frames a second while it slides in, 30 while a waiting alert's fire flickers,
                // 4 for a notice, whose only change is its clock.
                int interval = ForgePainter.Moving(ms) ? 15 : _painter.Stage != AlertStage.Over ? 33 : 250;
                if (_timer.Interval != interval) _timer.Interval = interval;
            }
            catch (Exception ex)
            {
                if (!_presentFailed) AppLog.Write("could not draw the popup: {0}", ex.Message);
                _presentFailed = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = _painter.CloseRect.Contains(e.Location);
            if (hover != _painter.HoverClose) { _painter.HoverClose = hover; Present(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_painter.HoverClose) { _painter.HoverClose = false; Present(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            EventHandler handler = (e.Button == MouseButtons.Left && !_painter.CloseRect.Contains(e.Location)) ? OpenRequested : CloseRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null) { _timer.Stop(); _timer.Dispose(); }
                if (_surface != null) { _surface.Dispose(); _surface = null; }
                _painter.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
