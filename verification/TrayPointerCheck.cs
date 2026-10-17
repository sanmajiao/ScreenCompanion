using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenCompanion
{
    // Deterministic pointer backend: no SendInput, SetCursorPos, Explorer click,
    // or physical pointer movement is performed by this check.
    internal static class TrayPointerCheck
    {
        private static int assertions;
        private sealed class Backend
        {
            internal Point Position=new Point(1900,800),Target,Return;
            internal int Moves,Clicks;
            internal MouseButtons Button;
            internal int Mode;
            internal int Offset;
            internal int FailedReturns;
            internal int ThrowReturns,LyingReturns,ReadFailures;
            internal readonly ManualResetEvent Restored=new ManualResetEvent(false);
            internal Point Read(){if(ReadFailures>0){ReadFailures--;throw new InvalidOperationException("fixture cursor temporarily unavailable");}return Position;}
            internal bool Move(Point point)
            {
                Interlocked.Increment(ref Moves);
                if(point==new Point(1900,800))
                {
                    if(ThrowReturns>0){ThrowReturns--;throw new InvalidOperationException("fixture return temporarily unavailable");}
                    if(FailedReturns>0){FailedReturns--;return false;}
                    if(LyingReturns>0){LyingReturns--;return true;}
                }
                Position=new Point(point.X+Offset,point.Y-Offset);Restored.Set();return true;
            }
            internal bool Click(Point point,MouseButtons button,Point origin)
            {
                Target=point;Return=origin;Button=button;Clicks++;
                if(Mode==1){Position=point;return false;}
                if(Mode==2){Position=point;throw new InvalidOperationException("fixture rejection");}
                if(Mode==3)return false;
                Position=origin;return true;
            }
            internal TrayPointerLease Lease(int deadline){return new TrayPointerLease(Read,Move,Click,deadline);}
        }
        [STAThread] static int Main(string[] args)
        {
            try
            {
                if(Array.IndexOf(args,"--repro-echo")>=0){InjectedEchoRegression();return 0;}
                if(Array.IndexOf(args,"--repro-restore")>=0){TransientReturnRegression();return 0;}
                InputSequence();VisibleClicks();HiddenLease();UserMovement();Failures();Deadline();InjectedEchoRegression();TransientReturnRegression();InputOwnership();ReturnRetries();ExpiredForward();CollapsedFrames();
                if(Array.IndexOf(args,"--pointer-only")<0){TaskbarExposure();HiddenStartup();ContentBridgeExposure();}
                Console.WriteLine("PASS: "+assertions+" tray pointer checks; simulated backend, no desktop input injected.");return 0;
            }
            catch(Exception error){Console.WriteLine("FAIL: "+error);return 1;}
        }
        private static void InjectedEchoRegression()
        {
            var backend=new Backend();Point origin=backend.Position;
            using(var lease=backend.Lease(1500))
            {
                lease.Begin(origin);lease.Move(new Point(170,950));
                lease.ObserveInput(0x200,backend.Position,1,new IntPtr(123));lease.Finish();
                Check(backend.Position==origin,"foreign injected no-op echo must not strand the moved pointer");
            }
        }
        private static void TransientReturnRegression()
        {
            var backend=new Backend();Point origin=backend.Position;
            using(var lease=backend.Lease(1500))
            {
                lease.Begin(origin);lease.Move(new Point(170,950));backend.Restored.Reset();backend.FailedReturns=1;lease.Finish();
                Check(backend.Restored.WaitOne(1000) && backend.Position==origin,"a rejected first return must be retried independently of the UI pump");
            }
        }
        private static void InputOwnership()
        {
            foreach(uint flag in new[]{1u,3u})
            {
                var backend=new Backend{Offset=1};Point origin=backend.Position;
                using(var lease=backend.Lease(1500))
                {
                    lease.Begin(origin);lease.Move(new Point(-100,950));
                    lease.ObserveInput(0x200,backend.Position,1,Native.PointerInputTag);
                    lease.ObserveInput(0x200,backend.Position,flag,new IntPtr(123));
                    Check(lease.IsOwned,"unchanged foreign injected position echo preserves rounded ownership: "+flag);
                    lease.Finish();Check(Math.Abs(backend.Position.X-origin.X)<=1,"injected position echo does not strand pointer: "+flag);
                }
            }
            foreach(uint flag in new[]{0u,1u,3u})
            {
                var backend=new Backend();Point target=new Point(170,950);
                using(var lease=backend.Lease(1500))
                {
                    lease.Begin(backend.Position);lease.Move(target);backend.Position=new Point(target.X+1,target.Y);
                    lease.ObserveInput(0x200,backend.Position,flag,new IntPtr(123));lease.Finish();
                    Check(backend.Position==new Point(target.X+1,target.Y) && !lease.IsOwned,"genuine one-pixel physical or remote movement stays put: "+flag);
                }
            }
            foreach(int message in new[]{0x201,0x204,0x20A})
            {
                var backend=new Backend();Point target=new Point(170,950);
                using(var lease=backend.Lease(1500))
                {
                    lease.Begin(backend.Position);lease.Move(target);lease.ObserveInput(message,target,1,new IntPtr(123));lease.Finish();
                    Check(backend.Position==target,"injected button or wheel intent revokes return: "+message);
                }
            }
            var physical=new Backend();
            using(var lease=physical.Lease(1500))
            {
                lease.Begin(physical.Position);lease.Move(new Point(170,950));lease.ObserveInput(0x200,physical.Position,0,IntPtr.Zero);
                lease.ObserveInput(0x200,physical.Position,1,new IntPtr(123));lease.Finish();
                Check(physical.Position==new Point(170,950),"later injected echo never reclaims a lease revoked by physical input");
            }
        }
        private static void ReturnRetries()
        {
            foreach(int failure in new[]{0,1,2,3})
            {
                var backend=new Backend();Point origin=backend.Position;
                using(var lease=backend.Lease(1500))
                {
                    lease.Begin(origin);lease.Move(new Point(170,950));backend.Restored.Reset();
                    if(failure==0)backend.FailedReturns=2;
                    if(failure==1)backend.ThrowReturns=2;
                    if(failure==2)backend.LyingReturns=2;
                    if(failure==3)backend.ReadFailures=2;
                    lease.Finish();
                    Check(backend.Restored.WaitOne(1000) && backend.Position==origin,"watchdog retries failed, throwing, deferred, or unreadable return: "+failure);
                    Check(!lease.RestorationPending && !lease.ReturnFailed,"successful retry releases restore state: "+failure);
                }
            }
            var blocked=new Backend();Point home=blocked.Position;
            using(var lease=blocked.Lease(1500))
            {
                lease.Begin(home);lease.Move(new Point(170,950));blocked.FailedReturns=10000;blocked.Restored.Reset();lease.Finish();
                Check(lease.RestorationPending,"failed return remains owned pending watchdog recovery");
                Check(!lease.Begin(blocked.Position) && lease.Origin==home,"pending return cannot replace home with temporary primary position");
                Thread.Sleep(350);
                Check(!lease.RestorationPending && lease.ReturnFailed && blocked.Moves<=9,"permanent native denial has bounded retries and exposes failure");
            }
            var canceled=new Backend();
            using(var lease=canceled.Lease(1500))
            {
                lease.Begin(canceled.Position);lease.Move(new Point(170,950));canceled.FailedReturns=3;lease.Finish();
                canceled.Position=new Point(171,950);lease.ObserveInput(0x200,canceled.Position,0,IntPtr.Zero);Thread.Sleep(100);
                Check(canceled.Position==new Point(171,950) && !lease.RestorationPending,"physical movement cancels all queued return retries");
            }
            var partial=new Backend{Mode=1};home=partial.Position;
            using(var lease=partial.Lease(1500))
            {
                lease.Begin(home);partial.FailedReturns=1;partial.Restored.Reset();
                Check(!lease.Click(new Point(170,950),MouseButtons.Left),"partial native click reports failure");
                Check(partial.Restored.WaitOne(1000) && partial.Position==home,"partial click return recovers after initial restore rejection");
            }
            var zero=new Backend{Mode=3};home=zero.Position;
            using(var lease=zero.Lease(1500))
            {
                lease.Begin(home);Check(!lease.Click(new Point(170,950),MouseButtons.Right) && zero.Position==home && zero.Moves==0,"zero inserted inputs require no pointer recovery");
            }
            var restart=new Backend();home=restart.Position;
            using(var lease=restart.Lease(1500))
            {
                lease.Begin(home);lease.Move(new Point(170,950));Point stale=restart.Position;
                Check(lease.Begin(stale) && lease.Origin==home && restart.Position==home,"reentrant Begin rereads restored home instead of adopting stale primary point");
            }
            var closing=new Backend();home=closing.Position;var closeLease=closing.Lease(1500);
            closeLease.Begin(home);closeLease.Move(new Point(170,950));closing.FailedReturns=1000;closing.Restored.Reset();closeLease.Dispose();
            int afterDispose=closing.Moves;closing.Position=new Point(171,950);Thread.Sleep(100);
            Check(closing.Position==new Point(171,950) && closing.Moves==afterDispose,"Dispose performs no delayed move after the input hook would be removed");
            Check(closeLease.ReturnFailed && !closeLease.RestorationPending,"Dispose records native return rejection and terminates retries");
        }
        private static void ExpiredForward()
        {
            var backend=new Backend();Point home=backend.Position;
            using(var lease=backend.Lease(10000))
            {
                lease.Begin(home);lease.Move(new Point(170,950));
                // Simulate resuming a suspended/UI-blocked process before its
                // timer callback runs. The dispatch path must enforce expiry.
                object sync=Field(lease,"sync");lock(sync)
                {
                    typeof(TrayPointerLease).GetField("started",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(lease,unchecked(Environment.TickCount-10001));
                    Check(!lease.Click(new Point(170,950),MouseButtons.Left) && backend.Clicks==0,"late resumed dispatch cannot click before watchdog catches up");
                }
                Check(backend.Position==home,"late resumed dispatch restores its old hover");
            }
        }
        private static void CollapsedFrames()
        {
            foreach(uint dpi in new[]{96u,144u,192u})
            {
                using(var source=new Bitmap(200,96))using(var closed=new Bitmap(200,96))using(var opened=new Bitmap(200,96))
                {
                    using(var g=Graphics.FromImage(source))g.Clear(Color.FromArgb(245,245,245));
                    using(var g=Graphics.FromImage(closed)){g.Clear(Color.Red);TrayMirrorForm.DrawOverflowIndicator(g,source,new Rectangle(0,0,200,96),false,false,dpi);}
                    using(var g=Graphics.FromImage(opened)){g.Clear(Color.Red);TrayMirrorForm.DrawOverflowIndicator(g,source,new Rectangle(0,0,200,96),false,true,dpi);}
                    int width=(int)(32*dpi/96),topClosed=96,topOpened=96;
                    for(int y=0;y<96;y++)for(int x=0;x<width;x++)
                    {if(closed.GetPixel(x,y).GetBrightness()<0.4f)topClosed=Math.Min(topClosed,y);if(opened.GetPixel(x,y).GetBrightness()<0.4f)topOpened=Math.Min(topOpened,y);}
                    int center=width/2;
                    Check(closed.GetPixel(center,topClosed+1).GetBrightness()<0.4f,"collapsed chevron apex points up without relying on a previous native frame: "+dpi);
                    Check(opened.GetPixel(center,topOpened+1).GetBrightness()>0.6f,"expanded chevron arms point down: "+dpi);
                    Check(closed.GetPixel(width,48).ToArgb()==Color.Red.ToArgb() && opened.GetPixel(199,48).ToArgb()==Color.Red.ToArgb(),"live neighboring icons outside scaled button remain untouched: "+dpi);
                }
            }
            using(var source=new Bitmap(200,48))using(var result=new Bitmap(200,48))
            {
                using(var g=Graphics.FromImage(source)){g.Clear(Color.Transparent);g.FillRectangle(Brushes.White,12,20,8,8);}
                using(var g=Graphics.FromImage(result)){g.Clear(Color.Red);TrayMirrorForm.DrawOverflowIndicator(g,source,new Rectangle(0,0,200,48),true,false,96);}
                Check(result.GetPixel(0,0).A==1 && result.GetPixel(32,20).ToArgb()==Color.Red.ToArgb(),"transparent arrow cell retains hit-test alpha while leaving neighboring pixels alone");
                Check(result.GetPixel(16,22).R>180,"transparent dark-taskbar glyph preserves genuine light foreground");
            }
        }
        private static void InputSequence()
        {
            Rectangle desktop=SystemInformation.VirtualScreen;
            Point source=new Point(desktop.Left+10,desktop.Top+10),origin=new Point(desktop.Right-10,desktop.Bottom-10);
            foreach(MouseButtons button in new[]{MouseButtons.Left,MouseButtons.Right})
            {
                Native.Input[] input=Native.PointerClick(source,button,origin);
                Check(input.Length==4,"native click has immediate fourth return event: "+button);
                Check(input[0].Data.Mouse.Flags==0xC001 && input[3].Data.Mouse.Flags==0xC001,"both moves use absolute virtual desktop");
                Check(input[1].Data.Mouse.Flags==(button==MouseButtons.Left?2u:8u) && input[2].Data.Mouse.Flags==(button==MouseButtons.Left?4u:16u),"native button down/up ordering");
                Check(input[3].Data.Mouse.X==Native.PointerMove(origin).Data.Mouse.X && input[3].Data.Mouse.Y==Native.PointerMove(origin).Data.Mouse.Y,"return is mapped to original secondary point");
                foreach(Native.Input item in input)Check(item.Data.Mouse.Extra==Native.PointerInputTag,"native event ownership marker");
            }
            Native.Input minimum=Native.PointerMove(new Point(Int32.MinValue,Int32.MinValue));
            Native.Input maximum=Native.PointerMove(new Point(Int32.MaxValue,Int32.MaxValue));
            Check(minimum.Data.Mouse.X==0 && minimum.Data.Mouse.Y==0 && maximum.Data.Mouse.X==65535 && maximum.Data.Mouse.Y==65535,"off-desktop pointer coordinates clamp at both edges");
        }
        private static void VisibleClicks()
        {
            var backend=new Backend();Point origin=backend.Position,target=new Point(170,950);
            using(var lease=backend.Lease(1500))
            {
                lease.Begin(origin);Check(backend.Moves==0 && lease.IsOwned && !lease.HasMoved,"visible tray waits without hover warp");
                Check(lease.Click(target,MouseButtons.Left),"visible native left click accepted");
                Check(backend.Position==origin && backend.Moves==0 && backend.Return==origin && !lease.IsOwned,"visible left click returns in its own batch");
                lease.Begin(origin);Check(lease.Click(target,MouseButtons.Right) && backend.Button==MouseButtons.Right,"visible native right click preserved");
                lease.Begin(origin);Check(lease.Click(target,MouseButtons.Left),"first double-click dispatch");
                lease.Begin(origin);Check(lease.Click(target,MouseButtons.Left) && backend.Clicks==4,"second double-click dispatch requires no delayed restore timer");
                Check(backend.Position==origin && backend.Moves==0,"both double-click dispatches finish at origin");
            }
        }
        private static void HiddenLease()
        {
            var backend=new Backend();Point origin=backend.Position;
            using(var lease=backend.Lease(1500))
            {
                lease.Begin(origin);Check(lease.Move(new Point(170,1080)) && lease.HasMoved,"auto-hide reveal acquires tracked hover");
                lease.ObserveMovement(Native.PointerInputTag);Check(lease.IsOwned,"tagged injected hover does not revoke lease");
                Check(lease.Move(new Point(170,950)) && lease.IsOwned,"animated tray remap remains owned");
                lease.Finish();Check(backend.Position==origin && !lease.IsOwned,"readiness timeout or cancel restores tracked hover");
                int moves=backend.Moves;lease.Finish();Check(backend.Moves==moves,"completion is idempotent");
                lease.Begin(origin);lease.Move(new Point(170,950));
                Check(lease.Click(new Point(170,950),MouseButtons.Right) && backend.Position==origin,"auto-hidden right click returns immediately");
            }
            var closing=new Backend();var closeLease=closing.Lease(1500);Point start=closing.Position;
            closeLease.Begin(start);closeLease.Move(new Point(100,900));closeLease.Dispose();Check(closing.Position==start,"form close restores owned hover");
            var rounded=new Backend{Offset=1};Point roundedOrigin=rounded.Position;
            using(var lease=rounded.Lease(1500))
            {
                lease.Begin(roundedOrigin);Check(lease.Move(new Point(-100,950)) && lease.IsOwned,"absolute-input rounding retains owned hover at negative coordinates");
                lease.Finish();Check(Math.Abs(rounded.Position.X-roundedOrigin.X)<=1 && Math.Abs(rounded.Position.Y-roundedOrigin.Y)<=1,"rounded auto-hide hover restores near original point");
            }
        }
        private static void UserMovement()
        {
            var backend=new Backend();Point target=new Point(170,950),deliberate=new Point(2300,500);
            using(var lease=backend.Lease(1500))
            {
                lease.Begin(backend.Position);lease.Move(target);backend.Position=deliberate;lease.ObserveMovement(IntPtr.Zero);lease.Finish();
                Check(backend.Position==deliberate && backend.Moves==1,"cleanup retains genuine movement");
                lease.Begin(deliberate);lease.Move(target);lease.ObserveMovement(IntPtr.Zero);backend.Position=deliberate;lease.ObserveMovement(IntPtr.Zero);backend.Position=target;
                Check(!lease.Click(target,MouseButtons.Left),"moving away and back cannot reclaim injection ownership");
                Check(backend.Position==target && backend.Clicks==0,"away-and-back movement is never overwritten");
                lease.Begin(target);lease.ObserveMovement(new IntPtr(123));Check(!lease.IsOwned,"other application's injected motion counts as user movement");lease.Finish();
                lease.Begin(target);backend.Position=deliberate;Check(!lease.IsOwned,"position guard protects movement even if hook was unavailable");lease.Finish();
                Check(backend.Position==deliberate,"position-only cancellation retains new point");
                lease.Begin(deliberate);lease.Move(target);backend.Position=new Point(target.X+1,target.Y);lease.ObserveUserMovement();lease.Finish();
                Check(backend.Position==new Point(target.X+1,target.Y),"one-pixel genuine movement revokes rounding tolerance");
            }
            var closing=new Backend();var closeLease=closing.Lease(1500);closeLease.Begin(closing.Position);closeLease.Move(target);closing.Position=deliberate;closeLease.ObserveUserMovement();closeLease.Dispose();
            Check(closing.Position==deliberate,"form close never forces cursor back after real input");
        }
        private static void Failures()
        {
            foreach(int mode in new[]{1,2})
            {
                var backend=new Backend{Mode=mode};Point origin=backend.Position;
                using(var lease=backend.Lease(1500))
                {
                    lease.Begin(origin);bool accepted=false;
                    try{accepted=lease.Click(new Point(170,950),MouseButtons.Left);}catch(InvalidOperationException){}
                    Check(!accepted && backend.Position==origin,"partial or throwing click restores owned source: "+mode);
                    Check(!lease.IsOwned,"failure releases lease: "+mode);
                }
            }
        }
        private static void Deadline()
        {
            var backend=new Backend();Point origin=backend.Position;
            using(var lease=backend.Lease(100))
            {
                lease.Begin(origin);lease.Move(new Point(170,950));backend.Restored.Reset();
                // No Application.DoEvents: restoration must work while the UI
                // thread is blocked. Event is signaled only by backend move.
                Check(backend.Restored.WaitOne(1000),"watchdog runs independently of blocked Forms pump");
                Check(backend.Position==origin && !lease.IsOwned,"deadline restores and releases pointer lease");
                Check(!lease.Click(new Point(170,950),MouseButtons.Left) && backend.Clicks==0,"late UI tick cannot dispatch after watchdog expiry");
            }
            var moved=new Backend();Point deliberate=new Point(2300,500);
            using(var lease=moved.Lease(100))
            {
                lease.Begin(moved.Position);lease.Move(new Point(170,950));moved.Restored.Reset();moved.Position=deliberate;lease.ObserveUserMovement();
                Check(!moved.Restored.WaitOne(200) && moved.Position==deliberate,"watchdog does not restore after deliberate movement");
            }
        }
        private sealed class ExposureWindow : Form
        {
            protected override bool ShowWithoutActivation{get{return true;}}
        }
        private static void TaskbarExposure()
        {
            Rectangle screen=new Rectangle(-1920,-200,1920,1080),bottom=new Rectangle(-1920,832,1920,48);
            Check(TrayMirrorForm.TaskbarExposed(screen,bottom,true,false),"visible native bar with negative monitor coordinates is exposed");
            Check(!TrayMirrorForm.TaskbarExposed(screen,bottom,false,false),"invisible native host hides mirror even with stale full bounds");
            Check(!TrayMirrorForm.TaskbarExposed(screen,bottom,true,true),"DWM-cloaked native host hides mirror");
            Check(!TrayMirrorForm.TaskbarExposed(screen,new Rectangle(-1920,900,1920,48),true,false),"offscreen native taskbar hides mirror");
            Check(!TrayMirrorForm.TaskbarExposed(screen,new Rectangle(-1920,878,1920,48),true,false),"bottom auto-hide reveal strip hides mirror");
            Check(!TrayMirrorForm.TaskbarExposed(screen,new Rectangle(-1920,-246,1920,48),true,false),"top auto-hide reveal strip hides mirror");
            Check(!TrayMirrorForm.TaskbarExposed(screen,new Rectangle(-1966,-200,48,1080),true,false),"left auto-hide reveal strip hides mirror");
            Check(!TrayMirrorForm.TaskbarExposed(screen,new Rectangle(-2,-200,48,1080),true,false),"right auto-hide reveal strip hides mirror");
            Check(TrayMirrorForm.TaskbarExposed(screen,new Rectangle(-1919,-200,48,1080),true,false),"exposed vertical native bar remains eligible");
            Rectangle actual;Check(!TrayMirrorForm.ReadTaskbarExposure(IntPtr.Zero,screen,out actual),"destroyed/missing native host cannot show mirror");
            Screen primary=Screen.PrimaryScreen;
            using(var own=new ExposureWindow{Text="任务栏可见性只读验证",ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.FixedToolWindow,StartPosition=FormStartPosition.Manual,Location=new Point(primary.Bounds.Left+20,primary.Bounds.Top+20),Size=new Size(220,60)})
            {
                IntPtr handle=own.Handle;
                Check(!TrayMirrorForm.ReadTaskbarExposure(handle,primary.Bounds,out actual),"native IsWindowVisible rejects own hidden fixture host");
                own.Show();Application.DoEvents();
                Check(TrayMirrorForm.ReadTaskbarExposure(handle,primary.Bounds,out actual),"native bounds and DWM query accept own shown fixture host");
                own.Hide();Application.DoEvents();
                Check(!TrayMirrorForm.ReadTaskbarExposure(handle,primary.Bounds,out actual),"native hide rejects retained fixture rectangle");
            }
        }
        private static object Field(object value,string name){return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
        private sealed class BridgeWindow : NativeWindow,IDisposable
        {
            private const string BridgeClass="Windows.UI.Composition.DesktopWindowContentBridge";
            private delegate IntPtr WindowProc(IntPtr h,uint message,IntPtr w,IntPtr l);
            [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct WindowClass
            {
                internal uint Style;internal WindowProc Procedure;internal int ClassExtra,WindowExtra;
                internal IntPtr Instance,Icon,Cursor,Background;internal string Menu,Class;
            }
            [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ushort RegisterClass(ref WindowClass windowClass);
            [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWindowEx(int exStyle,string cls,string title,int style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
            [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr DefWindowProc(IntPtr h,uint message,IntPtr w,IntPtr l);
            private static readonly WindowProc procedure=DefWindowProc;
            private static bool registered;
            internal BridgeWindow(IntPtr parent,bool visible,int width)
            {
                if(!registered)
                {
                    var cls=new WindowClass{Procedure=procedure,Instance=Native.GetModuleHandle(null),Class=BridgeClass};
                    if(RegisterClass(ref cls)==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    registered=true;
                }
                IntPtr h=CreateWindowEx(0,BridgeClass,"DesktopWindowXamlSource",0x40000000|(visible?0x10000000:0),0,0,width,35,parent,IntPtr.Zero,Native.GetModuleHandle(null),IntPtr.Zero);
                if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                AssignHandle(h);
            }
            public void Dispose(){DestroyHandle();}
        }
        private static void ContentBridgeExposure()
        {
            Screen primary=Screen.PrimaryScreen;
            using(var own=new ExposureWindow{Text="自身内容桥可见性夹具",ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(primary.Bounds.Left+30,primary.Bounds.Top+30),ClientSize=new Size(300,70)})
            {
                own.Show();Application.DoEvents();Rectangle bar=Native.Bounds(own.Handle),actual;
                Check(TrayMirrorForm.TaskbarContentExposed(own.Handle,primary.Bounds,bar),"Shell layouts without known content bridge retain old exposure behavior");
                using(var hidden=new BridgeWindow(own.Handle,false,240))
                {
                    Check(Native.ClassName(hidden.Handle)=="Windows.UI.Composition.DesktopWindowContentBridge","fixture owns registered native content-bridge class");
                    Check(!TrayMirrorForm.TaskbarContentExposed(own.Handle,primary.Bounds,bar),"known hidden bridge vetoes exposure despite full host geometry");
                    Check(TrayMirrorForm.ReadTaskbarExposure(own.Handle,primary.Bounds,out actual),"non-Shell fixture host is not subject to Shell-only content veto");
                    using(var shown=new BridgeWindow(own.Handle,true,240))
                    {
                        Check(TrayMirrorForm.TaskbarContentExposed(own.Handle,primary.Bounds,bar),"one visible bridge among hidden bridges allows exposure");
                        Native.ShowWindow(shown.Handle,0);
                        Check(!TrayMirrorForm.TaskbarContentExposed(own.Handle,primary.Bounds,bar),"all bridges hidden rejects unchanged full host bounds");
                        Native.ShowWindow(shown.Handle,8);
                        Check(TrayMirrorForm.TaskbarContentExposed(own.Handle,primary.Bounds,bar),"showing own content bridge restores exposure eligibility");
                    }
                    using(var tiny=new BridgeWindow(own.Handle,true,1))
                        Check(!TrayMirrorForm.TaskbarContentExposed(own.Handle,primary.Bounds,bar),"visible one-pixel content strip does not prove a rendered taskbar");
                    using(var outside=new BridgeWindow(own.Handle,true,240))
                    {
                        Native.SetWindowPos(outside.Handle,IntPtr.Zero,0,200,240,35,4|16);
                        Check(!TrayMirrorForm.TaskbarContentExposed(own.Handle,primary.Bounds,bar),"visible bridge clipped outside host has no exposed content area");
                    }
                }
                Check(TrayMirrorForm.TaskbarContentExposed(own.Handle,primary.Bounds,bar),"destroyed content bridges restore no-bridge compatibility");
                own.Close();
            }
        }
        private static void HiddenStartup()
        {
            Settings previous=Settings.Current;Screen target=null;
            foreach(Screen candidate in Screen.AllScreens)if(!candidate.Primary){target=candidate;break;}
            Check(target!=null,"secondary screen available for hidden-start integration");
            try
            {
                Settings.Current=new Settings{ScreenName=target.DeviceName,Desktop=false,AltTab=false,Tray=true,HighResponse=true,TransparentTray=true};
                using(var own=new ExposureWindow{Text="隐藏启动自己的任务栏夹具",ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(target.Bounds.Left+30,target.Bounds.Top+30),Size=new Size(250,60)})
                {
                    IntPtr hidden=own.Handle;
                    var capture=new TrayCaptureBackend
                    {
                        TargetScreen=delegate{return target;},TargetTaskbar=delegate{return hidden;},ReadTargetExposure=TrayMirrorForm.ReadTaskbarExposure,
                        FindPrimaryTray=delegate{return Rectangle.Empty;},PrimaryContentReady=delegate(Rectangle r){return false;},
                        Capture=delegate(Rectangle r){throw new Exception("hidden startup must never capture desktop pixels");},PrimaryDpi=delegate{return 96u;},EnableInputHooks=true
                    };
                    var backend=new Backend{Position=Cursor.Position};
                    using(var mirror=new TrayMirrorForm(delegate{},capture,backend.Lease(1500)))
                    {
                    mirror.Start();IntPtr handle=mirror.Handle;
                    Check(mirror.IsHandleCreated && !mirror.Visible && !Native.IsWindowVisible(handle),"Start creates a hidden mirror without outer Show resurrecting it");
                    var hook=(Thread)Field(mirror,"popupHookThread");
                    var timer=(System.Windows.Forms.Timer)Field(mirror,"timer");
                    Check(hook!=null && timer.Enabled,"hidden Start initializes hook thread and capture timer");
                    mirror.Start();Check(Object.ReferenceEquals(hook,Field(mirror,"popupHookThread")) && mirror.Handle==handle,"repeated Start reuses hook thread and native handle");
                    Application.DoEvents();Check(!mirror.Visible && !Native.IsWindowVisible(handle),"hidden startup stays hidden after pending messages run");
                    Check(mirror.ForwardCount==0 && !(bool)Field(mirror,"forwarding"),"hidden startup does not reveal or inject tray input");
                    mirror.Close();
                    Check(mirror.IsDisposed && !Native.IsWindow(handle),"Close disposes a started mirror that was never shown");
                    Check(!timer.Enabled && (bool)Field(mirror,"stoppingHooks"),"hidden Close stops poll timer and requests hook shutdown");
                    Check((bool)Field(Field(mirror,"pointer"),"disposed"),"hidden Close disposes pointer watchdog");
                    Check(hook.Join(1200),"hidden Close terminates native hook message thread");
                    Check((IntPtr)Field(mirror,"popupMouseHook")==IntPtr.Zero && (IntPtr)Field(mirror,"popupKeyboardHook")==IntPtr.Zero,"hidden Close releases native mouse and keyboard hooks");
                    mirror.Start();Check(mirror.IsDisposed && !timer.Enabled,"Start after Close never recreates disposed resources");
                    Check(backend.Moves==0 && backend.Clicks==0,"hidden startup lifecycle uses no real or simulated pointer movement");
                    }
                }
            }
            finally{Settings.Current=previous;}
        }
        private static void Check(bool condition,string name){if(!condition)throw new Exception(name);assertions++;Console.WriteLine("OK: "+name);}
    }
}
