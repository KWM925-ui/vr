@echo off
chcp 65001 >nul
setlocal
set UNITY=F:\Unity\Editor\Unity.exe
set DIR=%~dp0

echo ============================================
echo  Unity 手动许可证激活
echo ============================================
echo.

if not exist "%UNITY%" (
    echo [ERROR] 未找到 Unity: %UNITY%
    pause & exit /b 1
)

set ULF=
for %%f in ("%DIR%*.ulf") do set ULF=%%f

if "%ULF%"=="" (
    echo [缺少 .ulf 文件]
    echo.
    echo 请先把从 license.unity3d.com 下载的 .ulf 文件放到本目录：
    echo   %DIR%
    echo 再双击本脚本。
    echo.
    echo 有 .ulf 文件时，本目录现在只有：
    dir /b "%DIR%"
    pause & exit /b 1
)

echo 使用许可证文件: %ULF%
echo 正在激活...
"%UNITY%" -batchmode -nographics -quit -manualLicenseFile "%ULF%" -logFile "%DIR%activate.log"
echo 退出码: %ERRORLEVEL%
echo.
findstr /i "Licen" "%DIR%activate.log" | more
echo.
echo ============================================
echo 若上面没有 "No valid Unity Editor license" 字样，就是激活成功了。
echo 接着双击同目录的 编译校验.bat 验证工程能否编译。
echo ============================================
pause
