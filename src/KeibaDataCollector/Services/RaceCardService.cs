using System;
using System.Collections.Generic;
using System.Linq;
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
            {
                // 例外を投げる前にもCloseを呼ぶ。呼ばずに抜けると、JV-Linkインターフェース
                // 仕様書の-202（前回のOpenに対してCloseが呼ばれていない）により、次回以降の
                // Open呼び出しがすべて失敗し続けてしまう（実機で確認済みの問題パターン）。
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} Open failed: {open.ReturnCode}");
            }

            var entriesByRace = new Dictionary<string, List<RaceCardEntry>>();
            var raceKeys = new Dictionary<string, RaceKey>();

            int totalRecords = 0;
            int seRecords = 0;
            var otherDates = new HashSet<string>();
            var typeCounts = new Dictionary<string, int>();

            // Open成功後は、読み込みループの途中で例外が発生した場合でも必ずCloseが
            // 呼ばれるようtry/finallyで保護する（上記と同じ-202連鎖を防ぐため）。
            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue; // ファイル切り替わり（正常、JVRead仕様書より）
                    if (size == -3)
                    {
                        // ファイルダウンロード中（JVRead仕様書より）。busyループにならないよう少し待つ。
                        System.Threading.Thread.Sleep(500);
                        continue;
                    }
                    if (size < 0)
                        throw new InvalidOperationException($"{_source.SourceName} Read failed: {size}");
                    totalRecords++;

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);
                    typeCounts[typeId] = typeCounts.TryGetValue(typeId, out var c) ? c + 1 : 1;

                    if (typeId != "SE") continue;
                    seRecords++;

                    RaceKey raceKey;
                    RaceCardEntry entry;
                    try
                    {
                        (raceKey, entry) = JvRecordParser.ParseRaceCard(buffer);
                    }
                    catch (Exception ex)
                    {
                        // 1レコードのパース失敗で情報源全体を止めない。原因調査用に文字列長も残す。
                        Console.WriteLine(
                            $"[{_source.SourceName}] SEレコードのパース失敗（このレコードのみスキップ）: " +
                            $"生文字列長={buffer.Length}, エラー={ex.Message}");
                        continue;
                    }

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
            }
            finally
            {
                _source.Close();
            }

            // 取得内容の内訳。データが届いているか／対象日の絞り込みが効いているかを
            // 運用時に確認できるよう、通常ログとして残す。
            var typeBreakdown = string.Join(", ", typeCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"));
            Console.WriteLine(
                $"[{_source.SourceName}] 取得内訳: 全レコード{totalRecords}件, SEレコード{seRecords}件, " +
                $"対象日({targetDate:yyyy-MM-dd})以外の日付={string.Join(",", otherDates)}, " +
                $"種別内訳=[{typeBreakdown}]");

            // 1レースの反映に失敗しても、残りのレースは必ず試す。
            //
            // 実際に発生した障害（2026-08-25）: 船橋10Rの送信でWordPressが503を返し、
            // そこで例外が上まで抜けて以降のレースが全て中断した。
            // 船橋10〜12Rと笠松の全10レースに出走表が入らず、
            // 予想ページの馬名が空欄のまま公開された。
            // 一時的な不調は WordPressClient 側で再試行するが、それでも駄目だった1件のために
            // その日の残り全部を落としてはいけない。
            var failures = new List<string>();

            foreach (var slug in entriesByRace.Keys)
            {
                var entries = entriesByRace[slug];
                entries.Sort((a, b) => a.Umaban.CompareTo(b.Umaban));
                try
                {
                    _wp.UpsertRaceCardAsync(raceKeys[slug], entries).GetAwaiter().GetResult();
                    Console.WriteLine($"[{_source.SourceName}] {slug} 出走表 {entries.Count}頭 反映完了");
                }
                catch (Exception ex)
                {
                    failures.Add(slug);
                    Console.WriteLine($"[{_source.SourceName}] {slug} 出走表の反映に失敗（このレースのみスキップ）: {ex.Message}");
                }
            }

            var succeeded = entriesByRace.Count - failures.Count;
            Console.WriteLine(
                $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 出走表 {succeeded}/{entriesByRace.Count}レース 反映完了" +
                (failures.Count > 0 ? $"（失敗{failures.Count}件: {string.Join(", ", failures)}）" : ""));

            // 失敗が残ったことは終了コードに出す。黙って成功扱いにすると、
            // 出走表が欠けたまま公開されていることに誰も気付けない。
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{_source.SourceName} 出走表の反映に失敗したレースがあります（{failures.Count}件）。" +
                    "再実行すると未反映ぶんだけ入ります。");
            }
        }
    }
}
