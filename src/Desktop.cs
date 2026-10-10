using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DesktopPortal
{
    internal sealed class DesktopLayerForm : Form
    {
        private static readonly Color TransparentColor = Color.FromArgb(1, 2, 3);
        private readonly string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        private readonly DesktopCanvas canvas = new DesktopCanvas();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer directoryHeartbeat = new System.Windows.Forms.Timer();
        private readonly ContextMenuStrip launchMenu = new ContextMenuStrip();
        private DateTime desktopDirectoryStamp;
        private FileSystemWatcher watcher;
        private FileSystemWatcher commonWatcher;
        private LowLevelMouseProc mouseProc;
        private IntPtr mouseHook = IntPtr.Zero;

        public DesktopLayerForm()
        {
            Text = "副屏桌面克隆";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // A near-black color key prevents the bright magenta fringe produced
            // when icons and anti-aliased text are composited before color-keying.
            BackColor = TransparentColor;
            TransparencyKey = TransparentColor;
            TopMost = false;

            canvas.Dock = DockStyle.Fill;
            canvas.BackColor = TransparentColor;
            canvas.DesktopPath = desktopPath;
            canvas.LayoutPath = ScreenCompanion.Settings.LayoutFile();
            canvas.InteractiveRegionChanged += delegate { ApplyInteractiveRegion(); };
            Controls.Add(canvas);

            refreshTimer.Interval = 350;
            refreshTimer.Tick += delegate { refreshTimer.Stop(); RefreshDesktopItems(); };
            directoryHeartbeat.Interval = 2000;
            directoryHeartbeat.Tick += delegate { CheckDesktopDirectory(); };

            Shown += delegate
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    AttachAndPlace();
                    LoadDesktopItems();
                    StartWatcher();
                    desktopDirectoryStamp = GetDesktopDirectoryStamp();
                    directoryHeartbeat.Start();
                    StartMouseHook();
                });
            };
            FormClosing += delegate
            {
                if (watcher != null) watcher.Dispose();
                if (commonWatcher != null) commonWatcher.Dispose();
                refreshTimer.Dispose();
                directoryHeartbeat.Dispose();
                directoryHeartbeat.Stop();
                if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
                launchMenu.Dispose();
                canvas.DisposeItems();
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        private void StartWatcher()
        {
            if (watcher != null) watcher.Dispose();
            watcher = new FileSystemWatcher(desktopPath);
            watcher.IncludeSubdirectories = false;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.Created += QueueRefresh;
            watcher.Deleted += QueueRefresh;
            watcher.Renamed += QueueRefresh;
            watcher.Changed += QueueRefresh;
            watcher.Error += WatcherError;
            watcher.EnableRaisingEvents = true;
            if (commonWatcher != null) commonWatcher.Dispose();
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (Directory.Exists(common) && common != desktopPath)
            {
                commonWatcher = new FileSystemWatcher(common);
                commonWatcher.NotifyFilter = watcher.NotifyFilter;
                commonWatcher.Created += QueueRefresh; commonWatcher.Deleted += QueueRefresh;
                commonWatcher.Renamed += QueueRefresh; commonWatcher.Changed += QueueRefresh;
                commonWatcher.Error += WatcherError; commonWatcher.EnableRaisingEvents = true;
            }
        }

        private void WatcherError(object sender, ErrorEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    StartWatcher();
                    RefreshDesktopItems();
                });
            }
            catch { }
        }

        private void QueueRefresh(object sender, FileSystemEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { refreshTimer.Stop(); refreshTimer.Start(); }); }
            catch { }
        }

        private void LoadDesktopItems()
        {
            Screen right = FindRightScreen();
            if (right == null) return;
            canvas.SetNativeDesktopIcons(NativeDesktopIconReader.Read(right.Bounds), right.Bounds.Location);
            canvas.LoadItems();
            ApplyInteractiveRegion();
        }

        private void ApplyInteractiveRegion()
        {
            if (!IsHandleCreated) return;
            Region next = canvas.BuildInteractiveRegion();
            Region old = Region;
            Region = next;
            if (old != null) old.Dispose();
        }

        private void RefreshDesktopItems()
        {
            LoadDesktopItems();
            desktopDirectoryStamp = GetDesktopDirectoryStamp();
        }

        private DateTime GetDesktopDirectoryStamp()
        {
            try { return Directory.GetLastWriteTimeUtc(desktopPath); }
            catch { return DateTime.MinValue; }
        }

        private void CheckDesktopDirectory()
        {
            DateTime stamp = GetDesktopDirectoryStamp();
            if (stamp != desktopDirectoryStamp) RefreshDesktopItems();
        }

        private void StartMouseHook()
        {
            mouseProc = MouseHookCallback;
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(null), 0);
        }

        private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && wParam == new IntPtr(WM_LBUTTONDOWN))
            {
                MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                IntPtr target = WindowFromPoint(data.Point);
                Point screenPoint = new Point(data.Point.X, data.Point.Y);
                bool belongsToLayer = target == Handle || IsChild(Handle, target);
                if (!belongsToLayer || !canvas.IsItemAtScreenPoint(screenPoint))
                {
                    try { BeginInvoke((MethodInvoker)delegate { canvas.ClearSelection(); }); }
                    catch { }
                }
            }
            return CallNextHookEx(mouseHook, code, wParam, lParam);
        }

        private void AttachAndPlace()
        {
            Screen right = FindRightScreen();
            if (right == null) return;
            IntPtr host = FindDesktopHost();
            if (host != IntPtr.Zero)
            {
                const int GWL_STYLE = -16;
                const int WS_CHILD = 0x40000000;
                const int WS_POPUP = unchecked((int)0x80000000);
                int style = GetWindowLong(Handle, GWL_STYLE);
                SetWindowLong(Handle, GWL_STYLE, (style | WS_CHILD) & ~WS_POPUP);
                SetParent(Handle, host);
                POINT point = new POINT { X = right.Bounds.Left, Y = right.Bounds.Top };
                ScreenToClient(host, ref point);
                SetWindowPos(Handle, IntPtr.Zero, point.X, point.Y, right.Bounds.Width, right.Bounds.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            else
            {
                SetParent(Handle, IntPtr.Zero);
                SetWindowPos(Handle, new IntPtr(1), right.Bounds.Left, right.Bounds.Top, right.Bounds.Width, right.Bounds.Height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            ApplyInteractiveRegion();
        }

        private static Screen FindRightScreen() { return ScreenCompanion.Settings.TargetScreen(); }

        private static IntPtr FindDesktopHost()
        {
            IntPtr progman = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                var className = new StringBuilder(64);
                GetClassName(hWnd, className, className.Capacity);
                if (String.Equals(className.ToString(), "Progman", StringComparison.Ordinal))
                {
                    progman = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            // Windows 11 on this machine keeps the wallpaper and desktop icon view
            // inside one virtual-screen Progman window. Parenting directly to it
            // places this layer above the wallpaper and below every normal app.
            return progman;
        }

        private static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        private const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201;
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maximum);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int hook, LowLevelMouseProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }

    internal sealed class DesktopCanvas : Control
    {
        private const int CellWidth = 108, CellHeight = 102, MarginX = 14, MarginY = 18;
        private readonly List<DesktopItem> items = new List<DesktopItem>();
        private readonly List<Rectangle> reservedNativeBounds = new List<Rectangle>();
        private readonly HashSet<string> nativeRightScreenNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        private readonly Dictionary<string, Point> savedPositions = new Dictionary<string, Point>(StringComparer.CurrentCultureIgnoreCase);
        private readonly Font itemFont = new Font("Microsoft YaHei UI", 9F);
        private readonly StringFormat textFormat = new StringFormat();
        private readonly BlockingCollection<Action> imageJobs = new BlockingCollection<Action>();
        private volatile int imageGeneration;
        private DesktopItem hovered, selected;
        private DesktopItem dragItem;
        private Point dragStart, dragOrigin;
        private bool dragging, suppressDoubleClick;
        private string contextItemPath;
        private readonly ContextMenuStrip itemMenu = new ContextMenuStrip();
        public string DesktopPath { get; set; }
        public string LayoutPath { get; set; }
        public event EventHandler InteractiveRegionChanged;

        public DesktopCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            textFormat.Alignment = StringAlignment.Center;
            textFormat.LineAlignment = StringAlignment.Near;
            textFormat.Trimming = StringTrimming.EllipsisCharacter;
            textFormat.FormatFlags = StringFormatFlags.LineLimit;
            var open = new ToolStripMenuItem("打开");
            var locate = new ToolStripMenuItem("在资源管理器中选中");
            open.Click += delegate { if (!String.IsNullOrEmpty(contextItemPath)) OpenPath(contextItemPath); };
            locate.Click += delegate { if (!String.IsNullOrEmpty(contextItemPath)) LocatePath(contextItemPath); };
            itemMenu.Items.Add(open);
            itemMenu.Items.Add(locate);
            var imageThread = new Thread(delegate()
            {
                try { foreach (Action job in imageJobs.GetConsumingEnumerable()) job(); }
                finally { imageJobs.Dispose(); }
            });
            imageThread.SetApartmentState(ApartmentState.STA);
            imageThread.IsBackground = true;
            imageThread.Name = "ScreenCompanion desktop thumbnails";
            imageThread.Start();
        }

        public void SetNativeDesktopIcons(List<NativeDesktopIcon> nativeIcons, Point screenOrigin)
        {
            nativeRightScreenNames.Clear();
            reservedNativeBounds.Clear();
            foreach (NativeDesktopIcon icon in nativeIcons)
            {
                nativeRightScreenNames.Add(NormalizeDesktopName(icon.Name));
                int x = icon.ScreenPosition.X - screenOrigin.X;
                int y = icon.ScreenPosition.Y - screenOrigin.Y;
                reservedNativeBounds.Add(new Rectangle(x - 4, y - 2, 92, 98));
            }
        }

        public void LoadItems()
        {
            DisposeItems();
            if (String.IsNullOrEmpty(DesktopPath)) return;
            LoadSavedPositions();
            var paths = new List<string>();
            try
            {
                paths.AddRange(Directory.GetDirectories(DesktopPath));
                paths.AddRange(Directory.GetFiles(DesktopPath));
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (Directory.Exists(common) && !String.Equals(common, DesktopPath, StringComparison.OrdinalIgnoreCase))
                {
                    paths.AddRange(Directory.GetDirectories(common));
                    paths.AddRange(Directory.GetFiles(common));
                }
                paths.Sort(ComparePaths);
                foreach (string path in paths)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); } catch { continue; }
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (nativeRightScreenNames.Contains(NormalizeDesktopName(Path.GetFileName(path)))) continue;
                    var item = new DesktopItem { Path = path, Name = Path.GetFileName(path) };
                    items.Add(item);
                    QueueImage(item, imageGeneration, 32 << ScreenCompanion.Settings.Current.ImageQuality);
                }
                LayoutItems();
                Invalidate();
                RaiseInteractiveRegionChanged();
            }
            catch { }
        }

        public void DisposeItems()
        {
            imageGeneration++;
            foreach (DesktopItem item in items) if (item.Image != null) item.Image.Dispose();
            items.Clear(); hovered = null; selected = null;
        }

        private void QueueImage(DesktopItem item, int generation, int pixels)
        {
            imageJobs.Add(delegate
            {
                if (generation != imageGeneration) return;
                Bitmap image = ShellImage.Get(item.Path, pixels);
                if (image == null) return;
                if (generation != imageGeneration || IsDisposed || !IsHandleCreated) { image.Dispose(); return; }
                try { BeginInvoke((MethodInvoker)delegate
                {
                    if (generation != imageGeneration || IsDisposed) { image.Dispose(); return; }
                    if (item.Image != null) item.Image.Dispose();
                    item.Image = image;
                    Invalidate(item.Bounds);
                }); }
                catch { image.Dispose(); }
            });
        }

        internal long ImageCacheBytes { get { long bytes = 0; foreach (DesktopItem item in items) if (item.Image != null) bytes += (long)item.Image.Width * item.Image.Height * 4; return bytes; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposeItems(); imageJobs.CompleteAdding(); itemMenu.Dispose(); itemFont.Dispose(); textFormat.Dispose(); }
            base.Dispose(disposing);
        }

        public void ClearSelection()
        {
            if (selected == null) return;
            selected = null;
            Invalidate();
        }

        public bool IsItemAtScreenPoint(Point screenPoint)
        {
            return HitTest(PointToClient(screenPoint)) != null;
        }

        public Region BuildInteractiveRegion()
        {
            var region = new Region(Rectangle.Empty);
            foreach (DesktopItem item in items) region.Union(item.Bounds);
            return region;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutItems();
            RaiseInteractiveRegionChanged();
        }

        private void LayoutItems()
        {
            if (Width <= 0 || Height <= 0) return;
            var occupied = new List<Rectangle>(reservedNativeBounds);
            foreach (DesktopItem reset in items) reset.Bounds = Rectangle.Empty;

            foreach (DesktopItem item in items)
            {
                Point saved;
                if (!savedPositions.TryGetValue(item.Name, out saved)) continue;
                Rectangle candidate = CreateCellBounds(saved.X, saved.Y);
                if (IsInsideCanvas(candidate) && !IntersectsAny(candidate, occupied))
                {
                    item.Bounds = candidate;
                    occupied.Add(candidate);
                }
            }

            int rows = Math.Max(1, (Height - MarginY * 2) / CellHeight);
            foreach (DesktopItem item in items)
            {
                if (!item.Bounds.IsEmpty) continue;
                for (int slot = 0; slot < rows * Math.Max(1, Width / CellWidth); slot++)
                {
                    int column = slot / rows, row = slot % rows;
                    Rectangle candidate = CreateCellBounds(MarginX + column * CellWidth, MarginY + row * CellHeight);
                    if (!IsInsideCanvas(candidate) || IntersectsAny(candidate, occupied)) continue;
                    item.Bounds = candidate;
                    occupied.Add(candidate);
                    break;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // ClearType uses colored RGB subpixels and creates colored halos on a
            // transparent desktop layer. Grayscale anti-aliasing stays neutral.
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            foreach (DesktopItem item in items)
            {
                Rectangle r = item.Bounds;
                if (r.IsEmpty) continue;
                if (item == selected || item == hovered)
                {
                    Color fill = item == selected ? Color.FromArgb(105, 50, 125, 210) : Color.FromArgb(65, 255, 255, 255);
                    using (var brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, r);
                    using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255))) e.Graphics.DrawRectangle(pen, r);
                }
                Rectangle iconRect = new Rectangle(r.X + (r.Width - 48) / 2, r.Y + 5, 48, 48);
                if (item.Image != null)
                {
                    float scale = Math.Min(iconRect.Width / (float)item.Image.Width, iconRect.Height / (float)item.Image.Height);
                    int w = (int)(item.Image.Width * scale), h = (int)(item.Image.Height * scale);
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(item.Image, new Rectangle(iconRect.X + (iconRect.Width-w)/2, iconRect.Y + (iconRect.Height-h)/2, w, h));
                }
                Rectangle textRect = new Rectangle(r.X + 2, r.Y + 57, r.Width - 4, 36), shadowRect = textRect;
                shadowRect.Offset(1, 1);
                using (var shadow = new SolidBrush(Color.FromArgb(230, 0, 0, 0))) e.Graphics.DrawString(item.Name, itemFont, shadow, shadowRect, textFormat);
                using (var white = new SolidBrush(Color.White)) e.Graphics.DrawString(item.Name, itemFont, white, textRect, textFormat);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragItem != null && (e.Button & MouseButtons.Left) != 0)
            {
                int dx = e.X - dragStart.X, dy = e.Y - dragStart.Y;
                if (!dragging && (Math.Abs(dx) >= SystemInformation.DragSize.Width / 2 ||
                                  Math.Abs(dy) >= SystemInformation.DragSize.Height / 2))
                    dragging = true;
                if (dragging)
                {
                    Rectangle old = dragItem.Bounds;
                    int x = Math.Max(0, Math.Min(Width - old.Width, dragOrigin.X + dx));
                    int y = Math.Max(0, Math.Min(Height - old.Height, dragOrigin.Y + dy));
                    dragItem.Bounds = new Rectangle(x, y, old.Width, old.Height);
                    Invalidate(Rectangle.Union(old, dragItem.Bounds));
                    RaiseInteractiveRegionChanged();
                }
            }

            DesktopItem hit = HitTest(e.Location);
            if (hit != hovered) { hovered = hit; Cursor = hit == null ? Cursors.Default : Cursors.Hand; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hovered = null; Cursor = Cursors.Default; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            selected = HitTest(e.Location); Invalidate();
            if (e.Button == MouseButtons.Left && selected != null)
            {
                dragItem = selected;
                dragStart = e.Location;
                dragOrigin = selected.Bounds.Location;
                dragging = false;
                Capture = true;
            }
            if (e.Button == MouseButtons.Right && selected != null)
            {
                contextItemPath = selected.Path;
                itemMenu.Show(this, e.Location);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && dragItem != null)
            {
                if (dragging)
                {
                    Rectangle snapped = FindNearestFreeCell(dragItem, dragItem.Bounds.Location);
                    dragItem.Bounds = snapped;
                    savedPositions[dragItem.Name] = snapped.Location;
                    SavePositions();
                    suppressDoubleClick = true;
                    Invalidate();
                    RaiseInteractiveRegionChanged();
                }
                dragItem = null;
                dragging = false;
                Capture = false;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (suppressDoubleClick) { suppressDoubleClick = false; return; }
            DesktopItem hit = HitTest(e.Location); if (hit != null) OpenPath(hit.Path);
            base.OnMouseDoubleClick(e);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
            if (m.Msg == WM_NCHITTEST)
            {
                long raw = m.LParam.ToInt64();
                Point screen = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
                if (HitTest(PointToClient(screen)) == null) { m.Result = new IntPtr(HTTRANSPARENT); return; }
            }
            base.WndProc(ref m);
        }

        private DesktopItem HitTest(Point p) { foreach (DesktopItem item in items) if (item.Bounds.Contains(p)) return item; return null; }

        private Rectangle FindNearestFreeCell(DesktopItem moving, Point requested)
        {
            int requestedColumn = Math.Max(0, (int)Math.Round((requested.X - MarginX) / (double)CellWidth));
            int requestedRow = Math.Max(0, (int)Math.Round((requested.Y - MarginY) / (double)CellHeight));
            int rows = Math.Max(1, (Height - MarginY * 2) / CellHeight);
            int columns = Math.Max(1, (Width - MarginX) / CellWidth);

            Rectangle best = moving.Bounds;
            int bestDistance = Int32.MaxValue;
            for (int column = 0; column < columns; column++)
            {
                for (int row = 0; row < rows; row++)
                {
                    Rectangle candidate = CreateCellBounds(MarginX + column * CellWidth, MarginY + row * CellHeight);
                    if (!IsInsideCanvas(candidate) || !IsPositionAvailable(candidate, moving)) continue;
                    int distance = Math.Abs(column - requestedColumn) + Math.Abs(row - requestedRow);
                    if (distance < bestDistance)
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }
            }
            return best;
        }

        private bool IsPositionAvailable(Rectangle candidate, DesktopItem moving)
        {
            if (IntersectsAny(candidate, reservedNativeBounds)) return false;
            foreach (DesktopItem item in items)
                if (item != moving && !item.Bounds.IsEmpty && item.Bounds.IntersectsWith(candidate)) return false;
            return true;
        }

        private bool IsInsideCanvas(Rectangle bounds)
        {
            return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= Width && bounds.Bottom <= Height;
        }

        private static bool IntersectsAny(Rectangle bounds, List<Rectangle> occupied)
        {
            foreach (Rectangle other in occupied) if (bounds.IntersectsWith(other)) return true;
            return false;
        }

        private static Rectangle CreateCellBounds(int x, int y)
        {
            return new Rectangle(x, y, CellWidth - 8, CellHeight - 6);
        }

        private void LoadSavedPositions()
        {
            savedPositions.Clear();
            if (String.IsNullOrEmpty(LayoutPath) || !File.Exists(LayoutPath)) return;
            try
            {
                foreach (string line in File.ReadAllLines(LayoutPath, Encoding.UTF8))
                {
                    string[] parts = line.Split('|');
                    int x, y;
                    if (parts.Length != 3 || !Int32.TryParse(parts[1], out x) || !Int32.TryParse(parts[2], out y)) continue;
                    string name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                    savedPositions[name] = new Point(x, y);
                }
            }
            catch { savedPositions.Clear(); }
        }

        private void SavePositions()
        {
            if (String.IsNullOrEmpty(LayoutPath)) return;
            try
            {
                var lines = new List<string>();
                foreach (DesktopItem item in items)
                {
                    string key = Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Name));
                    lines.Add(key + "|" + item.Bounds.X + "|" + item.Bounds.Y);
                }
                File.WriteAllLines(LayoutPath, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        private void RaiseInteractiveRegionChanged()
        {
            EventHandler handler = InteractiveRegionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static string NormalizeDesktopName(string name)
        {
            if (String.IsNullOrEmpty(name)) return String.Empty;
            string extension = Path.GetExtension(name);
            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(name).Trim();
            return name.Trim();
        }

        private static int ComparePaths(string a, string b)
        {
            bool ad = Directory.Exists(a), bd = Directory.Exists(b);
            if (ad != bd) return ad ? -1 : 1;
            return StringComparer.CurrentCultureIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b));
        }
        private static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private static void LocatePath(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private sealed class DesktopItem { public string Path, Name; public Bitmap Image; public Rectangle Bounds; }
    }

    internal sealed class NativeDesktopIcon
    {
        public string Name;
        public Point ScreenPosition;
    }

    internal static class NativeDesktopIconReader
    {
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        private const uint LVM_GETITEMCOUNT = 0x1004;
        private const uint LVM_GETITEMPOSITION = 0x1010;
        private const uint LVM_GETITEMTEXTW = 0x1073;
        private const uint LVIF_TEXT = 0x0001;
        private const uint ProcessAccess = 0x0008 | 0x0010 | 0x0020 | 0x0400;

        public static List<NativeDesktopIcon> Read(Rectangle targetScreen)
        {
            var result = new List<NativeDesktopIcon>();
            IntPtr listView = FindDesktopListView();
            if (listView == IntPtr.Zero) return result;

            uint processId;
            GetWindowThreadProcessId(listView, out processId);
            IntPtr process = OpenProcess(ProcessAccess, false, processId);
            if (process == IntPtr.Zero) return result;

            int itemSize = Marshal.SizeOf(typeof(LVITEM));
            int textBytes = 1024;
            int totalBytes = itemSize + textBytes;
            IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, new UIntPtr((uint)totalBytes), 0x3000, 0x04);
            IntPtr local = Marshal.AllocHGlobal(totalBytes);
            if (remote == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(local);
                CloseHandle(process);
                return result;
            }

            try
            {
                int count = SendMessage(listView, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                for (int index = 0; index < count; index++)
                {
                    string name = ReadItemName(process, listView, remote, local, itemSize, textBytes, index);
                    NativePoint point = ReadItemPosition(process, listView, remote, local, index);
                    ClientToScreen(listView, ref point);
                    Point screenPoint = new Point(point.X, point.Y);
                    if (!String.IsNullOrWhiteSpace(name) && targetScreen.Contains(screenPoint))
                        result.Add(new NativeDesktopIcon { Name = name, ScreenPosition = screenPoint });
                }
            }
            catch { result.Clear(); }
            finally
            {
                Marshal.FreeHGlobal(local);
                VirtualFreeEx(process, remote, UIntPtr.Zero, 0x8000);
                CloseHandle(process);
            }
            return result;
        }

        private static string ReadItemName(IntPtr process, IntPtr listView, IntPtr remote,
                                           IntPtr local, int itemSize, int textBytes, int index)
        {
            var item = new LVITEM
            {
                mask = LVIF_TEXT,
                iItem = index,
                pszText = IntPtr.Add(remote, itemSize),
                cchTextMax = textBytes / 2
            };
            Marshal.StructureToPtr(item, local, false);
            UIntPtr transferred;
            WriteProcessMemory(process, remote, local, new UIntPtr((uint)itemSize), out transferred);
            SendMessage(listView, LVM_GETITEMTEXTW, new IntPtr(index), remote);
            ReadProcessMemory(process, IntPtr.Add(remote, itemSize), local,
                new UIntPtr((uint)textBytes), out transferred);
            return Marshal.PtrToStringUni(local) ?? String.Empty;
        }

        private static NativePoint ReadItemPosition(IntPtr process, IntPtr listView,
                                                    IntPtr remote, IntPtr local, int index)
        {
            UIntPtr transferred;
            SendMessage(listView, LVM_GETITEMPOSITION, new IntPtr(index), remote);
            ReadProcessMemory(process, remote, local, new UIntPtr(8), out transferred);
            return (NativePoint)Marshal.PtrToStructure(local, typeof(NativePoint));
        }

        private static IntPtr FindDesktopListView()
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows(delegate(IntPtr top, IntPtr unused)
            {
                EnumChildWindows(top, delegate(IntPtr child, IntPtr inner)
                {
                    var className = new StringBuilder(64);
                    GetClassName(child, className, className.Capacity);
                    if (String.Equals(className.ToString(), "SysListView32", StringComparison.Ordinal) &&
                        ScreenCompanion.Native.ClassName(GetParent(child)) == "SHELLDLL_DefView")
                    {
                        result = child;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                return result == IntPtr.Zero;
            }, IntPtr.Zero);
            return result;
        }

        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LVITEM
        {
            public uint mask;
            public int iItem, iSubItem;
            public uint state, stateMask;
            public IntPtr pszText;
            public int cchTextMax, iImage;
            public IntPtr lParam;
            public int iIndent, iGroupId;
            public uint cColumns;
            public IntPtr puColumns, piColFmt;
            public int iGroup;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maximum);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protection);
        [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, UIntPtr size, out UIntPtr written);
        [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }

    internal static class ShellIcon
    {
        private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint a, ref SHFILEINFO i, uint s, uint f);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        public static Icon Get(string path)
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_LARGEICON);
            if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try { return (Icon)Icon.FromHandle(info.hIcon).Clone(); } finally { DestroyIcon(info.hIcon); }
            }
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
