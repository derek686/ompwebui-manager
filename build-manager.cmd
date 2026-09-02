@echo off
setlocal
rem =====================================================================
rem  Build omp-web-manager.exe - single-file WinForms GUI manager.
rem  Uses the .NET Framework compiler shipped with Windows (no SDK needed).
rem  Compiles omp-web-manager.cs (same folder) -> omp-web-manager.exe.
rem  Usage:  build-manager.cmd
rem =====================================================================

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] .NET Framework csc.exe not found.
    exit /b 1
)

set "FRAMEWORK=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FRAMEWORK%\System.Windows.Forms.dll" set "FRAMEWORK=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

set "SRC=%~dp0omp-web-manager.cs"
set "OUT=%~dp0omp-web-manager.exe"
set "REFS=/r:%FRAMEWORK%\System.Windows.Forms.dll /r:%FRAMEWORK%\System.Drawing.dll /r:%FRAMEWORK%\System.dll"
set "ICONARG="
if exist "%~dp0public\omp-web.ico" set "ICONARG=/win32icon:%~dp0public\omp-web.ico"

"%CSC%" /nologo /target:winexe /optimize /platform:anycpu %REFS% %ICONARG% /out:"%OUT%" "%SRC%"
if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)
echo [OK] %OUT%
