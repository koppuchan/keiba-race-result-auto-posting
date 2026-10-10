<#
.SYNOPSIS
    最新版を取得してビルドし、タスクを登録し直して再開する。更新時はこれだけ実行する。

.DESCRIPTION
    「停止 → 取得 → ビルド → 登録 → 再開」を必ずこの順で行う。
    手作業で組み立てると、これまで実際に次の失敗を繰り返した。

      - 稼働中に dotnet build を実行 → exe がロックされていて
        「別のプロセスが使用中のため…にコピーできませんでした」でビルド失敗。
        しかも失敗に気づかず再開すると、修正前のexeがそのまま動き続ける。
      - Stop-ScheduledTask はバッチを止めるだけで、そこから起動された
        KeibaDataCollector.exe が残ることがある。
      - タスク定義を置き換えると実行中のインスタンスが終了する。watchは
        トリガーが1日1回なので、日中にやるとその日はもう動かない
        （2026-08-11に発生。結果の反映が57レース中18レースで止まった）。

    ビルドに失敗した場合はタスクを再開せずに中断する。
    修正前のexeで動き続けるほうが、止まっているより気づきにくく害が大きい。

.PARAMETER SkipPull
    git pull を行わず、現在のソースのままビルドし直す場合に指定する。

.PARAMETER SkipRegister
    タスクの登録し直しを省略する。スケジュール（時刻・間隔）を変更していない
    ときに指定すると少し速い。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\deploy.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipPull,
    [switch] $SkipRegister
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# 常駐・繰り返し実行されるタスク。停止したら最後に必ず戻す。
$RunningTasks = @('KeibaDataCollector-Watch', 'KeibaDataCollector-Predict')
# Catchup は20分ごとに自分で起動するので、再開はしない（止めるのはビルド中にexeを掴まないため）。
$AllTasks     = @('KeibaDataCollector-Morning', 'KeibaDataCollector-Catchup') + $RunningTasks

# 止める対象は、このリポジトリでビルドしたexeだけにする。
#
# 同じVPSで horse-race-custom-builder も同じ名前の KeibaDataCollector.exe を動かしている
# （C:\horse-race-custom-builder\collector\... の score / backfill）。名前だけで探すと
# 先方のプロセスまで止めてしまい、先方が固まっていると終了できずにこちらのデプロイが
# ビルド前に中断する（2026-10-05 発生: 先方の score が15:00から固まっており、
# 結果の監視を再開できなかった）。
$OurExe = Join-Path $scriptDir 'bin\Debug\net48\KeibaDataCollector.exe'

function Get-OurProcesses {
    Get-CimInstance Win32_Process -Filter "Name='KeibaDataCollector.exe'" |
        Where-Object { $_.ExecutablePath -eq $OurExe } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}

function Write-Step([string] $message) {
    Write-Output ""
    Write-Output "==== $message ===="
}

# --- 1. 止める ---------------------------------------------------------------
Write-Step '実行中のタスクとプロセスを停止'
foreach ($task in $AllTasks) {
    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
        Write-Output "  停止要求: $task"
    }
}

# タスクを止めても子プロセスが残る。残っているとexeを上書きできない。
# 他システムの同名プロセスは触らないが、あることは表示しておく（切り分けの手がかりになる）。
foreach ($other in (Get-CimInstance Win32_Process -Filter "Name='KeibaDataCollector.exe'" |
                    Where-Object { $_.ExecutablePath -ne $OurExe })) {
    Write-Output ("  他システムのプロセス（停止しません）: PID {0}  起動 {1}  {2}" -f
        $other.ProcessId, $other.CreationDate, $other.CommandLine)
}

$procs = Get-OurProcesses
if ($procs) {
    Write-Output ("  残存プロセスを終了: PID {0}" -f (($procs | ForEach-Object { $_.Id }) -join ', '))
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue

    # Stop-Process で落ちないことがある。子プロセスごと落とす taskkill も試す。
    Start-Sleep -Seconds 1
    foreach ($p in (Get-OurProcesses)) {
        # 標準エラーは cmd の中でまとめてから受け取る。PowerShell 側で 2>&1 にすると、
        # Windows PowerShell 5.1 では taskkill のエラー出力が ErrorActionPreference=Stop により
        # 例外になり、スクリプトごと中断する（2026-10-05 発生: 停止だけして、
        # ビルドも再開もしないまま終わった）。失敗しても続け、理由はそのまま表示する。
        $result = cmd /c "taskkill /F /T /PID $($p.Id) 2>&1"
        if ($LASTEXITCODE -ne 0) {
            Write-Output ("  taskkill PID {0} 失敗: {1}" -f $p.Id, (($result | Out-String).Trim() -replace '\s+', ' '))
        }
    }

    for ($i = 0; $i -lt 40; $i++) {          # ロックが解けるまで最大10秒待つ
        Start-Sleep -Milliseconds 250
        if (-not (Get-OurProcesses)) { break }
    }
}

$stuck = Get-OurProcesses
if ($stuck) {
    # どれがいつから残っているのか分からないと、原因の切り分けも復旧もできない。
    Write-Output ""
    Write-Output "  終了できないプロセス:"
    foreach ($p in $stuck) {
        $since = try { $p.StartTime.ToString('MM/dd HH:mm') } catch { '不明' }
        # どのモード（watch / predict 等）が固まっているのかで原因の当たりが変わるため、引数も出す。
        $mode = try {
            (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine -replace '^.*KeibaDataCollector\.exe"?\s*', ''
        } catch { '不明' }
        Write-Output ("    PID {0}  起動 {1}  応答 {2}  モード {3}" -f $p.Id, $since,
            $(if ($p.Responding) { 'あり' } else { 'なし' }), $mode)
    }
    Write-Output ""
    throw @"
プロセスが終了しません。exeを置き換えられないため、ビルド前に中断します。

COMの呼び出しが返らないまま止まっている状態で、taskkillでも落ちません。
タスクスケジューラでタスクを停止しても、これらは既に親から切り離されて
いるため消えません（タスク自体は「準備完了」に見えているはずです）。

  復旧: VPSを再起動してください。
        タスクはすべて登録済みなので、再起動後は自動で復帰します。
        そのうえで、もう一度 deploy.ps1 を実行してください。

ビルド前に中断しているため、中途半端な状態にはなっていません。
"@
}
Write-Output "  実行中のプロセスはありません"

# --- 2. 最新版を取得 ---------------------------------------------------------
if (-not $SkipPull) {
    Write-Step '最新版を取得 (git pull)'
    git pull origin main
    if ($LASTEXITCODE -ne 0) {
        throw "git pull に失敗しました。タスクは再開していません。"
    }
}

# --- 3. ビルド ---------------------------------------------------------------
Write-Step 'ビルド'
dotnet build -c Debug
if ($LASTEXITCODE -ne 0) {
    throw "ビルドに失敗しました。タスクは再開していません。上のエラーを確認してください。"
}

# --- 4. タスクを登録し直す ---------------------------------------------------
# 実行時刻や繰り返し間隔を変更した場合、ここを通さないと反映されない。
if (-not $SkipRegister) {
    Write-Step 'タスクを登録し直す'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'register-scheduled-tasks.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "タスクの登録に失敗しました。タスクは再開していません。"
    }
}

# --- 5. 再開 -----------------------------------------------------------------
Write-Step 'タスクを再開'
foreach ($task in $RunningTasks) {
    Start-ScheduledTask -TaskName $task
    Write-Output "  開始: $task"
}
Start-Sleep -Seconds 3

Write-Step '状態'
Get-ScheduledTask -TaskName 'KeibaDataCollector-*' | Format-Table TaskName, State -AutoSize

$today = Get-Date -Format 'yyyyMMdd'
Write-Step '数分後に確認してください'
Write-Output "  Get-Content '.\logs\predict-$today.log' -Encoding UTF8 | Select-String '反映完了'"
Write-Output "  Get-Content '.\logs\watch-$today.log'   -Encoding UTF8 -Tail 20"
Write-Output ""
Write-Output "確認ポイント:"
Write-Output "  ・予想: 「（所要 ◯分）」が繰り返し間隔(15分)を超えていないこと"
Write-Output "  ・予想: 「オッズ取得エラー」が0件であること"
Write-Output "  ・監視: 「監視中... メモリ◯MB」が横ばいであること"
