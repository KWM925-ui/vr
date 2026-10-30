@echo off
chcp 65001 >nul
title 打开龙舟VR工程 (F盘 2022.3.62f3)
echo ================================================
echo  不用 Unity Hub，直接用 F 盘的编辑器打开工程
echo  编辑器: F:\Unity\Editor\Unity.exe  (2022.3.62f3)
echo  工程  : F:\vr视觉\DragonBoatVR
echo ================================================
echo.

if not exist "F:\Unity\Editor\Unity.exe" (
    echo [错误] 找不到 F:\Unity\Editor\Unity.exe
    echo        编辑器可能被移动或删除了。
    pause
    exit /b 1
)

if not exist "F:\vr视觉\DragonBoatVR\Assets" (
    echo [错误] 找不到工程目录 F:\vr视觉\DragonBoatVR
    pause
    exit /b 1
)

echo 正在启动编辑器，首次打开要导入资源（Library 重建），约 5-10 分钟...
echo 打开后先看 Console 有没有红色报错。
echo.
start "" "F:\Unity\Editor\Unity.exe" -projectPath "F:\vr视觉\DragonBoatVR"
exit /b 0
