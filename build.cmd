@echo off
rem Builds ClipKeeper.exe with the C# compiler built into Windows (.NET Framework 4.8). Nothing to install.
rem Optional argument: output file name (default ClipKeeper.exe), e.g. build.cmd ClipKeeper.test.exe
cd /d "%~dp0"
set OUT=ClipKeeper.exe
if not "%~1"=="" set OUT=%~1
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set ICON=
if exist src\app.ico set ICON=/win32icon:src\app.ico
"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /out:%OUT% %ICON% ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  /r:System.Security.dll /r:System.Xaml.dll /r:Microsoft.VisualBasic.dll /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\PresentationFramework.dll" ^
  /r:"%FW%\WPF\WindowsBase.dll" ^
  /resource:ui\Styles.xaml,DeviceGuard.Styles.xaml /resource:ui\MainWindow.xaml,DeviceGuard.MainWindow.xaml /resource:ui\TrimWindow.xaml,DeviceGuard.TrimWindow.xaml ^
  src\*.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo OK: %OUT%
