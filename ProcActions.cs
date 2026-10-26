using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetMon
{
    // 右键菜单里对进程做的三件事：看属性、打开所在位置、结束进程
    static class ProcActions
    {
        public static bool CanOperate(int pid)
        {
            return pid > 4;   // 0/4 是内核伪进程，不能也不该动
        }

        // 用系统自带的文件属性对话框，能看版本、签名、兼容性等全部信息。
        // 注意：ShellExecute 的 "properties" 动词会失败（返回 31），
        // 必须走 ShellExecuteEx + SEE_MASK_INVOKEIDLIST
        public static void ShowProperties(IntPtr owner, string path)
        {
            if (!Usable(path))
            {
                Warn(Lang.T("msg.noExe"));
                return;
            }
            try
            {
                SHELLEXECUTEINFO ei = new SHELLEXECUTEINFO();
                ei.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
                ei.fMask = SEE_MASK_INVOKEIDLIST;
                ei.hwnd = owner;
                ei.lpVerb = "properties";
                ei.lpFile = path;
                ei.lpDirectory = Path.GetDirectoryName(path);
                ei.nShow = SW_SHOWNORMAL;
                if (!ShellExecuteEx(ref ei))
                {
                    Warn(Lang.F("msg.propsFail", Marshal.GetLastWin32Error()));
                }
            }
            catch (Exception e)
            {
                Warn(Lang.F("msg.propsError", e.Message));
            }
        }

        // 打开文件所在位置。
        // 提权运行时直接启动 explorer.exe 会得到 0xC0000142（Windows 禁止提权进程
        // 拉起资源管理器），所以通过 shell COM 接口让已在运行的 explorer 自己打开。
        // COM 调用可能长时间阻塞（已实测），因此放到后台线程并设超时，绝不可卡住界面。
        public static void OpenLocation(string path)
        {
            if (!Usable(path))
            {
                Warn(Lang.T("msg.noExe"));
                return;
            }

            string dir = Path.GetDirectoryName(path);

            // 首选：借用 explorer 的令牌降权启动 /select。
            // 这是提权场景下最直接的方式：快、能精确选中文件、不依赖 COM。
            if (ShellOpenAsExplorer("/select,\"" + path + "\"")) return;

            // 次选：Shell.Application COM，让已在运行的 explorer 自己打开
            bool done = false;
            bool ok = false;

            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                try { ok = OpenViaShell(path); }
                catch (Exception) { ok = false; }
                done = true;
            });
            th.IsBackground = true;
            th.SetApartmentState(System.Threading.ApartmentState.STA);   // Shell COM 需要 STA
            th.Start();

            // 最多等 2 秒，超时就走后面的兜底方案
            done = th.Join(2000);
            if (done && ok) return;

            // 兜底一：ShellExecute 调 explorer 选中文件
            if (ShellOpen("explorer.exe", "/select,\"" + path + "\"")) return;

            // 兜底二：直接打开目录
            if (ShellOpen("explorer.exe", "\"" + dir + "\"")) return;

            Warn(Lang.T("msg.locationFail"));
        }

        // 用 explorer 进程自身的令牌启动 explorer /select。
        // 提权进程不能直接拉起资源管理器（0xC0000142），但借它的令牌可以，
        // 而且能精确选中目标文件，比 COM 更直接。
        static bool ShellOpenAsExplorer(string args)
        {
            Process[] procs = null;
            try { procs = Process.GetProcessesByName("explorer"); }
            catch (Exception) { return false; }
            if (procs == null || procs.Length == 0) return false;

            foreach (Process p in procs)
            {
                IntPtr hProc = IntPtr.Zero, hTok = IntPtr.Zero, hDup = IntPtr.Zero;
                try
                {
                    hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (hProc == IntPtr.Zero) continue;

                    if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY, out hTok))
                        continue;

                    if (!DuplicateTokenEx(hTok, TOKEN_ALL_ACCESS, IntPtr.Zero,
                            SecurityImpersonation, TokenPrimary, out hDup))
                        continue;

                    STARTUPINFO si = new STARTUPINFO();
                    si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                    PROCESS_INFORMATION pi;

                    string cmd = "explorer.exe " + args;
                    if (CreateProcessWithTokenW(hDup, 0, null, cmd, 0, IntPtr.Zero, null, ref si, out pi))
                    {
                        if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                        if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                        return true;
                    }
                }
                catch (Exception) { }
                finally
                {
                    if (hDup != IntPtr.Zero) CloseHandle(hDup);
                    if (hTok != IntPtr.Zero) CloseHandle(hTok);
                    if (hProc != IntPtr.Zero) CloseHandle(hProc);
                    p.Dispose();
                }
            }
            return false;
        }

        const uint TOKEN_ALL_ACCESS = 0xF01FF;
        const uint TOKEN_DUPLICATE = 0x0002;
        const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        const uint TOKEN_QUERY = 0x0008;
        const int SecurityImpersonation = 2;
        const int TokenPrimary = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr hProcess, uint access, out IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint access, IntPtr tokenAttributes,
            int impersonationLevel, int tokenType, out IntPtr phNewToken);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessWithTokenW(IntPtr hToken, uint logonFlags, string appName,
            string commandLine, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

        // 通过 Shell.Application COM 对象让 explorer 打开目录并选中文件。
        // 注意：ParseName 只是取到对象，并不会选中；真正选中要用窗口的 Document.SelectItem。
        static bool OpenViaShell(string path)
        {
            object shell = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return false;
                shell = Activator.CreateInstance(t);
                if (shell == null) return false;

                string dir = Path.GetDirectoryName(path);
                string file = Path.GetFileName(path);

                // Explore 会打开（或复用）该目录的窗口
                object folder = t.InvokeMember("Explore",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { dir });
                if (folder != null)
                {
                    try { Marshal.ReleaseComObject(folder); }
                    catch (Exception) { }
                }

                // 等窗口就绪后再选中。explorer 打开窗口是异步的，需要重试几次
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    System.Threading.Thread.Sleep(attempt == 0 ? 400 : 250);
                    if (SelectInOpenWindow(t, shell, dir, file)) return true;
                }

                // 选中失败也至少已经把目录打开了，返回 true 避免再弹兜底
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (shell != null)
                {
                    try { Marshal.ReleaseComObject(shell); }
                    catch (Exception) { }
                }
            }
        }

        // 在已打开的窗口里选中目标文件
        static bool SelectInOpenWindow(Type shellType, object shell, string dir, string file)
        {
            object windows = null;
            try
            {
                windows = shellType.InvokeMember("Windows",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                if (windows == null) return false;

                object countObj = windows.GetType().InvokeMember("Count",
                    System.Reflection.BindingFlags.GetProperty, null, windows, null);
                int count = Convert.ToInt32(countObj);

                for (int i = 0; i < count; i++)
                {
                    object win = null;
                    try
                    {
                        win = windows.GetType().InvokeMember("Item",
                            System.Reflection.BindingFlags.InvokeMethod, null, windows, new object[] { i });
                        if (win == null) continue;

                        object doc = win.GetType().InvokeMember("Document",
                            System.Reflection.BindingFlags.GetProperty, null, win, null);
                        if (doc == null) continue;

                        // 只处理当前目录的窗口
                        object folderObj = doc.GetType().InvokeMember("Folder",
                            System.Reflection.BindingFlags.GetProperty, null, doc, null);
                        if (folderObj == null) continue;
                        object self = folderObj.GetType().InvokeMember("Self",
                            System.Reflection.BindingFlags.GetProperty, null, folderObj, null);
                        if (self == null) continue;
                        object urlObj = self.GetType().InvokeMember("Path",
                            System.Reflection.BindingFlags.GetProperty, null, self, null);
                        string winPath = urlObj as string;
                        if (winPath == null || !string.Equals(winPath.TrimEnd('\\'), dir.TrimEnd('\\'),
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        // SelectItem(item, flags)：把该文件加入选择集
                        object item = doc.GetType().InvokeMember("ParseName",
                            System.Reflection.BindingFlags.InvokeMethod, null, doc, new object[] { file });
                        if (item == null) continue;

                        doc.GetType().InvokeMember("SelectItem",
                            System.Reflection.BindingFlags.InvokeMethod, null, doc,
                            new object[] { item, 1 /*SVSI_SELECT*/ });

                        // 选中后把窗口提到前台，否则用户可能看不到
                        try
                        {
                            win.GetType().InvokeMember("Visible",
                                System.Reflection.BindingFlags.SetProperty, null, win, new object[] { true });
                        }
                        catch (Exception) { }
                        return true;
                    }
                    catch (Exception) { }
                    finally
                    {
                        if (win != null) { try { Marshal.ReleaseComObject(win); } catch (Exception) { } }
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (windows != null) { try { Marshal.ReleaseComObject(windows); } catch (Exception) { } }
            }
        }

        // 返回 true 表示 shell 接受了请求（返回值 > 32）
        static bool ShellOpen(string file, string args)
        {
            try
            {
                IntPtr r = ShellExecute(IntPtr.Zero, "open", file, args, null, SW_SHOWNORMAL);
                return r.ToInt64() > 32;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 仅自检使用
        public static bool ShellOpenForTest(string file, string args)
        {
            return ShellOpen(file, args);
        }

        // 用默认浏览器打开 URL。提权进程里 Process.Start(url) 常常拉不起浏览器，
        // 走 ShellExecute 交给 shell 处理更可靠
        public static bool OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (ShellOpen(url, null)) return true;
            try
            {
                Process.Start(url);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 路径可用：存在且不是 "-"（系统进程取不到路径时显示为 "-"）
        static bool Usable(string path)
        {
            return !string.IsNullOrEmpty(path) && path != "-" && File.Exists(path);
        }

        // 返回 null 表示成功，否则返回错误说明。
        // 直接用 OpenProcess + TerminateProcess 按 PID 操作：
        // .NET 的 Process.Kill 依赖进程枚举，在受限环境下可能找不到目标进程
        public static string Kill(int pid)
        {
            if (!CanOperate(pid)) return Lang.T("msg.killSystemDenied");

            IntPtr h = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 87 /*ERROR_INVALID_PARAMETER*/ || err == 1168 /*ERROR_NOT_FOUND*/)
                    return null;   // 进程已经不在了，目标已达成
                return Lang.F("msg.killOpenFail", err);
            }

            try
            {
                // 先判断进程是否还活着：已退出的进程句柄仍能打开，
                // 但 TerminateProcess 会返回"拒绝访问"，容易被误报成权限问题
                uint code;
                if (GetExitCodeProcess(h, out code) && code != STILL_ACTIVE)
                    return null;   // 已经退出了，目标达成

                if (TerminateProcess(h, 1)) return null;
                int err = Marshal.GetLastWin32Error();
                if (err == 5 /*ERROR_ACCESS_DENIED*/)
                    return Lang.T("msg.killAccessDenied");
                return Lang.F("msg.killError", err);
            }
            finally
            {
                CloseHandle(h);
            }
        }

        const uint PROCESS_TERMINATE = 0x0001;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint STILL_ACTIVE = 259;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr h, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeProcess(IntPtr h, out uint exitCode);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        // 返回文案 key 而不是翻译好的字符串：签名结果会缓存，
        // 存 key 才能在切语言时直接换文字，不用重新校验一遍文件
        public static string SignatureKey(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "sig.unknown";
            return IsTrusted(path) ? "sig.signed" : "sig.unsigned";
        }

        public static string SignatureText(string path)
        {
            return Lang.T(SignatureKey(path));
        }

        // 签名分两种：内嵌在 PE 里的，和系统目录（catalog）里的。
        // Windows 自带程序大多是目录签名，只查内嵌签名会把 cmd.exe 这类误判成未签名
        static bool IsTrusted(string path)
        {
            return HasEmbeddedSignature(path) || IsInCatalog(path);
        }

        static bool HasEmbeddedSignature(string path)
        {
            IntPtr fileInfoPtr = IntPtr.Zero;
            IntPtr trustDataPtr = IntPtr.Zero;
            try
            {
                WinTrustFileInfo fileInfo = new WinTrustFileInfo();
                fileInfo.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
                fileInfo.pcwszFilePath = path;
                fileInfo.hFile = IntPtr.Zero;
                fileInfo.pgKnownSubject = IntPtr.Zero;
                fileInfoPtr = Marshal.AllocHGlobal((int)fileInfo.cbStruct);
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                WinTrustData trustData = new WinTrustData();
                trustData.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
                trustData.dwUIChoice = WTD_UI_NONE;
                trustData.fdwRevocationChecks = WTD_REVOKE_NONE;   // 不联网查吊销，避免卡顿
                trustData.dwUnionChoice = WTD_CHOICE_FILE;
                trustData.pFile = fileInfoPtr;
                trustData.dwStateAction = WTD_STATEACTION_VERIFY;
                trustData.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;
                trustDataPtr = Marshal.AllocHGlobal((int)trustData.cbStruct);
                Marshal.StructureToPtr(trustData, trustDataPtr, false);

                Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
                int rc = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPtr);
                bool ok = rc == 0;

                // 必须显式关闭状态，否则会泄漏句柄
                trustData.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(trustData, trustDataPtr, false);
                WinVerifyTrust(IntPtr.Zero, ref action, trustDataPtr);
                return ok;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (trustDataPtr != IntPtr.Zero) Marshal.FreeHGlobal(trustDataPtr);
                if (fileInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPtr);
            }
        }

        // 计算文件在系统目录库里的哈希，能枚举到目录项就算目录签名。
        // 目录库用 SHA256，必须用 CalcHashFromFileHandle2 配合 SHA256 上下文，否则哈希对不上
        static bool IsInCatalog(string path)
        {
            IntPtr hCatAdmin = IntPtr.Zero;
            IntPtr hCatInfo = IntPtr.Zero;
            try
            {
                if (!CryptCATAdminAcquireContext2(ref hCatAdmin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
                    return false;

                using (FileStream fs = File.OpenRead(path))
                {
                    IntPtr hFile = fs.SafeFileHandle.DangerousGetHandle();
                    int hashSize = 0;
                    CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile, ref hashSize, null, 0);
                    if (hashSize <= 0) return false;

                    byte[] hash = new byte[hashSize];
                    if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile, ref hashSize, hash, 0))
                        return false;

                    hCatInfo = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, hashSize, 0, IntPtr.Zero);
                    return hCatInfo != IntPtr.Zero;
                }
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (hCatInfo != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0);
                if (hCatAdmin != IntPtr.Zero) CryptCATAdminReleaseContext(hCatAdmin, 0);
            }
        }

        static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        const int WTD_UI_NONE = 2;
        const int WTD_REVOKE_NONE = 0;
        const int WTD_CHOICE_FILE = 1;
        const int WTD_STATEACTION_VERIFY = 1;
        const int WTD_STATEACTION_CLOSE = 2;
        const int WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr policyCallbackData;
            public IntPtr sipClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr data);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern bool CryptCATAdminAcquireContext2(
            ref IntPtr phCatAdmin, IntPtr pgSubsystem, string pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminCalcHashFromFileHandle2(
            IntPtr hCatAdmin, IntPtr hFile, ref int pcbHash, byte[] pbHash, uint dwFlags);

        [DllImport("wintrust.dll")]
        static extern IntPtr CryptCATAdminEnumCatalogFromHash(
            IntPtr hCatAdmin, byte[] pbHash, int cbHash, uint dwFlags, IntPtr phPrevCatInfo);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

        static void Warn(string msg)
        {
            MessageBox.Show(msg, Lang.T("dlg.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        const int SW_SHOWNORMAL = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO execInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr ShellExecute(
            IntPtr hwnd, string op, string file, string args, string dir, int show);
    }
}
