using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace NetMon
{
    // 主窗口只负责界面与刷新调度，取数在 Native.cs，进程操作在 ProcActions.cs
    partial class MainForm : Form
    {
        DataTable table;
        DataView view;
        internal DataGridView grid;
        internal TextBox search;
        internal Button clearButton;
        internal LoadingOverlay loadingPanel;
        internal int loadCount;   // 整表重建次数，自检用来锁住"签名回填触发重建"的回归
        Label statusLabel;
        internal CheckBox autoRefresh;
        internal ComboBox intervalBox;
        internal Button refreshButton;
        internal ComboBox langBox;
        Label unitLabel;
        Label intervalLabel;
        Label langLabel;
        Timer timer;
        ToolTip tip;
        int lastHash;
        Dictionary<int, Meta> pidMeta = new Dictionary<int, Meta>();
        Dictionary<int, string> procNames;
        Dictionary<string, string> companyCache = new Dictionary<string, string>();
        Dictionary<string, string> sigCache = new Dictionary<string, string>();
        HashSet<string> sigPending = new HashSet<string>();
        readonly object sigLock = new object();
        Dictionary<string, Image> iconCache = new Dictionary<string, Image>();

        public MainForm()
        {
            Text = Lang.T("app.title");
            ClientSize = new Size(1520, 820);
            MinimumSize = new Size(1000, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;
            KeyPreview = true;

            Lang.Load(Settings.LanguageCode);
            BuildUi();
            Settings.Load(this, autoRefresh, intervalBox);
            langBox.SelectedItem = Lang.Code;   // 未指定过语言时按系统区域选中
            ApplyLang();

            timer = new Timer();
            timer.Tick += delegate { LoadRows(); };
            autoRefresh.CheckedChanged += delegate { SyncTimer(); };
            intervalBox.SelectedIndexChanged += delegate { SyncTimer(); };
            SyncTimer();   // 按还原后的开关状态决定是否启动

            // 首屏先亮遮罩，等窗口画出来再取数。取数会阻塞 UI 约半秒，
            // 直接放在构造函数里的话用户看到的是一张边加载边跳动的空表
            Shown += delegate { BeginInitialLoad(); };

            FormClosing += delegate { Settings.Save(this, autoRefresh, intervalBox); };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F5) LoadRows();
            };
        }

        void BeginInitialLoad()
        {
            loadingPanel.Visible = true;
            loadingPanel.BringToFront();
            loadingPanel.StartSpinner();

            Timer t = new Timer();
            t.Interval = 100;   // 让遮罩和转圈先画出来，别一帧都没显示就消失
            t.Tick += delegate(object s, EventArgs e)
            {
                t.Stop();
                t.Dispose();
                LoadRows();
                loadingPanel.StopSpinner();
                loadingPanel.Visible = false;
            };
            t.Start();
        }

        int CurrentSeconds()
        {
            return intervalBox.SelectedItem == null ? 2 : (int)intervalBox.SelectedItem;
        }

        void SyncTimer()
        {
            timer.Stop();
            if (!autoRefresh.Checked) return;
            timer.Interval = CurrentSeconds() * 1000;
            timer.Start();
        }

        internal bool TimerRunning { get { return timer != null && timer.Enabled; } }
        internal int TimerInterval { get { return timer == null ? 0 : timer.Interval; } }

        void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(root);

            // 顶部工具条：右对齐排布，顺序为 搜索框 / 间隔 / 自动刷新 / 刷新按钮
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.Padding = new Padding(0, 13, 18, 0);
            bar.BackColor = Color.White;
            root.Controls.Add(bar, 0, 0);

            // 搜索框 + 内嵌清空按钮。手写坐标而不是 Dock：Dock 的布局顺序取决于
            // z-order，Fill 与 Right 混用很容易把按钮挤到左边去
            Panel searchWrap = new Panel();
            searchWrap.Size = new Size(260, 25);
            searchWrap.BorderStyle = BorderStyle.FixedSingle;
            searchWrap.BackColor = Color.White;
            searchWrap.Margin = new Padding(0);

            search = new TextBox();
            search.BorderStyle = BorderStyle.None;
            search.Font = new Font("Microsoft YaHei UI", 9F);
            search.BackColor = Color.White;
            search.Location = new Point(4, 4);
            search.Width = 228;
            search.TextChanged += delegate { ApplyFilter(); };
            // 占位文字用系统原生 cue banner，比自己塞 Label 稳（TextBox 是原生控件，
            // 子控件绘制不可靠），且获得焦点时自动消失
            search.HandleCreated += delegate { SetCue(search, "请输入关键词"); };

            clearButton = new Button();
            clearButton.Text = "✕";
            clearButton.FlatStyle = FlatStyle.Flat;
            clearButton.FlatAppearance.BorderSize = 0;
            clearButton.BackColor = Color.White;
            clearButton.ForeColor = Color.FromArgb(130, 130, 130);
            clearButton.Font = new Font("Microsoft YaHei UI", 8F);
            clearButton.Size = new Size(20, 19);
            clearButton.Location = new Point(234, 2);
            clearButton.Cursor = Cursors.Hand;
            clearButton.TabStop = false;
            clearButton.Visible = false;
            clearButton.Click += delegate { search.Clear(); search.Focus(); };
            // 悬停变深，鼠标离开恢复，给个可点的暗示
            clearButton.MouseEnter += delegate { clearButton.ForeColor = Color.FromArgb(60, 60, 60); };
            clearButton.MouseLeave += delegate { clearButton.ForeColor = Color.FromArgb(130, 130, 130); };

            search.TextChanged += delegate { clearButton.Visible = search.TextLength > 0; };
            searchWrap.Controls.Add(search);
            searchWrap.Controls.Add(clearButton);
            bar.Controls.Add(searchWrap);

            // 自动刷新间隔（秒）
            intervalBox = new ComboBox();
            intervalBox.DropDownStyle = ComboBoxStyle.DropDownList;
            intervalBox.Width = 56;
            intervalBox.Margin = new Padding(6, 0, 0, 0);
            intervalBox.Items.AddRange(new object[] { 1, 2, 3 });
            intervalBox.SelectedItem = 2;
            bar.Controls.Add(intervalBox);

            Label unit = new Label();
            unit.Text = "秒";
            unit.AutoSize = true;
            unit.ForeColor = Color.FromArgb(90, 90, 90);
            unit.Margin = new Padding(4, 4, 0, 0);
            bar.Controls.Add(unit);
            unitLabel = unit;

            Label unit2 = new Label();
            unit2.Text = "自动刷新间隔";
            unit2.AutoSize = true;
            unit2.ForeColor = Color.FromArgb(90, 90, 90);
            unit2.Margin = new Padding(6, 4, 0, 0);
            bar.Controls.Add(unit2);
            intervalLabel = unit2;

            autoRefresh = new CheckBox();
            autoRefresh.Text = "自动刷新";
            autoRefresh.AutoSize = true;
            autoRefresh.Checked = false;   // 默认不自动刷新，由用户主动开启
            autoRefresh.ForeColor = Color.FromArgb(60, 60, 60);
            autoRefresh.Margin = new Padding(14, 3, 0, 0);
            bar.Controls.Add(autoRefresh);

            // 手动刷新一次：不改变自动刷新开关状态
            refreshButton = new Button();
            refreshButton.Text = "刷新";
            refreshButton.AutoSize = false;
            refreshButton.Size = new Size(64, 26);
            refreshButton.FlatStyle = FlatStyle.System;
            refreshButton.Margin = new Padding(14, 1, 0, 0);
            refreshButton.Click += delegate
            {
                lastHash = 0;      // 强制重建，即使内容没变也给用户一次真实反馈
                LoadRows();
            };
            bar.Controls.Add(refreshButton);

            // 语言选择：放最左边（RightToLeft 流式布局里最后添加的最靠左）
            langBox = new ComboBox();
            langBox.DropDownStyle = ComboBoxStyle.DropDownList;
            langBox.Width = 92;
            langBox.Margin = new Padding(6, 0, 0, 0);
            langBox.Items.AddRange(Lang.Codes);
            langBox.SelectedIndexChanged += delegate
            {
                string code = langBox.SelectedItem as string;
                if (code == null || code == Lang.Code) return;
                Lang.Load(code);
                ApplyLang();
            };
            bar.Controls.Add(langBox);

            langLabel = new Label();
            langLabel.Text = "语言";
            langLabel.AutoSize = true;
            langLabel.ForeColor = Color.FromArgb(90, 90, 90);
            langLabel.Margin = new Padding(6, 4, 0, 0);
            bar.Controls.Add(langLabel);

            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Color.White;
            root.Controls.Add(host, 0, 1);

            grid = BuildGrid();
            grid.Dock = DockStyle.Fill;
            host.Controls.Add(grid);

            // 首屏加载遮罩：盖住还在逐渐成形的表格，避免用户看到滚动条反复伸缩。
            // 上沿让出表头高度，加载期间列名仍然可见，窗口不至于像空壳
            loadingPanel = new LoadingOverlay();
            loadingPanel.Visible = false;
            host.Controls.Add(loadingPanel);
            host.Resize += delegate { PlaceOverlay(host); };
            PlaceOverlay(host);
            loadingPanel.BringToFront();

            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Padding = new Padding(10, 0, 0, 0);
            statusLabel.ForeColor = Color.FromArgb(110, 110, 110);
            statusLabel.BackColor = Color.FromArgb(250, 250, 250);
            root.Controls.Add(statusLabel, 0, 2);
        }

        // 原生占位文字（EM_SETCUEBANNER）。比在 TextBox 里塞 Label 可靠：
        // 原生控件不保证子控件绘制，且获得焦点时 cue 会自动消失，无需额外处理
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static void SetCue(TextBox box, string text)
        {
            const int EM_SETCUEBANNER = 0x1501;
            SendMessage(box.Handle, EM_SETCUEBANNER, new IntPtr(1), text);
        }

        void PlaceOverlay(Control host)
        {
            int top = grid.ColumnHeadersVisible ? grid.ColumnHeadersHeight : 0;
            loadingPanel.SetBounds(0, top, host.Width, Math.Max(0, host.Height - top));
        }

        // 切换语言：只改文案，不动数据。列标题、菜单、状态栏、占位文字都要重刷，
        // 已经取到的连接留在表里，不重新扫描（切语言不该卡一下）
        void ApplyLang()
        {
            Text = Lang.T("app.title");
            unitLabel.Text = Lang.T("bar.seconds");
            intervalLabel.Text = Lang.T("bar.interval");
            langLabel.Text = Lang.T("bar.language");
            autoRefresh.Text = Lang.T("bar.auto");
            refreshButton.Text = Lang.T("bar.refresh");
            SetCue(search, Lang.T("search.cue"));

            grid.Columns["name"].HeaderText = Lang.T("col.name");
            grid.Columns["pid"].HeaderText = Lang.T("col.pid");
            grid.Columns["sig"].HeaderText = Lang.T("col.sig");
            grid.Columns["module"].HeaderText = Lang.T("col.module");
            grid.Columns["proto"].HeaderText = Lang.T("col.proto");
            grid.Columns["local"].HeaderText = Lang.T("col.local");
            grid.Columns["remote"].HeaderText = Lang.T("col.remote");
            grid.Columns["state"].HeaderText = Lang.T("col.state");
            grid.Columns["company"].HeaderText = Lang.T("col.company");

            // 安全状态列按当前语言最长文案定宽：英文 "Digitally signed" 比中文长得多，
            // 固定 95px 会截成 "Digitally sig…"。这里按实测文字宽度留出内边距
            string[] sigTexts = { Lang.T("sig.signed"), Lang.T("sig.unsigned"), Lang.T("sig.checking") };
            int need = 0;
            using (Graphics gg = grid.CreateGraphics())
            {
                foreach (string t in sigTexts)
                    need = Math.Max(need, TextRenderer.MeasureText(gg, t, grid.DefaultCellStyle.Font).Width);
            }
            grid.Columns["sig"].Width = Math.Max(95, need + 18);

            ApplyMenuLang();

            // 表格里的签名文字也要跟着换；进程名/路径/公司名是系统数据，不翻译。
            // 用缓存的文案 key 重新翻译，不重新校验签名（那会每个文件卡 58ms）
            RetranslateSignatures();
            statusLabel.Text = "";
            if (grid.Rows.Count > 0)
                statusLabel.Text = Lang.F("status.updated", table.Rows.Count,
                    CountPids(), DateTime.Now);

            if (loadingPanel.Visible) loadingPanel.Invalidate();
        }

        int CountPids()
        {
            HashSet<int> set = new HashSet<int>();
            foreach (DataRow r in table.Rows)
                set.Add(Convert.ToInt32(r["pid"]));
            return set.Count;
        }

        // 用缓存的签名 key 重新翻译现有行的"安全状态"。
        // 未校验完的行保持原样，等后台回填时自然用新语言
        void RetranslateSignatures()
        {
            foreach (DataRow r in table.Rows)
            {
                string path = r["module"] as string;
                if (string.IsNullOrEmpty(path) || path == "-")
                {
                    r["sig"] = Lang.T(Convert.ToInt32(r["pid"]) <= 4 ? "sig.system" : "sig.unknown");
                    continue;
                }
                string key;
                lock (sigLock)
                {
                    if (!sigCache.TryGetValue(path, out key)) key = null;
                }
                r["sig"] = key == null ? Lang.T("sig.checking") : Lang.T(key);
            }
        }

        DataGridView BuildGrid()
        {
            DataTable t = new DataTable();
            t.Columns.Add("icon", typeof(Image));
            t.Columns.Add("name", typeof(string));
            t.Columns.Add("pid", typeof(int));
            t.Columns.Add("sig", typeof(string));
            t.Columns.Add("module", typeof(string));
            t.Columns.Add("proto", typeof(string));
            t.Columns.Add("local", typeof(string));
            t.Columns.Add("remote", typeof(string));
            t.Columns.Add("state", typeof(string));
            t.Columns.Add("company", typeof(string));
            table = t;
            view = t.DefaultView;

            DataGridView g = new DataGridView();
            g.AutoGenerateColumns = false;
            g.DataSource = view;
            g.BackgroundColor = Color.White;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = Color.FromArgb(236, 236, 236);
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            g.RowHeadersVisible = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.ReadOnly = true;
            g.MultiSelect = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            g.RowTemplate.Height = 27;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersHeight = 36;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(246, 246, 246);
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(60, 60, 60);
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(236, 236, 236);
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(60, 60, 60);
            g.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            g.DefaultCellStyle.BackColor = Color.White;
            g.DefaultCellStyle.ForeColor = Color.FromArgb(40, 40, 40);
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(240, 240, 240);
            g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 30, 30);
            g.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F);
            g.DefaultCellStyle.Padding = new Padding(6, 0, 4, 0);
            SetDoubleBuffered(g, true);   // 关掉重绘闪烁

            // 图标列不显示表头，靠无竖线边框与进程名连成一体
            DataGridViewImageColumn ic = new DataGridViewImageColumn();
            ic.Name = "icon";
            ic.HeaderText = "";
            ic.Width = 30;
            ic.ImageLayout = DataGridViewImageCellLayout.Zoom;
            ic.SortMode = DataGridViewColumnSortMode.NotSortable;
            ic.DefaultCellStyle.NullValue = null;
            ic.DefaultCellStyle.Padding = new Padding(0);
            ic.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            ic.Resizable = DataGridViewTriState.False;
            g.Columns.Add(ic);
            ic.DataPropertyName = "icon";

            DataGridViewTextBoxColumn nameCol = AddCol(g, Lang.T("col.name"), "name", 140, false);
            nameCol.DefaultCellStyle.Padding = new Padding(0, 0, 4, 0);
            AddCol(g, Lang.T("col.pid"), "pid", 65, true);
            AddCol(g, Lang.T("col.sig"), "sig", 95, false);
            AddCol(g, Lang.T("col.module"), "module", 140, false);
            AddCol(g, Lang.T("col.proto"), "proto", 60, false);
            // 带方括号的完整 IPv6 实测 287px，加左右内边距留到 305 才能不截断
            AddCol(g, Lang.T("col.local"), "local", 305, false);
            AddCol(g, Lang.T("col.remote"), "remote", 305, false);
            AddCol(g, Lang.T("col.state"), "state", 105, false);
            DataGridViewTextBoxColumn co = AddCol(g, Lang.T("col.company"), "company", 160, false);
            co.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            // 地址列放不下时靠 tooltip 看全；模块路径同理
            tip = new ToolTip();
            tip.AutoPopDelay = 15000;
            g.CellToolTipTextNeeded += delegate(object s, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                DataRowView drv = g.Rows[e.RowIndex].DataBoundItem as DataRowView;
                if (drv == null) return;
                string col = g.Columns[e.ColumnIndex].Name;
                if (col == "local") e.ToolTipText = drv["local"] as string;
                else if (col == "remote") e.ToolTipText = drv["remote"] as string;
                else if (col == "module") e.ToolTipText = drv["module"] as string;
            };

            // 安全状态着色：已签名绿色，未签名红色，其余保持默认
            g.CellFormatting += delegate(object s, DataGridViewCellFormattingEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (g.Columns[e.ColumnIndex].Name != "sig") return;
                Color c = SigColor(e.Value as string);
                if (c != Color.Empty) e.CellStyle.ForeColor = c;
            };
            g.CellMouseDown += GridCellMouseDown;
            g.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                // 双击直接打开该行 exe 所在位置
                menuModule = ModuleOfRow(e.RowIndex);
                ProcActions.OpenLocation(menuModule);
            };
            BuildContextMenu(g);
            return g;
        }

        // 安全状态对应的文字颜色：已签名绿、未签名红，其他不着色。
        // 比较的是当前语言的文案，切换语言后表格里的值也跟着换，所以仍然成立
        internal static Color SigColor(string sig)
        {
            if (sig == Lang.T("sig.signed")) return Color.FromArgb(0, 140, 60);
            if (sig == Lang.T("sig.unsigned")) return Color.FromArgb(200, 30, 30);
            return Color.Empty;
        }

        // 双缓冲需要反射打开，DataGridView 没暴露这个属性
        static void SetDoubleBuffered(Control c, bool on)
        {
            PropertyInfo pi = typeof(Control).GetProperty("DoubleBuffered",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (pi != null) pi.SetValue(c, on, null);
        }

        static DataGridViewTextBoxColumn AddCol(DataGridView g, string header, string prop, int width, bool right)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.Name = prop;
            c.HeaderText = header;
            c.DataPropertyName = prop;
            c.Width = width;
            c.SortMode = DataGridViewColumnSortMode.Automatic;
            c.DefaultCellStyle.Alignment = right
                ? DataGridViewContentAlignment.MiddleRight
                : DataGridViewContentAlignment.MiddleLeft;
            g.Columns.Add(c);
            return c;
        }

        void ApplyFilter()
        {
            string q = search.Text.Trim();
            if (q.Length == 0)
            {
                view.RowFilter = "";
                return;
            }
            q = q.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");
            string like = "%" + q + "%";
            view.RowFilter = string.Format(
                "name LIKE '{0}' OR remote LIKE '{0}' OR local LIKE '{0}' OR company LIKE '{0}'", like);
        }
    }

    // 加载遮罩：白底 + 旋转圆弧 + "正在加载…"。
    // 存在的意义是把"表格一边填一边撑出滚动条"的那段过程盖住
    class LoadingOverlay : Control
    {
        Timer spin;
        float angle;

        public LoadingOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.White;
        }

        public void StartSpinner()
        {
            angle = 0;
            if (spin == null)
            {
                spin = new Timer();
                spin.Interval = 30;
                spin.Tick += delegate
                {
                    angle = (angle + 12) % 360;
                    Invalidate();
                };
            }
            spin.Start();
        }

        public void StopSpinner()
        {
            if (spin != null) spin.Stop();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const int size = 34;
            int cx = Width / 2, cy = Height / 2 - 14;
            Rectangle box = new Rectangle(cx - size / 2, cy - size / 2, size, size);

            using (Pen bg = new Pen(Color.FromArgb(232, 232, 232), 3f))
                g.DrawEllipse(bg, box);
            using (Pen fg = new Pen(Color.FromArgb(0, 120, 215), 3f))
            {
                fg.StartCap = LineCap.Round;
                fg.EndCap = LineCap.Round;
                g.DrawArc(fg, box, angle, 110);
            }

            TextRenderer.DrawText(g, Lang.T("loading"), Font,
                new Rectangle(0, cy + size / 2 + 10, Width, 24),
                Color.FromArgb(120, 120, 120),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }
    }
}
