@echo off
rem Builds GlassTap.dll (taskbar transparency helper). Needs the Visual Studio (or Build Tools) C++ compiler.
rem Static CRT (/MT): no dependency on whichever vcruntime explorer happens to have loaded.
setlocal EnableDelayedExpansion
cd /d "%~dp0"

rem Find the C++ toolset: an already-open Developer prompt, else ask vswhere where Visual Studio is installed.
if defined VCINSTALLDIR goto build
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "!VSWHERE!" (
  echo Visual Studio / Build Tools with "Desktop development with C++" is required.
  exit /b 1
)
for /f "usebackq delims=" %%i in (`"!VSWHERE!" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (
  echo No C++ toolset found. Install "Desktop development with C++" in the Visual Studio Installer.
  exit /b 1
)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

:build
if not exist bin mkdir bin
cl /nologo /std:c++17 /EHsc /O2 /MT /W3 /utf-8 /permissive- /DNDEBUG /DUNICODE /D_UNICODE ^
   /LD GlassTap.cpp /Fobin\ /Febin\GlassTap.dll ^
   /link /DEF:GlassTap.def /OPT:REF /OPT:ICF windowsapp.lib ole32.lib user32.lib || exit /b 1
rem Only the DLL is needed; drop the intermediates.
del /q bin\*.obj bin\*.exp bin\*.lib 2>nul
