using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KeibaDataCollector.Scoring
{
    /// <summary>
    /// keiba-ai-data-system（同じVPSで動く別システム）が蓄積している過去データ（SQLite）を読む。
    ///
    /// 先方は JV-Link / UmaConn から過去3年分のレース成績・調教・血統を取り込み、毎日2:00に差分を足している。
    /// 同じ提供元から同じデータをもう一度取り込むと、提供元へのアクセスが倍になる
    /// （アクセスが過剰なIPは取得を制限されることがある）ため、こちらは読むだけにする。
    ///
    /// 読み取り専用で開き、書き込みは一切しない（先方の取り込みと同時に動いても壊さないため）。
    /// 先方のDBはWALモードなので、取り込み中でも読める。
    /// </summary>
    internal sealed class HistoryDb : IDisposable
    {
        /// <summary>過去の出走1件（race_entries の1行）。</summary>
        public sealed class Run
        {
            public string KettoNum;
            public int Day;                 // 日付（DayOf の値）
            public string TrackCode;        // 競馬場コード
            public int RaceNumber;          // 0 = 不明（先方の古い行）
            public string TrackSurfaceCode; // トラックコード（芝/ダート/障害）
            public int Distance;
            public int Waku;
            public int Umaban;
            public string JockeyCode;
            public string TrainerCode;
            public int Chakujun;            // 0 = 取消・除外・中止など
            public double Odds;             // 確定単勝オッズ。無ければ NaN
            public double Agari3F;          // 上がり3F（秒）。無ければ NaN
            public double FinishTime;       // 走破タイム（秒）。先方が列を追加するまでは NaN
            public string Going;            // 馬場状態コード。先方が列を追加するまでは null
        }

        /// <summary>調教1回分（training_laps の1行）。</summary>
        public sealed class Training
        {
            public string KettoNum;
            public int Day;
            public string Course;       // Slope（坂路）/ WoodChip（ウッドチップ）
            public string TresenKubun;  // トレセン区分（美浦/栗東）
            public double Last1F;       // ラスト1ハロン（秒）。無ければ NaN
            public double Last4F;       // 4ハロン合計（秒）。無ければ NaN
        }

        // 走破タイム・馬場状態は、先方に列を追加してもらった（2026-11-24）。
        // 列があれば読み、無ければ（古いDBなど）タイム指数を使わずに続ける。
        private const string FinishTimeColumn = "soha_time";
        private const string GoingColumn = "baba_code";

        private readonly SQLiteConnection _conn;
        private readonly Dictionary<string, string> _pool = new Dictionary<string, string>();

        public string DbPath { get; }

        /// <summary>走破タイムと馬場状態の列があるか（タイム指数を計算できるか）。</summary>
        public bool HasFinishTime { get; }

        public HistoryDb(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException($"過去データのDBが見つかりません: {path}");

            DbPath = path;
            _conn = new SQLiteConnection($"Data Source={path};Version=3;Read Only=True;BusyTimeout=10000;");
            _conn.Open();

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SQLiteCommand("PRAGMA table_info(race_entries);", _conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read()) columns.Add(Convert.ToString(reader["name"]));
            }
            if (columns.Count == 0)
                throw new InvalidOperationException($"過去データのDBに race_entries がありません: {path}");
            HasFinishTime = columns.Contains(FinishTimeColumn) && columns.Contains(GoingColumn);
        }

        /// <summary>日付を整数（0001-01-01 からの日数）にする。比較と引き算を軽くするため。</summary>
        public static int DayOf(DateTime date) => (int) (date.Date.Ticks / TimeSpan.TicksPerDay);

        public static DateTime DateOf(int day) => new DateTime(day * TimeSpan.TicksPerDay);

        /// <summary>指定期間（from 以上 to 未満）の出走を読む。日付の昇順。</summary>
        public List<Run> LoadRuns(DateTime from, DateTime toExclusive)
        {
            var sql =
                "SELECT ketto_num, race_date, track_code, race_number, track_surface_code, distance, waku, umaban, " +
                "jockey_code, trainer_code, chakujun, tansho_odds, agari_3f" +
                (HasFinishTime ? $", {FinishTimeColumn}, {GoingColumn}" : "") +
                " FROM race_entries WHERE race_date >= @from AND race_date < @to;";

            var runs = new List<Run>();
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                cmd.Parameters.AddWithValue("@from", from.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@to", toExclusive.ToString("yyyy-MM-dd"));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var ketto = Text(r, 0);
                        if (ketto == null || !TryParseDay(Text(r, 1), out var day)) continue;
                        runs.Add(new Run
                        {
                            KettoNum = ketto,
                            Day = day,
                            TrackCode = Text(r, 2),
                            RaceNumber = Int(r, 3),
                            TrackSurfaceCode = Text(r, 4),
                            Distance = Int(r, 5),
                            Waku = Int(r, 6),
                            Umaban = Int(r, 7),
                            JockeyCode = Text(r, 8),
                            TrainerCode = Text(r, 9),
                            Chakujun = Int(r, 10),
                            Odds = Real(r, 11),
                            Agari3F = Real(r, 12),
                            FinishTime = HasFinishTime ? Real(r, 13) : double.NaN,
                            Going = HasFinishTime ? Text(r, 14) : null,
                        });
                    }
                }
            }
            runs.Sort((a, b) => a.Day.CompareTo(b.Day));
            return runs;
        }

        /// <summary>
        /// 指定期間（from 以上 to 未満）の調教を読む。
        /// 先方はラップを「ラスト1F, 2F目, 3F目, 4F目, …」の順のカンマ区切りで保存している
        /// （JV-Data の LapTime1 = 200M-0M が先頭）。
        /// </summary>
        public List<Training> LoadTraining(DateTime from, DateTime toExclusive)
        {
            var list = new List<Training>();
            using (var cmd = new SQLiteCommand(
                "SELECT ketto_num, chokyo_date, course, tresen_kubun, lap_times_seconds FROM training_laps " +
                "WHERE chokyo_date >= @from AND chokyo_date < @to;", _conn))
            {
                cmd.Parameters.AddWithValue("@from", from.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@to", toExclusive.ToString("yyyy-MM-dd"));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var ketto = Text(r, 0);
                        if (ketto == null || !TryParseDay(Text(r, 1), out var day)) continue;

                        var laps = (Text(r, 4) ?? "").Split(',');
                        var lap = new double[4];
                        var complete = laps.Length >= 4;
                        for (int i = 0; i < 4; i++)
                        {
                            lap[i] = i < laps.Length && double.TryParse(laps[i], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var v) && v > 0 ? v : double.NaN;
                            if (double.IsNaN(lap[i])) complete = false;
                        }

                        list.Add(new Training
                        {
                            KettoNum = ketto,
                            Day = day,
                            Course = Text(r, 2),
                            TresenKubun = Text(r, 3),
                            Last1F = lap[0],
                            Last4F = complete ? lap.Sum() : double.NaN,
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>馬（血統登録番号） → 父（繁殖登録番号）。父が不明の馬は含まない。</summary>
        public Dictionary<string, string> LoadSires(IEnumerable<string> kettoNums)
        {
            var result = new Dictionary<string, string>();
            const int chunk = 500; // SQLite の変数の上限（既定999）に収まる件数ずつ聞く
            var all = kettoNums.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
            for (int start = 0; start < all.Count; start += chunk)
            {
                var part = all.Skip(start).Take(chunk).ToList();
                using (var cmd = new SQLiteCommand { Connection = _conn })
                {
                    var names = new List<string>();
                    for (int i = 0; i < part.Count; i++)
                    {
                        names.Add("@k" + i);
                        cmd.Parameters.AddWithValue("@k" + i, part[i]);
                    }
                    cmd.CommandText =
                        "SELECT ketto_num, sire_hansyoku_num FROM pedigree_links " +
                        $"WHERE ketto_num IN ({string.Join(",", names)}) AND sire_hansyoku_num IS NOT NULL;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var ketto = Text(r, 0);
                            var sire = Text(r, 1);
                            if (ketto != null && sire != null) result[ketto] = sire;
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>データの充足状況（calibrate の冒頭に出す）。どの要素が使えるかの確認用。</summary>
        public List<string> DescribeCoverage(DateTime from, DateTime toExclusive)
        {
            var lines = new List<string>();
            using (var cmd = new SQLiteCommand(
                "SELECT CASE WHEN CAST(track_code AS INTEGER) BETWEEN 1 AND 10 THEN '中央' ELSE '地方' END AS grp, " +
                "COUNT(*), SUM(race_number IS NOT NULL AND race_number > 0), SUM(tansho_odds IS NOT NULL AND tansho_odds > 0), " +
                "SUM(agari_3f IS NOT NULL AND agari_3f > 0), MIN(race_date), MAX(race_date)" +
                (HasFinishTime ? $", SUM({FinishTimeColumn} IS NOT NULL AND {FinishTimeColumn} > 0), SUM({GoingColumn} IS NOT NULL)" : "") +
                " FROM race_entries WHERE race_date >= @from AND race_date < @to GROUP BY grp;", _conn))
            {
                cmd.Parameters.AddWithValue("@from", from.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@to", toExclusive.ToString("yyyy-MM-dd"));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var total = Convert.ToDouble(r.GetValue(1));
                        string Pct(int i) => total > 0 ? $"{Convert.ToDouble(r.GetValue(i)) / total:P0}" : "-";
                        // 期間の区切りは「～」(U+FF5E)。「〜」(U+301C) は Windows の画面（CP932）では「?」になる。
                        lines.Add(
                            $"{r.GetString(0)}: {total:N0}件（{Text(r, 5)}～{Text(r, 6)}） " +
                            $"レース番号あり {Pct(2)} / 確定オッズあり {Pct(3)} / 上がり3Fあり {Pct(4)}" +
                            (HasFinishTime ? $" / 走破タイムあり {Pct(7)} / 馬場状態あり {Pct(8)}" : ""));
                    }
                }
            }
            lines.Add(HasFinishTime
                ? "走破タイム・馬場状態: あり（タイム指数を使います）"
                : "走破タイム・馬場状態: なし（タイム指数は使いません。keiba-ai-data-system の race_entries に soha_time・baba_code の列が必要）");
            return lines;
        }

        private string Text(SQLiteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return null;
            var s = Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            // 同じ騎手コード・競馬場コードなどが何十万回も出てくるので、同じ文字列を使い回してメモリを抑える。
            if (_pool.TryGetValue(s, out var pooled)) return pooled;
            _pool[s] = s;
            return s;
        }

        private static int Int(SQLiteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return 0;
            try { return Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture); }
            catch (FormatException) { return 0; }
        }

        private static double Real(SQLiteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return double.NaN;
            try
            {
                var v = Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
                return v > 0 ? v : double.NaN;
            }
            catch (FormatException) { return double.NaN; }
        }

        private static bool TryParseDay(string yyyyMMdd, out int day)
        {
            day = 0;
            if (!DateTime.TryParseExact(yyyyMMdd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) return false;
            day = DayOf(date);
            return true;
        }

        public void Dispose() => _conn.Dispose();
    }
}
