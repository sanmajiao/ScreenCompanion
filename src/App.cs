using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenCompanion
{
    internal sealed class Settings
    {
        public string ScreenName="";
        public bool Desktop=true, AltTab=true, Tray=true;
        public bool MoveOnSwitch=false,TransparentTray=true;
        public int ImageQuality=1;
        public int TrayWidth=520;
        public static readonly string DataDirectory=GetDataDirectory();
        public static readonly string FileName=Path.Combine(DataDirectory,"settings.ini");
        private static string GetDataDirectory()
        {
            string p=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data");
            try { Directory.CreateDirectory(p); string probe=Path.Combine(p,".write-"+Guid.NewGuid().ToString("N")); File.WriteAllText(probe,""); File.Delete(probe); return p; }
            catch { p=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenCompanion"); Directory.CreateDirectory(p); return p; }
        }
        public static Settings Load()
        {
            var s=new Settings();
            try { foreach(string line in File.ReadAllLines(FileName,Encoding.UTF8)) { int pos=line.IndexOf('='); if(pos<0)continue; string k=line.Substring(0,pos),v=line.Substring(pos+1); switch(k) { case "screen":s.ScreenName=v;break;case "desktop":s.Desktop=v!="0";break;case "alttab":s.AltTab=v!="0";break;case "tray":s.Tray=v!="0";break;case "moveOnSwitch":s.MoveOnSwitch=v=="1";break;case "transparentTray":s.TransparentTray=v!="0";break;case "imageQuality":int q;if(Int32.TryParse(v,out q))s.ImageQuality=Math.Max(0,Math.Min(2,q));break;case "trayWidth":int n;if(Int32.TryParse(v,out n))s.TrayWidth=Math.Max(220,Math.Min(1200,n));break; } } } catch { }
            return s;
        }
        public void Save() { File.WriteAllLines(FileName,new[]{"screen="+ScreenName,"desktop="+(Desktop?1:0),"alttab="+(AltTab?1:0),"tray="+(Tray?1:0),"trayWidth="+TrayWidth,"moveOnSwitch="+(MoveOnSwitch?1:0),"transparentTray="+(TransparentTray?1:0),"imageQuality="+ImageQuality},Encoding.UTF8); }
        internal static Settings Current;
        internal static Screen TargetScreen()
        {
            var all=Screen.AllScreens;
            if(Current!=null) foreach(Screen s in all) if(s.DeviceName==Current.ScreenName && !s.Primary) return s;
            Screen best=null; foreach(Screen s in all) if(!s.Primary && (best==null || s.Bounds.Left>best.Bounds.Left))best=s;
            return best;
        }
        internal static string LayoutFile()
        {
            Screen s=TargetScreen(); string name=s==null?"none":s.DeviceName;
            return Path.Combine(DataDirectory,"layout-"+name.Replace("\\","").Replace(".","")+".ini");
        }
    }
    internal static class Program
    {
        [STAThread] public static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e) { Log(e.Exception); };
            string identity=WindowsIdentity.GetCurrent().User.Value;
            bool created;
            using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\ScreenCompanion.Show."+identity))
            using(var quit=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\ScreenCompanion.Quit."+identity))
            using(var mutex=new Mutex(true,"Local\\ScreenCompanion.Instance."+identity,out created))
            {
                if(!created) { if(Array.IndexOf(args,"--quit")>=0)quit.Set();else signal.Set(); return; }
                if(Array.IndexOf(args,"--quit")>=0) { mutex.ReleaseMutex();return; }
                try { Settings.Current=Settings.Load(); Application.Run(new CompanionContext(signal,quit,Array.IndexOf(args,"--background")<0)); }
                catch(Exception e) { Log(e); MessageBox.Show("启动失败："+e.Message+"\n诊断记录位于："+Settings.DataDirectory,"副屏助手"); }
                finally { mutex.ReleaseMutex(); }
            }
        }
        internal static void Log(Exception e) { try { File.AppendAllText(Path.Combine(Settings.DataDirectory,"errors.log"),DateTime.Now.ToString("s")+" "+e+Environment.NewLine); } catch { } }
    }
    internal sealed class CompanionContext : ApplicationContext
    {
        private readonly Control dispatcher=new Control();
        private readonly NotifyIcon tray=new NotifyIcon();
        private RegisteredWaitHandle signalWait;
        private RegisteredWaitHandle quitWait;
        private DesktopPortal.DesktopLayerForm desktop;
        private SwitcherForm switcher;
        private TrayMirrorForm trayMirror;
        private SettingsForm settings;
        private readonly System.Windows.Forms.Timer topologyTimer=new System.Windows.Forms.Timer();
        private string topology="";
        internal CompanionContext(EventWaitHandle signal,EventWaitHandle quit,bool show)
        {
            dispatcher.CreateControl(); IntPtr handle=dispatcher.Handle;
            var menu=new ContextMenuStrip();
            menu.Items.Add("打开设置",null,delegate { ShowSettings(); });
            menu.Items.Add("刷新桌面 / 重新检测屏幕",null,delegate { Apply(); });
            menu.Items.Add("显示窗口面板",null,delegate { if(switcher!=null)switcher.OpenPinned(); });
            menu.Items.Add("打开真实桌面",null,delegate { Process.Start(new ProcessStartInfo(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)){UseShellExecute=true}); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出副屏助手",null,delegate { ExitThread(); });
            tray.Icon=AppIcon.Create(); tray.Text="副屏助手 · 桌面 / Alt+Tab / 托盘";tray.ContextMenuStrip=menu;tray.Visible=true;
            tray.DoubleClick+=delegate { ShowSettings(); };
            signalWait=ThreadPool.RegisterWaitForSingleObject(signal,delegate { Dispatch(ShowSettings); },null,Timeout.Infinite,false);
            quitWait=ThreadPool.RegisterWaitForSingleObject(quit,delegate { Dispatch(ExitThread); },null,Timeout.Infinite,false);
            SystemEvents.DisplaySettingsChanged+=DisplayChanged;
            // Low-rate topology check also recovers after Explorer restarts.
            topologyTimer.Interval=3000;topologyTimer.Tick+=delegate { string t=Topology();if(t!=topology)Apply(); };topologyTimer.Start();
            Apply(); if(show)ShowSettings();
        }
        private void Dispatch(Action a) { try { if(!dispatcher.IsDisposed)dispatcher.BeginInvoke(a); } catch { } }
        private void DisplayChanged(object sender,EventArgs e) { Dispatch(Apply); }
        private string Topology()
        {
            var b=new StringBuilder();foreach(Screen s in Screen.AllScreens)b.Append(s.DeviceName).Append(s.Bounds).Append(s.WorkingArea);
            b.Append(Native.FindWindow("Progman",null));return b.ToString();
        }
        internal void Apply()
        {
            CloseFeatures(); topology=Topology();
            if(Settings.TargetScreen()!=null)
            {
                if(Settings.Current.Desktop) { desktop=new DesktopPortal.DesktopLayerForm();desktop.Show(); }
                if(Settings.Current.AltTab) { switcher=new SwitcherForm();switcher.Initialize(); }
                if(Settings.Current.Tray) { trayMirror=new TrayMirrorForm(ShowSettings);trayMirror.Show(); }
            }
            if(settings!=null && !settings.IsDisposed)settings.RefreshStatus();
        }
        private void CloseFeatures()
        {
            if(desktop!=null){desktop.Close();desktop.Dispose();desktop=null;}
            if(switcher!=null){switcher.Close();switcher.Dispose();switcher=null;}
            if(trayMirror!=null){trayMirror.Close();trayMirror.Dispose();trayMirror=null;}
        }
        private void ShowSettings()
        {
            if(settings==null || settings.IsDisposed)settings=new SettingsForm(this);
            settings.Show();if(settings.WindowState==FormWindowState.Minimized)settings.WindowState=FormWindowState.Normal;settings.Activate();
        }
        protected override void ExitThreadCore()
        {
            SystemEvents.DisplaySettingsChanged-=DisplayChanged;
            topologyTimer.Stop();topologyTimer.Dispose();if(signalWait!=null)signalWait.Unregister(null);
            if(quitWait!=null)quitWait.Unregister(null);
            CloseFeatures();if(settings!=null)settings.Dispose();tray.Visible=false;tray.Icon.Dispose();tray.Dispose();dispatcher.Dispose();base.ExitThreadCore();
        }
    }
    internal static class AppIcon
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
        internal static Icon Create()
        {
            using(var b=new Bitmap(32,32))using(var g=Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);using(var brush=new SolidBrush(Color.FromArgb(42,115,205)))g.FillRectangle(brush,1,5,19,18);
                using(var brush=new SolidBrush(Color.FromArgb(72,179,147)))g.FillRectangle(brush,21,10,10,14);
                using(var pen=new Pen(Color.White,2)){g.DrawRectangle(pen,2,6,17,16);g.DrawLine(pen,8,27,15,27);g.DrawLine(pen,11,23,11,27);}
                IntPtr h=b.GetHicon();try{return (Icon)Icon.FromHandle(h).Clone();}finally{DestroyIcon(h);}
            }
        }
    }
    internal sealed class SettingsForm : Form
    {
        private readonly CompanionContext context;
        private readonly ComboBox screens=new ComboBox();
        private readonly CheckBox desktop=new CheckBox(),alt=new CheckBox(),tray=new CheckBox(),move=new CheckBox(),transparent=new CheckBox();
        private readonly ComboBox quality=new ComboBox();
        private readonly ToolTip help=new ToolTip();
        private readonly NumericUpDown width=new NumericUpDown();
        private readonly Label status=new Label();
        internal SettingsForm(CompanionContext ctx)
        {
            context=ctx;Text="副屏助手";Icon=AppIcon.Create();Font=new Font("Microsoft YaHei UI",10);
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(680,620);MinimumSize=new Size(560,420);StartPosition=FormStartPosition.CenterScreen;AutoScroll=true;
            BackColor=Color.FromArgb(246,248,252);
            var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Padding=new Padding(24),GrowStyle=TableLayoutPanelGrowStyle.AddRows};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(layout);
            AddRow(layout,new Label{Text="一个入口，管理你的副屏",Font=new Font(Font.FontFamily,19,FontStyle.Bold),AutoSize=true});
            AddRow(layout,new Label{Text="桌面克隆 · Alt+Tab 窗口面板 · 真实系统托盘",AutoSize=true,ForeColor=Color.DimGray});
            AddRow(layout,new Label{Text="目标副屏",AutoSize=true});screens.DropDownStyle=ComboBoxStyle.DropDownList;screens.Dock=DockStyle.Fill;AddRow(layout,screens);
            desktop.Text="桌面克隆：打开文件，拖动图标保存布局";AddRow(layout,desktop);
            alt.Text="Alt+Tab：实时预览，点击切换或正常关闭";AddRow(layout,alt);
            move.Text="切换时将选中的窗口直接移到目标副屏";AddRow(layout,move);help.SetToolTip(move,"勾选后，点击卡片或按 Alt+Tab 后松开 Alt，会把实际选中的窗口移到上方选择的副屏。\n保留最大化状态；取消勾选后只切换窗口。");
            tray.Text="托盘镜像：图标、网络、音量、电池与时钟";AddRow(layout,tray);
            transparent.Text="托盘背景透明，使用副屏自己的背景";AddRow(layout,transparent);
            help.SetToolTip(transparent,"从截图中分离背景，图标叠加到副屏任务栏；时钟使用副屏原生时钟，避免重影。\n复杂背景可能残留边缘；取消勾选可恢复完整原始镜像。");
            var images=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};images.Controls.Add(new Label{Text="图标 / 图片清晰度",AutoSize=true,Margin=new Padding(0,7,12,0)});
            quality.DropDownStyle=ComboBoxStyle.DropDownList;quality.Width=290;quality.Items.AddRange(new object[]{"低 · 32 像素 · 约 0.4 MiB / 100 项","中 · 64 像素 · 约 1.6 MiB / 100 项","高 · 128 像素 · 约 6.3 MiB / 100 项"});images.Controls.Add(quality);
            var question=new Label{Text="？",AutoSize=true,Cursor=Cursors.Help,ForeColor=Color.FromArgb(42,115,205),Margin=new Padding(8,7,0,0)};images.Controls.Add(question);AddRow(layout,images);
            help.SetToolTip(question,"每 100 张缓存图像的理论占用：\n低 32×32：约 0.4 MiB\n中 64×64：约 1.6 MiB\n高 128×128：约 6.3 MiB\n这是图像缓存，不是程序总内存；还会有界面、绘制和生成缩略图时的临时占用。\n照片预览在后台生成，桌面图标位置和大小保持不变。");
            var sizes=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};sizes.Controls.Add(new Label{Text="托盘最大宽度",AutoSize=true,Margin=new Padding(0,7,12,0)});width.Width=110;width.Minimum=220;width.Maximum=1200;width.Increment=20;sizes.Controls.Add(width);sizes.Controls.Add(new Label{Text="像素；保持原比例",AutoSize=true,Margin=new Padding(12,7,0,0),ForeColor=Color.DimGray});AddRow(layout,sizes);
            status.AutoSize=true;status.Dock=DockStyle.Fill;status.ForeColor=Color.DimGray;AddRow(layout,status);
            var buttons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};AddRow(layout,buttons);
            var apply=new Button{Text="应用设置",AutoSize=true,MinimumSize=new Size(120,38),BackColor=Color.FromArgb(42,115,205),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};apply.FlatAppearance.BorderSize=0;buttons.Controls.Add(apply);
            apply.Click+=delegate
            {
                if(screens.SelectedItem is ScreenChoice)Settings.Current.ScreenName=((ScreenChoice)screens.SelectedItem).Device;
                Settings.Current.Desktop=desktop.Checked;Settings.Current.AltTab=alt.Checked;Settings.Current.Tray=tray.Checked;Settings.Current.TrayWidth=(int)width.Value;
                Settings.Current.MoveOnSwitch=move.Checked;Settings.Current.TransparentTray=transparent.Checked;Settings.Current.ImageQuality=Math.Max(0,quality.SelectedIndex);
                try { Settings.Current.Save();context.Apply();status.Text="设置已保存。关闭这个窗口后，副屏功能继续运行。"; }
                catch(Exception e) { MessageBox.Show("设置未能保存："+e.Message,"副屏助手"); }
            };
            var hide=new Button{Text="收起到托盘",AutoSize=true,MinimumSize=new Size(120,38)};hide.Click+=delegate{Hide();};buttons.Controls.Add(hide);
            var exit=new Button{Text="退出",AutoSize=true,MinimumSize=new Size(90,38)};exit.Click+=delegate{context.ExitThread();};buttons.Controls.Add(exit);
            desktop.Checked=Settings.Current.Desktop;alt.Checked=Settings.Current.AltTab;tray.Checked=Settings.Current.Tray;width.Value=Settings.Current.TrayWidth;
            move.Checked=Settings.Current.MoveOnSwitch;transparent.Checked=Settings.Current.TransparentTray;quality.SelectedIndex=Settings.Current.ImageQuality;
            Shown+=delegate{Rectangle area=Screen.FromControl(this).WorkingArea;Size=new Size(Math.Min(Width,area.Width-24),Math.Min(Height,area.Height-24));};
            Disposed+=delegate{help.Dispose();};
            FormClosing+=delegate(object s,FormClosingEventArgs e){if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
            RefreshStatus();
        }
        private static void AddRow(TableLayoutPanel panel,Control control){int row=panel.RowCount++;panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));control.Margin=new Padding(0,6,0,6);if(control is CheckBox){control.AutoSize=true;control.Dock=DockStyle.Fill;}panel.Controls.Add(control,0,row);}
        internal void RefreshStatus()
        {
            screens.Items.Clear();int index=0,selected=-1;Screen target=Settings.TargetScreen();
            foreach(Screen s in Screen.AllScreens)if(!s.Primary){screens.Items.Add(new ScreenChoice{s=s,Device=s.DeviceName});if(target!=null && s.DeviceName==target.DeviceName)selected=index;index++;}
            if(selected>=0)screens.SelectedIndex=selected;
            status.Text=target==null?"未检测到扩展副屏，功能已暂停。连接副屏后会自动恢复。":"正在使用 "+target.DeviceName+"。右键副屏托盘的小齿轮可打开设置。";
        }
        private sealed class ScreenChoice
        {
            public Screen s;public string Device;
            public override string ToString(){return Device+"  ·  "+s.Bounds.Width+" × "+s.Bounds.Height+"  ·  位置 "+s.Bounds.Left+", "+s.Bounds.Top;}
        }
    }
}
