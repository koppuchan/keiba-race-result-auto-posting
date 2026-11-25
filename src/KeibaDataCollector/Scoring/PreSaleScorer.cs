using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace KeibaDataCollector.Scoring
{
    /// <summary>発売前スコアで付けた1レース分の印。</summary>
    internal sealed class ScoredRace
    {
        public DateTime ComputedAt { get; set; }
        /// <summary>使った重み（目安 / 調整済み）。あとで並行稼働の比較をするときの確認用。</summary>
        public string Weights { get; set; }
        /// <summary>印を付けられなかった理由。付けられたときは null。</summary>
        public string Skipped { get; set; }
        public List<ScoredRunner> Runners { get; set; } = new List<ScoredRunner>();

        /// <summary>馬番 → 印（印の付いた馬だけ）。</summary>
        public Dictionary<int, string> Marks() =>
            Runners.Where(r => !string.IsNullOrEmpty(r.Mark)).ToDictionary(r => r.Umaban, r => r.Mark);
    }

    internal sealed class ScoredRunner
    {
        public int Umaban { get; set; }
        public string HorseName { get; set; }
        public double Score { get; set; }
        public string Mark { get; set; }

        /// <summary>点数に効いた要素の上位（score コマンドの表示用。ファイルには保存しない）。</summary>
        [JsonIgnore] public string Reasons { get; set; }
    }

    /// <summary>
    /// 発売前スコア（予想印）の計算。オッズを使わず、過去データ（keiba-ai-data-system のDB）だけで付ける。
    ///
    /// 背景: 予想印は「朝一のオッズの人気順」で付けていたが、オッズは発売開始まで配信されない。
    /// 発売開始の遅い競馬場では締切直前まで印が出ず、お客様から改善のご依頼をいただいた。
    /// 公式データ（JRA-VAN / UmaConn）の過去成績・騎手・調教師・枠順・血統・調教から点数を付け、
    /// 9時の予想の時点で全レースに印を出せるようにする。重みは「最終的な人気順に近づく」ように
    /// 過去データで調整する（calibrate）。
    ///
    /// 計算した印は data\score-marks-yyyyMMdd.json に保存する。予想の処理は5分ごとに動くため、
    /// 1日1回だけ計算し、以降はファイルを読むだけにする（DBの読み込みには数十秒かかる）。
    /// </summary>
    internal static class PreSaleScorer
    {
        /// <summary>PredictionService と同じ印。スコアの高い順に付ける。</summary>
        public static readonly string[] Marks = { "◎", "○", "▲", "△" };

        // 騎手・父などの集計に使う過去の期間。先方が持っているのが3年分。
        private const int HistoryYears = 3;
        private const int TrainingDays = 15;

        private static string StorePath(DateTime date) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", $"score-marks-{date:yyyyMMdd}.json");

        public static Dictionary<string, ScoredRace> LoadStored(DateTime date)
        {
            try
            {
                var path = StorePath(date);
                if (File.Exists(path))
                    return JsonConvert.DeserializeObject<Dictionary<string, ScoredRace>>(File.ReadAllText(path))
                           ?? new Dictionary<string, ScoredRace>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"発売前スコアのファイルを読めませんでした（計算し直します）: {ex.Message}");
            }
            return new Dictionary<string, ScoredRace>();
        }

        private static void SaveStored(DateTime date, Dictionary<string, ScoredRace> races)
        {
            var path = StorePath(date);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(races, Formatting.Indented));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        /// <summary>
        /// 当日の全レースの発売前スコアをそろえて返す。計算済みのレースはファイルから読むだけ。
        /// 出走馬ファイル（朝一バッチが保存）や過去データのDBが無い場合は、計算できた分だけを返す
        /// （計算できなかったレースは、呼び出し側が従来どおりオッズの人気順で印を付ける）。
        /// 例外は投げない。
        /// </summary>
        public static Dictionary<string, ScoredRace> EnsureForDate(DateTime date, string dbPath)
        {
            var stored = LoadStored(date);
            var entries = RaceEntriesStore.Load(date);
            if (entries.Count == 0)
            {
                // 朝一バッチの前に入れ替えた日など。黙ってオッズの人気順に戻ると、比較が出ない理由が分からない。
                Console.WriteLine($"当日の出走馬ファイル（entries-{date:yyyyMMdd}.json）が無いため、発売前スコアは計算しません" +
                                  "（朝一バッチが保存します。予想はオッズの人気順で続行）。");
                return stored;
            }
            var missing = entries.Where(e => !stored.ContainsKey(e.Key)).ToList();
            if (missing.Count == 0) return stored;

            var started = DateTime.Now;
            try
            {
                var weights = ScoreWeights.Load();
                using (var db = new HistoryDb(dbPath))
                {
                    var index = BuildIndex(db, date, missing.SelectMany(e => e.Value.Runners).Select(r => r.KettoNum));
                    foreach (var race in missing)
                        stored[race.Key] = ScoreRace(index, date, race.Key, race.Value, weights);
                }
                SaveStored(date, stored);

                var marked = missing.Count(r => stored[r.Key].Skipped == null);
                Console.WriteLine(
                    $"発売前スコアを計算: {marked}/{missing.Count}レースに印（{weights.Describe()}、" +
                    $"所要 {(DateTime.Now - started).TotalSeconds:0}秒）");
            }
            catch (Exception ex)
            {
                // 保存しないので、次の回にもう一度計算を試みる。
                Console.WriteLine($"発売前スコアを計算できませんでした（予想はオッズの人気順で続行）: {ex.GetType().Name}: {ex.Message}");
            }
            return stored;
        }

        /// <summary>date の前日までの過去データを読み、date 時点の集計をそろえる。</summary>
        public static HistoryIndex BuildIndex(HistoryDb db, DateTime date, IEnumerable<string> extraKettoNums)
        {
            var runs = db.LoadRuns(date.AddYears(-HistoryYears), date);
            var trainings = db.LoadTraining(date.AddDays(-TrainingDays), date);
            var sires = db.LoadSires(runs.Select(r => r.KettoNum).Concat(extraKettoNums ?? Enumerable.Empty<string>()));
            var index = new HistoryIndex(runs, sires, trainings);
            index.AdvanceTo(HistoryDb.DayOf(date));
            return index;
        }

        /// <summary>1レースの印を付ける。index は AdvanceTo(date) 済みであること。</summary>
        public static ScoredRace ScoreRace(HistoryIndex index, DateTime date, string slug,
            RaceEntriesStore.Race race, ScoreWeights weights)
        {
            var result = new ScoredRace { ComputedAt = DateTime.Now, Weights = weights.Describe() };
            var key = Models.RaceKey.TryParseSlug(slug);
            var runners = (race.Runners ?? new List<RaceEntriesStore.Runner>())
                .Where(r => r.Umaban > 0)
                .GroupBy(r => r.Umaban).Select(g => g.Last()) // 同じ馬番が二重に届いた場合は後のもの
                .OrderBy(r => r.Umaban)
                .ToList();

            if (key == null) { result.Skipped = "レースキーが不正"; return result; }
            if (runners.Count < Marks.Length) { result.Skipped = $"出走{runners.Count}頭（印の数に足りない）"; return result; }

            var day = HistoryDb.DayOf(date);
            var raw = runners.Select(r => index.Features(day, key.TrackCode, race.TrackSurfaceCode, race.Distance,
                new RunnerInput
                {
                    Umaban = r.Umaban,
                    Waku = r.Waku,
                    HorseName = r.HorseName,
                    KettoNum = r.KettoNum,
                    JockeyCode = r.JockeyCode,
                    TrainerCode = r.TrainerCode,
                })).ToList();

            var x = ScoreModel.Standardize(raw);
            var w = weights.For(ScoreWeights.GroupOf(key.TrackCode));
            for (int i = 0; i < runners.Count; i++)
            {
                result.Runners.Add(new ScoredRunner
                {
                    Umaban = runners[i].Umaban,
                    HorseName = runners[i].HorseName,
                    Score = Math.Round(ScoreModel.Score(x[i], w), 4),
                    Reasons = DescribeReasons(raw[i], x[i], w),
                });
            }

            // 馬・騎手・調教師・父・調教のどれでも差が付かないレース（出走馬ファイルにコードが無い、
            // 全頭が過去データに無い等）は付けない（オッズの人気順に任せる）。
            // 枠順だけで差が付いても、それは馬番で並べたのとほとんど変わらないため数えない。
            var informative = Enumerable.Range(0, Factors.Count)
                .Any(j => j != Factors.Draw && x.Any(row => Math.Abs(row[j]) > 1e-9));
            if (!informative)
            {
                result.Runners.Clear();
                result.Skipped = "過去データで差が付かない";
                return result;
            }

            var ranked = result.Runners.OrderByDescending(r => r.Score).ThenBy(r => r.Umaban).ToList();
            for (int i = 0; i < Marks.Length; i++) ranked[i].Mark = Marks[i];
            return result;
        }

        /// <summary>
        /// 点数に効いた要素の上位3つ（例: 「タイム指数+0.52 近3走の着順+0.31 騎手複勝率-0.20」）。
        /// 点数はレース内の平均を0とした値なので、+ は平均より上、- は下に効いたことを表す。
        /// </summary>
        private static string DescribeReasons(double[] raw, double[] x, double[] w)
        {
            var top = Enumerable.Range(0, w.Length)
                .Select(j => (Label: Factors.Labels[j], Value: w[j] * x[j]))
                .Where(c => Math.Abs(c.Value) >= 0.005)
                .OrderByDescending(c => Math.Abs(c.Value))
                .Take(3)
                .Select(c => $"{c.Label}{c.Value:+0.00;-0.00}");
            var text = string.Join(" ", top);
            return raw[Factors.NoRecentRuns] == 1 ? "（近1年の出走記録なし） " + text : text;
        }

        /// <summary>
        /// 過去のレースを、その日の朝の時点の情報で再現する（重み調整・評価用）。
        /// fromDay 以上 toDay 未満の日のレースを日付順に返す。index は日付順に進める。
        /// </summary>
        public static List<RaceSample> PastRaceSamples(HistoryIndex index, int fromDay, int toDay)
        {
            var runs = index.Runs;
            var races = new SortedDictionary<(int Day, string Track, int Race), List<HistoryDb.Run>>();
            foreach (var r in runs)
            {
                if (r.Day < fromDay || r.Day >= toDay || r.RaceNumber <= 0 || r.TrackCode == null) continue;
                // 出走しなかった馬（取消・除外）は除く。中止など着順が無くても、オッズがあれば走った馬。
                if (r.Chakujun <= 0 && double.IsNaN(r.Odds)) continue;
                var key = (r.Day, r.TrackCode, r.RaceNumber);
                if (!races.TryGetValue(key, out var list)) races[key] = list = new List<HistoryDb.Run>();
                list.Add(r);
            }

            var samples = new List<RaceSample>();
            foreach (var race in races)
            {
                var members = race.Value.GroupBy(r => r.Umaban).Select(g => g.First()).OrderBy(r => r.Umaban).ToList();
                if (members.Count < 5) continue;

                index.AdvanceTo(race.Key.Day);
                var first = members[0];
                var raw = members.Select(r => index.Features(race.Key.Day, r.TrackCode, first.TrackSurfaceCode, first.Distance,
                    new RunnerInput
                    {
                        Umaban = r.Umaban,
                        Waku = r.Waku,
                        KettoNum = r.KettoNum,
                        JockeyCode = r.JockeyCode,
                        TrainerCode = r.TrainerCode,
                    })).ToList();

                double[] market = null;
                if (members.All(r => !double.IsNaN(r.Odds)))
                {
                    var inv = members.Select(r => 1 / r.Odds).ToArray();
                    var sum = inv.Sum();
                    market = inv.Select(v => v / sum).ToArray();
                }

                samples.Add(new RaceSample
                {
                    Group = ScoreWeights.GroupOf(race.Key.Track),
                    Day = race.Key.Day,
                    Slug = $"{HistoryDb.DateOf(race.Key.Day):yyyyMMdd}-{race.Key.Track}-{race.Key.Race}R",
                    X = ScoreModel.Standardize(raw),
                    MarketProb = market,
                    Chakujun = members.Select(r => r.Chakujun).ToArray(),
                    Umaban = members.Select(r => r.Umaban).ToArray(),
                });
            }
            return samples;
        }
    }
}
