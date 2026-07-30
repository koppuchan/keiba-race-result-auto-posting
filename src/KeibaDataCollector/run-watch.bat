@echo off
REM レース確定監視: 結果・払戻を随時WordPressへ反映する（開催時間帯に起動しておく）。
REM 停止するには Ctrl+C を押してください。

cd /d "%~dp0"
if not exist secrets.local.bat (
    echo secrets.local.bat が見つかりません。
    echo secrets.local.bat.example をコピーして secrets.local.bat を作成し、値を設定してください。
    pause
    exit /b 1
)
call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe watch

echo.
echo ===== 終了しました。ウィンドウを閉じるには何かキーを押してください =====
pause >nul
