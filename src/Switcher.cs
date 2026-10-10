using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenCompanion
{
    internal sealed class SwitcherForm : Form
    {
        private sealed class Card
        {
            public WindowEntry Window; public IntPtr Thumbnail; public Rectangle Bounds,Image,Close;
        }
        private readonly List<Card> cards=new List<Card>();
        private Native.HookProc hookProc;
        private IntPtr hook;
        private bool sequence,pinned,activating;
        private bool keyboardMove;
        private int keyboardAttempts;
        private int selected,page,pageSize=12;
        private WindowEntry pending;
        private readonly Font titleFont=new Font("Microsoft YaHei UI",14,FontStyle.Bold);
        private readonly System.Windows.Forms.Timer activateTimer=new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer guardTimer=new System.Windows.Forms.Timer();
        internal SwitcherForm()
        {
            Text="副屏窗口面板";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;
            StartPosition=FormStartPosition.Manual;TopMost=true;BackColor=Color.FromArgb(27,31,39);
            ForeColor=Color.White;Font=new Font("Microsoft YaHei UI",10);DoubleBuffered=true;
            AutoScaleMode=AutoScaleMode.None;
            activateTimer.Interval=90;activateTimer.Tick+=delegate
            {
                activateTimer.Stop();WindowEntry target=pending;pending=null;activating=false;
                if(keyboardMove)
                {
                    IntPtr foreground=Native.GetForegroundWindow();WindowEntry actual=null;
                    foreach(WindowEntry entry in WindowEntry.Read())if(entry.Handle==foreground){actual=entry;break;}
                    if(actual==null && ++keyboardAttempts<8){activateTimer.Start();return;}
                    keyboardMove=false;
                    if(actual!=null && Settings.Current.MoveOnSwitch && !Native.MoveToScreen(actual.Handle,Settings.TargetScreen()))ShowMoveFailure();
                    return;
                }
                if(target!=null && target.Valid)
                {
                    if(!Native.Activate(target.Handle))MessageBox.Show("Windows 未允许切换此窗口。若它以管理员身份运行，请使用相同权限启动副屏助手。","无法切换窗口");
                    else if(Settings.Current.MoveOnSwitch && !Native.MoveToScreen(target.Handle,Settings.TargetScreen()))ShowMoveFailure();
                }
            };
            guardTimer.Interval=100;guardTimer.Tick+=delegate
            {
                if(sequence && !pinned && !Native.KeyPressed(0x12)){sequence=false;Dismiss();}
                if(Visible) { bool changed=false;foreach(Card c in cards){string title=c.Window.Valid?c.Window.ReadTitle():"窗口已关闭";if(title!=c.Window.Title){c.Window.Title=title;changed=true;}}if(changed)Invalidate(); }
            };
            MouseUp+=ClickCard;
            MouseWheel+=delegate(object s,MouseEventArgs e){if(cards.Count>pageSize){page=Math.Max(0,Math.Min((cards.Count-1)/pageSize,page+(e.Delta<0?1:-1)));LayoutCards();}};
            FormClosing+=delegate
            {
                if(hook!=IntPtr.Zero){Native.UnhookWindowsHookEx(hook);hook=IntPtr.Zero;}
                activateTimer.Stop();guardTimer.Stop();activateTimer.Dispose();guardTimer.Dispose();ClearCards();titleFont.Dispose();
            };
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{var c=base.CreateParams;c.ExStyle|=0x80|0x08000000;return c;}}
        internal void Initialize()
        {
            IntPtr h=Handle;hookProc=Hook;hook=Native.SetWindowsHookEx(13,hookProc,Native.GetModuleHandle(null),0);
            if(hook==IntPtr.Zero)throw new InvalidOperationException("无法注册 Alt+Tab 监听。");
        }
        private IntPtr Hook(int code,IntPtr w,IntPtr l)
        {
            if(code>=0)
            {
                var d=(Native.KeyboardData)Marshal.PtrToStructure(l,typeof(Native.KeyboardData));
                bool down=w==new IntPtr(0x100)||w==new IntPtr(0x104),up=w==new IntPtr(0x101)||w==new IntPtr(0x105);
                if(down && d.Key==9 && ((d.Flags&0x20)!=0 || Native.KeyPressed(0x12)))
                {
                    bool reverse=Native.KeyPressed(0x10);bool fresh=!sequence;sequence=true;
                    Queue(delegate {if(!activating)Advance(fresh,reverse);});
                }
                else if(up && (d.Key==0x12 || d.Key==0xA4 || d.Key==0xA5))
                {bool completed=sequence;sequence=false;Queue(delegate{if(!pinned){Dismiss();if(completed && !activating && Settings.Current.MoveOnSwitch){keyboardMove=true;keyboardAttempts=0;activateTimer.Start();}}});}
                else if(down && d.Key==27){sequence=false;Queue(delegate{if(keyboardMove){keyboardMove=false;activateTimer.Stop();}Dismiss();});}
            }
            // Listening only; Windows keeps its native keyboard switching.
            return Native.CallNextHookEx(hook,code,w,l);
        }
        private void Queue(Action a){try{if(!IsDisposed)BeginInvoke(a);}catch{}}
        internal void OpenPinned(){pinned=true;sequence=false;LoadCards();selected=0;Present();}
        private void Advance(bool fresh,bool reverse)
        {
            pinned=false;
            if(fresh || cards.Count==0){LoadCards();selected=reverse?cards.Count-1:Math.Min(1,cards.Count-1);}
            else if(cards.Count>0)selected=(selected+(reverse?-1:1)+cards.Count)%cards.Count;
            if(cards.Count>0){page=selected/pageSize;Present();}
        }
        private void LoadCards()
        {
            ClearCards();foreach(WindowEntry w in WindowEntry.Read())cards.Add(new Card{Window=w});page=0;
        }
        private void Present()
        {
            Screen target=Settings.TargetScreen();if(target==null)return;
            Rectangle area=target.WorkingArea;int width=Math.Min(1180,Math.Max(220,area.Width-48));
            int columns=Math.Max(1,Math.Min(4,width/270));int rows=Math.Max(1,Math.Min(3,(area.Height-132)/205));pageSize=columns*rows;
            if(cards.Count>0)page=Math.Max(0,Math.Min((cards.Count-1)/pageSize,selected/pageSize));
            int shown=Math.Min(pageSize,Math.Max(1,cards.Count-page*pageSize));int actualRows=(shown+columns-1)/columns;
            int height=Math.Min(area.Height-24,76+actualRows*205);height=Math.Max(160,height);
            Bounds=new Rectangle(area.Left+(area.Width-width)/2,area.Top+(area.Height-height)/2,width,height);
            if(!Visible)Show();Native.SetWindowPos(Handle,new IntPtr(-1),Left,Top,Width,Height,0x10|0x40);
            LayoutCards();guardTimer.Start();Invalidate();
        }
        private void LayoutCards()
        {
            foreach(Card c in cards)if(c.Thumbnail!=IntPtr.Zero){Native.DwmUnregisterThumbnail(c.Thumbnail);c.Thumbnail=IntPtr.Zero;}
            int columns=Math.Max(1,Math.Min(4,ClientSize.Width/270));int gap=14;
            int cardWidth=(ClientSize.Width-gap*(columns+1))/columns;
            int rows=Math.Max(1,(Math.Min(pageSize,cards.Count-page*pageSize)+columns-1)/columns);
            int cardHeight=Math.Min(191,Math.Max(90,(ClientSize.Height-76)/rows-14));
            for(int i=0;i<cards.Count;i++)
            {
                Card c=cards[i];c.Bounds=Rectangle.Empty;c.Image=Rectangle.Empty;c.Close=Rectangle.Empty;
                if(i<page*pageSize || i>=(page+1)*pageSize)continue;
                int slot=i-page*pageSize;
                c.Bounds=new Rectangle(gap+(slot%columns)*(cardWidth+gap),62+(slot/columns)*(cardHeight+gap),cardWidth,cardHeight);
                c.Image=new Rectangle(c.Bounds.X+7,c.Bounds.Y+38,Math.Max(1,cardWidth-14),Math.Max(1,cardHeight-46));
                c.Close=new Rectangle(c.Bounds.Right-31,c.Bounds.Top+6,24,24);
                if(!c.Window.Valid)continue;
                IntPtr thumb;
                if(Native.DwmRegisterThumbnail(Handle,c.Window.Handle,out thumb)==0)
                {
                    c.Thumbnail=thumb;Size source;Rectangle destination=c.Image;
                    if(Native.DwmQueryThumbnailSourceSize(thumb,out source)==0 && source.Width>0 && source.Height>0)
                    {
                        double scale=Math.Min(c.Image.Width/(double)source.Width,c.Image.Height/(double)source.Height);
                        var size=new Size(Math.Max(1,(int)(source.Width*scale)),Math.Max(1,(int)(source.Height*scale)));
                        destination=new Rectangle(c.Image.X+(c.Image.Width-size.Width)/2,c.Image.Y+(c.Image.Height-size.Height)/2,size.Width,size.Height);
                    }
                    var props=new Native.Thumb{Flags=1|4|8|16,Destination=new Native.Rect(destination),Opacity=255,Visible=true,ClientOnly=false};Native.DwmUpdateThumbnailProperties(thumb,ref props);
                }
            }
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics,"副屏窗口面板",titleFont,new Rectangle(18,14,220,35),ForeColor,TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics,Settings.Current.MoveOnSwitch?"点击切换并移屏 · × 正常关闭 · 滚轮翻页":"点击切换 · × 正常关闭 · 滚轮翻页",Font,new Rectangle(230,20,Math.Max(1,Width-278),28),Color.Silver,TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics,"×",Font,new Rectangle(Width-36,18,26,26),Color.White,TextFormatFlags.HorizontalCenter);
            for(int i=0;i<cards.Count;i++)
            {
                Card c=cards[i];if(c.Bounds.IsEmpty)continue;
                using(var b=new SolidBrush(Color.FromArgb(43,48,60)))e.Graphics.FillRectangle(b,c.Bounds);
                using(var pen=new Pen(i==selected?Color.FromArgb(85,163,255):Color.FromArgb(70,78,95),i==selected?3:1))e.Graphics.DrawRectangle(pen,c.Bounds);
                TextRenderer.DrawText(e.Graphics,c.Window.Title,Font,new Rectangle(c.Bounds.X+9,c.Bounds.Y+7,Math.Max(1,c.Bounds.Width-45),26),ForeColor,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
                TextRenderer.DrawText(e.Graphics,"×",Font,c.Close,Color.FromArgb(255,135,135),TextFormatFlags.HorizontalCenter);
                if(c.Thumbnail==IntPtr.Zero)TextRenderer.DrawText(e.Graphics,c.Window.Valid?"此窗口暂不提供缩略图":"窗口已关闭",Font,c.Image,Color.Silver,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            }
            if(cards.Count==0)TextRenderer.DrawText(e.Graphics,"没有可切换的窗口",Font,ClientRectangle,Color.White,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics,"窗口 "+cards.Count+" · 第 "+(page+1)+" / "+Math.Max(1,(cards.Count+pageSize-1)/pageSize)+" 页",Font,new Rectangle(18,Height-23,Width-36,22),Color.Silver,TextFormatFlags.HorizontalCenter);
        }
        private void ClickCard(object sender,MouseEventArgs e)
        {
            if(e.Button!=MouseButtons.Left)return;
            if(new Rectangle(Width-40,10,40,40).Contains(e.Location)){pinned=false;Dismiss();return;}
            for(int i=0;i<cards.Count;i++)
            {
                Card c=cards[i];if(c.Bounds.IsEmpty || !c.Bounds.Contains(e.Location))continue;
                if(!c.Window.Valid){LoadCards();selected=Math.Min(selected,cards.Count-1);Present();return;}
                if(c.Close.Contains(e.Location))
                {
                    // Title, thumbnail, click, and close all use this same WindowEntry.
                    Native.PostMessage(c.Window.Handle,0x112,new IntPtr(0xF060),IntPtr.Zero);
                    selected=i;Invalidate();return;
                }
                ActivateCard(c.Window);return;
            }
        }
        internal void ActivateCard(WindowEntry w)
        {
            pending=w;keyboardMove=false;activating=true;pinned=false;sequence=false;Dismiss();Native.ReleaseAlt();activateTimer.Start();
        }
        private static void ShowMoveFailure(){MessageBox.Show("此窗口未能移到副屏。请确认副屏仍已连接；管理员窗口需要以相同权限运行副屏助手。","无法移动窗口");}
        private void Dismiss(){guardTimer.Stop();Hide();ClearCards();}
        private void ClearCards(){foreach(Card c in cards)if(c.Thumbnail!=IntPtr.Zero)Native.DwmUnregisterThumbnail(c.Thumbnail);cards.Clear();}
    }
}
