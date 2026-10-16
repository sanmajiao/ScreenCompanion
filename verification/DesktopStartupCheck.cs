using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using DesktopPortal;

namespace ScreenCompanion
{
    // Uses blocked fake readers and a hidden test form. It never reads the
    // Explorer ListView, installs global hooks, or injects desktop input.
    internal static class DesktopStartupCheck
    {
        private static int checks;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [STAThread]static int Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Coalesce();Generation();ClosedForm(false);ClosedForm(true);
                Console.WriteLine("PASS: " + checks + " desktop startup checks; no global input or native Explorer reads.");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL: " + error); return 1; }
        }
        private static void Coalesce()
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var completed = new CountdownEvent(2))
            {
                var calls = new List<int>();
                var results = new List<int>();
                int active = 0, maximum = 0;
                var queue = new DesktopIconReadQueue(delegate(Rectangle bounds)
                {
                    int concurrent = Interlocked.Increment(ref active);
                    lock (calls) { calls.Add(bounds.X); maximum = Math.Max(maximum, concurrent); }
                    try
                    {
                        if (bounds.X == 1) { entered.Set(); release.WaitOne(); }
                        return new List<NativeDesktopIcon> { new NativeDesktopIcon { Name = bounds.X.ToString() } };
                    }
                    finally { Interlocked.Decrement(ref active); }
                });
                Action<List<NativeDesktopIcon>> finish = delegate(List<NativeDesktopIcon> icons)
                {
                    lock (results) results.Add(Int32.Parse(icons[0].Name));
                    completed.Signal();
                };
                try
                {
                    queue.Enqueue(new Rectangle(1,0,100,100), finish);
                    Check(entered.WaitOne(3000), "first fake Explorer read entered background worker");
                    queue.Enqueue(new Rectangle(2,0,100,100), finish);
                    queue.Enqueue(new Rectangle(3,0,100,100), finish);
                    int ticks = 0;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 15 })
                    {
                        timer.Tick += delegate { ticks++; };timer.Start();
                        var clock = Stopwatch.StartNew();
                        while (clock.ElapsedMilliseconds < 120) { Application.DoEvents();Thread.Sleep(1); }
                        timer.Stop();
                    }
                    Check(ticks > 0, "UI timer keeps running while fake Explorer read is blocked");
                    lock (calls) Check(calls.Count == 1, "additional requests do not start parallel native reads");
                    release.Set();
                    Check(completed.Wait(3000), "first and latest pending read complete");
                    lock (calls) Check(calls.Count == 2 && calls[0] == 1 && calls[1] == 3, "intermediate pending refresh is replaced by latest request");
                    lock (results) Check(results.Count == 2 && results[0] == 1 && results[1] == 3, "completion order remains serial");
                    Check(maximum == 1, "at most one reader runs at a time");
                }
                finally { release.Set(); }
            }
        }
        private static void Generation()
        {
            var bounds = new Rectangle(1920,0,1920,1080);
            Check(DesktopLayerForm.CanApplyNativeRead(false,4,4,"DISPLAY2",bounds,"DISPLAY2",bounds), "current generation and target accept native result");
            Check(!DesktopLayerForm.CanApplyNativeRead(true,4,4,"DISPLAY2",bounds,"DISPLAY2",bounds), "closed layer rejects late native result");
            Check(!DesktopLayerForm.CanApplyNativeRead(false,3,4,"DISPLAY2",bounds,"DISPLAY2",bounds), "older refresh generation is rejected");
            Check(!DesktopLayerForm.CanApplyNativeRead(false,4,4,"DISPLAY2",bounds,"DISPLAY3",bounds), "changed screen identity rejects result even at same geometry");
            Check(!DesktopLayerForm.CanApplyNativeRead(false,4,4,"DISPLAY2",bounds,"DISPLAY2",new Rectangle(1920,0,2560,1440)), "changed screen geometry rejects result");
            Check(!DesktopLayerForm.CanApplyNativeRead(false,4,4,"DISPLAY2",bounds,null,Rectangle.Empty), "removed target rejects result");
        }
        private static void ClosedForm(bool replyAlreadyPosted)
        {
            Screen target = null;foreach (Screen screen in Screen.AllScreens) if (!screen.Primary) { target = screen;break; }
            if (target == null) { Console.WriteLine("SKIP: hidden-form lifecycle check requires an extended screen; pure generation guards ran.");return; }
            Settings previous = Settings.Current;Settings.Current = new Settings { ScreenName = target.DeviceName };
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var drained = new ManualResetEvent(false))
            {
                int calls = 0;
                var queue = new DesktopIconReadQueue(delegate(Rectangle bounds)
                {
                    if (Interlocked.Increment(ref calls) == 1) { entered.Set();release.WaitOne(); }
                    return new List<NativeDesktopIcon>();
                });
                var form = new DesktopLayerForm(queue);
                try
                {
                    IntPtr handle = form.Handle;
                    typeof(DesktopLayerForm).GetMethod("LoadDesktopItems",Private).Invoke(form,null);
                    Check(entered.WaitOne(3000), "hidden layer schedules native read without Shown hook installation");
                    int generation = (int)typeof(DesktopLayerForm).GetField("nativeReadVersion",Private).GetValue(form);
                    if (replyAlreadyPosted)
                    {
                        // Waiting without pumping the UI lets the worker post
                        // its completion before we close the hidden test layer.
                        queue.Enqueue(Rectangle.Empty,delegate { drained.Set(); });
                        release.Set();
                        Check(drained.WaitOne(3000), "native reply is posted before layer closes");
                    }
                    form.Close();form.Dispose();
                    Check(form.IsDisposed, "closing layer does not wait for blocked native read");
                    Check((bool)typeof(DesktopLayerForm).GetField("nativeReadClosed",Private).GetValue(form), "close invalidates pending native result");
                    Check((int)typeof(DesktopLayerForm).GetField("nativeReadVersion",Private).GetValue(form) > generation, "close advances read generation");
                    if (!replyAlreadyPosted)
                    {
                        queue.Enqueue(Rectangle.Empty,delegate { drained.Set(); });
                        release.Set();
                    }
                    Check(drained.WaitOne(3000), "late completion safely returns and shared worker can read again");
                    Application.DoEvents();
                    Check(form.IsDisposed, "late result does not recreate disposed layer handle");
                }
                finally { release.Set();form.Dispose();Settings.Current = previous; }
            }
        }
        private static void Check(bool condition,string label)
        { if (!condition) throw new Exception(label);checks++;Console.WriteLine("OK: " + label); }
    }
}
