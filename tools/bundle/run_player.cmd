@echo off
rem Starts the VR player with one OpenXR runtime, chosen for this launch only.
rem Usage: run_player.cmd steamvr|metalink|desktop [MultiPass] [extra player arguments]
rem   steamvr   Valve Index, or Meta Quest through Steam Link (SteamVR runtime)
rem   metalink  Meta Quest through Meta Horizon Link or Air Link (Meta runtime)
rem   desktop   no headset; a normal window
rem The runtime is passed to the player through XR_RUNTIME_JSON, so the system-wide OpenXR
rem runtime (which needs admin rights to change) stays as it is.
setlocal EnableDelayedExpansion

set "KIND=%~1"
set "STEREO=SPI"
if /i "%~2"=="MultiPass" set "STEREO=MultiPass"
set "PLAYER=%~dp0%STEREO%\Unity_VR_Client.exe"
if not exist "%PLAYER%" (
    echo The player was not found: %PLAYER%
    echo Extract the whole zip first ^(right-click the zip, Extract All^), then run this file from the extracted folder.
    pause
    exit /b 1
)

if /i "%KIND%"=="desktop" (
    echo Starting the player without a headset.
    start "" "%PLAYER%" -desktop
    exit /b 0
)

set "PATTERN="
if /i "%KIND%"=="steamvr" set "PATTERN=steamxr_win64.json"
if /i "%KIND%"=="metalink" set "PATTERN=oculus_openxr_64.json"
if not defined PATTERN (
    echo Unknown runtime "%KIND%". Use steamvr, metalink or desktop.
    pause
    exit /b 1
)

rem 1. Runtimes registered with the OpenXR loader (readable without admin rights).
set "XR_RUNTIME_JSON="
for %%H in (HKLM HKCU) do (
    for /f "delims=" %%L in ('reg query "%%H\SOFTWARE\Khronos\OpenXR\1\AvailableRuntimes" 2^>nul ^| findstr /i /c:"%PATTERN%"') do (
        if not defined XR_RUNTIME_JSON call :take_registry_line "%%L"
    )
)

rem 2. Usual install folders.
if not defined XR_RUNTIME_JSON if /i "%KIND%"=="steamvr" (
    for /f "tokens=2,*" %%A in ('reg query "HKCU\Software\Valve\Steam" /v SteamPath 2^>nul ^| findstr /i "SteamPath"') do (
        set "STEAM=%%B"
        set "STEAM=!STEAM:/=\!"
        if exist "!STEAM!\steamapps\common\SteamVR\%PATTERN%" set "XR_RUNTIME_JSON=!STEAM!\steamapps\common\SteamVR\%PATTERN%"
    )
)
rem Single-line checks: "%ProgramFiles(x86)%" contains a ")" that would end a parenthesized block.
set "STEAM_DEFAULT=%ProgramFiles(x86)%\Steam\steamapps\common\SteamVR\%PATTERN%"
set "META_DEFAULT=%ProgramFiles%\Oculus\Support\oculus-runtime\%PATTERN%"
if not defined XR_RUNTIME_JSON if /i "%KIND%"=="steamvr" if exist "!STEAM_DEFAULT!" set "XR_RUNTIME_JSON=!STEAM_DEFAULT!"
if not defined XR_RUNTIME_JSON if /i "%KIND%"=="metalink" if exist "!META_DEFAULT!" set "XR_RUNTIME_JSON=!META_DEFAULT!"

if not defined XR_RUNTIME_JSON (
    echo No %KIND% OpenXR runtime ^(%PATTERN%^) was found on this PC.
    if /i "%KIND%"=="steamvr" echo Install SteamVR in Steam and start it once.
    if /i "%KIND%"=="metalink" echo The Meta Horizon Link PC app is not installed. Ask the lab staff, or use Start_Quest_SteamLink.cmd.
    pause
    exit /b 1
)

echo OpenXR runtime for this launch: %XR_RUNTIME_JSON%
echo Player: %PLAYER%
start "" "%PLAYER%" %3 %4 %5 %6 %7 %8 %9
exit /b 0

rem A "reg query" value line looks like:   C:\path\runtime.json    REG_DWORD    0x0
:take_registry_line
set "LINE=%~1"
for /f "tokens=* delims= " %%A in ("!LINE!") do set "LINE=%%A"
set "LINE=!LINE:    REG_=|!"
for /f "delims=|" %%A in ("!LINE!") do set "CANDIDATE=%%A"
if exist "!CANDIDATE!" set "XR_RUNTIME_JSON=!CANDIDATE!"
exit /b 0
