using System;
using System.Threading;
using System.Threading.Tasks;
using KeibaDataCollector.Interop;
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

        public RaceResultService(IRaceDataSource source, WordPressClient wp, TimeSpan pollInterval)
        {
            _source = source;
            _wp = wp;
            _pollInterval = pollInterval;
        }

        public async Task RunWatchLoopAsync(string realtimeDataSpec, string key, CancellationToken ct)
        {
            // realtimeDataSpec: 確定成績/払戻に対応するリアルタイム系データ種別コード。
            // JV-Data仕様書の「リアルタイム系データ種別」一覧で正確な値を確認して差し替える。
            int rc = _source.OpenRealtime(realtimeDataSpec, key);
            if (rc != 0)
                throw new InvalidOperationException($"{_source.SourceName} OpenRealtime failed: {rc}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size > 0)
                    {
                        var recordType = JvRecordParser.GetRecordTypeId(buffer);
                        // TODO: recordType "SE"（着順確定後）/ "HR"（払戻）をパースし、
                        // RaceResultへ集約してから _wp.PublishRaceResultAsync を呼ぶ。
                        Console.WriteLine($"[{_source.SourceName}] realtime record type={recordType} size={size}（パース未実装）");
                    }

                    await Task.Delay(_pollInterval, ct);
                }
            }
            finally
            {
                _source.Close();
            }
        }
    }
}
