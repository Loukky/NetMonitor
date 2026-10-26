using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace NetMon
{
    // 界面文案。内置一份简体中文兜底，再用 exe 同级 lang/<代码>.ini 覆盖，
    // 所以缺词条时退回中文而不是露出 key，用户也能自己改 ini 增删文案
    static class Lang
    {
        public const string Simplified = "zh-CN";
        public const string Traditional = "zh-TW";
        public const string English = "en-US";

        // 下拉框显示用，各自用母语，不翻译
        public static readonly string[] Codes = { Simplified, Traditional, English };

        static readonly Dictionary<string, string> map = new Dictionary<string, string>(Builtin());
        public static string Code { get; private set; }

        public static void Load(string code)
        {
            if (Array.IndexOf(Codes, code) < 0) code = Detect();
            Code = code;

            map.Clear();
            foreach (KeyValuePair<string, string> kv in Builtin()) map[kv.Key] = kv.Value;
            Merge("lang/" + code + ".ini");
        }

        // 没在配置里指定过语言时按系统区域猜：繁中地区给繁中，其他中文给简中，其余英文
        public static string Detect()
        {
            string n = CultureInfo.CurrentUICulture.Name;
            if (n.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (n.IndexOf("TW", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("HK", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("MO", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Traditional;
                return Simplified;
            }
            return English;
        }

        static void Merge(string relative)
        {
            try
            {
                string path = Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath), relative);
                if (!File.Exists(path)) return;

                string section = "";
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                    // [section] 只是分组前缀，真正生效的 key 是 "section.name"
                    if (line[0] == '[')
                    {
                        int end = line.IndexOf(']');
                        section = end > 1 ? line.Substring(1, end - 1).Trim() : "";
                        continue;
                    }

                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string key = line.Substring(0, i).Trim();
                    if (section.Length > 0) key = section + "." + key;
                    // ini 里用 \n 表示换行，单行配置文件放不下多行文案
                    string val = line.Substring(i + 1).Trim().Replace("\\n", "\r\n");
                    map[key] = val;
                }
            }
            catch (Exception) { }
        }

        public static string T(string key)
        {
            string v;
            return map.TryGetValue(key, out v) ? v : key;
        }

        public static string F(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch (FormatException) { return T(key); }
        }

        static Dictionary<string, string> Builtin()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            d["app.title"] = "安全分析工具";

            d["bar.seconds"] = "秒";
            d["bar.interval"] = "自动刷新间隔";
            d["bar.auto"] = "自动刷新";
            d["bar.refresh"] = "刷新";
            d["bar.language"] = "语言";

            d["search.cue"] = "请输入关键词";
            d["loading"] = "正在加载…";

            d["col.name"] = "进程名";
            d["col.pid"] = "进程ID";
            d["col.sig"] = "安全状态";
            d["col.module"] = "模块";
            d["col.proto"] = "协议";
            d["col.local"] = "本地地址";
            d["col.remote"] = "远程地址";
            d["col.state"] = "状态";
            d["col.company"] = "公司名";

            d["status.nochange"] = "共 {0} 条连接 · 无变化 · {1:HH:mm:ss}";
            d["status.updated"] = "共 {0} 条连接 · {1} 个进程 · 更新于 {2:HH:mm:ss}";
            d["status.copied"] = "已复制远程地址：{0}";
            d["status.ipquery"] = "已打开 IP 查询：{0}";

            d["menu.copyip"] = "复制远程地址";
            d["menu.queryip"] = "查询远程地址 IP 信息";
            d["menu.props"] = "查看进程属性";
            d["menu.location"] = "打开文件所在位置";
            d["menu.kill"] = "结束进程";

            d["sig.signed"] = "数字签名文件";
            d["sig.unsigned"] = "未签名";
            d["sig.checking"] = "校验中…";
            d["sig.unknown"] = "未知";
            d["sig.system"] = "系统文件";

            d["name.idle"] = "System Idle Process";
            d["name.system"] = "System";

            d["dlg.title"] = "提示";

            d["msg.noRemote"] = "该行没有远程地址。";
            d["msg.queryTitle"] = "查询 IP 信息";
            d["msg.openBrowserFail"] = "打开浏览器失败：{0}";
            d["msg.copyTitle"] = "复制远程地址";
            d["msg.copyFail"] = "复制失败：{0}";
            d["msg.killTitle"] = "结束进程";
            d["msg.killConfirm"] = "确定要结束进程 {0} (PID {1}) 吗？\\n未保存的数据可能丢失。";

            d["msg.noExe"] = "找不到该进程的可执行文件。\\n系统进程或受保护进程通常取不到路径。";
            d["msg.propsFail"] = "无法打开属性对话框（错误码 {0}）。";
            d["msg.propsError"] = "打开属性失败：{0}";
            d["msg.locationFail"] = "打开位置失败。\\n如果程序以管理员身份运行，系统会禁止直接启动资源管理器；"
                + "本程序已尝试通过 shell 接口打开，仍失败时请检查资源管理器是否正常运行。";
            d["msg.killSystemDenied"] = "系统进程不允许结束。";
            d["msg.killOpenFail"] = "结束失败：无法打开进程（错误码 {0}）。\\n\\n系统进程、受保护进程或其他用户的进程需要以管理员身份运行本程序。";
            d["msg.killAccessDenied"] = "结束失败：拒绝访问。\\n\\n该进程受保护或属于其他用户，请以管理员身份运行本程序。";
            d["msg.killError"] = "结束失败：错误码 {0}。";
            return d;
        }
    }
}
