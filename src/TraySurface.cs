using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace ScreenCompanion
{
 internal sealed class TrayPixelBuffer : IDisposable
 {
  internal byte[] Input,Output;
  internal long Bytes { get { return Input==null?0:(long)Input.Length+Output.Length; } }
  internal void Get(int size,out byte[] input,out byte[] output)
  {
   if((long)size*2>8L*1024*1024){Dispose();input=new byte[size];output=new byte[size];return;}
   if(Input==null || Input.Length!=size){Input=new byte[size];Output=new byte[size];}
   input=Input;output=Output;
  }
  public void Dispose(){Input=Output=null;}
 }
 internal static class TraySurface
 {
  // Windows 11 supplies composited pixels. Estimate acrylic from glyph-free
  // margins and reconstruct a premultiplied alpha layer for the secondary bar.
  internal static Bitmap SeparateBackground(Bitmap source)
  {return SeparateBackground(source,null);}
  internal static Bitmap SeparateBackground(Bitmap source,TrayPixelBuffer buffer)
  {
   var result=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppPArgb);
   var rect=new Rectangle(Point.Empty,source.Size);
   var read=source.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
   var write=result.LockBits(rect,ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
   byte[] input,output;int length=checked(source.Width*source.Height*4);
   if(buffer==null){input=new byte[length];output=new byte[length];}else buffer.Get(length,out input,out output);
   try
   {
    for(int y=0;y<source.Height;y++)Marshal.Copy(IntPtr.Add(read.Scan0,y*read.Stride),input,y*source.Width*4,source.Width*4);
    // Only three tiny arrays per frame, rather than one array for every pixel.
    double[] top=new double[3],bottom=new double[3],background=new double[3];
    for(int x=0;x<source.Width;x++)
    {
     Array.Clear(top,0,3);Array.Clear(bottom,0,3);int count=0;
     for(int xx=Math.Max(0,x-1);xx<=Math.Min(source.Width-1,x+1);xx++)for(int yy=2;yy<=Math.Min(4,source.Height/4);yy++)
     {for(int c=0;c<3;c++){top[c]+=input[(yy*source.Width+xx)*4+c];bottom[c]+=input[((source.Height-1-yy)*source.Width+xx)*4+c];}count++;}
     if(count==0)count=1;
     for(int y=0;y<source.Height;y++)
     {
      int i=(y*source.Width+x)*4;double t=Math.Max(0,Math.Min(1,(y-3.0)/Math.Max(1,source.Height-7))),alpha=0,diff=0;
      for(int c=0;c<3;c++){double b=(top[c]*(1-t)+bottom[c]*t)/count; background[c]=b;double d=input[i+c]-b;diff=Math.Max(diff,Math.Abs(d));alpha=Math.Max(alpha,d>=0?d/Math.Max(1,255-b):-d/Math.Max(1,b));}
      if(diff<9)alpha=0;alpha=Math.Min(1,Math.Max(0,alpha));byte a=(byte)Math.Round(alpha*255);output[i+3]=a;
      for(int c=0;c<3;c++)output[i+c]=(byte)Math.Max(0,Math.Min(a,input[i+c]-(1-alpha)*background[c]));
     }
    }
    for(int y=0;y<source.Height;y++)Marshal.Copy(output,y*source.Width*4,IntPtr.Add(write.Scan0,y*write.Stride),source.Width*4);
   }
   finally{source.UnlockBits(read);result.UnlockBits(write);}
   return result;
  }
  [StructLayout(LayoutKind.Sequential)]private struct Blend{internal byte Op,Flags,Alpha,Format;}
  [DllImport("user32.dll")]private static extern bool UpdateLayeredWindow(IntPtr window,IntPtr destination,ref Point position,ref Size size,IntPtr source,ref Point origin,int key,ref Blend blend,uint flags);
  [DllImport("user32.dll")]private static extern IntPtr GetDC(IntPtr window);
  [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr window,IntPtr dc);
  [DllImport("gdi32.dll")]private static extern IntPtr CreateCompatibleDC(IntPtr dc);
  [DllImport("gdi32.dll")]private static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
  [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr value);
  [DllImport("gdi32.dll")]private static extern bool DeleteDC(IntPtr dc);
  internal static void Present(IntPtr window,Bitmap image,Point position)
  {
   IntPtr dc=GetDC(IntPtr.Zero),memory=CreateCompatibleDC(dc),bitmap=image.GetHbitmap(Color.FromArgb(0)),old=SelectObject(memory,bitmap);
   try{Size size=image.Size;Point origin=Point.Empty;var blend=new Blend{Alpha=255,Format=1};if(!UpdateLayeredWindow(window,dc,ref position,ref size,memory,ref origin,0,ref blend,2))throw new System.ComponentModel.Win32Exception();}
   finally{SelectObject(memory,old);DeleteObject(bitmap);DeleteDC(memory);ReleaseDC(IntPtr.Zero,dc);}
  }
 }
}
