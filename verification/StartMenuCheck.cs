using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenCompanion
{
    // Exercise our own native windows. This never opens/injects real Start.
    internal static class StartMenuCheck
    {
        private static int checks;
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h,uint attribute,ref int value,int size);
        [STAThread] static int Main()
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                CheckClickPairs();
                CheckTaskbarGeometry();
                Check(StartMenuMirrorForm.Fit(Rectangle.Empty,new Rectangle(0,0,100,100)).IsEmpty,"empty source is rejected");
                Check(StartMenuMirrorForm.Fit(new Rectangle(0,0,100,100),Rectangle.Empty).IsEmpty,"empty target is rejected");
                Check(StartMenuMirrorForm.Map(Point.Empty,new Size(100,50),new Rectangle(-1920,30,1920,1080))==new Point(-1920,30),"negative-coordinate source maps first pixel");
                Check(StartMenuMirrorForm.Map(new Point(100,50),new Size(100,50),new Rectangle(-1920,30,1920,1080))==new Point(-1,1109),"last pixel is clamped inside native source");
                Check(StartMenuMirrorForm.Map(new Point(50,25),new Size(100,50),new Rectangle(-1920,30,1920,1080))==new Point(-960,570),"scaled center maps accurately");
                int secondary=0;
                foreach(Screen target in Screen.AllScreens)
                {
                    if(target.Primary)continue;secondary++;
                    CheckNamedStartLookup(target);
                    Rectangle canvas=new Rectangle(target.Bounds.Left,target.Bounds.Top,target.Bounds.Width,Math.Max(100,target.Bounds.Height-40));
                    Rectangle primary=StartMenuPlacement.PrimaryBounds(canvas);
                    Check(primary.Left==Screen.PrimaryScreen.Bounds.Left && primary.Top==Screen.PrimaryScreen.Bounds.Top && primary.Width==Screen.PrimaryScreen.Bounds.Width,"legacy canvas returns to primary on "+target.DeviceName);
                    Check(primary.Height==Screen.PrimaryScreen.Bounds.Height-40,"legacy canvas preserves taskbar margin");
                    Rectangle fit=StartMenuMirrorForm.Fit(primary,target.WorkingArea);
                    Check(target.WorkingArea.Contains(fit),"mirror stays within secondary work area");
                    Check(fit.Bottom==target.WorkingArea.Bottom,"mirror anchors above native taskbar");
                    IntPtr taskbar=Taskbar(target);Rectangle taskbarBefore=Native.Bounds(taskbar);
                    using(var source=new Form{Text="Start placement fixture",FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,AutoScaleMode=AutoScaleMode.None,TopMost=true,Bounds=new Rectangle(target.Bounds.Left+40,target.Bounds.Top+40,420,280)})
                    {
                        IntPtr h=source.Handle;Check(!Native.IsWindowVisible(h),"fixture begins hidden");
                        var placement=new StartMenuPlacement(h);placement.EnsurePrimary();
                        Check(!Native.IsWindowVisible(h),"hidden legacy repair never opens the source");
                        Check(Native.Bounds(h)==placement.Original,"hidden source immediately reaches recorded primary baseline");
                        Rectangle displaced=new Rectangle(target.Bounds.Left+60,target.Bounds.Top+70,420,280);
                        Native.SetWindowPos(h,IntPtr.Zero,displaced.X,displaced.Y,displaced.Width,displaced.Height,4|16);
                        placement.Restore();Check(Native.Bounds(h)==placement.Original,"retained hidden restoration restores recorded baseline");
                        placement.Restore();Check(Native.Bounds(h)==placement.Original,"restoration is idempotent");
                        source.Show();source.Activate();Pump(100);Rectangle original=Native.Bounds(h);
                        Check(!StartMenuController.IsStartMenu(h),"ordinary fixture window cannot be mistaken for native Start");
                        using(var mirror=new StartMenuMirrorForm())
                        {
                            IntPtr focus=Native.GetForegroundWindow();
                            Check(PresentReady(mirror,h,target.WorkingArea),"async hook readiness leads to a live DWM presentation");Pump(100);
                            Check(mirror.HasThumbnail && mirror.Source==h,"thumbnail remains bound to exact source HWND");
                            Console.WriteLine("FOCUS: before="+focus+" after="+Native.GetForegroundWindow()+" source="+h+" mirror="+mirror.Handle);
                            Check(Native.GetForegroundWindow()!=mirror.Handle,"DWM mirror never takes foreground focus");
                            Check(Native.GetForegroundWindow()==focus,"native foreground focus is preserved");
                            Check(Native.Bounds(h)==original,"DWM presentation leaves primary source geometry intact");
                            Check(target.WorkingArea.Contains(mirror.Bounds),"visible mirror fits mixed-resolution secondary");
                            Check((Native.GetWindowLong(mirror.Handle,-20)&0x08000000)!=0,"mirror has native no-activate style");
                            var stable=mirror.ClickSnapshot;
                            Check(stable!=null && StartMenuPointerListener.Valid(stable),"shown source and mirror publish a valid immutable input snapshot");
                            Check(mirror.PointerListener.Ready && mirror.PointerListener.Responsive(500),"real dedicated hook message pump responds without global input injection");
                            Check(mirror.Present(h,target.WorkingArea) && Object.ReferenceEquals(stable,mirror.ClickSnapshot),"unchanged tracking tick retains snapshot identity across a click pair");
                            Point center=new Point(mirror.Bounds.Left+mirror.Width/2,mirror.Bounds.Top+mirror.Height/2);
                            Check(StartMenuPointerListener.CanBegin(stable,center),"down guard validates exact own mirror and mapped native source hit");
                            Check(!StartMenuPointerListener.CanBegin(stable,new Point(mirror.Bounds.Right+4,mirror.Bounds.Bottom+4)),"outside input cannot start an intercepted pair");
                            using(var blocker=new Form{FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,AutoScaleMode=AutoScaleMode.None,Bounds=new Rectangle(original.Left,original.Top,original.Width,original.Height),TopMost=true})
                            {
                                blocker.Show();Pump(30);
                                Check(!StartMenuPointerListener.CanBegin(stable,center),"overlapping unrelated source window prevents interception");
                                blocker.Hide();
                            }
                            var hookBeforeHide=mirror.PointerListener;
                            source.Hide();Check(!mirror.Present(h,target.WorkingArea),"hidden source is rejected rather than forced open");
                            Check(!mirror.Visible && !mirror.HasThumbnail,"hidden source clears presentation and releases thumbnail");
                            Check(mirror.ClickSnapshot==null && !StartMenuPointerListener.Valid(stable),"hiding source immediately invalidates queued input snapshot");
                            Check(WaitStopped(hookBeforeHide,800),"cleared idle hook unregisters and completes on its dedicated pump");
                            source.Show();Pump(80);Check(PresentReady(mirror,h,target.WorkingArea),"thumbnail and listener reopen for reused source HWND");
                            Check(!Object.ReferenceEquals(stable,mirror.ClickSnapshot),"reopened menu uses a fresh epoch and rejects prior queued requests");
                            var finalHook=mirror.PointerListener;
                            mirror.Clear();Check(!mirror.HasThumbnail && mirror.Source==IntPtr.Zero,"explicit clear releases source identity and thumbnail");
                            Check(WaitStopped(finalHook,800),"explicit Clear reliably unregisters its listener without UI thread join");
                        }
                        source.Show();Pump(50);int cloak=1;
                        Check(DwmSetWindowAttribute(h,13,ref cloak,4)==0,"own source accepts DWM cloaking");
                        int cloaked;Check(Native.DwmGetWindowAttribute(h,14,out cloaked,4)==0 && cloaked!=0,"fixture source is really cloaked");
                        Native.SetWindowPos(h,IntPtr.Zero,displaced.X,displaced.Y,displaced.Width,displaced.Height,4|16);
                        placement.Restore();Check(Native.Bounds(h)==placement.Original,"cloaked source restores without another manual action");
                        Check(Native.DwmGetWindowAttribute(h,14,out cloaked,4)==0 && cloaked!=0,"restoration does not uncloak source");
                        using(var mirror=new StartMenuMirrorForm())Check(!mirror.Present(h,target.WorkingArea),"cloaked source is never presented");
                        cloak=0;DwmSetWindowAttribute(h,13,ref cloak,4);
                        source.Hide();Native.SetWindowPos(h,IntPtr.Zero,displaced.X,displaced.Y,displaced.Width,displaced.Height,4|16);
                        placement.Restore();Check(!source.Visible && Native.Bounds(h)==placement.Original,"shutdown restoration works after source dismissal");
                        source.Dispose();Check(!placement.Valid,"destroyed HWND cannot be restored");placement.Restore();
                    }
                    Check(Native.Bounds(taskbar)==taskbarBefore,"native taskbar geometry is untouched");
                }
                Check(secondary>0,"secondary monitor available");
                Console.WriteLine("PASS: "+checks+" Start placement and DWM lifecycle checks; no real Start/menu input injected");return 0;
            }
            catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
        }
        private static void Pump(int milliseconds)
        {int start=Environment.TickCount;while(Environment.TickCount-start<milliseconds){Application.DoEvents();System.Threading.Thread.Sleep(8);}}
        private static void Check(bool condition,string message)
        {if(!condition)throw new Exception(message);checks++;Console.WriteLine("OK: "+message);}
        private static bool PresentReady(StartMenuMirrorForm mirror,IntPtr source,Rectangle target)
        {
            int start=Environment.TickCount;
            do{if(mirror.Present(source,target))return true;Pump(10);}while(unchecked(Environment.TickCount-start)<1500);
            return false;
        }
        private static bool WaitStopped(StartMenuPointerListener listener,int milliseconds)
        {
            int start=Environment.TickCount;
            while(!listener.Completed && unchecked(Environment.TickCount-start)<milliseconds)Pump(5);
            return listener.Completed && !listener.Ready;
        }
        private static void CheckClickPairs()
        {
            var first=new StartMenuClickSnapshot(new IntPtr(111),new Rectangle(-1000,0,400,300),new IntPtr(222),new Rectangle(0,0,800,600),1);
            var newer=new StartMenuClickSnapshot(new IntPtr(111),first.MirrorBounds,new IntPtr(222),first.SourceBounds,2);
            Point point=new Point(-800,150);var router=new StartMenuClickRouter(new Size(8,8));StartMenuClickRequest request;
            bool sourceOpen=true;
            bool suppressed=router.Route(0x201,point,first,true,true,out request);
            if(!suppressed)sourceOpen=false; // A native light-dismiss observer sees only delivered down.
            Check(suppressed && sourceOpen && router.HasPending && request==null,"consumed mirror down cannot trigger mocked native light-dismiss");
            Check(!router.RouteInput(0x202,point,Native.PointerInputTag,first,true,true,out request) && router.HasPending && request==null,"tagged forwarded up bypasses hook without consuming physical pair");
            Check(!router.Route(0x205,point,first,true,true,out request) && router.HasPending,"unmatched right release passes while left pair remains pending");
            Check(router.Route(0x202,point,first,false,true,out request) && request!=null && request.Button==MouseButtons.Left && request.SourcePoint==new Point(400,300),"physical left release produces exactly one correctly mapped request");
            Check(!router.HasPending && !router.Route(0x202,point,first,false,true,out request) && request==null,"duplicate release passes and cannot replay a second click");
            Check(router.Route(0x204,point,first,true,true,out request) && router.Route(0x205,point,first,false,true,out request) && request!=null && request.Button==MouseButtons.Right,"right input uses its own complete click pair");
            Check(!router.Route(0x201,point,first,false,true,out request) && request==null,"wrong mirror or mapped source hit passes physical down");
            Check(!router.Route(0x201,point,first,true,false,out request) && request==null,"hidden/cloaked or stale geometry cannot suppress a new down");
            Check(!router.Route(0x201,point,null,true,true,out request),"cleared snapshot cannot suppress a new down");
            Check(!router.Route(0x20A,point,first,true,true,out request) && !router.Route(0x207,point,first,true,true,out request),"wheel and middle-button input always pass");
            Check(!router.Route(0x100,point,first,true,true,out request),"keyboard messages are never routed or suppressed");
            router.Route(0x201,point,first,true,true,out request);
            Check(router.Route(0x202,point,newer,false,true,out request) && request==null && !router.HasPending,"epoch change consumes paired release but cancels replay");
            router.Route(0x201,point,first,true,true,out request);
            Check(router.Route(0x202,point,first,false,false,out request) && request==null,"source hide or changed bounds consumes release without replay");
            router.Route(0x201,point,first,true,true,out request);
            Check(router.Route(0x202,point,null,false,false,out request) && request==null,"Clear/disposal drains only the already-consumed pair");
            router.Route(0x201,point,first,true,true,out request);
            Check(!router.Route(0x200,new Point(point.X+20,point.Y),first,false,true,out request),"drag movement passes to other observers");
            Check(router.Route(0x202,point,first,false,true,out request) && request==null,"drag away and back cannot become a synthetic click");
            router.Route(0x201,point,first,true,true,out request);
            Check(router.Route(0x202,new Point(-2000,-2000),first,false,true,out request) && request==null,"release outside mirror cancels replay");
            router.Route(0x201,point,first,true,true,out request);
            Check(!router.Route(0x200,point,newer,false,true,out request),"snapshot replacement invalidates pending pair even during movement");
            Check(router.Route(0x202,point,first,false,true,out request) && request==null,"returning to obsolete snapshot never revalidates canceled pair");
            router.Clear();Check(!router.HasPending,"router cleanup releases all pair state");
            using(var listener=new StartMenuPointerListener(delegate{}))
            {listener.Dispose();Check(WaitStopped(listener,800),"disposing before asynchronous listener startup cannot leak a hook");}
        }
        private static IntPtr Taskbar(Screen screen)
        {
            IntPtr found=IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.ClassName(h)=="Shell_SecondaryTrayWnd" && Screen.FromHandle(h).DeviceName==screen.DeviceName){found=h;return false;}return true;},IntPtr.Zero);
            return found;
        }
        private static void CheckTaskbarGeometry()
        {
            foreach(Rectangle screen in new[]{new Rectangle(0,0,1920,1080),new Rectangle(-1600,-900,1600,900),new Rectangle(1920,48,1920,1080)})
            {
                Rectangle[] hidden={new Rectangle(screen.Left,screen.Bottom-2,screen.Width,48),new Rectangle(screen.Left,screen.Top-46,screen.Width,48),new Rectangle(screen.Left-46,screen.Top,48,screen.Height),new Rectangle(screen.Right-2,screen.Top,48,screen.Height)};
                Rectangle[] shown={new Rectangle(screen.Left,screen.Bottom-48,screen.Width,48),new Rectangle(screen.Left,screen.Top,screen.Width,48),new Rectangle(screen.Left,screen.Top,48,screen.Height),new Rectangle(screen.Right-48,screen.Top,48,screen.Height)};
                Rectangle[] hiddenAreas={new Rectangle(screen.Left,screen.Top,screen.Width,screen.Height-2),new Rectangle(screen.Left,screen.Top+2,screen.Width,screen.Height-2),new Rectangle(screen.Left+2,screen.Top,screen.Width-2,screen.Height),new Rectangle(screen.Left,screen.Top,screen.Width-2,screen.Height)};
                Rectangle[] shownAreas={new Rectangle(screen.Left,screen.Top,screen.Width,screen.Height-48),new Rectangle(screen.Left,screen.Top+48,screen.Width,screen.Height-48),new Rectangle(screen.Left+48,screen.Top,screen.Width-48,screen.Height),new Rectangle(screen.Left,screen.Top,screen.Width-48,screen.Height)};
                for(int edge=0;edge<hidden.Length;edge++)
                {
                    Rectangle available=StartTaskbarGeometry.AvailableArea(screen,screen,shown[edge]);
                    Check(available==shownAreas[edge],"mirror excludes actual visible bar at edge "+edge+" of "+screen);
                    Check(StartTaskbarGeometry.AvailableArea(available,screen,shown[edge])==available,"existing WorkArea exclusion is not counted twice");
                    Rectangle fallback=StartTaskbarGeometry.AvailableArea(screen,screen,hidden[edge]);
                    Check(fallback==hiddenAreas[edge],"native two-pixel hover trigger stays uncovered without moving taskbar");
                    Check(StartTaskbarGeometry.AvailableArea(fallback,screen,hidden[edge])==fallback,"two-pixel strip is excluded only once");
                }
                Check(StartTaskbarGeometry.AvailableArea(screen,screen,Rectangle.Empty)==screen,"missing taskbar does not reserve a false taskbar gap");
                Check(StartTaskbarGeometry.AvailableArea(screen,screen,new Rectangle(screen.Left+100,screen.Top+100,300,48))==screen,"unrelated interior window does not alter available area");
                Check(StartTaskbarGeometry.AvailableArea(screen,screen,new Rectangle(screen.Left,screen.Bottom+2,screen.Width,48))==screen,"fully offscreen taskbar does not create a false visible strip");
            }
        }
        private static void CheckNamedStartLookup(Screen target)
        {
            Rectangle secondary=new Rectangle(target.Bounds.Left+40,target.Bounds.Top+40,420,280);
            using(var fixture=new Form{Text="Named Start lookup fixture",FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,AutoScaleMode=AutoScaleMode.None,Bounds=secondary})
            {
                IntPtr h=fixture.Handle;var placement=new StartMenuPlacement(h);bool captured=false;
                StartMenuController.CaptureNamedStartWindows(delegate(string title){return title=="开始"?h:IntPtr.Zero;},delegate(IntPtr candidate){return candidate==h;},delegate(IntPtr candidate){placement.EnsurePrimary();captured=true;});
                Check(captured && !fixture.Visible && Native.Bounds(h)==placement.Original,"named hidden host lookup repairs a canvas omitted by window enumeration");
                captured=false;
                StartMenuController.CaptureNamedStartWindows(delegate(string title){return h;},delegate(IntPtr candidate){return false;},delegate(IntPtr candidate){captured=true;});
                Check(!captured,"named lookup still rejects an unrelated host identity");
                StartMenuController.CaptureNamedStartWindows(delegate(string title){return IntPtr.Zero;},delegate(IntPtr candidate){throw new Exception("zero handle must not be inspected");},delegate(IntPtr candidate){captured=true;});
                Check(!captured,"missing named window is safely ignored");
                Native.SetWindowPos(h,IntPtr.Zero,secondary.X,secondary.Y,secondary.Width,secondary.Height,4|16);
                int cloak=1;Check(DwmSetWindowAttribute(h,13,ref cloak,4)==0,"named lookup fixture accepts cloaking");
                StartMenuController.CaptureNamedStartWindows(delegate(string title){return title=="Start"?h:IntPtr.Zero;},delegate(IntPtr candidate){return candidate==h;},delegate(IntPtr candidate){placement.EnsurePrimary();captured=true;});
                int cloaked;Check(captured && !fixture.Visible && Native.Bounds(h)==placement.Original && Native.DwmGetWindowAttribute(h,14,out cloaked,4)==0 && cloaked!=0,"English named probe repairs a hidden cloaked canvas without showing it");
            }
        }
    }
}
