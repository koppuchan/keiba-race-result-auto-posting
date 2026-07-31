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
```

タスクスケジューラで `morning` を毎朝定時実行、`watch` を開催時間帯だけ起動する運用を想定。

## 機密情報の扱い

`App.config` には非機密の既定値のみをコミットしています。利用キー・パスワード類は
**環境変数**（`AppConfig.cs` が環境変数を優先して読む）で実行時に注入してください。
`App.config` に実際の値を書き込んでコミットしないよう注意してください。
