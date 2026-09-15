@echo off
rem Build the DSH MSYS2 agent-profile installer.
rem
rem Compiles with the .NET Framework csc.exe that ships with Windows, so this
rem needs no .NET SDK and no network access.
rem
rem Output: build\msys2-installer.exe (+ the shim source it compiles at install
rem time), and build\msys2-tests.exe for the test harness.
rem
rem Usage:  build.cmd [installer|tests|all]     (default: all)
rem
rem Note: this file stays pure ASCII. Non-ASCII bytes in a batch file are
rem decoded with the console's OEM codepage, which can turn a punctuation
rem character into something cmd.exe tries to execute.

setlocal EnableExtensions
set "HERE=%~dp0"
set "SRC=%HERE%src"
set "OUT=%HERE%build"

set "CSC="
for %%V in ("%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe") do if exist %%V set "CSC=%%~V"
if not defined CSC (
  for /f "delims=" %%V in ('dir /b /o-n "%SystemRoot%\Microsoft.NET\Framework64\v*" 2^>nul') do (
    if not defined CSC if exist "%SystemRoot%\Microsoft.NET\Framework64\%%V\csc.exe" set "CSC=%SystemRoot%\Microsoft.NET\Framework64\%%V\csc.exe"
  )
)
if not defined CSC (
  echo [build] ERROR: no csc.exe found under %SystemRoot%\Microsoft.NET\Framework64
  exit /b 1
)

if not exist "%OUT%" mkdir "%OUT%"

set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=all"

if /i "%TARGET%"=="installer" goto installer
if /i "%TARGET%"=="tests" goto tests
if /i "%TARGET%"=="all" goto all
echo [build] unknown target "%TARGET%" ^(use installer^|tests^|all^)
exit /b 2

:all
call :do_installer || exit /b 1
call :do_tests || exit /b 1
echo [build] all targets built.
exit /b 0

:installer
call :do_installer || exit /b 1
exit /b 0

:tests
call :do_tests || exit /b 1
exit /b 0

:do_installer
echo [build] compiling installer...
rem /main picks the GUI entry point: InstallerForm.cs also carries the headless
rem CLI's entry point, because the two share all of their logic.
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 ^
  /main:Dsh.Msys2Installer.Program ^
  /out:"%OUT%\msys2-installer.exe" ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  "%SRC%\Msys2Flavour.cs" "%SRC%\ShimBuilder.cs" "%SRC%\PresetWriter.cs" "%SRC%\InstallerForm.cs"
if errorlevel 1 (
  echo [build] ERROR: installer build failed.
  exit /b 1
)

rem The headless CLI is a SEPARATE, console-subsystem binary. A winexe cannot
rem report an exit code, and its stdout cannot be captured, so a script cannot
rem assert on `msys2-installer.exe --list`. Same sources; /main picks the CLI
rem entry point, exactly as :do_tests picks the test runner.
echo [build] compiling headless CLI...
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /warn:4 ^
  /main:Dsh.Msys2Installer.ProgramCli ^
  /out:"%OUT%\msys2-installer-cli.exe" ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  "%SRC%\Msys2Flavour.cs" "%SRC%\ShimBuilder.cs" "%SRC%\PresetWriter.cs" "%SRC%\InstallerForm.cs"
if errorlevel 1 (
  echo [build] ERROR: headless CLI build failed.
  exit /b 1
)
rem The shim is compiled on the target machine, so ship its source beside the exe.
copy /y "%SRC%\Msys2ShellShim.cs" "%OUT%\Msys2ShellShim.cs" >nul
echo [build]   %OUT%\msys2-installer.exe
echo [build]   %OUT%\msys2-installer-cli.exe
exit /b 0

:do_tests
echo [build] compiling tests...
rem /main picks the test runner: InstallerForm.cs also carries the installer's
rem own entry point, because the tests share the installer's source directly.
"%CSC%" /nologo /target:exe /platform:anycpu /optimize- /warn:4 ^
  /main:Dsh.Msys2Installer.Tests.TestMain ^
  /out:"%OUT%\msys2-tests.exe" ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  "%SRC%\Msys2Flavour.cs" "%SRC%\ShimBuilder.cs" "%SRC%\PresetWriter.cs" "%SRC%\InstallerForm.cs" ^
  "%HERE%tests\TestFramework.cs" "%HERE%tests\InstallerTests.cs" "%HERE%tests\ShimTests.cs" ^
  "%HERE%tests\TestMain.cs"
if errorlevel 1 (
  echo [build] ERROR: test build failed.
  exit /b 1
)
echo [build]   %OUT%\msys2-tests.exe
exit /b 0