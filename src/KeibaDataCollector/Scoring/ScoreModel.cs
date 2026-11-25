using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace KeibaDataCollector.Scoring
{
    /// <summary>
    /// 要素ごとの重み。中央・地方・ばんえいで別々に持つ（データの揃い方もレースの性質も違うため）。
    /// calibrate で過去データから求め、data\scoring-weights.json に保存する。
    /// ファイルが無い間は、下の既定値（手で決めた目安）を使う。
    /// </summary>
    internal sealed class ScoreWeights
    {
        public const string Jra = "jra", Nar = "nar", Banei = "banei";

        public sealed class Group
        {
            public Dictionary<string, double> Weights { get; set; } = new Dictionary<string, double>();
            /// <summary>何に合わせて求めたか（market = 確定オッズの人気 / result = 着順）。</summary>
            public string Target { get; set; }
            public int Races { get; set; }
            public Dictionary<string, double> Metrics { get; set; } = new Dictionary<string, double>();
        }

        public DateTime? FittedAt { get; set; }
        public Dictionary<string, Group> Groups { get; set; } = new Dictionary<string, Group>();

        [JsonIgnore] public bool IsDefault => !FittedAt.HasValue;

        public static string GroupOf(string trackCode)
        {
            if (!int.TryParse(trackCode, out var code)) return Nar;
            if (code >= 1 && code <= 10) return Jra;
            if (code == 83) return Banei; // 帯広（ばんえい）
            return Nar;
        }

        /// <summary>目安の重み（要素の並びは Factors.Names）。calibrate 前はこれで計算する。</summary>
        private static readonly double[] DefaultWeights =
        {
            0.6,  // form3
            0.3,  // prevFinish
            0.8,  // prevPopularity
            0.3,  // agari3f
            0.5,  // timeIndex
            0.4,  // jockeyWin
            0.3,  // jockeyTop3
            0.2,  // trainerWin
            0.2,  // trainerTop3
            0.1,  // draw
            0.2,  // sire
            0.2,  // training4f
            0.1,  // training1f
            0.0,  // noRecentRuns
        };

        public double[] For(string group)
        {
            var w = (double[]) DefaultWeights.Clone();
            if (Groups.TryGetValue(group, out var g))
            {
                for (int i = 0; i < Factors.Count; i++)
                    if (g.Weights.TryGetValue(Factors.Names[i], out var v)) w[i] = v;
            }
            return w;
        }

        public static Dictionary<string, double> ToMap(double[] weights)
        {
            var map = new Dictionary<string, double>();
            for (int i = 0; i < Factors.Count; i++) map[Factors.Names[i]] = Math.Round(weights[i], 4);
            return map;
        }

        public static string PathFor() =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "scoring-weights.json");

        public static ScoreWeights Load()
        {
            try
            {
                var path = PathFor();
                if (File.Exists(path))
                    return JsonConvert.DeserializeObject<ScoreWeights>(File.ReadAllText(path)) ?? new ScoreWeights();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"重みのファイルを読めませんでした（目安の重みで計算します）: {ex.Message}");
            }
            return new ScoreWeights();
        }

        public void Save()
        {
            var path = PathFor();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        public string Describe() => IsDefault ? "目安の重み（calibrate 前）" : $"調整済みの重み（{FittedAt:yyyy-MM-dd}）";
    }

    /// <summary>
    /// 1レース分のデータ（重み調整・評価用）。X は出走馬×要素（レース内で標準化済み）。
    /// 正解は、確定オッズがあれば人気（オッズから求めた勝つ確率）、無ければ着順。
    /// </summary>
    internal sealed class RaceSample
    {
        public string Group;
        public int Day;
        public string Slug;
        public double[][] X;
        public double[] MarketProb;  // null = 確定オッズ無し
        public int[] Chakujun;       // 0 = 着順なし
        public int[] Umaban;
    }

    internal static class ScoreModel
    {
        /// <summary>
        /// レース内で各要素を標準化する（平均0・標準偏差1）。値が無い馬は 0（そのレースの平均並み）にする。
        /// 要素ごとの単位（勝率・秒・順位）の違いを消し、重みをそのまま「効き具合」として比べられるようにするため。
        /// </summary>
        public static double[][] Standardize(IList<double[]> raw)
        {
            int n = raw.Count, k = Factors.Count;
            var x = new double[n][];
            for (int i = 0; i < n; i++) x[i] = new double[k];

            for (int j = 0; j < k; j++)
            {
                double sum = 0, sq = 0;
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    var v = raw[i][j];
                    if (double.IsNaN(v)) continue;
                    sum += v; sq += v * v; count++;
                }
                if (count < 2) continue;
                var mean = sum / count;
                var sd = Math.Sqrt(Math.Max(0, sq / count - mean * mean));
                if (sd < 1e-9) continue;
                for (int i = 0; i < n; i++)
                {
                    var v = raw[i][j];
                    x[i][j] = double.IsNaN(v) ? 0 : (v - mean) / sd;
                }
            }
            return x;
        }

        public static double Score(double[] x, double[] w)
        {
            double s = 0;
            for (int j = 0; j < w.Length; j++) s += x[j] * w[j];
            return s;
        }

        /// <summary>
        /// 重みを求める。レースごとに「スコアから計算した勝つ確率」が正解に近づくように調整する
        /// （条件付きロジット。正解が人気なら人気の確率分布に、着順なら1〜3着の順に合わせる）。
        /// 重みが極端な値にならないよう、小さな罰則（L2）を付ける。
        /// </summary>
        public static double[] Fit(IList<RaceSample> samples, double[] initial, int iterations = 400, double l2 = 0.001)
        {
            var w = (double[]) initial.Clone();
            int k = w.Length;
            var m = new double[k];
            var v = new double[k];
            const double lr = 0.05, b1 = 0.9, b2 = 0.999, eps = 1e-8;

            for (int it = 1; it <= iterations; it++)
            {
                var grad = new double[k];
                foreach (var s in samples) AddGradient(s, w, grad);
                for (int j = 0; j < k; j++)
                {
                    var g = grad[j] / samples.Count + 2 * l2 * w[j];
                    m[j] = b1 * m[j] + (1 - b1) * g;
                    v[j] = b2 * v[j] + (1 - b2) * g * g;
                    var mHat = m[j] / (1 - Math.Pow(b1, it));
                    var vHat = v[j] / (1 - Math.Pow(b2, it));
                    w[j] -= lr * mHat / (Math.Sqrt(vHat) + eps);
                }
            }
            return w;
        }

        private static void AddGradient(RaceSample s, double[] w, double[] grad)
        {
            int n = s.X.Length;
            var score = new double[n];
            for (int i = 0; i < n; i++) score[i] = Score(s.X[i], w);

            if (s.MarketProb != null)
            {
                var q = Softmax(score, Enumerable.Range(0, n).ToList());
                for (int i = 0; i < n; i++)
                {
                    var d = q[i] - s.MarketProb[i];
                    for (int j = 0; j < grad.Length; j++) grad[j] += d * s.X[i][j];
                }
                return;
            }

            // 着順: 1着を全頭の中から、2着を残りの中から、3着をその残りから選ぶ確率を上げる。
            var order = Enumerable.Range(0, n).Where(i => s.Chakujun[i] > 0).OrderBy(i => s.Chakujun[i]).ToList();
            var remaining = Enumerable.Range(0, n).ToList();
            for (int place = 0; place < Math.Min(3, order.Count); place++)
            {
                var q = Softmax(score, remaining);
                for (int r = 0; r < remaining.Count; r++)
                {
                    var i = remaining[r];
                    var d = q[r] - (i == order[place] ? 1 : 0);
                    for (int j = 0; j < grad.Length; j++) grad[j] += d * s.X[i][j];
                }
                remaining.Remove(order[place]);
            }
        }

        private static double[] Softmax(double[] score, List<int> members)
        {
            var max = members.Max(i => score[i]);
            var e = members.Select(i => Math.Exp(score[i] - max)).ToArray();
            var sum = e.Sum();
            for (int r = 0; r < e.Length; r++) e[r] /= sum;
            return e;
        }

        /// <summary>スコアの高い順の出走馬の位置（同点は前の馬を上に）。</summary>
        public static List<int> Ranking(RaceSample s, double[] w) =>
            Enumerable.Range(0, s.X.Length).OrderByDescending(i => Score(s.X[i], w)).ThenBy(i => i).ToList();

        /// <summary>
        /// 当て具合。印は上位4頭（◎○▲△）。
        ///   人気（確定オッズ）があるレース: ◎が1番人気の割合、印の馬が5番人気以内の割合、
        ///     印4頭と人気上位4頭の重なり、スコア順と人気順の順位相関
        ///   着順だけのレース: ◎の勝率・複勝率、印の馬が3着以内の割合
        /// </summary>
        public static Dictionary<string, double> Evaluate(IList<RaceSample> samples, double[] w) =>
            EvaluateMarks(samples.Select(s =>
            {
                var ranking = Ranking(s, w);
                return new MarkedRace
                {
                    Marked = ranking.Take(4).ToList(),
                    FullRanking = ranking,
                    Popularity = s.MarketProb == null ? null
                        : Enumerable.Range(0, s.X.Length).OrderByDescending(i => s.MarketProb[i]).ThenBy(i => i).ToList(),
                    Chakujun = Enumerable.Range(0, s.X.Length).ToDictionary(i => i, i => s.Chakujun[i]),
                };
            }).ToList());

        /// <summary>1レース分の印と、実際の人気・着順（当て具合の集計用）。馬は馬番などの番号で表す。</summary>
        public sealed class MarkedRace
        {
            /// <summary>印の付いた馬（◎○▲△の順）。</summary>
            public List<int> Marked;
            /// <summary>全頭の順位（順位相関を出すときだけ。印しか分からない場合は null）。</summary>
            public List<int> FullRanking;
            /// <summary>確定オッズの人気順（無ければ null）。</summary>
            public List<int> Popularity;
            /// <summary>着順（0 = 着順なし）。</summary>
            public Dictionary<int, int> Chakujun;
        }

        /// <summary>印の当て具合を集計する（Evaluate の説明を参照）。</summary>
        public static Dictionary<string, double> EvaluateMarks(IList<MarkedRace> races)
        {
            int market = 0, result = 0, ranked = 0;
            double favTop = 0, marksInTop5 = 0, overlap4 = 0, spearman = 0;
            double favWin = 0, favTop3 = 0, marksInTop3 = 0;

            foreach (var r in races)
            {
                var marked = r.Marked;
                if (marked.Count == 0) continue;

                if (r.Popularity != null && r.Popularity.Count > 0)
                {
                    market++;
                    if (marked[0] == r.Popularity[0]) favTop++;
                    var top5 = new HashSet<int>(r.Popularity.Take(5));
                    marksInTop5 += marked.Count(i => top5.Contains(i)) / (double) marked.Count;
                    overlap4 += marked.Intersect(r.Popularity.Take(4)).Count();
                    if (r.FullRanking != null && r.FullRanking.Count == r.Popularity.Count)
                    {
                        ranked++;
                        spearman += Spearman(r.FullRanking, r.Popularity);
                    }
                }

                int Place(int i) => r.Chakujun != null && r.Chakujun.TryGetValue(i, out var c) ? c : 0;
                if (r.Chakujun != null && r.Chakujun.Values.Any(c => c > 0))
                {
                    result++;
                    if (Place(marked[0]) == 1) favWin++;
                    if (Place(marked[0]) >= 1 && Place(marked[0]) <= 3) favTop3++;
                    marksInTop3 += marked.Count(i => Place(i) >= 1 && Place(i) <= 3) / (double) marked.Count;
                }
            }

            var metrics = new Dictionary<string, double> { ["races"] = races.Count };
            if (market > 0)
            {
                metrics["honmeiIsFavorite"] = Math.Round(favTop / market, 3);
                metrics["marksInPopularTop5"] = Math.Round(marksInTop5 / market, 3);
                metrics["overlapWithPopularTop4"] = Math.Round(overlap4 / market, 2);
            }
            if (ranked > 0) metrics["spearmanWithPopularity"] = Math.Round(spearman / ranked, 3);
            if (result > 0)
            {
                metrics["honmeiWin"] = Math.Round(favWin / result, 3);
                metrics["honmeiTop3"] = Math.Round(favTop3 / result, 3);
                metrics["marksInTop3"] = Math.Round(marksInTop3 / result, 3);
            }
            return metrics;
        }

        /// <summary>2つの順位付けの順位相関（1 = 完全に一致）。</summary>
        private static double Spearman(List<int> a, List<int> b)
        {
            int n = a.Count;
            if (n < 2) return 0;
            var rankA = new int[n];
            var rankB = new int[n];
            for (int r = 0; r < n; r++) { rankA[a[r]] = r; rankB[b[r]] = r; }
            double d2 = 0;
            for (int i = 0; i < n; i++) d2 += Math.Pow(rankA[i] - rankB[i], 2);
            return 1 - 6 * d2 / (n * ((double) n * n - 1));
        }

        /// <summary>評価結果を人が読める1行にする。</summary>
        public static string DescribeMetrics(Dictionary<string, double> m)
        {
            var parts = new List<string> { $"{m["races"]:N0}レース" };
            if (m.TryGetValue("honmeiIsFavorite", out var a)) parts.Add($"◎=1番人気 {a:P0}");
            if (m.TryGetValue("marksInPopularTop5", out var b)) parts.Add($"印が5番人気以内 {b:P0}");
            if (m.TryGetValue("overlapWithPopularTop4", out var c)) parts.Add($"人気上位4頭との重なり {c:0.00}/4");
            if (m.TryGetValue("spearmanWithPopularity", out var d)) parts.Add($"人気との順位相関 {d:0.00}");
            if (m.TryGetValue("honmeiWin", out var e)) parts.Add($"◎の勝率 {e:P0}");
            if (m.TryGetValue("honmeiTop3", out var f)) parts.Add($"◎の複勝率 {f:P0}");
            if (m.TryGetValue("marksInTop3", out var g)) parts.Add($"印が3着以内 {g:P0}");
            return string.Join(" / ", parts);
        }
    }
}
