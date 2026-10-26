using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace NetMon
{
    // 窗口位置/大小/自动刷新设置存到 exe 同级 settings.ini，下次启动还原
    static class Settings
    {
        static string Path
        {
            get
            {
                // 优先 exe 同级（便携），不可写时退回 %APPDATA%
                string beside = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.ExecutablePath), "settings.ini");
                try
                {
                    if (File.Exists(beside)) return beside;
                    using (FileStream fs = File.Create(beside)) { }
                    File.Delete(beside);
                    return beside;
                }
                catch (Exception) { }

                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetMonitor");
                Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "settings.ini");
            }
        }

        public static void Load(Form f, CheckBox auto, ComboBox interval)
        {
            try
            {
                if (!File.Exists(Path)) return;
                Dictionary<string, string> kv = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(Path))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }

                int x, y, w, h;
                if (int.TryParse(Get(kv, "x"), out x) && int.TryParse(Get(kv, "y"), out y)
                    && int.TryParse(Get(kv, "w"), out w) && int.TryParse(Get(kv, "h"), out h))
                {
                    Rectangle r = new Rectangle(x, y, w, h);
                    // 只在仍然落在某个屏幕内时还原，避免拔掉外接显示器后窗口跑到看不见的地方
                    foreach (Screen s in Screen.AllScreens)
                    {
                        if (s.WorkingArea.IntersectsWith(r))
                        {
                            f.StartPosition = FormStartPosition.Manual;
                            f.Bounds = r;
                            break;
                        }
                    }
                }

                bool on;
                if (bool.TryParse(Get(kv, "auto"), out on)) auto.Checked = on;
                int sec;
                if (int.TryParse(Get(kv, "interval"), out sec)) interval.SelectedItem = sec;
                // 旧版本存的是毫秒，读出来不在选项里就退回 2 秒
                if (interval.SelectedItem == null) interval.SelectedItem = 2;
            }
            catch (Exception) { }
        }

        public static void Save(Form f, CheckBox auto, ComboBox interval)
        {
            try
            {
                Rectangle r = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
                File.WriteAllLines(Path, new string[] {
                    "x=" + r.X, "y=" + r.Y, "w=" + r.Width, "h=" + r.Height,
                    "auto=" + auto.Checked,
                    "interval=" + (interval.SelectedItem == null ? 2 : (int)interval.SelectedItem)
                });
            }
            catch (Exception) { }
        }

        static string Get(Dictionary<string, string> kv, string key)
        {
            string v;
            return kv.TryGetValue(key, out v) ? v : null;
        }

        // 仅自检使用：删掉配置文件，让下次 Load 走默认值
        public static void ResetForTest()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch (Exception) { }
        }
    }
}
