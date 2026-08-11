<#
.SYNOPSIS
    KeibaDataCollector の朝一バッチ／確定監視をタスクスケジューラへ登録する。

.DESCRIPTION
    2つのタスクを作成します。

      KeibaDataCollector-Morning : 毎日 -MorningTime に scheduled-morning.bat
      KeibaDataCollector-Predict : 毎日 -PredictTime に scheduled-predict.bat
      KeibaDataCollector-Watch   : 毎日 -WatchTime   に scheduled-watch.bat

    watch モードは当日の全レースが確定すると自身で終了するため、停止トリガーは不要です。

    重要な前提:
      JV-Link / UmaConn の利用キーは「setup を実行したWindowsユーザー」の
      レジストリに保存されます。そのため、このタスクは必ず同じユーザーで
      実行される必要があります。既定では、このスクリプトを実行している
      ユーザー自身が登録されます。

.PARAMETER MorningTime
    朝一バッチの実行時刻（HH:mm）。既定 07:00。
    出馬表は開催日より前に配信されるため、早朝で問題ありません。

.PARAMETER PredictTime
    予想印を生成する時刻。出走表が揃い、オッズが動き始めたあとに実行する。

.PARAMETER WatchTime
    確定監視の開始時刻（HH:mm）。既定 09:30。
    地方競馬のナイター開催まで監視が続くよう、余裕をもって早めに開始します。

.PARAMETER RunOnlyWhenLoggedOn
    指定すると「ログオン時のみ実行」で登録します（既定）。
    JV-Link / UmaConn はダイアログを出すことがあり、非対話セッションだと
    それが見えないまま処理が止まる可能性があるため、まずはこちらを推奨します。
    -RunOnlyWhenLoggedOn:$false を指定すると、パスワードを保存して
    「ログオンしていなくても実行する」で登録します（要パスワード入力）。

.EXAMPLE
    # 既定（07:00 朝一 / 09:30 監視開始、ログオン時のみ実行）
    powershell -ExecutionPolicy Bypass -File .\register-scheduled-tasks.ps1

.EXAMPLE
    # 時刻を変える
    powershell -ExecutionPolicy Bypass -File .\register-scheduled-tasks.ps1 -MorningTime 06:30 -WatchTime 10:00
#>
[CmdletBinding()]
param(
    [string] $MorningTime = '07:00',
    [string] $PredictTime = '09:00',
    [string] $WatchTime = '09:30',
    [switch] $RunOnlyWhenLoggedOn = $true
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$morningBat = Join-Path $scriptDir 'scheduled-morning.bat'
$predictBat = Join-Path $scriptDir 'scheduled-predict.bat'
$watchBat = Join-Path $scriptDir 'scheduled-watch.bat'
$exePath = Join-Path $scriptDir 'bin\Debug\net48\KeibaDataCollector.exe'
$secrets = Join-Path $scriptDir 'secrets.local.bat'

# --- 事前チェック ------------------------------------------------------------
foreach ($required in @($morningBat, $predictBat, $watchBat, $exePath)) {
    if (-not (Test-Path $required)) {
        throw "必要なファイルが見つかりません: $required`nビルド済みか確認してください（dotnet build -c Debug）。"
    }
}
if (-not (Test-Path $secrets)) {
    throw "secrets.local.bat が見つかりません: $secrets`nsecrets.local.bat.example をコピーして値を設定してください。"
}

$currentUser = "$env:USERDOMAIN\$env:USERNAME"
Write-Output "登録ユーザー : $currentUser"
Write-Output "作業ディレクトリ: $scriptDir"
Write-Output ""

# --- 登録 --------------------------------------------------------------------
function Register-KeibaTask {
    param(
        [string] $TaskName,
        [string] $BatPath,
        [string] $StartTime,
        [string] $Description,
        # 指定すると、開始時刻から $RepeatFor の間、$RepeatEvery ごとに繰り返し実行する。
        [timespan] $RepeatEvery,
        [timespan] $RepeatFor
    )

    $action = New-ScheduledTaskAction -Execute $BatPath -WorkingDirectory $scriptDir
    $trigger = New-ScheduledTaskTrigger -Daily -At $StartTime

    # 地方競馬のオッズは朝のうちに順次配信されるため、予想生成は1回では全レースを賄えない
    # （実測: 09:31時点で57レース中2件、10:05時点で24件）。
    # 繰り返し実行し、オッズが出たレースから順に埋めていく。
    # 既に予想が入っているレースは上書きしないので、何度走らせても最初の値が残る。
    if ($RepeatEvery -gt [timespan]::Zero) {
        $trigger.Repetition = (New-ScheduledTaskTrigger -Once -At $StartTime `
            -RepetitionInterval $RepeatEvery -RepetitionDuration $RepeatFor).Repetition
    }

    # 同じCOMオブジェクトを二重に開かないよう多重起動を禁止する。
    # 電源/ネットワーク条件でスキップされないようにもしておく（VPSは常時電源のため）。
    $settings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -DontStopIfGoingOnBatteries `
        -AllowStartIfOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Hours 20)

    if ($RunOnlyWhenLoggedOn) {
        # 対話セッションで実行。ダイアログが出た場合に気づける。
        $principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Highest
    }
    else {
        # ログオンしていなくても実行。パスワードの保存が必要。
        $principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Password -RunLevel Highest
    }

    $task = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description $Description

    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Write-Output "既存タスクを更新します: $TaskName"
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }

    if ($RunOnlyWhenLoggedOn) {
        Register-ScheduledTask -TaskName $TaskName -InputObject $task | Out-Null
    }
    else {
        $cred = Get-Credential -UserName $currentUser -Message "$TaskName を「ログオンしていなくても実行」で登録します。$currentUser のパスワードを入力してください。"
        Register-ScheduledTask -TaskName $TaskName -InputObject $task -User $cred.UserName -Password $cred.GetNetworkCredential().Password | Out-Null
    }

    Write-Output "登録しました: $TaskName ($StartTime 毎日)"
}

Register-KeibaTask -TaskName 'KeibaDataCollector-Morning' -BatPath $morningBat -StartTime $MorningTime `
    -Description '当日の出走表を取得しWordPressへ反映する（朝一バッチ）'

Register-KeibaTask -TaskName 'KeibaDataCollector-Predict' -BatPath $predictBat -StartTime $PredictTime `
    -Description '朝一オッズの人気順から予想印を生成しWordPressへ反映する（オッズ配信を待って繰り返す）' `
    -RepeatEvery (New-TimeSpan -Minutes 30) -RepeatFor (New-TimeSpan -Hours 6)

Register-KeibaTask -TaskName 'KeibaDataCollector-Watch' -BatPath $watchBat -StartTime $WatchTime `
    -Description 'レース確定を監視し、結果・払戻をWordPressへ随時反映する。全レース確定で自動終了する'

Write-Output ""
Write-Output "完了しました。確認方法:"
Write-Output "  Get-ScheduledTask -TaskName 'KeibaDataCollector-*' | Format-Table TaskName,State"
Write-Output "  Start-ScheduledTask -TaskName 'KeibaDataCollector-Morning'   # 手動で試運転"
Write-Output "  Get-ScheduledTaskInfo -TaskName 'KeibaDataCollector-Morning' # 前回結果を確認"
Write-Output ""
Write-Output "ログは $scriptDir\logs\ に日付ごとに出力されます。"
