using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
namespace ScreenCompanion
{
    // Pass global mouse input through an independent pump, never the UI pump.
    internal sealed class MouseListener : IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEvent ready=new ManualResetEvent(false);
        private readonly AutoResetEvent pong=new AutoResetEvent(false);
        private readonly Action<Point> observer;
        private Native.HookProc proc;
        private IntPtr hook;
        private volatile uint threadId;
        private volatile bool stopping;
        private int disposed;
        internal MouseListener(Action<Point> callback)
        {
            observer=callback;thread=new Thread(Run){IsBackground=true,Name="ScreenCompanion desktop mouse input"};thread.Start();
            if(!ready.WaitOne(2000) || hook==IntPtr.Zero){Dispose();throw new InvalidOperationException("无法注册桌面鼠标监听。");}
        }
        private void Run()
        {
            Native.Message message;Native.PeekMessage(out message,IntPtr.Zero,0,0,0);threadId=Native.GetCurrentThreadId();
            try
            {
                if(stopping)return;
                proc=delegate(int code,IntPtr w,IntPtr l)
                {
                    try{if(code>=0 && !stopping && w==new IntPtr(0x201))observer(((Native.MouseData)Marshal.PtrToStructure(l,typeof(Native.MouseData))).Point);}catch{}
                    return Native.CallNextHookEx(hook,code,w,l);
                };
                hook=Native.SetWindowsHookEx(14,proc,Native.GetModuleHandle(null),0);ready.Set();
                if(hook==IntPtr.Zero || stopping)return;
                while(Native.GetMessage(out message,IntPtr.Zero,0,0)>0)
                {
                    if(message.Id==0x8001){pong.Set();continue;}
                    Native.TranslateMessage(ref message);Native.DispatchMessage(ref message);
                }
            }
            finally{if(hook!=IntPtr.Zero)Native.UnhookWindowsHookEx(hook);hook=IntPtr.Zero;threadId=0;ready.Set();}
        }
        internal bool Responsive(int timeout){uint id=threadId;return id!=0 && Native.PostThreadMessage(id,0x8001,IntPtr.Zero,IntPtr.Zero) && pong.WaitOne(timeout);}
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            stopping=true;uint id=threadId;if(id!=0)Native.PostThreadMessage(id,0x12,IntPtr.Zero,IntPtr.Zero);
            if(Thread.CurrentThread!=thread && thread.Join(2000)){ready.Dispose();pong.Dispose();}
        }
    }
}
