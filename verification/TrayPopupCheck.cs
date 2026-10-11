using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenCompanion
{
    internal static class TrayPopupCheck
    {
        [STAThread] static int Main()
        {
            Application.EnableVisualStyles();
            try
            {
                int screens=0;
                foreach(Screen target in Screen.AllScreens)
                {
                    if(target.Primary)continue;screens++;
                    Settings.Current=new Settings{ScreenName=target.DeviceName,Desktop=false,AltTab=false,Tray=false};
                    Point source=new Point(Screen.PrimaryScreen.Bounds.Right-200,Screen.PrimaryScreen.Bounds.Bottom-140);
                    Point destination=new Point(target.Bounds.Right-220,target.Bounds.Bottom-80);
                    using(var owner=new Form{Text="托盘菜单定位验证",StartPosition=FormStartPosition.Manual,Location=Screen.PrimaryScreen.Bounds.Location,Size=new Size(300,120)})
                    using(var popup=new ContextMenuStrip())
                    using(var tracker=new TrayPopupTracker())
                    {
                        owner.Show();Pump(80);popup.Items.Add("托盘菜单验证条目");
                        tracker.Begin(source,destination);popup.Show(source);Pump(350);
                        Console.WriteLine("Menu="+popup.Handle+" "+Native.ClassName(popup.Handle)+" style="+Native.GetWindowLong(popup.Handle,-16).ToString("X")+" ex="+Native.GetWindowLong(popup.Handle,-20).ToString("X")+" bounds="+Native.Bounds(popup.Handle)+" moved="+tracker.LastMoved+" "+Native.ClassName(tracker.LastMoved));
                        Check(tracker.LastMoved==popup.Handle,"exact native context-menu handle relocated");
                        Check(Screen.FromHandle(popup.Handle).DeviceName==target.DeviceName,"menu reaches "+target.DeviceName);
                        Check(target.Bounds.Contains(Native.Bounds(popup.Handle)),"menu stays inside mixed-DPI target bounds");
                        popup.Close();Pump(250);Check(!popup.Visible,"closed menu is never forced open again");
                        tracker.Begin(source,destination);
                        using(var unrelated=new Form{Text="普通窗口不能被托盘捕获",StartPosition=FormStartPosition.Manual,Location=source,Size=new Size(260,120)})
                        {
                            unrelated.Show();Rectangle original=unrelated.Bounds;Pump(300);
                            Check(unrelated.Bounds==original && tracker.LastMoved==IntPtr.Zero,"unrelated normal window is not moved");
                        }
                        owner.Close();
                    }
                }
                Check(screens>0,"secondary monitor available");
                Console.WriteLine("PASS: native popup integration on "+screens+" secondary monitors");return 0;
            }
            catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
        }
        static void Pump(int milliseconds){int start=Environment.TickCount;while(Environment.TickCount-start<milliseconds){Application.DoEvents();System.Threading.Thread.Sleep(8);}}
        static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("OK: "+message);}
    }
}
