using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HonQueueNotifier
{
    // =====================================================================================
    // The program's icon: a phoenix with raised wings, adapted from OpenMoji's phoenix emoji (CC BY-SA 4.0;
    // see assets/app-icon.svg and the README): one colour with its outlines cut out, so it stays clear at
    // 16 pixels. In the tray it is white on a dark taskbar and black on a light one, like the icons around
    // it; windows show it in the program's orange. It does not change while an alert is up.
    // =====================================================================================
    static class AppIcon
    {
        public static readonly Color Orange = Color.FromArgb(255, 122, 26);

        // The glyph in assets/app-icon.svg: one path on a 256 x 256 box, written by tools/assets/make_icon.ps1
        // with absolute M, L, C and Z commands only.
        public static GraphicsPath Glyph()
        {
            string svg = Encoding.UTF8.GetString(AppFiles.Resource("app-icon.svg"));
            Match d = Regex.Match(svg, @"<path[^>]*\sd=""([^""]*)""");
            if (!d.Success) throw new InvalidDataException("app-icon.svg has no path");
            return ParsePath(d.Groups[1].Value, svg.Contains("fill-rule=\"evenodd\"") ? FillMode.Alternate : FillMode.Winding);
        }

        public static GraphicsPath ParsePath(string d, FillMode mode)
        {
            GraphicsPath path = new GraphicsPath(mode);
            PointF at = PointF.Empty;
            foreach (Match m in Regex.Matches(d, "([MLCZ])([^MLCZ]*)"))
            {
                float[] v = Array.ConvertAll(m.Groups[2].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries),
                    s => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture));
                switch (m.Groups[1].Value[0])
                {
                    case 'M': path.StartFigure(); at = new PointF(v[0], v[1]); break;
                    case 'L': { PointF to = new PointF(v[0], v[1]); path.AddLine(at, to); at = to; break; }
                    case 'C': { PointF to = new PointF(v[4], v[5]); path.AddBezier(at, new PointF(v[0], v[1]), new PointF(v[2], v[3]), to); at = to; break; }
                    case 'Z': path.CloseFigure(); break;
                }
            }
            return path;
        }

        // The glyph filling a square of the given size, in one colour. The soft edge keeps only its
        // transparency: GDI+ rounds the colour of faint edge pixels, which would show as a grey fringe.
        public static Bitmap Draw(int size, Color color)
        {
            Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (GraphicsPath glyph = Glyph())
            using (Graphics g = Graphics.FromImage(b))
            using (Matrix m = new Matrix())
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                m.Scale(size / 256f, size / 256f);
                g.Transform = m;
                g.FillPath(brush, glyph);
            }
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                {
                    int a = b.GetPixel(x, y).A;
                    if (a > 0) b.SetPixel(x, y, Color.FromArgb(a, color));
                }
            return b;
        }

        const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        // Whether the taskbar is light (Windows 10 1903 and later can have one). Without the setting it is dark.
        public static bool LightTaskbar()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ThemeKey, false))
                    return IsLight(k == null ? null : k.GetValue("SystemUsesLightTheme"));
            }
            catch (Exception) { return false; }
        }

        public static bool IsLight(object systemUsesLightTheme)
        {
            return systemUsesLightTheme is int && (int)systemUsesLightTheme == 1;
        }

        // For the tray, at the size Windows shows it (16 pixels at 100 %, 24 at 150 %).
        public static Icon Tray(bool lightTaskbar)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (Bitmap b = Draw(size, lightTaskbar ? Color.Black : Color.White))
            {
                IntPtr h = b.GetHicon();
                try { using (Icon temp = Icon.FromHandle(h)) return (Icon)temp.Clone(); }
                finally { if (Native.IsWindows) Native.DestroyIcon(h); }
            }
        }

        public static readonly int[] WindowSizes = { 16, 20, 24, 32, 40, 48, 64 };

        // For windows: in orange, drawn at every size Windows asks for (title bar, taskbar, Alt+Tab, at
        // any display scale), so none of them is a blurred resize of another.
        public static Icon Window()
        {
            using (MemoryStream ico = new MemoryStream(Ico(WindowSizes, false))) return new Icon(ico);
        }

        // For the exe file in Explorer: the window icon's images plus 256 pixels for the large views, stored
        // as PNG as Windows expects at that size. tools/assets/make_exe_icon.ps1 writes it to
        // assets/app-icon.ico, which the build puts into the exe.
        public static byte[] FileIcon()
        {
            List<int> sizes = new List<int>(WindowSizes) { 256 };
            return Ico(sizes.ToArray(), true);
        }

        // An .ico file with the orange glyph at each size; 256 pixels as PNG when asked.
        static byte[] Ico(int[] sizes, bool png256)
        {
            List<byte[]> images = new List<byte[]>();
            foreach (int s in sizes) using (Bitmap b = Draw(s, Orange)) images.Add(s == 256 && png256 ? Png(b) : IconImage(b));
            using (MemoryStream ico = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ico);
                w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
                int offset = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    w.Write((byte)(sizes[i] & 255)); w.Write((byte)(sizes[i] & 255)); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32); w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
                w.Flush();
                return ico.ToArray();
            }
        }

        static byte[] Png(Bitmap b)
        {
            using (MemoryStream m = new MemoryStream()) { b.Save(m, ImageFormat.Png); return m.ToArray(); }
        }

        // One image of an .ico file: a 32-bit bitmap with its alpha, bottom row first, and an empty mask.
        static byte[] IconImage(Bitmap b)
        {
            int s = b.Width, maskStride = (s + 31) / 32 * 4;
            using (MemoryStream m = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(m);
                w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
                w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = s - 1; y >= 0; y--)
                    for (int x = 0; x < s; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }
                w.Write(new byte[maskStride * s]);
                w.Flush();
                return m.ToArray();
            }
        }
    }
}
