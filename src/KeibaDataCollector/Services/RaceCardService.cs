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

        public void RunMorningBatch(DateTime targetDate, string trackCode)
        {
            // dataspec "RACE" は番組表系レコードを想定。JV-Linkインターフェース仕様書のJVOpen
            // option早見表(option=2:今週データ)で dataspec="RACE" 指定可であることを確認済み。
            var fromTime = targetDate.ToString("yyyyMMdd") + "000000";
            var open = _source.Open("RACE", fromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode < 0)
                throw new InvalidOperationException($"{_source.SourceName} Open failed: {open.ReturnCode}");

            var entriesByRace = new Dictionary<string, List<RaceCardEntry>>();
            var raceKeys = new Dictionary<string, RaceKey>();

            while (true)
            {
                int size = _source.Read(out var buffer, out _);
                if (size == 0) break;
                if (size < 0) continue; // ファイル切り替わり等の制御コード

                if (JvRecordParser.GetRecordTypeId(buffer) != "SE") continue;

                var (raceKey, entry) = JvRecordParser.ParseRaceCard(buffer);
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
