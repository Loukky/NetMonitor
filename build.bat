@echo off
rem 用系统自带的 C# 编译器构建，不需要 Visual Studio
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo 找不到 csc.exe，请确认已安装 .NET Framework 4.x
    exit /b 1
)

rem 版本号的唯一来源，格式 MAJOR.MINOR.PATCH
set VER=
for /f "usebackq delims=" %%v in ("version.txt") do if not defined VER set VER=%%v
if not defined VER set VER=0.0.0
for /f "tokens=1,2,3 delims=." %%a in ("%VER%") do set V4=%%a.%%b.%%c.0

rem 生成程序集版本信息，编译完即删，不进仓库
> _version.cs echo using System.Reflection;
>> _version.cs echo [assembly: AssemblyVersion("%V4%")]
>> _version.cs echo [assembly: AssemblyFileVersion("%V4%")]
>> _version.cs echo [assembly: AssemblyProduct("NetMonitor")]
>> _version.cs echo [assembly: AssemblyTitle("NetMonitor")]

"%CSC%" /nologo /target:winexe /out:NetMonitor.exe /win32manifest:app.manifest /win32icon:app.ico ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Data.dll ^
    Native.cs Settings.cs Lang.cs ProcActions.cs MainForm.cs GridData.cs Program.cs _version.cs
set RC=%ERRORLEVEL%
del _version.cs 2>nul

if %RC% neq 0 (
    echo 编译失败
    exit /b 1
)

rem 语言文件随 exe 分发，缺了也能跑（内置简体中文兜底）
if not exist lang mkdir lang
xcopy /y /q lang\*.ini lang\ >nul 2>&1

echo 编译完成: NetMonitor.exe  (v%VER%)
