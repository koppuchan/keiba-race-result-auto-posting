using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using KeibaDataCollector.Models;
using Newtonsoft.Json;

namespace KeibaDataCollector.WordPress
{
    /// <summary>
    /// WordPress REST API 経由でカスタム投稿タイプ "race" を作成・更新するクライアント。
    /// 認証は Application Passwords（WP標準機能、WP 5.6+）を使用する。
    ///
    /// 前提（WordPress側で別途必要な準備）:
    ///  - カスタム投稿タイプ "race" を show_in_rest=true で登録
    ///  - race_key, race_card, race_result, payouts, corner_passage を
    ///    register_post_meta で REST 経由の読み書きを許可
    ///  - 対象ユーザーでアプリケーションパスワードを発行し、環境変数 WordPressAppPassword に設定
    /// </summary>
    public class WordPressClient
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public WordPressClient(string baseUrl, string username, string applicationPassword)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient();
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{applicationPassword}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        public async Task UpsertRaceCardAsync(RaceKey key, object raceCardEntries)
        {
            var existingId = await FindPostIdByRaceKeyAsync(key.AsSlug());
            var payload = new
            {
                title = $"{key.RaceDate:yyyy/MM/dd} {key.TrackCode} {key.RaceNumber}R 出走表",
                status = "publish",
                meta = new
                {
                    race_key = key.AsSlug(),
                    race_card = raceCardEntries,
                }
            };
            await SendAsync(existingId, payload);
        }

        public async Task PublishRaceResultAsync(RaceResult result)
        {
            var existingId = await FindPostIdByRaceKeyAsync(result.Key.AsSlug());
            var payload = new
            {
                title = $"{result.Key.RaceDate:yyyy/MM/dd} {result.Key.TrackCode} {result.Key.RaceNumber}R 結果",
                status = "publish",
                meta = new
                {
                    race_key = result.Key.AsSlug(),
                    race_result = result.Entries,
                    payouts = result.Payouts,
                    corner_passage = result.CornerPassage,
                }
            };
            await SendAsync(existingId, payload);
        }

        private async Task<int?> FindPostIdByRaceKeyAsync(string raceKeySlug)
        {
            // race_key をmeta_queryで検索できるようWordPress側にカスタムRESTフィルタを用意する想定。
            var response = await _http.GetAsync($"{_baseUrl}/wp-json/wp/v2/race?meta_key=race_key&meta_value={raceKeySlug}");
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync();
            var posts = JsonConvert.DeserializeObject<WpPost[]>(body);
            return posts != null && posts.Length > 0 ? posts[0].Id : (int?)null;
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
        }
    }
}
