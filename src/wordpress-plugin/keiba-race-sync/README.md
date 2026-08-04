# Keiba Race Sync（WordPress companion プラグイン）

`KeibaDataCollector`（C#常駐アプリ、JV-Link/UmaConn連携）から送られる出走表・結果データを
受け取り、カスタム投稿タイプ `race` として保存し、レースページに自動表示する。

## できること

- カスタム投稿タイプ `race` を登録（`show_in_rest: true`）
- メタキー `race_key` / `race_card` / `race_result` / `payouts` / `corner_passage` をREST経由で読み書き許可
- `GET /wp-json/wp/v2/race?meta_key=race_key&meta_value=xxxx` で既存レース投稿を検索可能にする
  （`KeibaDataCollector` の `WordPressClient.FindPostIdByRaceKeyAsync` が使用）
- レース個別ページで、枠番カラー付きの出走表／着順表・払戻金・コーナー通過順位を自動表示
  （`the_content` フィルタで生成するため、既存テーマのheader/footerはそのまま使われる）
- **レース選択UI**（競馬場を選ぶ → レース番号を選ぶ → 出走表を表示）をショートコードで設置可能
- **予想印**の入力欄（管理画面）と「予想」列の表示

## レース選択UI

固定ページや投稿に次のショートコードを置くと、当日の開催から
「競馬場 → レース番号 → 出走表」の順に選べる画面になります。

```
[keiba_race_selector]
```

### ⚠ 固定ページのスラッグを `race` にしないこと

カスタム投稿タイプ `race` のアーカイブが `/race/` を使うため、
スラッグ `race` の固定ページを作ると**アーカイブ側が優先されページが表示できません**
（`?page_id=` で直接開いてもアーカイブにリダイレクトされます）。

`today-races` など別のスラッグにしてください。該当する固定ページがある場合は
管理画面に警告を表示します。

日付を指定する場合:

```
[keiba_race_selector date="2026-08-03"]
```

動作:

- 競馬場は当日データから自動抽出し、競馬場コードを名称（東京・盛岡 など）に変換して表示
- レース番号ボタンは、結果が確定済みのレースを色分けして表示
- 出走表は選択時にRESTから取得して差し込む（1日分の全テーブルを最初から埋め込まないため軽い）
- 開催が1場のみの日は、競馬場選択を省いて自動で展開
- スマホ／PCともにボタン数が自動で折り返す（レスポンシブ）

### 競馬場名について

`keiba_race_sync_track_name()` はJV-Data仕様書「コード表 2001.競馬場コード」に基づきます。
地方競馬DATA(UmaConn)はこの表に無いコードを返すことがあり（実データで `83` を確認）、
その場合はコード番号のまま表示します。名称が必要な場合は同関数の対応表に追記してください。

## 予想印

予想印はJV-Link/UmaConnからは取得できない（データ提供元に無い）ため、
**サイト側で入力する項目**です。

- レース編集画面の「予想印」欄で、出走馬ごとに ◎ ○ ▲ △ ☆ × を選択
- 1頭でも印があるレースだけ、出走表・結果表に「予想」列が表示される
- 収集アプリは `predictions` メタを送信しないため、自動更新で消えることはない

## インストール手順

1. このディレクトリ（`keiba-race-sync`）を丸ごと `wp-content/plugins/` にコピー
2. WordPress管理画面 → プラグイン → 「Keiba Race Sync」を有効化
3. 設定 → パーマリンク設定 を開いて「保存」を押す（`race` の書き換えルールを反映させるため）

## アプリケーションパスワードの発行（KeibaDataCollectorが使う認証情報）

1. 管理画面 → ユーザー → 該当ユーザーのプロフィール編集
2. ページ下部「アプリケーションパスワード」欄で任意の名前（例: `keiba-collector`）を入力し「新規追加」
3. 表示されたパスワードを `KeibaDataCollector` 実行環境の環境変数 `WordPressUser` / `WordPressAppPassword`
   に設定する（`App.config` には書かないこと。README参照）

## データ形式の取り決め

`race_card` / `race_result` / `payouts` / `corner_passage` は、WordPress REST APIの
メタスキーマ検証がネストした任意配列を安定して扱えないため、**camelCaseキーのJSON文字列**
として保存する取り決めにしている（`WordPressClient.cs` 側もこの形式で送信するよう実装済み）。

例（`race_result` 1件分）:

```json
{
  "chakujun": 1,
  "waku": 7,
  "umaban": 9,
  "horseName": "マコトアタキギリ",
  "sexAge": "牝3",
  "kinryo": 54.0,
  "jockeyName": "飛田愛斗",
  "time": "1:32.5",
  "chakusaText": "",
  "ninki": 1,
  "tanshoOdds": 1.6,
  "ushi3F": 40.7,
  "trainerName": "真島二也",
  "bataijuuZengo": 455,
  "bataijuuZogen": -8
}
```
