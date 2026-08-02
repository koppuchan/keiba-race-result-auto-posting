using System;
using System.Collections.Generic;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector.Services
{
    /// <summary>朝一バッチ: 当日の番組表（出走表）を取得しWordPressへ反映する。</summary>
    public class RaceCardService
    {
        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;

        public RaceCardService(IRaceDataSource source, WordPressClient wp)
        {
            _source = source;
            _wp = wp;
        }

        // option=2(今週データ)は「直近の未来のレースに関するデータ」に取得範囲をサーバー側で
        // 絞ってくれるため、fromtimeは実際の対象範囲決定には使われない（更新差分の再開用途）。
        // 出馬表は開催日より前（火・水曜等）に公開されるため、fromtimeを「今日0時」にすると
        // 「今日0時以降に新規提供されたデータ」しか拾えず、既に公開済みの当日レース分を
        // 取りこぼす。そのため十分に過去の固定値を指定し、option=2の範囲を丸ごと取得する。
        private const string EarlyAnchorFromTime = "19860101000000";

        public void RunMorningBatch(DateTime targetDate, string trackCode)
        {
            // dataspec "RACE" は番組表系レコードを想定。JV-Linkインターフェース仕様書のJVOpen
            // option早見表(option=2:今週データ)で dataspec="RACE" 指定可であることを確認済み。
            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1)
            {
                // JV-Linkインターフェース仕様書のコード表より: -1は「該当データ無し」であり異常ではない
                // （指定期間に開催が無い等）。JVCloseを呼んで正常終了する。
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 該当データなし（開催が無い等）。");
                return;
            }
            if (open.ReturnCode < 0)
                throw new InvalidOperationException($"{_source.SourceName} Open failed: {open.ReturnCode}");

            var entriesByRace = new Dictionary<string, List<RaceCardEntry>>();
            var raceKeys = new Dictionary<string, RaceKey>();

            int totalRecords = 0;
            int seRecords = 0;
            var otherDates = new HashSet<string>();

            while (true)
            {
                int size = _source.Read(out var buffer, out _);
                if (size == 0) break;
                if (size < 0) continue; // ファイル切り替わり等の制御コード
                totalRecords++;

                if (JvRecordParser.GetRecordTypeId(buffer) != "SE") continue;
                seRecords++;

                var (raceKey, entry) = JvRecordParser.ParseRaceCard(buffer);
                // option=2は「今週データ」全体を返しうるため、朝一バッチの対象日以外は捨てる。
                if (raceKey.RaceDate.Date != targetDate.Date)
                {
                    otherDates.Add(raceKey.RaceDate.ToString("yyyy-MM-dd"));
                    continue;
                }

                var slug = raceKey.AsSlug();
                if (!entriesByRace.TryGetValue(slug, out var list))
                {
                    list = new List<RaceCardEntry>();
                    entriesByRace[slug] = list;
                    raceKeys[slug] = raceKey;
                }
                list.Add(entry);
            }
            _source.Close();

            Console.WriteLine(
                $"[{_source.SourceName}] 診断: 全レコード{totalRecords}件, SEレコード{seRecords}件, " +
                $"対象日({targetDate:yyyy-MM-dd})以外の日付={string.Join(",", otherDates)}");

            foreach (var slug in entriesByRace.Keys)
            {
                var entries = entriesByRace[slug];
                entries.Sort((a, b) => a.Umaban.CompareTo(b.Umaban));
                _wp.UpsertRaceCardAsync(raceKeys[slug], entries).GetAwaiter().GetResult();
                Console.WriteLine($"[{_source.SourceName}] {slug} 出走表 {entries.Count}頭 反映完了");
            }

            Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 出走表 {entriesByRace.Count}レース 反映完了");
        }
    }
}
