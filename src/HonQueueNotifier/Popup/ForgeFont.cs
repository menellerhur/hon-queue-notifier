using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace HonQueueNotifier
{
    // assets/popup-font.ttf: a static weight-700 instance of Cinzel with overlapping contours merged
    // (GDI+ would otherwise fill the overlaps as holes, as in 4 and 8), cut down to printable ASCII.
    // SIL Open Font License 1.1, copyright 2020 The Cinzel Project Authors; docs/licenses/Cinzel-OFL.txt.
    static class ForgeFont
    {
        static PrivateFontCollection _fonts;
        static FontFamily _family;
        internal static int Created;   // fonts made so far; the self-test checks a waiting popup makes none per frame
        static IntPtr _memory;   // AddMemoryFont needs the bytes to stay where they are
        static bool _tried;

        public static FontFamily Family
        {
            get
            {
                if (!_tried)
                {
                    _tried = true;
                    try
                    {
                        byte[] bytes = AppFiles.Resource("popup-font.ttf");
                        _memory = Marshal.AllocCoTaskMem(bytes.Length);
                        Marshal.Copy(bytes, 0, _memory, bytes.Length);
                        PrivateFontCollection fonts = new PrivateFontCollection();
                        fonts.AddMemoryFont(_memory, bytes.Length);
                        _fonts = fonts;
                        if (fonts.Families.Length > 0) _family = fonts.Families[0];   // Families makes a new array each time
                    }
                    catch (Exception ex) { AppLog.Write("could not load the popup font, using Georgia: {0}", ex.Message); }
                }
                return _family;
            }
        }

        public static Font Create(float pixels)
        {
            Created++;
            FontFamily f = Family;
            if (f != null && f.IsStyleAvailable(FontStyle.Bold)) return new Font(f, pixels, FontStyle.Bold, GraphicsUnit.Pixel);
            return new Font("Georgia", pixels, FontStyle.Bold, GraphicsUnit.Pixel);
        }
    }
}
