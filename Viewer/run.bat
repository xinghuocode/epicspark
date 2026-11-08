@echo off
chcp 65001 >nul
title EpicSpark 局域网展示端
cd /d "%~dp0"

where python >nul 2>nul
if errorlevel 1 (
  echo.
  echo [错误] 未找到 python。请先安装 Python 3.8+ 并勾选 "Add python.exe to PATH"。
  echo.
  pause
  exit /b 1
)

python "%~dp0epicspark_viewer.py" %*
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" (
  echo.
  echo [提示] 展示端退出（代码 %CODE%）。常见原因：端口被占用 / 游戏未启动 / Ctrl+C 主动退出。
  echo.
  pause
)
