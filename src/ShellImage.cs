using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DesktopPortal
{
    internal static class ShellImage
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ImageFactory { [PreserveSig] int GetImage(Size size, uint flags, out IntPtr bitmap); }
        [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { internal int Type, Width, Height, WidthBytes; internal ushort Planes, BitsPixel; internal IntPtr Bits; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader { internal uint Size; internal int Width, Height; internal ushort Planes, BitCount; internal uint Compression, ImageSize; internal int XPels, YPels; internal uint Used, Important; }
        [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string name, IntPtr bind, ref Guid iid, out ImageFactory item);
        [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr bitmap, int size, out NativeBitmap value);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint rows, byte[] bits, ref BitmapHeader header, uint usage);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        internal static Bitmap Get(string path, int pixels)
        {
            ImageFactory factory = null; IntPtr handle = IntPtr.Zero;
            try
            {
                Guid iid = typeof(ImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) >= 0 &&
                    factory.GetImage(new Size(pixels, pixels), 0, out handle) >= 0 && handle != IntPtr.Zero)
                {
                    using (Bitmap source = ReadBitmap(handle)) return Resize(source, pixels);
                }
            }
            catch { }
            finally { if (handle != IntPtr.Zero) DeleteObject(handle); if (factory != null) Marshal.FinalReleaseComObject(factory); }
            try { using (Icon icon = ShellIcon.Get(path)) using (Bitmap source = icon.ToBitmap()) return Resize(source, pixels); }
            catch { return null; }
        }

        private static Bitmap ReadBitmap(IntPtr handle)
        {
            NativeBitmap native; GetObjectW(handle, Marshal.SizeOf(typeof(NativeBitmap)), out native);
            if (native.Width <= 0 || native.Height <= 0 || native.Width > 4096 || native.Height > 4096) throw new InvalidOperationException();
            var header = new BitmapHeader { Size=40, Width=native.Width, Height=-native.Height, Planes=1, BitCount=32 };
            byte[] data = new byte[native.Width * native.Height * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, handle, 0, (uint)native.Height, data, ref header, 0) == 0) throw new InvalidOperationException(); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            bool alpha = false; for (int i=3; i<data.Length; i+=4) if (data[i]!=0) { alpha=true; break; }
            if (!alpha) for (int i=3; i<data.Length; i+=4) data[i]=255;
            var image = new Bitmap(native.Width, native.Height, PixelFormat.Format32bppPArgb);
            BitmapData locked = image.LockBits(new Rectangle(Point.Empty,image.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { for (int y=0;y<image.Height;y++) Marshal.Copy(data,y*image.Width*4,IntPtr.Add(locked.Scan0,y*locked.Stride),image.Width*4); }
            finally { image.UnlockBits(locked); }
            return image;
        }

        private static Bitmap Resize(Bitmap source, int pixels)
        {
            double scale = pixels / (double)Math.Max(source.Width, source.Height);
            var result = new Bitmap(Math.Max(1,(int)Math.Round(source.Width*scale)), Math.Max(1,(int)Math.Round(source.Height*scale)), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(result)) { g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.DrawImage(source,new Rectangle(Point.Empty,result.Size)); }
            return result;
        }
    }
}
