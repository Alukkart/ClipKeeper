@echo off
rem The README GIF (docs\screenshots\<lang>\showcase.gif) from the current interface, in both languages.
rem Builds ClipKeeper.test.exe and runs it with --showcase: real clips from D:\Sources, covers, settings.json; needs ffmpeg\.
rem The slides stay in preview\showcase-<lang>\ to look at.
cd /d "%~dp0"
call "%~dp0build.cmd" ClipKeeper.test.exe
if errorlevel 1 exit /b 1
for %%l in (en ru) do (
  if exist preview\showcase-%%l\showcase.gif del preview\showcase-%%l\showcase.gif
  start "" /wait "%~dp0ClipKeeper.test.exe" --showcase preview\showcase-%%l --lang %%l
  if not exist preview\showcase-%%l\showcase.gif (
    echo FAILED: %%l, see preview\showcase-%%l\showcase-error.txt
    exit /b 1
  )
  copy /y preview\showcase-%%l\showcase.gif docs\screenshots\%%l\showcase.gif >nul
  echo OK: docs\screenshots\%%l\showcase.gif
)
