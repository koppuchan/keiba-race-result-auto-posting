@echo off
chcp 65001 >nul
REM 初回のみ実行: JV-Link/UmaConnの利用キー登録ダイアログを開く。
REM WordPressの認証情報は不要（setupモードはWordPressに一切接続しない）。

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe setup

echo.
echo ===== 終了しました。ウィンドウを閉じるには何かキーを押してください =====
pause >nul
