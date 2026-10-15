using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace ScreenCompanion
{
 internal sealed class TrayPixelBuffer : IDisposable
 {
  internal byte[] Input,Output,Background;
  internal long Bytes { get { return Input==null?0:(long)Input.Length+Output.Length+(Background==null?0:Background.Length); } }
  internal void Get(int size,out byte[] input,out byte[] output)
  {
   if((long)size*2>8L*1024*1024){Dispose();input=new byte[size];output=new byte[size];return;}
   if(Input==null || Input.Length!=size){Input=new byte[size];Output=new byte[size];Background=null;}
   input=Input;output=Output;
  }
  internal byte[] GetBackground(int size)
  {
   if((long)size*3>8L*1024*1024){Background=null;return new byte[size];}
   if(Background==null || Background.Length!=size)Background=new byte[size];return Background;
  }
  public void Dispose(){Input=Output=Background=null;}
 }
 internal static class TraySurface
 {
  // Windows 11 supplies composited pixels. Estimate acrylic from glyph-free
  // margins and reconstruct a premultiplied alpha layer for the secondary bar.
  internal static Bitmap SeparateBackground(Bitmap source)
  {return SeparateBackground(source,null);}
  internal static Bitmap SeparateBackground(Bitmap source,TrayPixelBuffer buffer)
  {return SeparateBackground(source,buffer,null);}
  internal static Bitmap SeparateBackground(Bitmap source,TrayPixelBuffer buffer,Bitmap wallpaper)
  {
   var result=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppPArgb);
   var rect=new Rectangle(Point.Empty,source.Size);
   try
   {
    BitmapData read=null,write=null;
    try
    {
    read=source.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
    write=result.LockBits(rect,ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
    byte[] input,output;int length=checked(source.Width*source.Height*4);
    if(buffer==null){input=new byte[length];output=new byte[length];}else buffer.Get(length,out input,out output);
    for(int y=0;y<source.Height;y++)Marshal.Copy(IntPtr.Add(read.Scan0,y*read.Stride),input,y*source.Width*4,source.Width*4);
    byte[] reference=null;WallpaperMatting model=null;
    if(wallpaper!=null && wallpaper.Size==source.Size)
    {
     reference=buffer==null?new byte[length]:buffer.GetBackground(length);
     var data=wallpaper.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
     try{for(int y=0;y<source.Height;y++)Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),reference,y*source.Width*4,source.Width*4);}finally{wallpaper.UnlockBits(data);}
     model=WallpaperMatting.Fit(input,reference,source.Width,source.Height);
    }
    // The row interpolation is identical for every column. Compute it once;
    // keep the original arithmetic order so antialiased glyph bytes are stable.
    var rowBlend=new double[source.Height];
    for(int y=0;y<source.Height;y++)rowBlend[y]=Math.Max(0,Math.Min(1,(y-3.0)/Math.Max(1,source.Height-7)));
    double[] top=new double[3],bottom=new double[3],background=new double[3];
    for(int x=0;x<source.Width;x++)
    {
     Array.Clear(top,0,3);Array.Clear(bottom,0,3);int count=0;
     for(int xx=Math.Max(0,x-1);xx<=Math.Min(source.Width-1,x+1);xx++)for(int yy=2;yy<=Math.Min(4,source.Height/4);yy++)
     {for(int c=0;c<3;c++){top[c]+=input[(yy*source.Width+xx)*4+c];bottom[c]+=input[((source.Height-1-yy)*source.Width+xx)*4+c];}count++;}
     if(count==0)count=1;
     for(int y=0;y<source.Height;y++)
     {
      int i=(y*source.Width+x)*4;double t=rowBlend[y],alpha=0,diff=0;
      for(int c=0;c<3;c++)
      {
       double b;
       if(model==null)b=(top[c]*(1-t)+bottom[c]*t)/count;
       else{b=reference[i+c]*model.Scale+model.Offset[c];if(b<0)b=0;else if(b>255)b=255;}
       background[c]=b;double d=input[i+c]-b,absolute=d<0?-d:d;if(absolute>diff)diff=absolute;
       double denominator=d>=0?255-b:b;if(denominator<1)denominator=1;
       double candidate=absolute/denominator;if(candidate>alpha)alpha=candidate;
      }
      if(diff<9)alpha=0;else if(alpha>1)alpha=1;
      byte a=(byte)Math.Round(alpha*255);output[i+3]=a;
      for(int c=0;c<3;c++){double value=input[i+c]-(1-alpha)*background[c];output[i+c]=(byte)(value<0?0:value>a?a:value);}
     }
    }
    for(int y=0;y<source.Height;y++)Marshal.Copy(output,y*source.Width*4,IntPtr.Add(write.Scan0,y*write.Stride),source.Width*4);
    }
    finally{if(read!=null)source.UnlockBits(read);if(write!=null)result.UnlockBits(write);}
    return result;
   }
   catch{result.Dispose();throw;}
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
