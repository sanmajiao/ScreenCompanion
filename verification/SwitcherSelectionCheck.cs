using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ScreenCompanion
{
    // Own-window geometry/DWM plus direct event callbacks. No SendInput, global
    // hook installation or activation of user applications is used by this test.
    internal static class SwitcherSelectionCheck
    {
        private static readonly BindingFlags Members=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static int checks;
        [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")] private static extern int FrameBounds(IntPtr h,uint attribute,out Native.Rect value,int size);
        [STAThread] static int Main(string[] args)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            if(Array.IndexOf(args,"--native")>=0)return NativeMode();
            var windows=new List<Form>();
            try
            {
                var entries=new List<WindowEntry>();uint process=(uint)Process.GetCurrentProcess().Id;
                for(int i=0;i<5;i++)
                {
                    var form=new Form{Text="相同标题",StartPosition=FormStartPosition.Manual,AutoScaleMode=AutoScaleMode.None,Bounds=new Rectangle(80+i*30,100+i*30,340+i*30,230+i*17)};
                    windows.Add(form);IntPtr h=form.Handle;form.Show();
                    entries.Add(new WindowEntry{Handle=h,ProcessId=process,Title=form.Text});
                }
                Pump(80);int screens=0;
                foreach(Screen target in Screen.AllScreens)
                {
                    if(target.Primary)continue;screens++;
                    Settings.Current=new Settings{ScreenName=target.DeviceName,MoveOnSwitch=false,HighResponse=true};
                    var order=new List<WindowEntry>(entries);var activated=new List<IntPtr>();int altReleases=0;
                    bool alt=false,shift=false;
                    using(var panel=new SwitcherForm(delegate{return new List<WindowEntry>(order);},delegate(IntPtr h){activated.Add(h);return true;},delegate{altReleases++;},delegate(int key){return key==0x12?alt:key==0x10?shift:false;},delegate{return entries[4].Handle;}))
                    {
                        IntPtr foreground=Native.GetForegroundWindow();panel.OpenPinned();Pump(30);
                        Check(Native.GetForegroundWindow()==foreground,"creating/showing no-activate panel preserves native foreground on "+target.DeviceName);
                        Check(Cards(panel).Count==5,"five same-title HWNDs remain five distinct cards");
                        Check(Native.Bounds(panel.Handle).Width==panel.ClientSize.Width,"borderless client coordinates match native physical width at target DPI");
                        foreach(int width in new[]{1180,1079,800,540,269})
                        {
                            panel.ClientSize=new Size(width,650);Invoke(panel,"LayoutCards");
                            IList cards=Cards(panel);int columns=Math.Max(1,Math.Min(4,width/270));int slot=Math.Min(4,columns-1);
                            Rectangle last=Rect(cards[slot],"Bounds");
                            Check(last.Top==Rect(cards[0],"Bounds").Top && last.Right<=panel.ClientSize.Width,"five-window first-row last card stays inside width "+width);
                            for(int i=0;i<cards.Count;i++)for(int j=i+1;j<cards.Count;j++)Check(!Rect(cards[i],"Bounds").IntersectsWith(Rect(cards[j],"Bounds")),"card hit regions do not overlap at width "+width+" slots "+i+"/"+j);
                            activated.Clear();Click(panel,new Point(last.Right-1,last.Bottom-2));Pump(130);
                            Check(activated.Count==1 && activated[0]==order[slot].Handle,"single click on first-row last rightmost pixel commits its exact HWND at width "+width);
                            panel.OpenPinned();
                        }
                        panel.OpenPinned();IList five=Cards(panel);object rowEnd=five[3];Rectangle image=Rect(rowEnd,"Image");
                        IntPtr thumbnail=(IntPtr)rowEnd.GetType().GetField("Thumbnail",Members).GetValue(rowEnd);Size size;
                        Native.Rect frame;Check(FrameBounds(order[3].Handle,9,out frame,Marshal.SizeOf(typeof(Native.Rect)))==0,"own source provides physical DWM visual frame bounds");
                        Check(thumbnail!=IntPtr.Zero && Native.DwmQueryThumbnailSourceSize(thumbnail,out size)==0 && size==frame.Rectangle.Size,"first-row last DWM thumbnail dimensions belong to its exact same-title HWND");
                        Invoke(panel,"LayoutCards");
                        Check((IntPtr)rowEnd.GetType().GetField("Thumbnail",Members).GetValue(rowEnd)==thumbnail,"geometry refresh retains the same source-bound DWM thumbnail cache");
                        activated.Clear();altReleases=0;alt=true;
                        Key(panel,9,true,false);Pump(12);Key(panel,9,true,false);Pump(12);Key(panel,9,true,false);Pump(12);
                        Check((int)typeof(SwitcherForm).GetField("selected",Members).GetValue(panel)==3,"three Alt+Tab callbacks highlight first-row last of five");
                        IntPtr selected=order[3].Handle;alt=false;Key(panel,0x12,false,true);Pump(150);
                        Check(activated.Count==1 && activated[0]==selected && activated[0]!=entries[4].Handle,"visible highlighter A wins over conflicting native foreground B on Alt release");
                        Check(altReleases==0,"physical Alt release never injects a redundant Alt-up");
                        Settings.Current.MoveOnSwitch=true;
                        typeof(SwitcherForm).GetMethod("CompleteSequence",Members).Invoke(panel,new object[]{true});
                        Check(!(bool)typeof(SwitcherForm).GetField("keyboardMove",Members).GetValue(panel) && !((System.Windows.Forms.Timer)typeof(SwitcherForm).GetField("activateTimer",Members).GetValue(panel)).Enabled,"duplicate late Alt-up cannot restart foreground-based move after explicit HWND commit");
                        Settings.Current.MoveOnSwitch=false;

                        activated.Clear();panel.OpenPinned();object picked=Cards(panel)[3];Point hit=Inside(picked);Mouse(panel,"PressCard",hit);
                        Mouse(panel,"ClickCard",hit);order.Reverse();Key(panel,0x12,false,true);Pump(130);
                        Check(activated.Count==1 && activated[0]==selected,"mouse pending HWND survives foreground/order change and queued Alt-up");
                        Check(altReleases==1,"one mouse choice ends the native switcher exactly once");

                        activated.Clear();panel.OpenPinned();picked=Cards(panel)[3];hit=Inside(picked);Mouse(panel,"PressCard",hit);
                        order.Reverse();Invoke(panel,"LoadCards");Invoke(panel,"Present");Mouse(panel,"ClickCard",hit);Pump(130);
                        Check(activated.Count==0,"list reordering between down/up cancels stale card instead of activating its replacement");
                        picked=Cards(panel)[3];hit=Inside(picked);Click(panel,hit);Pump(130);
                        Check(activated.Count==1 && activated[0]==order[3].Handle,"fresh click after reorder uses new exact HWND despite duplicate titles");

                        activated.Clear();panel.OpenPinned();picked=Cards(panel)[3];hit=Inside(picked);Mouse(panel,"PressCard",hit);
                        IntPtr renamed=order[3].Handle;Form renamedForm=null;foreach(Form own in windows)if(own.Handle==renamed){renamedForm=own;break;}
                        renamedForm.Text="运行中标题改变";
                        var guard=(System.Windows.Forms.Timer)typeof(SwitcherForm).GetField("guardTimer",Members).GetValue(panel);
                        typeof(System.Windows.Forms.Timer).GetMethod("OnTick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(guard,new object[]{EventArgs.Empty});
                        Check(order[3].Title==renamedForm.Text,"guard refresh updates live title without replacing its HWND identity");
                        Mouse(panel,"ClickCard",hit);Pump(130);
                        Check(activated.Count==1 && activated[0]==renamed,"title/cache refresh between down/up still activates the same captured HWND");renamedForm.Text="相同标题";

                        activated.Clear();panel.OpenPinned();picked=Cards(panel)[3];hit=Inside(picked);Mouse(panel,"PressCard",hit);Invoke(panel,"LayoutCards");Mouse(panel,"ClickCard",hit);Pump(130);
                        Check(activated.Count==0,"layout/DPI geometry refresh invalidates in-flight mouse press");
                        panel.OpenPinned();Click(panel,new Point(1,80));Pump(130);Check(activated.Count==0,"gap click cannot select a neighboring card");

                        panel.OpenPinned();picked=Cards(panel)[3];Click(panel,Inside(picked));Key(panel,27,true,false);Pump(130);
                        Check(activated.Count==0,"Escape cancels queued selection before activation timer");
                        panel.OpenPinned();Click(panel,Inside(Cards(panel)[3]));panel.OpenPinned();Pump(130);
                        Check(activated.Count==0,"opening a new panel cancels an obsolete activation request");
                        panel.OpenPinned();Click(panel,Inside(Cards(panel)[3]));alt=false;Key(panel,9,true,false);Key(panel,0x12,false,true);Pump(130);
                        Check(activated.Count==0,"new fast native chord cancels stale mouse pending even if its Alt state is already released");

                        alt=true;shift=true;Key(panel,9,true,false);Pump(15);Check((int)typeof(SwitcherForm).GetField("selected",Members).GetValue(panel)==4,"reverse first selection resolves last HWND of five");
                        selected=order[4].Handle;alt=false;Key(panel,0x12,false,true);Pump(150);
                        Check(activated.Count==1 && activated[0]==selected,"reverse Alt release commits the displayed last HWND");
                        shift=false;activated.Clear();alt=false;Key(panel,9,true,false);Key(panel,0x12,false,true);Pump(150);
                        Check(activated.Count==0,"fast native sequence with no displayed panel is not overridden");
                    }
                }
                Check(screens>0,"secondary monitors available for physical DWM/client coordinate checks");
                CheckClosedTarget(entries);
                Console.WriteLine("PASS: "+checks+" switcher selection checks; own HWNDs only, no global input injection");return 0;
            }
            catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
            finally{foreach(Form window in windows)window.Dispose();}
        }
        private static void CheckClosedTarget(List<WindowEntry> entries)
        {
            var activated=new List<IntPtr>();
            using(var panel=new SwitcherForm(delegate{return new List<WindowEntry>(entries);},delegate(IntPtr h){activated.Add(h);return true;},delegate{},delegate(int k){return false;},delegate{return entries[0].Handle;}))
            {
                panel.OpenPinned();object last=Cards(panel)[3];Rectangle close=Rect(last,"Close");
                Click(panel,new Point(close.Left+10,close.Top+10));Pump(80);
                Check(!entries[3].Valid && entries[0].Valid && entries[4].Valid,"close at first-row last posts normal close only to that exact same-title own HWND");
                Check(activated.Count==0,"close action never also activates another card");
                panel.ActivateCard(entries[4]);Native.PostMessage(entries[4].Handle,0x10,IntPtr.Zero,IntPtr.Zero);Pump(130);
                Check(activated.Count==0,"target closed before commit never falls back to a different window");
            }
        }
        private static void CheckNativeActivation(List<WindowEntry> entries,List<Form> windows)
        {
            foreach(Screen screen in Screen.AllScreens)
            {
                if(screen.Primary)continue;
                Settings.Current=new Settings{ScreenName=screen.DeviceName,MoveOnSwitch=false,HighResponse=true};
                IntPtr submitted=IntPtr.Zero;bool nativeSuccess=false;int releases=0;
                windows[4].BringToFront();windows[4].Activate();Pump(40);
                bool baseline=Native.Activate(entries[4].Handle);Pump(30);
                Console.WriteLine("NATIVE baseline="+baseline+" foreground="+Native.GetForegroundWindow()+" expectedB="+entries[4].Handle+" class="+Native.ClassName(Native.GetForegroundWindow()));
                Check(Native.GetForegroundWindow()==entries[4].Handle,"own competing HWND B has actual native foreground before panel selection");
                using(var panel=new SwitcherForm(delegate{return new List<WindowEntry>(entries);},delegate(IntPtr h)
                {
                    Check(Native.ProcessId(h)==(uint)Process.GetCurrentProcess().Id,"native activation is restricted to this fixture's own HWND");
                    submitted=h;nativeSuccess=Native.Activate(h);
                    return true; // Report failure with an assertion, never a blocking product MessageBox.
                },delegate{releases++;},delegate(int key){return false;},Native.GetForegroundWindow))
                {
                    panel.OpenPinned();object selected=Cards(panel)[3];Click(panel,Inside(selected));Pump(150);
                    Check(submitted==entries[3].Handle && nativeSuccess && Native.GetForegroundWindow()==entries[3].Handle,"actual Native.Activate after five-window first-row last click gives foreground to exact own HWND A on "+screen.DeviceName);
                    Check(releases==1,"actual own-HWND activation test uses a stub Alt release and no global input injection");
                }
            }
        }
        private static int NativeMode()
        {
            var windows=new List<Form>();var entries=new List<WindowEntry>();
            string path=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","dist","switcher-native-activation.txt"));
            int result=1;
            try
            {
                for(int i=0;i<5;i++)
                {
                    var own=new Form{Text="Switcher own HWND "+(i+1),StartPosition=FormStartPosition.Manual,AutoScaleMode=AutoScaleMode.None,Bounds=new Rectangle(100+i*35,130+i*35,340+i*30,230+i*17)};
                    windows.Add(own);entries.Add(new WindowEntry{Handle=own.Handle,ProcessId=(uint)Process.GetCurrentProcess().Id,Title=own.Text});
                }
                using(var driver=new Form{Text="Switcher Native Activation Check",StartPosition=FormStartPosition.CenterScreen,ClientSize=new Size(760,430)})
                using(var run=new Button{Text="运行自身窗口原生激活验证",Dock=DockStyle.Top,Height=46})
                using(var output=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill,Text="等待点击按钮。此入口只运行五个自身窗口的原生激活验证。\r\n无全局键盘/鼠标输入注入。\r\n输出："+path})
                {
                    driver.Controls.Add(output);driver.Controls.Add(run);
                    run.Click+=delegate
                    {
                        run.Enabled=false;var captured=new StringWriter();TextWriter original=Console.Out;
                        try
                        {
                            Console.SetOut(captured);Console.WriteLine("START "+DateTime.Now.ToString("o")+" ownPid="+Process.GetCurrentProcess().Id+" mode=native-only globalInput=False");
                            foreach(Form own in windows)own.Show();Pump(40);checks=0;
                            CheckNativeActivation(entries,windows);
                            Console.WriteLine("PASS: "+checks+" native own-HWND checks. Simulated card callbacks; actual Native.Activate/GetForegroundWindow. No physical Alt interaction tested.");result=0;
                        }
                        catch(Exception e){Console.WriteLine("FAIL: "+e);result=1;}
                        finally
                        {
                            Console.SetOut(original);Directory.CreateDirectory(Path.GetDirectoryName(path));
                            File.WriteAllText(path,captured.ToString());output.Text=captured.ToString()+"\r\nLog: "+path;
                            driver.Text=result==0?"Switcher Native Activation Check - PASS":"Switcher Native Activation Check - FAIL";
                            driver.BringToFront();driver.Activate();captured.Dispose();
                        }
                    };
                    Application.Run(driver);
                }
                return result;
            }
            finally{foreach(Form own in windows)own.Dispose();}
        }
        private static IList Cards(SwitcherForm panel){return (IList)typeof(SwitcherForm).GetField("cards",Members).GetValue(panel);}
        private static Rectangle Rect(object card,string field){return (Rectangle)card.GetType().GetField(field,Members).GetValue(card);}
        private static Point Inside(object card){Rectangle r=Rect(card,"Image");return new Point(r.Left+r.Width/2,r.Top+r.Height/2);}
        private static void Invoke(SwitcherForm panel,string name){typeof(SwitcherForm).GetMethod(name,Members).Invoke(panel,null);}
        private static void Mouse(SwitcherForm panel,string method,Point p){typeof(SwitcherForm).GetMethod(method,Members).Invoke(panel,new object[]{panel,new MouseEventArgs(MouseButtons.Left,1,p.X,p.Y,0)});}
        private static void Click(SwitcherForm panel,Point p){Mouse(panel,"PressCard",p);Mouse(panel,"ClickCard",p);}
        private static void Key(SwitcherForm panel,uint key,bool down,bool up){typeof(SwitcherForm).GetMethod("ObserveKey",Members).Invoke(panel,new object[]{new Native.KeyboardData{Key=key,Flags=0x20},down,up});}
        private static void Pump(int milliseconds){var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(3);}}
        private static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("OK: "+message);}
    }
}
