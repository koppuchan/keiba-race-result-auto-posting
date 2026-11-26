using System;
using System.Collections.Generic;
using System.IO;
using KeibaDataCollector.Models;
using Newtonsoft.Json;

namespace KeibaDataCollector.Scoring
{
    /// <summary>
    /// 当日の出走馬（馬番・枠・血統登録番号・騎手コード・調教師コード）とレース条件を、
    /// VPS上のファイル（data\entries-yyyyMMdd.json）に保存・参照する。
    ///
    /// 発売前スコアは過去データ（血統登録番号・騎手コード等で引く）と突き合わせて算出するが、
    /// サイトの出走表（race_card）には名前しか載せていない。コードを公開の投稿に載せずに済むよう、
    /// 朝一バッチが読んだ出馬表から、ここにだけ保存する。
    ///
    /// 読めない場合は空として扱い、予想は従来どおりオッズの人気順で付ける（PredictionService）。
    /// </summary>
    internal static class RaceEntriesStore
    {
        public class Race
        {
            public int Distance { get; set; }
            /// <summary>JV-Dataのトラックコード（10〜22 芝 / 23〜29 ダート / 51〜59 障害）。</summary>
            public string TrackSurfaceCode { get; set; }
            public List<Runner> Runners { get; set; } = new List<Runner>();
        }

        public class Runner
        {
            public int Umaban { get; set; }
            public int Waku { get; set; }
            public string HorseName { get; set; }
            public string KettoNum { get; set; }
            public string JockeyCode { get; set; }
            public string TrainerCode { get; set; }
            /// <summary>朝一の時点で出走取消・除外が分かっている馬。印を付けない。</summary>
            public bool Scratched { get; set; }
        }

        private static string PathFor(DateTime date) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", $"entries-{date:yyyyMMdd}.json");

        /// <summary>レースキー（例: 20261120-44-11R） → レース条件と出走馬。</summary>
        public static Dictionary<string, Race> Load(DateTime date)
        {
            try
            {
                var path = PathFor(date);
                if (!File.Exists(path)) return new Dictionary<string, Race>();
                return JsonConvert.DeserializeObject<Dictionary<string, Race>>(File.ReadAllText(path))
                       ?? new Dictionary<string, Race>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"出走馬ファイルを読めませんでした（予想はオッズの人気順で続行）: {ex.Message}");
                return new Dictionary<string, Race>();
            }
        }

        /// <summary>
        /// 朝一バッチが読んだ出馬表を、既存の内容に追記して保存する。
        /// 朝一バッチは中央（JV-Link）と地方（UmaConn）で2回呼ぶため、上書きではなく合わせる。
        /// </summary>
        public static void Merge(DateTime date, IDictionary<string, List<RaceCardEntry>> entriesByRace,
            IDictionary<string, (int Distance, string TrackSurfaceCode)> conditions)
        {
            try
            {
                var all = Load(date);
                foreach (var pair in entriesByRace)
                {
                    var race = new Race();
                    if (conditions.TryGetValue(pair.Key, out var cond))
                    {
                        race.Distance = cond.Distance;
                        race.TrackSurfaceCode = cond.TrackSurfaceCode;
                    }
                    foreach (var e in pair.Value)
                    {
                        race.Runners.Add(new Runner
                        {
                            Umaban = e.Umaban,
                            Waku = e.Waku,
                            HorseName = e.HorseName,
                            KettoNum = e.KettoNum,
                            JockeyCode = e.JockeyCode,
                            TrainerCode = e.TrainerCode,
                            Scratched = e.Scratched,
                        });
                    }
                    all[pair.Key] = race;
                }

                var path = PathFor(date);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(all, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex)
            {
                // 保存できなくても出走表の反映は止めない。
                Console.WriteLine($"出走馬ファイルを保存できませんでした（出走表の反映は継続）: {ex.Message}");
            }
        }
    }
}
