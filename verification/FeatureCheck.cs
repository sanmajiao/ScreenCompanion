using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DesktopPortal;
namespace ScreenCompanion
{
 internal static class FeatureCheck
 {
  static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
  static readonly string Folder=AppDomain.CurrentDomain.BaseDirectory;
  [STAThread]static int Main(string[] args)
  {
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   if(args.Length>0 && args[0]=="--samples"){Samples();return 0;}
   if(args.Length>0 && args[0]=="--bars"){Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.ClassName(h).StartsWith("Shell_") && Native.ClassName(h).Contains("TrayWnd")){Console.WriteLine(Native.ClassName(h)+" "+Screen.FromHandle(h).DeviceName+" "+Native.Bounds(h));Native.EnumChildWindows(h,delegate(IntPtr c,IntPtr unused){Console.WriteLine("  "+Native.ClassName(c)+" "+Native.Bounds(c)+" visible="+Native.IsWindowVisible(c));return true;},IntPtr.Zero);}return true;},IntPtr.Zero);return 0;}
   Process samples=null;SwitcherForm switcher=null;Point saved=Cursor.Position;IntPtr original=Native.GetForegroundWindow();
   try
   {
    Settings.Current=new Settings{Desktop=false,AltTab=false,Tray=false};
    Images();Alpha();Panel();
    samples=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--samples"){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});Pump(800);
    IntPtr a=IntPtr.Zero,b=IntPtr.Zero;Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.ProcessId(h)==samples.Id){if(Native.Title(h)=="移屏测试 A")a=h;if(Native.Title(h)=="移屏测试 B")b=h;}return true;},IntPtr.Zero);
    Check(a!=IntPtr.Zero && b!=IntPtr.Zero,"isolated move fixtures ready");
    Screen target=Settings.TargetScreen(),primary=Screen.PrimaryScreen;Check(target!=null,"secondary available");
    Check(Native.MoveToScreen(a,target),"normal window moves to secondary");Pump(200);
    Check(target.WorkingArea.Contains(Native.Bounds(a)),"normal window fits target working area at mixed DPI");
    Native.MoveToScreen(a,primary);Native.ShowWindow(a,3);Pump(200);Check(Native.MoveToScreen(a,target),"maximized window moves");Pump(250);Check(Native.IsZoomed(a),"maximized state preserved");
    Native.ShowWindow(a,9);Native.MoveToScreen(a,primary);Native.ShowWindow(a,6);Pump(150);Check(Native.MoveToScreen(a,target) && !Native.IsIconic(a),"minimized window restored and moved");
    foreach(Screen screen in Screen.AllScreens)if(!screen.Primary && screen.Bounds.Left<0){Check(Native.MoveToScreen(a,screen),"negative-coordinate secondary supported");Pump(100);}
    Native.MoveToScreen(a,primary);Native.MoveToScreen(b,primary);
    switcher=new SwitcherForm();switcher.Initialize();
    WindowEntry entry=new WindowEntry{Handle=b,ProcessId=(uint)samples.Id,Title="移屏测试 B"};
    switcher.ActivateCard(entry);Pump(300);Check(Native.GetForegroundWindow()==b && Screen.FromHandle(b).Primary,"disabled option only activates");
    Settings.Current.MoveOnSwitch=true;switcher.OpenPinned();Pump(200);
    object chosen=null;foreach(object card in (IEnumerable)typeof(SwitcherForm).GetField("cards",Fields).GetValue(switcher))if(((WindowEntry)card.GetType().GetField("Window",Fields).GetValue(card)).Handle==b){chosen=card;break;}
    Check(chosen!=null,"mouse card corresponds to exact selected handle");
    Rectangle cell=(Rectangle)chosen.GetType().GetField("Bounds",Fields).GetValue(chosen);
    Check(!cell.IsEmpty,"selected card visible");
    Native.Click(switcher.PointToScreen(new Point(cell.X+24,cell.Y+52)),MouseButtons.Left);Pump(500);
    Check(Native.GetForegroundWindow()==b && Screen.FromHandle(b).DeviceName==target.DeviceName,"real mouse selection activates and moves exact window");
    Native.MoveToScreen(b,primary);Native.Activate(b);Pump(200);Native.Activate(a);Pump(200);
    Key(0x12,true);Key(9,true);Key(9,false);Pump(300);Check(switcher.Visible,"keyboard switch sequence shown");Key(0x12,false);Pump(700);
    Check(Native.GetForegroundWindow()==b,"native Alt+Tab selects fixture B");
    Check(Screen.FromHandle(b).DeviceName==target.DeviceName,"Alt release moves actual native selection");
    Native.MoveToScreen(b,primary);Native.Activate(b);Pump(100);Native.Activate(a);Pump(100);
    Key(0x12,true);Key(9,true);Key(9,false);Pump(180);Key(27,true);Key(27,false);Key(0x12,false);Pump(400);
    Check(Screen.FromHandle(a).Primary && Screen.FromHandle(b).Primary,"Escape cancellation never moves windows");
    Console.WriteLine("PASS: new feature checks");return 0;
   }
   catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
   finally
   {
    Native.ReleaseAlt();if(switcher!=null){switcher.Close();switcher.Dispose();}
    if(samples!=null && !samples.HasExited){Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.ProcessId(h)==samples.Id)Native.PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero);return true;},IntPtr.Zero);if(!samples.WaitForExit(1500))samples.Kill();}
    if(Native.IsWindow(original))Native.Activate(original);Native.SetCursorPos(saved.X,saved.Y);
   }
  }
  static void Images()
  {
   string dir=Path.Combine(Folder,"image-fixture");Directory.CreateDirectory(dir);string path=Path.Combine(dir,"photo.png");
   using(var bitmap=new Bitmap(640,320))using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Magenta);using(var b=new SolidBrush(Color.Lime))g.FillRectangle(b,0,0,320,320);bitmap.Save(path);}
   foreach(int pixels in new[]{32,64,128})using(Bitmap image=ShellImage.Get(path,pixels))
   {
    Check(image!=null && Math.Max(image.Width,image.Height)==pixels,"thumbnail limit "+pixels);Check(image.Width==image.Height*2,"photo aspect ratio retained");
    Color left=image.GetPixel(image.Width/4,image.Height/2),right=image.GetPixel(image.Width*3/4,image.Height/2);Check(left.G>200 && left.R<40 && right.R>200 && right.B>200,"real photo thumbnail, not generic file icon");image.Save(Path.Combine(Folder,"thumbnail-"+pixels+".png"));
   }
   using(var form=new Form())using(var canvas=new DesktopCanvas{DesktopPath=dir,Dock=DockStyle.Fill})
   {
    form.Controls.Add(canvas);form.Show();
    foreach(int quality in new[]{0,1,2}){Settings.Current.ImageQuality=quality;canvas.LoadItems();Pump(900);long bytes=canvas.ImageCacheBytes;Check(bytes>0,"background thumbnails arrive at quality "+quality);Console.WriteLine("Image cache bytes quality "+quality+": "+bytes);}
    canvas.LoadItems();canvas.LoadItems();form.Close();
   }
  }
  static void Alpha()
  {
   using(var source=new Bitmap(120,48))
   {
    for(int y=0;y<48;y++)for(int x=0;x<120;x++)source.SetPixel(x,y,Color.FromArgb(80+x/4,100+y/3,120));
    using(var g=Graphics.FromImage(source)){g.FillRectangle(Brushes.White,20,16,12,16);g.FillRectangle(Brushes.Black,50,16,12,16);}
    using(var result=TraySurface.SeparateBackground(source)){Check(result.GetPixel(5,24).A==0,"wallpaper background produces transparent channel");Check(result.GetPixel(24,24).A>240 && result.GetPixel(54,24).A>240,"light and dark icons preserved");result.Save(Path.Combine(Folder,"alpha-fixture.png"));}
   }
  }
  static void Panel()
  {
   using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset))using(var quit=new EventWaitHandle(false,EventResetMode.AutoReset))
   {
    var ctx=new CompanionContext(signal,quit,false);var form=new SettingsForm(ctx);form.Show();Pump(250);
    foreach(Screen screen in Screen.AllScreens)
    {
     form.Location=new Point(screen.WorkingArea.Left+60,screen.WorkingArea.Top+60);Pump(350);
     CheckLayout(form);Capture(form,"settings-"+Native.Dpi(form.Handle)+"-"+screen.DeviceName.Replace("\\","").Replace(".","")+".png");
    }
    form.Font=new Font("Microsoft YaHei UI",12);form.Size=new Size(650,440);Pump(250);CheckLayout(form);Capture(form,"settings-large-font.png");
    FindButton(form,"应用设置").PerformClick();Pump(200);Check(File.Exists(Settings.FileName),"nested Apply button saves portable settings");form.Dispose();ctx.ExitThread();
   }
  }
  static Button FindButton(Control c,string text){foreach(Control child in c.Controls){if(child is Button && child.Text==text)return (Button)child;Button found=FindButton(child,text);if(found!=null)return found;}return null;}
  static void CheckLayout(Control parent)
  {
   foreach(Control c in parent.Controls)
   {
    if(c is CheckBox && c.Height<c.GetPreferredSize(Size.Empty).Height)throw new Exception("checkbox text clipped");
    if(parent is TableLayoutPanel)foreach(Control other in parent.Controls)if(other!=c && c.Bounds.IntersectsWith(other.Bounds))throw new Exception("settings rows overlap");
    CheckLayout(c);
   }
  }
  static void Capture(Form f,string name){Rectangle r=Native.Bounds(f.Handle);using(var b=new Bitmap(r.Width,r.Height))using(var g=Graphics.FromImage(b)){IntPtr dc=g.GetHdc();bool ok=Native.PrintWindow(f.Handle,dc,2);g.ReleaseHdc(dc);Check(ok,"control panel captured");b.Save(Path.Combine(Folder,name));}}
  static void Samples(){var ctx=new ApplicationContext();int count=2;foreach(string name in new[]{"移屏测试 A","移屏测试 B"}){var f=new Form{Text=name,Size=new Size(440,250),StartPosition=FormStartPosition.Manual,Location=new Point(name.EndsWith("A")?100:600,200),BackColor=name.EndsWith("A")?Color.CornflowerBlue:Color.SeaGreen};f.FormClosed+=delegate{if(--count==0)ctx.ExitThread();};f.Show();}Application.Run(ctx);}
  static void Key(int key,bool down){var input=new Native.Input[1];input[0].Type=1;input[0].Data.Key=new Native.KeyInput{Key=(ushort)key,Flags=down?0u:2u};Native.SendInput(1,input,Marshal.SizeOf(typeof(Native.Input)));}
  static void Pump(int ms){var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(10);}}
  static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS: "+name);}
 }
}
