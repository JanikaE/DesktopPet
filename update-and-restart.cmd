@echo off
setlocal EnableExtensions

title DesktopPet Updater
cd /d "%~dp0"

set "PROJECT_FILE=%~dp0src\DesktopPet\DesktopPet.csproj"
set "LIVE2D_WEB_DIR=%~dp0src\DesktopPet.Live2D.Web"
set "LIVE2D_RUNTIME_DIR=%~dp0src\DesktopPet\Assets\Live2D\Runtime"
set "APP_EXE=%~dp0src\DesktopPet\bin\Debug\net8.0-windows10.0.17763.0\DesktopPet.exe"
set "PROCESS_NAME=DesktopPet.exe"
set "PULL_FAILED=0"
set "FRONTEND_FAILED=0"
set "BUILD_FAILED=0"

echo [1/5] Checking the repository...
where git >nul 2>&1
if errorlevel 1 goto :git_missing

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 goto :not_a_repository

echo [2/5] Pulling the latest code...
git pull --ff-only
if errorlevel 1 (
    set "PULL_FAILED=1"
    echo.
    echo WARNING: Could not pull the latest code. The current local code will be built.
    echo Local changes are not discarded by this script.
    echo.
)

echo [3/5] Stopping DesktopPet before building...
call :stop_running_app
if errorlevel 1 goto :stop_failed

echo [4/5] Building Live2D runtime and DesktopPet...
call :build_live2d_frontend
if errorlevel 1 (
    set "FRONTEND_FAILED=1"
    set "BUILD_FAILED=1"
    echo.
    echo ERROR: The Live2D frontend build failed. The .NET build was skipped.
) else (
    call :build_desktop_pet
)

echo [5/5] Starting DesktopPet...
if not exist "%APP_EXE%" goto :start_missing
start "" "%APP_EXE%"
if errorlevel 1 goto :start_failed

echo.
if "%BUILD_FAILED%"=="1" (
    echo WARNING: The build failed, but DesktopPet was started from the existing Debug output.
) else (
    echo DesktopPet including the Live2D runtime was built and restarted successfully.
)

if "%PULL_FAILED%"=="1" echo WARNING: The latest code could not be pulled.

if "%BUILD_FAILED%"=="1" goto :completed_with_errors
if "%PULL_FAILED%"=="1" goto :completed_with_errors
goto :success

:build_live2d_frontend
if not exist "%LIVE2D_WEB_DIR%\package.json" (
    echo ERROR: Live2D frontend project was not found:
    echo %LIVE2D_WEB_DIR%
    exit /b 1
)

if not exist "%LIVE2D_RUNTIME_DIR%\live2dcubismcore.js" (
    echo ERROR: Cubism Core is missing:
    echo %LIVE2D_RUNTIME_DIR%\live2dcubismcore.js
    echo Copy it from the licensed Cubism SDK for Web package before building.
    exit /b 1
)

where node >nul 2>&1
if errorlevel 1 (
    echo WARNING: Node.js was not found. The checked-in Live2D runtime will be used.
    call :validate_prebuilt_live2d
    if errorlevel 1 exit /b 1
    exit /b 0
)

pushd "%LIVE2D_WEB_DIR%"
if exist "node_modules\typescript\bin\tsc" (
    node "node_modules\typescript\bin\tsc" --noEmit
    if errorlevel 1 goto :live2d_failed
    node "node_modules\typescript\bin\tsc"
    if errorlevel 1 goto :live2d_failed
    node "scripts\fix-module-specifiers.mjs"
    if errorlevel 1 goto :live2d_failed
    popd
    exit /b 0
)

where pnpm >nul 2>&1
if not errorlevel 1 (
    call pnpm install --frozen-lockfile
    if not errorlevel 1 (
        call pnpm run typecheck
        if errorlevel 1 goto :live2d_failed
        call pnpm run build
        if errorlevel 1 goto :live2d_failed
        popd
        exit /b 0
    )
    echo WARNING: pnpm could not restore dependencies. Trying npm if available...
)

where npm >nul 2>&1
if not errorlevel 1 (
    call npm install --no-audit --no-fund --no-package-lock
    if errorlevel 1 goto :live2d_restore_failed
    call npm run typecheck
    if errorlevel 1 goto :live2d_failed
    call npm run build
    if errorlevel 1 goto :live2d_failed
    popd
    exit /b 0
)

echo WARNING: Neither pnpm nor npm could restore dependencies.

:live2d_restore_failed
popd
echo WARNING: The checked-in Live2D runtime will be used. Install Node.js 20+ and pnpm or npm to rebuild TypeScript sources.
call :validate_prebuilt_live2d
if errorlevel 1 exit /b 1
exit /b 0

:live2d_failed
popd
exit /b 1

:validate_prebuilt_live2d
if not exist "%LIVE2D_RUNTIME_DIR%\renderer.html" goto :prebuilt_live2d_missing
if not exist "%LIVE2D_RUNTIME_DIR%\renderer-host.js" goto :prebuilt_live2d_missing
if not exist "%LIVE2D_RUNTIME_DIR%\modules\src\adapter.js" goto :prebuilt_live2d_missing
if not exist "%LIVE2D_RUNTIME_DIR%\live2dcubismcore.js" goto :prebuilt_live2d_missing
echo Live2D prebuilt runtime validated.
exit /b 0

:prebuilt_live2d_missing
echo ERROR: The prebuilt Live2D runtime is incomplete.
exit /b 1

:build_desktop_pet
where dotnet >nul 2>&1
if errorlevel 1 (
    set "BUILD_FAILED=1"
    echo ERROR: The .NET SDK was not found. The existing application will be started.
    exit /b 1
)
dotnet build "%PROJECT_FILE%" --configuration Debug --nologo
if errorlevel 1 (
    set "BUILD_FAILED=1"
    exit /b 1
)
exit /b 0

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

:start_missing
echo.
echo ERROR: The Debug executable does not exist at:
echo %APP_EXE%
echo The build did not produce a runnable application.
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
