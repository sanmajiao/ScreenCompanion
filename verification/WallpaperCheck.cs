using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
namespace ScreenCompanion
{
    internal static class WallpaperCheck
    {
        private static int checks;
        [STAThread]static int Main()
        {
            try
            {
                Application.EnableVisualStyles();Settings.Current=new Settings();
                Matting(false);Matting(true);Fallback();Layout();NativeReference();HiddenFrame();
                Console.WriteLine("PASS: "+checks+" wallpaper compatibility checks; no user settings modified.");return 0;
            }
            catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
        }
        private static Bitmap Pattern(int width,int height)
        {
            var result=new Bitmap(width,height,PixelFormat.Format32bppPArgb);
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)result.SetPixel(x,y,Color.FromArgb(25+(x*7+y*13)%190,30+(x*11+y*3)%180,20+(x*3+y*17)%200));
            return result;
        }
        private static void Matting(bool tinted)
        {
            using(var wallpaper=Pattern(180,48))using(var source=(Bitmap)wallpaper.Clone())using(var buffer=new TrayPixelBuffer())
            {
                if(tinted)for(int y=0;y<48;y++)for(int x=0;x<180;x++){Color c=source.GetPixel(x,y);source.SetPixel(x,y,Color.FromArgb((int)Math.Round(c.R*.7+25),(int)Math.Round(c.G*.7+15),(int)Math.Round(c.B*.7+10)));}
                using(var g=Graphics.FromImage(source)){g.FillRectangle(Brushes.White,20,16,12,16);g.FillRectangle(Brushes.Black,60,16,12,16);g.FillRectangle(Brushes.Lime,100,16,12,16);}
                using(var old=TraySurface.SeparateBackground(source))using(var fixedFrame=TraySurface.SeparateBackground(source,buffer,wallpaper))
                {
                    int leakedBefore=0,leakedAfter=0,total=0;
                    for(int y=0;y<48;y++)for(int x=0;x<180;x++)
                    {
                        if(new Rectangle(20,16,12,16).Contains(x,y) || new Rectangle(60,16,12,16).Contains(x,y) || new Rectangle(100,16,12,16).Contains(x,y))continue;
                        total++;if(old.GetPixel(x,y).A>8)leakedBefore++;if(fixedFrame.GetPixel(x,y).A>8)leakedAfter++;
                    }
                    Check(leakedBefore>total/3,"fixture reproduces previous wallpaper leak");Check(leakedAfter==0,"complex wallpaper has zero residual background "+(tinted?"tinted":"clear"));
                    Check(fixedFrame.GetPixel(24,24).A>250 && fixedFrame.GetPixel(64,24).A>250 && fixedFrame.GetPixel(104,24).A>250,"white black colored glyphs remain opaque");
                    Color green=fixedFrame.GetPixel(104,24);Check(green.G>245 && green.R<8 && green.B<8,"colored glyph content retained");
                    Console.WriteLine("Background leaked pixels "+leakedBefore+" -> "+leakedAfter+" / "+total+"; reusable bytes="+buffer.Bytes);
                    string output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"wallpaper-"+(tinted?"tinted":"clear")+".png");fixedFrame.Save(output);
                }
            }
        }
        private static void Fallback()
        {
            using(var wallpaper=Pattern(160,48))using(var source=new Bitmap(160,48,PixelFormat.Format32bppPArgb))
            {
                using(var g=Graphics.FromImage(source)){g.Clear(Color.FromArgb(90,110,130));g.FillRectangle(Brushes.White,20,16,12,16);g.FillRectangle(Brushes.Black,60,16,12,16);}
                using(var plain=TraySurface.SeparateBackground(source))using(var withReference=TraySurface.SeparateBackground(source,null,wallpaper))
                {
                    for(int y=0;y<48;y++)for(int x=0;x<160;x++)if(plain.GetPixel(x,y)!=withReference.GetPixel(x,y))throw new Exception("Opaque/mismatched background changed glyph output");
                    Check(true,"opaque bar rejects wallpaper and retains old separation output");
                }
            }
            using(var source=Pattern(160,48))using(var unrelated=Pattern(160,48))
            {
                unrelated.RotateFlip(RotateFlipType.RotateNoneFlipX);
                using(var plain=TraySurface.SeparateBackground(source))using(var withReference=TraySurface.SeparateBackground(source,null,unrelated))
                {
                    for(int y=0;y<48;y++)for(int x=0;x<160;x++)if(plain.GetPixel(x,y)!=withReference.GetPixel(x,y))throw new Exception("Unrelated wallpaper reference was applied");
                    Check(true,"unrelated dynamic/app background is rejected");
                }
            }
        }
        private static void Layout()
        {
            RectangleF fit=WallpaperBackdrop.Destination(new Size(400,200),new Size(300,300),3);
            Check(fit==new RectangleF(0,75,300,150),"fit letterbox position correct");
            Check(WallpaperBackdrop.Destination(new Size(400,200),new Size(300,300),4)==new RectangleF(-150,0,600,300),"fill crop correct");
            Check(WallpaperBackdrop.Destination(new Size(400,200),new Size(300,300),0)==new RectangleF(-50,50,400,200),"center position correct");
            Check(WallpaperBackdrop.Destination(new Size(400,200),new Size(300,300),2)==new RectangleF(0,0,300,300),"stretch position correct");
            string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"image-fixture");Directory.CreateDirectory(directory);string path=Path.Combine(directory,"wallpaper.png");
            using(var pattern=Pattern(40,20))pattern.Save(path);
            var request=new WallpaperBackdrop.Request{Monitor=new Rectangle(-300,0,300,300),Virtual=new Rectangle(-300,0,600,300),Source=new Rectangle(-287,41,20,8)};
            using(var rendered=WallpaperBackdrop.Render(path,1,Color.Black,request))using(var original=new Bitmap(path))
            {Check(rendered.GetPixel(0,0)==original.GetPixel(13,1),"tile uses virtual origin with negative monitor coordinates");}
            request.Source=new Rectangle(-287,285,20,8);
            using(var rendered=WallpaperBackdrop.Render(path,3,Color.Purple,request))Check(rendered.GetPixel(0,0).ToArgb()==Color.Purple.ToArgb(),"fit bottom letterbox has actual desktop background color");
            request.Virtual=new Rectangle(-300,0,600,300);request.Source=new Rectangle(-40,10,20,8);
            using(var span=WallpaperBackdrop.Render(path,5,Color.Black,request))Check(span.Size==request.Source.Size,"span renders only requested physical tray crop");
            byte[] previous=File.Exists(Settings.FileName)?File.ReadAllBytes(Settings.FileName):null;
            try
            {
                File.WriteAllText(Settings.FileName,"transparentTray=1\nhighResponse=1\n");Settings.Current=Settings.Load();Check(Settings.Current.WallpaperAwareTray && Settings.Current.HighResponse,"old config enables compatibility and preserves high mode");
                Settings.Current.WallpaperAwareTray=false;Settings.Current.Save();Check(!Settings.Load().WallpaperAwareTray,"compatibility off persists");
            }
            finally{if(previous!=null)File.WriteAllBytes(Settings.FileName,previous);else if(File.Exists(Settings.FileName))File.Delete(Settings.FileName);}
        }
        private static void NativeReference()
        {
            using(var backdrop=new WallpaperBackdrop())
            {
                int screens=0;foreach(Screen screen in Screen.AllScreens)
                {
                    Rectangle crop=new Rectangle(screen.Bounds.Right-180,screen.Bounds.Bottom-48,180,48);Bitmap reference=null;
                    for(int i=0;i<100 && reference==null;i++){reference=backdrop.Get(crop,screen.Bounds,SystemInformation.VirtualScreen);if(reference==null)Thread.Sleep(25);}
                    using(reference)Check(reference!=null && reference.Size==crop.Size,"actual per-monitor wallpaper crop available "+screen.DeviceName);screens++;
                }
                Check(screens>0,"native wallpaper interface exercised");
                Check(backdrop.Get(new Rectangle(0,0,2000,2000),Screen.PrimaryScreen.Bounds,SystemInformation.VirtualScreen)==null,"oversized wallpaper crop rejected");
            }
        }
        private static void Check(bool condition,string label){if(!condition)throw new Exception(label);checks++;Console.WriteLine("OK: "+label);}
        private static void HiddenFrame()
        {
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            Settings previous=Settings.Current;Settings.Current=new Settings();
            try
            {
                using(var pattern=Pattern(180,48))using(var source=(Bitmap)pattern.Clone())using(var form=new TrayMirrorForm(delegate{}))
                {
                    using(var g=Graphics.FromImage(source))g.FillRectangle(Brushes.White,20,16,12,16);
                    var provider=new WallpaperBackdrop();
                    Rectangle crop=new Rectangle(Screen.PrimaryScreen.Bounds.Right-180,Screen.PrimaryScreen.Bounds.Bottom-48,180,48);
                    var request=new WallpaperBackdrop.Request{Source=crop,Monitor=Screen.PrimaryScreen.Bounds,Virtual=SystemInformation.VirtualScreen};
                    typeof(WallpaperBackdrop).GetField("cached",flags).SetValue(provider,(Bitmap)pattern.Clone());
                    typeof(WallpaperBackdrop).GetField("cachedKey",flags).SetValue(provider,request.Key);
                    typeof(WallpaperBackdrop).GetField("nextCheck",flags).SetValue(provider,Environment.TickCount+60000);
                    typeof(TrayMirrorForm).GetField("wallpaper",flags).SetValue(form,provider);
                    typeof(TrayMirrorForm).GetField("lastTraySource",flags).SetValue(form,crop);
                    typeof(TrayMirrorForm).GetField("lastTrayFrame",flags).SetValue(form,(Bitmap)source.Clone());
                    try
                    {
                        typeof(TrayMirrorForm).GetMethod("RefreshHiddenClock",flags).Invoke(form,null);
                        var picture=(PictureBox)typeof(TrayMirrorForm).GetField("picture",flags).GetValue(form);
                        var output=(Bitmap)picture.Image;
                        Check(output!=null && output.GetPixel(140,10).A==0,"auto-hidden transparent frame retains wallpaper fit at clock margin");
                        Check(output.GetPixel(24,24).A==255,"auto-hidden transparent frame retains actual captured glyph");
                    }
                    finally{provider.Dispose();form.Close();}
                }
            }
            finally{Settings.Current=previous;}
        }
    }
}
