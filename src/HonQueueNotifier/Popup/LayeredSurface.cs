using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HonQueueNotifier
{
    // A window-sized bitmap whose pixels go straight to a layered window, alpha and all.
    sealed class LayeredSurface : IDisposable
    {
        public readonly Bitmap Bitmap;
        readonly int _width, _height;
        IntPtr _dc, _dib, _old;

        public LayeredSurface(int width, int height)
        {
            _width = width;
            _height = height;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            _dc = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            Native.BITMAPINFOHEADER bi = new Native.BITMAPINFOHEADER();
            bi.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.biWidth = width;
            bi.biHeight = -height;   // top-down, like a Bitmap
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            IntPtr bits;
            _dib = Native.CreateDIBSection(_dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero) { Native.DeleteDC(_dc); _dc = IntPtr.Zero; throw new InvalidOperationException("CreateDIBSection failed"); }
            _old = Native.SelectObject(_dc, _dib);
            Bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        }

        public bool Present(IntPtr hwnd, Point topLeft, byte opacity)
        {
            Native.POINT dst = new Native.POINT();
            dst.X = topLeft.X;
            dst.Y = topLeft.Y;
            Native.POINT src = new Native.POINT();
            Native.SIZE size = new Native.SIZE();
            size.CX = _width;
            size.CY = _height;
            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.SourceConstantAlpha = opacity;
            blend.AlphaFormat = 1;   // AC_SRC_ALPHA: the bitmap carries its own (premultiplied) alpha
            return Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, _dc, ref src, 0, ref blend, 2);
        }

        public void Dispose()
        {
            if (Bitmap != null) Bitmap.Dispose();
            if (_dc != IntPtr.Zero)
            {
                Native.SelectObject(_dc, _old);
                Native.DeleteObject(_dib);
                Native.DeleteDC(_dc);
                _dc = IntPtr.Zero;
            }
        }
    }
}
