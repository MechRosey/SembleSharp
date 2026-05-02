@echo off
REM Build the vendored tree-sitter and tree-sitter-cpp shared libraries on
REM Windows. Requires `cl.exe` on PATH (run from a Visual Studio Developer
REM Command Prompt or set VCINSTALLDIR appropriately).
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
    echo build-natives.cmd: cl.exe not found; run from a Visual Studio Developer Command Prompt 1>&2
    exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

REM tree-sitter runtime — single-file amalgamation in lib/src/lib.c.
cl /nologo /LD /O2 /MT ^
    /I "%SCRIPT_DIR%tree-sitter\lib\include" ^
    /I "%SCRIPT_DIR%tree-sitter\lib\src" ^
    "%SCRIPT_DIR%tree-sitter\lib\src\lib.c" ^
    /link /OUT:"%OUTDIR%\tree-sitter.dll" /IMPLIB:"%OUTDIR%\tree-sitter.lib"
if errorlevel 1 exit /b 1

REM tree-sitter-cpp grammar.
cl /nologo /LD /O2 /MT /w ^
    /I "%SCRIPT_DIR%tree-sitter-cpp\src" ^
    "%SCRIPT_DIR%tree-sitter-cpp\src\parser.c" ^
    "%SCRIPT_DIR%tree-sitter-cpp\src\scanner.c" ^
    /link /OUT:"%OUTDIR%\tree-sitter-cpp.dll" /IMPLIB:"%OUTDIR%\tree-sitter-cpp.lib"
if errorlevel 1 exit /b 1

echo build-natives.cmd: built %OUTDIR%\tree-sitter.dll and %OUTDIR%\tree-sitter-cpp.dll
endlocal
