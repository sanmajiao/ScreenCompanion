using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using DesktopPortal;
namespace ScreenCompanion
{
 internal static class ResponseCheck
 {
  private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
  private static int checks;
  [STAThread]static int Main()
  {
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   byte[] old=File.Exists(Settings.FileName)?File.ReadAllBytes(Settings.FileName):null;
   try
   {
    File.WriteAllText(Settings.FileName,"desktop=0\nalttab=0\ntray=0\nimageQuality=2\n");
    Settings.Current=Settings.Load();Check(!Settings.Current.HighResponse && Settings.Current.ImageQuality==2,"old config defaults to normal mode, quality preserved");
    Settings.Current.HighResponse=true;Settings.Current.Save();Settings.Current=Settings.Load();Check(Settings.Current.HighResponse,"high mode persists");
    Settings.Current.HighResponse=false;Settings.Current.Save();Check(!Settings.Load().HighResponse,"normal mode persists");
    int normalFrame=ResponsePolicy.TrayFrame,normalRefresh=ResponsePolicy.DesktopRefresh,normalActivation=ResponsePolicy.WindowActivation;
    Check(ResponsePolicy.ImageCacheLimit==0,"normal mode has no extra desktop cache");Settings.Current.HighResponse=true;
    Check(ResponsePolicy.TrayFrame<normalFrame && ResponsePolicy.DesktopRefresh<normalRefresh && ResponsePolicy.WindowActivation<normalActivation,"all response policies speed up");
    Console.WriteLine("Program waits normal/high ms: tray "+normalFrame+"/"+ResponsePolicy.TrayFrame+", desktop "+normalRefresh+"/"+ResponsePolicy.DesktopRefresh+", activation "+normalActivation+"/"+ResponsePolicy.WindowActivation);
    Cache();Pixels();DesktopRefresh();Panel();Thumbnails();InputPump();
    Console.WriteLine("PASS: "+checks+" response-mode checks");return 0;
   }
   catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
   finally{if(old!=null)File.WriteAllBytes(Settings.FileName,old);else if(File.Exists(Settings.FileName))File.Delete(Settings.FileName);}
  }
  static void Cache()
  {
   using(var cache=new DesktopImageCache(32*32*4*2))using(var image=new Bitmap(32,32))
   {
    image.SetPixel(0,0,Color.Red);cache.Put("A",image);cache.Put("B",image);
    using(var hit=cache.Get("A")){Check(hit!=null && hit.GetPixel(0,0).R==255,"cached image clone preserves content");hit.SetPixel(0,0,Color.Blue);}
    cache.Put("C",image);Check(cache.Get("B")==null,"least recently used item evicted");
    using(var hit=cache.Get("A"))Check(hit.GetPixel(0,0).R==255,"caller cannot modify cache storage");
    Check(cache.Bytes<=32*32*4*2,"cache obeys byte cap");cache.Dispose();Check(cache.Bytes==0,"cache releases on disposal");
   }
   using(var cache=new DesktopImageCache(0))using(var image=new Bitmap(32,32)){cache.Put("X",image);Check(cache.Bytes==0 && cache.Get("X")==null,"disabled mode retains no extra image");}
  }
  static void Pixels()
  {
   using(var source=new Bitmap(520,48))using(var buffer=new TrayPixelBuffer())
   {
    using(var g=Graphics.FromImage(source)){g.Clear(Color.FromArgb(90,110,130));g.FillRectangle(Brushes.White,20,15,13,16);g.FillRectangle(Brushes.Black,50,15,13,16);}
    using(var normal=TraySurface.SeparateBackground(source))using(var high=TraySurface.SeparateBackground(source,buffer))
    {for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++)if(normal.GetPixel(x,y)!=high.GetPixel(x,y))throw new Exception("Alpha buffer changed visual output");Check(high.GetPixel(5,24).A==0 && high.GetPixel(24,24).A>240 && high.GetPixel(54,24).A>240,"transparent background and dark/light glyphs preserved");}
    byte[] retained=buffer.Input;var clock=Stopwatch.StartNew();int before=GC.CollectionCount(0);
    for(int i=0;i<60;i++)using(var frame=TraySurface.SeparateBackground(source,buffer)){}
    clock.Stop();Check(Object.ReferenceEquals(retained,buffer.Input),"high mode reuses pixel buffers");
    Console.WriteLine("Alpha 520x48: "+(clock.Elapsed.TotalMilliseconds/60).ToString("F2")+" ms/frame; Gen0 collections="+(GC.CollectionCount(0)-before)+"; retained="+buffer.Bytes+" bytes");
    byte[] input,output;buffer.Get(5*1024*1024,out input,out output);Check(buffer.Bytes==0,"oversized frames do not exceed retained-buffer cap");
    buffer.Get(64,out input,out output);buffer.Dispose();Check(buffer.Bytes==0,"pixel buffers released on disposal");
   }
  }
  static void DesktopRefresh()
  {
   string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"image-fixture");Directory.CreateDirectory(dir);string path=Path.Combine(dir,"response.png");
   using(var b=new Bitmap(256,128))using(var g=Graphics.FromImage(b)){g.Clear(Color.Orange);b.Save(path);}
   foreach(bool high in new[]{false,true})
   {
    Settings.Current.HighResponse=high;Settings.Current.ImageQuality=1;
    using(var form=new Form())using(var canvas=new DesktopCanvas{DesktopPath=dir,Dock=DockStyle.Fill})
    {
     form.Controls.Add(canvas);form.Show();canvas.LoadItems();WaitImages(canvas);
     var cache=(DesktopImageCache)typeof(DesktopCanvas).GetField("recentImages",Fields).GetValue(canvas);
     long loaded=canvas.ImageCacheBytes;canvas.LoadItems();Check(high?canvas.ImageCacheBytes==loaded:canvas.ImageCacheBytes==0,"directory reload "+(high?"has immediately available images":"loads asynchronously without extra cache"));
     WaitImages(canvas);
     if(high){File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddMinutes(1));canvas.LoadItems();Check(canvas.ImageCacheBytes<loaded,"changed file invalidates its cache key");WaitImages(canvas);Settings.Current.ImageQuality=2;canvas.LoadItems();Check(canvas.ImageCacheBytes==0,"quality change invalidates cached resolution");WaitImages(canvas);}
     form.Close();canvas.Dispose();Check(cache.Bytes==0,"desktop disposal releases cache");
    }
   }
  }
  static void Panel()
  {
   Settings.Current=new Settings{Desktop=false,AltTab=false,Tray=false,HighResponse=true};
   using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset))using(var quit=new EventWaitHandle(false,EventResetMode.AutoReset))
   {
    var context=new CompanionContext(signal,quit,false);
    try
    {
     using(var form=new SettingsForm(context))
     {
      form.Show();Pump(150);var toggle=(CheckBox)typeof(SettingsForm).GetField("highResponse",Fields).GetValue(form);
      Check(toggle.Checked,"settings UI loads high mode");toggle.Checked=false;FindApply(form).PerformClick();Check(!Settings.Load().HighResponse,"UI switch applies and saves normal mode");
      toggle.Checked=true;FindApply(form).PerformClick();Check(Settings.Load().HighResponse,"UI switch applies and saves high mode");
      form.Font=new Font("Microsoft YaHei UI",14);form.PerformLayout();Pump(50);
      var layout=(TableLayoutPanel)form.Controls[0];int bottom=0;
      using(var b=new Bitmap(form.Width,form.Height))using(var g=Graphics.FromImage(b)){IntPtr dc=g.GetHdc();bool captured=Native.PrintWindow(form.Handle,dc,2);g.ReleaseHdc(dc);Check(captured,"control panel renders");b.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"response-settings-ui.png"));}
      foreach(Control c in layout.Controls){Console.WriteLine("Row "+layout.GetRow(c)+" "+c.GetType().Name+" "+c.Bounds);Check(c.Top>=bottom,"large-font rows do not overlap");bottom=c.Bottom;}
     }
    }
    finally{context.ExitThread();context.Dispose();}
   }
  }
  static void InputPump()
  {
   using(var listener=new MouseListener(delegate{}))
   {
    Exception failure=null;var worker=new Thread(delegate(){Thread.Sleep(40);for(int i=0;i<5;i++){if(!listener.Responsive(150))failure=new Exception("Mouse pump blocked by owner thread");Thread.Sleep(20);}});worker.Start();Thread.Sleep(400);worker.Join();if(failure!=null)throw failure;Check(true,"native mouse message pump responds during 400ms owner stall");
   }
  }
  static void Thumbnails()
  {
   using(var samples=Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FeatureCheck.exe"),"--samples"){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden}))
   {
    try
    {
     Pump(400);
     using(var panel=new SwitcherForm())
     {
      panel.OpenPinned();Pump(80);var cards=(System.Collections.IEnumerable)typeof(SwitcherForm).GetField("cards",Fields).GetValue(panel);object fixture=null;
      foreach(object card in cards){var w=(WindowEntry)card.GetType().GetField("Window").GetValue(card);if(w.ProcessId==(uint)samples.Id){fixture=card;break;}}
      Check(fixture!=null,"native fixture appears in exact-HWND window list");
      var thumbField=fixture.GetType().GetField("Thumbnail");IntPtr first=(IntPtr)thumbField.GetValue(fixture);Check(first!=IntPtr.Zero,"fixture has live DWM thumbnail");
      var layout=typeof(SwitcherForm).GetMethod("LayoutCards",Fields);for(int i=0;i<4;i++)layout.Invoke(panel,null);
      Check((IntPtr)thumbField.GetValue(fixture)==first,"repeated selection/layout reuses the same native thumbnail");panel.Close();
     }
    }
    finally
    {
     Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.ProcessId(h)==(uint)samples.Id)Native.PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero);return true;},IntPtr.Zero);
     Check(samples.WaitForExit(2000),"own native fixtures closed normally");
    }
   }
  }
  static Button FindApply(Control parent){foreach(Control c in parent.Controls){if(c is Button && c.Text=="应用设置")return (Button)c;var found=FindApply(c);if(found!=null)return found;}return null;}
  static void WaitImages(DesktopCanvas c){for(int i=0;i<300 && c.ImageCacheBytes==0;i++)Pump(10);Check(c.ImageCacheBytes>0,"background desktop images arrive");}
  static void Pump(int ms){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(5);}}
  static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("OK: "+message);}
 }
}
