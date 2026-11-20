using System;
using System.Collections.Generic;
using System.Linq;

namespace KeibaDataCollector.Scoring
{
    /// <summary>
    /// 発売前スコアの要素（ファクター）。並び順がそのまま特徴量ベクトルの並びになる。
    /// どれも「値が大きいほど評価が高い」向きにそろえてある（着順などは符号を反転）。
    /// </summary>
    internal static class Factors
    {
        public const int Form3 = 0;          // 近3走の着順（頭数で割った位置、新しい走ほど重く）
        public const int PrevFinish = 1;     // 前走着順
        public const int PrevPopularity = 2; // 前走人気（確定オッズの人気順。データがある場合のみ）
        public const int Agari = 3;          // 近3走の上がり3F順位
        public const int TimeIndex = 4;      // タイム指数（走破タイムを条件別の標準タイムと比べた値）
        public const int JockeyWin = 5;      // 騎手勝率（直近1年）
        public const int JockeyTop3 = 6;     // 騎手複勝率（直近1年）
        public const int TrainerWin = 7;     // 調教師勝率（直近1年）
        public const int TrainerTop3 = 8;    // 調教師複勝率（直近1年）
        public const int Draw = 9;           // 枠順（同じ競馬場・芝ダ・距離での、その枠の複勝率）
        public const int Sire = 10;          // 父（同じ芝ダでの産駒の複勝率）
        public const int Training4F = 11;    // 調教 4ハロン（直近2週間の最終追い切り）
        public const int Training1F = 12;    // 調教 ラスト1ハロン
        public const int NoRecentRuns = 13;  // 直近1年に出走が無い（新馬・休み明け）

        public static readonly string[] Names =
        {
            "form3", "prevFinish", "prevPopularity", "agari3f", "timeIndex",
            "jockeyWin", "jockeyTop3", "trainerWin", "trainerTop3",
            "draw", "sire", "training4f", "training1f", "noRecentRuns",
        };

        public static readonly string[] Labels =
        {
            "近3走の着順", "前走着順", "前走人気", "上がり3F", "タイム指数",
            "騎手勝率", "騎手複勝率", "調教師勝率", "調教師複勝率",
            "枠順", "父（血統）", "調教4F", "調教ラスト1F", "近1年の出走なし",
        };

        public static int Count => Names.Length;
    }

    /// <summary>当日（または過去のある日）の出走馬1頭分の入力。</summary>
    internal sealed class RunnerInput
    {
        public int Umaban;
        public int Waku;
        public string HorseName;
        public string KettoNum;
        public string JockeyCode;
        public string TrainerCode;
    }

    /// <summary>
    /// 過去データから、ある日の時点での各要素の値を計算する。
    ///
    /// 「その日より前のデータだけ」を使うのが肝心。重み調整（calibrate）で過去の各レースを
    /// 再現するときに、その日以降の成績を混ぜると、本番では得られない情報で調整したことになり、
    /// 実際の精度より良く見えてしまう。そのため集計は日付順に積み上げていく形にしてあり、
    /// AdvanceTo(日) を呼ぶと「その日の前日までの成績」がそろう（日付は戻せない）。
    /// 当日の予想も、重み調整も、同じこのクラスで計算する（計算方法を食い違わせないため）。
    /// </summary>
    internal sealed class HistoryIndex
    {
        // 騎手・調教師の成績を数える期間。古い成績ほど今の調子と関係が薄いため、直近1年に絞る。
        private const int RecentWindowDays = 365;
        // 近走として見る期間。これより前の出走しか無い馬は「近1年の出走なし」とする。
        private const int FormWindowDays = 365;
        // 調教は、最終追い切り（レース当週）を見る。
        private const int TrainingWindowDays = 14;

        // 成績が少ないときに全体の平均へ寄せる強さ（「この件数分の平均的な成績があった」とみなす）。
        // 3戦1勝の騎手を勝率33%と評価しないため。
        private const double JockeyPrior = 30, TrainerPrior = 30, DrawPrior = 100, SirePrior = 30;

        private sealed class Counter
        {
            public int Starts, Wins, Top3;

            public void Add(int chakujun, int sign)
            {
                Starts += sign;
                if (chakujun == 1) Wins += sign;
                if (chakujun <= 3) Top3 += sign;
            }
        }

        private readonly List<HistoryDb.Run> _runs;
        private readonly double[] _finishRatio, _popRatio, _agariRatio, _timeZ;
        private readonly Dictionary<string, List<int>> _byHorse = new Dictionary<string, List<int>>();
        private readonly Dictionary<string, string> _sireOf;
        private readonly Dictionary<string, List<HistoryDb.Training>> _trainingByHorse =
            new Dictionary<string, List<HistoryDb.Training>>();
        private readonly Dictionary<(string Course, string Tresen), (double Mean4F, double Sd4F, double Mean1F, double Sd1F)>
            _trainingNorms = new Dictionary<(string, string), (double, double, double, double)>();

        private readonly Dictionary<string, Counter> _jockey = new Dictionary<string, Counter>();
        private readonly Dictionary<string, Counter> _trainer = new Dictionary<string, Counter>();
        private readonly Dictionary<(string Track, int Surface, int Distance, int Waku), Counter> _draw =
            new Dictionary<(string, int, int, int), Counter>();
        private readonly Dictionary<(string Sire, int Surface), Counter> _sire = new Dictionary<(string, int), Counter>();
        private readonly Counter _recentAll = new Counter(), _allTime = new Counter();
        private int _addPtr, _removePtr, _asOf = int.MinValue;

        public HistoryIndex(List<HistoryDb.Run> runs, Dictionary<string, string> sireOf,
            List<HistoryDb.Training> trainings)
        {
            _runs = runs;
            _sireOf = sireOf ?? new Dictionary<string, string>();
            int n = runs.Count;
            _finishRatio = Fill(n);
            _popRatio = Fill(n);
            _agariRatio = Fill(n);
            _timeZ = Fill(n);

            for (int i = 0; i < n; i++)
            {
                var k = runs[i].KettoNum;
                if (!_byHorse.TryGetValue(k, out var list)) _byHorse[k] = list = new List<int>();
                list.Add(i); // runs は日付順なので、各馬の一覧も日付順になる
            }

            ComputeWithinRaceRatios();
            ComputeTimeIndex();
            IndexTraining(trainings ?? new List<HistoryDb.Training>());
        }

        public IReadOnlyList<HistoryDb.Run> Runs => _runs;

        /// <summary>芝=1 / ダート=2 / 障害=3 / 不明=0（JV-Data コード表 2009 トラックコード）。</summary>
        public static int SurfaceOf(string trackSurfaceCode)
        {
            if (!int.TryParse(trackSurfaceCode, out var code)) return 0;
            if (code >= 10 && code <= 22) return 1;
            if (code >= 23 && code <= 29) return 2;
            if (code >= 51 && code <= 59) return 3;
            return 0;
        }

        /// <summary>レース内での位置（着順・人気・上がり）を 0（最上位）〜1（最下位）にそろえる。
        /// 頭数が違うレースの「5着」を同じに扱わないため。</summary>
        private void ComputeWithinRaceRatios()
        {
            foreach (var race in RaceGroups())
            {
                var finishers = race.Where(i => _runs[i].Chakujun > 0).ToList();
                if (finishers.Count > 1)
                    foreach (var i in finishers)
                        _finishRatio[i] = Math.Min(1.0, (_runs[i].Chakujun - 1) / (double) (finishers.Count - 1));

                Rank(race, i => _runs[i].Odds, _popRatio);
                Rank(finishers, i => _runs[i].Agari3F, _agariRatio);
            }
        }

        /// <summary>レース単位のまとまり（同じ日・競馬場・レース番号）。レース番号が不明な行は含めない。</summary>
        private IEnumerable<List<int>> RaceGroups()
        {
            var groups = new Dictionary<(int Day, string Track, int Race), List<int>>();
            for (int i = 0; i < _runs.Count; i++)
            {
                var r = _runs[i];
                if (r.RaceNumber <= 0) continue;
                var key = (r.Day, r.TrackCode, r.RaceNumber);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<int>();
                list.Add(i);
            }
            return groups.Values;
        }

        /// <summary>値の小さい順に 0〜1 の位置を付ける（同値は上の順位にそろえる）。値が無い馬は付けない。</summary>
        private static void Rank(List<int> members, Func<int, double> value, double[] output)
        {
            var valid = members.Where(i => !double.IsNaN(value(i))).ToList();
            if (valid.Count < 2) return;
            foreach (var i in valid)
            {
                var v = value(i);
                var better = valid.Count(j => value(j) < v);
                output[i] = better / (double) (valid.Count - 1);
            }
        }

        /// <summary>
        /// タイム指数: 走破タイムを、同じ競馬場・芝ダ・距離・馬場状態の平均と比べた値（標準偏差単位、速いほど大きい）。
        /// 走破タイムと馬場状態は keiba-ai-data-system に列の追加を依頼中で、列が無いうちは全頭 NaN（使わない）。
        /// 平均は手元の全期間から取る（条件ごとの標準タイムは年単位でほぼ変わらないため）。
        /// </summary>
        private void ComputeTimeIndex()
        {
            var groups = new Dictionary<(string, int, int, string), List<int>>();
            for (int i = 0; i < _runs.Count; i++)
            {
                var r = _runs[i];
                if (r.Chakujun <= 0 || double.IsNaN(r.FinishTime) || r.Going == null || r.Distance <= 0) continue;
                var key = (r.TrackCode, SurfaceOf(r.TrackSurfaceCode), r.Distance, r.Going);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<int>();
                list.Add(i);
            }

            foreach (var list in groups.Values)
            {
                if (list.Count < 30) continue; // 標準タイムとして当てにならない
                var times = list.Select(i => _runs[i].FinishTime).ToList();
                var mean = times.Average();
                var sd = Math.Sqrt(times.Sum(t => (t - mean) * (t - mean)) / times.Count);
                if (sd < 1e-6) continue;
                foreach (var i in list)
                    _timeZ[i] = Math.Max(-4, Math.Min(4, (mean - _runs[i].FinishTime) / sd));
            }
        }

        private void IndexTraining(List<HistoryDb.Training> trainings)
        {
            foreach (var t in trainings.OrderBy(t => t.Day))
            {
                if (!_trainingByHorse.TryGetValue(t.KettoNum, out var list))
                    _trainingByHorse[t.KettoNum] = list = new List<HistoryDb.Training>();
                list.Add(t);
            }

            // 坂路とウッド、美浦と栗東ではタイムの水準が違うので、それぞれの平均と比べる。
            foreach (var g in trainings.GroupBy(t => (t.Course ?? "", t.TresenKubun ?? "")))
            {
                var t4 = g.Select(t => t.Last4F).Where(v => !double.IsNaN(v) && v > 30 && v < 90).ToList();
                var t1 = g.Select(t => t.Last1F).Where(v => !double.IsNaN(v) && v > 9 && v < 25).ToList();
                _trainingNorms[g.Key] = (Mean(t4), Sd(t4), Mean(t1), Sd(t1));
            }
        }

        /// <summary>day の前日までの成績を集計に入れる。日付を戻すことはできない。</summary>
        public void AdvanceTo(int day)
        {
            if (day < _asOf) throw new InvalidOperationException("集計の日付は戻せません。");
            _asOf = day;

            while (_addPtr < _runs.Count && _runs[_addPtr].Day < day)
            {
                var r = _runs[_addPtr++];
                if (r.Chakujun <= 0) continue;
                Count(_jockey, r.JockeyCode, r.Chakujun, +1);
                Count(_trainer, r.TrainerCode, r.Chakujun, +1);
                _recentAll.Add(r.Chakujun, +1);
                _allTime.Add(r.Chakujun, +1);

                var surface = SurfaceOf(r.TrackSurfaceCode);
                if (surface > 0 && r.Distance > 0 && r.Waku > 0 && r.TrackCode != null)
                    Count(_draw, (r.TrackCode, surface, r.Distance, r.Waku), r.Chakujun, +1);
                if (_sireOf.TryGetValue(r.KettoNum, out var sire))
                    Count(_sire, (sire, surface), r.Chakujun, +1);
            }

            // 騎手・調教師は直近1年だけ数える（古い分を引く）。
            while (_removePtr < _addPtr && _runs[_removePtr].Day < day - RecentWindowDays)
            {
                var r = _runs[_removePtr++];
                if (r.Chakujun <= 0) continue;
                Count(_jockey, r.JockeyCode, r.Chakujun, -1);
                Count(_trainer, r.TrainerCode, r.Chakujun, -1);
                _recentAll.Add(r.Chakujun, -1);
            }
        }

        /// <summary>
        /// 1頭分の要素の値。値が無い要素は NaN（呼び出し側でレース内の平均扱いにする）。
        /// AdvanceTo(day) を呼んだ後に使うこと。
        /// </summary>
        public double[] Features(int day, string trackCode, string trackSurfaceCode, int distance, RunnerInput runner)
        {
            if (day != _asOf) throw new InvalidOperationException("AdvanceTo で日付をそろえてから呼んでください。");

            var f = Fill(Factors.Count);
            var surface = SurfaceOf(trackSurfaceCode);

            // 近走（直近1年、出走したものだけ。取消・除外は数えない）
            var recent = new List<int>(3);
            if (runner.KettoNum != null && _byHorse.TryGetValue(runner.KettoNum, out var history))
            {
                for (int i = history.Count - 1; i >= 0 && recent.Count < 3; i--)
                {
                    var run = _runs[history[i]];
                    if (run.Day >= day) continue;
                    if (run.Day < day - FormWindowDays) break;
                    if (run.Chakujun <= 0) continue;
                    recent.Add(history[i]);
                }
            }

            if (recent.Count > 0)
            {
                f[Factors.PrevFinish] = -FinishRatio(recent[0]);
                f[Factors.Form3] = -WeightedAverage(recent, FinishRatio);
                f[Factors.PrevPopularity] = -_popRatio[recent[0]];
                f[Factors.Agari] = -WeightedAverage(recent, i => _agariRatio[i]);
                var times = recent.Select(i => _timeZ[i]).Where(v => !double.IsNaN(v)).ToList();
                if (times.Count > 0) f[Factors.TimeIndex] = times.Max(); // 近3走のうち一番良いもの
            }
            f[Factors.NoRecentRuns] = recent.Count == 0 ? 1 : 0;

            if (runner.JockeyCode != null)
            {
                var c = Get(_jockey, runner.JockeyCode);
                f[Factors.JockeyWin] = Shrunk(c.Wins, c.Starts, _recentAll.Wins, _recentAll.Starts, JockeyPrior);
                f[Factors.JockeyTop3] = Shrunk(c.Top3, c.Starts, _recentAll.Top3, _recentAll.Starts, JockeyPrior);
            }
            if (runner.TrainerCode != null)
            {
                var c = Get(_trainer, runner.TrainerCode);
                f[Factors.TrainerWin] = Shrunk(c.Wins, c.Starts, _recentAll.Wins, _recentAll.Starts, TrainerPrior);
                f[Factors.TrainerTop3] = Shrunk(c.Top3, c.Starts, _recentAll.Top3, _recentAll.Starts, TrainerPrior);
            }

            if (surface > 0 && distance > 0 && runner.Waku > 0 && trackCode != null)
            {
                var c = Get(_draw, (trackCode, surface, distance, runner.Waku));
                f[Factors.Draw] = Shrunk(c.Top3, c.Starts, _allTime.Top3, _allTime.Starts, DrawPrior);
            }

            if (runner.KettoNum != null && _sireOf.TryGetValue(runner.KettoNum, out var sire))
            {
                var c = Get(_sire, (sire, surface));
                f[Factors.Sire] = Shrunk(c.Top3, c.Starts, _allTime.Top3, _allTime.Starts, SirePrior);
            }

            var training = LatestTraining(runner.KettoNum, day);
            if (training != null && _trainingNorms.TryGetValue((training.Course ?? "", training.TresenKubun ?? ""), out var norm))
            {
                // 速いほど大きい向き。極端な値（記録の誤りなど）に引っ張られないよう ±4 で止める。
                if (!double.IsNaN(training.Last4F) && norm.Sd4F > 0)
                    f[Factors.Training4F] = Clamp((norm.Mean4F - training.Last4F) / norm.Sd4F);
                if (!double.IsNaN(training.Last1F) && norm.Sd1F > 0)
                    f[Factors.Training1F] = Clamp((norm.Mean1F - training.Last1F) / norm.Sd1F);
            }

            return f;
        }

        /// <summary>レース前2週間の調教のうち、一番新しいもの（4ハロンの記録があるものを優先）。</summary>
        private HistoryDb.Training LatestTraining(string kettoNum, int day)
        {
            if (kettoNum == null || !_trainingByHorse.TryGetValue(kettoNum, out var list)) return null;
            HistoryDb.Training latest = null;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var t = list[i];
                if (t.Day >= day) continue;
                if (t.Day < day - TrainingWindowDays) break;
                if (!double.IsNaN(t.Last4F)) return t;
                latest = latest ?? t;
            }
            return latest;
        }

        private double FinishRatio(int i)
        {
            if (!double.IsNaN(_finishRatio[i])) return _finishRatio[i];
            // レース番号が不明でレース単位にまとめられなかった行。12頭立てとみなす。
            return Math.Min(1.0, (_runs[i].Chakujun - 1) / 11.0);
        }

        /// <summary>新しい走ほど重く（3:2:1）。値が無い走は除く。</summary>
        private static double WeightedAverage(List<int> recent, Func<int, double> value)
        {
            double sum = 0, weight = 0;
            for (int k = 0; k < recent.Count; k++)
            {
                var v = value(recent[k]);
                if (double.IsNaN(v)) continue;
                var w = 3 - k;
                sum += w * v;
                weight += w;
            }
            return weight > 0 ? sum / weight : double.NaN;
        }

        private static double Shrunk(int hits, int starts, int allHits, int allStarts, double prior)
        {
            var baseRate = allStarts > 0 ? allHits / (double) allStarts : 0;
            return (hits + prior * baseRate) / (starts + prior);
        }

        private static void Count<TKey>(Dictionary<TKey, Counter> map, TKey key, int chakujun, int sign)
        {
            if (key == null) return;
            if (!map.TryGetValue(key, out var c)) map[key] = c = new Counter();
            c.Add(chakujun, sign);
        }

        private static readonly Counter Empty = new Counter();

        private static Counter Get<TKey>(Dictionary<TKey, Counter> map, TKey key) =>
            map.TryGetValue(key, out var c) ? c : Empty;

        private static double[] Fill(int n)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = double.NaN;
            return a;
        }

        private static double Clamp(double v) => Math.Max(-4, Math.Min(4, v));

        private static double Mean(List<double> v) => v.Count > 0 ? v.Average() : double.NaN;

        private static double Sd(List<double> v)
        {
            if (v.Count < 2) return double.NaN;
            var m = v.Average();
            return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count);
        }
    }
}
