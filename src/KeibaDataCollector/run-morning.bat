@echo off
chcp 65001 >nul
REM 朝一バッチ: 当日の出走表を取得しWordPressへ反映する。

cd /d "%~dp0"
if not exist secrets.local.bat (
    echo secrets.local.bat が見つかりません。
    echo secrets.local.bat.example をコピーして secrets.local.bat を作成し、値を設定してください。
    pause
    exit /b 1
)
call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe morning

echo.
echo ===== 終了しました。ウィンドウを閉じるには何かキーを押してください =====
pause >nul
