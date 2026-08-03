using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 確定後リアルタイム: レース確定検知〜結果・払戻の取得〜WordPress即時反映をポーリングで回す。
    ///
    /// JVRTOpenの"0B12"（払戻確定）は、公式にはJVWatchEvent（COMイベント）が返すレース単位の
    /// キー（"YYYYMMDDJJRR"）で呼ぶ設計だが、後期バインドではCOMイベントを購読できないため、
    /// 朝一相当のクエリで当日のレース一覧を先に取得し、レースごとに個別ポーリングする方式にしている。
    /// </summary>
    public class RaceResultService
    {
        // option=2(今週データ)はサーバー側で対象範囲を絞ってくれるため、fromtimeは十分に古い
        // 固定値にする（RaceCardServiceと同じ理由）。
        private const string EarlyAnchorFromTime = "19860101000000";

        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;
        private readonly TimeSpan _pollInterval;

        // レースキーごとに結果を積み上げるバッファ。SE/HR/RAレコードが個別に届くたび、
        // このレースの最新状態でWordPressへ再送する（Eventually Consistentな即時反映）。
        private readonly Dictionary<string, RaceResult> _buffers = new Dictionary<string, RaceResult>();

        public RaceResultService(IRaceDataSource source, WordPressClient wp, TimeSpan pollInterval)
        {
            _source = source;
            _wp = wp;
            _pollInterval = pollInterval;
        }

        public async Task RunWatchLoopAsync(DateTime targetDate, CancellationToken ct)
        {
            var pending = DiscoverTodaysRaceKeys(targetDate);
            Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 監視対象レース {pending.Count}件を検出。");

            while (!ct.IsCancellationRequested && pending.Count > 0)
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (ct.IsCancellationRequested) break;

                    var raceKey = pending[i];
                    bool confirmed;
                    try
                    {
                        confirmed = await CheckAndPublishRaceAsync(raceKey);
                    }
                    catch (Exception ex)
                    {
                        // このレースの監視で失敗しても、他のレースの監視は続ける。
                        Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 監視中にエラー（次回リトライ）: {ex.Message}");
                        confirmed = false;
                    }

                    if (confirmed)
                    {
                        Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 払戻確定・反映完了。監視対象から除外。");
                        pending.RemoveAt(i);
                    }
                }

                if (pending.Count > 0)
                    await Task.Delay(_pollInterval, ct);
            }

            Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 監視終了（全レース確定、またはキャンセル）。");
        }

        /// <summary>指定レースの払戻確定状況をJVRTOpen("0B12", レースキー)で確認し、
        /// データがあれば取得・WordPress反映する。払戻まで取得できたらtrueを返す
        /// （＝このレースはもう監視不要）。</summary>
        private async Task<bool> CheckAndPublishRaceAsync(RaceKey raceKey)
        {
            int rc = _source.OpenRealtime("0B12", raceKey.AsJvRealtimeKey());
            if (rc == -1)
            {
                // JV-Linkインターフェース仕様書のコード表より: -1は「該当データ無し」＝まだ未確定。
                // ただし同仕様書に「-1の場合もJVCloseを呼び出して取り込み処理を終了してください」と
                // 明記されている。実機で確認: ここでCloseを呼ばずにreturnすると、次回以降の
                // OpenRealtime呼び出しが全て「-202 前回のOpenに対してCloseが呼ばれていない
                // （オープン中）」で失敗し続ける。
                _source.Close();
                return false;
            }
            if (rc != 0)
            {
                // 例外を投げる前にもCloseを呼び、次回以降の呼び出しが-202で
                // 連鎖的に失敗しないようにする。
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} OpenRealtime failed: {rc}");
            }

            var result = GetOrCreate(raceKey);
            bool gotPayout = false;

            // Open成功後は、読み込みループの途中で例外が発生した場合でも必ずCloseが
            // 呼ばれるようtry/finallyで保護する（-202連鎖を防ぐため）。
            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue; // ファイル切り替わり（正常）
                    if (size == -3)
                    {
                        await Task.Delay(500);
                        continue;
                    }
                    if (size < 0)
                        throw new InvalidOperationException($"{_source.SourceName} Read failed: {size}");

                    switch (JvRecordParser.GetRecordTypeId(buffer))
                    {
                        case "SE":
                        {
                            var (_, entry) = JvRecordParser.ParseRaceResult(buffer);
                            result.Entries.RemoveAll(e => e.Umaban == entry.Umaban);
                            result.Entries.Add(entry);
                            break;
                        }
                        case "HR":
                        {
                            var (_, payouts) = JvRecordParser.ParsePayouts(buffer);
                            result.Payouts = payouts;
                            gotPayout = true;
                            break;
                        }
                        case "RA":
                        {
                            var (_, cornerPassage) = JvRecordParser.ParseCornerPassage(buffer);
                            result.CornerPassage = cornerPassage;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _source.Close();
            }

            result.Entries.Sort((a, b) => a.Umaban.CompareTo(b.Umaban));
            await _wp.PublishRaceResultAsync(result);
            Console.WriteLine(
                $"[{_source.SourceName}] {raceKey.AsSlug()} 反映（着順{result.Entries.Count}件, " +
                $"払戻{result.Payouts.Count}件, コーナー{result.CornerPassage.Count}件）");

            return gotPayout;
        }

        /// <summary>朝一バッチと同じ方法で当日のレース一覧（キーのみ）を取得する。
        /// "RA"レコード（レース詳細、1レース1件）を使うため"SE"より効率的。</summary>
        private List<RaceKey> DiscoverTodaysRaceKeys(DateTime targetDate)
        {
            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1)
            {
                _source.Close();
                return new List<RaceKey>();
            }
            if (open.ReturnCode < 0)
            {
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} Open failed: {open.ReturnCode}");
            }

            var keys = new List<RaceKey>();
            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3)
                    {
                        Thread.Sleep(500);
                        continue;
                    }
                    if (size < 0)
                        throw new InvalidOperationException($"{_source.SourceName} Read failed: {size}");

                    if (JvRecordParser.GetRecordTypeId(buffer) != "RA") continue;

                    RaceKey raceKey;
                    try
                    {
                        (raceKey, _) = JvRecordParser.ParseCornerPassage(buffer);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{_source.SourceName}] RAレコードのパース失敗（このレコードのみスキップ）: {ex.Message}");
                        continue;
                    }

                    if (raceKey.RaceDate.Date == targetDate.Date)
                        keys.Add(raceKey);
                }
            }
            finally
            {
                _source.Close();
            }

            return keys;
        }

        private RaceResult GetOrCreate(RaceKey key)
        {
            var slug = key.AsSlug();
            if (!_buffers.TryGetValue(slug, out var result))
            {
                result = new RaceResult { Key = key };
                _buffers[slug] = result;
            }
            return result;
        }
    }
}
