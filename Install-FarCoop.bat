@echo off
setlocal EnableExtensions
title FAR: Lone Sails co-op mod installer
rem ============================================================================
rem  Installs everything the co-op mod needs into one or more FAR: Lone Sails
rem  folders: BepInEx 6.0.0-pre.1 (IL2CPP x64) + FarCoopMod.dll.
rem
rem  How to use (any of these):
rem   - put this file inside the game folder (next to FarLoneSails.exe), run it
rem   - put it in a folder that CONTAINS several game folders, run it
rem     (every subfolder with FarLoneSails.exe gets the mod)
rem   - drag one or more game folders onto this file
rem
rem  FarCoopMod.dll is taken from next to this file, or from bin\Release (after
rem  building), or downloaded from the newest GitHub release.
rem ============================================================================

set "HERE=%~dp0"
set "BEPVER=6.0.0-pre.1"
set "BEPZIPNAME=BepInEx_UnityIL2CPP_x64_%BEPVER%.zip"
set "BEPURL=https://github.com/BepInEx/BepInEx/releases/download/v%BEPVER%/%BEPZIPNAME%"
set "MODURL=https://github.com/Dzima92/Far-lone-sail-coop/releases/latest/download/FarCoopMod.dll"
set "BEPZIP=%TEMP%\%BEPZIPNAME%"
set "MODDLL="
set /a DONE=0
set /a FAILED=0

echo.
echo  FAR: Lone Sails co-op mod installer
echo  -----------------------------------
echo.

rem ---- find FarCoopMod.dll ----
if exist "%HERE%FarCoopMod.dll" set "MODDLL=%HERE%FarCoopMod.dll"
if not defined MODDLL if exist "%HERE%bin\Release\FarCoopMod.dll" set "MODDLL=%HERE%bin\Release\FarCoopMod.dll"
if not defined MODDLL call :download "%MODURL%" "%TEMP%\FarCoopMod.dll" "FarCoopMod.dll"
if not defined MODDLL if exist "%TEMP%\FarCoopMod.dll" set "MODDLL=%TEMP%\FarCoopMod.dll"
if not defined MODDLL (
  echo  [!] FarCoopMod.dll not found.
  echo      Put FarCoopMod.dll next to this file and run it again.
  goto :end
)
echo  Mod file: %MODDLL%
echo.

rem ---- which game folders? ----
if not "%~1"=="" goto :fromargs
if exist "%HERE%FarLoneSails.exe" (
  call :install "%HERE%."
  goto :summary
)
set /a FOUND=0
for /d %%D in ("%HERE%*") do if exist "%%~fD\FarLoneSails.exe" (
  set /a FOUND+=1
  call :install "%%~fD"
)
if %FOUND% GTR 0 goto :summary

echo  No game found next to this file.
set "GAMEDIR="
set /p "GAMEDIR=  Paste the path of your FAR: Lone Sails folder and press Enter: "
if not defined GAMEDIR goto :end
set "GAMEDIR=%GAMEDIR:"=%"
call :install "%GAMEDIR%"
goto :summary

:fromargs
if "%~1"=="" goto :summary
call :install "%~1"
shift
goto :fromargs

rem ============================================================================
:install
set "G=%~f1"
echo  == %G%
if not exist "%G%\FarLoneSails.exe" (
  echo     [!] FarLoneSails.exe is not in this folder - skipped.
  set /a FAILED+=1
  goto :eof
)
if exist "%G%\BepInEx\core\BepInEx.IL2CPP.dll" (
  echo     BepInEx is already installed.
) else (
  call :getbepinex
  if not exist "%BEPZIP%" (
    echo     [!] Could not download BepInEx. Download %BEPZIPNAME% yourself from
    echo         https://github.com/BepInEx/BepInEx/releases  ^(tag v%BEPVER%^)
    echo         and unzip it into the game folder, then run this again.
    set /a FAILED+=1
    goto :eof
  )
  echo     Unpacking BepInEx...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Expand-Archive -LiteralPath $env:BEPZIP -DestinationPath $env:G -Force } catch { Write-Host $_.Exception.Message; exit 1 }"
  if errorlevel 1 (
    echo     [!] Unpacking failed. Is the game running? Close it and try again.
    set /a FAILED+=1
    goto :eof
  )
)
if not exist "%G%\BepInEx\plugins" mkdir "%G%\BepInEx\plugins"
copy /y "%MODDLL%" "%G%\BepInEx\plugins\FarCoopMod.dll" >nul
if errorlevel 1 (
  echo     [!] Could not copy FarCoopMod.dll. Is the game running? Close it and try again.
  set /a FAILED+=1
  goto :eof
)
echo     FarCoopMod.dll installed.
set /a DONE+=1
goto :eof

rem ============================================================================
:getbepinex
if exist "%BEPZIP%" goto :eof
call :download "%BEPURL%" "%BEPZIP%" "BepInEx %BEPVER%"
goto :eof

:download
echo  Downloading %~3 ...
set "DL_URL=%~1"
set "DL_OUT=%~2"
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -UseBasicParsing -Uri $env:DL_URL -OutFile $env:DL_OUT } catch { if (Test-Path $env:DL_OUT) { Remove-Item $env:DL_OUT }; exit 1 }"
if errorlevel 1 echo     (download of %~3 failed)
goto :eof

rem ============================================================================
:summary
echo.
echo  Installed into %DONE% game folder(s), %FAILED% problem(s).
if %DONE% EQU 0 goto :end

rem ---- firewall (only possible when started as administrator) ----
net session >nul 2>&1
if errorlevel 1 (
  echo.
  echo  Firewall: when you HOST for the first time, Windows asks to allow the game -
  echo  click "Allow". (Run this file as administrator to open the ports now.)
) else (
  netsh advfirewall firewall delete rule name="FAR Coop mod" >nul 2>&1
  netsh advfirewall firewall add rule name="FAR Coop mod" dir=in action=allow protocol=TCP localport=7777 >nul
  netsh advfirewall firewall add rule name="FAR Coop mod" dir=in action=allow protocol=UDP localport=7777-7778 >nul
  echo  Firewall: ports 7777 ^(TCP+UDP^) and 7778 ^(UDP^) opened for hosting.
)

echo.
echo  NEXT STEPS
echo   1. Start the game once and wait - the very first start takes 1-3 minutes
echo      (BepInEx prepares the game; it needs internet that one time).
echo   2. In the game: F1 = host, F2 = find and join on LAN, F4 = show/hide the menu.
echo   3. Every player needs the same FarCoopMod.dll version.

:end
echo.
pause
endlocal
