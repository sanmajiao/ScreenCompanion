using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ScreenCompanion
{
    // A short lease over only the movements made by this mirror. The watchdog
    // does not use the Forms message pump, so a slow capture cannot strand the
    // pointer on the primary screen. Real input revokes the lease permanently.
    internal sealed class TrayPointerLease : IDisposable
    {
        private readonly object sync=new object();
        private readonly Func<Point> read;
        private readonly Func<Point,bool> move;
        private readonly Func<Point,MouseButtons,Point,bool> click;
        private readonly System.Threading.Timer watchdog;
        private readonly int duration;
        private int userMovement,ownedMovement,started;
        private bool active,moved,disposed;
        private Point origin,expected;
        internal TrayPointerLease(Func<Point> read,Func<Point,bool> move,Func<Point,MouseButtons,Point,bool> click,int duration)
        {
            this.read=read;this.move=move;this.click=click;this.duration=duration;
            watchdog=new System.Threading.Timer(delegate{Expire();},null,System.Threading.Timeout.Infinite,System.Threading.Timeout.Infinite);
        }
        internal void ObserveUserMovement(){System.Threading.Interlocked.Increment(ref userMovement);}
        internal void ObserveMovement(IntPtr extra){if(extra!=Native.PointerInputTag)ObserveUserMovement();}
        internal void Begin(Point point)
        {
            lock(sync)
            {
                if(disposed)return;FinishLocked();origin=expected=point;moved=false;
                ownedMovement=System.Threading.Volatile.Read(ref userMovement);started=Environment.TickCount;active=true;watchdog.Change(25,25);
            }
        }
        // Absolute desktop input can quantize by a physical pixel. The hook
        // generation still rejects even a one-pixel movement made by the user.
        private bool Owned(){Point actual=read();return active && System.Threading.Volatile.Read(ref userMovement)==ownedMovement && Math.Abs((long)actual.X-expected.X)<=2 && Math.Abs((long)actual.Y-expected.Y)<=2;}
        internal bool IsOwned{get{lock(sync){return Owned();}}}
        internal bool HasMoved{get{lock(sync){return moved;}}}
        internal bool Move(Point point)
        {
            lock(sync)
            {
                if(!Owned()){FinishLocked();return false;}
                // Set expected before injection; the hook ignores our tagged
                // events and never takes this lock while SendInput is running.
                expected=point;moved=true;
                bool ok=move(point);if(!ok){FinishLocked();return false;}return true;
            }
        }
        internal bool Click(Point point,MouseButtons button)
        {
            lock(sync)
            {
                if(!Owned()){FinishLocked();return false;}
                expected=point;moved=true;
                try{return click(point,button,origin);}
                finally{FinishLocked();}
            }
        }
        private void Expire()
        {
            lock(sync)
            {
                if(!active)return;
                if(!Owned() || Environment.TickCount-started>=duration)FinishLocked();
            }
        }
        private void FinishLocked()
        {
            if(!active)return;
            try{if(moved && Owned())move(origin);}
            finally{active=false;moved=false;watchdog.Change(System.Threading.Timeout.Infinite,System.Threading.Timeout.Infinite);}
        }
        internal void Finish(){lock(sync){FinishLocked();}}
        public void Dispose(){lock(sync){if(disposed)return;FinishLocked();disposed=true;watchdog.Dispose();}}
    }

    internal sealed class TrayMirrorForm : Form
    {
        private readonly PictureBox picture=new PictureBox();
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer revealTimer=new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer popupTimer=new System.Windows.Forms.Timer();
        private readonly TrayPopupTracker flyouts=new TrayPopupTracker();
        private Rectangle source=Rectangle.Empty;
        private Point returnCursor;
        private bool forwarding;
        private bool pendingClick;
        private float pendingX,pendingY;
        private MouseButtons pendingButton;
        private int revealAttempts;
        private Point revealPoint;
        private Rectangle stableSource;
        private int sourceStableSince;
        private readonly TrayPointerLease pointer=new TrayPointerLease(delegate{return Cursor.Position;},Native.MovePointer,Native.ClickAndRestore,2400);
        private int capturePausedUntil=Environment.TickCount;
        private bool openingOverflow;
        private int clickStarted;
        private IntPtr movedPopup;
        private Rectangle originalPopupBounds;
        private uint originalPopupDpi;
        private volatile bool managedOverflow;
        private Native.HookProc popupMouseProc,popupKeyboardProc;
        private IntPtr popupMouseHook,popupKeyboardHook;
        private IntPtr mirrorHandle;
        private System.Threading.Thread popupHookThread;
        private volatile uint popupHookThreadId;
        private volatile bool stoppingHooks;
        internal int LastPopupLatency=-1;
        internal int ForwardCount;
        internal Point LastClickPoint;
        internal IntPtr LastInputTarget;
        internal bool LastInputSucceeded;
        private readonly Label gear=new Label();
        private IntPtr taskbarHost;
        private int lastFrame;
        private Bitmap lastTrayFrame;
        private Rectangle lastTraySource;
        private WallpaperBackdrop wallpaper;
        private readonly TrayPixelBuffer pixels=new TrayPixelBuffer();
        private int lastClockSecond=-1;
        private bool started;
        internal TrayMirrorForm(Action settings)
        {
            Text="副屏真实托盘";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;TopMost=true;AutoScaleMode=AutoScaleMode.None;
            BackColor=Color.FromArgb(35,39,47);ClientSize=new Size(440,48);
            picture.Dock=DockStyle.Fill;picture.SizeMode=PictureBoxSizeMode.StretchImage;Controls.Add(picture);
            gear.Text="⚙";gear.Dock=DockStyle.Left;gear.Width=30;gear.Font=new Font("Segoe UI",14);gear.TextAlign=ContentAlignment.MiddleCenter;gear.ForeColor=Color.White;gear.Cursor=Cursors.Hand;Controls.Add(gear);
            var menu=new ContextMenuStrip();menu.Items.Add("打开副屏助手设置",null,delegate{settings();});menu.Items.Add("刷新托盘画面",null,delegate{CaptureTray();});gear.ContextMenuStrip=menu;gear.Click+=delegate{settings();};
            var tip=new ToolTip();tip.SetToolTip(gear,"副屏助手设置");tip.SetToolTip(picture,"真实系统托盘 · 支持左键、右键和双击\n箭头在副屏展开真实隐藏图标菜单，菜单图标可直接操作。\n主屏隐藏时保留图标画面，点击会先展开真实托盘。");
            picture.MouseUp+=Forward;
            timer.Interval=ResponsePolicy.TrayPoll;timer.Tick+=delegate
            {
                if(!forwarding){CaptureTray();return;}
                // Native auto-hide continues during a primary-tray reveal.
                // Visibility tracking must not pause with expensive capture.
                Screen target=Settings.TargetScreen();Rectangle bar;
                if((target==null || !ReadTaskbarExposure(taskbarHost,target.Bounds,out bar)) && Visible)Hide();
            };
            popupTimer.Interval=16;popupTimer.Tick+=delegate{TrackOverflow();};
            revealTimer.Interval=16;revealTimer.Tick+=delegate
            {
                if(!pointer.IsOwned){EndForward();return;}
                source=FindTrayBounds();
                if(SourceVisible(source))
                {
                    // The auto-hide rectangle reaches its final position before
                    // the XAML buttons finish animating. Wait for stable geometry
                    // and a real taskbar hit instead of clicking the desktop behind it.
                    if(stableSource!=source){stableSource=source;sourceStableSince=Environment.TickCount;}
                    Point hover=pendingClick?MapPoint(pendingX,pendingY,source):revealPoint;
                    if(pointer.HasMoved && hover!=revealPoint){if(!pointer.Move(hover)){EndForward();return;}revealPoint=hover;}
                    IntPtr taskbar=Native.FindWindow("Shell_TrayWnd",null);
                    bool hit=Native.GetAncestor(Native.WindowFromPoint(hover),2)==taskbar;
                    if(Environment.TickCount-sourceStableSince<120 || !hit)
                    {
                        if(++revealAttempts<100)return;
                        Text="真实托盘尚未就绪，点击重试";EndForward();return;
                    }
                    revealTimer.Stop();
                    if(pendingClick && pointer.IsOwned)
                    {
                        Point p=MapPoint(pendingX,pendingY,source);LastClickPoint=p;LastInputTarget=Native.WindowFromPoint(p);
                        // Keep capture/matting work outside the pointer lease.
                        // Native dispatch and return are one ordered input batch.
                        flyouts.Begin(p,returnCursor);LastInputSucceeded=pointer.Click(p,pendingButton);
                    }
                    else if(!pendingClick)CaptureTray();
                    EndForward();
                }
                else if(++revealAttempts>=75){Text="托盘未能展开，点击重试";EndForward();}
            };
            Shown+=delegate{Start();};
            FormClosing+=delegate{timer.Stop();EndForward();pointer.Dispose();popupTimer.Stop();RestoreOverflow();StopPopupHooks();flyouts.Dispose();if(wallpaper!=null)wallpaper.Dispose();pixels.Dispose();timer.Dispose();revealTimer.Dispose();popupTimer.Dispose();if(picture.Image!=null){picture.Image.Dispose();picture.Image=null;}if(lastTrayFrame!=null)lastTrayFrame.Dispose();tip.Dispose();};
        }
        internal void Start()
        {
            if(started || IsDisposed || Disposing)return;
            // CaptureTray may Place -> Show -> Shown. Mark initialization before
            // that path, and let Place alone decide whether the bar is exposed.
            started=true;IntPtr ownHandle=Handle;InstallPopupHooks();timer.Start();CaptureTray();
        }
        protected override bool ShowWithoutActivation{get{return true;}}
        protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x80|0x08000000|0x80000;return p;}}
        internal static Rectangle FindTrayBounds()
        {
            IntPtr taskbar=Native.FindWindow("Shell_TrayWnd",null);if(taskbar==IntPtr.Zero)return Rectangle.Empty;
            IntPtr notify=Native.FindWindowEx(taskbar,IntPtr.Zero,"TrayNotifyWnd",null);
            Rectangle r=Native.Bounds(notify);
            if(r.Width>20 && r.Height>12 && Native.IsWindowVisible(notify))return r;
            // Do not copy arbitrary taskbar pixels when the OS layout is unrecognized.
            return Rectangle.Empty;
        }
        private void CaptureTray()
        {
            Screen target=Settings.TargetScreen();if(target==null){Hide();return;}
            IntPtr host=taskbarHost;
            if(host==IntPtr.Zero || !Native.IsWindow(host) || Screen.FromHandle(host).DeviceName!=target.DeviceName)host=FindTargetTaskbar();
            if(host==IntPtr.Zero){Hide();return;}
            Rectangle bar;
            if(!ReadTaskbarExposure(host,target.Bounds,out bar)){if(Visible)Hide();return;}
            if(taskbarHost!=host)
            {
                taskbarHost=host;
            }
            source=FindTrayBounds();
            if(source.IsEmpty)
            {
                if(picture.Image!=null){picture.Image.Dispose();picture.Image=null;}
                picture.BackColor=BackColor;picture.Invalidate();
                Text="副屏托盘：主屏托盘隐藏或无法识别";
                using(var b=new Bitmap(330,42))using(var g=Graphics.FromImage(b)){g.Clear(BackColor);TextRenderer.DrawText(g,"主屏托盘隐藏 / 暂不可捕获",Font,new Rectangle(8,0,322,42),Color.White,TextFormatFlags.VerticalCenter);SetImage((Bitmap)b.Clone());}
                Place(target,bar,360,bar.Height);return;
            }
            int maxWidth=Math.Min(Settings.Current.TrayWidth,Math.Max(100,target.Bounds.Width-46));
            int h=bar.Height;
            double scale=Math.Min(h/(double)source.Height,maxWidth/(double)source.Width);int w=Math.Max(40,(int)(source.Width*scale));
            Place(target,bar,w+30,h);
            if(!Visible)return;
            if(!SourceVisible(source))
            {
                Text="主屏已隐藏；保留上次托盘画面，点击展开操作";
                if(picture.Image==null && !forwarding)BeginReveal(false);
                else if(Visible)RefreshHiddenClock();
                return;
            }
            if(Environment.TickCount-capturePausedUntil<0 || (Environment.TickCount-lastFrame<ResponsePolicy.TrayFrame && picture.Image!=null))return;
            lastFrame=Environment.TickCount;Text="副屏真实托盘";
            Bitmap image=null;
            try
            {
                image=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppPArgb);
                using(var g=Graphics.FromImage(image))g.CopyFromScreen(source.Location,Point.Empty,source.Size);
                if(lastTrayFrame!=null)lastTrayFrame.Dispose();lastTrayFrame=(Bitmap)image.Clone();
                lastTraySource=source;
                SetImage(image);image=null;
            }
            catch { Text="副屏托盘：屏幕暂不可捕获"; }
            finally{if(image!=null)image.Dispose();}
        }
        internal static IntPtr FindTargetTaskbar()
        {
            Screen screen=Settings.TargetScreen();if(screen==null)return IntPtr.Zero;
            IntPtr result=IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h,IntPtr p)
            {
                if(Native.ClassName(h)=="Shell_SecondaryTrayWnd" && Screen.FromHandle(h).DeviceName==screen.DeviceName){result=h;return false;}return true;
            },IntPtr.Zero);
            return result;
        }
        private void Place(Screen screen,Rectangle bar,int w,int h)
        {
            // Follow the native taskbar rectangle without becoming a child of its
            // composition surface, which would cover GDI content and hit testing.
            // Match the target taskbar's physical height, including per-screen DPI.
            // Capture and background separation can take time. Never show from
            // the earlier rectangle after the native taskbar has hidden.
            if(!ReadTaskbarExposure(taskbarHost,screen.Bounds,out bar)){if(Visible)Hide();return;}
            h=bar.Height;
            var desired=new Rectangle(bar.Right-w,bar.Top,w,h);
            bool resized=Bounds.Size!=desired.Size;
            if(!Visible){Show();Native.SetWindowPos(Handle,new IntPtr(-1),desired.X,desired.Y,w,h,0x10|0x40);}
            else if(Bounds!=desired)Native.SetWindowPos(Handle,new IntPtr(-1),desired.X,desired.Y,w,h,0x10|0x40);
            if(resized && picture.Image!=null)RenderSurface();
        }
        internal static bool TaskbarExposed(Rectangle screen,Rectangle bar,bool visible,bool cloaked)
        {
            Rectangle exposed=Rectangle.Intersect(screen,bar);
            // An auto-hidden bar may retain a two-pixel reveal strip on any
            // edge. Neither that strip nor an invisible/cloaked host is a bar.
            return visible && !cloaked && exposed.Width>3 && exposed.Height>3;
        }
        internal static bool ReadTaskbarExposure(IntPtr host,Rectangle screen,out Rectangle bar)
        {
            bar=Native.Bounds(host);
            if(host==IntPtr.Zero || !Native.IsWindow(host))return false;
            int cloak;bool cloaked=Native.DwmGetWindowAttribute(host,14,out cloak,4)==0 && cloak!=0;
            if(!TaskbarExposed(screen,bar,Native.IsWindowVisible(host),cloaked))return false;
            string cls=Native.ClassName(host);
            return (cls!="Shell_SecondaryTrayWnd" && cls!="Shell_TrayWnd") || TaskbarContentExposed(host,screen,bar);
        }
        internal static bool TaskbarContentExposed(IntPtr host,Rectangle screen,Rectangle bar)
        {
            bool found=false,exposed=false;
            Native.EnumChildWindows(host,delegate(IntPtr child,IntPtr unused)
            {
                if(Native.ClassName(child)!="Windows.UI.Composition.DesktopWindowContentBridge")return true;
                found=true;
                // A moved taskbar HWND is not proof that Shell has opened its
                // XAML content. Known hidden bridges veto the mirror, while
                // older Shell layouts without this bridge retain compatibility.
                int cloak;if(!Native.IsWindowVisible(child) || (Native.DwmGetWindowAttribute(child,14,out cloak,4)==0 && cloak!=0))return true;
                Rectangle visible=Rectangle.Intersect(Rectangle.Intersect(screen,bar),Native.Bounds(child));
                if(visible.Width>3 && visible.Height>3){exposed=true;return false;}
                return true;
            },IntPtr.Zero);
            return !found || exposed;
        }
        private void SetImage(Bitmap b)
        {
            if(Settings.Current.TransparentTray)
            {
                Bitmap reference=null;
                if(Settings.Current.WallpaperAwareTray && !lastTraySource.IsEmpty && lastTraySource.Size==b.Size)
                {
                    if(wallpaper==null)wallpaper=new WallpaperBackdrop();
                    reference=wallpaper.Get(lastTraySource,Screen.FromRectangle(lastTraySource).Bounds,SystemInformation.VirtualScreen);
                }
                try{Bitmap transparent=TraySurface.SeparateBackground(b,ResponsePolicy.High?pixels:null,reference);b.Dispose();b=transparent;}
                finally{if(reference!=null)reference.Dispose();}
            }
            Image old=picture.Image;picture.Image=b;if(old!=null)old.Dispose();RenderSurface();
        }
        private void RenderSurface()
        {
            if(!IsHandleCreated || Width<31 || Height<1 || picture.Image==null)return;
            using(var surface=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(surface))
            {
                // Alpha 1 retains click input in the spaces around tray glyphs.
                g.Clear(Settings.Current.TransparentTray?Color.FromArgb(1,0,0,0):BackColor);
                g.DrawImage(picture.Image,new Rectangle(30,0,Width-30,Height));
                if(Settings.Current.TransparentTray)
                {
                    // The secondary taskbar already draws its own clock. Expose
                    // that clock rather than stacking primary-monitor glyphs.
                    int sourceClock=Math.Min(picture.Image.Width/2,Math.Max(82,picture.Image.Height*7/4));
                    int clock=(int)Math.Ceiling(sourceClock*(Width-30)/(double)picture.Image.Width);
                    g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    using(var clear=new SolidBrush(Color.Transparent))g.FillRectangle(clear,Width-clock,0,clock,Height);
                    g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceOver;
                }
                using(var b=new SolidBrush(BackColor))g.FillRectangle(b,0,0,30,Height);
                using(var font=new Font("Segoe UI",14))using(var b=new SolidBrush(Color.White))using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})g.DrawString("⚙",font,b,new RectangleF(0,0,30,Height),format);
                TraySurface.Present(Handle,surface,Location);
            }
        }
        private void RefreshHiddenClock()
        {
            // Keep system time current while an auto-hidden primary taskbar has no
            // visible pixels; application icons retain the last real tray frame.
            DateTime now=DateTime.Now;if(lastTrayFrame==null || lastClockSecond==now.Second)return;lastClockSecond=now.Second;
            Bitmap frame=(Bitmap)lastTrayFrame.Clone();
            // Transparent mode exposes the secondary native clock. Painting a
            // synthetic primary clock would contaminate wallpaper-fit margins.
            if(Settings.Current.TransparentTray){SetImage(frame);return;}
            int clockWidth=Math.Min(frame.Width/2,Math.Max(82,frame.Height*7/4));
            Rectangle area=new Rectangle(frame.Width-clockWidth,0,clockWidth,frame.Height);
            Color background=frame.GetPixel(frame.Width-4,Math.Min(4,frame.Height-1));
            Color foreground=background.GetBrightness()<0.5f?Color.White:Color.Black;
            bool seconds=Convert.ToInt32(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","ShowSecondsInSystemClock",0))!=0;
            using(var g=Graphics.FromImage(frame))using(var b=new SolidBrush(background))using(var font=new Font("Segoe UI",9))
            {
                g.FillRectangle(b,area);int row=Math.Max(14,frame.Height/3);
                TextRenderer.DrawText(g,now.ToString(seconds?"HH:mm:ss":"HH:mm"),font,new Rectangle(area.X+2,frame.Height/2-row,area.Width-8,row),foreground,background,TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g,now.ToShortDateString(),font,new Rectangle(area.X+2,frame.Height/2,area.Width-8,row),foreground,background,TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
            }
            SetImage(frame);
        }
        private void Forward(object sender,MouseEventArgs e)
        {
            ForwardCount++;
            picture.Capture=false;
            if(source.IsEmpty || forwarding || (e.Button!=MouseButtons.Left && e.Button!=MouseButtons.Right))return;
            bool arrow=e.Button==MouseButtons.Left && e.X/(float)Math.Max(1,picture.Width)*source.Width<32;
            if(arrow)
            {
                if(managedOverflow){RestoreOverflow();return;}
                clickStarted=Environment.TickCount;LastPopupLatency=-1;openingOverflow=true;popupTimer.Interval=16;popupTimer.Start();
            }
            // Recheck the source; a layout change must never click a stale coordinate.
            Rectangle live=FindTrayBounds();
            pendingX=e.X/(float)Math.Max(1,picture.Width);pendingY=e.Y/(float)Math.Max(1,picture.Height);pendingButton=e.Button;
            source=live;
            // Dispatch after the original MouseUp has released all capture and
            // after the native primary taskbar is ready to receive the input.
            BeginReveal(true);
        }
        private static bool SourceVisible(Rectangle r)
        {
            IntPtr taskbar=Native.FindWindow("Shell_TrayWnd",null);
            return !r.IsEmpty && taskbar!=IntPtr.Zero && Screen.FromHandle(taskbar).Bounds.Contains(r);
        }
        private static Point MapPoint(float x,float y,Rectangle r){return new Point(r.X+Math.Max(0,Math.Min(r.Width-1,(int)(x*r.Width))),r.Y+Math.Max(0,Math.Min(r.Height-1,(int)(y*r.Height))));}
        internal static IntPtr FindOverflowWindow()
        {
            uint shell=Native.ProcessId(Native.FindWindow("Shell_TrayWnd",null));
            foreach(string cls in new[]{"TopLevelWindowForOverflowXamlIsland","NotifyIconOverflowWindow","Shell_NotifyIconOverflowWindow"})
            {
                IntPtr h=Native.FindWindow(cls,null);
                if(h!=IntPtr.Zero && Native.IsWindowVisible(h) && Native.ProcessId(h)==shell)
                {
                    Rectangle r=Native.Bounds(h);
                    // WinUI keeps its host visible after collapsing the menu to
                    // a thin strip; that host is not an open overflow panel.
                    if(r.Width>40 && r.Height>40)return h;
                }
            }
            return IntPtr.Zero;
        }
        private void TrackOverflow()
        {
            if(!openingOverflow && !managedOverflow)return;
            IntPtr h=managedOverflow && Native.IsWindow(movedPopup)?movedPopup:FindOverflowWindow();
            if(h==IntPtr.Zero)
            {
                if(!openingOverflow || Environment.TickCount-clickStarted>1800){openingOverflow=false;popupTimer.Stop();movedPopup=IntPtr.Zero;}
                return;
            }
            Screen screen=Settings.TargetScreen();if(screen==null)return;
            Rectangle r=Native.Bounds(h);if(r.Width<10 || r.Height<10)return;
            if(movedPopup!=h){movedPopup=h;originalPopupBounds=r;originalPopupDpi=Native.Dpi(h);managedOverflow=true;}
            double scale=Native.Dpi(taskbarHost)/(double)Math.Max(1,originalPopupDpi);
            int width=Math.Max(10,(int)Math.Round(originalPopupBounds.Width*scale));
            int height=Math.Max(10,(int)Math.Round(originalPopupBounds.Height*scale));
            Point anchor=picture.PointToScreen(new Point(Math.Min(picture.Width-1,(int)(16*picture.Width/(double)Math.Max(1,source.Width))),0));
            int x=Math.Max(screen.Bounds.Left,Math.Min(screen.Bounds.Right-width,anchor.X-width/2));
            int y=Math.Max(screen.Bounds.Top,Math.Min(screen.Bounds.Bottom-height,Top-height-6));
            var desired=new Rectangle(x,y,width,height);
            // Own the relocated popup's visibility. The original XAML anchor is
            // still on the primary monitor; native source icons handle all input.
            // Synchronous positioning leaves no queued show after an outside click.
            if(r!=desired || !Native.IsWindowVisible(h))Native.SetWindowPos(h,new IntPtr(-1),x,y,width,height,0x10|0x40);
            Rectangle actual=Native.Bounds(h);
            if(openingOverflow && screen.Bounds.Contains(actual))
            {
                LastPopupLatency=Environment.TickCount-clickStarted;openingOverflow=false;
                // Track DPI adjustment briefly; native popup handles all icon input.
                popupTimer.Interval=ResponsePolicy.PopupTracking;
            }
        }
        private void RestoreOverflow()
        {
            popupTimer.Stop();openingOverflow=false;managedOverflow=false;
            if(movedPopup!=IntPtr.Zero && Native.IsWindow(movedPopup))
            {
                Rectangle r=originalPopupBounds;
                Native.ShowWindow(movedPopup,0);
                Native.SetWindowPos(movedPopup,IntPtr.Zero,r.X,r.Y,r.Width,r.Height,0x10|4);
            }
            movedPopup=IntPtr.Zero;
        }
        private void InstallPopupHooks()
        {
            if(popupHookThread!=null)return;mirrorHandle=Handle;
            popupMouseProc=delegate(int code,IntPtr w,IntPtr l)
            {
                if(code>=0)
                {
                    var movement=(Native.MouseData)System.Runtime.InteropServices.Marshal.PtrToStructure(l,typeof(Native.MouseData));
                    // Clicking or scrolling while a reveal is pending also
                    // revokes it; our whole native batch carries the same tag.
                    pointer.ObserveMovement(movement.Extra);
                }
                if(code>=0 && managedOverflow && (w==new IntPtr(0x201)||w==new IntPtr(0x204)||w==new IntPtr(0x207)))
                {
                    var m=(Native.MouseData)System.Runtime.InteropServices.Marshal.PtrToStructure(l,typeof(Native.MouseData));
                    if(!Native.Bounds(movedPopup).Contains(m.Point) && !Native.Bounds(mirrorHandle).Contains(m.Point))
                    {
                        if(!IsContextMenuWindow(Native.WindowFromPoint(m.Point)))QueuePopupClose();
                    }
                    else if(Native.Bounds(movedPopup).Contains(m.Point))
                    {
                        Point clicked=m.Point;try{BeginInvoke(new Action(delegate{flyouts.Begin(clicked,clicked);}));}catch{}
                    }
                }
                return Native.CallNextHookEx(popupMouseHook,code,w,l);
            };
            popupKeyboardProc=delegate(int code,IntPtr w,IntPtr l)
            {
                if(code>=0 && managedOverflow && (w==new IntPtr(0x100)||w==new IntPtr(0x104)))
                {
                    var k=(Native.KeyboardData)System.Runtime.InteropServices.Marshal.PtrToStructure(l,typeof(Native.KeyboardData));
                    if(k.Key==27)
                    {
                        bool context=false;Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.IsWindowVisible(h) && IsContextMenuWindow(h)){context=true;return false;}return true;},IntPtr.Zero);
                        if(!context)QueuePopupClose();
                    }
                }
                return Native.CallNextHookEx(popupKeyboardHook,code,w,l);
            };
            // Low-level hooks require a responsive message pump. Screen capture
            // and thumbnail rendering must not make Windows time out this pump.
            popupHookThread=new System.Threading.Thread(delegate()
            {
                Native.Message message;Native.PeekMessage(out message,IntPtr.Zero,0,0,0);
                popupHookThreadId=Native.GetCurrentThreadId();
                try
                {
                    if(stoppingHooks)return;
                    popupMouseHook=Native.SetWindowsHookEx(14,popupMouseProc,Native.GetModuleHandle(null),0);
                    popupKeyboardHook=Native.SetWindowsHookEx(13,popupKeyboardProc,Native.GetModuleHandle(null),0);
                    while(Native.GetMessage(out message,IntPtr.Zero,0,0)>0){Native.TranslateMessage(ref message);Native.DispatchMessage(ref message);}
                }
                finally
                {
                    if(popupMouseHook!=IntPtr.Zero)Native.UnhookWindowsHookEx(popupMouseHook);
                    if(popupKeyboardHook!=IntPtr.Zero)Native.UnhookWindowsHookEx(popupKeyboardHook);
                    popupMouseHook=popupKeyboardHook=IntPtr.Zero;popupHookThreadId=0;
                }
            });
            popupHookThread.IsBackground=true;popupHookThread.Name="ScreenCompanion tray input";popupHookThread.Start();
        }
        private void StopPopupHooks(){stoppingHooks=true;uint id=popupHookThreadId;if(id!=0)Native.PostThreadMessage(id,0x12,IntPtr.Zero,IntPtr.Zero);}
        private void QueuePopupClose(){try{BeginInvoke(new Action(RestoreOverflow));}catch{}}
        private bool IsContextMenuWindow(IntPtr h)
        {
            if(h==mirrorHandle || h==movedPopup)return false;
            string cls=Native.ClassName(h);
            return cls=="#32768" || cls=="Microsoft.UI.Content.PopupWindowSiteBridge" ||
                (cls.StartsWith("WindowsForms10.",StringComparison.Ordinal) &&
                 (Native.GetWindowLong(h,-20)&0x88)!=0 && (Native.GetWindowLong(h,-16)&unchecked((int)0x80000000))!=0);
        }
        private void BeginReveal(bool click)
        {
            if(forwarding)return;
            IntPtr taskbar=Native.FindWindow("Shell_TrayWnd",null);if(taskbar==IntPtr.Zero)return;
            Rectangle b=Screen.FromHandle(taskbar).Bounds,tr=Native.Bounds(taskbar);
            returnCursor=Cursor.Position;pendingClick=click;revealAttempts=0;forwarding=true;stableSource=Rectangle.Empty;
            pointer.Begin(returnCursor);
            int x=Math.Max(b.Left,Math.Min(b.Right-2,tr.Right-70));int y=Math.Max(b.Top,Math.Min(b.Bottom-1,tr.Top+2));
            if(tr.Height>tr.Width){x=Math.Max(b.Left,Math.Min(b.Right-1,tr.Left+2));y=Math.Max(b.Top,Math.Min(b.Bottom-2,tr.Bottom-70));}
            Rectangle live=FindTrayBounds();
            revealPoint=new Point(x,y);
            // Visible tray needs no hover warp: validate its hit and geometry
            // while the pointer remains exactly where the user clicked.
            if(!SourceVisible(live) && !pointer.Move(revealPoint)){EndForward();return;}
            Text="正在展开真实托盘…";revealTimer.Start();
        }
        private void EndForward(){revealTimer.Stop();pendingClick=false;pointer.Finish();forwarding=false;capturePausedUntil=Environment.TickCount+450;}
    }
}
