using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;

namespace ScreenCompanion
{
    // Track transient windows created by a real tray click. Some modern shell
    // flyouts are not returned by EnumWindows, so inspect the foreground HWND too.
    internal sealed class TrayPopupTracker : IDisposable
    {
        private readonly Timer timer=new Timer();
        private readonly HashSet<IntPtr> baseline=new HashSet<IntPtr>();
        private readonly HashSet<IntPtr> relocated=new HashSet<IntPtr>();
        private Point destination,source;
        private int started;
        private IntPtr current;
        private Rectangle original;
        private uint originalDpi;
        internal IntPtr LastMoved;
        internal int LastLatency=-1;
        internal TrayPopupTracker(){timer.Interval=16;timer.Tick+=delegate{Track();};}
        internal void Begin(Point from,Point to)
        {
            baseline.Clear();relocated.Clear();current=IntPtr.Zero;LastMoved=IntPtr.Zero;LastLatency=-1;
            source=from;destination=to;started=Environment.TickCount;
            Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Exposed(h))baseline.Add(h);return true;},IntPtr.Zero);
            IntPtr foreground=Native.GetForegroundWindow();if(Exposed(foreground))baseline.Add(foreground);
            timer.Interval=16;timer.Start();
        }
        private static bool Exposed(IntPtr h)
        {
            if(h==IntPtr.Zero || !Native.IsWindow(h) || !Native.IsWindowVisible(h))return false;
            int cloaked;return Native.DwmGetWindowAttribute(h,14,out cloaked,4)!=0 || cloaked==0;
        }
        internal static bool IsShellFlyout(string cls)
        {
            return cls=="ControlCenterWindow" || cls=="Windows.UI.Core.CoreWindow" ||
                cls=="XamlExplorerHostIslandWindow" || cls=="Shell_InputSwitchTopLevelWindow";
        }
        private bool Eligible(IntPtr h,bool foreground)
        {
            if(!Exposed(h) || relocated.Contains(h))return false;
            string cls=Native.ClassName(h);
            if(cls=="SysShadow" || cls=="tooltips_class32")return false;
            if(cls=="TopLevelWindowForOverflowXamlIsland" || cls=="NotifyIconOverflowWindow" || cls=="Shell_NotifyIconOverflowWindow")return false;
            Rectangle r=Native.Bounds(h);if(r.Width<20 || r.Height<20)return false;
            if(IsShellFlyout(cls))return ShellProcess(h) && (foreground || !baseline.Contains(h));
            if(baseline.Contains(h))return false;
            if(cls=="#32768" || cls=="Microsoft.UI.Content.PopupWindowSiteBridge")return true;
            // App-owned tray menus are popups near the source click. Never move
            // an unrelated normal application window just because it appeared.
            int ex=Native.GetWindowLong(h,-20),style=Native.GetWindowLong(h,-16);
            return (style&unchecked((int)0x80000000))!=0 && (ex&0x88)!=0 && (style&0x00C00000)==0 &&
                Rectangle.Inflate(r,80,80).Contains(source);
        }
        private static bool ShellProcess(IntPtr h)
        {
            try{using(var process=Process.GetProcessById((int)Native.ProcessId(h)))
            {
                string name=process.ProcessName;
                return name.Equals("ShellHost",StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ShellExperienceHost",StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("explorer",StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("TextInputHost",StringComparison.OrdinalIgnoreCase);
            }}catch{return false;}
        }
        private void Track()
        {
            Screen target=Settings.TargetScreen();if(target==null){timer.Stop();return;}
            IntPtr found=IntPtr.Zero,foreground=Native.GetForegroundWindow();
            if(current!=IntPtr.Zero && Exposed(current))
            {
                // The shell reanchors its window after opening animations and
                // after the primary auto-hide bar collapses. Keep the cached
                // geometry while it is active, without showing a closed popup.
                if(!IsShellFlyout(Native.ClassName(current)) || foreground==current || Native.GetAncestor(foreground,2)==current)
                {
                    Place(current,target,original,originalDpi);return;
                }
                else {timer.Stop();return;}
            }
            if(Eligible(foreground,true))found=foreground;
            if(found==IntPtr.Zero)Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Eligible(h,false)){found=h;return false;}return true;},IntPtr.Zero);
            if(found!=IntPtr.Zero)
            {
                Rectangle r=Native.Bounds(found);uint dpi=Native.Dpi(found);
                if(Place(found,target,r,dpi))
                {
                    original=r;originalDpi=dpi;
                    relocated.Add(found);current=found;LastMoved=found;LastLatency=Environment.TickCount-started;timer.Interval=50;
                }
            }
            if(Environment.TickCount-started>2500 && (current==IntPtr.Zero || !Exposed(current)))timer.Stop();
            else if(Environment.TickCount-started>15000)timer.Stop();
        }
        private bool Place(IntPtr found,Screen target,Rectangle r,uint dpi)
        {
                Rectangle area=target.Bounds;
                IntPtr bar=TrayMirrorForm.FindTargetTaskbar();Rectangle barBounds=Native.Bounds(bar);
                int bottom=area.Bottom-Math.Max(0,Math.Min(area.Height/4,barBounds.Height));
                double scale=Native.Dpi(bar)/(double)Math.Max(1,dpi);
                int width=Math.Min(area.Width,Math.Max(20,(int)Math.Round(r.Width*scale)));
                int height=Math.Min(bottom-area.Top,Math.Max(20,(int)Math.Round(r.Height*scale)));
                bool shell=IsShellFlyout(Native.ClassName(found));
                int x=shell?area.Right-width-8:destination.X;
                int y=shell?bottom-height-8:destination.Y-height;
                x=Math.Max(area.Left,Math.Min(area.Right-width,x));y=Math.Max(area.Top,Math.Min(bottom-height,y));
                Rectangle desired=new Rectangle(x,y,width,height);
                return Native.Bounds(found)==desired || Native.SetWindowPos(found,IntPtr.Zero,x,y,width,height,4|16);
        }
        public void Dispose(){timer.Stop();timer.Dispose();}
    }
}
