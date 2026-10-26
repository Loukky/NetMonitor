# NetMonitor

一个仿「火绒安全分析工具」网络模块的 Windows 网络连接监视器。用 C# / WinForms 编写，单文件编译，无第三方依赖。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue.svg)]()
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.5%2B-purple.svg)]()

![界面截图](screenshot.png)

## 功能

- **实时连接列表** — 通过 `GetExtendedTcpTable` 直接读取内核 TCP 连接表，拿到每条连接的所属 PID
- **IPv4 / IPv6** — 双栈支持，IPv6 用方括号包裹主机部分（`[2408:8266::1]:443`），与端口明确区分
- **进程信息** — 进程名、完整路径、公司名、程序图标
- **安全状态** — 校验数字签名（含 Windows 目录签名），已签名绿色、未签名红色
- **自动刷新** — 可选开关，间隔 1/2/3 秒，默认关闭
- **手动刷新** — 顶部「刷新」按钮或 F5
- **关键词过滤** — 按进程名、IP、公司名实时筛选，输入框内带一键清空按钮
- **加载动画** — 首屏取数与签名校验期间显示转圈遮罩，不再看到滚动条反复伸缩
- **窗口记忆** — 记住窗口位置、大小、刷新设置

### 右键菜单

| 菜单项 | 说明 |
| --- | --- |
| 复制远程地址 | 只复制 IP，不含端口 |
| 查询远程地址 IP 信息 | 用默认浏览器打开 `geoip.loukky.com` 查询归属地 |
| 查看进程属性 | 打开系统文件属性对话框 |
| 打开文件所在位置 | 在资源管理器中定位并选中该 exe |
| 结束进程 | 二次确认后结束进程 |

## 系统要求

- Windows 10 / 11（x64）
- .NET Framework 4.5+（系统自带）
- **需要管理员权限**（读取系统进程信息、结束受保护进程所需）

## 使用

直接双击 `NetMonitor.exe`，UAC 提示点「是」。

## 从源码编译

不需要安装 Visual Studio，用系统自带的 C# 编译器即可：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /target:winexe /out:NetMonitor.exe /win32manifest:app.manifest ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Data.dll ^
  Native.cs Settings.cs ProcActions.cs MainForm.cs GridData.cs Program.cs
```

或双击 `build.bat`。

### 自检

代码内置两套断言自检，改完可以直接跑：

```bat
NetMonitor.exe --check      :: 网络层、签名、进程操作
NetMonitor.exe --check-ui   :: 界面、菜单定位、刷新行为
```

## 项目结构

| 文件 | 职责 |
| --- | --- |
| `Native.cs` | TCP 连接表读取（iphlpapi）、进程快照、IPv6 格式化 |
| `Settings.cs` | 窗口位置/大小/刷新设置的持久化 |
| `ProcActions.cs` | 进程属性、打开位置、结束进程、数字签名校验 |
| `MainForm.cs` | 界面构建、刷新调度、签名着色 |
| `GridData.cs` | 数据装载、右键菜单、缓存与过滤 |
| `Program.cs` | 程序入口与自检 |
| `app.manifest` | UAC 清单（requireAdministrator） |

## 实现说明

### 为什么用 GetExtendedTcpTable 而不是 netstat

`netstat` 输出是文本，要正则解析，慢且容易出错。`GetExtendedTcpTable` 直接返回结构化的连接表，含 `OwningPid`，一次调用拿到全部连接。

### 为什么结束进程不用 Process.Kill()

`.NET` 的 `Process.Kill()` 依赖进程枚举。在受限环境下 `GetProcessById` 可能直接抛 `ArgumentException`（进程明明还在），会被误判为「进程已退出」。所以改用 `OpenProcess` + `TerminateProcess` 按 PID 直接操作，并先用 `GetExitCodeProcess` 判断进程是否还活着。

### 签名校验为什么不能用 X509Certificate

`X509Certificate.CreateFromSignedFile` 只读 PE 文件内嵌的签名，而 Windows 自带程序绝大多数是**目录签名（catalog）**，会被全部误判成「未签名」。

正确做法是双路径：
1. `WinVerifyTrust` 校验内嵌签名
2. `CryptCATAdmin*` 系列 API 查询目录库成员（注意目录库用 SHA256，必须用 `CryptCATAdminCalcHashFromFileHandle2` 配合 `CryptCATAdminAcquireContext2`）

签名校验单次约 58ms，几十个进程会卡住界面，所以放在后台线程异步校验，先显示「校验中…」再回填。

### 提权后怎么打开资源管理器

Windows 禁止提权进程直接启动 `explorer.exe`（会得到 `0xC0000142`）。三种绕法依次尝试：

1. 借用 explorer 进程的令牌（`OpenProcessToken` + `DuplicateTokenEx` + `CreateProcessWithTokenW`）降权启动 `explorer.exe /select,路径`
2. `Shell.Application` COM 接口让已在运行的 explorer 自己打开，用 `Document.SelectItem` 选中文件
3. `ShellExecute` 兜底

### 刷新为什么不会闪烁

三层处理：
1. 连接表按「噪声分组 → 状态 → PID → 地址」排序（内核返回顺序不固定，不排序会乱跳）
2. 内容 hash 未变时跳过重建
3. DataGridView 开启双缓冲

同时记住滚动位置和选中行，刷新后原样恢复。

### 滚动条为什么不再反复伸缩

首屏几十个进程的签名要异步校验，每个校验完成后都要把结果写回界面。早期实现是每次写回都 `lastHash = 0` 后整表重建——几十个文件就是几十次重建，滚动条跟着一会儿变长一会儿变短。

现在签名结果直接改 `DataTable` 里对应的那一行（`ApplySignature`），不碰行数、不碰滚动位置。首屏加载期间另有一层转圈遮罩盖住表格，等取数完成再一次性揭开。

自检里的「首屏只重建一次」断言锁的就是这条：整表重建次数必须为 1。

## 已知限制

- 普通权限下部分系统进程会显示为「PID xxxx」，这是 Windows 的进程访问限制，以管理员运行即可看到全部
- 只监测 TCP，不含 UDP
- 签名校验首次加载约需数秒（后台进行，不阻塞界面）

## 许可

MIT
