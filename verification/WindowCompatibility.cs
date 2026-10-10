using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
namespace ScreenCompanion
{
 internal static class WindowCompatibility
 {
  private static BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
  [STAThread]static int Main(string[] args)
  {
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   if(args.Length>0 && args[0]=="--samples"){Samples();return 0;}
   Process samples=null;Form panel=null;IntPtr previous=Native.GetForegroundWindow();Point previousCursor=Cursor.Position;
   try
   {
    var path=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    var assembly=Assembly.LoadFrom(Path.Combine(path,"..","release","副屏助手.exe"));
    var settings=assembly.GetType("ScreenCompanion.Settings");settings.GetField("Current",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Activator.CreateInstance(settings));
    samples=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--samples"){UseShellExecute=false,RedirectStandardOutput=true,WindowStyle=ProcessWindowStyle.Hidden});
    var handles=new Dictionary<string,IntPtr>();
    for(int i=0;i<7;i++){string line=samples.StandardOutput.ReadLine();if(line==null)throw new Exception("Sample process exited");string[] parts=line.Split('=');handles.Add(parts[0],new IntPtr(Int64.Parse(parts[1])));}
    Pump(400);
    Rectangle dialogBounds=Native.Bounds(handles["owned"]);Native.Click(new Point(dialogBounds.Left+dialogBounds.Width/2,dialogBounds.Top+dialogBounds.Height/2),MouseButtons.Left);Pump(200);
    Assert(Native.GetForegroundWindow()==handles["owned"],"owned dialog foreground established by real input");
    foreach(var pair in handles)Console.WriteLine(pair.Key+" h="+pair.Value+" ex="+Native.GetWindowLong(pair.Value,-20).ToString("X")+" owner="+Native.GetWindow(pair.Value,4)+" root="+Native.GetAncestor(pair.Value,3)+" last="+Native.GetLastActivePopup(Native.GetAncestor(pair.Value,3)));
    var type=assembly.GetType("ScreenCompanion.SwitcherForm");panel=(Form)Activator.CreateInstance(type,true);type.GetMethod("OpenPinned",fields).Invoke(panel,null);Pump(350);
    var cards=Cards(type,panel);
    Assert(cards.ContainsKey(handles["blank"]),"visible untitled normal window remains switchable");
    Assert(cards.ContainsKey(handles["owned"]),"active owned dialog included");
    Assert(!cards.ContainsKey(handles["owner"]),"owner group uses active dialog without duplicate parent");
    Assert(cards.ContainsKey(handles["hiddenOwned"]),"dialog with hidden owner included");
    Assert(!cards.ContainsKey(handles["tool"]) && !cards.ContainsKey(handles["noactivate"]),"tool and non-activating surfaces excluded");
    foreach(string name in new[]{"blank","owned","hiddenOwned"})
    {
     object card=cards[handles[name]],entry=card.GetType().GetField("Window",fields).GetValue(card);
     string title=(string)entry.GetType().GetField("Title").GetValue(entry);
     Assert(!String.IsNullOrWhiteSpace(title) && title.Contains("无标题窗口"),name+" has stable application-name fallback title");
     Assert((IntPtr)card.GetType().GetField("Thumbnail",fields).GetValue(card)!=IntPtr.Zero,name+" live DWM thumbnail registered");
     Rectangle image=(Rectangle)card.GetType().GetField("Image",fields).GetValue(card);
     Point center=panel.PointToScreen(new Point(image.Left+image.Width/2,image.Top+image.Height/2));
     using(var b=new Bitmap(1,1))using(var g=Graphics.FromImage(b))
     {
      g.CopyFromScreen(center,Point.Empty,new Size(1,1));Color actual=b.GetPixel(0,0),expected=name=="blank"?Color.CornflowerBlue:name=="owned"?Color.DarkOrange:Color.MediumSeaGreen;
      Assert(Math.Abs(actual.R-expected.R)<5 && Math.Abs(actual.G-expected.G)<5 && Math.Abs(actual.B-expected.B)<5,name+" actual thumbnail content matches its source window");
     }
    }
    using(var b=new Bitmap(panel.Width,panel.Height))using(var g=Graphics.FromImage(b)){g.CopyFromScreen(panel.Location,Point.Empty,panel.Size);b.Save(Path.Combine(path,"window-compatibility.png"));}
    ClickCard(panel,cards[handles["blank"]]);Pump(250);Assert(Native.GetForegroundWindow()==handles["blank"],"mouse activates exact untitled window");
    type.GetMethod("OpenPinned",fields).Invoke(panel,null);Pump(250);cards=Cards(type,panel);
    ClickCard(panel,cards[handles["hiddenOwned"]]);Pump(250);Assert(Native.GetForegroundWindow()==handles["hiddenOwned"],"mouse activates dialog with hidden owner");
    IntPtr studio=IntPtr.Zero;
    Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.IsWindowVisible(h) && Native.ClassName(h)=="SunAwtDialog" && String.IsNullOrWhiteSpace(Native.Title(h)))studio=h;return true;},IntPtr.Zero);
    if(studio!=IntPtr.Zero)
    {
     type.GetMethod("OpenPinned",fields).Invoke(panel,null);Pump(350);cards=Cards(type,panel);
     Assert(cards.ContainsKey(studio),"current real Java dialog no longer omitted");object card=cards[studio];
     object entry=card.GetType().GetField("Window",fields).GetValue(card);Console.WriteLine("Real Java fallback: "+entry.GetType().GetField("Title").GetValue(entry));
     Assert((IntPtr)card.GetType().GetField("Thumbnail",fields).GetValue(card)!=IntPtr.Zero,"real Java dialog DWM thumbnail registered");
     Rectangle cardBounds=(Rectangle)card.GetType().GetField("Bounds",fields).GetValue(card);
     Point location=panel.PointToScreen(cardBounds.Location);
     using(var b=new Bitmap(cardBounds.Width,cardBounds.Height))using(var g=Graphics.FromImage(b)){g.CopyFromScreen(location,Point.Empty,cardBounds.Size);b.Save(Path.Combine(path,"real-java-dialog-card.png"));}
     ClickCard(panel,card);Pump(250);Assert(Native.GetForegroundWindow()==studio,"mouse switches to actual Java warning without clicking its controls");
    }
    else Console.WriteLine("SKIP: real Java warning is no longer visible");
    return 0;
   }
   catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
   finally
   {
    if(panel!=null){panel.Close();panel.Dispose();}
    if(samples!=null && !samples.HasExited){Native.EnumWindows(delegate(IntPtr h,IntPtr p){if(Native.ProcessId(h)==samples.Id)Native.PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero);return true;},IntPtr.Zero);if(!samples.WaitForExit(1000))samples.Kill();}
    if(Native.IsWindow(previous))Native.Activate(previous);
    Native.SetCursorPos(previousCursor.X,previousCursor.Y);
   }
  }
  private static Dictionary<IntPtr,object> Cards(Type type,Form panel)
  {
   var result=new Dictionary<IntPtr,object>();foreach(object c in (IEnumerable)type.GetField("cards",fields).GetValue(panel)){object w=c.GetType().GetField("Window",fields).GetValue(c);result.Add((IntPtr)w.GetType().GetField("Handle").GetValue(w),c);}return result;
  }
  private static void ClickCard(Form panel,object card){Rectangle r=(Rectangle)card.GetType().GetField("Bounds",fields).GetValue(card);if(r.IsEmpty)throw new Exception("Test card is not visible");Native.Click(panel.PointToScreen(new Point(r.X+24,r.Y+52)),MouseButtons.Left);}
  private static void Assert(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS: "+name);}
  private static void Pump(int ms){var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(10);}}
  private sealed class SampleForm:Form
  {
   internal bool Tool,NoActivate,Popup;
   protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle&=~(0x40000|0x80|0x08000000);if(Tool)p.ExStyle|=0x80;if(NoActivate)p.ExStyle|=0x08000000;if(Popup)p.Style|=unchecked((int)0x80000000);return p;}}
  }
  private static SampleForm Sample(string title,Color color,int x,int y){return new SampleForm{Text=title,BackColor=color,ShowInTaskbar=false,Size=new Size(300,160),Location=new Point(x,y),StartPosition=FormStartPosition.Manual};}
  private static void Samples()
  {
   var context=new ApplicationContext();
   var blank=Sample("",Color.CornflowerBlue,50,50);var owner=Sample("测试所属主窗口",Color.SlateGray,370,50);var owned=Sample("",Color.DarkOrange,370,230);owned.Popup=true;owned.Owner=owner;
   var hiddenOwner=Sample("隐藏的测试宿主",Color.Black,0,0);IntPtr hiddenHandle=hiddenOwner.Handle;var hiddenOwned=Sample("",Color.MediumSeaGreen,690,50);hiddenOwned.Popup=true;hiddenOwned.Owner=hiddenOwner;
   var tool=Sample("测试工具窗口",Color.Purple,1010,50);tool.Tool=true;var noactivate=Sample("测试非激活窗口",Color.Pink,1010,230);noactivate.NoActivate=true;
   blank.FormClosed+=delegate{context.ExitThread();};blank.Show();owner.Show();owned.Show();hiddenOwned.Show();tool.Show();noactivate.Show();Native.Activate(owned.Handle);
   foreach(var pair in new[]{new KeyValuePair<string,SampleForm>("blank",blank),new KeyValuePair<string,SampleForm>("owner",owner),new KeyValuePair<string,SampleForm>("owned",owned),new KeyValuePair<string,SampleForm>("hiddenOwner",hiddenOwner),new KeyValuePair<string,SampleForm>("hiddenOwned",hiddenOwned),new KeyValuePair<string,SampleForm>("tool",tool),new KeyValuePair<string,SampleForm>("noactivate",noactivate)})Console.WriteLine(pair.Key+"="+pair.Value.Handle.ToInt64());
   Application.Run(context);
  }
 }
}
