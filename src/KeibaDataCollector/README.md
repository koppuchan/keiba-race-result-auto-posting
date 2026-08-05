# KeibaDataCollector

JV-Link（中央競馬）とUmaConn（地方競馬DATA／競馬最強の法則WEB）から出走表・結果データを取得し、
WordPressへ自動反映するWindows常駐アプリの土台。

## 前提・設計方針

- JV-LinkとUmaConnは**同一仕様（JV-Link Interface Specification）のCOMコンポーネント**なので、
  `Interop/JvSpecComDataSource.cs` 1つで両方を扱う（ProgIDだけが違う）。
- JV-Link/UmaConnは**32bit専用**。ビルド設定は `PlatformTarget=x86` 固定（csproj設定済み）。
- COMは後期バインド（ProgID + リフレクション）で呼んでいる。理由: 環境依存のインタロップ型名を
  この開発環境では検証できないため。副作用として、COMイベント（JVWatchEvent等）は使えないので、
  結果監視は**ポーリング方式**にしている（`RaceResultService`）。

## 実機で必ず確認・修正が必要な項目（★重要）

1. ~~**UmaConnの正確なProgID**~~ → 実機のレジストリで確認済み: `NVDTLabLib.NVLink`（`App.config`反映済み）。
   `setup`コマンドでUmaConn側のダイアログも開くことを確認済み。
2. ~~**メソッド名のプレフィックス**~~ → 実機で `New-Object -ComObject` + `Get-Member`により、
   JV-LinkとUmaConnは同じメソッド一覧を `JV*`/`NV*` のプレフィックス違いで持つことを確認済み
   （`JvSpecComDataSource`にmethodPrefix引数として反映済み）。
3. ~~**JVOpen / JVRead 等の引数の正確な型・並び順**~~ → JRA-VAN公式のJV-Linkインターフェース仕様書
   （`JV-Linkインターフェース仕様書_4.9.0.1(Win).pdf`）で確認済み。既存の実装のシグネチャと一致。
4. ~~**レコードのバイトレベルパース**~~ → JRA-VAN公式SDKの `JVData_Struct.cs` を入手し
   `Interop/JvDataSdk/JVData_Struct.cs` に配置、`JvRecordParser` から `SetDataB()` を呼ぶ形で実装済み。
   `RaceCardService`/`RaceResultService` から `WordPressClient` への配線も完了。
   **残課題**: `ChakusaCD`（着差コード、数値）をハナ/クビ/アタマ等の表示文字列に変換するコード表が
   未確認（SDK同梱ドキュメントには見当たらず）。現状は生コードをそのまま`ChakusaText`に出している。
5. ~~**リアルタイム系データ種別コード**~~ → JV-Linkインターフェース仕様書「JVRTOpen」の対応表で確認済み。
   払戻確定＝`0B12`で、既存の仮実装の値がそのまま正しかった。
6. **JVSetServiceKey の要否** → 実機で`JVSetUIProperties`（`setup`）による利用キー登録が成功したため、
   通常はこちらの方式のみで運用可能と思われる（`JVSetServiceKey`を明示的に呼ぶ実装は現状なし）。

## 実機での既知の問題と対応

- **JRA-VAN利用キーは1台のPCにしか紐付けられない**（データラボサービスの仕様）。
  複数PCで同じ利用キーを使うと「認証エラー：この利用キーは既に使用されています」となる。
  発生した場合は JRA-VAN Data Lab.のマイページで利用キーの再発行が必要（該当PCのJV-Link
  アンインストール→再インストールも必要になる場合がある）。
- Windowsの「システムロケール」（Unicode対応をしていないプログラムの言語）が日本語でないと、
  JV-Linkのネイティブダイアログが文字化けする。`intl.cpl` → 管理タブ → システムロケールの変更 →
  日本語（日本）に変更し、PC再起動が必要。

## WordPress側で別途必要な準備

- カスタム投稿タイプ `race` を `show_in_rest: true` で登録
- `race_key` / `race_card` / `race_result` / `payouts` / `corner_passage` を
  `register_post_meta` でREST読み書き許可
- 対象ユーザーで [アプリケーションパスワード](https://ja.wordpress.org/support/article/introduction-to-application-passwords/) を発行

これは別途 `functions.php` またはコンパニオンプラグインとして用意する想定（未着手）。

## 実行方法

```
KeibaDataCollector.exe setup    # 初回のみ。利用キー等をGUIダイアログで登録
KeibaDataCollector.exe morning  # 朝一: 当日の出走表取得→WordPress反映
KeibaDataCollector.exe watch    # レース確定監視→結果・払戻を随時反映
KeibaDataCollector.exe probe    # 調査用: どのデータ種別で何が取れるか確認（WordPressへ書き込まない）
```

対話的に動かす場合は同名の `run-*.bat` から起動できます。

## 自動運用（タスクスケジューラ）

### 前提：`setup` を実行したユーザーで動かすこと

JV-Link / UmaConn の利用キーは **`setup` を実行したWindowsユーザーのレジストリ**に
保存されます。別ユーザーでタスクを動かすと認証エラー（-301/-303）になります。
登録スクリプトは実行中のユーザー自身を登録するため、**VPSに`setup`したユーザーで
ログインしてから**実行してください。

### 登録

```powershell
cd (作業ディレクトリ)\src\KeibaDataCollector
powershell -ExecutionPolicy Bypass -File .\register-scheduled-tasks.ps1
```

既定で以下の2タスクを作成します（時刻は `-MorningTime` / `-WatchTime` で変更可）。

| タスク名 | 既定時刻 | 内容 |
| --- | --- | --- |
| `KeibaDataCollector-Morning` | 毎日 07:00 | 当日の出走表を取得・反映 |
| `KeibaDataCollector-Watch` | 毎日 09:30 | 確定監視。全レース確定で自動終了 |

`watch` は当日の全レースが確定すると自分で終了するため、停止トリガーは不要です。
多重起動は禁止設定（同じCOMを二重に開かないため）にしています。

### スケジューラ用スクリプトを別に用意している理由

`run-*.bat` は末尾に `pause` があり、**タスクスケジューラから実行すると
キー入力待ちでタスクが終了しません**（翌日のトリガーもスキップされる）。
そのため `scheduled-morning.bat` / `scheduled-watch.bat` を別に用意し、
`pause` を外して `logs\` へ日付ごとにログ出力するようにしています。

### 更新の反映（推奨手順）

修正を本番へ反映するときは、次のスクリプトを使ってください。

```powershell
cd (作業ディレクトリ)\src\KeibaDataCollector
powershell -ExecutionPolicy Bypass -File .\redeploy-watch.ps1
```

「タスク停止 → 残存プロセス終了 → git pull → ビルド → 監視再開」をこの順で行います。

手作業で行うと次を踏みやすいため、スクリプト化しています。

- `Stop-ScheduledTask` は起動用バッチを止めますが、そこから起動された
  `KeibaDataCollector.exe` が残ることがあります。残っているとexeを上書きできず、
  `別のプロセスが使用中のため…にコピーできませんでした` でビルドが失敗します。
- ビルド失敗に気づかず監視を再開すると、**修正前のexeのまま動き続けます**。
  スクリプトはビルド失敗時に再開せず中断します。

### 確認・トラブルシュート

```powershell
Get-ScheduledTask -TaskName 'KeibaDataCollector-*' | Format-Table TaskName,State
Start-ScheduledTask -TaskName 'KeibaDataCollector-Morning'      # 手動で試運転
Get-ScheduledTaskInfo -TaskName 'KeibaDataCollector-Morning'    # 前回結果
Get-Content .\logs\morning-*.log -Tail 50 -Encoding UTF8         # 実行ログ
```

**ログは必ず `-Encoding UTF8` を付けて読んでください。**
`scheduled-*.bat` は `chcp 65001` の下でアプリのUTF-8出力をファイルへ流していますが、
Windows PowerShell 5.1 の `Get-Content` は既定でANSI(CP932)として読むため、
付けないと日本語が文字化けします（ファイル自体は壊れていません）。

`LastTaskResult` の主な値:

| 値 | 16進 | 意味 |
| --- | --- | --- |
| `0` | `0x0` | 正常終了 |
| `267009` | `0x41301` | **まだ実行中**（エラーではない。少し待って再確認） |
| `267011` | `0x41303` | 一度も実行されていない |
| `1` | `0x1` | スクリプトが `exit /b 1` で失敗（ログの `[ERROR]` 行を確認） |

### 無人実行時のダイアログ対策

JV-Link / UmaConn はモーダルダイアログを出すことがあり、**出たままだと
ユーザー操作待ちでプロセスが停止**します。以下を実施済み／実施してください。

- 払戻ダイアログ：`m_payflag = 1` をコード側で設定済み（`JvSpecComDataSource.Initialize`）
- 「JRA-VANからのお知らせを表示する」：`setup` のダイアログで**チェックを外す**
- UmaConn終了時のメモリリークダイアログ：発生する場合あり。**未解決**

上記の懸念があるため、登録スクリプトの既定は「**ログオン時のみ実行**」です。
VPSにログインしたセッションを維持（RDPは「切断」でログオフしない）して運用してください。
ダイアログ要因を解消できたら `-RunOnlyWhenLoggedOn:$false` で
「ログオンしていなくても実行」に切り替えられます（パスワード保存が必要）。

## ページキャッシュに注意（実際に障害になった）

サイトにはWP Rocketが入っており、**投稿を更新してもキャッシュが残っていると
朝に生成された「出走表だけ」のHTMLが配信され続けます**。

2026-08-05の実測:

| ページ | キャッシュ生成 | 投稿の更新 | 着順の表示 |
| --- | --- | --- | --- |
| 園田12R | 08:22 | 17:22 | されない |
| 船橋5R | 10:15 | 17:22 | されない |
| 門別6R | キャッシュ無し | 17:22 | される |

キャッシュが無いページだけ正しく表示されるため、**「一部のレースだけ更新される」**
という分かりにくい形で表面化します。収集アプリ側のログが「反映完了」でも、
サイトには出ていないことがある点に注意してください。

プラグイン v0.1.3 以降は、メタ更新時に該当レースページとレース一覧ページの
キャッシュを自動で破棄します（`keiba_race_sync_purge_post_cache`）。
WP Rocket / WP Super Cache / W3 Total Cache / WP Fastest Cache / LiteSpeed /
Cache Enabler に対応。他の仕組みを使う場合は `keiba_race_sync_purge_post`
アクションで追加できます。

確認方法（キャッシュを迂回すると出るなら、キャッシュが原因）:

```powershell
# 通常アクセス
(Invoke-WebRequest 'https://www.keiba-tips.top/race/<slug>/' -UseBasicParsing).Content -match 'keiba-result-table'
# クエリ付き＝キャッシュ迂回
(Invoke-WebRequest 'https://www.keiba-tips.top/race/<slug>/?nocache=1' -UseBasicParsing).Content -match 'keiba-result-table'
```

HTML末尾の `Debug: cached@<unixtime>` でキャッシュ生成時刻が分かります。

## データ源ごとの項目の入り方（実測）

地方競馬（UmaConn）は、成績レコード(SE)に入れてくる項目が**競馬場によって異なります**。
「地方競馬だから一律に取れない」ではない点に注意してください。

2026-08-03 の本番データを実測した結果（各レース1R、「入っている頭数／全頭数」）:

| 競馬場コード | 後3F | 単勝オッズ | 斤量 | 馬体重 |
| --- | --- | --- | --- | --- |
| 35 盛岡 | 12/12 | 12/12 | 12/12 | 12/12 |
| 43 船橋 | 9/9 | **0/9** | 9/9 | 9/9 |
| 46 金沢 | 8/8 | 8/8 | 8/8 | 8/8 |
| 83 ばんえい | **0/9** | 9/9 | 6/9 | 2/9 |

読み取れること:

- **後3ハロンタイム**は通常の地方競馬場では取得できる。
  ばんえい(83)のみ入らないが、200m直線競走でハロンの概念が無いため妥当。
- **単勝オッズ・人気順**はSEに入らない競馬場がある（船橋43など）。
  そのため速報オッズ `0B31` のO1レコードから補完している（`RaceResultService`）。
  SE側に実値がある場合は上書きしないため、中央競馬や盛岡・金沢の挙動は変わらない。
- **ばんえい(83)は負担重量・馬体重が不完全**。ばんえいはそり重量が数百kg、
  馬体重も1000kg近くあり、仕様上3バイト（最大998）の数値枠に収まらないため。
  通常の地方競馬場では正常。

バイト位置のズレではないことは確認済み（同レコード内の着差コード=343バイト目が
正しく読めるため、その後ろの単勝オッズ=360・人気順=364・後3ハロン=391もズレていない）。
値が入らないケースは、データ源がその項目を送っていないことによる。

## 機密情報の扱い

`App.config` には非機密の既定値のみをコミットしています。利用キー・パスワード類は
**環境変数**（`AppConfig.cs` が環境変数を優先して読む）で実行時に注入してください。
`App.config` に実際の値を書き込んでコミットしないよう注意してください。
