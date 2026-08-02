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
    /// </summary>
    public class RaceResultService
    {
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

        public async Task RunWatchLoopAsync(string realtimeDataSpec, string key, CancellationToken ct)
        {
            // realtimeDataSpec: JV-Linkインターフェース仕様書「JVRTOpen」記載の対応表で確認済み。
            // 払戻確定="0B12"（本サービスが監視する対象）。
            // ★TODO: 仕様書によると"0B12"のkeyは「レース単位」("YYYYMMDDJJKKHHRR"等)が必須で、
            // 日付単位・空文字は想定されていない。現状 key="" で呼んでいるため恒常的に失敗する見込み。
            // 本来はJVWatchEvent（COMイベント）で払戻確定イベントを受け取り、そのイベントが返す
            // レースキーでJVRTOpenする設計だが、後期バインドではCOMイベント購読ができないため、
            // 朝一バッチで取得した当日のレース一覧を使い、レースごとに個別ポーリングする方式へ
            // 改修する必要がある。
            int rc = _source.OpenRealtime(realtimeDataSpec, key);
            if (rc == -1)
            {
                // JV-Linkインターフェース仕様書のコード表より: -1は「該当データ無し」であり異常ではない。
                Console.WriteLine($"[{_source.SourceName}] realtime該当データなし（key='{key}'）。");
                return;
            }
            if (rc != 0)
                throw new InvalidOperationException($"{_source.SourceName} OpenRealtime failed: {rc}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size > 0)
                    {
                        await HandleRecordAsync(buffer);
                    }

                    await Task.Delay(_pollInterval, ct);
                }
            }
            finally
            {
                _source.Close();
            }
        }

        private async Task HandleRecordAsync(string buffer)
        {
            var recordType = JvRecordParser.GetRecordTypeId(buffer);
            switch (recordType)
            {
                case "SE":
                {
                    var (raceKey, entry) = JvRecordParser.ParseRaceResult(buffer);
                    var result = GetOrCreate(raceKey);
                    result.Entries.RemoveAll(e => e.Umaban == entry.Umaban);
                    result.Entries.Add(entry);
                    result.Entries.Sort((a, b) => a.Umaban.CompareTo(b.Umaban));
                    await _wp.PublishRaceResultAsync(result);
                    Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 馬番{entry.Umaban} 着順反映");
                    break;
                }
                case "HR":
                {
                    var (raceKey, payouts) = JvRecordParser.ParsePayouts(buffer);
                    var result = GetOrCreate(raceKey);
                    result.Payouts = payouts;
                    await _wp.PublishRaceResultAsync(result);
                    Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 払戻 {payouts.Count}件 反映");
                    break;
                }
                case "RA":
                {
                    var (raceKey, cornerPassage) = JvRecordParser.ParseCornerPassage(buffer);
                    var result = GetOrCreate(raceKey);
                    result.CornerPassage = cornerPassage;
                    await _wp.PublishRaceResultAsync(result);
                    Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} コーナー通過順位 反映");
                    break;
                }
                default:
                    Console.WriteLine($"[{_source.SourceName}] realtime record type={recordType}（対象外レコード、無視）");
                    break;
            }
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
