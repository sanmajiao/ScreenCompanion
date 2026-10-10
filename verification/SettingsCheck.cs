using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
namespace ScreenCompanion
{
 internal static class SettingsCheck
 {
  [STAThread]static void Main()
  {
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   var a=Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","release","副屏助手.exe"));
   var s=a.GetType("ScreenCompanion.Settings");object config=Activator.CreateInstance(s);s.GetField("Current",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,config);
   var signal=new EventWaitHandle(false,EventResetMode.AutoReset);var quit=new EventWaitHandle(false,EventResetMode.AutoReset);
   var t=a.GetType("ScreenCompanion.CompanionContext");var flags=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance;
   var ctx=(ApplicationContext)Activator.CreateInstance(t,flags,null,new object[]{signal,quit,true},null);
   for(int i=0;i<70;i++){Application.DoEvents();Thread.Sleep(10);}
   var f=(Form)t.GetField("settings",flags).GetValue(ctx);
   Rectangle r=Native.Bounds(f.Handle);Console.WriteLine("Settings UI "+r);
   using(var b=new Bitmap(r.Width,r.Height))using(var g=Graphics.FromImage(b)){IntPtr dc=g.GetHdc();bool ok=Native.PrintWindow(f.Handle,dc,2);g.ReleaseHdc(dc);if(!ok)throw new Exception("Settings window render capture failed.");b.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings-ui.png"));}
   Button apply=FindApply(f);if(apply==null)throw new Exception("Apply button missing.");apply.PerformClick();
   for(int i=0;i<70;i++){Application.DoEvents();Thread.Sleep(10);}
   string path=(string)s.GetField("FileName",BindingFlags.Public|BindingFlags.Static).GetValue(null);
   if(!File.Exists(path))throw new Exception("Portable settings did not save.");
   Console.WriteLine("PASS: integrated features start; settings apply and save beside executable: "+path);
   ctx.ExitThread();signal.Dispose();quit.Dispose();
  }
  static Button FindApply(Control parent){foreach(Control c in parent.Controls){if(c is Button && c.Text=="应用设置")return (Button)c;Button found=FindApply(c);if(found!=null)return found;}return null;}
 }
}
