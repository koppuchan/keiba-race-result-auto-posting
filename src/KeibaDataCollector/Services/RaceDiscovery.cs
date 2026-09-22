using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 当日のレース一覧（キーのみ）を取得する処理。
    ///
    /// 結果監視（RaceResultService）と予想生成（PredictionService）の両方が
    /// 「その日のレースを列挙する」必要があるため、共通化している。
    /// 片方だけ直して挙動がずれる事故を避けるのが目的。
    /// </summary>
    public static class RaceDiscovery
    {
        // option=2(今週データ)はサーバー側で対象範囲を絞るため、fromtimeは範囲決定に使われない。
        // 出馬表は開催日より前に公開されるので、fromtimeを当日0時にすると公開済みの当日分を
        // 取りこぼす。十分に過去の固定値を渡して option=2 の範囲を丸ごと受け取る。
        public const string EarlyAnchorFromTime = "19860101000000";

        /// <summary>
        /// 指定日のレースキーを列挙する。
        /// "RA"レコード（レース詳細、1レース1件）を使うため "SE"（1頭1件）より効率的。
        /// </summary>
        public static List<RaceKey> ForDate(IRaceDataSource source, DateTime targetDate)
            => Scan(source, targetDate).Active;

        /// <summary>
        /// 当日の対象レースと、中止になったレースの一覧。
        ///
        /// 中止のぶんだけ対象日で絞らないのは、中止が判明するのが開催の翌日以降になるため。
        /// 仕様書 p.38「※開催中止時の運用について」に、中止当日の蓄積系データは
        /// 出馬表のまま（区分2）で追加・訂正の提供が無く、区分9が入るのは
        /// 「蓄積系データの成績登録日」だと明記されている。
        ///
        /// Open は option=2（今週データ）なので前日以前のレースも一緒に流れてくる。
        /// そこから中止を拾えば、翌日のバッチが前日の中止を自動で片付けられる。
        /// </summary>
        public class RaceListing
        {
            public List<RaceKey> Active = new List<RaceKey>();
            public List<RaceKey> Cancelled = new List<RaceKey>();
        }

        public static RaceListing Scan(IRaceDataSource source, DateTime targetDate)
        {
            var open = source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1)
            {
                // 該当データ無し。開催が無い日など、異常ではない。
                source.Close();
                return new RaceListing();
            }
            if (open.ReturnCode < 0)
            {
                // Closeを呼ばずに抜けると次回以降のOpenが -202 で失敗し続ける。
                source.Close();
                throw new InvalidOperationException($"{source.SourceName} Open failed: {open.ReturnCode}");
            }

            // 同じレースのRAレコードは出馬表→速報成績→確定成績と何度も流れてくる。
            // 後から来たものが新しいので、レースキーで上書きしながら最後の状態だけを残す。
            // 中止になったレースは通常のレコードが流れたあとに区分9が来るため、
            // 出現順に全部追加していくと中止を見落とす。
            var latest = new Dictionary<string, RaceKey>();
            var latestKubun = new Dictionary<string, string>();
            try
            {
                while (true)
                {
                    int size = source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;            // ファイル切り替わり（正常）
                    if (size == -3)
                    {
                        Thread.Sleep(500);               // ダウンロード中。busyループにしない。
                        continue;
                    }
                    if (size < 0)
                        throw new InvalidOperationException($"{source.SourceName} Read failed: {size}");

                    if (JvRecordParser.GetRecordTypeId(buffer) != "RA") continue;

                    RaceKey raceKey;
                    try
                    {
                        (raceKey, _) = JvRecordParser.ParseCornerPassage(buffer);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{source.SourceName}] RAレコードのパース失敗（このレコードのみスキップ）: {ex.Message}");
                        continue;
                    }

                    // 対象日のレースに加えて、中止になったレースは日付を問わず拾う。
                    // 中止が蓄積系に反映されるのは開催の翌日以降のため、
                    // 対象日だけを見ていると前日の中止を永久に取りこぼす。
                    var kubun = JvRecordParser.GetDataKubun(buffer);
                    if (raceKey.RaceDate.Date != targetDate.Date
                        && !JvRecordParser.IsRaceCancelled(kubun))
                    {
                        continue;
                    }

                    var slug = raceKey.AsSlug();
                    latest[slug] = raceKey;
                    latestKubun[slug] = kubun;
                }
            }
            finally
            {
                source.Close();
            }

            // 中止になったレースは、予想も結果監視も対象から外す。
            var listing = new RaceListing();
            foreach (var pair in latest)
            {
                if (JvRecordParser.IsRaceCancelled(latestKubun[pair.Key]))
                    listing.Cancelled.Add(pair.Value);
                else
                    listing.Active.Add(pair.Value);
            }

            listing.Active.Sort((a, b) => string.CompareOrdinal(a.AsSlug(), b.AsSlug()));
            listing.Cancelled.Sort((a, b) => string.CompareOrdinal(a.AsSlug(), b.AsSlug()));

            if (listing.Cancelled.Count > 0)
            {
                Console.WriteLine(
                    $"[{source.SourceName}] 中止: {listing.Cancelled.Count}件 " +
                    $"({string.Join(", ", listing.Cancelled.Select(k => k.AsSlug()))})");
            }

            return listing;
        }
    }
}
