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
    ///
    /// 【重要】全レース分を一度に取ることはできない。
    /// JV-Data仕様書「（２）速報系データ」より、速報オッズ(0B30/0B31)は
    ///   提供単位     : レース毎（0B12等と違い「開催日単位」は選べない）
    ///   提供タイミング: 対象レースの勝ち馬投票券発売以降に提供
    /// つまり発売前のオッズは存在せず、データ種別を変えても解決しない。
    /// 発売開始が競馬場ごとに違うため（実測 2026-08-11: 10時台に盛岡・笠松は全レース揃う一方、
    /// 浦和・門別・金沢は12:30時点で0件）、このモードは開催時間中ずっと繰り返し実行し、
    /// 発売が始まったレースから順に埋めていく前提で作られている。
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

            int published = 0, already = 0, noOdds = 0, failed = 0;

            foreach (var race in races)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    // 先にWordPressを見て、既に予想があればオッズ取得ごと省く。
                    // このモードは開催時間中ずっと繰り返し走るため、逆順にすると
                    // 反映済みのレースにも毎回COM呼び出しが発生する
                    // （57レース×1日24回＝1368回）。
                    if (await _wp.HasPredictionsAsync(race))
                    {
                        already++;
                        continue;
                    }

                    var marks = BuildMarks(race);
                    if (marks.Count == 0)
                    {
                        // まだ発売が始まっていないレース。異常ではない。
                        // JV-Data仕様書「（２）速報系データ」より、速報オッズ(0B30/0B31)は
                        // 「対象レースの勝ち馬投票券発売以降に提供」される。
                        // 発売前のオッズは、どのデータ種別を使っても存在しない。
                        noOdds++;
                        continue;
                    }

                    if (!await _wp.UpsertPredictionsAsync(race, marks))
                    {
                        // 直前に他の実行が書き込んだ場合。上書きしない。
                        already++;
                        continue;
                    }

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
                $"[{_source.SourceName}] 予想の反映完了: 新規{published}件 / 反映済み{already}件 / " +
                $"オッズ未提供{noOdds}件 / 失敗{failed}件");

            // 「1件も新規が無い」は正常な状態でも起きる（前回までに全レース反映済み）。
            // また、朝の早い時間帯はオッズがまだ配信されておらず0件が正常
            // （実測: 09:31時点で57レース中2件、10:05時点で24件）。
            //
            // 異常と言えるのは「この時刻になっても1件も予想が付いていない」場合だけ。
            // ここを単純に「0件なら失敗」にすると毎朝1本目が必ず失敗し、
            // 本当の異常を知らせる警報として機能しなくなる。
            if (published == 0 && already == 0 && races.Count > 0
                && DateTime.Now.TimeOfDay >= NoPredictionIsAbnormalAfter)
            {
                throw new InvalidOperationException(
                    $"{_source.SourceName} 予想が1件もありません（対象{races.Count}件、うちオッズ未提供{noOdds}件）。" +
                    "オッズ取得を確認してください。");
            }
        }

        /// <summary>この時刻を過ぎても予想が1件も無ければ異常とみなす。</summary>
        private static readonly TimeSpan NoPredictionIsAbnormalAfter = TimeSpan.FromHours(11);

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
