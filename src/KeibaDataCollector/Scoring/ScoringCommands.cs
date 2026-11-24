using System;
using System.Collections.Generic;
using System.Linq;

namespace KeibaDataCollector.Scoring
{
    /// <summary>
    /// 発売前スコアの手動コマンド（WordPressには書き込まない）。
    ///   calibrate : 過去1年のレースで重みを求め、data\scoring-weights.json に保存する。
    ///   score     : 指定日（またはレース）の印を計算して表示する。過去の日なら実際の人気・着順と並べる。
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
                                Console.WriteLine($"   {r.Mark ?? "  "} {r.Umaban,2} {r.HorseName,-12} {r.Score,7:0.00}");
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
