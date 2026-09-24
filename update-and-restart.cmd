@echo off
setlocal EnableExtensions

title DesktopPet Updater
cd /d "%~dp0"

set "PROJECT_FILE=%~dp0src\DesktopPet\DesktopPet.csproj"
set "APP_EXE=%~dp0src\DesktopPet\bin\Debug\net8.0-windows\DesktopPet.exe"
set "PROCESS_NAME=DesktopPet.exe"
set "PULL_FAILED=0"
set "BUILD_FAILED=0"

echo [1/4] Checking the repository...
where git >nul 2>&1
if errorlevel 1 goto :git_missing

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 goto :not_a_repository

echo [2/4] Pulling the latest code...
git pull --ff-only
if errorlevel 1 (
    set "PULL_FAILED=1"
    echo.
    echo WARNING: Could not pull the latest code. The current local code will be built.
    echo Local changes are not discarded by this script.
    echo.
)

echo [3/4] Stopping DesktopPet before building...
call :stop_running_app
if errorlevel 1 goto :stop_failed

echo Building DesktopPet in the default Debug output directory...
where dotnet >nul 2>&1
if errorlevel 1 (
    set "BUILD_FAILED=1"
    echo ERROR: The .NET SDK was not found. The existing application will be started.
) else (
    dotnet build "%PROJECT_FILE%" --configuration Debug --nologo
    if errorlevel 1 set "BUILD_FAILED=1"
)

echo [4/4] Starting DesktopPet...
start "" "%APP_EXE%"
if errorlevel 1 goto :start_failed

echo.
if "%BUILD_FAILED%"=="1" (
    echo WARNING: The build failed, but DesktopPet was started from the existing Debug output.
) else (
    echo DesktopPet was built and restarted successfully.
)

if "%PULL_FAILED%"=="1" echo WARNING: The latest code could not be pulled.

if "%BUILD_FAILED%"=="1" goto :completed_with_errors
if "%PULL_FAILED%"=="1" goto :completed_with_errors
goto :success

:stop_running_app
tasklist /fi "IMAGENAME eq %PROCESS_NAME%" /nh 2>nul | find /i "%PROCESS_NAME%" >nul
if errorlevel 1 exit /b 0

rem Do not use /T here: this updater may be a child process launched by DesktopPet.
rem First request a normal shutdown, then force termination only if it is still running.
taskkill /im "%PROCESS_NAME%" >nul 2>&1
for /l %%I in (1,1,5) do (
    tasklist /fi "IMAGENAME eq %PROCESS_NAME%" /nh 2>nul | find /i "%PROCESS_NAME%" >nul
    if errorlevel 1 exit /b 0
    timeout /t 1 /nobreak >nul
)

taskkill /f /im "%PROCESS_NAME%" >nul 2>&1
timeout /t 1 /nobreak >nul
tasklist /fi "IMAGENAME eq %PROCESS_NAME%" /nh 2>nul | find /i "%PROCESS_NAME%" >nul
if errorlevel 1 exit /b 0
exit /b 1

:git_missing
echo.
echo ERROR: Git was not found. Install Git or add it to PATH.
goto :failure

:not_a_repository
echo.
echo ERROR: This script must be run from the DesktopPet Git repository.
goto :failure

:stop_failed
echo.
echo ERROR: DesktopPet could not be stopped, so the build was not started.
goto :failure

:start_failed
echo.
echo ERROR: DesktopPet could not be started from:
echo %APP_EXE%
goto :failure

:completed_with_errors
echo.
pause
exit /b 1

:success
echo.
timeout /t 3 /nobreak >nul
exit /b 0

:failure
echo.
pause
exit /b 1
