using System;
using System.Collections.Generic;
using System.IO;
using KeibaDataCollector.Models;
using Newtonsoft.Json;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 当日のレースの発走時刻を、VPS上のファイル（data\post-times-yyyyMMdd.json）に保存・参照する。
    ///
    /// 用意した理由: UmaConn への問い合わせを減らすため。
    ///   ・結果の確認は、発走時刻を過ぎたレースだけにする（発走前に聞いても結果は無い）
    ///   ・予想は、発走時刻を過ぎたレースには出さない（確定オッズで付けた「後出し」になる）
    /// 発走時刻はRAレコードにしか無く、それを読むには提供元のデータを全件読み込む必要がある。
    /// 朝一バッチで1回だけ読んで保存し、各処理はこのファイルを読むだけにする。
    ///
    /// ファイルが無い・読めない場合は空として扱う。その場合、各処理は時刻による絞り込みをせず
    /// 従来どおりに動く（取りこぼすより、多めに聞くほうに倒す）。
    /// </summary>
    internal static class PostTimeStore
    {
        private static string PathFor(DateTime date) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", $"post-times-{date:yyyyMMdd}.json");

        /// <summary>発走時刻を読む。キーはレースキー（例: 20261013-35-5R）、値は "HH:mm"。</summary>
        public static Dictionary<string, TimeSpan> Load(DateTime date)
        {
            var result = new Dictionary<string, TimeSpan>();
            try
            {
                var path = PathFor(date);
                if (!File.Exists(path)) return result;

                var raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                if (raw == null) return result;

                foreach (var pair in raw)
                {
                    if (TimeSpan.TryParse(pair.Value, out var time)) result[pair.Key] = time;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"発走時刻ファイルを読めませんでした（時刻による絞り込みなしで続行）: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 発走時刻が分かっているレースを、既存の内容に追記して保存する。
        /// 朝一バッチと watch の起動時の一覧取得の両方から呼ばれるため、上書きではなく合わせる。
        /// </summary>
        public static void Merge(DateTime date, IEnumerable<RaceKey> races)
        {
            try
            {
                var times = new Dictionary<string, string>();
                foreach (var pair in Load(date)) times[pair.Key] = pair.Value.ToString(@"hh\:mm");

                var added = 0;
                foreach (var race in races)
                {
                    if (!race.PostTime.HasValue || race.RaceDate.Date != date.Date) continue;
                    times[race.AsSlug()] = race.PostTime.Value.ToString(@"hh\:mm");
                    added++;
                }
                if (added == 0) return;

                var path = PathFor(date);
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                // 読み手（predict・catchup）が書きかけを読まないよう、一時ファイルに書いてから置き換える。
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(times, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex)
            {
                // 保存できなくても本来の処理（出走表・監視）は止めない。
                Console.WriteLine($"発走時刻ファイルを保存できませんでした（処理は継続）: {ex.Message}");
            }
        }

        /// <summary>発走時刻が未設定のレースに、ファイルの値を当てはめる。</summary>
        public static void Apply(DateTime date, IEnumerable<RaceKey> races)
        {
            var times = Load(date);
            if (times.Count == 0) return;

            foreach (var race in races)
            {
                if (race.PostTime.HasValue) continue;
                if (times.TryGetValue(race.AsSlug(), out var time)) race.PostTime = time;
            }
        }
    }
}
