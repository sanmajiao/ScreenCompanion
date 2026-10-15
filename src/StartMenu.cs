using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenCompanion
{
    // Observe genuine gestures and mirror the primary native Start surface.
    // Never inject Win keys or show a closed Start menu window.
    internal sealed class StartMenuController : IDisposable
    {
        private readonly Control dispatcher;
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        private readonly WinKeyGesture gesture=new WinKeyGesture();
        private readonly KeyboardListener keyboard;
        private readonly Dictionary<IntPtr,StartMenuPlacement> placements=new Dictionary<IntPtr,StartMenuPlacement>();
        private StartMenuMirrorForm mirror;
        private int started;
        private bool disposed,observed;
        internal StartMenuController(Control owner)
        {
            dispatcher=owner;timer.Interval=ResponsePolicy.StartDiscovery;timer.Tick+=delegate{Track();};
            // Repair old relocated canvases, including hidden/cloaked windows,
            // without opening them or changing their visibility or taskbar.
            CaptureNamedStartWindows(delegate(string title){return Native.FindWindow("Windows.UI.Core.CoreWindow",title);},IsStartHost,EnsurePrimary);
            Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(IsStartHost(h))EnsurePrimary(h);return true;},IntPtr.Zero);
            keyboard=new KeyboardListener(delegate(Native.KeyboardData key,bool down,bool up)
            {
                bool tap=gesture.Update(key.Key,down,up);
                if(down && (key.Key==0x5B || key.Key==0x5C) &&
                    (Native.KeyPressed(0x10) || Native.KeyPressed(0x11) || Native.KeyPressed(0x12)))gesture.Cancel();
                // Ctrl+Esc invokes the same native Start menu and is useful on
                // keyboards without a Win key. Other Escape gestures stay intact.
                if(tap || (down && key.Key==27 && Native.KeyPressed(0x11) &&
                    !Native.KeyPressed(0x10) && !Native.KeyPressed(0x12) && !Native.KeyPressed(0x5B) && !Native.KeyPressed(0x5C)))
                    try{dispatcher.BeginInvoke(new Action(Begin));}catch{}
            });
        }
        private static bool IsStartHost(IntPtr h)
        {
            if(h==IntPtr.Zero || !Native.IsWindow(h))return false;
            Rectangle r=Native.Bounds(h);if(r.Width<100 || r.Height<100)return false;
            try{using(var process=Process.GetProcessById((int)Native.ProcessId(h)))
            {
                // Some Windows builds expose Start through SearchHost, with its
                // search box focused. Check only after an explicit Start gesture.
                string name=process.ProcessName;
                return name.Equals("StartMenuExperienceHost",StringComparison.OrdinalIgnoreCase);
            }}
            catch{return false;}
        }
        internal static bool IsStartMenu(IntPtr h)
        {
            if(!IsStartHost(h) || !Native.IsWindowVisible(h))return false;
            int cloaked;return Native.DwmGetWindowAttribute(h,14,out cloaked,4)!=0 || cloaked==0;
        }
        internal static void CaptureNamedStartWindows(Func<string,IntPtr> lookup,Func<IntPtr,bool> eligible,Action<IntPtr> capture)
        {
            // UWP CoreWindow may be absent from EnumWindows on some builds.
            // Eligibility deliberately includes hidden/cloaked Start hosts.
            foreach(string title in new[]{"Start","开始"})
            {IntPtr h=lookup(title);if(h!=IntPtr.Zero && eligible(h))capture(h);}
        }
        private void EnsurePrimary(IntPtr h)
        {
            StartMenuPlacement placement;
            if(!placements.TryGetValue(h,out placement) || !placement.Valid)
            {placement=new StartMenuPlacement(h);placements[h]=placement;}
            placement.EnsurePrimary();
        }
        private void Begin()
        {
            if(disposed || Settings.TargetScreen()==null)return;
            started=Environment.TickCount;observed=false;timer.Interval=ResponsePolicy.StartDiscovery;Track();if(!disposed && (observed || Environment.TickCount-started<2000))timer.Start();
        }
        internal static IntPtr FindStartWindow()
        {
            foreach(string title in new[]{"Start","开始"})
            {
                IntPtr h=Native.FindWindow("Windows.UI.Core.CoreWindow",title);
                if(IsStartMenu(h))return h;
            }
            IntPtr found=IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(IsStartMenu(h)){found=h;return false;}return true;},IntPtr.Zero);
            return found;
        }
        private static bool StartForeground(IntPtr h)
        {
            if(IsStartMenu(h))return true;
            try{using(var process=Process.GetProcessById((int)Native.ProcessId(h)))return process.ProcessName.Equals("SearchHost",StringComparison.OrdinalIgnoreCase);}
            catch{return false;}
        }
        private void Track()
        {
            if(disposed)return;
            Screen target=Settings.TargetScreen();if(target==null){Stop();return;}
            IntPtr foreground=Native.GetForegroundWindow(),found=IntPtr.Zero;
            if(StartForeground(foreground))found=FindStartWindow();
            if(found==IntPtr.Zero)
            {
                if(observed || Environment.TickCount-started>2000)Stop();
                return;
            }
            EnsurePrimary(found);observed=true;
            if(mirror==null || mirror.IsDisposed)mirror=new StartMenuMirrorForm();
            IntPtr bar=TrayMirrorForm.FindTargetTaskbar();
            Rectangle available=StartTaskbarGeometry.AvailableArea(target.WorkingArea,target.Bounds,Native.Bounds(bar));
            mirror.Present(found,available);
            timer.Interval=ResponsePolicy.PopupTracking;
        }
        private void Stop()
        {
            timer.Stop();if(mirror!=null){mirror.Clear();mirror.Hide();}
            // Keep records through dismissal for reused hidden/cloaked canvases.
            foreach(var placement in placements.Values)placement.Restore();
            observed=false;
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;keyboard.Dispose();Stop();
            if(mirror!=null){mirror.Dispose();mirror=null;}timer.Dispose();placements.Clear();
        }
    }

    // Leave Shell state intact and keep its real exposed edge available for hover.
    internal static class StartTaskbarGeometry
    {
        internal static Rectangle AvailableArea(Rectangle work,Rectangle screen,Rectangle bar)
        {
            Rectangle visible=Rectangle.Intersect(screen,bar),overlap=Rectangle.Intersect(work,visible);
            if(overlap.Width<=0 || overlap.Height<=0)return work;
            Rectangle result=work;
            if(visible.Width>=screen.Width-4 && visible.Height<=screen.Height/4)
            {
                if(visible.Top==screen.Top)result=Rectangle.FromLTRB(work.Left,Math.Max(work.Top,visible.Bottom),work.Right,work.Bottom);
                else if(visible.Bottom==screen.Bottom)result=Rectangle.FromLTRB(work.Left,work.Top,work.Right,Math.Min(work.Bottom,visible.Top));
            }
            else if(visible.Height>=screen.Height-4 && visible.Width<=screen.Width/4)
            {
                if(visible.Left==screen.Left)result=Rectangle.FromLTRB(Math.Max(work.Left,visible.Right),work.Top,work.Right,work.Bottom);
                else if(visible.Right==screen.Right)result=Rectangle.FromLTRB(work.Left,work.Top,Math.Min(work.Right,visible.Left),work.Bottom);
            }
            return result.Width>0 && result.Height>0?result:work;
        }
    }

    // Restore a recorded HWND identity even after the shell hides/cloaks it.
    // Neither normalization nor restoration changes visibility, focus or z-order.
    internal sealed class StartMenuPlacement
    {
        private readonly IntPtr handle;
        private readonly uint process;
        private readonly string windowClass;
        private Rectangle original;
        private bool moved;
        internal StartMenuPlacement(IntPtr h)
        {handle=h;process=Native.ProcessId(h);windowClass=Native.ClassName(h);original=PrimaryBounds(Native.Bounds(h));}
        internal bool Valid {get{return Native.IsWindow(handle) && Native.ProcessId(handle)==process && Native.ClassName(handle)==windowClass;}}
        internal Rectangle Original {get{return original;}}
        internal static Rectangle PrimaryBounds(Rectangle bounds)
        {
            Screen primary=Screen.PrimaryScreen;
            if(primary==null || bounds.IsEmpty)return bounds;
            Screen source=Screen.FromRectangle(bounds);
            if(source.Primary)return bounds;
            Rectangle from=source.Bounds,to=primary.Bounds;
            if(bounds.Width>=from.Width-16)
            {
                // Preserve the native taskbar margin of a monitor-sized canvas.
                int bottomMargin=Math.Max(0,from.Bottom-bounds.Bottom);
                return new Rectangle(to.Left,to.Top,to.Width,Math.Max(100,to.Height-bottomMargin));
            }
            Rectangle work=primary.WorkingArea;
            int width=Math.Min(bounds.Width,work.Width),height=Math.Min(bounds.Height,work.Height);
            return new Rectangle(work.Left+(work.Width-width)/2,work.Bottom-height,width,height);
        }
        internal void EnsurePrimary()
        {
            if(!Valid)return;
            Rectangle current=Native.Bounds(handle),desired=PrimaryBounds(current);
            if(current==desired)return;
            // Use current shell dimensions after layout/DPI/topology changes.
            original=desired;
            if(Native.SetWindowPos(handle,IntPtr.Zero,desired.X,desired.Y,desired.Width,desired.Height,4|16))moved=true;
        }
        internal void Restore()
        {
            if(!moved || !Valid || original.IsEmpty)return;
            Rectangle desired=PrimaryBounds(original);
            if(Native.Bounds(handle)!=desired)
                Native.SetWindowPos(handle,IntPtr.Zero,desired.X,desired.Y,desired.Width,desired.Height,4|16);
        }
    }

    internal sealed class StartMenuMirrorForm : Form
    {
        private IntPtr source,thumbnail;
        private Rectangle sourceBounds;
        private bool alphaComposition;
        private StartMenuPointerListener pointer;
        private StartMenuClickSnapshot clickSnapshot;
        private int clickEpoch;
        [StructLayout(LayoutKind.Sequential)] private struct Blur
        {internal uint Flags;[MarshalAs(UnmanagedType.Bool)] internal bool Enable;internal IntPtr Region;[MarshalAs(UnmanagedType.Bool)] internal bool Transition;}
        [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr h,ref Blur blur);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        internal StartMenuMirrorForm()
        {
            Text="副屏原生开始菜单镜像";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;
            StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;BackColor=Color.Black;
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=8|0x80|0x08000000;return p;}}
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Empty blur region enables alpha composition. Source canvas alpha
            // exposes the secondary desktop rather than a monitor-sized backdrop.
            IntPtr region=CreateRectRgn(0,0,-1,-1);
            try{var blur=new Blur{Flags=1|2,Enable=true,Region=region};alphaComposition=region!=IntPtr.Zero && DwmEnableBlurBehindWindow(Handle,ref blur)==0;}
            finally{if(region!=IntPtr.Zero)DeleteObject(region);}
        }
        internal bool HasThumbnail {get{return thumbnail!=IntPtr.Zero;}}
        internal IntPtr Source {get{return source;}}
        internal StartMenuClickSnapshot ClickSnapshot {get{return clickSnapshot;}}
        internal StartMenuPointerListener PointerListener {get{return pointer;}}
        internal static Rectangle Fit(Rectangle source,Rectangle target)
        {
            if(source.Width<=0 || source.Height<=0 || target.Width<=0 || target.Height<=0)return Rectangle.Empty;
            double scale=Math.Min(target.Width/(double)source.Width,target.Height/(double)source.Height);
            int width=Math.Max(1,(int)Math.Round(source.Width*scale)),height=Math.Max(1,(int)Math.Round(source.Height*scale));
            return new Rectangle(target.Left+(target.Width-width)/2,target.Bottom-height,width,height);
        }
        internal static Point Map(Point point,Size destination,Rectangle source)
        {
            return new Point(source.Left+Math.Min(source.Width-1,Math.Max(0,(int)(point.X*source.Width/(double)Math.Max(1,destination.Width)))),
                source.Top+Math.Min(source.Height-1,Math.Max(0,(int)(point.Y*source.Height/(double)Math.Max(1,destination.Height)))));
        }
        internal bool Present(IntPtr h,Rectangle target)
        {
            if(!Native.IsWindow(h) || !Native.IsWindowVisible(h)){Clear();Hide();return false;}
            int cloaked;if(Native.DwmGetWindowAttribute(h,14,out cloaked,4)==0 && cloaked!=0){Clear();Hide();return false;}
            Rectangle bounds=Native.Bounds(h),desired=Fit(bounds,target);
            if(desired.IsEmpty){Clear();Hide();return false;}
            if(!IsHandleCreated){IntPtr unused=Handle;}
            if(!alphaComposition){Clear();Hide();return false;}
            if(source!=h || thumbnail==IntPtr.Zero)
            {
                Clear();
                if(Native.DwmRegisterThumbnail(Handle,h,out thumbnail)!=0){thumbnail=IntPtr.Zero;Hide();return false;}
                source=h;
            }
            sourceBounds=bounds;Bounds=desired;
            var properties=new Native.Thumb{Flags=1|4|8|16,Destination=new Native.Rect(new Rectangle(Point.Empty,ClientSize)),Opacity=255,Visible=true,ClientOnly=false};
            if(Native.DwmUpdateThumbnailProperties(thumbnail,ref properties)!=0){Clear();Hide();return false;}
            if(pointer==null)pointer=new StartMenuPointerListener(QueueClick);
            if(!pointer.Ready){Hide();return false;}
            if(!Visible)Show();
            Native.SetWindowPos(Handle,new IntPtr(-1),desired.X,desired.Y,desired.Width,desired.Height,16|64);
            if(clickSnapshot==null || clickSnapshot.Source!=source || clickSnapshot.SourceBounds!=sourceBounds || clickSnapshot.MirrorBounds!=Native.Bounds(Handle) ||
                clickSnapshot.SourceProcess!=Native.ProcessId(source) || clickSnapshot.MirrorProcess!=Native.ProcessId(Handle))
                clickSnapshot=new StartMenuClickSnapshot(Handle,Native.Bounds(Handle),source,sourceBounds,++clickEpoch);
            pointer.Publish(clickSnapshot);
            return true;
        }
        protected override void WndProc(ref Message message)
        {
            if(message.Msg==0x21){message.Result=new IntPtr(3);return;} // MA_NOACTIVATE keeps genuine Start focus.
            base.WndProc(ref message);
        }
        private void QueueClick(StartMenuClickRequest request)
        {
            // The paired physical down/up was consumed before shell light-dismiss.
            // Revalidate after queuing: never replay against a replacement menu.
            try{BeginInvoke(new Action(delegate
            {
                if(IsDisposed || !Object.ReferenceEquals(clickSnapshot,request.Snapshot) || !StartMenuPointerListener.Valid(request.Snapshot))return;
                Native.ClickPreservingCursor(request.Snapshot.Source,request.Snapshot.SourceBounds,request.SourcePoint,request.Button);
            }));}catch{}
        }
        internal void Clear()
        {
            clickSnapshot=null;clickEpoch++;
            if(pointer!=null){pointer.Dispose();pointer=null;}
            if(thumbnail!=IntPtr.Zero){Native.DwmUnregisterThumbnail(thumbnail);thumbnail=IntPtr.Zero;}
            source=IntPtr.Zero;sourceBounds=Rectangle.Empty;
        }
        protected override void Dispose(bool disposing){Clear();base.Dispose(disposing);}
    }

    internal sealed class StartMenuClickSnapshot
    {
        internal readonly IntPtr Mirror,Source;
        internal readonly uint MirrorProcess,SourceProcess;
        internal readonly Rectangle MirrorBounds,SourceBounds;
        internal readonly int Epoch;
        internal StartMenuClickSnapshot(IntPtr mirror,Rectangle mirrorBounds,IntPtr source,Rectangle sourceBounds,int epoch)
        {Mirror=mirror;Source=source;MirrorBounds=mirrorBounds;SourceBounds=sourceBounds;Epoch=epoch;MirrorProcess=Native.ProcessId(mirror);SourceProcess=Native.ProcessId(source);}
        internal Point Map(Point point)
        {return StartMenuMirrorForm.Map(new Point(point.X-MirrorBounds.Left,point.Y-MirrorBounds.Top),MirrorBounds.Size,SourceBounds);}
    }
    internal sealed class StartMenuClickRequest
    {
        internal readonly StartMenuClickSnapshot Snapshot;
        internal readonly Point SourcePoint;
        internal readonly MouseButtons Button;
        internal StartMenuClickRequest(StartMenuClickSnapshot snapshot,Point point,MouseButtons button)
        {Snapshot=snapshot;SourcePoint=snapshot.Map(point);Button=button;}
    }
    // Pair state is independent of native hooks so light-dismiss ordering can be
    // tested without synthesizing global pointer or keyboard input.
    internal sealed class StartMenuClickRouter
    {
        private volatile StartMenuClickSnapshot pending;
        private MouseButtons button;
        private Point downPoint;
        private bool dragged;
        private bool cancelled;
        private readonly Size dragSize;
        internal StartMenuClickRouter(Size threshold){dragSize=threshold;}
        internal bool HasPending {get{return pending!=null;}}
        internal void Clear(){pending=null;button=MouseButtons.None;dragged=false;cancelled=false;}
        internal bool RouteInput(uint message,Point point,IntPtr extra,StartMenuClickSnapshot snapshot,bool canStart,bool valid,out StartMenuClickRequest request)
        {
            request=null;
            if(extra==Native.PointerInputTag)return false;
            return Route(message,point,snapshot,canStart,valid,out request);
        }
        internal bool Route(uint message,Point point,StartMenuClickSnapshot snapshot,bool canStart,bool valid,out StartMenuClickRequest request)
        {
            request=null;
            // Once a down was consumed, consume its matching up even if Clear,
            // cloak or a geometry change cancels replay in the meantime.
            if(pending!=null && (!Object.ReferenceEquals(snapshot,pending) || !valid))cancelled=true;
            if(message==0x200)
            {
                if(pending!=null && (Math.Abs((long)point.X-downPoint.X)>dragSize.Width/2 || Math.Abs((long)point.Y-downPoint.Y)>dragSize.Height/2))dragged=true;
                return false;
            }
            MouseButtons pressed=message==0x201 || message==0x202?MouseButtons.Left:message==0x204 || message==0x205?MouseButtons.Right:MouseButtons.None;
            if(pressed==MouseButtons.None)return false;
            bool down=message==0x201 || message==0x204;
            if(down)
            {
                if(pending!=null || !valid || !canStart || snapshot==null)return false;
                pending=snapshot;button=pressed;downPoint=point;dragged=false;cancelled=false;return true;
            }
            if(pending==null || pressed!=button)return false;
            if(!cancelled && !dragged && pending.MirrorBounds.Contains(point) && Math.Abs((long)point.X-downPoint.X)<=dragSize.Width/2 && Math.Abs((long)point.Y-downPoint.Y)<=dragSize.Height/2)
                request=new StartMenuClickRequest(pending,point,button);
            Clear();return true;
        }
    }
    internal sealed class StartMenuPointerListener : IDisposable
    {
        private readonly Thread thread;
        private readonly Action<StartMenuClickRequest> callback;
        private readonly StartMenuClickRouter router=new StartMenuClickRouter(SystemInformation.DragSize);
        private volatile StartMenuClickSnapshot snapshot;
        private volatile bool stopping;
        private volatile uint threadId;
        private volatile bool completed;
        private int responseSerial;
        private int disposeRequested;
        private IntPtr hook;
        private Native.HookProc procedure;
        [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr h,UIntPtr id,uint milliseconds,IntPtr procedure);
        [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr h,UIntPtr id);
        internal StartMenuPointerListener(Action<StartMenuClickRequest> action)
        {callback=action;thread=new Thread(Run){IsBackground=true,Name="ScreenCompanion Start mirror pointer"};thread.Start();}
        internal bool Ready {get{return !stopping && Interlocked.CompareExchange(ref hook,IntPtr.Zero,IntPtr.Zero)!=IntPtr.Zero;}}
        internal bool Completed {get{return completed;}}
        internal void Publish(StartMenuClickSnapshot value){if(!stopping)snapshot=value;}
        internal bool Responsive(int timeout)
        {
            int before=Interlocked.CompareExchange(ref responseSerial,0,0);uint id=threadId;
            if(id==0 || !Native.PostThreadMessage(id,0x8001,IntPtr.Zero,IntPtr.Zero))return false;
            int start=Environment.TickCount;
            while(unchecked(Environment.TickCount-start)<timeout)
            {if(Interlocked.CompareExchange(ref responseSerial,0,0)!=before)return true;Thread.Sleep(1);}
            return false;
        }
        internal static bool Valid(StartMenuClickSnapshot value)
        {
            if(value==null || !Native.IsWindow(value.Mirror) || !Native.IsWindow(value.Source) ||
                !Native.IsWindowVisible(value.Mirror) || !Native.IsWindowVisible(value.Source) ||
                Native.ProcessId(value.Mirror)!=value.MirrorProcess || Native.ProcessId(value.Source)!=value.SourceProcess ||
                Native.Bounds(value.Mirror)!=value.MirrorBounds || Native.Bounds(value.Source)!=value.SourceBounds)return false;
            int cloak;return Native.DwmGetWindowAttribute(value.Source,14,out cloak,4)!=0 || cloak==0;
        }
        internal static bool CanBegin(StartMenuClickSnapshot value,Point point)
        {
            return Valid(value) && value.MirrorBounds.Contains(point) &&
                Native.GetAncestor(Native.WindowFromPoint(point),2)==value.Mirror &&
                Native.GetAncestor(Native.WindowFromPoint(value.Map(point)),2)==value.Source;
        }
        private void Run()
        {
            Native.Message message;Native.PeekMessage(out message,IntPtr.Zero,0,0,0);threadId=Native.GetCurrentThreadId();
            UIntPtr shutdownTimer=UIntPtr.Zero;
            try
            {
                if(stopping)return;
                procedure=Observe;
                IntPtr installed=Native.SetWindowsHookEx(14,procedure,Native.GetModuleHandle(null),0);
                Interlocked.Exchange(ref hook,installed);
                if(installed==IntPtr.Zero || stopping)return;
                while(Native.GetMessage(out message,IntPtr.Zero,0,0)>0)
                {
                    if(message.Id==0x8001){Interlocked.Increment(ref responseSerial);continue;}
                    if(message.Id==0x8002)
                    {
                        if(!router.HasPending)break;
                        shutdownTimer=SetTimer(IntPtr.Zero,UIntPtr.Zero,2000,IntPtr.Zero);
                        if(shutdownTimer==UIntPtr.Zero)break;
                        continue;
                    }
                    if(message.Id==0x113 && shutdownTimer!=UIntPtr.Zero)break;
                    Native.TranslateMessage(ref message);Native.DispatchMessage(ref message);
                }
            }
            finally
            {
                if(shutdownTimer!=UIntPtr.Zero)KillTimer(IntPtr.Zero,shutdownTimer);
                snapshot=null;router.Clear();IntPtr installed=Interlocked.Exchange(ref hook,IntPtr.Zero);
                if(installed!=IntPtr.Zero)Native.UnhookWindowsHookEx(installed);threadId=0;completed=true;
            }
        }
        private IntPtr Observe(int code,IntPtr w,IntPtr l)
        {
            try
            {
                if(code>=0 && (!stopping || router.HasPending))
                {
                    var data=(Native.MouseData)Marshal.PtrToStructure(l,typeof(Native.MouseData));
                    if(data.Extra==Native.PointerInputTag)return Native.CallNextHookEx(hook,code,w,l);
                    StartMenuClickSnapshot current=snapshot;uint message=(uint)w.ToInt64();
                    // Movement always passes through; do no cross-process work
                    // on its high-volume path. Pair identity still detects Clear.
                    if(message==0x200)
                    {StartMenuClickRequest ignored;router.RouteInput(message,data.Point,data.Extra,current,false,current!=null,out ignored);}
                    else if(message==0x201 || message==0x202 || message==0x204 || message==0x205)
                    {
                        bool valid=Valid(current),canStart=false;
                        if(valid && (message==0x201 || message==0x204))canStart=CanBegin(current,data.Point);
                        if(stopping || !Object.ReferenceEquals(current,snapshot)){valid=false;canStart=false;}
                        StartMenuClickRequest request;
                        if(router.RouteInput(message,data.Point,data.Extra,current,canStart,valid,out request))
                        {
                            // Once down is consumed, a callback failure must not
                            // leak its up into another window's input sequence.
                            if(request!=null)try{callback(request);}catch{}
                            if(stopping && !router.HasPending)Native.PostThreadMessage(threadId,0x12,IntPtr.Zero,IntPtr.Zero);
                            return new IntPtr(1);
                        }
                    }
                }
            }
            catch { /* Fail open if input routing cannot be validated. */ }
            return Native.CallNextHookEx(hook,code,w,l);
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposeRequested,1)!=0)return;
            stopping=true;snapshot=null;
            uint id=threadId;if(id!=0)Native.PostThreadMessage(id,0x8002,IntPtr.Zero,IntPtr.Zero);
            // Only a previously consumed pair may drain after Clear; no new
            // downs are suppressed. Worker cleanup is bounded and never joined.
        }
    }
}
