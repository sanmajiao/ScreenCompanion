using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenCompanion
{
    // Read the real per-monitor wallpaper on a background STA. Only its small
    // tray crop is retained; never change wallpaper or TranslucentTB settings.
    internal sealed class WallpaperBackdrop : IDisposable
    {
        internal sealed class Request
        {
            internal Rectangle Source,Monitor,Virtual;
            internal string Key { get { return Source+"|"+Monitor+"|"+Virtual; } }
        }
        [ComImport,Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            [PreserveSig]int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)]string monitor,[MarshalAs(UnmanagedType.LPWStr)]string path);
            [PreserveSig]int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)]string monitor,[MarshalAs(UnmanagedType.LPWStr)]out string path);
            [PreserveSig]int GetMonitorDevicePathAt(uint index,[MarshalAs(UnmanagedType.LPWStr)]out string monitor);
            [PreserveSig]int GetMonitorDevicePathCount(out uint count);
            [PreserveSig]int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)]string monitor,out Native.Rect rect);
            [PreserveSig]int SetBackgroundColor(uint color);
            [PreserveSig]int GetBackgroundColor(out uint color);
            [PreserveSig]int SetPosition(int position);
            [PreserveSig]int GetPosition(out int position);
        }
        private readonly object sync=new object();
        private readonly AutoResetEvent wake=new AutoResetEvent(false);
        private readonly Thread worker;
        private Request requested;
        private Bitmap cached;
        private string cachedKey,stamp;
        private int nextCheck;
        private bool pending,disposed;
        internal WallpaperBackdrop()
        {
            worker=new Thread(Run){IsBackground=true,Name="ScreenCompanion tray wallpaper reference"};worker.SetApartmentState(ApartmentState.STA);worker.Start();
        }
        internal Bitmap Get(Rectangle source,Rectangle monitor,Rectangle virtualScreen)
        {
            if(source.Width<=0 || source.Height<=0 || (long)source.Width*source.Height*4>1024*1024)return null;
            var request=new Request{Source=source,Monitor=monitor,Virtual=virtualScreen};
            lock(sync)
            {
                if(disposed)return null;
                // While decoding an earlier monitor crop, retain only the most
                // recent geometry. A topology/auto-hide change must not wait for
                // another poll and a second stale wallpaper load.
                if(pending)
                {if(requested.Key!=request.Key)requested=request;}
                else if(cachedKey!=request.Key || Environment.TickCount-nextCheck>=0)
                {requested=request;pending=true;wake.Set();}
                return cached!=null && cachedKey==request.Key?(Bitmap)cached.Clone():null;
            }
        }
        private void Run()
        {
            try
            {
                while(true)
                {
                    wake.WaitOne();Request request;lock(sync){if(disposed)return;request=requested;}
                    Bitmap next=null;string nextStamp=null;bool unchanged=false;
                    try
                    {
                        string path;int position;Color color;
                        if(ReadWallpaper(request.Monitor,out path,out position,out color))
                        {
                            nextStamp=request.Key+"|"+path+"|"+position+"|"+color.ToArgb()+"|"+(String.IsNullOrEmpty(path)?0:File.GetLastWriteTimeUtc(path).Ticks);
                            lock(sync)unchanged=nextStamp==stamp && cached!=null;
                            if(!unchanged)next=Render(path,position,color,request);
                        }
                    }
                    catch { /* A failed reference must not damage native tray input. */ }
                    lock(sync)
                    {
                        if(disposed){if(next!=null)next.Dispose();return;}
                        if(requested.Key!=request.Key)
                        {if(next!=null)next.Dispose();wake.Set();continue;}
                        if(!unchanged){if(cached!=null)cached.Dispose();cached=next;stamp=nextStamp;cachedKey=request.Key;}
                        pending=false;nextCheck=Environment.TickCount+3000;
                    }
                }
            }
            finally{wake.Dispose();}
        }
        private static bool ReadWallpaper(Rectangle bounds,out string path,out int position,out Color color)
        {
            path=null;position=0;color=Color.Black;object com=null;
            try
            {
                com=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")));
                var api=(IDesktopWallpaper)com;uint count;if(api.GetMonitorDevicePathCount(out count)<0)return false;
                string device=null;
                for(uint i=0;i<Math.Min(count,32);i++){string candidate;Native.Rect rect;if(api.GetMonitorDevicePathAt(i,out candidate)>=0 && api.GetMonitorRECT(candidate,out rect)>=0 && rect.Rectangle==bounds){device=candidate;break;}}
                if(device==null || api.GetPosition(out position)<0)return false;
                uint rgb;if(api.GetBackgroundColor(out rgb)>=0)color=Color.FromArgb((int)(rgb&255),(int)((rgb>>8)&255),(int)((rgb>>16)&255));
                if(api.GetWallpaper(device,out path)<0)return false;
                if(position==5 && String.IsNullOrEmpty(path))api.GetWallpaper(null,out path);
                return String.IsNullOrEmpty(path) || File.Exists(path);
            }
            finally{if(com!=null && Marshal.IsComObject(com))Marshal.ReleaseComObject(com);}
        }
        internal static Bitmap Render(string path,int position,Color color,Request request)
        {
            var result=new Bitmap(request.Source.Width,request.Source.Height,PixelFormat.Format32bppPArgb);
            try
            {
                using(var g=Graphics.FromImage(result))
                {
                    g.Clear(color);if(String.IsNullOrEmpty(path))return result;
                    using(var image=Image.FromFile(path))
                    {
                        Rectangle area=position==5?request.Virtual:request.Monitor;
                        g.TranslateTransform(area.Left-request.Source.Left,area.Top-request.Source.Top);
                        g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                        if(position==1)
                        {
                            // Tile uses one virtual-desktop origin, including negative monitors.
                            int left=request.Virtual.Left-area.Left,top=request.Virtual.Top-area.Top;
                            int startX=left+(int)Math.Floor((request.Source.Left-request.Virtual.Left)/(double)image.Width)*image.Width;
                            int startY=top+(int)Math.Floor((request.Source.Top-request.Virtual.Top)/(double)image.Height)*image.Height;
                            for(int y=startY;y<request.Source.Bottom-area.Top;y+=image.Height)for(int x=startX;x<request.Source.Right-area.Left;x+=image.Width)g.DrawImageUnscaled(image,x,y);
                        }
                        else
                        {
                            RectangleF destination=Destination(image.Size,area.Size,position);
                            using(var attributes=new ImageAttributes()){attributes.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(image,new[]{destination.Location,new PointF(destination.Right,destination.Top),new PointF(destination.Left,destination.Bottom)},new RectangleF(0,0,image.Width,image.Height),GraphicsUnit.Pixel,attributes);}
                        }
                    }
                }
                return result;
            }
            catch{result.Dispose();throw;}
        }
        internal static RectangleF Destination(Size image,Size screen,int position)
        {
            if(position==2)return new RectangleF(0,0,screen.Width,screen.Height);
            double scale=position==0?1:position==3?Math.Min(screen.Width/(double)image.Width,screen.Height/(double)image.Height):Math.Max(screen.Width/(double)image.Width,screen.Height/(double)image.Height);
            float width=(float)(image.Width*scale),height=(float)(image.Height*scale);return new RectangleF((screen.Width-width)/2,(screen.Height-height)/2,width,height);
        }
        public void Dispose()
        {
            lock(sync){if(disposed)return;disposed=true;if(cached!=null){cached.Dispose();cached=null;}wake.Set();}
        }
    }
}
