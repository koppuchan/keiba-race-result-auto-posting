using System;
using System.Collections.Generic;
using KeibaDataCollector.Interop;
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
            // dataspec "RACE" は番組表系レコードを想定。正確な値はJV-Data仕様書のデータ種別一覧で要確認。
            var fromTime = targetDate.ToString("yyyyMMdd") + "000000";
            var open = _source.Open("RACE", fromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode < 0)
                throw new InvalidOperationException($"{_source.SourceName} Open failed: {open.ReturnCode}");

            var rawRecords = new List<string>();
            while (true)
            {
                int size = _source.Read(out var buffer, out _);
                if (size == 0) break;
                if (size < 0) continue; // ファイル切り替わり等の制御コード

                if (JvRecordParser.GetRecordTypeId(buffer) == "SE")
                    rawRecords.Add(buffer);
            }
            _source.Close();

            // TODO: rawRecords を JvRecordParser.ParseRaceCardEntry でDTO化し、
            // レースキーごとにグルーピングしてから UpsertRaceCardAsync を呼ぶ。
            // 現状はJVData_Struct.cs未組み込みのためパース未実装（JvRecordParser参照）。
            Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 出走表レコード {rawRecords.Count} 件取得（パース未実装）");
        }
    }
}
