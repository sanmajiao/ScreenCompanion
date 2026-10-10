using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenCompanion
{
    internal static class Verification
    {
        private static BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        private static string folder;
        private static bool trayMenuOpened;
        [STAThread]private static int Main(string[] args)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            folder=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if(args.Length>0 && args[0]=="--samples"){Samples();return 0;}
            Process samples=null;Form switcher=null;Form widget=null;NotifyIcon testIcon=null;Icon testDrawing=null;
            try
            {
                var a=Assembly.LoadFrom(Path.Combine(folder,"..","release","副屏助手.exe"));
                var settings=a.GetType("ScreenCompanion.Settings");object config=Activator.CreateInstance(settings);
                settings.GetField("Current",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,config);
                object target=settings.GetMethod("TargetScreen",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
                if(target==null)throw new Exception("No secondary screen available for UI validation.");
                Console.WriteLine("Screens: "+Screen.AllScreens.Length+"; Target: "+((Screen)target).DeviceName+" "+((Screen)target).Bounds);
                samples=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--samples"){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});
                Pump(1000);
                Point saved=Cursor.Position;IntPtr hb=IntPtr.Zero;
                if(args.Length>0 && args[0]=="--tray-only")
                {
                    Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.ProcessId(h)==samples.Id && Native.IsWindowVisible(h) && Native.Title(h)=="副屏助手同名回归窗口")hb=h;return true;},IntPtr.Zero);
                    Assert(hb!=IntPtr.Zero,"temporary outside-click target ready");
                }
                else
                {
                var type=a.GetType("ScreenCompanion.SwitcherForm");switcher=(Form)Activator.CreateInstance(type,true);
                type.GetMethod("Initialize",fields).Invoke(switcher,null);
                Key(0x12,true);Key(9,true);Key(9,false);Pump(600);
                Assert(switcher.Visible,"real Alt+Tab keyboard hook shows secondary panel");
                bool nativeSwitcher=false;Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.IsWindowVisible(h) && Native.ClassName(h)=="XamlExplorerHostIslandWindow")nativeSwitcher=true;return true;},IntPtr.Zero);
                Assert(nativeSwitcher,"native primary Alt+Tab remains visible");
                object ca=null,cb=null;IntPtr ha=IntPtr.Zero;
                foreach(object c in (IEnumerable)type.GetField("cards",fields).GetValue(switcher))
                {
                    object entry=c.GetType().GetField("Window",fields).GetValue(c);
                    IntPtr h=(IntPtr)entry.GetType().GetField("Handle").GetValue(entry);
                    if(Native.ProcessId(h)==samples.Id)
                    {
                        Assert((string)entry.GetType().GetField("Title").GetValue(entry)=="副屏助手同名回归窗口","title bound to handle");
                        if(ca==null){ca=c;ha=h;}else{cb=c;hb=h;}
                    }
                }
                Assert(ca!=null && cb!=null && ha!=hb,"two equal titles remain separate HWND cards");
                Assert((IntPtr)ca.GetType().GetField("Thumbnail",fields).GetValue(ca)!=IntPtr.Zero,"DWM thumbnail A registered");
                Assert((IntPtr)cb.GetType().GetField("Thumbnail",fields).GetValue(cb)!=IntPtr.Zero,"DWM thumbnail B registered");
                Capture(switcher.Bounds,Path.Combine(folder,"switcher-ui.png"));
                Rectangle r=(Rectangle)cb.GetType().GetField("Bounds",fields).GetValue(cb);
                Assert(!r.IsEmpty,"target card visible");
                Native.Click(switcher.PointToScreen(new Point(r.X+24,r.Y+52)),MouseButtons.Left);Pump(600);
                Assert(Native.GetForegroundWindow()==hb,"real mouse click activates exact HWND B");
                Assert(!Native.KeyPressed(0x12) && !switcher.Visible,"mouse switching ends Alt sequence and hides panel");
                type.GetMethod("OpenPinned",fields).Invoke(switcher,null);Pump(400);
                ca=null;
                foreach(object c in (IEnumerable)type.GetField("cards",fields).GetValue(switcher))
                {
                    object entry=c.GetType().GetField("Window",fields).GetValue(c);
                    if((IntPtr)entry.GetType().GetField("Handle").GetValue(entry)==ha){ca=c;break;}
                }
                Assert(ca!=null,"A still present after B activation");
                Rectangle close=(Rectangle)ca.GetType().GetField("Close",fields).GetValue(ca);
                Native.Click(switcher.PointToScreen(new Point(close.X+12,close.Y+12)),MouseButtons.Left);Pump(450);
                Assert(!Native.IsWindow(ha) && Native.IsWindow(hb),"close click closes only HWND A with duplicate title");
                switcher.Close();switcher.Dispose();switcher=null;
                }
                testDrawing=TestIcon();testIcon=new NotifyIcon{Icon=testDrawing,Text="副屏助手托盘菜单回归",Visible=true};
                var ownMenu=new ContextMenuStrip();ownMenu.Items.Add("副屏助手菜单回归",null,delegate{});ownMenu.Opened+=delegate{trayMenuOpened=true;};testIcon.ContextMenuStrip=ownMenu;
                var trayType=a.GetType("ScreenCompanion.TrayMirrorForm");
                Rectangle tray=(Rectangle)trayType.GetMethod("FindTrayBounds",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
                Assert(!tray.IsEmpty,"actual tray bounds found");Console.WriteLine("Tray source: "+tray);
                Native.SetCursorPos(Screen.PrimaryScreen.Bounds.Right-70,Screen.PrimaryScreen.Bounds.Bottom-1);Pump(900);
                widget=(Form)Activator.CreateInstance(trayType,fields,null,new object[]{new Action(delegate{})},null);widget.Show();Pump(1600);
                Screen targetScreen=(Screen)target;
                IntPtr targetBar=(IntPtr)trayType.GetMethod("FindTargetTaskbar",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
                Assert(targetBar!=IntPtr.Zero,"native secondary taskbar found");
                if(Rectangle.Intersect(targetScreen.Bounds,Native.Bounds(targetBar)).Height<=3)Assert(!widget.Visible,"tray initially hides with native secondary taskbar");
                Native.SetCursorPos(targetScreen.Bounds.Right-430,targetScreen.Bounds.Bottom-1);Pump(1000);
                Console.WriteLine("Reveal widget="+widget.Visible+" native="+Native.Bounds(targetBar)+" cursor="+Cursor.Position+" source="+trayType.GetMethod("FindTrayBounds",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null));
                Assert(widget.Visible,"tray follows secondary taskbar reveal");
                Rectangle actualWidget=Native.Bounds(widget.Handle),actualBar=Native.Bounds(targetBar);
                Assert(actualWidget.Top==actualBar.Top && actualWidget.Bottom==actualBar.Bottom && actualWidget.Right==actualBar.Right,"tray edges exactly match secondary taskbar physical pixels");
                Console.WriteLine("Aligned widget "+actualWidget+" taskbar "+actualBar);
                var pic=(PictureBox)trayType.GetField("picture",fields).GetValue(widget);
                Assert(pic.Image!=null && pic.Image.Width==tray.Width,"tray copies exact source area");
                pic.Image.Save(Path.Combine(folder,"tray-alpha.png"));
                ((Bitmap)trayType.GetField("lastTrayFrame",fields).GetValue(widget)).Save(Path.Combine(folder,"tray-source.png"));
                Capture(Native.Bounds(widget.Handle),Path.Combine(folder,"tray-ui.png"));
                // Click the real overflow arrow, without changing any system setting.
                int sourceX=16,px=sourceX*pic.Width/tray.Width;
                var opening=Stopwatch.StartNew();
                Native.Click(pic.PointToScreen(new Point(Math.Max(2,Math.Min(pic.Width-2,px)),pic.Height/2)),MouseButtons.Left);
                IntPtr overflow=IntPtr.Zero;
                while(opening.ElapsedMilliseconds<1600)
                {
                    Pump(10);overflow=(IntPtr)trayType.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
                    if(overflow!=IntPtr.Zero && targetScreen.Bounds.Contains(Native.Bounds(overflow)) && FindMarker(Native.Bounds(overflow))!=Point.Empty)break;
                }
                Assert(overflow!=IntPtr.Zero && targetScreen.Bounds.Contains(Native.Bounds(overflow)),"arrow opens actual complete hidden-icon menu on secondary screen");
                Console.WriteLine("Click to rendered secondary menu: "+opening.ElapsedMilliseconds+" ms");
                Pump(250);
                Console.WriteLine("Forward count "+trayType.GetField("ForwardCount",fields).GetValue(widget)+" mapped "+trayType.GetField("LastClickPoint",fields).GetValue(widget)+" forwarding "+trayType.GetField("forwarding",fields).GetValue(widget));
                bool popup=false;Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.IsWindowVisible(h)){string cls=Native.ClassName(h);if(cls=="NotifyIconOverflowWindow" || cls=="Shell_NotifyIconOverflowWindow" || cls=="TopLevelWindowForOverflowXamlIsland") {Console.WriteLine("Popup class: "+cls+" "+Native.Bounds(h));popup=true;}}return true;},IntPtr.Zero);
                Capture(Native.Bounds(overflow),Path.Combine(folder,"tray-overflow-secondary.png"));
                Assert(popup,"tray left-click opens the native hidden-icon panel");
                Point marker=FindMarker(Native.Bounds(overflow));
                Assert(marker!=Point.Empty,"temporary regression icon present in complete overflow menu");
                Native.Click(marker,MouseButtons.Right);Pump(200);
                Assert(trayMenuOpened,"native menu icon on secondary screen opens its own real context menu");
                Console.WriteLine("Context class="+Native.ClassName(testIcon.ContextMenuStrip.Handle)+" style="+Native.GetWindowLong(testIcon.ContextMenuStrip.Handle,-16).ToString("X")+" ex="+Native.GetWindowLong(testIcon.ContextMenuStrip.Handle,-20).ToString("X"));
                Key(27,true);Key(27,false);Pump(200);
                Console.WriteLine("Escape contextVisible="+testIcon.ContextMenuStrip.Visible+" overflow="+trayType.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)+" managed="+trayType.GetField("managedOverflow",fields).GetValue(widget));
                Assert(!testIcon.ContextMenuStrip.Visible && (IntPtr)trayType.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)!=IntPtr.Zero,"Escape closes icon context menu while preserving overflow panel");
                Native.SetCursorPos(targetScreen.Bounds.Right-430,targetScreen.Bounds.Bottom-1);Pump(700);
                Native.Click(pic.PointToScreen(new Point(Math.Max(2,Math.Min(pic.Width-2,px)),pic.Height/2)),MouseButtons.Left);Pump(600);
                Console.WriteLine("Toggle visible="+widget.Visible+" forwards="+trayType.GetField("ForwardCount",fields).GetValue(widget)+" popup="+trayType.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null));
                Console.WriteLine("Toggle bounds="+Native.Bounds(overflow)+" source="+trayType.GetMethod("FindTrayBounds",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)+" marker="+FindMarker(Native.Bounds(overflow))+" mapped="+trayType.GetField("LastClickPoint",fields).GetValue(widget)+" pending="+trayType.GetField("forwarding",fields).GetValue(widget));
                Capture(Native.Bounds(overflow),Path.Combine(folder,"overflow-after-toggle.png"));
                Assert((IntPtr)trayType.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)==IntPtr.Zero,"second arrow click closes overflow menu");
                Native.Click(pic.PointToScreen(new Point(Math.Max(2,Math.Min(pic.Width-2,px)),pic.Height/2)),MouseButtons.Left);
                IntPtr reopened=WaitOverflow(trayType,targetScreen,1500);
                Console.WriteLine("Reopened="+reopened+" bounds="+Native.Bounds(reopened)+" marker="+(reopened==IntPtr.Zero?Point.Empty:FindMarker(Native.Bounds(reopened)))+" forwards="+trayType.GetField("ForwardCount",fields).GetValue(widget)+" pending="+trayType.GetField("forwarding",fields).GetValue(widget));
                Assert(reopened!=IntPtr.Zero && targetScreen.Bounds.Contains(Native.Bounds(reopened)) && FindMarker(Native.Bounds(reopened))!=Point.Empty,"arrow can reopen complete usable menu after closing");
                Rectangle sampleBounds=Native.Bounds(hb);
                Native.Activate(hb);Pump(100);
                Point outsidePoint=new Point(sampleBounds.Left+sampleBounds.Width/2,sampleBounds.Top+sampleBounds.Height/2);
                IntPtr outsideWindow=Native.WindowFromPoint(outsidePoint);
                Console.WriteLine("Outside target="+outsideWindow+" class="+Native.ClassName(outsideWindow)+" menu="+trayType.GetMethod("IsContextMenuWindow",fields).Invoke(widget,new object[]{outsideWindow}));
                var inputDuringStall=new Thread(delegate(){Thread.Sleep(150);Native.Click(outsidePoint,MouseButtons.Left);});inputDuringStall.Start();
                Thread.Sleep(1300);Pump(300);inputDuringStall.Join(1000);
                Console.WriteLine("After outside managed="+trayType.GetField("managedOverflow",fields).GetValue(widget)+" root="+trayType.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null));
                Assert((IntPtr)trayType.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)==IntPtr.Zero,"outside click received during 1.3-second UI stall closes overflow panel");
                Native.SetCursorPos(targetScreen.Bounds.Right-430,targetScreen.Bounds.Bottom-1);Pump(700);
                Native.Click(pic.PointToScreen(new Point(Math.Max(2,Math.Min(pic.Width-2,px)),pic.Height/2)),MouseButtons.Left);WaitOverflow(trayType,targetScreen,1500);
                Assert((IntPtr)trayType.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)!=IntPtr.Zero,"menu reopens after outside click");
                Key(27,true);Key(27,false);Pump(200);
                Assert((IntPtr)trayType.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)==IntPtr.Zero,"Escape closes relocated overflow panel");
                Native.SetCursorPos(saved.X,saved.Y);
                Native.Activate(hb);Native.SetCursorPos(600,550);
                var hiding=Stopwatch.StartNew();while(hiding.ElapsedMilliseconds<3500 && Rectangle.Intersect(targetScreen.Bounds,Native.Bounds(targetBar)).Height>3)Pump(50);Pump(100);
                bool nativeExposed=Rectangle.Intersect(targetScreen.Bounds,Native.Bounds(targetBar)).Height>3;
                Assert(widget.Visible==nativeExposed,"tray follows native taskbar visibility after cursor leaves");
                if(nativeExposed)Console.WriteLine("INFO: Windows keeps its native taskbar exposed; mirror remains aligned with it");
                Native.SetCursorPos(saved.X,saved.Y);
                Console.WriteLine("PASS: "+(args.Length>0 && args[0]=="--tray-only"?"focused tray regression":"window and tray regression"));
                return 0;
            }
            catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
            finally
            {
                Native.ReleaseAlt();if(widget!=null){widget.Close();widget.Dispose();}if(switcher!=null){switcher.Close();switcher.Dispose();}
                if(testIcon!=null){testIcon.Visible=false;testIcon.ContextMenuStrip.Dispose();testIcon.Dispose();}if(testDrawing!=null)testDrawing.Dispose();
                if(samples!=null && !samples.HasExited){uint pid=(uint)samples.Id;Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.ProcessId(h)==pid)Native.PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero);return true;},IntPtr.Zero);samples.WaitForExit(1000);}
            }
        }
        private static void Assert(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS: "+name);}
        private static IntPtr WaitOverflow(Type type,Screen target,int timeout)
        {
            var sw=Stopwatch.StartNew();IntPtr h=IntPtr.Zero;
            while(sw.ElapsedMilliseconds<timeout){Pump(20);h=(IntPtr)type.GetMethod("FindOverflowWindow",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);if(h!=IntPtr.Zero && target.Bounds.Contains(Native.Bounds(h)) && FindMarker(Native.Bounds(h))!=Point.Empty)return h;}
            return h;
        }
        private static void Pump(int ms){var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(10);}}
        private static void Capture(Rectangle r,string path){using(var b=new Bitmap(r.Width,r.Height))using(var g=Graphics.FromImage(b)){g.CopyFromScreen(r.Location,Point.Empty,r.Size);b.Save(path,System.Drawing.Imaging.ImageFormat.Png);}}
        private static void Key(int key,bool down){var a=new Native.Input[1];a[0].Type=1;a[0].Data.Key=new Native.KeyInput{Key=(ushort)key,Flags=down?0u:2u};Native.SendInput(1,a,Marshal.SizeOf(typeof(Native.Input)));}
        [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr h);
        private static Icon TestIcon()
        {
            using(var b=new Bitmap(16,16))using(var g=Graphics.FromImage(b))using(var brush=new SolidBrush(Color.FromArgb(19,233,149)))
            {g.FillRectangle(brush,0,0,16,16);IntPtr h=b.GetHicon();try{return (Icon)Icon.FromHandle(h).Clone();}finally{DestroyIcon(h);}}
        }
        private static Point FindMarker(Rectangle r)
        {
            using(var b=new Bitmap(r.Width,r.Height))using(var g=Graphics.FromImage(b))
            {
                g.CopyFromScreen(r.Location,Point.Empty,r.Size);long x=0,y=0;int count=0;
                for(int row=0;row<b.Height;row++)for(int col=0;col<b.Width;col++){Color c=b.GetPixel(col,row);if(Math.Abs(c.R-19)<3 && Math.Abs(c.G-233)<3 && Math.Abs(c.B-149)<3){x+=col;y+=row;count++;}}
                return count>20?new Point(r.X+(int)(x/count),r.Y+(int)(y/count)):Point.Empty;
            }
        }
        private static void Samples()
        {
            var ctx=new ApplicationContext();int count=2;
            var f1=new Form{Text="副屏助手同名回归窗口",BackColor=Color.FromArgb(45,126,205),Size=new Size(420,240),StartPosition=FormStartPosition.Manual,Location=new Point(100,100)};
            var f2=new Form{Text="副屏助手同名回归窗口",BackColor=Color.FromArgb(35,166,117),Size=new Size(420,240),StartPosition=FormStartPosition.Manual,Location=new Point(560,100)};
            f1.Controls.Add(new Label{Text="测试窗口 A · 蓝色",Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",18),ForeColor=Color.White,TextAlign=ContentAlignment.MiddleCenter});
            f2.Controls.Add(new Label{Text="测试窗口 B · 绿色",Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",18),ForeColor=Color.White,TextAlign=ContentAlignment.MiddleCenter});
            FormClosedEventHandler closed=delegate{if(--count==0)ctx.ExitThread();};f1.FormClosed+=closed;f2.FormClosed+=closed;f1.Show();f2.Show();Application.Run(ctx);
        }
    }
}
