using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ScreenCompanion
{
    // Local processing benchmark, with byte digests recorded from v1.1.4.
    // It never starts a companion form, installs hooks, or injects input.
    internal static class LatencyCheck
    {
        private static int checks;
        private static bool record;
        [STAThread]static int Main(string[] args)
        {
            record=args.Length==1 && args[0]=="--record-baseline";
            try
            {
                Case(520,48,false,"02B9401BB1A6A31037E974BED3673E903F29AF1276B2108A473C0BE4A519B716");
                Case(520,48,true,"4308ECBDD60D0522952EAFD544ABCF79E1E521069F3E1D96F9DB1365F4BEF69F");
                Case(780,72,true,"0E2D7013239D32771BB6C66949C765C6AE8F152D47EA00A0FACCE44C6779FF98");
                Case(1200,96,true,"0689645D82398FF130343D0B5F51F3A3CA71D0A266F04A8537ADBA24DB4574EA");
                Case(1,1,false,"A9EB0854E6B67E5D37C92B17C61902C2876A42C1FB4DBF60F84E0B7E1C442B51");
                Case(2,12,true,"2EA9AB9198D1638007400CD2C3BEF1CC745B864B76011A0E1BC52180AC6452D4");
                Buffers();FailureCleanup();
                Console.WriteLine("PASS: "+checks+" latency/visual checks. Timings cover local matting only, excluding screenshot, native input, and other machines.");
                return 0;
            }
            catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
        }
        private static Bitmap Pattern(int width,int height)
        {
            var bitmap=new Bitmap(width,height,PixelFormat.Format32bppPArgb);
            using(var g=Graphics.FromImage(bitmap))g.Clear(Color.Black);
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                bitmap.SetPixel(x,y,Color.FromArgb(25+(x*7+y*13)%190,30+(x*11+y*3)%180,20+(x*3+y*17)%200));
            return bitmap;
        }
        private static void Case(int width,int height,bool useWallpaper,string expected)
        {
            using(var wallpaper=Pattern(width,height))using(var source=(Bitmap)wallpaper.Clone())using(var buffer=new TrayPixelBuffer())
            {
                if(useWallpaper)
                    for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                    {
                        Color c=source.GetPixel(x,y);
                        source.SetPixel(x,y,Color.FromArgb((int)Math.Round(c.R*.7+25),(int)Math.Round(c.G*.7+15),(int)Math.Round(c.B*.7+10)));
                    }
                else using(var g=Graphics.FromImage(source))g.Clear(Color.FromArgb(90,110,130));
                // Include full-color and partially covered pixels well inside
                // glyph-free fit margins, to catch antialias rounding drift.
                if(width>20 && height>20)
                    for(int y=height/3;y<height*2/3;y++)for(int x=12;x<width-12;x+=17)
                    {
                        Color c=source.GetPixel(x,y);double a=((x+y)%9+1)/10.0;
                        source.SetPixel(x,y,Color.FromArgb((int)(c.R*(1-a)+255*a),(int)(c.G*(1-a)),(int)(c.B*(1-a)+100*a)));
                    }
                string unchanged=Digest(source),digest;
                Bitmap reference=useWallpaper?wallpaper:null;
                using(var frame=TraySurface.SeparateBackground(source,buffer,reference))digest=Digest(frame);
                string label=width+"x"+height+(useWallpaper?" wallpaper":" fallback");
                Console.WriteLine("Digest "+label+": "+digest);
                if(!record)Check(digest==expected,label+" matches every baseline output byte");
                using(var frame=TraySurface.SeparateBackground(source,null,reference))Check(Digest(frame)==digest,label+" reusable and uncached outputs agree");
                Check(Digest(source)==unchanged,label+" source pixels unchanged");
                if(width<20)return;
                for(int i=0;i<8;i++)using(var frame=TraySurface.SeparateBackground(source,buffer,reference)){}
                var samples=new double[100];int collections=GC.CollectionCount(0);
                for(int i=0;i<samples.Length;i++)
                {
                    var clock=Stopwatch.StartNew();using(var frame=TraySurface.SeparateBackground(source,buffer,reference)){}clock.Stop();
                    samples[i]=clock.Elapsed.TotalMilliseconds;
                }
                Array.Sort(samples);
                Console.WriteLine("Matting "+label+": median="+samples[50].ToString("F3")+" ms p95="+samples[94].ToString("F3")+" ms max="+samples[99].ToString("F3")+" ms; Gen0="+(GC.CollectionCount(0)-collections)+"; retained="+buffer.Bytes+" bytes");
                Check(buffer.Bytes<=8L*1024*1024,label+" buffer remains bounded");
            }
        }
        private static string Digest(Bitmap image)
        {
            var rectangle=new Rectangle(Point.Empty,image.Size);
            var data=image.LockBits(rectangle,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try
            {
                var bytes=new byte[image.Width*image.Height*4];
                for(int y=0;y<image.Height;y++)Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),bytes,y*image.Width*4,image.Width*4);
                using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
            }
            finally{image.UnlockBits(data);}
        }
        private static void Buffers()
        {
            using(var buffer=new TrayPixelBuffer())
            {
                byte[] input,output;buffer.Get(1024,out input,out output);byte[] previous=input;
                buffer.Get(1024,out input,out output);Check(Object.ReferenceEquals(previous,input),"same-size input is reused");
                buffer.GetBackground(1024);Check(buffer.Bytes==3072,"wallpaper buffer is included in retained bytes");
                buffer.Get(4*1024*1024,out input,out output);buffer.GetBackground(4*1024*1024);
                Check(buffer.Bytes<=8L*1024*1024,"large wallpaper reference is transient at byte cap");
                buffer.Get(5*1024*1024,out input,out output);Check(buffer.Bytes==0,"oversize frame releases retained buffers");
                buffer.Get(64,out input,out output);buffer.Dispose();Check(buffer.Bytes==0,"disposal releases retained arrays");
            }
        }
        private static void FailureCleanup()
        {
            using(var source=Pattern(20,20))
            {
                var data=source.LockBits(new Rectangle(Point.Empty,source.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
                bool rejected=false;
                try{using(var unexpected=TraySurface.SeparateBackground(source)){} }
                catch(InvalidOperationException){rejected=true;}
                catch(ExternalException){rejected=true;}
                finally{source.UnlockBits(data);}
                Check(rejected,"already locked source is rejected");
                using(var next=TraySurface.SeparateBackground(source))Check(next.Size==source.Size,"source remains usable after failure");
            }
        }
        private static void Check(bool condition,string label)
        {if(!condition)throw new Exception(label);checks++;Console.WriteLine("OK: "+label);}
    }
}
