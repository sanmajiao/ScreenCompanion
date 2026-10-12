using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenCompanion
{
    // Relocate only the genuine Start menu produced by a plain Win tap.
    // Never inject Win keys or show a closed Start menu window.
    internal sealed class StartMenuController : IDisposable
    {
        private readonly Control dispatcher;
        private readonly Timer timer=new Timer();
        private readonly WinKeyGesture gesture=new WinKeyGesture();
        private readonly KeyboardListener keyboard;
        private IntPtr menu,bar;
        private Rectangle originalMenu,originalBar,lastBar;
        private uint menuDpi;
        private int started;
        private bool disposed,observed;
        internal StartMenuController(Control owner)
        {
            dispatcher=owner;timer.Interval=ResponsePolicy.StartDiscovery;timer.Tick+=delegate{Track();};
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
        internal static bool IsStartMenu(IntPtr h)
        {
            if(h==IntPtr.Zero || !Native.IsWindowVisible(h))return false;
            int cloaked;if(Native.DwmGetWindowAttribute(h,14,out cloaked,4)==0 && cloaked!=0)return false;
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
            Screen target=Settings.TargetScreen();if(target==null){Stop();return;}
            IntPtr foreground=Native.GetForegroundWindow(),found=IntPtr.Zero;
            if(StartForeground(foreground))found=FindStartWindow();
            if(found==IntPtr.Zero)
            {
                if(observed || Environment.TickCount-started>2000)Stop();
                return;
            }
            if(found!=menu)
            {
                RestoreBar();menu=found;originalMenu=Native.Bounds(found);menuDpi=Native.Dpi(found);
                bar=TrayMirrorForm.FindTargetTaskbar();originalBar=Native.Bounds(bar);lastBar=Rectangle.Empty;
            }
            observed=true;
            // Auto-hide is kept intact: expose the real target taskbar only
            // while Start is open, then restore the shell's original rectangle.
            Rectangle screen=target.Bounds;int barHeight=Math.Max(1,Math.Min(screen.Height/4,originalBar.Height));
            if(bar!=IntPtr.Zero)
            {
                lastBar=new Rectangle(originalBar.Left,screen.Bottom-barHeight,originalBar.Width,barHeight);
                if(Native.Bounds(bar)!=lastBar)Native.SetWindowPos(bar,IntPtr.Zero,lastBar.X,lastBar.Y,lastBar.Width,lastBar.Height,4|16);
            }
            // Start often uses a transparent canvas covering the entire source
            // monitor. Move that canvas as a screen, not as a small tray flyout.
            Screen sourceScreen=Screen.FromRectangle(originalMenu);
            bool canvas=originalMenu.Width>=sourceScreen.Bounds.Width-16;
            double scale=canvas?1:Native.Dpi(bar)/(double)Math.Max(1,menuDpi);
            int width=Math.Min(screen.Width,Math.Max(100,(int)Math.Round(originalMenu.Width*scale)));
            int height=Math.Min(screen.Height-barHeight-8,Math.Max(100,(int)Math.Round(originalMenu.Height*scale)));
            Rectangle desired=canvas?new Rectangle(screen.Left,screen.Top,screen.Width,screen.Height-barHeight):
                new Rectangle(screen.Left+(screen.Width-width)/2,screen.Bottom-barHeight-height-4,width,height);
            if(Native.Bounds(menu)!=desired)Native.SetWindowPos(menu,IntPtr.Zero,desired.X,desired.Y,desired.Width,desired.Height,4|16);
            timer.Interval=ResponsePolicy.PopupTracking;
        }
        private void RestoreBar()
        {
            if(bar!=IntPtr.Zero && Native.IsWindow(bar) && !originalBar.IsEmpty && Native.Bounds(bar)==lastBar)
                Native.SetWindowPos(bar,IntPtr.Zero,originalBar.X,originalBar.Y,originalBar.Width,originalBar.Height,4|16);
            bar=IntPtr.Zero;lastBar=Rectangle.Empty;
        }
        private void Stop(){timer.Stop();RestoreBar();menu=IntPtr.Zero;observed=false;}
        public void Dispose(){if(disposed)return;disposed=true;keyboard.Dispose();Stop();timer.Dispose();}
    }
}
