@echo off
rem 用系统自带的 C# 编译器构建，不需要 Visual Studio
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo 找不到 csc.exe，请确认已安装 .NET Framework 4.x
    exit /b 1
)

"%CSC%" /nologo /target:winexe /out:NetMonitor.exe /win32manifest:app.manifest ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Data.dll ^
    Native.cs Settings.cs Lang.cs ProcActions.cs MainForm.cs GridData.cs Program.cs

if errorlevel 1 (
    echo 编译失败
    exit /b 1
)

rem 语言文件随 exe 分发，缺了也能跑（内置简体中文兜底）
if not exist lang mkdir lang
xcopy /y /q lang\*.ini lang\ >nul 2>&1

echo 编译完成: NetMonitor.exe
