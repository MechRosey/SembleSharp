@echo off
REM Build the vendored tree-sitter and tree-sitter-cpp shared libraries on
REM Windows. Requires cl.exe -- auto-discovered via vswhere.exe if not already
REM on PATH, so this works from a plain dotnet build as well as from a VS
REM Developer Command Prompt.
REM
REM Usage:  build-natives.cmd <output-dir>

setlocal enabledelayedexpansion

if "%~1"=="" (
    echo usage: %~nx0 ^<output-dir^> 1>&2
    exit /b 2
)

set "OUTDIR=%~1"
set "SCRIPT_DIR=%~dp0"

where cl.exe >nul 2>&1
if errorlevel 1 (
    REM cl.exe not on PATH -- try to locate it via vswhere.exe.
    set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if not exist "!VSWHERE!" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
    if not exist "!VSWHERE!" (
        echo build-natives.cmd: cl.exe not found and vswhere.exe not found; install Visual Studio with the C++ workload 1>&2
        exit /b 1
    )
    for /f "usebackq delims=" %%P in (`"!VSWHERE!" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
        set "VS_INSTALL=%%P"
    )
    if "!VS_INSTALL!"=="" (
        echo build-natives.cmd: vswhere found no VS installation with VC++ tools 1>&2
        exit /b 1
    )
    call "!VS_INSTALL!\VC\Auxiliary\Build\vcvarsall.bat" amd64 >nul 2>&1
    where cl.exe >nul 2>&1
    if errorlevel 1 (
        echo build-natives.cmd: cl.exe still not found after vcvarsall.bat 1>&2
        exit /b 1
    )
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

REM tree-sitter runtime -- single-file amalgamation in lib/src/lib.c.
REM /DEF: explicitly exports only the symbols Semble uses; MSVC does not honour
REM GCC visibility pragmas so without a .def file no symbols would be exported.
cl /nologo /LD /O2 /MT ^
    /I "%SCRIPT_DIR%tree-sitter\lib\include" ^
    /I "%SCRIPT_DIR%tree-sitter\lib\src" ^
    "%SCRIPT_DIR%tree-sitter\lib\src\lib.c" ^
    /link /OUT:"%OUTDIR%\tree-sitter.dll" /IMPLIB:"%OUTDIR%\tree-sitter.lib" ^
         /DEF:"%SCRIPT_DIR%tree-sitter.def"
if errorlevel 1 exit /b 1

REM tree-sitter-cpp grammar.
cl /nologo /LD /O2 /MT /w ^
    /I "%SCRIPT_DIR%tree-sitter-cpp\src" ^
    "%SCRIPT_DIR%tree-sitter-cpp\src\parser.c" ^
    "%SCRIPT_DIR%tree-sitter-cpp\src\scanner.c" ^
    /link /OUT:"%OUTDIR%\tree-sitter-cpp.dll" /IMPLIB:"%OUTDIR%\tree-sitter-cpp.lib" ^
         /DEF:"%SCRIPT_DIR%tree-sitter-cpp.def"
if errorlevel 1 exit /b 1

echo build-natives.cmd: built %OUTDIR%\tree-sitter.dll and %OUTDIR%\tree-sitter-cpp.dll
endlocal
