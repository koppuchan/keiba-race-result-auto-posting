using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.Scoring;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// JRAのデータマイニング予想を取り込み、data\mining.sqlite3（MiningStore）に保存する。中央競馬（JV-Link）のみ。
    ///
    /// 朝一バッチ（07:00）の最後に呼ぶ。JV-Link はそこで既に開いているので、別に起動しなくてよい。
    ///   1. 過去分: 蓄積系 MING（前回保存した日の1週間前から。初回は過去1年分）
    ///      → 発売前スコアの重みを求める（calibrate）ための材料
    ///   2. 当日分: 速報系 0B13（タイム型）・0B17（対戦型）をレースごとに
    ///      → 9時の発売前スコアで使う。前日の夕方に作られる（実測 2026-11-27 17:27 作成）
    /// 当日分は蓄積系にはまだ載っていない（実測 2026-11-28 09:42、MING に当日のレースは無し）。
    ///
    /// UmaConn（地方競馬）は提供していない（0B17・MING は rc=-111、0B13 は該当データ無し）。
    /// 取り込めなくても朝一バッチ・予想は止めない（マイニング値なしで計算される）。
    /// </summary>
    public class MiningService
    {
        private readonly IRaceDataSource _source;

        // 初回に遡る期間。重みを求める期間（過去1年）に合わせる。
        private const int BackfillDays = 365;

        public MiningService(IRaceDataSource source)
        {
            _source = source;
        }

        public void Collect(DateTime targetDate, IEnumerable<RaceKey> todaysRaces)
        {
            using (var store = new MiningStore())
            {
                var latest = store.LatestRaceDate();
                var from = latest.HasValue ? latest.Value.AddDays(-7) : targetDate.AddDays(-BackfillDays);
                var past = ReadAccumulated(from);
                var pastRaces = store.Upsert(past);
                Console.WriteLine($"[{_source.SourceName}] データマイニング予想（過去分 {from:yyyy-MM-dd}〜）: {pastRaces}レースを保存");

                var today = new List<JvRecordParser.MiningRecord>();
                foreach (var race in todaysRaces)
                {
                    today.AddRange(ReadRealtime("0B13", race));
                    today.AddRange(ReadRealtime("0B17", race));
                }
                var todayRaces = today.Select(r => r.Key?.AsSlug()).Where(s => s != null).Distinct().Count();
                store.Upsert(today);
                Console.WriteLine($"[{_source.SourceName}] データマイニング予想（当日分）: {todayRaces}レースを保存" +
                                  (today.Count > 0 ? $"（作成 {today[0].MadeAt}）" : ""));
            }
        }

        private List<JvRecordParser.MiningRecord> ReadAccumulated(DateTime from)
        {
            var records = new List<JvRecordParser.MiningRecord>();
            var open = _source.Open("MING", from.ToString("yyyyMMdd") + "000000", DataOption.Normal);
            if (open.ReturnCode < 0)
            {
                _source.Close();
                // -1 は「該当データ無し」（前回から新しい分が無い）。それ以外は記録だけして続ける。
                if (open.ReturnCode != -1)
                    Console.WriteLine($"[{_source.SourceName}] MING を開けませんでした rc={open.ReturnCode}（過去分の取り込みのみスキップ）");
                return records;
            }

            try
            {
                ReadAll(records);
            }
            finally
            {
                _source.Close();
            }
            return records;
        }

        private List<JvRecordParser.MiningRecord> ReadRealtime(string dataSpec, RaceKey race)
        {
            var records = new List<JvRecordParser.MiningRecord>();
            int rc = _source.OpenRealtime(dataSpec, race.AsJvRealtimeKey());
            if (rc != 0)
            {
                // -1 = まだ作られていない（前日の夕方より前など）。Close は必ず呼ぶ（呼ばないと以降が -202 になる）。
                _source.Close();
                return records;
            }

            try
            {
                ReadAll(records);
            }
            finally
            {
                _source.Close();
            }
            return records;
        }

        private void ReadAll(List<JvRecordParser.MiningRecord> records)
        {
            while (true)
            {
                int size = _source.Read(out var buffer, out _);
                if (size == 0) break;
                if (size == -1) continue;
                if (size == -3) { Thread.Sleep(500); continue; }
                if (size < 0) break;

                var type = JvRecordParser.GetRecordTypeId(buffer);
                if (type != "DM" && type != "TM") continue;
                try
                {
                    var record = JvRecordParser.ParseMining(buffer);
                    if (record?.Key != null) records.Add(record);
                }
                catch (Exception ex)
                {
                    // 1件の読み違いで全体を止めない。
                    Console.WriteLine($"[{_source.SourceName}] データマイニング予想のレコードを読めませんでした（この1件のみスキップ）: {ex.Message}");
                }
            }
        }
    }
}
