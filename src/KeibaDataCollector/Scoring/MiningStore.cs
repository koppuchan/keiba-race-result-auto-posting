using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using KeibaDataCollector.Interop;

namespace KeibaDataCollector.Scoring
{
    /// <summary>
    /// JRAのデータマイニング予想（タイム型 DM / 対戦型 TM）を、こちらのSQLite（data\mining.sqlite3）に貯める。
    ///
    /// 発売前スコアの要素「JRAデータマイニング値」に使う（お客様のご要望。中央競馬のみ提供される）。
    /// 当日の値は前日の夕方に作られる（実測 2026-11-28 のレースは 11-27 17:27 作成）ので、
    /// 朝一バッチ（07:00）で取れる。重みを過去データで求めるには過去の値も要るため、
    /// 蓄積系の MING から過去1年分も取り込んでおく（MiningService）。
    ///
    /// keiba-ai-data-system のDBは読み取り専用で使っているので、こちらの値は別のファイルに持つ。
    /// </summary>
    internal sealed class MiningStore : IDisposable
    {
        private readonly SQLiteConnection _conn;

        public static string DefaultPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "mining.sqlite3");

        public MiningStore(string path = null)
        {
            path = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _conn = new SQLiteConnection($"Data Source={path};Version=3;BusyTimeout=10000;");
            _conn.Open();
            using (var cmd = new SQLiteCommand(@"
                CREATE TABLE IF NOT EXISTS mining (
                    race_date TEXT NOT NULL,
                    track_code TEXT NOT NULL,
                    race_number INTEGER NOT NULL,
                    umaban INTEGER NOT NULL,
                    dm_time REAL,        -- タイム型: 予想走破タイム（秒）。短いほど評価が高い
                    tm_score REAL,       -- 対戦型: 予測スコア。高いほど評価が高い
                    dm_made_at TEXT,
                    tm_made_at TEXT,
                    PRIMARY KEY (race_date, track_code, race_number, umaban)
                );", _conn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>DM/TM レコードを保存する（同じレース・馬番は新しい値で上書き）。保存したレース数を返す。</summary>
        public int Upsert(IEnumerable<JvRecordParser.MiningRecord> records)
        {
            var races = 0;
            using (var tx = _conn.BeginTransaction())
            {
                foreach (var r in records)
                {
                    if (r?.Key == null || (r.Kind != "DM" && r.Kind != "TM")) continue;
                    var isDm = r.Kind == "DM";
                    var sql = isDm
                        ? @"INSERT INTO mining (race_date, track_code, race_number, umaban, dm_time, dm_made_at)
                            VALUES (@d, @t, @r, @u, @v, @m)
                            ON CONFLICT(race_date, track_code, race_number, umaban)
                            DO UPDATE SET dm_time = excluded.dm_time, dm_made_at = excluded.dm_made_at;"
                        : @"INSERT INTO mining (race_date, track_code, race_number, umaban, tm_score, tm_made_at)
                            VALUES (@d, @t, @r, @u, @v, @m)
                            ON CONFLICT(race_date, track_code, race_number, umaban)
                            DO UPDATE SET tm_score = excluded.tm_score, tm_made_at = excluded.tm_made_at;";

                    foreach (var (umaban, raw) in r.Horses)
                    {
                        var value = isDm ? DmSeconds(raw) : TmScore(raw);
                        using (var cmd = new SQLiteCommand(sql, _conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@d", r.Key.RaceDate.ToString("yyyy-MM-dd"));
                            cmd.Parameters.AddWithValue("@t", r.Key.TrackCode);
                            cmd.Parameters.AddWithValue("@r", r.Key.RaceNumber);
                            cmd.Parameters.AddWithValue("@u", umaban);
                            cmd.Parameters.AddWithValue("@v", value.HasValue ? (object) value.Value : DBNull.Value);
                            cmd.Parameters.AddWithValue("@m", r.MadeAt ?? "");
                            cmd.ExecuteNonQuery();
                        }
                    }
                    races++;
                }
                tx.Commit();
            }
            return races;
        }

        /// <summary>
        /// 指定期間（from 以上 to 未満）の値。レースキー（例: 20261128-05-11R） → 馬番 → (タイム型の秒, 対戦型のスコア)。
        /// 値が無いものは NaN。
        /// </summary>
        public Dictionary<string, Dictionary<int, (double Time, double Score)>> Load(DateTime from, DateTime toExclusive)
        {
            var result = new Dictionary<string, Dictionary<int, (double, double)>>();
            using (var cmd = new SQLiteCommand(
                "SELECT race_date, track_code, race_number, umaban, dm_time, tm_score FROM mining " +
                "WHERE race_date >= @from AND race_date < @to;", _conn))
            {
                cmd.Parameters.AddWithValue("@from", from.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@to", toExclusive.ToString("yyyy-MM-dd"));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (!DateTime.TryParseExact(Convert.ToString(r.GetValue(0)), "yyyy-MM-dd",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                        var slug = $"{date:yyyyMMdd}-{Convert.ToString(r.GetValue(1))}-{Convert.ToInt32(r.GetValue(2))}R";
                        if (!result.TryGetValue(slug, out var horses))
                            result[slug] = horses = new Dictionary<int, (double, double)>();
                        horses[Convert.ToInt32(r.GetValue(3))] = (
                            r.IsDBNull(4) ? double.NaN : Convert.ToDouble(r.GetValue(4)),
                            r.IsDBNull(5) ? double.NaN : Convert.ToDouble(r.GetValue(5)));
                    }
                }
            }
            return result;
        }

        /// <summary>保存済みの最も新しいレース日（無ければ null）。過去分の取り込みをどこから始めるかに使う。</summary>
        public DateTime? LatestRaceDate()
        {
            using (var cmd = new SQLiteCommand("SELECT MAX(race_date) FROM mining;", _conn))
            {
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull || !DateTime.TryParseExact(Convert.ToString(v), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? (DateTime?) null : d;
            }
        }

        /// <summary>保存済みのレース数と期間（calibrate の冒頭に出す）。</summary>
        public string Describe()
        {
            using (var cmd = new SQLiteCommand(
                "SELECT COUNT(DISTINCT race_date || track_code || race_number), MIN(race_date), MAX(race_date) FROM mining;", _conn))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read() || r.IsDBNull(1)) return "データマイニング予想: まだありません（朝一バッチが取り込みます）";
                return $"データマイニング予想: {Convert.ToInt32(r.GetValue(0)):N0}レース（{r.GetValue(1)}～{r.GetValue(2)}）";
            }
        }

        /// <summary>
        /// 保存済みの値を読む。ファイルが無い・読めないときは空（発売前スコアはマイニング値なしで計算する）。
        /// </summary>
        public static Dictionary<string, Dictionary<int, (double Time, double Score)>> TryLoad(DateTime from, DateTime toExclusive)
        {
            try
            {
                if (!File.Exists(DefaultPath)) return new Dictionary<string, Dictionary<int, (double, double)>>();
                using (var store = new MiningStore())
                    return store.Load(from, toExclusive);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"データマイニング予想を読めませんでした（マイニング値なしで計算します）: {ex.Message}");
                return new Dictionary<string, Dictionary<int, (double, double)>>();
            }
        }

        /// <summary>タイム型の予想走破タイム（"mssff" 例: 13415 = 1分34秒15）を秒にする。0・空は null。</summary>
        public static double? DmSeconds(string raw)
        {
            if (!int.TryParse((raw ?? "").Trim(), out var v) || v <= 0) return null;
            return v / 10000 * 60 + v / 100 % 100 + v % 100 / 100.0;
        }

        /// <summary>対戦型の予測スコア。0・空は null。</summary>
        public static double? TmScore(string raw)
        {
            if (!int.TryParse((raw ?? "").Trim(), out var v) || v <= 0) return null;
            return v;
        }

        public void Dispose() => _conn.Dispose();
    }
}
