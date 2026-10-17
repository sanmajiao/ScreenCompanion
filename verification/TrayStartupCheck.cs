using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace ScreenCompanion
{
    // Synthetic capture/pointer backends and HWNDs owned by this process only.
    // No screen capture, Explorer input, pointer movement or global key injection.
    internal static class TrayStartupCheck
    {
        private static int checks;
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        [STAThread] static int Main()
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                BridgeReadiness();
                ReadinessPredicates();
                CaptureLifecycle();
                PresentationRollback();
                ManualRevealLifecycle();
                Console.WriteLine("PASS: "+checks+" tray startup checks; own HWNDs and synthetic frames only.");return 0;
            }
            catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
        }
        private sealed class OwnBar : Form
        {
            protected override bool ShowWithoutActivation {get{return true;}}
            protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x80|0x08000000;return p;}}
        }
        private sealed class OwnBridge : NativeWindow,IDisposable
        {
            private const string Class="Windows.UI.Composition.DesktopWindowContentBridge";
            private delegate IntPtr Procedure(IntPtr h,uint message,IntPtr w,IntPtr l);
            [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct WindowClass
            {internal uint Style;internal Procedure Proc;internal int ExtraClass,ExtraWindow;internal IntPtr Instance,Icon,Cursor,Background;internal string Menu,Name;}
            [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern ushort RegisterClass(ref WindowClass value);
            [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateWindowEx(int ex,string cls,string title,int style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
            [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h,uint message,IntPtr w,IntPtr l);
            private static readonly Procedure procedure=DefWindowProc;
            private static bool registered;
            internal OwnBridge(IntPtr parent,Rectangle bounds,bool visible)
            {
                if(!registered)
                {
                    var value=new WindowClass{Proc=procedure,Instance=Native.GetModuleHandle(null),Name=Class};
                    if(RegisterClass(ref value)==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());registered=true;
                }
                IntPtr h=CreateWindowEx(0,Class,"Own synthetic XAML bridge",0x40000000|(visible?0x10000000:0),bounds.X,bounds.Y,bounds.Width,bounds.Height,parent,IntPtr.Zero,Native.GetModuleHandle(null),IntPtr.Zero);
                if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());AssignHandle(h);
            }
            public void Dispose(){DestroyHandle();}
        }
        private static void BridgeReadiness()
        {
            Screen screen=Screen.PrimaryScreen;
            using(var host=new OwnBar{Text="Own primary tray readiness",ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,Bounds=new Rectangle(screen.Bounds.Left+30,screen.Bounds.Top+30,520,70)})
            {
                host.Show();Pump(25);Rectangle bounds=Native.Bounds(host.Handle);
                Check(screen.Bounds.Contains(bounds),"primary host fixture has fully onscreen nonempty geometry");
                using(var bridge=new OwnBridge(host.Handle,new Rectangle(0,0,520,70),false))
                {
                    Check(Native.IsWindowVisible(host.Handle) && !Native.IsWindowVisible(bridge.Handle),"primary host can be visible while real-class XAML content is hidden");
                    Check(!TrayMirrorForm.TaskbarContentExposed(host.Handle,screen.Bounds,bounds),"hidden primary bridge rejects host geometry as proof of usable source pixels");
                    Native.ShowWindow(bridge.Handle,8);
                    Check(TrayMirrorForm.TaskbarContentExposed(host.Handle,screen.Bounds,bounds),"own content-ready transition is observed without changing host geometry");
                    Native.SetWindowPos(bridge.Handle,IntPtr.Zero,0,0,520,2,4|16);
                    Check(!TrayMirrorForm.TaskbarContentExposed(host.Handle,screen.Bounds,bounds),"two-pixel XAML strip does not qualify as a usable tray frame");
                    Native.SetWindowPos(bridge.Handle,IntPtr.Zero,0,0,520,70,4|16);Native.ShowWindow(bridge.Handle,0);
                    Check(!TrayMirrorForm.TaskbarContentExposed(host.Handle,screen.Bounds,bounds),"same valid host geometry is rejected again after content collapses");
                }
            }
        }
        private sealed class CaptureFixture : IDisposable
        {
            internal bool PrimaryReady,TargetExposed=true,FailPresent;
            internal int Captures,Moves,Clicks,SettingsOpened,Presents;
            internal uint Dpi=96;
            internal Point Pointer=Cursor.Position;
            internal Rectangle Source;
            internal Bitmap Next;
            internal readonly OwnBar Host;
            internal readonly TrayMirrorForm Mirror;
            internal CaptureFixture(Screen target):this(target,false){}
            internal CaptureFixture(Screen target,bool interceptPresentation)
            {
                Host=new OwnBar{Text="Own secondary tray lifecycle",ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,Bounds=new Rectangle(target.Bounds.Left,target.Bounds.Bottom-48,target.Bounds.Width,48)};
                IntPtr h=Host.Handle;Host.Show();
                Source=new Rectangle(Screen.PrimaryScreen.Bounds.Left+100,Screen.PrimaryScreen.Bounds.Top+100,520,48);Next=Icons(false);
                var backend=new TrayCaptureBackend
                {
                    TargetScreen=delegate{return target;},TargetTaskbar=delegate{return h;},
                    ReadTargetExposure=delegate(IntPtr host,Rectangle screen,out Rectangle bar){bar=Native.Bounds(Host.Handle);return TargetExposed;},
                    FindPrimaryTray=delegate{return Source;},PrimaryContentReady=delegate(Rectangle r){return PrimaryReady;},
                    Capture=delegate(Rectangle r){Captures++;return (Bitmap)Next.Clone();},PrimaryDpi=delegate{return Dpi;},EnableInputHooks=false
                };
                if(interceptPresentation)backend.PresentSurface=delegate(IntPtr window,Bitmap image,Point location)
                {Presents++;if(FailPresent)throw new InvalidOperationException("synthetic layered presentation denial");};
                var pointer=new TrayPointerLease(delegate{return Pointer;},delegate(Point p){Moves++;Pointer=p;return true;},delegate(Point p,MouseButtons button,Point origin){Clicks++;Pointer=origin;return true;},1700);
                Mirror=new TrayMirrorForm(delegate{SettingsOpened++;},backend,pointer);
            }
            internal Image Image {get{return ((PictureBox)Field(Mirror,"picture")).Image;}}
            internal Bitmap Cached {get{return (Bitmap)Field(Mirror,"lastTrayFrame");}}
            public void Dispose(){Mirror.Close();Mirror.Dispose();Host.Dispose();if(Next!=null)Next.Dispose();}
        }
        private static void ReadinessPredicates()
        {
            Rectangle screen=new Rectangle(-1600,-900,1600,900),bar=new Rectangle(-1600,-48,1600,48),source=new Rectangle(-800,-48,520,48);
            Check(TrayMirrorForm.SourceGeometryReady(source,screen,bar,true,true,true,true),"onscreen matched unoccluded source on a rendered primary bar is ready");
            Check(!TrayMirrorForm.SourceGeometryReady(source,screen,bar,false,true,true,true),"XAML-hidden primary vetoes source even though all rectangles remain onscreen");
            Check(!TrayMirrorForm.SourceGeometryReady(source,screen,bar,true,false,true,true),"hidden notify host vetoes a retained source rectangle");
            Check(!TrayMirrorForm.SourceGeometryReady(source,screen,bar,true,true,false,true),"source bounds from another primary layout are rejected");
            Check(!TrayMirrorForm.SourceGeometryReady(source,screen,bar,true,true,true,false),"unrelated app occluding source prevents accepting its desktop pixels");
            foreach(bool light in new[]{false,true})using(var raw=Icons(light))using(var separated=TraySurface.SeparateBackground(raw))
                Check(TrayMirrorForm.HasVisibleTrayContent(separated,96),"light/dark genuine glyphs qualify without a brightness threshold: "+light);
            foreach(Color color in new[]{Color.Black,Color.White,Color.Transparent})using(var raw=Flat(color))using(var separated=TraySurface.SeparateBackground(raw))
                Check(!TrayMirrorForm.HasVisibleTrayContent(separated,96),"uniform no-icon capture cannot become a trusted broad tray frame: "+color);
            using(var clock=new Bitmap(520,48,PixelFormat.Format32bppPArgb))
            {
                using(var g=Graphics.FromImage(clock)){g.Clear(Color.Transparent);g.FillRectangle(Brushes.White,470,18,12,12);}
                Check(!TrayMirrorForm.HasVisibleTrayContent(clock,96),"clock pixels alone cannot prove icons exist in a broad tray source");
            }
            using(var narrow=new Bitmap(80,48,PixelFormat.Format32bppPArgb))
            {
                using(var g=Graphics.FromImage(narrow)){g.Clear(Color.Transparent);g.FillRectangle(Brushes.White,20,18,8,12);}
                Check(TrayMirrorForm.HasVisibleTrayContent(narrow,96),"legitimate narrow source keeps a sparse glyph in its finally visible left half");
                using(var g=Graphics.FromImage(narrow)){g.Clear(Color.Transparent);g.FillRectangle(Brushes.White,60,18,8,12);}
                Check(!TrayMirrorForm.HasVisibleTrayContent(narrow,96),"glyphs entirely inside the final clock mask cannot qualify a narrow source as visible icon content");
            }
        }
        private static void CaptureLifecycle()
        {
            Screen target=null;foreach(Screen screen in Screen.AllScreens)if(!screen.Primary){target=screen;break;}
            Check(target!=null,"secondary screen exists for own host fixture");Settings previous=Settings.Current;
            try
            {
                Settings.Current=new Settings{ScreenName=target.DeviceName,Desktop=false,AltTab=false,Tray=true,TransparentTray=true,WallpaperAwareTray=false,HighResponse=true};
                using(var fixture=new CaptureFixture(target))
                {
                    fixture.TargetExposed=false;fixture.Mirror.Start();
                    Check(!fixture.Mirror.Visible && !fixture.Mirror.FrameReady,"hidden target startup creates neither a floating mirror nor a claimed good frame");
                    Check(fixture.Captures==0 && fixture.Moves==0,"geometrically valid but content-hidden primary source causes no capture or automatic warp");
                    fixture.TargetExposed=true;fixture.Mirror.CaptureNow();
                    Check(fixture.Mirror.Visible && !fixture.Mirror.FrameReady,"exposed target has an operable first-frame placeholder while primary content is not ready");
                    var gear=(Label)Field(fixture.Mirror,"gear");
                    Check(gear.Visible && gear.Enabled,"settings gear exists before any trusted primary frame");
                    typeof(Control).GetMethod("OnClick",Private).Invoke(gear,new object[]{EventArgs.Empty});
                    Check(fixture.SettingsOpened==1,"first-frame settings entry reaches its callback without native pointer input");
                    for(int i=0;i<8;i++)fixture.Mirror.CaptureNow();
                    Check(fixture.Moves==0 && fixture.Clicks==0,"repeated startup polls never repeatedly reveal primary tray with pointer warps");
                    fixture.TargetExposed=false;fixture.Mirror.CaptureNow();
                    Check(!fixture.Mirror.Visible,"target hide also hides placeholder/settings strip immediately");

                    fixture.PrimaryReady=true;fixture.Mirror.CaptureNow();
                    Check(fixture.Mirror.FrameReady && !fixture.Mirror.Visible && fixture.Captures>0,"hidden target can warm a trusted primary frame without showing overlay");
                    long raw=Fingerprint(fixture.Cached);long image=Fingerprint(fixture.Image);
                    Check(IconPixels(fixture.Image)>0,"accepted transparent frame contains icon pixels outside chevron and clock regions");
                    Check(fixture.Mirror.FrameMatchesSource(fixture.Source),"trusted frame matches its original source scale");
                    Check(fixture.Mirror.FrameMatchesSource(new Rectangle(fixture.Source.Left+40,fixture.Source.Top+100,fixture.Source.Width,fixture.Source.Height)),"auto-hide coordinate translation retains valid cached scale");
                    Check(!fixture.Mirror.FrameMatchesSource(new Rectangle(fixture.Source.X,fixture.Source.Y,fixture.Source.Width-30,fixture.Source.Height)),"changed source width disables stale cached click mapping");
                    fixture.Dpi=144;Check(!fixture.Mirror.FrameMatchesSource(fixture.Source),"changed primary DPI disables stale cached click mapping");fixture.Dpi=96;
                    foreach(Color bad in new[]{Color.Black,Color.White,Color.Transparent})
                    {
                        using(var candidate=Flat(bad))
                        {
                            Check(!fixture.Mirror.TryAcceptFrame(candidate,fixture.Source,true),"empty composited frame is rejected: "+bad);
                            Check(Fingerprint(fixture.Cached)==raw && Fingerprint(fixture.Image)==image,"rejected frame cannot replace trusted raw/image cache: "+bad);
                        }
                    }
                    using(var candidate=Icons(true))
                    {
                        Check(!fixture.Mirror.TryAcceptFrame(candidate,fixture.Source,false),"late source-readiness veto rejects otherwise plausible pixels");
                        Check(Fingerprint(fixture.Cached)==raw,"readiness veto preserves last good raw frame");
                    }
                    fixture.Next.Dispose();fixture.Next=Flat(Color.Black);fixture.Mirror.CaptureNow();
                    Check(fixture.Mirror.FrameReady && Fingerprint(fixture.Cached)==raw && IconPixels(fixture.Image)>0,"live capture of a bad frame preserves previously rendered icons");
                    int captures=fixture.Captures;fixture.PrimaryReady=false;fixture.TargetExposed=true;fixture.Mirror.CaptureNow();
                    Check(fixture.Captures==captures,"content-hidden primary with onscreen source bounds is never captured as desktop/background");
                    Check(fixture.Mirror.Visible && fixture.Mirror.FrameReady && IconPixels(fixture.Image)>0,"target reopens with hidden cached primary icons, beyond gear and native clock only");
                    for(int i=0;i<12;i++)fixture.Mirror.CaptureNow();
                    Check(fixture.Moves==0 && fixture.Clicks==0,"cached recovery and passive retries never warp or click pointer");
                    fixture.TargetExposed=false;fixture.Mirror.CaptureNow();Check(!fixture.Mirror.Visible,"target hides a cached mirror without discarding good frame");
                    Check(Fingerprint(fixture.Cached)==raw,"target visibility changes do not replace trusted primary pixels");
                }
                using(var retry=new CaptureFixture(target))
                {
                    retry.Mirror.Start();retry.Pointer=Cursor.Position;
                    var placeholder=(PictureBox)Field(retry.Mirror,"picture");
                    typeof(Control).GetMethod("OnMouseUp",Private).Invoke(placeholder,new object[]{new MouseEventArgs(MouseButtons.Left,1,placeholder.Width/2,placeholder.Height/2,0)});
                    Check(!(bool)Field(retry.Mirror,"pendingClick"),"first-frame placeholder requests loading without queuing a native tray click");
                    Pump(1800);
                    int moves=retry.Moves;for(int i=0;i<15;i++)retry.Mirror.CaptureNow();Pump(80);
                    Check(retry.Clicks==0 && retry.Moves<=2,"one explicit first-frame retry uses at most one bounded simulated reveal/return");
                    Check(retry.Moves==moves,"expired manual retry cannot become repeated background pointer recovery");
                    Check(retry.Mirror.Visible && !retry.Mirror.FrameReady,"failed retry leaves an exposed settings/retry entry rather than disappearing entirely");
                    var gear=(Label)Field(retry.Mirror,"gear");
                    Check(gear.ContextMenuStrip!=null && gear.ContextMenuStrip.Items.Count>=2,"first-frame settings gear retains an explicit reload menu entry");
                    gear.ContextMenuStrip.Items[1].PerformClick();
                    Check(!(bool)Field(retry.Mirror,"pendingClick"),"reload menu requests loading without inventing a native button coordinate");
                    Pump(1800);
                    Check(retry.Clicks==0 && retry.Moves<=moves+2 && retry.SettingsOpened==0,"explicit reload menu performs one bounded fake reveal and never forwards a tray click");
                }
                using(var fixture=new CaptureFixture(target))
                using(var raw=Icons(false))
                {
                    Check(fixture.Mirror.TryAcceptFrame(raw,fixture.Source,true),"unstarted component accepts a synthetic good frame for work-area preservation check");
                    object cached=fixture.Cached,image=fixture.Image;long fingerprint=Fingerprint(fixture.Cached);
                    object context=FormatterServices.GetUninitializedObject(typeof(CompanionContext));
                    var contextType=typeof(CompanionContext);
                    string topology=(string)contextType.GetMethod("Topology",Private).Invoke(context,null);
                    contextType.GetField("topology",Private).SetValue(context,topology);
                    contextType.GetField("workAreas",Private).SetValue(context,"synthetic previous work-area key");
                    contextType.GetField("trayMirror",Private).SetValue(context,fixture.Mirror);
                    contextType.GetMethod("RefreshTopology",Private).Invoke(context,null);
                    Check(Object.ReferenceEquals(contextType.GetField("trayMirror",Private).GetValue(context),fixture.Mirror),"work-area-only refresh preserves exact tray component instance");
                    Check(Object.ReferenceEquals(cached,fixture.Cached) && Object.ReferenceEquals(image,fixture.Image) && Fingerprint(fixture.Cached)==fingerprint,"work-area-only refresh does not clear cached raw pixels or prepared icons");
                    Check((string)contextType.GetField("workAreas",Private).GetValue(context)!="synthetic previous work-area key" && !(bool)Field(fixture.Mirror,"started"),"lightweight work-area path updates its key without starting hooks or rebuilding components");
                }
            }
            finally{Settings.Current=previous;}
        }
        private static void PresentationRollback()
        {
            Screen target=null;foreach(Screen screen in Screen.AllScreens)if(!screen.Primary){target=screen;break;}
            if(target==null)throw new Exception("Presentation fixtures require a secondary screen");
            Settings previous=Settings.Current;
            try
            {
                Settings.Current=new Settings{ScreenName=target.DeviceName,Desktop=false,AltTab=false,Tray=true,TransparentTray=true,WallpaperAwareTray=false,HighResponse=true};
                using(var fixture=new CaptureFixture(target,true))
                {
                    fixture.PrimaryReady=true;fixture.Mirror.Start();
                    Check(fixture.Mirror.FrameReady && fixture.Presents>0,"presentation rollback starts from an actually submitted synthetic good frame");
                    object raw=fixture.Cached,display=fixture.Image;long rawHash=Fingerprint(fixture.Cached),displayHash=Fingerprint(fixture.Image);
                    object source=Field(fixture.Mirror,"lastTraySource"),dpi=Field(fixture.Mirror,"lastSourceDpi"),waiting=Field(fixture.Mirror,"waitingForSource");string title=fixture.Mirror.Text;
                    fixture.FailPresent=true;fixture.Dpi=144;
                    using(var candidate=new Bitmap(510,48,PixelFormat.Format32bppPArgb))
                    using(var icons=Icons(true))
                    {
                        using(var graphics=Graphics.FromImage(candidate))graphics.DrawImageUnscaled(icons,Point.Empty);
                        Check(!fixture.Mirror.TryAcceptFrame(candidate,new Rectangle(fixture.Source.Location,candidate.Size),true),"layer presentation denial rejects a valid candidate instead of committing it");
                        Check(candidate.Width==510,"presentation rejection preserves caller ownership of candidate pixels");
                    }
                    Check(Object.ReferenceEquals(raw,fixture.Cached) && Object.ReferenceEquals(display,fixture.Image),"presentation failure restores exact raw and displayed image references");
                    Check(source.Equals(Field(fixture.Mirror,"lastTraySource")) && dpi.Equals(Field(fixture.Mirror,"lastSourceDpi")),"presentation failure restores cached source geometry and DPI mapping");
                    Check(waiting.Equals(Field(fixture.Mirror,"waitingForSource")) && fixture.Mirror.Text==title,"presentation failure restores previous ready state and accessible title");
                    Check(Fingerprint(fixture.Cached)==rawHash && Fingerprint(fixture.Image)==displayHash,"rolled-back raw and displayed images remain usable and undisposed");
                    fixture.Dpi=96;
                    typeof(TrayMirrorForm).GetMethod("SetImage",Private).Invoke(fixture.Mirror,new object[]{Icons(true)});
                    Check(Object.ReferenceEquals(display,fixture.Image) && Fingerprint(fixture.Image)==displayHash,"hidden-clock SetImage failure retains the previous usable display instead of disposing it");
                    fixture.FailPresent=false;fixture.Mirror.CaptureNow();
                    Check(fixture.Mirror.FrameReady && fixture.Mirror.Visible && IconPixels(fixture.Image)>0,"successful retry after a presentation rollback can submit usable icons again");
                }
                using(var fixture=new CaptureFixture(target,true))
                {
                    fixture.PrimaryReady=true;fixture.FailPresent=true;fixture.Mirror.Start();int initial=fixture.Presents;
                    Check(initial>0 && !fixture.Mirror.FrameReady && !fixture.Mirror.Visible,"first presentation failure keeps an unsubmitted layer hidden without escaping Start");
                    Pump(350);
                    Check(fixture.Presents<=initial+1 && fixture.Captures<=2,"persistent first-frame presentation failure backs off instead of failing on every short timer tick");
                    Check(!fixture.Mirror.FrameReady && fixture.Image!=null && fixture.Moves==0 && fixture.Clicks==0,"first-frame failure retains a recoverable placeholder and never injects input");
                    int presents=fixture.Presents,captures=fixture.Captures;for(int i=0;i<8;i++)fixture.Mirror.RetryLoad();
                    Check(fixture.Presents==presents && fixture.Captures==captures && fixture.Moves==0 && fixture.Clicks==0,"repeated explicit reload requests respect presentation cooldown instead of forcing capture or pointer recovery");
                    fixture.FailPresent=false;fixture.PrimaryReady=false;fixture.Mirror.CaptureNow();
                    Check(fixture.Mirror.Visible && fixture.Mirror.WaitingForSource && !fixture.Mirror.FrameReady,"presentation recovery can expose the settings and loading entry before source pixels are ready");
                    using(var surface=fixture.Mirror.ComposeSurface())
                    {
                        Check(surface!=null && surface.Size==fixture.Mirror.Size,"fallback preview uses the real final layered-surface composition");
                        int clock=(int)Math.Ceiling(TrayMirrorForm.EstimatedClockWidth(fixture.Image.Size,fixture.Dpi)*(surface.Width-30)/(double)fixture.Image.Width);
                        Rectangle message=new Rectangle(30,0,surface.Width-30-clock,surface.Height);
                        Check(BrightOpaquePixels(surface,new Rectangle(0,0,30,surface.Height))>15,"actual fallback composition draws a visible opaque settings gear");
                        Check(BrightOpaquePixels(surface,message)>60,"actual fallback composition draws readable bright opaque loading glyphs");
                        Check(OpaquePixels(surface,message)>=message.Width*message.Height*9/10,"loading text has a mostly opaque backing rather than disappearing into transparent taskbar pixels");
                    }
                    var gear=(Label)Field(fixture.Mirror,"gear");typeof(Control).GetMethod("OnClick",Private).Invoke(gear,new object[]{EventArgs.Empty});
                    Check(fixture.SettingsOpened==1,"recovered fallback settings entry remains operable after an initial presentation exception");
                    fixture.PrimaryReady=true;fixture.Mirror.CaptureNow();
                    Check(fixture.Mirror.Visible && fixture.Mirror.FrameReady && !fixture.Mirror.WaitingForSource && IconPixels(fixture.Image)>0,"a later trusted capture replaces recovered fallback with genuine icon pixels");
                }
                using(var fixture=new CaptureFixture(target,true))
                {
                    fixture.Source=new Rectangle(fixture.Source.Location,new Size(80,48));fixture.Next.Dispose();fixture.Next=new Bitmap(80,48,PixelFormat.Format32bppPArgb);
                    using(var graphics=Graphics.FromImage(fixture.Next)){graphics.Clear(Color.FromArgb(35,39,47));graphics.FillRectangle(Brushes.White,20,16,10,16);}
                    fixture.PrimaryReady=true;fixture.Mirror.Start();
                    Check(fixture.Mirror.FrameReady,"narrow native tray source accepts its sparse synthetic icon");
                    using(var surface=fixture.Mirror.ComposeSurface())
                        Check(BrightOpaquePixels(surface,new Rectangle(30,0,(surface.Width-30)/2,surface.Height))>20,"final narrow-source composition preserves left icon pixels when clearing the native clock area");
                }
            }
            finally{Settings.Current=previous;}
        }
        private static void ManualRevealLifecycle()
        {
            Screen target=null;foreach(Screen screen in Screen.AllScreens)if(!screen.Primary){target=screen;break;}
            if(target==null)throw new Exception("Manual reveal fixtures require a secondary screen");
            Settings previous=Settings.Current;
            try
            {
                Settings.Current=new Settings{ScreenName=target.DeviceName,Desktop=false,AltTab=false,Tray=true,TransparentTray=true,WallpaperAwareTray=false,HighResponse=true};
                using(var fixture=new CaptureFixture(target,true))
                {
                    fixture.PrimaryReady=true;fixture.TargetExposed=false;fixture.Next.Dispose();fixture.Next=Flat(Color.Black);fixture.Mirror.Start();
                    var pointer=ArmManualReveal(fixture);Point home=pointer.Origin;int generation=(int)Field(fixture.Mirror,"frameGeneration"),captures=fixture.Captures;
                    fixture.Mirror.RefreshManualReveal();
                    Check(fixture.Captures==captures+1 && !fixture.Mirror.FrameReady,"manual reveal samples an actually bad first frame without claiming readiness");
                    Check((bool)Field(fixture.Mirror,"forwarding") && pointer.IsOwned && fixture.Moves==1,"bad first frame keeps its one simulated reveal owned instead of returning before pixels exist");
                    Check((int)Field(fixture.Mirror,"frameGeneration")==generation,"rejected first pixels do not advance successful-frame generation");
                    fixture.Next.Dispose();fixture.Next=Icons(false);Pump(530);fixture.Mirror.RefreshManualReveal();
                    Check(fixture.Mirror.FrameReady && (int)Field(fixture.Mirror,"frameGeneration")!=generation,"delayed good pixels after capture cooldown complete the current manual reveal generation");
                    Check(!(bool)Field(fixture.Mirror,"forwarding") && !pointer.IsOwned && fixture.Pointer==home && fixture.Moves==2 && fixture.Clicks==0,"successful manual reveal returns its simulated hover once without forwarding a click");
                }
                using(var fixture=new CaptureFixture(target,true))
                {
                    fixture.PrimaryReady=true;fixture.TargetExposed=false;fixture.Mirror.Start();object cached=fixture.Cached;int generation=(int)Field(fixture.Mirror,"frameGeneration");
                    fixture.Next.Dispose();fixture.Next=Flat(Color.Black);var pointer=ArmManualReveal(fixture);
                    fixture.Mirror.RefreshManualReveal();
                    Check(fixture.Mirror.FrameReady && Object.ReferenceEquals(cached,fixture.Cached) && (int)Field(fixture.Mirror,"frameGeneration")==generation,"failed refresh preserves the old good cache without creating a new accepted generation");
                    Check((bool)Field(fixture.Mirror,"forwarding") && pointer.IsOwned,"an old cached good frame cannot prematurely complete a new manual loading request");
                    fixture.Next.Dispose();fixture.Next=Icons(true);Pump(530);fixture.Mirror.RefreshManualReveal();
                    Check(!(bool)Field(fixture.Mirror,"forwarding") && !pointer.IsOwned && fixture.Moves==2,"newly accepted frame eventually completes a reveal that already had an old cache");
                }
                using(var fixture=new CaptureFixture(target,true))
                {
                    fixture.PrimaryReady=true;fixture.TargetExposed=false;fixture.Next.Dispose();fixture.Next=Flat(Color.Black);fixture.Mirror.Start();var pointer=ArmManualReveal(fixture);Point home=pointer.Origin;
                    for(int i=0;i<99;i++)fixture.Mirror.RefreshManualReveal();
                    Check((bool)Field(fixture.Mirror,"forwarding") && pointer.IsOwned && (int)Field(fixture.Mirror,"revealAttempts")==99,"persistent bad pixels remain bounded by the explicit manual attempt budget");
                    fixture.Mirror.RefreshManualReveal();
                    Check(!(bool)Field(fixture.Mirror,"forwarding") && !pointer.IsOwned && (int)Field(fixture.Mirror,"revealAttempts")==100,"hundredth failed manual refresh terminates the reveal instead of polling forever");
                    Check(fixture.Pointer==home && fixture.Moves==2 && fixture.Clicks==0,"manual attempt exhaustion returns only its owned simulated hover");
                }
                using(var fixture=new CaptureFixture(target,true))
                {
                    fixture.PrimaryReady=true;fixture.TargetExposed=false;fixture.Next.Dispose();fixture.Next=Flat(Color.Black);fixture.Mirror.Start();var pointer=ArmManualReveal(fixture);
                    Point user=new Point(fixture.Pointer.X+17,fixture.Pointer.Y+9);fixture.Pointer=user;pointer.ObserveUserMovement();int moves=fixture.Moves,captures=fixture.Captures;
                    fixture.Mirror.RefreshManualReveal();
                    Check(!(bool)Field(fixture.Mirror,"forwarding") && !pointer.IsOwned,"observed user movement cancels manual loading ownership immediately");
                    Check(fixture.Pointer==user && fixture.Moves==moves && fixture.Clicks==0 && fixture.Captures==captures,"cancelled manual loading never forces the pointer home or captures after ownership is lost");
                }
            }
            finally{Settings.Current=previous;}
        }
        private static TrayPointerLease ArmManualReveal(CaptureFixture fixture)
        {
            var pointer=(TrayPointerLease)Field(fixture.Mirror,"pointer");
            if(!pointer.Begin(fixture.Pointer) || !pointer.Move(new Point(fixture.Pointer.X+41,fixture.Pointer.Y-23)))throw new Exception("Could not arm simulated manual reveal");
            var type=typeof(TrayMirrorForm);type.GetField("forwarding",Private).SetValue(fixture.Mirror,true);type.GetField("pendingClick",Private).SetValue(fixture.Mirror,false);
            type.GetField("revealAttempts",Private).SetValue(fixture.Mirror,0);type.GetField("revealFrameGeneration",Private).SetValue(fixture.Mirror,Field(fixture.Mirror,"frameGeneration"));
            type.GetField("nextCaptureAttempt",Private).SetValue(fixture.Mirror,Environment.TickCount);type.GetField("capturePausedUntil",Private).SetValue(fixture.Mirror,Environment.TickCount);
            return pointer;
        }
        private static int BrightOpaquePixels(Bitmap image,Rectangle area)
        {
            int count=0;area=Rectangle.Intersect(area,new Rectangle(Point.Empty,image.Size));
            for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++){Color pixel=image.GetPixel(x,y);if(pixel.A>=250 && pixel.R>160 && pixel.G>160 && pixel.B>160)count++;}return count;
        }
        private static int OpaquePixels(Bitmap image,Rectangle area)
        {
            int count=0;area=Rectangle.Intersect(area,new Rectangle(Point.Empty,image.Size));
            for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++)if(image.GetPixel(x,y).A>=250)count++;return count;
        }
        private static Bitmap Icons(bool light)
        {
            var image=new Bitmap(520,48,PixelFormat.Format32bppPArgb);
            using(var g=Graphics.FromImage(image))
            {
                g.Clear(light?Color.FromArgb(235,235,235):Color.FromArgb(35,39,47));
                using(var brush=new SolidBrush(light?Color.Black:Color.White))
                {g.FillRectangle(brush,65,16,11,16);g.FillEllipse(brush,111,16,15,15);g.FillRectangle(brush,157,19,13,11);}
                g.FillRectangle(Brushes.CornflowerBlue,202,17,13,13);
            }
            return image;
        }
        private static Bitmap Flat(Color color)
        {var image=new Bitmap(520,48,PixelFormat.Format32bppPArgb);using(var g=Graphics.FromImage(image))g.Clear(color);return image;}
        private static long Fingerprint(Image image)
        {
            if(image==null)return 0;var bitmap=image as Bitmap;if(bitmap==null)throw new Exception("Expected bitmap frame");
            long value=17;unchecked{for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)value=value*31+bitmap.GetPixel(x,y).ToArgb();}return value;
        }
        private static int IconPixels(Image image)
        {
            var bitmap=image as Bitmap;if(bitmap==null)return 0;int count=0;
            // Exclude assistant chevron and native clock; only genuine icon cells.
            for(int y=10;y<Math.Min(38,bitmap.Height);y++)for(int x=50;x<Math.Min(230,bitmap.Width);x++)if(bitmap.GetPixel(x,y).A>=180)count++;
            return count;
        }
        private static object Field(object value,string name){return value.GetType().GetField(name,Private).GetValue(value);}
        private static void Pump(int milliseconds){int start=Environment.TickCount;while(unchecked(Environment.TickCount-start)<milliseconds){Application.DoEvents();Thread.Sleep(2);}}
        private static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("OK: "+message);}
    }
}
