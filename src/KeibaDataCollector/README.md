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
1b. ~~**メソッド名のプレフィックス**~~ → 実機で `New-Object -ComObject` + `Get-Member`により、
    JV-LinkとUmaConnは同じメソッド一覧を `JV*`/`NV*` のプレフィックス違いで持つことを確認済み
    （`JvSpecComDataSource`にmethodPrefix引数として反映済み）。
2. **JVOpen / JVRead 等の引数の正確な型・並び順**
   - `JvSpecComDataSource.cs` 内のコメントに記載した想定シグネチャは、コミュニティで広く使われている
     一般的な形ですが、公式のJV-Link/UmaConnインターフェース仕様書で最終確認してください。
3. **レコードのバイトレベルパース**（`Interop/JvRecordParser.cs`）
   - 意図的に未実装にしてあります。理由: 着順・払戻金額など実際の金銭に関わる数値を、
     未検証のオフセットで実装すると本番サイトに誤情報を出すリスクがあるため。
   - JRA-VAN公式SDKに含まれる `JVData_Struct.cs`（レコード構造体定義、C#版あり）を共有いただければ、
     このファイルに実際のマッピングを実装します。UmaConn側も同様の構造体定義があるはずです。
4. **リアルタイム系データ種別コード**（`Program.cs` 内の `"0B12"` は仮置き）
   - 速報オッズ／中間成績／確定成績／払戻等でコードが分かれています。JV-Data仕様書の
     「リアルタイム系データ種別一覧」で、確定成績・払戻に対応するコードに差し替えてください。
5. **JVSetServiceKey の要否**
   - JV-Link/UmaConnは`JVSetUIProperties`（`setup`モードで呼び出し）による初回GUI設定で
     利用キーを保存する方式が一般的です。コード側で明示的にキーを渡す必要があるかは
     実機で`setup`を一度実行して確認してください。

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
