using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using KeibaDataCollector.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace KeibaDataCollector.WordPress
{
    /// <summary>
    /// WordPress REST API 経由でカスタム投稿タイプ "race" を作成・更新するクライアント。
    /// 認証は Application Passwords（WP標準機能、WP 5.6+）を使用する。
    ///
    /// 前提（WordPress側で別途必要な準備。src/wordpress-plugin/keiba-race-sync が対応）:
    ///  - カスタム投稿タイプ "race" を show_in_rest=true で登録
    ///  - race_key, race_card, race_result, payouts, corner_passage を
    ///    register_post_meta で REST 経由の読み書きを許可
    ///  - 対象ユーザーでアプリケーションパスワードを発行し、環境変数 WordPressAppPassword に設定
    ///
    /// race_card/race_result/payouts/corner_passage は、WPのREST metaスキーマ検証が
    /// 任意ネスト構造の配列を安定して受け付けないため、camelCaseキーのJSON文字列として送る
    /// （WP側は register_post_meta の type を "string" で登録し、表示時にjson_decodeする）。
    /// </summary>
    public class WordPressClient
    {
        private static readonly JsonSerializerSettings CamelCaseSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
        };

        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public WordPressClient(string baseUrl, string username, string applicationPassword)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient();
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{applicationPassword}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        public async Task UpsertRaceCardAsync(RaceKey key, List<RaceCardEntry> raceCardEntries)
        {
            var existing = await FindPostByRaceKeyAsync(key.AsSlug());

            // 既に結果が入っている投稿には出走表のタイトルを被せない。
            // 朝一バッチを結果反映後に再実行した場合（手動での試運転や、出馬表の訂正が
            // 後から届いた場合など）、タイトルだけ「出走表」に戻り、本文は結果表という
            // 不整合になるため。表示側は race_result があれば結果表を出す作りなので、
            // タイトルもそれに合わせる。
            var suffix = existing != null && existing.HasRaceResult ? "結果" : "出走表";

            var payload = new
            {
                title = $"{key.RaceDate:yyyy/MM/dd} {key.TrackCode} {key.RaceNumber}R {suffix}",
                status = "publish",
                meta = new
                {
                    race_key = key.AsSlug(),
                    race_card = JsonConvert.SerializeObject(raceCardEntries, CamelCaseSettings),
                }
            };
            await SendAsync(existing?.Id, payload);
        }

        /// <summary>
        /// 予想印（馬番 => ◎○▲△）をWordPressへ反映する。
        ///
        /// predictions だけを送る。出走表や結果を一緒に送ると、予想生成の時点では
        /// まだ確定していない項目を空で上書きしてしまう。
        ///
        /// 投稿がまだ無い場合は新規作成する（朝一バッチより先に走っても取りこぼさない）。
        /// LINE限定フラグはサイト側で設定する項目のため、こちらからは一切送らない
        /// ＝毎朝の予想更新で消えることはない。
        /// </summary>
        public async Task UpsertPredictionsAsync(RaceKey key, Dictionary<int, string> marks)
        {
            var existing = await FindPostByRaceKeyAsync(key.AsSlug());
            var suffix = existing != null && existing.HasRaceResult ? "結果" : "出走表";

            // キーは馬番。JSONでは文字列キーになるため、表示側は文字列・数値の両方を見る作り。
            var byUmaban = new Dictionary<string, string>();
            foreach (var mark in marks)
                byUmaban[mark.Key.ToString()] = mark.Value;

            var payload = new
            {
                title = $"{key.RaceDate:yyyy/MM/dd} {key.TrackCode} {key.RaceNumber}R {suffix}",
                status = "publish",
                meta = new
                {
                    race_key = key.AsSlug(),
                    predictions = JsonConvert.SerializeObject(byUmaban),
                }
            };
            await SendAsync(existing?.Id, payload);
        }

        // レースキーごとに、最後に送信した内容を保持する。watchモードは確定するまで同じレースを
        // 繰り返しポーリングするため、これが無いと内容が1文字も変わっていなくても
        // ポーリング間隔ごとにWordPressへ書き込み続けてしまう（実機で確認: 速報段階のまま
        // 止まっているレースが20秒ごとに同一内容で更新され、投稿の最終更新日時だけが
        // 無意味に進み続けていた）。
        private readonly Dictionary<string, string> _lastPublishedResult = new Dictionary<string, string>();

        /// <summary>レース結果をWordPressへ反映する。前回送信時から内容が変わっていない場合は
        /// 何もせず false を返す（無駄なAPI呼び出しと投稿更新を避けるため）。</summary>
        public async Task<bool> PublishRaceResultAsync(RaceResult result)
        {
            var slug = result.Key.AsSlug();
            var raceResultJson = JsonConvert.SerializeObject(result.Entries, CamelCaseSettings);
            var payoutsJson = JsonConvert.SerializeObject(result.Payouts, CamelCaseSettings);
            var cornerPassageJson = JsonConvert.SerializeObject(result.CornerPassage, CamelCaseSettings);

            var signature = string.Join("", raceResultJson, payoutsJson, cornerPassageJson);
            if (_lastPublishedResult.TryGetValue(slug, out var previous) && previous == signature)
                return false;

            var existing = await FindPostByRaceKeyAsync(slug);
            var payload = new
            {
                title = $"{result.Key.RaceDate:yyyy/MM/dd} {result.Key.TrackCode} {result.Key.RaceNumber}R 結果",
                status = "publish",
                meta = new
                {
                    race_key = slug,
                    race_result = raceResultJson,
                    payouts = payoutsJson,
                    corner_passage = cornerPassageJson,
                }
            };
            await SendAsync(existing?.Id, payload);

            // 送信に成功した場合のみ記録する（失敗時は次回リトライさせたいため）。
            _lastPublishedResult[slug] = signature;
            return true;
        }

        /// <summary>race_key が一致する既存投稿を探す。無ければ null。</summary>
        private async Task<ExistingRacePost> FindPostByRaceKeyAsync(string raceKeySlug)
        {
            // race_key をmeta_queryで検索できるようWordPress側にカスタムRESTフィルタを用意している
            // （src/wordpress-plugin/keiba-race-sync の rest_race_query フィルタ）。
            var response = await _http.GetAsync($"{_baseUrl}/wp-json/wp/v2/race?meta_key=race_key&meta_value={raceKeySlug}");
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync();
            var posts = JsonConvert.DeserializeObject<WpPost[]>(body);
            if (posts == null || posts.Length == 0) return null;

            var post = posts[0];
            var raceResult = post.Meta?.RaceResult;

            return new ExistingRacePost
            {
                Id = post.Id,
                // メタは未設定だと "" や "[]" になりうるため、中身のある配列かどうかで判定する。
                HasRaceResult = !string.IsNullOrWhiteSpace(raceResult)
                                && raceResult.Trim() != "[]",
            };
        }

        private class ExistingRacePost
        {
            public int Id { get; set; }
            public bool HasRaceResult { get; set; }
        }

        private async Task SendAsync(int? existingId, object payload)
        {
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var path = existingId.HasValue
                ? $"{_baseUrl}/wp-json/wp/v2/race/{existingId.Value}"
                : $"{_baseUrl}/wp-json/wp/v2/race";

            var response = await _http.PostAsync(path, content);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"WordPress API failed ({response.StatusCode}): {body}");
            }
        }

        private class WpPost
        {
            [JsonProperty("id")]
            public int Id { get; set; }

            [JsonProperty("meta")]
            public WpPostMeta Meta { get; set; }
        }

        private class WpPostMeta
        {
            [JsonProperty("race_result")]
            public string RaceResult { get; set; }
        }
    }
}
