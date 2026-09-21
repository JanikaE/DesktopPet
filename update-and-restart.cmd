@echo off
setlocal EnableExtensions

title DesktopPet Updater
cd /d "%~dp0"

set "PROJECT_FILE=%~dp0src\DesktopPet\DesktopPet.csproj"
set "OUTPUT_ROOT=%~dp0src\DesktopPet\bin\AutoUpdate"
set "PROCESS_NAME=DesktopPet.exe"

echo [1/4] Checking the repository...
where git >nul 2>&1
if errorlevel 1 goto :git_missing

where dotnet >nul 2>&1
if errorlevel 1 goto :dotnet_missing

for /f "delims=" %%I in ('git rev-parse HEAD 2^>nul') do set "OLD_COMMIT=%%I"
if not defined OLD_COMMIT goto :not_a_repository

echo [2/4] Pulling the latest code...
git pull --ff-only
if errorlevel 1 goto :pull_failed

for /f "delims=" %%I in ('git rev-parse HEAD 2^>nul') do set "NEW_COMMIT=%%I"
if not defined NEW_COMMIT goto :pull_failed

set "BUILD_DIR=%OUTPUT_ROOT%\%NEW_COMMIT%"
set "RETRY_MARKER=%BUILD_DIR%\.retry-required"
set "RESTART_MARKER=%BUILD_DIR%\.restart-required"

if /i not "%OLD_COMMIT%"=="%NEW_COMMIT%" goto :build
if exist "%RETRY_MARKER%" (
    echo A previous build of this update did not finish. Retrying...
    goto :build
)
if exist "%RESTART_MARKER%" (
    echo A previous restart did not finish. Retrying...
    goto :restart
)

echo.
echo Already up to date. No build or restart is needed.
goto :success

:build
echo [3/4] Building the updated application...
if exist "%BUILD_DIR%" rmdir /s /q "%BUILD_DIR%"
mkdir "%BUILD_DIR%" 2>nul
if errorlevel 1 goto :build_failed
>"%RETRY_MARKER%" echo This marker is removed after a successful build.

rem A commit-specific output directory cannot be locked by the currently running app.
dotnet build "%PROJECT_FILE%" --configuration Release --output "%BUILD_DIR%" --nologo
if errorlevel 1 goto :build_failed

if not exist "%BUILD_DIR%\DesktopPet.exe" goto :build_failed
del /q "%RETRY_MARKER%" >nul 2>&1
>"%RESTART_MARKER%" echo This marker is removed after a successful restart.

:restart
echo [4/4] Restarting DesktopPet...
call :stop_running_app
if errorlevel 1 goto :stop_failed

start "" "%BUILD_DIR%\DesktopPet.exe"
if errorlevel 1 goto :start_failed
del /q "%RESTART_MARKER%" >nul 2>&1

rem Keep only the successfully launched build to avoid accumulating old versions.
for /d %%D in ("%OUTPUT_ROOT%\*") do (
    if /i not "%%~fD"=="%BUILD_DIR%" rmdir /s /q "%%~fD" >nul 2>&1
)

echo.
echo Update completed and DesktopPet was restarted successfully.
goto :success

:stop_running_app
tasklist /fi "IMAGENAME eq %PROCESS_NAME%" /nh 2>nul | find /i "%PROCESS_NAME%" >nul
if errorlevel 1 exit /b 0

rem First request a normal shutdown, then force termination only if it is still running.
taskkill /im "%PROCESS_NAME%" /t >nul 2>&1
for /l %%I in (1,1,5) do (
    tasklist /fi "IMAGENAME eq %PROCESS_NAME%" /nh 2>nul | find /i "%PROCESS_NAME%" >nul
    if errorlevel 1 exit /b 0
    timeout /t 1 /nobreak >nul
)

taskkill /f /im "%PROCESS_NAME%" /t >nul 2>&1
timeout /t 1 /nobreak >nul
tasklist /fi "IMAGENAME eq %PROCESS_NAME%" /nh 2>nul | find /i "%PROCESS_NAME%" >nul
if errorlevel 1 exit /b 0
exit /b 1

:git_missing
echo.
echo ERROR: Git was not found. Install Git or add it to PATH.
goto :failure

:dotnet_missing
echo.
echo ERROR: The .NET SDK was not found. Install the .NET 8 SDK or add dotnet to PATH.
goto :failure

:not_a_repository
echo.
echo ERROR: This script must be run from the DesktopPet Git repository.
goto :failure

:pull_failed
echo.
echo ERROR: Could not pull the latest code.
echo Check the message above. Local changes are not discarded by this script.
goto :failure

:build_failed
echo.
echo ERROR: The updated code could not be built.
echo The currently running DesktopPet instance was left untouched.
goto :failure

:stop_failed
echo.
echo ERROR: DesktopPet could not be stopped. The new build was not started.
goto :failure

:start_failed
echo.
echo ERROR: The new DesktopPet build could not be started.
goto :failure

:success
echo.
timeout /t 3 /nobreak >nul
exit /b 0

:failure
echo.
pause
exit /b 1
