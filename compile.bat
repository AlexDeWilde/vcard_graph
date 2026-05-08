@echo off
setlocal enabledelayedexpansion

echo ============================================
echo  GPU Widget Builder
echo ============================================
echo.

:: Use the .NET Framework 4 compiler that ships with every Windows 10/11 install.
:: No Visual Studio or .NET SDK required.

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4 compiler not found at:
    echo         %CSC%
    echo.
    echo This file ships with Windows. Please ensure .NET Framework 4 is installed.
    pause
    exit /b 1
)

echo Compiler : %CSC%
echo Source   : GPUWidget.cs
echo Output   : GPUWidget.exe
echo.

"%CSC%" ^
    /nologo ^
    /t:winexe ^
    /platform:anycpu ^
    /optimize+ ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
    GPUWidget.cs ^
    /out:GPUWidget.exe

if exist GPUWidget.exe (
    echo.
    echo  [OK] GPUWidget.exe built successfully.
    echo.
    echo  Copy GPUWidget.exe anywhere on your disk and run it directly.
    echo  Right-click the widget to close it.
) else (
    echo.
    echo  [FAIL] Build failed. See errors above.
)

echo.
pause
