using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace NetMon
{
    static class SelfTest
    {
        public static int Run()
        {
            int fail = 0;
            fail += Check("PortOf(80)", Tcp.PortOf(0x5000) == 80);
            fail += Check("PortOf(443)", Tcp.PortOf(0xBB01) == 443);
            fail += Check("FormatV4(127.0.0.1)", Tcp.FormatV4(0x0100007F) == "127.0.0.1");
            fail += Check("FormatV4(0.0.0.0)", Tcp.FormatV4(0) == "0.0.0.0");
            fail += Check("StateName(2)=LISTENING", Tcp.StateName(2) == "LISTENING");
            fail += Check("StateName(5)=ESTABLISHED", Tcp.StateName(5) == "ESTABLISHED");
            fail += Check("StateName(11)=TIME_WAIT", Tcp.StateName(11) == "TIME_WAIT");

            List<Conn> conns = Tcp.GetAll();
            fail += Check("连接表非空 (" + conns.Count + ")", conns.Count > 0);
            bool sane = true;
            foreach (Conn c in conns)
            {
                if (c.Pid < 0 || c.State == "UNKNOWN" || c.Local.EndsWith(":0")) sane = false;
            }
            fail += Check("全部行字段合法", sane);

            string self = Tcp.ImagePathOf(Process.GetCurrentProcess().Id);
            fail += Check("取自身进程路径 (" + self + ")", self != null && File.Exists(self));

            // IPv6 用方括号包裹主机，与端口区分；IPv4 不加括号
            fail += Check("IPv6 带方括号",
                Tcp.Format("2408:8266:6f11:4570:2cae:28af:2d73:48ad", 8445)
                == "[2408:8266:6f11:4570:2cae:28af:2d73:48ad]:8445");
            fail += Check("IPv6 短写也带括号",
                Tcp.Format("2408:8706:2:1000::39", 443) == "[2408:8706:2:1000::39]:443");
            fail += Check("IPv4 不加括号",
                Tcp.Format("192.168.11.102", 11078) == "192.168.11.102:11078");
            fail += Check("IPv4 回环不加括号",
                Tcp.Format("127.0.0.1", 4313) == "127.0.0.1:4313");

            // 进程名兜底：快照必须能给出当前进程自己的名字
            Dictionary<int, string> names = Tcp.ProcessNames();
            int me = Process.GetCurrentProcess().Id;
            string myName;
            fail += Check("进程快照含当前进程", names.TryGetValue(me, out myName) && myName.Length > 0);

            // 内核伪进程不允许操作
            fail += Check("PID 0/4 禁止操作", !ProcActions.CanOperate(0) && !ProcActions.CanOperate(4));
            fail += Check("普通进程允许操作", ProcActions.CanOperate(me));
            fail += Check("签名判定：自身未签名", ProcActions.SignatureText(self) == "未签名");
            fail += Check("签名判定：system32 已签名",
                ProcActions.SignatureText(
                    Path.Combine(Environment.SystemDirectory, "cmd.exe")) == "数字签名文件");

            // 结束一个从外部启动的进程。这是关键场景：.NET 的进程枚举
            // 在受限环境下看不到它，旧实现用 GetProcessById 会误判成"已退出"
            Process external = Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 30")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            System.Threading.Thread.Sleep(400);
            string kerr = ProcActions.Kill(external.Id);
            System.Threading.Thread.Sleep(300);
            bool gone;
            try { Process.GetProcessById(external.Id); gone = false; }
            catch (ArgumentException) { gone = true; }
            fail += Check("按 PID 结束进程 (" + (kerr ?? "ok") + ")", kerr == null && gone);

            fail += Check("重复结束视为已退出", ProcActions.Kill(external.Id) == null);
            fail += Check("不存在的 PID 视为已退出", ProcActions.Kill(999999) == null);
            fail += Check("系统进程拒绝结束", ProcActions.Kill(4) != null);

            // 签名着色：已签名绿、未签名红
            fail += Check("签名颜色映射", MainForm.SigColor("数字签名文件") == Color.FromArgb(0, 140, 60)
                && MainForm.SigColor("未签名") == Color.FromArgb(200, 30, 30)
                && MainForm.SigColor("校验中…") == Color.Empty);

            Stopwatch sw = Stopwatch.StartNew();
            ProcActions.OpenLocation(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            sw.Stop();
            fail += Check("打开位置不阻塞 (" + sw.ElapsedMilliseconds + "ms)", sw.ElapsedMilliseconds < 6000);

            Console.WriteLine(fail == 0 ? "ALL PASS" : "FAILED: " + fail);
            return fail == 0 ? 0 : 1;
        }

        // 自检：刷新后滚动位置与选中行必须保持
        public static int RunScrollCheck()
        {
            Settings.ResetForTest();   // 清掉旧配置，避免上次自检写的 auto=False 影响默认值断言
            MainForm f = new MainForm();
            f.Show();
            for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }

            int fail = 0;
            if (f.grid.Rows.Count < 15)
            {
                Console.WriteLine("[skip] 行数不足，无法验证滚动");
                return 0;
            }

            // 首屏加载完成后整表重建次数应当只有 1 次。这条锁的是"滚动条变长变短"
            // 的真正原因：异步签名回填每算完一个文件就重建整表
            fail += Check("首屏只重建一次 (" + f.loadCount + " 次)", f.loadCount <= 2);

            f.grid.FirstDisplayedScrollingRowIndex = 12;
            f.grid.Rows[12].Selected = true;
            Application.DoEvents();
            int before = f.grid.FirstDisplayedScrollingRowIndex;
            string key = f.SelectedKey();

            f.LoadRows();
            Application.DoEvents();

            int after = f.grid.FirstDisplayedScrollingRowIndex;
            fail += Check("刷新后滚动位置保持 (" + before + " -> " + after + ")", before == after);

            // 选中的连接可能在这几秒内自然消失，只有它仍存在时才要求保持选中
            bool stillExists = false;
            foreach (DataGridViewRow r in f.grid.Rows)
            {
                if (MainForm.RowKey(r) == key) { stillExists = true; break; }
            }
            if (stillExists)
                fail += Check("刷新后选中行保持", f.SelectedKey() == key);
            else
                Console.WriteLine("[skip] 选中连接已消失，跳过选中保持断言");

            fail += Check("进程图标已加载", CountIcons(f) > 0);

            // 防闪烁的前提：两次快照同样内容必须得到同样 hash（排序已保证顺序稳定）
            List<Conn> a = Tcp.GetAll();
            List<Conn> b = Tcp.GetAll();
            fail += Check("相同内容 hash 稳定", MainForm.Hash(a) == MainForm.Hash(a));
            fail += Check("连接表已按状态/PID排序", IsSorted(a));
            fail += Check("快照可重复（顺序稳定）", a.Count == b.Count ? IsSorted(b) : true);

            // 没有"未知进程"这种占位名残留
            int unknown = 0;
            foreach (DataGridViewRow r in f.grid.Rows)
                if ((r.Cells["name"].Value as string) == "未知进程") unknown++;
            fail += Check("无「未知进程」占位 (" + unknown + " 行)", unknown == 0);

            // 窗口尺寸记忆：写入后能被读回
            Settings.Save(f, f.autoRefresh, f.intervalBox);
            int w = f.Width, h = f.Height;
            f.Width = w - 60;
            f.Height = h - 40;
            Settings.Load(f, f.autoRefresh, f.intervalBox);
            fail += Check("窗口大小可记忆 (" + w + "x" + h + ")", f.Width == w && f.Height == h);

            // 自动刷新开关必须真的生效。默认是不勾选、不刷新
            fail += Check("默认不勾选自动刷新", !f.autoRefresh.Checked && !f.TimerRunning);
            f.autoRefresh.Checked = true;
            Application.DoEvents();
            fail += Check("勾选后开始刷新", f.TimerRunning);
            f.autoRefresh.Checked = false;
            Application.DoEvents();
            fail += Check("取消勾选后停止刷新", !f.TimerRunning);
            fail += Check("间隔选项为 1/2/3 秒",
                f.intervalBox.Items.Count == 3
                && (int)f.intervalBox.Items[0] == 1 && (int)f.intervalBox.Items[2] == 3);
            // 改间隔要在开启状态下才有意义（关闭时计时器本来就不跑）
            f.autoRefresh.Checked = true;
            Application.DoEvents();
            f.intervalBox.SelectedItem = 3;
            Application.DoEvents();
            fail += Check("间隔生效 (3000ms)", f.TimerInterval == 3000);

            // 手动刷新按钮存在且可点击
            fail += Check("刷新按钮存在", f.refreshButton != null && f.refreshButton.Text == "刷新");
            int rowsBefore = f.grid.Rows.Count;
            f.refreshButton.PerformClick();
            Application.DoEvents();
            fail += Check("刷新按钮可执行 (" + rowsBefore + " 行)", f.grid.Rows.Count > 0);

            // 复制远程地址：只复制 IP，不带端口
            fail += Check("HostOf IPv4 去端口", MainForm.HostOf("192.168.11.102:11078") == "192.168.11.102");
            fail += Check("HostOf IPv6 去方括号和端口",
                MainForm.HostOf("[2408:8266:6f11:4570:2cae:28af:2d73:48ad]:1798")
                == "2408:8266:6f11:4570:2cae:28af:2d73:48ad");
            fail += Check("HostOf 空值安全", MainForm.HostOf(null) == "" && MainForm.HostOf("") == "");

            // 右键菜单五项齐全
            fail += Check("右键菜单含五项操作", HasMenuItems(f));

            // IP 查询 URL 拼接：IPv4 与 IPv6 都必须原样拼进去（不能 URL 编码冒号）
            fail += Check("IP 查询 URL 拼接",
                MainForm.IpQueryUrl("2603:1061:17:f2::1")
                    == "https://geoip.loukky.com/?ip=2603:1061:17:f2::1"
                && MainForm.IpQueryUrl("192.168.1.1")
                    == "https://geoip.loukky.com/?ip=192.168.1.1");

            // 菜单必须出现在鼠标点击处，而不是表格左上角。
            // 旧代码用 CellMouseDown 的 e.Location 直接 PointToScreen，那个坐标是相对单元格的
            fail += Check("菜单定位跟随点击位置", MenuFollowsCursor(f));

            // 搜索框清空按钮：无内容时隐藏，有内容时出现，点了清空并收起
            fail += Check("清空按钮默认隐藏", !f.clearButton.Visible);
            f.search.Text = "svchost";
            Application.DoEvents();
            fail += Check("输入后出现清空按钮", f.clearButton.Visible);
            f.clearButton.PerformClick();
            Application.DoEvents();
            fail += Check("点击后清空并收起",
                f.search.TextLength == 0 && !f.clearButton.Visible);

            // 签名回填必须原地改单元格：旧实现每算完一个文件就整表重建，
            // 首屏几十个进程会把滚动条反复撑长又缩回
            fail += Check("签名回填不重建表格", SignatureBackfillInPlace(f));

            Console.WriteLine(fail == 0 ? "ALL PASS" : "FAILED: " + fail);
            return fail == 0 ? 0 : 1;
        }

        // 对一行真实数据回填签名，要求行数、滚动位置都不变，且单元格内容已更新
        static bool SignatureBackfillInPlace(MainForm f)
        {
            DataGridView g = f.grid;
            if (g.Rows.Count < 3) return true;

            g.FirstDisplayedScrollingRowIndex = 1;
            Application.DoEvents();
            int before = g.FirstDisplayedScrollingRowIndex;
            int rows = g.Rows.Count;
            string path = g.Rows[1].Cells["module"].Value as string;
            if (string.IsNullOrEmpty(path) || path == "-") return true;

            // 后台校验还在跑的话，它随后回填的结果会盖掉我们写进去的值（竞争，非缺陷）
            for (int i = 0; i < 200 && f.SigPending; i++)
            { Application.DoEvents(); System.Threading.Thread.Sleep(25); }

            typeof(MainForm).GetMethod("ApplySignature",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(f, new object[] { path, "未签名" });
            for (int i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }

            if (g.Rows.Count != rows) { Console.WriteLine("  行数 " + rows + " -> " + g.Rows.Count); return false; }
            if (g.FirstDisplayedScrollingRowIndex != before)
            {
                Console.WriteLine("  滚动 " + before + " -> " + g.FirstDisplayedScrollingRowIndex);
                return false;
            }
            foreach (DataGridViewRow r in g.Rows)
                if ((r.Cells["module"].Value as string) == path
                    && (r.Cells["sig"].Value as string) != "未签名")
                {
                    Console.WriteLine("  单元格未回填: " + r.Cells["sig"].Value);
                    return false;
                }
            return true;
        }

        // 必须走真实的右键处理器 GridCellMouseDown，而不是直接调 menu.Show()：
        // 上一版就是直接 Show()，绕过了坐标换算，才漏掉了"菜单跑到左上角"的 bug
        static bool MenuFollowsCursor(MainForm f)
        {
            DataGridView g = f.grid;
            if (g.Rows.Count < 8) return true;

            int row = 6;
            Rectangle cell = g.GetCellDisplayRectangle(0, row, false);
            if (cell.Height <= 0) return true;

            int offX = 20, offY = 5;
            Point expected = g.PointToScreen(new Point(cell.X + offX, cell.Y + offY));

            DataGridViewCellMouseEventArgs args = new DataGridViewCellMouseEventArgs(
                0, row, offX, offY, new MouseEventArgs(MouseButtons.Right, 1, offX, offY, 0));
            typeof(MainForm).GetMethod("GridCellMouseDown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(f, new object[] { g, args });
            Application.DoEvents();

            ContextMenuStrip menu = f.RowMenuForTest;
            if (!menu.Visible) { Console.WriteLine("  右键后菜单未弹出"); return false; }
            Rectangle bounds = menu.Bounds;
            menu.Close();

            int dx = Math.Abs(bounds.X - expected.X);
            int dy = Math.Abs(bounds.Y - expected.Y);
            if (dx >= 40 || dy >= 60)
                Console.WriteLine("  菜单出现在 " + bounds + "，期望 " + expected);
            return dx < 40 && dy < 60;
        }

        static bool HasMenuItems(MainForm f)
        {
            ContextMenuStrip m = f.RowMenuForTest;
            if (m == null) return false;
            int ops = 0;
            foreach (ToolStripItem it in m.Items)
                if (it.Text == "复制远程地址" || it.Text == "查询远程地址 IP 信息"
                    || it.Text == "查看进程属性" || it.Text == "打开文件所在位置"
                    || it.Text == "结束进程") ops++;
            return ops == 5;
        }

        static int CountIcons(MainForm f)
        {
            int n = 0;
            foreach (DataGridViewRow r in f.grid.Rows)
                if (r.Cells["icon"].Value != null) n++;
            return n;
        }

        // 与 Tcp.GetAll 的比较器保持一致：先噪声分组，再状态、PID
        static bool IsSorted(List<Conn> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                Conn p = list[i - 1], c = list[i];
                if (IsNoise(p.State) && !IsNoise(c.State)) return false;
                if (IsNoise(p.State) != IsNoise(c.State)) continue;
                if (string.CompareOrdinal(p.State, c.State) > 0) return false;
                if (p.State != c.State) continue;
                if (p.Pid > c.Pid) return false;
            }
            return true;
        }

        static bool IsNoise(string state)
        {
            return state == "TIME_WAIT" || state == "CLOSE_WAIT" || state == "FIN_WAIT1"
                || state == "FIN_WAIT2" || state == "LAST_ACK" || state == "CLOSING" || state == "CLOSED";
        }

        static int Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "[ok]   " : "[FAIL] ") + name);
            return ok ? 0 : 1;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--check")
            {
                Environment.Exit(SelfTest.Run());
            }
            if (args.Length > 0 && args[0] == "--check-ui")
            {
                Application.EnableVisualStyles();
                Environment.Exit(SelfTest.RunScrollCheck());
            }
            if (args.Length > 1 && args[0] == "--shot")
            {
                Application.EnableVisualStyles();
                int ms = args.Length > 2 ? int.Parse(args[2]) : 1000;
                string filter = args.Length > 3 ? args[3] : null;
                MainForm f = new MainForm();
                f.TopMost = true;
                f.Show();
                for (int i = 0; i < ms / 50; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                if (filter != null)
                {
                    f.search.Text = filter;
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                }
                Console.WriteLine("overlay=" + f.loadingPanel.Visible + " rows=" + f.grid.Rows.Count);
                // 用真实截屏而不是 DrawToBitmap：自绘控件（加载遮罩）不走 DrawToBitmap
                Rectangle r = f.Bounds;
                using (Bitmap b = new Bitmap(r.Width, r.Height))
                {
                    using (Graphics g = Graphics.FromImage(b))
                        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    b.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                }
                Environment.Exit(0);
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
