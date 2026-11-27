<#
.SYNOPSIS
    夜間の自動更新。GitHub の main に新しい版があるときだけ deploy.ps1 を実行する。

.DESCRIPTION
    タスク KeibaDataCollector-AutoDeploy から毎日 03:00 に実行される（register-scheduled-tasks.ps1）。
    修正を反映するたびに手作業で deploy.ps1 を実行してもらっていたが、反映を忘れると
    修正前の版が動き続けるため、レースの無い深夜に自動で反映するようにした（2026-11-27）。

      - 新しい版が無ければ何もしない（ビルドもタスクの停止もしない）
      - あれば deploy.ps1 を実行する（停止 → 取得 → ビルド → タスク登録 → 再開）
        deploy.ps1 は、デプロイ前に動いていたタスクだけを再開するので、
        深夜に監視や予想が始まることはない
      - ビルドに失敗した場合、deploy.ps1 はタスクを再開せずに中断する。
        夜間はどのタスクも動いていないため、朝の各タスクは自分の時刻に修正前の版で動く
        （ビルドの失敗で exe は置き換わらない）。ログの「失敗」を見て手で直す

    ログ: logs\autodeploy-yyyyMMdd.log

    このスクリプトは bat を挟まずに powershell.exe から直接起動する。
    bat は実行中に1行ずつ読まれるため、git pull で bat 自体が書き換わると
    途中から読む位置がずれて誤動作する。ps1 は起動時に全体が読み込まれるので安全。
#>
$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$logDir = Join-Path $scriptDir 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("autodeploy-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))

function Write-Log([string] $message) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy/MM/dd HH:mm:ss'), $message
    Add-Content -Path $log -Value $line -Encoding UTF8
}

# 認証の入力画面を出さない。深夜に誰も答えられない画面で止まり続けるより、失敗して記録に残すほうがよい。
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'never'

# git の標準エラーは cmd の中でまとめて受け取る（deploy.ps1 の taskkill と同じ理由。
# PowerShell 5.1 では 2>&1 が ErrorActionPreference=Stop により例外になる）。
function Invoke-Git([string] $arguments) {
    $output = cmd /c "git $arguments 2>&1"
    return @{ ExitCode = $LASTEXITCODE; Output = (($output | Out-String).Trim()) }
}

try {
    Write-Log '---- 自動更新の確認を開始'

    $fetch = Invoke-Git 'fetch origin main'
    if ($fetch.ExitCode -ne 0) {
        Write-Log "失敗: GitHub から最新版を確認できませんでした（何も変更していません）: $($fetch.Output)"
        exit 1
    }

    $local = (Invoke-Git 'rev-parse HEAD').Output
    $remote = (Invoke-Git 'rev-parse origin/main').Output
    if ($local -eq $remote) {
        Write-Log "更新なし（$($local.Substring(0, 7))）。何もしません。"
        exit 0
    }

    $changes = (Invoke-Git "log --oneline $local..$remote").Output
    Write-Log "新しい版があります: $($local.Substring(0, 7)) → $($remote.Substring(0, 7))"
    Add-Content -Path $log -Value $changes -Encoding UTF8

    # deploy.ps1 は別の powershell で実行し、出力をすべてログへ送る。
    # chcp 65001 で出力を UTF-8 にそろえる（他のログと同じ文字コードにするため）。
    Write-Log 'deploy.ps1 を実行します'
    cmd /c "chcp 65001 >nul & powershell -NoProfile -ExecutionPolicy Bypass -File `"$scriptDir\deploy.ps1`" >> `"$log`" 2>&1"
    $deployExit = $LASTEXITCODE

    if ($deployExit -ne 0) {
        Write-Log "失敗: deploy.ps1 が終了コード $deployExit で終了しました。上の出力を確認してください。"
        exit $deployExit
    }
    Write-Log "完了: $($remote.Substring(0, 7)) を反映しました。"
    exit 0
}
catch {
    Write-Log "失敗: $($_.Exception.Message)"
    exit 1
}
