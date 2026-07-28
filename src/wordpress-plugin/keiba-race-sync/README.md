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

## 含まれないもの（別途対応が必要）

- 日付・開催場・レース番号を切り替えるナビゲーション画面（「本日の開催一覧」等）
- カスタムCSSのデザイン微調整（提供いただいた画像に寄せた最低限のテーブル装飾のみ実装済み）

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
