@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "APP_PROJ=src\EasyFileSync.App\EasyFileSync.App.csproj"
set "PUBLISH_DIR=publish\win-x64"
set "APP_EXE=%PUBLISH_DIR%\EasyFileSync.exe"

:menu
cls
echo ==================================================
echo    EasyFileSync - Run / Build
echo ==================================================
echo    1. Run App (Debug)
echo    2. Run Published App (win-x64)
echo    3. Build Windows x64 (self-contained, single exe)
echo    4. Build Windows x64 (framework-dependent)
echo    5. Clean build output
echo    0. Exit
echo ==================================================
choice /c 123450 /n /m "Select option [0-5]: "
set "PILIH=%errorlevel%"

if "%PILIH%"=="1" goto run_debug
if "%PILIH%"=="2" goto run_published
if "%PILIH%"=="3" goto build_scd
if "%PILIH%"=="4" goto build_fdd
if "%PILIH%"=="5" goto clean
if "%PILIH%"=="6" goto end
goto menu

:run_debug
cls
echo [Run] Debug build...
echo.
dotnet run --project "%APP_PROJ%" -c Debug
if errorlevel 1 (
    echo.
    echo [FAILED] Application exited with error. See messages above.
) else (
    echo.
    echo [OK] Application closed.
)
echo.
pause
goto menu

:run_published
cls
if not exist "%APP_EXE%" (
    echo [INFO] No win-x64 build found in "%PUBLISH_DIR%".
    echo        Please run option 3 first to build.
    echo.
    choice /c YN /n /m "Build now? [Y/N]: "
    if "!errorlevel!"=="1" goto build_scd
    goto menu
)
echo [Run] %APP_EXE%
echo.
start "" "%APP_EXE%"
echo [OK] Application launched.
echo.
pause
goto menu

:build_scd
cls
echo [Build] Windows x64 self-contained (single exe)...
echo.
dotnet publish "%APP_PROJ%" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo.
    echo [FAILED] Build failed. Please check the error above.
    echo.
    pause
    goto menu
)
echo.
echo [OK] Build completed: "%PUBLISH_DIR%\EasyFileSync.exe"
echo.
choice /c YN /n /m "Open publish folder? [Y/N]: "
if "!errorlevel!"=="1" start "" "%PUBLISH_DIR%"
echo.
pause
goto menu

:build_fdd
cls
echo [Build] Windows x64 framework-dependent...
echo.
dotnet publish "%APP_PROJ%" -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=false ^
    -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo.
    echo [FAILED] Build failed. Please check the error above.
    echo.
    pause
    goto menu
)
echo.
echo [OK] Build completed: "%PUBLISH_DIR%\EasyFileSync.exe"
echo     Note: Requires .NET 10 Desktop Runtime installed on target PC.
echo.
pause
goto menu

:clean
cls
echo [Clean] Cleaning bin, obj, and publish folders...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "src\EasyFileSync.App\bin" rmdir /s /q "src\EasyFileSync.App\bin"
if exist "src\EasyFileSync.App\obj" rmdir /s /q "src\EasyFileSync.App\obj"
if exist "src\EasyFileSync.Core\bin" rmdir /s /q "src\EasyFileSync.Core\bin"
if exist "src\EasyFileSync.Core\obj" rmdir /s /q "src\EasyFileSync.Core\obj"
echo [OK] Completed.
echo.
pause
goto menu

:end
endlocal
exit /b 0
