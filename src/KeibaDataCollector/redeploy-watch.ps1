<#
.SYNOPSIS
    最新版を取得してビルドし、監視タスクを入れ直す。

.DESCRIPTION
    更新のたびに手作業で行うと、次の点を踏みやすいためスクリプト化した。

      - Stop-ScheduledTask はタスク（起動用バッチ）を止めるが、そこから起動された
        KeibaDataCollector.exe が残ることがある。残っていると exe を上書きできず
        「別のプロセスが使用中のため…にコピーできませんでした」でビルドが失敗する。
      - ビルドが失敗したことに気づかないまま監視を再開すると、修正前のまま動き続ける。

    そのため「タスク停止 → プロセス終了 → 取得 → ビルド（失敗したら中断）→ 再開」
    の順で確実に行う。

.PARAMETER SkipPull
    git pull を行わず、現在のソースのままビルドし直す場合に指定する。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\redeploy-watch.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipPull
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

function Write-Step([string] $message) {
    Write-Output ""
    Write-Output "==== $message ===="
}

# --- 1. 監視を止める ---------------------------------------------------------
Write-Step '監視タスクを停止'
foreach ($task in @('KeibaDataCollector-Watch', 'KeibaDataCollector-Morning')) {
    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
        Write-Output "  停止要求: $task"
    }
}

# タスクを止めても子プロセスが残ることがあるため、明示的に終了させる。
$procs = Get-Process KeibaDataCollector -ErrorAction SilentlyContinue
if ($procs) {
    Write-Output ("  残存プロセスを終了: PID {0}" -f (($procs | ForEach-Object { $_.Id }) -join ', '))
    $procs | Stop-Process -Force
    # ファイルロックが解けるまで少し待つ
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 250
        if (-not (Get-Process KeibaDataCollector -ErrorAction SilentlyContinue)) { break }
    }
}
else {
    Write-Output "  実行中のプロセスはありません"
}

# --- 2. 最新版を取得 ---------------------------------------------------------
if (-not $SkipPull) {
    Write-Step '最新版を取得 (git pull)'
    git pull origin main
    if ($LASTEXITCODE -ne 0) {
        throw "git pull に失敗しました。監視は再開していません。"
    }
}

# --- 3. ビルド ---------------------------------------------------------------
Write-Step 'ビルド'
dotnet build -c Debug
if ($LASTEXITCODE -ne 0) {
    # ここで止める。失敗に気づかず再開すると、修正前のexeで動き続けてしまう。
    throw "ビルドに失敗しました。監視は再開していません。上のエラーを確認してください。"
}

# --- 4. 監視を再開 -----------------------------------------------------------
Write-Step '監視タスクを再開'
Start-ScheduledTask -TaskName 'KeibaDataCollector-Watch'
Start-Sleep -Seconds 3
$info = Get-ScheduledTaskInfo -TaskName 'KeibaDataCollector-Watch'
Write-Output ("  最終実行: {0}  結果: {1}" -f $info.LastRunTime, $info.LastTaskResult)
Write-Output "  ※ 267009 (0x41301) は「実行中」を表します。異常ではありません。"

Write-Step '完了'
$log = Join-Path $scriptDir ("logs\watch-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))
Write-Output "数分後、次のコマンドでログを確認してください:"
Write-Output "  Get-Content '$log' -Encoding UTF8 -Tail 20"
Write-Output ""
Write-Output "確認ポイント:"
Write-Output "  ・「レース一覧を取得: ◯件」の件数が増えていくこと"
Write-Output "  ・「監視中... メモリ◯MB」の数値が横ばいであること（増え続ける場合は要連絡）"
