using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ScreenCompanion
{
    internal static class Native
    {
        internal delegate bool EnumProc(IntPtr h, IntPtr p);
        internal delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public Rect(Rectangle r) { Left=r.Left; Top=r.Top; Right=r.Right; Bottom=r.Bottom; }
            public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left,Top,Right,Bottom); } }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData { public uint Key, Scan, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public Point Point; public uint Data,Flags,Time;public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct Message { public IntPtr Handle;public uint Id;public IntPtr WParam,LParam;public uint Time;public Point Point;public uint Private; }
        [StructLayout(LayoutKind.Sequential)] internal struct Thumb
        {
            public uint Flags; public Rect Destination, Source; public byte Opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool Visible;
            [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyInput Key;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X,Y; public uint MouseData,Flags,Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyInput { public ushort Key, Scan; public uint Flags,Time; public IntPtr Extra; }
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls,string title);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string cls,string title);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr h,StringBuilder b,int n);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetWindowText(IntPtr h,StringBuilder b,int n);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr h,uint command);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr h,uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetLastActivePopup(IntPtr h);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr h,int index);
        [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr h,int index,int value);
        [DllImport("user32.dll")] internal static extern IntPtr SetParent(IntPtr child,IntPtr parent);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h,int command);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h,out Rect r);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint thread,uint message,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] internal static extern int GetMessage(out Message message,IntPtr h,uint min,uint max);
        [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message,IntPtr h,uint min,uint max,uint remove);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] internal static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr SetWindowsHookEx(int kind,HookProc proc,IntPtr module,uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr h,int code,IntPtr w,IntPtr l);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint a,uint b,bool attach);
        [DllImport("user32.dll")] internal static extern uint SendInput(uint n,Input[] input,int size);
        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x,int y);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h,uint attr,out int value,int size);
        [DllImport("dwmapi.dll")] internal static extern int DwmRegisterThumbnail(IntPtr destination,IntPtr source,out IntPtr thumb);
        [DllImport("dwmapi.dll")] internal static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] internal static extern int DwmUpdateThumbnailProperties(IntPtr thumb,ref Thumb properties);
        [DllImport("dwmapi.dll")] internal static extern int DwmQueryThumbnailSourceSize(IntPtr thumb,out Size size);
        internal static string ClassName(IntPtr h) { var b=new StringBuilder(256); GetClassName(h,b,b.Capacity); return b.ToString(); }
        internal static string Title(IntPtr h) { var b=new StringBuilder(2048); GetWindowText(h,b,b.Capacity); return b.ToString(); }
        internal static Rectangle Bounds(IntPtr h) { Rect r; return GetWindowRect(h,out r)?r.Rectangle:Rectangle.Empty; }
        internal static uint Dpi(IntPtr h){try{uint d=GetDpiForWindow(h);return d==0?96:d;}catch(EntryPointNotFoundException){return 96;}}
        internal static uint ProcessId(IntPtr h) { uint p; GetWindowThreadProcessId(h,out p); return p; }
        internal static bool KeyPressed(int key) { return (GetAsyncKeyState(key)&0x8000)!=0; }
        internal static void ReleaseAlt()
        {
            // End the native switcher before applying the explicitly clicked HWND.
            var a=new Input[3]; int[] keys={0x12,0xA4,0xA5};
            for(int i=0;i<a.Length;i++) { a[i].Type=1; a[i].Data.Key=new KeyInput { Key=(ushort)keys[i], Flags=2 }; }
            SendInput((uint)a.Length,a,Marshal.SizeOf(typeof(Input)));
        }
        internal static void PressKey(int key)
        {
            var a=new Input[2];for(int i=0;i<2;i++){a[i].Type=1;a[i].Data.Key=new KeyInput{Key=(ushort)key,Flags=i==0?0u:2u};}
            SendInput(2,a,Marshal.SizeOf(typeof(Input)));
        }
        internal static bool Activate(IntPtr h)
        {
            if(!IsWindow(h)) return false;
            if(IsIconic(h)) ShowWindow(h,9);
            IntPtr fg=GetForegroundWindow(); uint unused;
            uint targetThread=GetWindowThreadProcessId(fg,out unused), ourThread=GetCurrentThreadId();
            bool attached=targetThread!=0 && targetThread!=ourThread && AttachThreadInput(ourThread,targetThread,true);
            try { SetForegroundWindow(h); }
            finally { if(attached) AttachThreadInput(ourThread,targetThread,false); }
            return GetForegroundWindow()==h;
        }
        internal static bool MoveToScreen(IntPtr h,Screen target)
        {
            if(!IsWindow(h) || target==null)return false;
            if(Screen.FromHandle(h).DeviceName==target.DeviceName)return true;
            bool maximized=IsZoomed(h);
            if(maximized || IsIconic(h))ShowWindow(h,9);
            Rectangle old=Bounds(h),from=Screen.FromHandle(h).WorkingArea,to=target.WorkingArea;
            if(old.IsEmpty)return false;
            int width=Math.Min(old.Width,to.Width),height=Math.Min(old.Height,to.Height);
            double x=(old.Left-from.Left)/(double)Math.Max(1,from.Width-old.Width);
            double y=(old.Top-from.Top)/(double)Math.Max(1,from.Height-old.Height);
            int left=to.Left+(int)(Math.Max(0,Math.Min(1,x))*Math.Max(0,to.Width-width));
            int top=to.Top+(int)(Math.Max(0,Math.Min(1,y))*Math.Max(0,to.Height-height));
            bool moved=SetWindowPos(h,IntPtr.Zero,left,top,width,height,4|16);
            if(maximized)ShowWindow(h,3);
            return moved && Screen.FromHandle(h).DeviceName==target.DeviceName;
        }
        internal static bool Click(Point point,MouseButtons button)
        {
            Rectangle desktop=SystemInformation.VirtualScreen;
            uint down=button==MouseButtons.Right?8u:2u, up=button==MouseButtons.Right?16u:4u;
            // Keep movement and buttons in one virtual-desktop input sequence.
            var a=new Input[3];
            a[0].Data.Mouse=new MouseInput{X=(int)Math.Round((point.X-desktop.Left)*65535.0/Math.Max(1,desktop.Width-1)),Y=(int)Math.Round((point.Y-desktop.Top)*65535.0/Math.Max(1,desktop.Height-1)),Flags=0xC001};
            a[1].Data.Mouse.Flags=down; a[2].Data.Mouse.Flags=up;
            return SendInput(3,a,Marshal.SizeOf(typeof(Input)))==3;
        }
        // Mark our pointer transaction so restoration guards distinguish it
        // from genuine user movement and other applications' cursor input.
        internal static readonly IntPtr PointerInputTag=new IntPtr(0x53435051);
        internal static Input PointerMove(Point point)
        {
            Rectangle desktop=SystemInformation.VirtualScreen;
            int x=Math.Max(desktop.Left,Math.Min(desktop.Right-1,point.X));
            int y=Math.Max(desktop.Top,Math.Min(desktop.Bottom-1,point.Y));
            return new Input{Data=new InputUnion{Mouse=new MouseInput{
                X=(int)Math.Round((x-desktop.Left)*65535.0/Math.Max(1,desktop.Width-1)),
                Y=(int)Math.Round((y-desktop.Top)*65535.0/Math.Max(1,desktop.Height-1)),
                Flags=0xC001,Extra=PointerInputTag}}};
        }
        internal static bool MovePointer(Point point)
        {return SendInput(1,new[]{PointerMove(point)},Marshal.SizeOf(typeof(Input)))==1;}
        internal static Input[] PointerClick(Point point,MouseButtons button,Point returnPoint)
        {
            uint down=button==MouseButtons.Right?8u:2u,up=button==MouseButtons.Right?16u:4u;
            return new[]{PointerMove(point),
                new Input{Data=new InputUnion{Mouse=new MouseInput{Flags=down,Extra=PointerInputTag}}},
                new Input{Data=new InputUnion{Mouse=new MouseInput{Flags=up,Extra=PointerInputTag}}},
                PointerMove(returnPoint)};
        }
        internal static bool ClickAndRestore(Point point,MouseButtons button,Point returnPoint)
        {
            if(button!=MouseButtons.Left && button!=MouseButtons.Right)return false;
            var input=PointerClick(point,button,returnPoint);
            uint sent=SendInput((uint)input.Length,input,Marshal.SizeOf(typeof(Input)));
            if(sent==2)SendInput(1,new[]{input[2]},Marshal.SizeOf(typeof(Input)));
            // A partial native-input insertion must not leave our moved cursor
            // behind. Guard this fallback rather than overwriting new user input.
            Point actual=Cursor.Position;
            if(sent>0 && sent<input.Length && Math.Abs(actual.X-point.X)<=2 && Math.Abs(actual.Y-point.Y)<=2)MovePointer(returnPoint);
            return sent==input.Length;
        }
        internal static bool ClickPreservingCursor(IntPtr expectedRoot,Rectangle expectedBounds,Point point,MouseButtons button)
        {
            if(expectedRoot==IntPtr.Zero || !IsWindowVisible(expectedRoot) || Bounds(expectedRoot)!=expectedBounds || !expectedBounds.Contains(point))return false;
            int cloaked;if(DwmGetWindowAttribute(expectedRoot,14,out cloaked,4)==0 && cloaked!=0)return false;
            if(GetAncestor(WindowFromPoint(point),2)!=expectedRoot)return false;
            return ClickAndRestore(point,button,Cursor.Position);
        }
    }
    internal sealed class WindowEntry
    {
        public IntPtr Handle; public uint ProcessId; public string Title;
        private string untitledName;
        public string ReadTitle()
        {
            string title=Native.Title(Handle);if(!String.IsNullOrWhiteSpace(title))return title;
            if(untitledName==null)
            {
                string name="应用";
                try
                {
                    using(var process=Process.GetProcessById((int)ProcessId))
                    {
                        name=process.ProcessName;
                        try { string product=process.MainModule.FileVersionInfo.ProductName;if(!String.IsNullOrWhiteSpace(product))name=product; } catch { }
                    }
                }
                catch { }
                untitledName=name+"（无标题窗口）";
            }
            return untitledName;
        }
        public static List<WindowEntry> Read()
        {
            var result=new List<WindowEntry>(); uint ours=(uint)Process.GetCurrentProcess().Id;
            var groups=new Dictionary<IntPtr,int>();
            var priorities=new List<int>();
            Native.EnumWindows(delegate(IntPtr h,IntPtr p)
            {
                if(!Native.IsWindowVisible(h) || Native.ProcessId(h)==ours) return true;
                string cls=Native.ClassName(h);
                if(cls=="Progman" || cls=="WorkerW" || cls=="Shell_TrayWnd" || cls=="Shell_SecondaryTrayWnd" || cls=="XamlExplorerHostIslandWindow") return true;
                int ex=Native.GetWindowLong(h,-20); bool app=(ex&0x40000)!=0;
                if(!app && (ex&(0x80|0x08000000))!=0) return true;
                if((Native.GetWindowLong(h,-16)&0x40000000)!=0) return true;
                Rectangle bounds=Native.Bounds(h);if(bounds.Width<=0 || bounds.Height<=0)return true;
                int cloaked; if(Native.DwmGetWindowAttribute(h,14,out cloaked,4)==0 && cloaked!=0) return true;
                // A visible owned dialog can be the usable window of its app.
                // Retain one representative per owner group; APPWINDOW is independent.
                IntPtr root=h;
                if(!app)
                {
                    // Some frameworks use an invisible, non-popup owner. Walk
                    // GW_OWNER itself rather than losing that link via GetParent.
                    var seen=new HashSet<IntPtr>();seen.Add(root);
                    for(int depth=0;depth<32;depth++)
                    {
                        if((Native.GetWindowLong(root,-20)&0x40000)!=0)break;
                        IntPtr owner=Native.GetWindow(root,4);if(owner==IntPtr.Zero || !seen.Add(owner))break;root=owner;
                    }
                }
                IntPtr active=Native.GetLastActivePopup(root);
                int priority=h==active?2:h==root?1:0;
                int slot;var entry=new WindowEntry { Handle=h,ProcessId=Native.ProcessId(h) };
                if(groups.TryGetValue(root,out slot))
                {
                    if(priority<=priorities[slot])return true;
                    result[slot]=entry;priorities[slot]=priority;
                }
                else { groups.Add(root,result.Count);result.Add(entry);priorities.Add(priority); }
                return true;
            },IntPtr.Zero);
            foreach(var entry in result)entry.Title=entry.ReadTitle();
            return result;
        }
        public bool Valid { get { return Native.IsWindow(Handle) && Native.ProcessId(Handle)==ProcessId; } }
    }
}
