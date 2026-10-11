using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;

namespace ScreenCompanion
{
    // Never install a global keyboard hook on the rendering/UI thread.
    // Pass every key through, even if the observer throws or the UI is busy.
    internal sealed class KeyboardListener : IDisposable
    {
        private readonly Action<Native.KeyboardData,bool,bool> observer;
        private readonly Thread thread;
        private readonly ManualResetEvent ready=new ManualResetEvent(false);
        private readonly AutoResetEvent pong=new AutoResetEvent(false);
        private Native.HookProc proc;
        private IntPtr hook;
        private volatile uint threadId;
        private volatile bool stopping;
        private int disposed;
        internal KeyboardListener(Action<Native.KeyboardData,bool,bool> callback)
        {
            observer=callback;thread=new Thread(Run);thread.IsBackground=true;
            thread.Name="ScreenCompanion keyboard input";thread.Start();
            if(!ready.WaitOne(2000) || hook==IntPtr.Zero){Dispose();throw new InvalidOperationException("无法注册键盘监听。");}
        }
        private void Run()
        {
            Native.Message message;Native.PeekMessage(out message,IntPtr.Zero,0,0,0);
            threadId=Native.GetCurrentThreadId();
            try
            {
                if(stopping)return;
                proc=delegate(int code,IntPtr w,IntPtr l)
                {
                    try
                    {
                        if(code>=0 && !stopping)
                        {
                            var data=(Native.KeyboardData)Marshal.PtrToStructure(l,typeof(Native.KeyboardData));
                            observer(data,w==new IntPtr(0x100)||w==new IntPtr(0x104),w==new IntPtr(0x101)||w==new IntPtr(0x105));
                        }
                    }
                    catch { /* An observer failure must never suppress system keys. */ }
                    return Native.CallNextHookEx(hook,code,w,l);
                };
                hook=Native.SetWindowsHookEx(13,proc,Native.GetModuleHandle(null),0);ready.Set();
                if(hook==IntPtr.Zero || stopping)return;
                while(Native.GetMessage(out message,IntPtr.Zero,0,0)>0)
                {
                    if(message.Id==0x8001){pong.Set();continue;}
                    Native.TranslateMessage(ref message);Native.DispatchMessage(ref message);
                }
            }
            finally
            {
                if(hook!=IntPtr.Zero)Native.UnhookWindowsHookEx(hook);
                hook=IntPtr.Zero;threadId=0;ready.Set();
            }
        }
        // Integration probe: tests the actual input pump without injecting keys.
        internal bool Responsive(int timeout)
        {
            uint id=threadId;return id!=0 && Native.PostThreadMessage(id,0x8001,IntPtr.Zero,IntPtr.Zero) && pong.WaitOne(timeout);
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            stopping=true;uint id=threadId;if(id!=0)Native.PostThreadMessage(id,0x12,IntPtr.Zero,IntPtr.Zero);
            if(Thread.CurrentThread!=thread && thread.Join(2000)){ready.Dispose();pong.Dispose();}
        }
    }
    internal sealed class WinKeyGesture
    {
        private bool left,right,other;
        private readonly HashSet<uint> modifiers=new HashSet<uint>();
        internal void Cancel(){other=true;}
        // Recognize a single Win tap, never Win+E/Win+D or other chords.
        internal bool Update(uint key,bool down,bool up)
        {
            bool win=key==0x5B || key==0x5C;
            if(key==0x10 || key==0x11 || key==0x12 || (key>=0xA0 && key<=0xA5))
            {if(down)modifiers.Add(key);if(up)modifiers.Remove(key);}
            if(down && win)
            {
                if(!left && !right)other=modifiers.Count!=0;
                if(key==0x5B)left=true;else right=true;
            }
            else if(down && (left || right))other=true;
            if(!up || !win)return false;
            bool wasHeld=key==0x5B?left:right;
            if(key==0x5B)left=false;else right=false;
            return wasHeld && !left && !right && !other;
        }
    }
}
