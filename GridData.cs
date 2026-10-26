using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace NetMon
{
    partial class MainForm
    {
        ContextMenuStrip rowMenu;
        int menuRowIndex = -1;
        // 右键那一刻就把行内容固化下来：菜单打开期间定时刷新会重建所有行，
        // 到时候再用行号取值就会拿到别的进程，甚至越界（表现为点了没反应）
        int menuPid = -1;
        string menuModule;
        string menuName;
        string menuRemoteIp;

        void BuildContextMenu(DataGridView g)
        {
            rowMenu = new ContextMenuStrip();
            rowMenu.Items.Add("复制远程地址", null, delegate { ActOnMenuRow(CopyRemoteIp); });
            rowMenu.Items.Add("查询远程地址 IP 信息", null, delegate { ActOnMenuRow(QueryRemoteIp); });
            rowMenu.Items.Add(new ToolStripSeparator());
            rowMenu.Items.Add("查看进程属性", null, delegate { ActOnMenuRow(ShowProps); });
            rowMenu.Items.Add("打开文件所在位置", null, delegate { ActOnMenuRow(OpenLocation); });
            rowMenu.Items.Add(new ToolStripSeparator());
            rowMenu.Items.Add("结束进程", null, delegate { ActOnMenuRow(KillProcess); });
            rowMenu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (menuRowIndex < 0) { e.Cancel = true; return; }
                // 内核伪进程没有可操作对象，只留地址相关的两项可用
                bool can = ProcActions.CanOperate(menuPid);
                foreach (ToolStripItem it in rowMenu.Items)
                {
                    if (it is ToolStripSeparator) continue;
                    it.Enabled = can || it.Text == "复制远程地址" || it.Text == "查询远程地址 IP 信息";
                }
            };
        }

        // 右键先选中该行，菜单关闭后仍以这行为准
        void GridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;

            menuRowIndex = e.RowIndex;
            menuPid = PidOfRow(e.RowIndex);
            menuModule = ModuleOfRow(e.RowIndex);
            menuName = grid.Rows[e.RowIndex].Cells["name"].Value as string;
            menuRemoteIp = HostOf(grid.Rows[e.RowIndex].Cells["remote"].Value as string);

            grid.ClearSelection();
            grid.Rows[e.RowIndex].Selected = true;

            // e.Location 是相对单元格的坐标（不含表头/滚动），
            // 必须加上单元格在表格内的偏移再转屏幕坐标，否则菜单会贴到表格顶部。
            // 注意 PointToScreen 已经包含表头高度和滚动偏移，不能再额外加 ColumnHeadersHeight
            Rectangle cellRect = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            Point inGrid = new Point(cellRect.X + e.X, cellRect.Y + e.Y);
            rowMenu.Show(grid.PointToScreen(inGrid));
        }

        void ActOnMenuRow(Action action)
        {
            menuRowIndex = -1;
            action();
        }

        int PidOfRow(int rowIndex)
        {
            object v = grid.Rows[rowIndex].Cells["pid"].Value;
            return v == null ? -1 : Convert.ToInt32(v);
        }

        string ModuleOfRow(int rowIndex)
        {
            return grid.Rows[rowIndex].Cells["module"].Value as string;
        }

        // 从 "ip:port" 或 "[ipv6]:port" 里取出纯 IP，去掉端口和方括号
        internal static string HostOf(string addrPort)
        {
            if (string.IsNullOrEmpty(addrPort)) return "";
            if (addrPort[0] == '[')
            {
                int close = addrPort.IndexOf(']');
                return close > 0 ? addrPort.Substring(1, close - 1) : addrPort;
            }
            int colon = addrPort.LastIndexOf(':');
            return colon > 0 ? addrPort.Substring(0, colon) : addrPort;
        }

        // IP 归属地查询地址。IPv6 的冒号不编码，浏览器才能正确解析
        internal static string IpQueryUrl(string ip)
        {
            return "https://geoip.loukky.com/?ip=" + ip;
        }

        // 用默认浏览器打开 IP 归属地查询页。
        // 提权进程里 Process.Start(url) 可能拉不起默认浏览器，走 ShellExecute 更可靠
        void QueryRemoteIp()
        {
            if (!HasRemoteIp()) return;
            string url = IpQueryUrl(menuRemoteIp);
            if (!ProcActions.OpenUrl(url))
            {
                MessageBox.Show("打开浏览器失败：" + url, "查询 IP 信息",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            statusLabel.Text = "已打开 IP 查询：" + menuRemoteIp;
        }

        bool HasRemoteIp()
        {
            if (string.IsNullOrEmpty(menuRemoteIp) || menuRemoteIp == "0.0.0.0" || menuRemoteIp == "::")
            {
                MessageBox.Show("该行没有远程地址。", "查询 IP 信息",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }

        void CopyRemoteIp()
        {
            if (!HasRemoteIp()) return;
            try
            {
                Clipboard.SetText(menuRemoteIp);
                statusLabel.Text = "已复制远程地址：" + menuRemoteIp;
            }
            catch (Exception e)
            {
                MessageBox.Show("复制失败：" + e.Message, "复制远程地址",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void ShowProps()
        {
            ProcActions.ShowProperties(Handle, menuModule);
        }

        void OpenLocation()
        {
            ProcActions.OpenLocation(menuModule);
        }

        void KillProcess()
        {
            DialogResult r = MessageBox.Show(
                string.Format("确定要结束进程 {0} (PID {1}) 吗？\r\n未保存的数据可能丢失。", menuName, menuPid),
                "结束进程", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;

            string err = ProcActions.Kill(menuPid);
            if (err != null)
            {
                MessageBox.Show(err, "结束进程", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            lastHash = 0;
            LoadRows();
        }

        internal void LoadRows()
        {
            List<Conn> conns;
            try { conns = Tcp.GetAll(); }
            catch (Exception) { return; }

            // 连接没变就不动表格：每 2 秒清空重填会整屏闪一下
            int hash = Hash(conns);
            if (hash == lastHash && table.Rows.Count > 0)
            {
                statusLabel.Text = string.Format("共 {0} 条连接 · 无变化 · {1:HH:mm:ss}",
                    conns.Count, DateTime.Now);
                return;
            }
            lastHash = hash;

            // 记住滚动位置和选中行，刷新后原样恢复，不然每次都会跳回顶部
            int top = 0;
            bool hadScroll = false;
            try
            {
                if (grid.Rows.Count > 0) { top = grid.FirstDisplayedScrollingRowIndex; hadScroll = true; }
            }
            catch (Exception) { }
            string selectedKey = SelectedKey();

            pidMeta.Clear();
            procNames = null;   // 进程可能已退出/新建，快照每次刷新重建
            HashSet<int> pids = new HashSet<int>();

            table.BeginLoadData();
            try
            {
                table.Rows.Clear();
                foreach (Conn c in conns)
                {
                    Meta m = MetaFor(c.Pid);
                    pids.Add(c.Pid);
                    DataRow r = table.NewRow();
                    r["icon"] = m.Icon;
                    r["name"] = m.Name;
                    r["pid"] = c.Pid;
                    r["sig"] = m.Sig;
                    r["module"] = m.Module;
                    r["proto"] = c.Proto;
                    r["local"] = c.Local;
                    r["remote"] = c.Remote;
                    r["state"] = c.State;
                    r["company"] = m.Company;
                    table.Rows.Add(r);
                }
            }
            finally
            {
                table.EndLoadData();
            }

            RestoreSelection(selectedKey);
            if (hadScroll && grid.Rows.Count > 0)
            {
                try { grid.FirstDisplayedScrollingRowIndex = Math.Min(top, grid.Rows.Count - 1); }
                catch (Exception) { }
            }

            statusLabel.Text = string.Format("共 {0} 条连接 · {1} 个进程 · 更新于 {2:HH:mm:ss}",
                conns.Count, pids.Count, DateTime.Now);
        }

        // 只看会影响显示内容的部分，避免时间戳之类造成假变化
        internal static int Hash(List<Conn> conns)
        {
            unchecked
            {
                int h = 17;
                foreach (Conn c in conns)
                {
                    h = h * 31 + c.Pid;
                    h = h * 31 + c.Local.GetHashCode();
                    h = h * 31 + c.Remote.GetHashCode();
                    h = h * 31 + c.State.GetHashCode();
                }
                return h;
            }
        }

        internal string SelectedKey()
        {
            if (grid.CurrentRow == null) return null;
            return RowKey(grid.CurrentRow);
        }

        internal static string RowKey(DataGridViewRow row)
        {
            object[] cells = new object[] {
                row.Cells["pid"].Value, row.Cells["local"].Value, row.Cells["remote"].Value
            };
            return string.Join("|", Array.ConvertAll(cells, delegate(object o) { return o == null ? "" : o.ToString(); }));
        }

        internal ContextMenuStrip RowMenuForTest { get { return rowMenu; } }

        void RestoreSelection(string key)
        {
            if (key == null) return;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (RowKey(row) == key)
                {
                    row.Selected = true;
                    return;
                }
            }
        }

        Meta MetaFor(int pid)
        {
            Meta m;
            if (pidMeta.TryGetValue(pid, out m)) return m;

            m = new Meta();
            string path = pid == 0 ? null : Tcp.ImagePathOf(pid);

            if (path != null)
            {
                m.Module = path;
                m.Name = Path.GetFileName(path);
                m.Sig = SignatureOf(path);
                m.Company = CompanyOf(path);
                m.Icon = IconOf(path);
            }
            else
            {
                // 拿不到路径时（系统/受保护进程）退回进程快照里的 exe 名，总比"未知进程"强
                m.Name = FallbackName(pid);
                m.Sig = pid <= 4 ? "系统文件" : "未知";
            }

            pidMeta[pid] = m;
            return m;
        }

        string FallbackName(int pid)
        {
            if (procNames == null) procNames = Tcp.ProcessNames();
            string name;
            if (procNames.TryGetValue(pid, out name) && !string.IsNullOrEmpty(name))
                return name == "[System Process]" ? "System Idle Process" : name;
            if (pid == 0) return "System Idle Process";
            if (pid == 4) return "System";
            return "PID " + pid;
        }

        string CompanyOf(string path)
        {
            string co;
            if (companyCache.TryGetValue(path, out co)) return co;
            try { co = FileVersionInfo.GetVersionInfo(path).CompanyName; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(co)) co = "-";
            companyCache[path] = co;
            return co;
        }

        Image IconOf(string path)
        {
            Image img;
            if (iconCache.TryGetValue(path, out img)) return img;
            img = LoadIcon(path);
            iconCache[path] = img;
            return img;
        }

        // 签名校验涉及目录库枚举，单次约 58ms，几十个进程就是秒级卡顿。
        // 这里只返回已缓存的结果，未校验的返回"校验中"，由后台线程补齐
        string SignatureOf(string path)
        {
            string sig;
            lock (sigLock)
            {
                if (sigCache.TryGetValue(path, out sig)) return sig;
                if (sigPending.Contains(path)) return "校验中…";
                sigPending.Add(path);
            }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string result = ProcActions.SignatureText(path);
                lock (sigLock)
                {
                    sigCache[path] = result;
                    sigPending.Remove(path);
                }
                // 回填界面必须在 UI 线程
                if (IsHandleCreated && !IsDisposed)
                {
                    try { BeginInvoke((MethodInvoker)delegate { lastHash = 0; LoadRows(); }); }
                    catch (Exception) { }
                }
            });
            return "校验中…";
        }

        // 取 exe 关联的小图标；失败返回 null，单元格留空
        static Image LoadIcon(string path)
        {
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(path);
                return ico == null ? null : ico.ToBitmap();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
