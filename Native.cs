using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;

namespace NetMon
{
    class Conn
    {
        public int Pid;
        public string Proto;
        public string Local;
        public string Remote;
        public string State;
    }

    class Meta
    {
        public string Name = "-";
        public string Module = "-";
        public string Company = "-";
        public string Sig = "-";
        public Image Icon;
    }

    // 通过 iphlpapi 的 GetExtendedTcpTable 读取全系统 TCP 连接表（含所属 PID）
    static class Tcp
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        const int AF_INET = 2;
        const int AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_ALL = 5;
        const int ERROR_INSUFFICIENT_BUFFER = 122;

        [StructLayout(LayoutKind.Sequential)]
        struct Row4
        {
            public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, Pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Row6
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
            public uint LocalScope, LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
            public uint RemoteScope, RemotePort, State, Pid;
        }

        public static List<Conn> GetAll()
        {
            List<Conn> list = new List<Conn>();
            Read(AF_INET, list);
            Read(AF_INET6, list);
            // 内核返回顺序不保证稳定，不排序的话每次刷新行都在乱跳。
            // 已关闭/半关闭的连接排到最后，否则 PID 0 的 TIME_WAIT 会刷满整屏
            list.Sort(delegate(Conn a, Conn b)
            {
                int c = Noise(a).CompareTo(Noise(b));
                if (c != 0) return c;
                c = string.CompareOrdinal(a.State, b.State);
                if (c != 0) return c;
                c = a.Pid.CompareTo(b.Pid);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Local, b.Local);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Remote, b.Remote);
            });
            return list;
        }

        static int Noise(Conn c)
        {
            return c.State == "TIME_WAIT" || c.State == "CLOSE_WAIT" || c.State == "FIN_WAIT1"
                || c.State == "FIN_WAIT2" || c.State == "LAST_ACK" || c.State == "CLOSING"
                || c.State == "CLOSED" ? 1 : 0;
        }

        static void Read(int af, List<Conn> list)
        {
            int size = 0;
            int rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != ERROR_INSUFFICIENT_BUFFER || size <= 0) return;

            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                rc = GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
                if (rc != 0) return;

                int count = Marshal.ReadInt32(buf);
                long p = buf.ToInt64() + 4;

                if (af == AF_INET)
                {
                    int sz = Marshal.SizeOf(typeof(Row4));
                    for (int i = 0; i < count; i++)
                    {
                        Row4 r = (Row4)Marshal.PtrToStructure(new IntPtr(p + (long)i * sz), typeof(Row4));
                        list.Add(Make("TCP", FormatV4(r.LocalAddr), PortOf(r.LocalPort),
                                      FormatV4(r.RemoteAddr), PortOf(r.RemotePort), StateName(r.State), (int)r.Pid));
                    }
                }
                else
                {
                    int sz = Marshal.SizeOf(typeof(Row6));
                    for (int i = 0; i < count; i++)
                    {
                        Row6 r = (Row6)Marshal.PtrToStructure(new IntPtr(p + (long)i * sz), typeof(Row6));
                        list.Add(Make("TCPv6", new IPAddress(r.LocalAddr).ToString(), PortOf(r.LocalPort),
                                      new IPAddress(r.RemoteAddr).ToString(), PortOf(r.RemotePort), StateName(r.State), (int)r.Pid));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static Conn Make(string proto, string lip, int lport, string rip, int rport, string state, int pid)
        {
            Conn c = new Conn();
            c.Proto = proto;
            c.Local = Format(lip, lport);
            c.Remote = Format(rip, rport);
            c.State = state;
            c.Pid = pid;
            return c;
        }

        // IPv6 用方括号包住主机部分，与端口隔开：[2408:...:48ad]:1798
        public static string Format(string host, int port)
        {
            return host.IndexOf(':') >= 0 ? "[" + host + "]:" + port : host + ":" + port;
        }

        public static string FormatV4(uint addr)
        {
            return new IPAddress(BitConverter.GetBytes(addr)).ToString();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder sb, ref int size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcEntry
        {
            public int dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32First(IntPtr h, ref ProcEntry e);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32Next(IntPtr h, ref ProcEntry e);

        // 进程名快照：OpenProcess 被拒时（系统进程、受保护进程）还能靠这个拿到 exe 名
        public static Dictionary<int, string> ProcessNames()
        {
            Dictionary<int, string> map = new Dictionary<int, string>();
            IntPtr snap = CreateToolhelp32Snapshot(2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                ProcEntry e = new ProcEntry();
                e.dwSize = Marshal.SizeOf(typeof(ProcEntry));
                if (Process32First(snap, ref e))
                {
                    do { map[e.th32ProcessID] = e.szExeFile; }
                    while (Process32Next(snap, ref e));
                }
            }
            finally
            {
                CloseHandle(snap);
            }
            return map;
        }

        // 取进程可执行文件路径；比 Process.MainModule 更抗权限问题，失败返回 null
        public static string ImagePathOf(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static int PortOf(uint raw)
        {
            return (ushort)IPAddress.NetworkToHostOrder((short)(raw & 0xFFFF));
        }

        public static string StateName(uint s)
        {
            switch (s)
            {
                case 1: return "CLOSED";
                case 2: return "LISTENING";
                case 3: return "SYN_SENT";
                case 4: return "SYN_RECEIVED";
                case 5: return "ESTABLISHED";
                case 6: return "FIN_WAIT1";
                case 7: return "FIN_WAIT2";
                case 8: return "CLOSE_WAIT";
                case 9: return "CLOSING";
                case 10: return "LAST_ACK";
                case 11: return "TIME_WAIT";
                case 12: return "DELETE_TCB";
                default: return "UNKNOWN";
            }
        }
    }
}
