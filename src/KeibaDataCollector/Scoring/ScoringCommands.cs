using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KeibaDataCollector.Scoring
{
    /// <summary>
    /// 発売前スコアの手動コマンド（WordPressには書き込まない）。
    ///   calibrate : 過去1年のレースで重みを求め、data\scoring-weights.json に保存する。
    ///   score     : 指定日（またはレース）の印を計算して表示する。過去の日なら実際の人気・着順と並べる。
    ///   compare   : 並行稼働の比較。公開した印と発売前スコアの印を、実際の人気・着順と突き合わせる。
    /// </summary>
    internal static class ScoringCommands
    {
        private const int CalibrationDays = 365;
        // 最後の4週間は重みを求めるのに使わず、当て具合の確認だけに使う
        // （求めるのに使ったレースで測ると、実際より良く見えるため）。
        private const int TestDays = 28;
        // 過去データの最初の半年のレースは、重みを求めるのに使わない（Calibrate 内のコメント参照）。
        private const int WarmUpDays = 180;

        public static void Calibrate(string dbPath, DateTime today)
        {
            var started = DateTime.Now;
            using (var db = new HistoryDb(dbPath))
            {
                var from = today.AddDays(-CalibrationDays);
                Console.WriteLine($"過去データ: {db.DbPath}");
                foreach (var line in db.DescribeCoverage(from, today)) Console.WriteLine("  " + line);

                Console.WriteLine("過去データを読み込み中...");
                var runs = db.LoadRuns(today.AddYears(-3), today);
                var trainings = db.LoadTraining(from.AddDays(-15), today);
                var sires = db.LoadSires(runs.Select(r => r.KettoNum));
                Console.WriteLine($"  出走 {runs.Count:N0}件 / 調教 {trainings.Count:N0}件 / 父が分かる馬 {sires.Count:N0}頭 " +
                                  $"（{(DateTime.Now - started).TotalSeconds:0}秒）");

                // 区分（中央・地方・ばんえい）ごとの、過去データの最初の日。
                var firstDay = runs.GroupBy(r => ScoreWeights.GroupOf(r.TrackCode))
                                   .ToDictionary(g => g.Key, g => g.Min(r => r.Day));

                var index = new HistoryIndex(runs, sires, trainings);
                var samples = PreSaleScorer.PastRaceSamples(index, HistoryDb.DayOf(from), HistoryDb.DayOf(today));
                Console.WriteLine($"  再現したレース {samples.Count:N0}件（{(DateTime.Now - started).TotalSeconds:0}秒）");

                var current = ScoreWeights.Load();
                var fitted = new ScoreWeights { FittedAt = DateTime.Now };
                var testFrom = HistoryDb.DayOf(today.AddDays(-TestDays));

                foreach (var group in new[] { ScoreWeights.Jra, ScoreWeights.Nar, ScoreWeights.Banei })
                {
                    // 過去データの最初の半年のレースは使わない。そのころのレースは、どの馬も前の成績が
                    // ほとんど無い状態で再現され、「近1年の出走なし」などの重みが本番（1年分の成績がそろう）と
                    // ずれてしまうため。中央は先方のデータが1年分しかない（3年分の取得は JRA-VAN の
                    // ダイアログに手で答える必要があり、数時間かかる）ので、ここで避ける。
                    var usableFrom = firstDay.TryGetValue(group, out var first) ? first + WarmUpDays : int.MinValue;
                    var all = samples.Where(s => s.Group == group && s.Day >= usableFrom).ToList();
                    if (all.Count < 200)
                    {
                        Console.WriteLine($"[{GroupLabel(group)}] レースが少ないため調整しません（{all.Count}件）。");
                        if (current.Groups.TryGetValue(group, out var keep)) fitted.Groups[group] = keep;
                        continue;
                    }

                    // 確定オッズのあるレースが多ければ人気に、無ければ着順に合わせる（混ぜない）。
                    var withMarket = all.Count(s => s.MarketProb != null);
                    var useMarket = withMarket >= all.Count / 2;
                    var target = all.Where(s => useMarket ? s.MarketProb != null : s.Chakujun.Any(c => c > 0))
                                    .Select(s => useMarket ? s : WithoutMarket(s)).ToList();
                    var train = target.Where(s => s.Day < testFrom).ToList();
                    var test = target.Where(s => s.Day >= testFrom).ToList();

                    var before = current.For(group);
                    var weights = ScoreModel.Fit(train, before);
                    Console.WriteLine();
                    Console.WriteLine($"[{GroupLabel(group)}] {(useMarket ? "確定オッズの人気" : "着順（確定オッズが無いため）")}に合わせて調整 " +
                                      $"（調整 {train.Count:N0}レース / 確認 {test.Count:N0}レース）");
                    if (usableFrom > HistoryDb.DayOf(from))
                        Console.WriteLine($"  {HistoryDb.DateOf(usableFrom):yyyy-MM-dd} 以降のレースを使用" +
                                          "（過去データの最初の半年は、前の成績がそろっていないため使いません）");
                    if (test.Count > 0)
                    {
                        Console.WriteLine($"  調整前: {ScoreModel.DescribeMetrics(ScoreModel.Evaluate(test, before))}");
                        Console.WriteLine($"  調整後: {ScoreModel.DescribeMetrics(ScoreModel.Evaluate(test, weights))}");
                    }
                    else
                    {
                        Console.WriteLine("  確認用の直近4週間にレースが無いため、当て具合は出せません。");
                    }
                    var metrics = test.Count > 0 ? ScoreModel.Evaluate(test, weights) : new Dictionary<string, double>();

                    // 保存する重みは、確認用の4週間も含めた全期間で求め直したもの（最新の傾向を入れるため）。
                    weights = ScoreModel.Fit(target, weights, iterations: 150);
                    fitted.Groups[group] = new ScoreWeights.Group
                    {
                        Weights = ScoreWeights.ToMap(weights),
                        Target = useMarket ? "market" : "result",
                        Races = target.Count,
                        Metrics = metrics,
                    };

                    Console.WriteLine("  重み（大きいほど強く効く。0付近はほぼ効いていない）:");
                    foreach (var j in Enumerable.Range(0, Factors.Count).OrderByDescending(j => Math.Abs(weights[j])))
                        Console.WriteLine($"    {weights[j],7:0.000}  {Factors.Labels[j]}");
                }

                fitted.Save();
                Console.WriteLine();
                Console.WriteLine($"重みを保存しました: {ScoreWeights.PathFor()}（所要 {(DateTime.Now - started).TotalMinutes:0.0}分）");
            }
        }

        /// <summary>
        /// score [yyyy-MM-dd | レースキー]
        /// 当日で出走馬ファイルがあれば本番と同じ入力で計算する。過去の日は過去データから再現し、
        /// 実際の人気・着順と並べて表示する。ファイルには保存しない。
        /// </summary>
        public static void Score(string dbPath, string arg)
        {
            var slugKey = Models.RaceKey.TryParseSlug(arg);
            var date = slugKey?.RaceDate ?? (DateTime.TryParse(arg, out var d) ? d.Date : DateTime.Today);
            var onlySlug = slugKey?.AsSlug();
            var weights = ScoreWeights.Load();
            Console.WriteLine($"{date:yyyy-MM-dd} の発売前スコア（{weights.Describe()}）");

            using (var db = new HistoryDb(dbPath))
            {
                var entries = RaceEntriesStore.Load(date);
                if (entries.Count > 0)
                {
                    var index = PreSaleScorer.BuildIndex(db, date, entries.SelectMany(e => e.Value.Runners).Select(r => r.KettoNum));
                    foreach (var race in entries.OrderBy(e => e.Key))
                    {
                        if (onlySlug != null && race.Key != onlySlug) continue;
                        var scored = PreSaleScorer.ScoreRace(index, date, race.Key, race.Value, weights);
                        if (scored.Skipped != null)
                        {
                            Console.WriteLine($"{race.Key} 印なし（{scored.Skipped}）");
                            continue;
                        }
                        Console.WriteLine($"{race.Key} {FormatMarks(scored.Marks())}");
                        if (onlySlug != null)
                            foreach (var r in scored.Runners.OrderByDescending(r => r.Score))
                                Console.WriteLine($"   {r.Mark ?? "  "} {r.Umaban,2} {r.Score,6:0.00}  {r.HorseName}  {r.Reasons}");
                    }
                    return;
                }

                // 出走馬ファイルが無い日（過去の日）は、過去データにあるその日のレースを再現する。
                var runs = db.LoadRuns(date.AddYears(-3), date.AddDays(1));
                var trainings = db.LoadTraining(date.AddDays(-15), date);
                var sires = db.LoadSires(runs.Select(r => r.KettoNum));
                var history = new HistoryIndex(runs, sires, trainings);
                var day = HistoryDb.DayOf(date);
                var samples = PreSaleScorer.PastRaceSamples(history, day, day + 1)
                    .Where(s => onlySlug == null || s.Slug == onlySlug).ToList();
                if (samples.Count == 0)
                {
                    Console.WriteLine("対象のレースが見つかりません（出走馬ファイルも過去データもありません）。");
                    return;
                }

                foreach (var s in samples)
                {
                    var ranking = ScoreModel.Ranking(s, weights.For(s.Group));
                    var popularity = s.MarketProb == null ? null
                        : Enumerable.Range(0, s.X.Length).OrderByDescending(i => s.MarketProb[i]).ThenBy(i => i).ToList();

                    var marks = ranking.Take(PreSaleScorer.Marks.Length).Select((i, rank) =>
                    {
                        var info = new List<string>();
                        if (popularity != null) info.Add($"{popularity.IndexOf(i) + 1}番人気");
                        if (s.Chakujun[i] > 0) info.Add($"{s.Chakujun[i]}着");
                        return $"{PreSaleScorer.Marks[rank]}{s.Umaban[i]}（{string.Join("・", info)}）";
                    });
                    Console.WriteLine($"{s.Slug} {string.Join(" ", marks)}");
                }
                foreach (var group in samples.GroupBy(s => s.Group))
                    Console.WriteLine($"  [{GroupLabel(group.Key)}] " +
                                      ScoreModel.DescribeMetrics(ScoreModel.Evaluate(group.ToList(), weights.For(group.Key))));
            }
        }

        /// <summary>
        /// compare [yyyy-MM-dd]（省略時は前日）
        /// 並行稼働の比較。その日に公開した印（予想のログから）と、発売前スコアの印（score-marks ファイル）を、
        /// 確定オッズの人気・着順と突き合わせる。人気・着順は過去データのDBから取る
        /// （keiba-ai-data-system が毎晩2:00に取り込むので、前日分は翌朝から比べられる）。
        /// 参考として、同じ日を過去データから再現した印（calibrate と同じ計算）も並べる。
        /// </summary>
        public static void Compare(string dbPath, string arg)
        {
            var date = DateTime.TryParse(arg, out var d) ? d.Date : DateTime.Today.AddDays(-1);
            var weights = ScoreWeights.Load();
            var preSale = PreSaleScorer.LoadStored(date);
            var published = PublishedMarks(date);
            Console.WriteLine($"{date:yyyy-MM-dd} の予想印の比較（公開した印 {published.Count}レース / 発売前スコア {preSale.Count(p => p.Value.Skipped == null)}レース）");

            using (var db = new HistoryDb(dbPath))
            {
                var runs = db.LoadRuns(date.AddYears(-3), date.AddDays(1));
                var day = HistoryDb.DayOf(date);

                // 確定オッズの人気順と着順（馬番で表す）。結果がまだ取り込まれていないレースは除く。
                var outcome = runs.Where(r => r.Day == day && r.RaceNumber > 0 && r.TrackCode != null)
                    .GroupBy(r => $"{date:yyyyMMdd}-{r.TrackCode}-{r.RaceNumber}R")
                    .Select(g => new
                    {
                        Slug = g.Key,
                        Popularity = g.Where(r => !double.IsNaN(r.Odds)).OrderBy(r => r.Odds).ThenBy(r => r.Umaban)
                                      .Select(r => r.Umaban).Distinct().ToList(),
                        Chakujun = g.GroupBy(r => r.Umaban).ToDictionary(x => x.Key, x => x.First().Chakujun),
                    })
                    .Where(o => o.Popularity.Count > 0 || o.Chakujun.Values.Any(c => c > 0))
                    .ToDictionary(o => o.Slug);

                var history = new HistoryIndex(runs, db.LoadSires(runs.Select(r => r.KettoNum)),
                    db.LoadTraining(date.AddDays(-15), date));
                var replay = PreSaleScorer.PastRaceSamples(history, day, day + 1).ToDictionary(
                    s => s.Slug, s => ScoreModel.Ranking(s, weights.For(s.Group)).Take(4).Select(i => s.Umaban[i]).ToList());

                // 公開した印と発売前スコアの両方があり、結果も分かっているレースだけで比べる（同じレースで比べないと公平でない）。
                var common = outcome.Keys
                    .Where(k => published.ContainsKey(k) && preSale.TryGetValue(k, out var p) && p.Skipped == null)
                    .OrderBy(k => k).ToList();
                if (common.Count == 0)
                {
                    if (outcome.Count == 0)
                        Console.WriteLine("この日の結果がまだ過去データにありません（keiba-ai-data-system が毎晩2:00に取り込みます）。");
                    else if (preSale.Count == 0)
                        Console.WriteLine($"この日の発売前スコアのファイル（score-marks-{date:yyyyMMdd}.json）がありません。");
                    else
                        Console.WriteLine($"比べられるレースがありません（結果が分かっているレース {outcome.Count}件）。");
                    return;
                }

                ScoreModel.MarkedRace Marked(string slug, List<int> marks) => new ScoreModel.MarkedRace
                {
                    Marked = marks,
                    Popularity = outcome[slug].Popularity.Count > 0 ? outcome[slug].Popularity : null,
                    Chakujun = outcome[slug].Chakujun,
                };
                List<int> PreSaleList(string slug) => preSale[slug].Marks()
                    .OrderBy(m => Array.IndexOf(PreSaleScorer.Marks, m.Value)).Select(m => m.Key).ToList();

                foreach (var group in common.GroupBy(k => ScoreWeights.GroupOf(Models.RaceKey.TryParseSlug(k)?.TrackCode)))
                {
                    var slugs = group.ToList();
                    var both = slugs.Count(k => published[k].FirstOrDefault() == PreSaleList(k).FirstOrDefault());
                    var overlap = slugs.Average(k => published[k].Intersect(PreSaleList(k)).Count());
                    Console.WriteLine();
                    Console.WriteLine($"[{GroupLabel(group.Key)}] {slugs.Count}レース（◎が同じ {both}レース・印の重なり 平均{overlap:0.00}/4）");
                    Console.WriteLine("  公開した印          : " + ScoreModel.DescribeMetrics(
                        ScoreModel.EvaluateMarks(slugs.Select(k => Marked(k, published[k])).ToList())));
                    Console.WriteLine("  発売前スコア        : " + ScoreModel.DescribeMetrics(
                        ScoreModel.EvaluateMarks(slugs.Select(k => Marked(k, PreSaleList(k))).ToList())));
                    var replayed = slugs.Where(replay.ContainsKey).ToList();
                    if (replayed.Count > 0)
                        Console.WriteLine("  参考: 過去データで再現: " + ScoreModel.DescribeMetrics(
                            ScoreModel.EvaluateMarks(replayed.Select(k => Marked(k, replay[k])).ToList())));
                }
            }
        }

        /// <summary>
        /// その日に公開した印（予想のログの「予想を反映（◎8 ○1 ▲9 △6）」の行から）。レースキー → 馬番（◎○▲△の順）。
        /// ログは scheduled-predict.bat が logs\predict-yyyyMMdd.log に UTF-8 で書いている。
        /// </summary>
        private static Dictionary<string, List<int>> PublishedMarks(DateTime date)
        {
            var name = $"predict-{date:yyyyMMdd}.log";
            var path = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "logs", name),
                // exe は bin\Debug\net48 にあり、ログはその3つ上（scheduled-predict.bat と同じ場所）。
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "logs", name)),
            }.FirstOrDefault(File.Exists);

            var result = new Dictionary<string, List<int>>();
            if (path == null)
            {
                Console.WriteLine($"予想のログ（{name}）が見つかりません。公開した印とは比べられません。");
                return result;
            }

            var line = new Regex(@"(\d{8}-\w+-\d+R) 予想を反映（([^）]*)）");
            var mark = new Regex(@"([◎○▲△])(\d+)");
            foreach (var text in File.ReadLines(path, Encoding.UTF8))
            {
                var m = line.Match(text);
                if (!m.Success) continue;
                var marks = mark.Matches(m.Groups[2].Value).Cast<Match>()
                    .OrderBy(x => Array.IndexOf(PreSaleScorer.Marks, x.Groups[1].Value))
                    .Select(x => int.Parse(x.Groups[2].Value)).ToList();
                if (marks.Count > 0) result[m.Groups[1].Value] = marks;
            }
            return result;
        }

        private static RaceSample WithoutMarket(RaceSample s) => new RaceSample
        {
            Group = s.Group, Day = s.Day, Slug = s.Slug, X = s.X, MarketProb = null, Chakujun = s.Chakujun, Umaban = s.Umaban,
        };

        private static string FormatMarks(Dictionary<int, string> marks) =>
            string.Join(" ", marks.OrderBy(m => Array.IndexOf(PreSaleScorer.Marks, m.Value)).Select(m => $"{m.Value}{m.Key}"));

        private static string GroupLabel(string group) =>
            group == ScoreWeights.Jra ? "中央" : group == ScoreWeights.Banei ? "ばんえい" : "地方";
    }
}
