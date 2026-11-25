// 音频桥 · Windows 端 · 界面
//
// 整个窗口自绘（GDI+），布局和配色与 Mac 端 MainView.swift 是同一套数值：
// 逻辑尺寸 400×720，按系统 DPI 等比放大。只有「手动地址」那一格是真的 TextBox。
// 图标全部用矢量画，不依赖图标字体，Win7–Win11 上长得一样。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AudioBridge
{
    internal static class Theme
    {
        public static bool Dark;
        public static Color Window, Card, Inset, Hairline, Text, Text2, Text3, Live, Warn, Error, Track;

        private static Color Pick(int light, int dark)
        {
            int v = Dark ? dark : light;
            return Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        /// 界面预览用：-1 跟随系统，0 强制浅色，1 强制深色。
        public static int Force = -1;

        public static void Load()
        {
            Dark = false;
            if (Force < 0) try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) Dark = (int)v == 0;
                    }
                }
            }
            catch { }
            if (Force >= 0) Dark = Force == 1;
            Window = Pick(0xF4F4F6, 0x1B1B1D);
            Card = Pick(0xFFFFFF, 0x262628);
            Inset = Pick(0xEEEEF1, 0x1F1F21);
            Hairline = Pick(0xE2E2E6, 0x38383B);
            Text = Pick(0x1A1A1C, 0xF2F2F3);
            Text2 = Pick(0x6C6C72, 0x9C9CA3);
            Text3 = Pick(0x9A9AA1, 0x6E6E75);
            Live = Pick(0x1F9D6B, 0x3CCB8A);
            Warn = Pick(0xC98A12, 0xE5A93A);
            Error = Pick(0xD13B40, 0xF0666A);
            Track = Pick(0xE4E4E8, 0x3A3A3D);
        }

        public static Color StateColor(LinkState s)
        {
            switch (s)
            {
                case LinkState.Live: return Live;
                case LinkState.Idle:
                case LinkState.Connecting: return Color.FromArgb(190, Live);
                case LinkState.Warning: return Warn;
                case LinkState.Error: return Error;
                default: return Text3;
            }
        }
    }

    internal sealed class Zone
    {
        public RectangleF R;
        public string Key;
        public string Tip;
        public Action Click;
    }

    internal sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Hairline; } }
        public override Color MenuItemBorder { get { return Theme.Track; } }
        public override Color MenuItemSelected { get { return Theme.Track; } }
        public override Color SeparatorDark { get { return Theme.Hairline; } }
        public override Color SeparatorLight { get { return Theme.Hairline; } }
        public override Color CheckBackground { get { return Theme.Track; } }
        public override Color CheckSelectedBackground { get { return Theme.Track; } }
        public override Color CheckPressedBackground { get { return Theme.Track; } }
    }

    internal sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Text3;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.ImageRectangle;
            using (Pen p = new Pen(Theme.Live, 2f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                float x = r.X, y = r.Y, w = r.Width, h = r.Height;
                g.DrawLines(p, new PointF[] {
                    new PointF(x + w * 0.22f, y + h * 0.52f), new PointF(x + w * 0.42f, y + h * 0.72f), new PointF(x + w * 0.78f, y + h * 0.30f) });
            }
        }
    }

    internal sealed class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private const float W = 400, H = 720;

        private readonly AppModel model;
        private readonly Timer timer = new Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly TextBox manualBox = new TextBox();
        private readonly ToolTip tip = new ToolTip();
        private readonly List<Zone> zones = new List<Zone>();
        private readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        private readonly Icon iconIdle, iconLive, iconWarn;

        private float s = 1f;
        private string hoverKey = "";
        private string pressedKey = "";
        private bool draggingVolume;
        private RectangleF volumeTrack;
        private int lastRevision = -1;
        private int peerScroll;
        private bool autostart;
        private bool startHidden;
        private bool exiting;
        private bool balloonShown;
        private long copiedUntil;
        private string fontName = "Microsoft YaHei UI";
        private LinkState trayState = (LinkState)(-1);
        private string trayText = "";

        public MainForm(AppModel model, bool startHidden)
        {
            this.model = model;
            this.startHidden = startHidden;
            model.AskPermission = delegate(string title, string text)
            {
                // 只驻留托盘时窗口可能是隐藏/最小化的：先亮出来，保证弹窗能被看见。
                try
                {
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    if (!Visible) Show();
                    TopMost = true;
                    Activate();
                    BringToFront();
                }
                catch { }
                try { tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info); } catch { }
                // 不能同时传所属窗口和 DefaultDesktopOnly：.NET 会抛 ArgumentException（A-1）。
                // 置顶靠上面的 TopMost = true，复原一定要在 finally 里。
                try
                {
                    DialogResult r = MessageBox.Show(this, text, title, MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                    return r == DialogResult.Yes;
                }
                finally { TopMost = false; }
            };
            autostart = Autostart.IsEnabled;

            // 取系统 DPI（进程已声明 DPI 感知）。用桌面 DC，不提前创建窗口句柄。
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f; } catch { s = 1f; }
            if (s < 1f) s = 1f;
            if (Program.SnapshotMode) s = 2f;
            using (Font probe = new Font(fontName, 9f))
            {
                if (probe.Name != fontName) fontName = "Segoe UI";
            }

            Text = "音频桥";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size((int)Math.Round(W * s), (int)Math.Round(H * s));
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            KeyPreview = true;

            iconIdle = MakeIcon(Color.FromArgb(255, 0x9C, 0x9C, 0xA3));
            iconLive = MakeIcon(Color.FromArgb(255, 0x3C, 0xCB, 0x8A));
            iconWarn = MakeIcon(Color.FromArgb(255, 0xE5, 0xA9, 0x3A));
            Icon = iconLive;
            // 窗口/任务栏用 exe 里嵌的那个多尺寸图标（编译时 /win32icon:app.ico），取不到就用上面画的。
            try
            {
                Icon embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null) Icon = embedded;
            }
            catch { }

            manualBox.BorderStyle = BorderStyle.None;
            manualBox.Font = new Font("Consolas", 12.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            manualBox.Text = model.ManualHost;
            manualBox.MaxLength = 80;
            manualBox.KeyDown += delegate(object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    UseManual();
                }
            };
            manualBox.TextChanged += delegate { Invalidate(); };
            manualBox.HandleCreated += delegate { SendMessage(manualBox.Handle, 0x1501, (IntPtr)1, "手动输入 IP 地址"); };
            Controls.Add(manualBox);

            tray.Icon = iconIdle;
            tray.Text = "音频桥";
            tray.Visible = !Program.SnapshotMode;
            tray.ContextMenuStrip = trayMenu;
            tray.MouseClick += delegate(object o, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMain(); };
            trayMenu.Opening += delegate { BuildTrayMenu(); };

            tip.InitialDelay = 500;
            tip.ReshowDelay = 200;

            ApplyTheme();
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;

            timer.Interval = 50;
            timer.Tick += delegate { OnTick(); };
            if (!Program.SnapshotMode) timer.Start();
        }

        /// 界面预览：不显示窗口，直接把当前状态画进 PNG。
        public void Snapshot(string path)
        {
            ApplyTheme();
            using (Bitmap bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    OnPaint(new PaintEventArgs(g, new Rectangle(Point.Empty, ClientSize)));
                    if (model.Role == Role.Sender && manualBox.Text.Length == 0)
                        TL(g, "手动输入 IP 地址", Mono(12.5f), Theme.Text3, 72, 382, 240, 24);
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // 登录自启时不弹窗口，只进托盘。
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        public void ShowMain()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            exiting = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 点 × 只是收进托盘；真正退出走托盘菜单。系统关机/注销照常放行。
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!balloonShown)
                {
                    balloonShown = true;
                    try { tray.ShowBalloonTip(3000, "音频桥仍在后台运行", "点托盘图标可以重新打开窗口，右键可退出。", ToolTipIcon.None); } catch { }
                }
                return;
            }
            timer.Stop();
            tray.Visible = false;
            try { SystemEvents.UserPreferenceChanged -= OnPreferenceChanged; } catch { }
            model.Shutdown();
            base.OnFormClosing(e);
        }

        private void OnPreferenceChanged(object o, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General) ApplyTheme();
        }

        private void ApplyTheme()
        {
            Theme.Load();
            BackColor = Theme.Window;
            manualBox.BackColor = Theme.Inset;
            manualBox.ForeColor = Theme.Text;
            trayMenu.Renderer = new MenuRenderer();
            trayMenu.ForeColor = Theme.Text;
            if (IsHandleCreated) ApplyTitleBar();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBar();
        }

        private void ApplyTitleBar()
        {
            try
            {
                int on = Theme.Dark ? 1 : 0;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 2004+），老版本是 19
                if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4);
            }
            catch { }
        }

        // ---------- 定时刷新 ----------

        private void OnTick()
        {
            bool levels = model.Tick();
            UpdateTray();
            if (model.Revision != lastRevision)
            {
                lastRevision = model.Revision;
                if (Visible) Invalidate();
            }
            else if (levels && Visible)
            {
                Invalidate(Rectangle.Round(SR(16, 60, 368, 172)));
            }
            if (copiedUntil != 0 && Proto.NowNs() > copiedUntil)
            {
                copiedUntil = 0;
                Invalidate();
            }
        }

        private void UpdateTray()
        {
            LinkState st = model.State;
            string text = "音频桥 · " + model.Title;
            if (text.Length > 60) text = text.Substring(0, 60);
            if (st == trayState && text == trayText) return;
            trayState = st;
            trayText = text;
            tray.Icon = st == LinkState.Live ? iconLive : ((st == LinkState.Warning || st == LinkState.Error) ? iconWarn : iconIdle);
            tray.Text = text;
        }

        private void BuildTrayMenu()
        {
            trayMenu.Items.Clear();
            ToolStripMenuItem head = new ToolStripMenuItem((model.Role == Role.Receiver ? "接收" : "发送") + " · " + model.Title);
            head.Enabled = false;
            trayMenu.Items.Add(head);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("打开主窗口", null, delegate { ShowMain(); });
            trayMenu.Items.Add(PrimaryTitle(), null, delegate { model.SetRunning(!model.Running); });
            if (model.Role == Role.Receiver)
            {
                ToolStripMenuItem mute = new ToolStripMenuItem("静音", null, delegate { model.SetMuted(!model.Muted); });
                mute.Checked = model.Muted;
                trayMenu.Items.Add(mute);
            }
            trayMenu.Items.Add(model.Role == Role.Receiver ? "切换为发送" : "切换为接收", null,
                               delegate { model.SetRole(model.Role == Role.Receiver ? Role.Sender : Role.Receiver); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出音频桥", null, delegate { ExitApp(); });
        }

        private string PrimaryTitle()
        {
            if (model.Running) return model.Role == Role.Receiver ? "停止接收" : "停止发送";
            return model.Role == Role.Receiver ? "开始接收" : "开始发送";
        }

        // ---------- 绘制工具 ----------

        private RectangleF SR(float x, float y, float w, float h)
        {
            return new RectangleF(x * s, y * s, w * s, h * s);
        }

        private Font F(float px, bool bold)
        {
            string key = px + (bold ? "b" : "r");
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(fontName, px * s, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        private Font Mono(float px)
        {
            string key = px + "m";
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font("Consolas", px * s, FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        private static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius * s))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        private void Stroke(Graphics g, RectangleF r, float radius, Color c)
        {
            RectangleF q = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
            using (GraphicsPath p = Round(q, radius * s))
            using (Pen pen = new Pen(c, 1f)) g.DrawPath(pen, p);
        }

        private void CardBox(Graphics g, RectangleF r)
        {
            Fill(g, r, 12, Theme.Card);
            Stroke(g, r, 12, Theme.Hairline);
        }

        private void Txt(Graphics g, string text, Font f, Color c, RectangleF r, StringAlignment h, StringAlignment v, bool wrap)
        {
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(c))
            {
                sf.Alignment = h;
                sf.LineAlignment = v;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                if (!wrap) sf.FormatFlags |= StringFormatFlags.NoWrap;
                g.DrawString(text, f, b, r, sf);
            }
        }

        private void TL(Graphics g, string text, Font f, Color c, float x, float y, float w, float h)
        {
            Txt(g, text, f, c, SR(x, y, w, h), StringAlignment.Near, StringAlignment.Center, false);
        }

        private void TR(Graphics g, string text, Font f, Color c, float x, float y, float w, float h)
        {
            Txt(g, text, f, c, SR(x, y, w, h), StringAlignment.Far, StringAlignment.Center, false);
        }

        private void TC(Graphics g, string text, Font f, Color c, float x, float y, float w, float h)
        {
            Txt(g, text, f, c, SR(x, y, w, h), StringAlignment.Center, StringAlignment.Center, false);
        }

        private Zone AddZone(float x, float y, float w, float h, string key, string tipText, Action click)
        {
            Zone z = new Zone();
            z.R = SR(x, y, w, h);
            z.Key = key;
            z.Tip = tipText;
            z.Click = click;
            zones.Add(z);
            return z;
        }

        private Pen P(Color c, float width)
        {
            Pen p = new Pen(c, width * s);
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            return p;
        }

        // ---------- 矢量图标（r 为逻辑坐标下的方形区域）----------

        private PointF Pt(RectangleF r, float u, float v)
        {
            return new PointF(r.X + r.Width * u, r.Y + r.Height * v);
        }

        private void IconSpeaker(Graphics g, RectangleF r, Color c, bool muted)
        {
            using (SolidBrush b = new SolidBrush(c))
            using (Pen p = P(c, 1.4f))
            {
                g.FillPolygon(b, new PointF[] {
                    Pt(r, 0.08f, 0.38f), Pt(r, 0.26f, 0.38f), Pt(r, 0.48f, 0.18f),
                    Pt(r, 0.48f, 0.82f), Pt(r, 0.26f, 0.62f), Pt(r, 0.08f, 0.62f) });
                if (muted)
                {
                    g.DrawLine(p, Pt(r, 0.64f, 0.38f), Pt(r, 0.88f, 0.62f));
                    g.DrawLine(p, Pt(r, 0.88f, 0.38f), Pt(r, 0.64f, 0.62f));
                }
                else
                {
                    Arc(g, p, r, 0.48f, 0.5f, 0.17f, -42, 84);
                    Arc(g, p, r, 0.48f, 0.5f, 0.33f, -42, 84);
                }
            }
        }

        private void Arc(Graphics g, Pen p, RectangleF r, float cu, float cv, float radius, float start, float sweep)
        {
            float rad = r.Width * radius;
            PointF c = Pt(r, cu, cv);
            g.DrawArc(p, c.X - rad, c.Y - rad, rad * 2, rad * 2, start, sweep);
        }

        private void IconBroadcast(Graphics g, RectangleF r, Color c)
        {
            using (SolidBrush b = new SolidBrush(c))
            using (Pen p = P(c, 1.4f))
            {
                PointF m = Pt(r, 0.5f, 0.5f);
                float d = r.Width * 0.09f;
                g.FillEllipse(b, m.X - d, m.Y - d, d * 2, d * 2);
                Arc(g, p, r, 0.5f, 0.5f, 0.24f, -48, 96);
                Arc(g, p, r, 0.5f, 0.5f, 0.24f, 132, 96);
                Arc(g, p, r, 0.5f, 0.5f, 0.40f, -48, 96);
                Arc(g, p, r, 0.5f, 0.5f, 0.40f, 132, 96);
            }
        }

        private void IconCheck(Graphics g, RectangleF r, Color c, float width)
        {
            using (Pen p = P(c, width))
                g.DrawLines(p, new PointF[] { Pt(r, 0.2f, 0.52f), Pt(r, 0.42f, 0.74f), Pt(r, 0.8f, 0.28f) });
        }

        private void IconState(Graphics g, RectangleF r, LinkState st, Color c)
        {
            switch (st)
            {
                case LinkState.Live:
                    if (model.Role == Role.Receiver) IconSpeaker(g, r, c, false); else IconBroadcast(g, r, c);
                    break;
                case LinkState.Idle:
                    IconCheck(g, r, c, 1.8f);
                    break;
                case LinkState.Waiting:
                    IconBroadcast(g, r, c);
                    break;
                case LinkState.Connecting:
                    using (SolidBrush b = new SolidBrush(c))
                    {
                        float d = r.Width * 0.14f;
                        for (int i = 0; i < 3; i++)
                        {
                            PointF m = Pt(r, 0.22f + 0.28f * i, 0.5f);
                            g.FillEllipse(b, m.X - d / 2, m.Y - d / 2, d, d);
                        }
                    }
                    break;
                case LinkState.Warning:
                    using (Pen p = P(c, 2f))
                    using (SolidBrush b = new SolidBrush(c))
                    {
                        g.DrawLine(p, Pt(r, 0.5f, 0.18f), Pt(r, 0.5f, 0.58f));
                        PointF m = Pt(r, 0.5f, 0.8f);
                        float d = 1.3f * s;
                        g.FillEllipse(b, m.X - d, m.Y - d, d * 2, d * 2);
                    }
                    break;
                case LinkState.Error:
                    using (Pen p = P(c, 2f))
                    {
                        g.DrawLine(p, Pt(r, 0.24f, 0.24f), Pt(r, 0.76f, 0.76f));
                        g.DrawLine(p, Pt(r, 0.76f, 0.24f), Pt(r, 0.24f, 0.76f));
                    }
                    break;
                default:
                    using (SolidBrush b = new SolidBrush(c))
                    {
                        g.FillRectangle(b, r.X + r.Width * 0.26f, r.Y + r.Height * 0.2f, r.Width * 0.16f, r.Height * 0.6f);
                        g.FillRectangle(b, r.X + r.Width * 0.58f, r.Y + r.Height * 0.2f, r.Width * 0.16f, r.Height * 0.6f);
                    }
                    break;
            }
        }

        private void IconComputer(Graphics g, RectangleF r, Color c, bool laptop)
        {
            using (Pen p = P(c, 1.3f))
            {
                if (laptop)
                {
                    using (GraphicsPath path = Round(new RectangleF(r.X + r.Width * 0.2f, r.Y + r.Height * 0.24f, r.Width * 0.6f, r.Height * 0.42f), 1.5f * s))
                        g.DrawPath(p, path);
                    g.DrawLine(p, Pt(r, 0.08f, 0.76f), Pt(r, 0.92f, 0.76f));
                }
                else
                {
                    using (GraphicsPath path = Round(new RectangleF(r.X + r.Width * 0.12f, r.Y + r.Height * 0.18f, r.Width * 0.76f, r.Height * 0.5f), 1.5f * s))
                        g.DrawPath(p, path);
                    g.DrawLine(p, Pt(r, 0.5f, 0.68f), Pt(r, 0.5f, 0.82f));
                    g.DrawLine(p, Pt(r, 0.34f, 0.82f), Pt(r, 0.66f, 0.82f));
                }
            }
        }

        private void IconGlobe(Graphics g, RectangleF r, Color c)
        {
            using (Pen p = P(c, 1.2f))
            {
                RectangleF o = new RectangleF(r.X + r.Width * 0.14f, r.Y + r.Height * 0.14f, r.Width * 0.72f, r.Height * 0.72f);
                g.DrawEllipse(p, o);
                g.DrawEllipse(p, r.X + r.Width * 0.34f, o.Y, r.Width * 0.32f, o.Height);
                g.DrawLine(p, Pt(r, 0.14f, 0.5f), Pt(r, 0.86f, 0.5f));
            }
        }

        private void IconCopy(Graphics g, RectangleF r, Color c)
        {
            using (Pen p = P(c, 1.2f))
            using (SolidBrush bg = new SolidBrush(Theme.Card))
            {
                RectangleF a = new RectangleF(r.X + r.Width * 0.36f, r.Y + r.Height * 0.16f, r.Width * 0.44f, r.Height * 0.52f);
                RectangleF b = new RectangleF(r.X + r.Width * 0.2f, r.Y + r.Height * 0.32f, r.Width * 0.44f, r.Height * 0.52f);
                using (GraphicsPath pa = Round(a, 1.5f * s)) g.DrawPath(p, pa);
                using (GraphicsPath pb = Round(b, 1.5f * s)) { g.FillPath(bg, pb); g.DrawPath(p, pb); }
            }
        }

        private void IconChevrons(Graphics g, RectangleF r, Color c)
        {
            using (Pen p = P(c, 1.3f))
            {
                g.DrawLines(p, new PointF[] { Pt(r, 0.3f, 0.4f), Pt(r, 0.5f, 0.22f), Pt(r, 0.7f, 0.4f) });
                g.DrawLines(p, new PointF[] { Pt(r, 0.3f, 0.6f), Pt(r, 0.5f, 0.78f), Pt(r, 0.7f, 0.6f) });
            }
        }

        /// 托盘图标：和应用图标同一个图形（石墨底 + 两根白色立柱各开一个圆孔 + 中间一道下垂的弧），
        /// 只有弧的颜色随状态变。坐标与 scripts/make-icon.swift 的 variant 0 一一对应（底板边长 30px，y 轴翻转），
        /// 改图形时两边要一起改。
        private Icon MakeIcon(Color accent)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    Color plate = Color.FromArgb(255, 0x24, 0x24, 0x27);
                    using (GraphicsPath p = Round(new RectangleF(1, 1, 30, 30), 7))
                    using (SolidBrush b = new SolidBrush(plate)) g.FillPath(b, p);
                    // 弧：抛物线，两端藏在立柱后面
                    PointF[] arc = new PointF[17];
                    for (int i = 0; i < arc.Length; i++)
                    {
                        float u = i / 8f - 1f;                       // -1 .. 1
                        arc[i] = new PointF(16f + u * 6.45f, 19f - 7.8f * u * u);
                    }
                    using (Pen pen = new Pen(accent, 2.2f))
                    {
                        pen.LineJoin = LineJoin.Round;
                        g.DrawLines(pen, arc);
                    }
                    // 两根立柱压在弧上面，各开一个圆孔
                    using (SolidBrush body = new SolidBrush(Color.FromArgb(255, 0xF4, 0xF5, 0xF7)))
                    using (SolidBrush hole = new SolidBrush(plate))
                    {
                        for (int k = 0; k < 2; k++)
                        {
                            float x = k == 0 ? 7f : 19.9f;
                            using (GraphicsPath p = Round(new RectangleF(x, 8.5f, 5.1f, 15f), 1.65f)) g.FillPath(body, p);
                            g.FillEllipse(hole, x + 2.55f - 1.4f, 19.45f - 1.4f, 2.8f, 2.8f);
                        }
                    }
                }
                // GetHicon 出来的句柄归这个 Icon 所有，进程存活期间一直要用，不销毁。
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ---------- 绘制 ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Theme.Window);
            zones.Clear();

            DrawHeader(g);
            DrawStatus(g);
            if (model.Role == Role.Receiver) DrawReceiver(g); else DrawSender(g);
            DrawFooter(g);
            LayoutManualBox();
        }

        private void DrawHeader(Graphics g)
        {
            TL(g, "音频桥", F(15, true), Theme.Text, 18, 12, 160, 22);
            TL(g, "局域网实时音频", F(10.5f, false), Theme.Text3, 18, 33, 160, 16);

            float x = 230, y = 15;
            Fill(g, SR(x, y, 154, 30), 9, Theme.Track);
            DrawRoleSeg(g, x + 2, y + 2, Role.Receiver, "接收");
            DrawRoleSeg(g, x + 78, y + 2, Role.Sender, "发送");
        }

        private void DrawRoleSeg(Graphics g, float x, float y, Role r, string title)
        {
            bool on = model.Role == r;
            string key = "role" + (int)r;
            if (on) Fill(g, SR(x, y, 74, 26), 7, Theme.Card);
            else if (hoverKey == key) Fill(g, SR(x, y, 74, 26), 7, Color.FromArgb(40, Theme.Card));
            Color c = on ? Theme.Text : Theme.Text2;
            RectangleF ic = SR(x + 12, y + 6, 14, 14);
            if (r == Role.Receiver) IconSpeaker(g, ic, c, false); else IconBroadcast(g, ic, c);
            TL(g, title, F(12.5f, on), c, x + 31, y, 40, 26);
            AddZone(x, y, 74, 26, key, r == Role.Receiver ? "接收并播放另一台电脑的声音" : "把这台电脑的声音送到另一台",
                    delegate { model.SetRole(r); });
        }

        private void DrawStatus(Graphics g)
        {
            RectangleF card = SR(16, 60, 368, 172);
            CardBox(g, card);

            Color sc = Theme.StateColor(model.State);
            RectangleF circle = SR(34, 78, 34, 34);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(40, sc))) g.FillEllipse(b, circle);
            IconState(g, SR(43, 87, 16, 16), model.State, sc);

            TL(g, model.Title, F(18, true), Theme.Text, 80, 74, 290, 26);
            bool hasMetrics = model.Metrics.Count > 0;
            Txt(g, model.Detail, F(12, false), Theme.Text2, SR(80, 101, 288, hasMetrics ? 18 : 52),
                StringAlignment.Near, StringAlignment.Near, !hasMetrics);

            float my = hasMetrics ? 136 : 196;
            DrawLevel(g, "L", model.LevelL, my);
            DrawLevel(g, "R", model.LevelR, my + 13);

            if (hasMetrics)
            {
                int n = model.Metrics.Count;
                float colW = 336f / n;
                for (int i = 0; i < n; i++)
                {
                    float x = 34 + colW * i;
                    TL(g, model.Metrics[i].Value, F(14, true), Theme.Text, x, 172, colW - 4, 22);
                    TL(g, model.Metrics[i].Label, F(10.5f, false), Theme.Text3, x, 194, colW - 4, 16);
                }
            }
        }

        private void DrawLevel(Graphics g, string label, double level, float y)
        {
            TL(g, label, Mono(10), Theme.Text3, 34, y - 5, 12, 15);
            RectangleF track = SR(52, y, 314, 5);
            Fill(g, track, 2.5f, Theme.Track);
            if (level > 0.005)
            {
                // −6 dB 以上换成琥珀色、贴顶变红：一眼能看出是不是爆了
                Color c = level > 0.985 ? Theme.Error : (level > 0.9 ? Theme.Warn : Theme.Live);
                if (!model.Running) c = Color.FromArgb(90, c);
                RectangleF bar = new RectangleF(track.X, track.Y, Math.Max(track.Height, (float)(track.Width * level)), track.Height);
                Fill(g, bar, 2.5f, c);
            }
        }

        private void SectionLabel(Graphics g, string text, float y)
        {
            TL(g, text, F(11, true), Theme.Text2, 32, y, 200, 16);
        }

        private void DrawDropdown(Graphics g, float x, float y, float w, string text, string key, Action click)
        {
            RectangleF r = SR(x, y, w, 26);
            Fill(g, r, 6, hoverKey == key ? Theme.Track : Theme.Inset);
            Stroke(g, r, 6, Theme.Hairline);
            TL(g, text, F(12, false), Theme.Text, x + 9, y, w - 32, 26);
            IconChevrons(g, SR(x + w - 22, y + 6, 14, 14), Theme.Text2);
            AddZone(x, y, w, 26, key, null, click);
        }

        private void DrawReceiver(Graphics g)
        {
            CardBox(g, SR(16, 244, 368, 376));

            // —— 接收来源：等对方推送，或者主动去拉 ——
            SectionLabel(g, "接收来源", 258);
            TL(g, SourceTitle(), F(12.5f, true), Theme.Text, 32, 276, 190, 20);
            Txt(g, SourceSub(), F(10.5f, false), Theme.Text3, SR(32, 296, 188, 36),
                StringAlignment.Near, StringAlignment.Near, true);
            bool pulling = model.Pull != PullState.None;
            RectangleF sb = SR(228, 276, 140, 28);
            Fill(g, sb, 7, hoverKey == "pull" ? Theme.Track : Theme.Inset);
            Stroke(g, sb, 7, Theme.Hairline);
            TC(g, pulling ? "取消拉取" : "选择发送端…", F(11.5f, true), pulling ? Theme.Text2 : Theme.Text, 228, 276, 140, 28);
            AddZone(228, 276, 140, 28, "pull",
                pulling ? "不再接收这台电脑的声音" : "主动让某台电脑把声音送过来",
                delegate { if (pulling) model.CancelPull(); else ShowPullMenu(228, 306); });

            Divider(g, 342);

            // —— 播放 ——
            SectionLabel(g, "播放", 352);
            Color mc = model.Muted ? Theme.Warn : Theme.Text2;
            IconSpeaker(g, SR(33, 382, 18, 18), mc, model.Muted);
            AddZone(30, 379, 24, 24, "mute", model.Muted ? "取消静音" : "静音", delegate { model.SetMuted(!model.Muted); });

            volumeTrack = SR(64, 389, 270, 4);
            Fill(g, volumeTrack, 2, Theme.Track);
            float vx = volumeTrack.X + (float)(volumeTrack.Width * model.Volume);
            Color fillC = model.Muted ? Theme.Text3 : Theme.Live;
            if (model.Volume > 0.005)
                Fill(g, new RectangleF(volumeTrack.X, volumeTrack.Y, vx - volumeTrack.X, volumeTrack.Height), 2, fillC);
            float kr = 7.5f * s;
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                g.FillEllipse(shadow, vx - kr, volumeTrack.Y + volumeTrack.Height / 2 - kr + 1f * s, kr * 2, kr * 2);
            using (SolidBrush knob = new SolidBrush(Color.White))
                g.FillEllipse(knob, vx - kr, volumeTrack.Y + volumeTrack.Height / 2 - kr, kr * 2, kr * 2);
            // 浅色主题下白旋钮压在白卡片上，描一圈边才看得见
            using (Pen edge = new Pen(Color.FromArgb(Theme.Dark ? 0 : 60, 0, 0, 0), 1f))
                g.DrawEllipse(edge, vx - kr, volumeTrack.Y + volumeTrack.Height / 2 - kr, kr * 2, kr * 2);
            TR(g, ((int)Math.Round(model.Volume * 100)).ToString(), F(12, false), Theme.Text2, 338, 380, 30, 22);

            // 输出设备
            TL(g, "输出设备", F(12.5f, false), Theme.Text, 32, 416, 100, 26);
            DrawDropdown(g, 138, 416, 230, model.DeviceLabel(model.OutputId), "output", delegate { ShowDeviceMenu(true, 138, 442); });

            // 缓冲
            TL(g, "缓冲", F(12.5f, false), Theme.Text, 32, 452, 100, 26);
            Fill(g, SR(138, 452, 230, 26), 7, Theme.Track);
            float segW = 226f / 4;
            for (int i = 0; i < 4; i++)
            {
                int mode = i;
                float x = 140 + segW * i;
                bool on = model.BufferMode == i;
                string key = "buf" + i;
                if (on) Fill(g, SR(x, 454, segW, 22), 5.5f, Theme.Card);
                else if (hoverKey == key) Fill(g, SR(x, 454, segW, 22), 5.5f, Color.FromArgb(40, Theme.Card));
                TC(g, AppModel.BufferTitles[i], F(11.5f, on), on ? Theme.Text : Theme.Text2, x, 454, segW, 22);
                AddZone(x, 452, segW, 26, key, null, delegate { model.SetBufferMode(mode); });
            }
            int ms = (int)model.BufferMs;
            string hint;
            switch (model.BufferMode)
            {
                case 0: hint = "按网络状况自动调整，当前 " + ms + " ms"; break;
                case 1: hint = ms + " ms · 最跟手，网络差时可能断续"; break;
                case 2: hint = ms + " ms · 多数网络下稳定"; break;
                default: hint = ms + " ms · 抗干扰最强，延迟明显"; break;
            }
            TR(g, hint, F(11, false), Theme.Text3, 100, 481, 268, 16);

            // 原本这里有一条分隔线（y=506），正好压在「同时接收多路」那一行字上，去掉；
            // 上方提示与下方两行靠留白分隔就够，下面 y=574 还有一条分隔线。
            // 多路混音
            DrawRowSwitch(g, "同时接收多路", "最多 4 台设备一起混音", 496, model.MixMode, "mixMode",
                "开了以后可以同时听好几台电脑", delegate { model.SetMixMode(!model.MixMode); });

            // 停在接收模式的机器也要能管拉流，不然得先切到发送模式才能开
            DrawRowSwitch(g, "允许其它设备拉取", "受信任设备 "
                + (model.TrustedDevices.Count == 0 ? "尚无" : model.TrustedDevices.Count + " 台")
                + "（点这里管理）", 534, model.AllowPull, "allowPull",
                "允许局域网里的设备主动来拉这台电脑的声音，新设备需要你确认",
                delegate { model.SetAllowPull(!model.AllowPull); });
            AddZone(32, 553, 290, 18, "trust", "管理已经允许过的设备", delegate { ShowTrustMenu(248, 566); });

            Divider(g, 574);

            // 本机
            TL(g, "本机", F(11, false), Theme.Text3, 32, 580, 100, 16);
            TL(g, Proto.DeviceName, F(12.5f, true), Theme.Text, 32, 596, 170, 20);
            bool copied = copiedUntil != 0;
            string addr = model.LocalAddress.Length == 0 ? "未联网" : model.LocalAddress;
            TR(g, addr, Mono(12.5f), Theme.Text2, 190, 588, 150, 22);
            RectangleF ci = SR(347, 591, 16, 16);
            if (copied) IconCheck(g, ci, Theme.Live, 1.6f); else IconCopy(g, ci, hoverKey == "copy" ? Theme.Text : Theme.Text2);
            AddZone(343, 587, 24, 24, "copy", "复制本机地址", delegate
            {
                if (model.LocalAddress.Length == 0) return;
                try { Clipboard.SetText(model.LocalAddress); } catch { }
                copiedUntil = Proto.NowNs() + 1200000000L;
                Invalidate();
            });
        }

        private string PullName()
        {
            return model.PullTargetName.Length > 0 ? model.PullTargetName : "对方";
        }

        private string SourceTitle()
        {
            if (model.Pull == PullState.Requesting) return "正在请求「" + PullName() + "」";
            if (model.Pull == PullState.Accepted) return "已连上「" + PullName() + "」";
            if (model.Pull == PullState.Denied || model.Pull == PullState.Busy
                || model.Pull == PullState.Disabled || model.Pull == PullState.Failed) return "拉取没有成功";
            return "由发送端推送，或主动拉取";
        }

        private string SourceSub()
        {
            if (model.Pull == PullState.Denied || model.Pull == PullState.Busy
                || model.Pull == PullState.Disabled || model.Pull == PullState.Failed)
                return model.PullError.Length > 0 ? model.PullError : "对方没有回应";
            if (model.Pull == PullState.Requesting)
                return "等对方确认。对方开启「允许其它设备拉取」后可以直接连上";
            if (model.Pull == PullState.Accepted) return "对方正在把声音送到这台电脑";
            return "选一台正在发送的电脑，让它把声音送过来；也可以在对方那边选这台电脑";
        }

        private List<Peer> VisibleReceivers(out bool offlineTarget)
        {
            List<Peer> list = new List<Peer>();
            bool found = false;
            foreach (Peer p in model.Peers)
            {
                if (p.IsReceiver || p.Id == model.TargetPeerId) list.Add(p);
                if (p.Id == model.TargetPeerId) found = true;
            }
            // 上次的目标当前不在线，也留一行，方便看清「正在往哪发」
            offlineTarget = !model.UseManual && model.TargetPeerId != 0 && !found;
            return list;
        }

        private void DrawSender(Graphics g)
        {
            CardBox(g, SR(16, 244, 368, 376));
            SectionLabel(g, "发送到", 258);

            RectangleF box = SR(32, 280, 336, 132);
            Fill(g, box, 9, Theme.Inset);

            bool offline;
            List<Peer> list = VisibleReceivers(out offline);
            int rows = list.Count + (offline ? 1 : 0);
            const float rowH = 47;
            int maxScroll = Math.Max(0, rows - 2);
            if (peerScroll > maxScroll) peerScroll = maxScroll;
            if (peerScroll < 0) peerScroll = 0;

            if (rows == 0)
            {
                TC(g, "未发现接收端", F(12.5f, true), Theme.Text2, 32, 300, 336, 20);
                Txt(g, "在另一台电脑上打开音频桥并切到「接收」，它会自动出现在这里", F(11, false), Theme.Text3,
                    SR(52, 322, 296, 36), StringAlignment.Center, StringAlignment.Near, true);
            }
            else
            {
                GraphicsState saved = g.Save();
                g.SetClip(SR(32, 280, 336, 94));
                for (int i = 0; i < rows; i++)
                {
                    float y = 280 + (i - peerScroll) * rowH;
                    if (y < 280 - rowH || y > 374) continue;
                    if (offline && i == 0)
                    {
                        DrawPeerRow(g, y, model.TargetPeerName.Length > 0 ? model.TargetPeerName : "上次的设备",
                                    "未在线 · 上线后自动连接", false, true, true, "peer-offline", null);
                    }
                    else
                    {
                        Peer p = list[i - (offline ? 1 : 0)];
                        string sub = p.VersionMismatch
                            ? p.PlatformLabel + " · " + p.Ip + " · 版本不同（" + p.Version + "），不能互连"
                            : p.PlatformLabel + " · " + p.Ip + " · "
                              + (p.IsReceiver ? (p.State == 1 ? "接收中" : "等待中") : (p.IsSender ? "当前为发送模式" : "未开启接收"));
                        DrawPeerRow(g, y, p.Name, sub, p.Platform == Proto.PlatformMac,
                                    !model.UseManual && p.Id == model.TargetPeerId, !p.IsReceiver || p.VersionMismatch,
                                    "peer" + p.Id,
                                    p.VersionMismatch ? (Action)null : delegate { model.SelectPeer(p); });
                    }
                }
                g.Restore(saved);
                if (rows > 2)
                {
                    // 细滚动条：提示还有更多，滚轮可翻
                    float trackH = 86, thumbH = trackH * 2f / rows;
                    float ty = 284 + (trackH - thumbH) * peerScroll / (float)maxScroll;
                    Fill(g, SR(363.5f, ty, 2.5f, thumbH), 1.25f, Theme.Text3);
                }
            }

            using (Pen p = new Pen(Theme.Hairline, 1f)) g.DrawLine(p, 72 * s, 374 * s, 368 * s, 374 * s);
            IconGlobe(g, SR(42, 384, 18, 18), Theme.Text2);
            if (model.UseManual)
            {
                IconCheck(g, SR(344, 386, 14, 14), Theme.Live, 1.8f);
            }
            else if (manualBox.Text.Trim().Length > 0)
            {
                TC(g, "使用", F(11.5f, true), Theme.Live, 326, 380, 40, 26);
                AddZone(326, 378, 40, 30, "useManual", null, delegate { UseManual(); });
            }

            DrawRowSwitch(g, "允许其它设备拉取", "对方点一下就能听这台电脑，新设备需要你确认", 424,
                model.AllowPull, "allowPull", "允许局域网里的设备主动来拉这台电脑的声音",
                delegate { model.SetAllowPull(!model.AllowPull); });
            DrawButtonRow(g, "受信任设备  ·  "
                + (model.TrustedDevices.Count == 0 ? "尚无" : model.TrustedDevices.Count + " 台"),
                460, "trust", "管理已经允许过的设备", delegate { ShowTrustMenu(248, 490); });
            DrawRowSwitch(g, "丢包冗余", "每包重发上一包，丢一包不卡；流量约 ×2", 496, model.Redundancy, "redundancy",
                "网络不稳时打开，用双倍流量换不卡顿", delegate { model.SetRedundancy(!model.Redundancy); });

            TL(g, "采集来源", F(12.5f, false), Theme.Text, 32, 534, 100, 26);
            DrawDropdown(g, 138, 534, 230, model.CaptureLabel(),
                         "capture", delegate { ShowDeviceMenu(false, 138, 560); });
        }

        private void DrawRowSwitch(Graphics g, string title, string sub, float y, bool on, string key, string tip, Action click)
        {
            TL(g, title, F(12.5f, false), Theme.Text, 32, y, 250, 22);
            TL(g, sub, F(10.5f, false), Theme.Text3, 32, y + 19, 270, 16);
            DrawSwitch(g, 334, y + 2, on, key, tip, click);
        }

        private void DrawSwitch(Graphics g, float x, float y, bool on, string key, string tip, Action click)
        {
            RectangleF r = SR(x, y, 34, 20);
            Fill(g, r, 10, on ? Theme.Live : Theme.Track);
            float kx = on ? x + 34 - 17 : x + 3;
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, SR(kx, y + 3, 14, 14));
            AddZone(x - 3, y - 4, 40, 28, key, tip, click);
        }

        private void DrawButtonRow(Graphics g, string text, float y, string key, string tip, Action click)
        {
            RectangleF r = SR(32, y, 336, 30);
            Fill(g, r, 7, hoverKey == key ? Theme.Track : Theme.Inset);
            TL(g, text, F(12, false), Theme.Text, 44, y, 250, 30);
            TR(g, ">", F(12, false), Theme.Text3, 348, y, 12, 30);
            AddZone(32, y, 336, 30, key, tip, click);
        }

        private void Divider(Graphics g, float y)
        {
            using (Pen p = new Pen(Theme.Hairline, 1f)) g.DrawLine(p, 32 * s, y * s, 368 * s, y * s);
        }

        private void ShowPullMenu(float x, float y)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new MenuRenderer();
            menu.ForeColor = Theme.Text;
            menu.Font = F(12, false);
            menu.Padding = new Padding(0, 4, 0, 4);
            bool any = false;
            foreach (Peer p in model.Peers)
            {
                if (p.IsReceiver && !p.AllowsPull && !p.VersionMismatch) continue;
                any = true;
                Peer peer = p;
                string state = p.VersionMismatch
                    ? "  ·  版本不同（" + p.Version + "），不能互连"
                    : (p.AllowsPull ? "  ·  可拉取" : "  ·  未开启拉取");
                ToolStripMenuItem it = new ToolStripMenuItem(p.Name + "  ·  " + p.Ip + state, null,
                    delegate { if (!peer.VersionMismatch) model.PullFrom(peer); });
                if (p.VersionMismatch) it.Enabled = false;
                menu.Items.Add(it);
            }
            if (!any)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("没有发现可拉取的设备");
                none.Enabled = false;
                menu.Items.Add(none);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("手动输入 IP 地址…", null, delegate { PromptPull(); }));
            menu.Closed += delegate { BeginInvoke((Action)delegate { menu.Dispose(); }); };
            menu.Show(this, new Point((int)(x * s), (int)(y * s)));
        }

        // 按程序采集：只把选中的那个程序的声音发出去（需要 Win10 2004+）。
        private sealed class ProcItem
        {
            public int Pid;
            public string Name = "";
            public string Text = "";
        }

        private void PromptCaptureProcess()
        {
            using (Form f = new Form())
            {
                f.Text = "按程序采集";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ClientSize = new Size(400, 460);
                f.BackColor = Theme.Card;
                f.ForeColor = Theme.Text;
                f.Font = F(12, false);

                Label t = new Label();
                t.Text = "只采集下面这个程序的声音（含它自己弹出的子进程）：";
                t.Bounds = new Rectangle(20, 16, 360, 22);
                t.ForeColor = Theme.Text;

                ListBox list = new ListBox();
                list.Bounds = new Rectangle(20, 44, 360, 344);
                list.BackColor = Theme.Inset;
                list.ForeColor = Theme.Text;
                list.BorderStyle = BorderStyle.FixedSingle;
                list.IntegralHeight = false;
                list.Font = F(11.5f, false);

                List<ProcItem> items = new List<ProcItem>();
                try
                {
                    Process[] ps = Process.GetProcesses();
                    Array.Sort(ps, delegate(Process a, Process b)
                    {
                        return string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
                    });
                    foreach (Process p in ps)
                    {
                        try
                        {
                            string title = p.MainWindowTitle;
                            if (string.IsNullOrEmpty(title)) continue;
                            ProcItem it = new ProcItem();
                            it.Pid = p.Id;
                            it.Name = p.ProcessName;
                            it.Text = p.ProcessName + "  ·  " + title;
                            items.Add(it);
                            list.Items.Add(it.Text);
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }

                if (items.Count == 0)
                {
                    list.Items.Add("（没有找到带窗口的程序）");
                    list.Enabled = false;
                }

                Button byDevice = new Button();
                byDevice.Text = "按设备采集";
                byDevice.Bounds = new Rectangle(20, 402, 96, 28);
                byDevice.Click += delegate
                {
                    f.DialogResult = DialogResult.Cancel;
                    model.SetCapture("");
                };

                Button ok = new Button();
                ok.Text = "采集";
                ok.DialogResult = DialogResult.OK;
                ok.Bounds = new Rectangle(224, 402, 76, 28);
                Button no = new Button();
                no.Text = "取消";
                no.DialogResult = DialogResult.Cancel;
                no.Bounds = new Rectangle(308, 402, 76, 28);

                list.DoubleClick += delegate
                {
                    if (list.SelectedIndex >= 0) f.DialogResult = DialogResult.OK;
                };

                f.Controls.Add(t);
                f.Controls.Add(list);
                f.Controls.Add(byDevice);
                f.Controls.Add(ok);
                f.Controls.Add(no);
                f.AcceptButton = ok;
                f.CancelButton = no;
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    int i = list.SelectedIndex;
                    if (i >= 0 && i < items.Count) model.SetCaptureProcess(items[i].Pid, items[i].Name);
                }
            }
        }

        private void PromptPull()
        {
            using (Form f = new Form())
            {
                f.Text = "拉取声音";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ClientSize = new Size(360, 150);
                f.BackColor = Theme.Card;
                f.ForeColor = Theme.Text;
                f.Font = F(12, false);

                Label l = new Label();
                l.Text = "输入正在发送声音的那台电脑的 IP 地址：";
                l.Bounds = new Rectangle(20, 18, 320, 22);
                l.ForeColor = Theme.Text;

                TextBox t = new TextBox();
                t.Text = model.ManualHost;
                t.Bounds = new Rectangle(20, 46, 320, 26);
                t.BackColor = Theme.Inset;
                t.ForeColor = Theme.Text;
                t.BorderStyle = BorderStyle.FixedSingle;

                Button ok = new Button();
                ok.Text = "拉取";
                ok.DialogResult = DialogResult.OK;
                ok.Bounds = new Rectangle(180, 92, 76, 28);
                Button no = new Button();
                no.Text = "取消";
                no.DialogResult = DialogResult.Cancel;
                no.Bounds = new Rectangle(264, 92, 76, 28);

                f.Controls.Add(l);
                f.Controls.Add(t);
                f.Controls.Add(ok);
                f.Controls.Add(no);
                f.AcceptButton = ok;
                f.CancelButton = no;
                while (true)
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    string host = t.Text.Trim();
                    if (host.Length == 0) continue;
                    model.PullManual(host);
                    if (model.PullError.Length == 0) return;
                    MessageBox.Show(this, model.PullError, "拉取没有成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ShowTrustMenu(float x, float y)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new MenuRenderer();
            menu.ForeColor = Theme.Text;
            menu.Font = F(12, false);
            if (model.TrustedDevices.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("还没有允许过任何设备");
                none.Enabled = false;
                menu.Items.Add(none);
            }
            foreach (TrustDevice d in model.TrustedDevices)
            {
                TrustDevice dev = d;
                string name = d.Name.Length > 0 ? d.Name : d.Id.ToString();
                ToolStripMenuItem it = new ToolStripMenuItem(name);
                it.DropDownItems.Add("移除这台设备", null, delegate { model.RevokeTrusted(dev.Id); });
                menu.Items.Add(it);
            }
            if (model.TrustedDevices.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("全部移除", null, delegate { model.ClearTrusted(); }));
            }
            menu.Closed += delegate { BeginInvoke((Action)delegate { menu.Dispose(); }); };
            menu.Show(this, new Point((int)(x * s), (int)(y * s)));
        }

        private void DrawPeerRow(Graphics g, float y, string name, string sub, bool laptop, bool selected, bool dim,
                                 string key, Action click)
        {
            if (click != null && hoverKey == key) Fill(g, SR(34, y + 2, 332, 43), 7, Color.FromArgb(Theme.Dark ? 28 : 255, Theme.Card));
            Color c1 = dim ? Theme.Text2 : Theme.Text;
            IconComputer(g, SR(42, y + 14, 18, 18), Theme.Text2, laptop);
            TL(g, name, F(12.5f, true), c1, 72, y + 6, 250, 20);
            TL(g, sub, F(10.5f, false), Theme.Text3, 72, y + 24, 250, 16);
            if (selected) IconCheck(g, SR(344, y + 16, 14, 14), Theme.Live, 1.8f);
            using (Pen p = new Pen(Theme.Hairline, 1f)) g.DrawLine(p, 72 * s, (y + 47) * s, 368 * s, (y + 47) * s);
            if (click != null && y >= 280 - 1 && y + 47 <= 375) AddZone(32, y, 336, 47, key, null, click);
        }

        private void DrawFooter(Graphics g)
        {
            // 主按钮
            RectangleF r = SR(16, 632, 368, 38);
            bool hot = hoverKey == "primary", down = pressedKey == "primary";
            Color bg = model.Running ? Theme.Track : Theme.Live;
            if (down) bg = Color.FromArgb(200, bg);
            else if (hot) bg = ControlPaint.Light(bg, model.Running ? 0.25f : 0.12f);
            Fill(g, r, 10, bg);
            TC(g, PrimaryTitle(), F(13.5f, true), model.Running ? Theme.Text : Color.White, 16, 632, 368, 38);
            AddZone(16, 632, 368, 38, "primary", null, delegate { model.SetRunning(!model.Running); });

            // 登录自启
            RectangleF box = SR(17, 683, 14, 14);
            if (autostart)
            {
                Fill(g, box, 3.5f, Theme.Live);
                IconCheck(g, SR(19, 685, 10, 10), Color.White, 1.6f);
            }
            else
            {
                Fill(g, box, 3.5f, Theme.Card);
                Stroke(g, box, 3.5f, hoverKey == "autostart" ? Theme.Text3 : Theme.Hairline);
            }
            TL(g, "登录时自动启动", F(11.5f, false), Theme.Text2, 38, 680, 140, 20);
            AddZone(14, 678, 150, 24, "autostart", null, delegate
            {
                autostart = !autostart;
                Autostart.SetEnabled(autostart);
                autostart = Autostart.IsEnabled;
                Invalidate();
            });
            TR(g, "v" + Program.VersionText, F(10.5f, false), Theme.Text3, 284, 680, 100, 20);
        }

        private void LayoutManualBox()
        {
            bool show = model.Role == Role.Sender;
            if (manualBox.Visible != show) manualBox.Visible = show;
            if (!show) return;
            Rectangle want = Rectangle.Round(SR(72, 385, 246, 18));
            if (manualBox.Bounds != want) manualBox.Bounds = want;
        }

        private void UseManual()
        {
            string host = manualBox.Text.Trim();
            if (host.Length == 0) return;
            model.SelectManual(host);
            Invalidate();
        }

        private void ShowDeviceMenu(bool output, float x, float y)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new MenuRenderer();
            menu.ForeColor = Theme.Text;
            menu.Font = F(12, false);
            string current = output ? model.OutputId : model.CaptureId;
            ToolStripMenuItem def = new ToolStripMenuItem(output ? "跟随系统" : "系统默认播放设备", null, delegate
            {
                if (output) model.SetOutput(""); else model.SetCapture("");
            });
            def.Checked = current.Length == 0;
            menu.Items.Add(def);
            menu.Items.Add(new ToolStripSeparator());
            foreach (AudioDevice d in model.Devices)
            {
                string id = d.Id;
                ToolStripMenuItem it = new ToolStripMenuItem(d.Name, null, delegate
                {
                    if (output) model.SetOutput(id); else model.SetCapture(id);
                });
                it.Checked = id == current;
                menu.Items.Add(it);
            }
            if (!output)
            {
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem proc = new ToolStripMenuItem("按程序采集…", null,
                    delegate { PromptCaptureProcess(); });
                proc.Checked = model.CaptureProcessId > 0;
                menu.Items.Add(proc);
            }
            menu.Closed += delegate { BeginInvoke((Action)delegate { menu.Dispose(); }); };
            menu.Show(this, new Point((int)(x * s), (int)(y * s)));
        }

        // ---------- 鼠标 / 键盘 ----------

        private Zone HitTest(Point pt)
        {
            for (int i = zones.Count - 1; i >= 0; i--)
                if (zones[i].R.Contains(pt)) return zones[i];
            return null;
        }

        private bool InVolume(Point pt)
        {
            if (model.Role != Role.Receiver) return false;
            RectangleF r = volumeTrack;
            r.Inflate(9 * s, 11 * s);
            return r.Contains(pt);
        }

        private void DragVolume(int x)
        {
            if (volumeTrack.Width <= 0) return;
            model.SetVolume((x - volumeTrack.X) / volumeTrack.Width);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (draggingVolume) { DragVolume(e.X); return; }
            Zone z = HitTest(e.Location);
            string key = z != null ? z.Key : "";
            Cursor = (z != null || InVolume(e.Location)) ? Cursors.Hand : Cursors.Default;
            if (key != hoverKey)
            {
                hoverKey = key;
                tip.SetToolTip(this, z != null && z.Tip != null ? z.Tip : "");
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverKey.Length > 0) { hoverKey = ""; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            // 点空白处让输入框失焦
            if (manualBox.Focused) ActiveControl = null;
            Zone z = HitTest(e.Location);
            if (z != null)
            {
                pressedKey = z.Key;
                Invalidate();
                return;
            }
            if (InVolume(e.Location))
            {
                draggingVolume = true;
                Capture = true;
                DragVolume(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (draggingVolume)
            {
                draggingVolume = false;
                Capture = false;
                model.SaveVolume();
                return;
            }
            string key = pressedKey;
            pressedKey = "";
            if (key.Length == 0) return;
            Zone z = HitTest(e.Location);
            Invalidate();
            if (z != null && z.Key == key && z.Click != null) z.Click();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (model.Role == Role.Sender && SR(32, 280, 336, 94).Contains(e.Location))
            {
                peerScroll += e.Delta > 0 ? -1 : 1;
                Invalidate();
            }
            else if (InVolume(e.Location))
            {
                model.SetVolume(model.Volume + (e.Delta > 0 ? 0.05 : -0.05));
                model.SaveVolume();
                Invalidate();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (manualBox.Focused) return;
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                model.SetRunning(!model.Running);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.M && model.Role == Role.Receiver)
            {
                model.SetMuted(!model.Muted);
                e.Handled = true;
            }
            else if (model.Role == Role.Receiver && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right))
            {
                model.SetVolume(model.Volume + (e.KeyCode == Keys.Right ? 0.05 : -0.05));
                model.SaveVolume();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        }
    }
}
