using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 予想印（◎○▲△）を生成してWordPressへ反映する。
    ///
    /// 予想はJV-Link/UmaConnからは提供されない。お客様が別ツールで
    /// 「朝一時点のオッズから人気順に印を付ける」運用をされていたものを、
    /// こちらのデータで直接生成するようにしたもの。
    ///
    /// ルールが「人気順の上位4頭に◎○▲△」であることは、
    /// 実際に公開されていた予想26レース分と当方のオッズデータを突き合わせて確認した
    /// （全レースで必ず4頭、印の付いた馬の87%が確定人気5位以内。
    ///   ずれは朝から発走までのオッズ変動で説明がつく）。
    ///
    /// 別ツールのページを解析する案もあったが、生成側の書式が変わるだけで
    /// 予想が丸ごと消えるため採用していない。元データが同じなら自前で作るほうが安全で、
    /// ばんえい・中央競馬も自動的に対象にできる。
    /// </summary>
    public class PredictionService
    {
        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;

        // 速報オッズ（単複枠）。O1レコードに単勝オッズと単勝人気順が入る。
        private const string RealtimeOddsDataSpec = "0B31";

        /// <summary>人気1位から順に割り当てる印。数を増やせば5番手以降にも付けられる。</summary>
        private static readonly string[] Marks = { "◎", "○", "▲", "△" };

        public PredictionService(IRaceDataSource source, WordPressClient wp)
        {
            _source = source;
            _wp = wp;
        }

        public async Task RunAsync(DateTime targetDate, CancellationToken ct)
        {
            var races = RaceDiscovery.ForDate(_source, targetDate);
            Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 予想対象レース: {races.Count}件");

            int published = 0, noOdds = 0, failed = 0;

            foreach (var race in races)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var marks = BuildMarks(race);
                    if (marks.Count == 0)
                    {
                        // 発売前などでオッズがまだ無い。異常ではない。
                        noOdds++;
                        continue;
                    }

                    await _wp.UpsertPredictionsAsync(race, marks);
                    published++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {race.AsSlug()} 予想を反映（" +
                        string.Join(" ", marks.OrderBy(m => Array.IndexOf(Marks, m.Value))
                                              .Select(m => $"{m.Value}{m.Key}")) + "）");
                }
                catch (Exception ex)
                {
                    // 1レースの失敗で当日分すべてを落とさない。
                    failed++;
                    Console.WriteLine($"[{_source.SourceName}] {race.AsSlug()} 予想の反映に失敗（このレースのみスキップ）: {ex.Message}");
                }
            }

            Console.WriteLine(
                $"[{_source.SourceName}] 予想の反映完了: 成功{published}件 / オッズ未提供{noOdds}件 / 失敗{failed}件");

            if (published == 0 && races.Count > 0)
            {
                // 1件も付けられないのは、オッズ取得が丸ごと失敗している可能性が高い。
                // 黙って正常終了すると、予想が出ないまま誰も気付けない。
                throw new InvalidOperationException(
                    $"{_source.SourceName} 予想を1件も生成できませんでした（対象{races.Count}件）。オッズ取得を確認してください。");
            }
        }

        /// <summary>
        /// 1レース分の印を作る。戻り値は 馬番 => 印。
        /// 人気順が取得できない馬（発売前取消・無投票など）は対象外になる。
        /// </summary>
        private Dictionary<int, string> BuildMarks(RaceKey race)
        {
            var byUmaban = FetchTanshoOdds(race);
            var marks = new Dictionary<int, string>();
            if (byUmaban == null) return marks;

            // 人気順が有効（1以上）の馬だけを対象に、人気の若い順へ印を割り当てる。
            // 同一人気が複数返ることは通常ないが、返ってきた場合は馬番順で安定させる。
            var ordered = byUmaban
                .Where(kv => kv.Value.Ninki > 0)
                .OrderBy(kv => kv.Value.Ninki)
                .ThenBy(kv => kv.Key)
                .Take(Marks.Length)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
                marks[ordered[i].Key] = Marks[i];

            return marks;
        }

        private Dictionary<int, JvRecordParser.TanshoOdds> FetchTanshoOdds(RaceKey race)
        {
            Dictionary<int, JvRecordParser.TanshoOdds> byUmaban = null;

            int rc = _source.OpenRealtime(RealtimeOddsDataSpec, race.AsJvRealtimeKey());
            if (rc != 0)
            {
                // -1（該当データ無し）など。Openに対するCloseを必ず呼ぶ。
                // 呼ばずに抜けると以降のOpenが -202 で失敗し続ける。
                _source.Close();
                return null;
            }

            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3) { Thread.Sleep(500); continue; }
                    if (size < 0) break;

                    if (JvRecordParser.GetRecordTypeId(buffer) != "O1") continue;

                    var (_, parsed) = JvRecordParser.ParseTanshoOdds(buffer);
                    if (parsed.Count > 0) byUmaban = parsed; // 後から届いたものほど新しい
                }
            }
            finally
            {
                _source.Close();
            }

            return byUmaban;
        }
    }
}
