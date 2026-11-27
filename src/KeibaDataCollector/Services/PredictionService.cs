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
    ///
    /// 【発売前スコア】上の制約で、発売開始の遅い競馬場は締切直前まで印が出なかった。
    /// そこでオッズを使わず過去データだけで付ける印（Scoring.PreSaleScorer）を用意した。
    /// 設定 PredictionMethod が score ならその印を9時の時点で公開し、計算できなかったレースだけ
    /// 従来どおりオッズの人気順で付ける。shadow なら公開はオッズの人気順のまま、比較をログに出す。
    /// </summary>
    public class PredictionService
    {
        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;

        // 速報オッズ（単複枠）。O1レコードに単勝オッズと単勝人気順が入る。
        private const string RealtimeOddsDataSpec = "0B31";

        /// <summary>人気1位から順に割り当てる印。数を増やせば5番手以降にも付けられる。</summary>
        private static readonly string[] Marks = { "◎", "○", "▲", "△" };

        // このデータ源が受け持つレース（中央 = JV-Link、地方 = UmaConn）。null なら全レース。
        private readonly Func<RaceKey, bool> _ownsRace;

        // データ源の初期化（JVInit/NVInit）。対象レースがあるときだけ呼ぶ。
        // 平日の中央のように受け持ちが無い日は JV-Link を開かないので、
        // JRA-VAN のメンテナンス中にダイアログで止まることも無い（2026-10-06 に発生）。
        private readonly Action _initialize;
        private bool _initialized;

        // 発売前スコアで付けた印（レースキー → 印）。オッズを使わず、過去データだけで朝のうちに計算してある
        // （Scoring.PreSaleScorer）。_publishPreSale が true を返すレースはこちらを公開し、
        // それ以外（null のときは全レース）は比較のログだけ出す。区分（中央・地方・ばんえい）ごとに切り替えるため。
        private readonly IReadOnlyDictionary<string, Scoring.ScoredRace> _preSale;
        private readonly Func<RaceKey, bool> _publishPreSale;

        internal PredictionService(IRaceDataSource source, WordPressClient wp,
            Func<RaceKey, bool> ownsRace = null, Action initialize = null,
            IReadOnlyDictionary<string, Scoring.ScoredRace> preSale = null, Func<RaceKey, bool> publishPreSale = null)
        {
            _source = source;
            _wp = wp;
            _ownsRace = ownsRace;
            _initialize = initialize;
            _preSale = preSale;
            _publishPreSale = publishPreSale;
        }

        /// <summary>発売前スコアの印（印が揃っているときだけ）。無ければ null。</summary>
        private Dictionary<int, string> PreSaleMarksFor(RaceKey race)
        {
            if (_preSale == null || !_preSale.TryGetValue(race.AsSlug(), out var scored) || scored.Skipped != null)
                return null;
            var marks = scored.Marks();
            return marks.Count >= Marks.Length ? marks : null;
        }

        private bool PublishesPreSale(RaceKey race) => _publishPreSale != null && _publishPreSale(race);

        private bool NeedsOdds(RaceKey race) => !PublishesPreSale(race) || PreSaleMarksFor(race) == null;

        private bool Owns(RaceKey key) => _ownsRace == null || _ownsRace(key);

        private void EnsureInitialized()
        {
            if (_initialized) return;
            _initialize?.Invoke();
            _initialized = true;
        }

        /// <summary>
        /// 予想を付けるレースの一覧。サイトの投稿（朝一バッチが全レースを投稿している）から、
        /// まだ予想が揃っておらず、結果も出ていないレースを選ぶ。
        ///
        /// 以前は毎回、提供元のデータを全件読み込んで一覧を作っており、1回に3〜4分かかっていた。
        /// 15分ごとに実行していたため、オッズの配信から予想の反映まで最大20分近く遅れ、
        /// お客様から「締切に間に合わないレースがある」とご指摘をいただいた（2026-10-13）。
        /// 提供元への負荷も大きかった（アクセスが過剰なIPは取得を制限されることがある）。
        ///
        /// サイトに聞けない・サイトにその日のレースが無い（朝一バッチの失敗など）ときは null を返し、
        /// 呼び出し側は従来どおり提供元から一覧を取る。
        /// </summary>
        private async Task<(List<RaceKey> Races, int AlreadyPredicted)?> RacesFromSiteAsync(DateTime targetDate)
        {
            List<WordPressClient.DayRace> day;
            try
            {
                day = await _wp.GetDayRacesAsync(targetDate);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_source.SourceName}] サイトのレース一覧を取得できませんでした（提供元から取得します）: {ex.Message}");
                return null;
            }
            if (day.Count == 0) return null;

            var own = day.Where(r => Owns(r.Key)).ToList();
            var alreadyPredicted = own.Count(r => r.PredictionMarks >= Marks.Length);
            var races = own
                .Where(r => !r.Cancelled && !r.HasResult && r.PredictionMarks < Marks.Length)
                .Select(r => r.Key)
                .ToList();
            return (races, alreadyPredicted);
        }

        public async Task RunAsync(DateTime targetDate, CancellationToken ct)
        {
            var started = DateTime.Now;

            List<RaceKey> races;
            bool fromSite;
            int already = 0;
            var site = await RacesFromSiteAsync(targetDate);
            if (site.HasValue)
            {
                races = site.Value.Races;
                already = site.Value.AlreadyPredicted;
                fromSite = true;
            }
            else
            {
                EnsureInitialized();
                races = RaceDiscovery.ForDate(_source, targetDate);
                fromSite = false;
            }
            PostTimeStore.Apply(targetDate, races);

            Console.WriteLine(
                $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 予想対象レース: {races.Count}件" +
                (fromSite ? $"（サイトの一覧から。予想済み{already}件）" : "（提供元の一覧から）"));

            int published = 0, notOnSale = 0, oddsError = 0, failed = 0, afterPost = 0, byScore = 0;
            // オッズを取りに行く発走前のレースが1件でもあるときだけデータ源を開く
            // （夜など、残りが発走済みだけのときや、発売前スコアで全レースに印が付くときは開かない）。
            if (races.Any(r => NeedsOdds(r) && (!r.PostDateTime.HasValue || DateTime.Now < r.PostDateTime.Value)))
                EnsureInitialized();

            foreach (var race in races)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    // 先にWordPressを見て、済んでいるレースはオッズ取得ごと省く。
                    // このモードは開催時間中ずっと繰り返し走るため、逆順にすると
                    // 反映済みのレースにも毎回COM呼び出しが発生する
                    // （57レース×1日24回＝1368回）。
                    // オッズ取得は1レースあたり数十秒かかることがあり、
                    // 1回の実行が長引くほど、オッズ公開から反映までの遅れが大きくなる。
                    // サイトの一覧は「予想が揃っていない・結果が出ていない」ものだけなので、確認は不要。
                    if (!fromSite && await _wp.ShouldSkipPredictionAsync(race, Marks.Length))
                    {
                        already++;
                        continue;
                    }

                    // 発走時刻を過ぎたレースには予想を出さない。
                    // 結果の反映が遅れた日に、発走後のオッズ（確定オッズ）で印を付けた
                    // 「後出し」の予想が載るおそれがあった（2026-10-09）。
                    if (race.PostDateTime.HasValue && DateTime.Now >= race.PostDateTime.Value)
                    {
                        afterPost++;
                        continue;
                    }

                    var preSale = PreSaleMarksFor(race);
                    var fromScore = PublishesPreSale(race) && preSale != null;
                    var marks = fromScore ? preSale : BuildMarks(race);
                    if (marks.Count == 0)
                    {
                        // JV-Linkインターフェース仕様書 p.54 のコード表より、
                        //   -1 = 該当データ無し（＝まだ発売前。正常）
                        //   それ以外の負値 = 実際のエラー（-202 オープン中 / -301 認証 など）
                        // 両方を「オッズ未提供」で片付けると、COM競合や認証切れが
                        // 発売前と同じ見え方になり切り分けられない。必ず分けて記録する。
                        if (_lastOpenReturnCode == 0 || _lastOpenReturnCode == -1)
                        {
                            notOnSale++;
                        }
                        else
                        {
                            oddsError++;
                            Console.WriteLine(
                                $"[{_source.SourceName}] {race.AsSlug()} オッズ取得エラー rc={_lastOpenReturnCode}" +
                                $"（{DescribeReturnCode(_lastOpenReturnCode)}）");
                        }
                        continue;
                    }

                    if (!await _wp.UpsertPredictionsAsync(race, marks))
                    {
                        // 直前に他の実行が書き込んだ場合。上書きしない。
                        already++;
                        continue;
                    }

                    published++;
                    if (fromScore) byScore++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {race.AsSlug()} 予想を反映（{FormatMarks(marks)}" +
                        (fromScore ? "、発売前スコア" : "") + "）" +
                        (!fromScore && preSale != null ? $" 発売前スコアなら {FormatMarks(preSale)}（{Compare(preSale, marks)}）" : ""));
                }
                catch (Exception ex)
                {
                    // 1レースの失敗で当日分すべてを落とさない。
                    failed++;
                    Console.WriteLine($"[{_source.SourceName}] {race.AsSlug()} 予想の反映に失敗（このレースのみスキップ）: {ex.Message}");
                }
            }

            // 発走前のレースで、印の付いた馬が取り消されていたら付け直す。
            var remarked = await ReplaceScratchedMarksAsync(targetDate, ct);

            // 所要時間も出す。繰り返し間隔より長くなると、次の回が多重起動禁止で弾かれ、
            // オッズ公開から反映までの遅れがそのまま伸びる。調整の判断材料として必ず残す。
            Console.WriteLine(
                $"[{_source.SourceName}] 予想の反映完了: 新規{published}件" +
                (remarked > 0 ? $"（取消で付け直し{remarked}件）" : "") +
                (byScore > 0 ? $"（うち発売前スコア{byScore}件）" : "") + $" / 反映済み{already}件 / " +
                $"発売前{notOnSale}件 / 発走済み{afterPost}件 / オッズ取得エラー{oddsError}件 / 反映失敗{failed}件 " +
                $"（所要 {(DateTime.Now - started).TotalMinutes:0.0}分）");

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
                    $"{_source.SourceName} 予想が1件もありません" +
                    $"（対象{races.Count}件、発売前{notOnSale}件、オッズ取得エラー{oddsError}件）。");
            }
        }

        private static string FormatMarks(Dictionary<int, string> marks) =>
            string.Join(" ", marks.OrderBy(m => Array.IndexOf(Marks, m.Value)).Select(m => $"{m.Value}{m.Key}"));

        /// <summary>並行稼働中の比較用。発売前スコアの印が、オッズの人気順の印とどれだけ合っているか。</summary>
        private static string Compare(Dictionary<int, string> preSale, Dictionary<int, string> odds)
        {
            var honmei = preSale.FirstOrDefault(m => m.Value == Marks[0]).Key == odds.FirstOrDefault(m => m.Value == Marks[0]).Key;
            var overlap = preSale.Keys.Intersect(odds.Keys).Count();
            return $"◎{(honmei ? "一致" : "不一致")}・印の重なり{overlap}/{Marks.Length}";
        }

        /// <summary>
        /// JVRTOpen/NVRTOpen の戻り値の意味。
        /// 出典: JV-Linkインターフェース仕様書 p.54「ＪＶＯｐｅｎ／ＪＶＲＴＯｐｅｎ」コード表。
        /// ログを読む人が仕様書を引かずに原因へ辿り着けるようにするため、文言も出す。
        /// </summary>
        private static string DescribeReturnCode(int rc)
        {
            switch (rc)
            {
                case -1:   return "該当データ無し（発売前）";
                case -111: return "dataspecパラメータが不正";
                case -114: return "keyパラメータが不正";
                case -201: return "JVInitが行われていない";
                case -202: return "前回のOpenに対してCloseが呼ばれていない（オープン中）";
                case -211: return "レジストリ内容が不正";
                case -301: return "認証エラー（利用キーを確認）";
                case -302: return "利用キーの有効期限切れ";
                case -303: return "利用キーが設定されていない";
                case -504: return "サーバーメンテナンス中";
                default:   return "仕様書p.54のコード表を参照";
            }
        }

        /// <summary>
        /// 発走までこの時間を切ったレースは、印の付いた馬が取り消されていないかを確かめる。
        /// 予想の処理は5分ごとに動くので、1レースにつき発走前に数回確かめることになる。
        /// </summary>
        private static readonly TimeSpan ScratchCheckWindow = TimeSpan.FromMinutes(30);

        /// <summary>
        /// 印の付いた馬が取り消された（出走取消・除外）発走前のレースに、印を付け直す。
        ///
        /// 発売前スコアの印は朝の出走表（07:00）をもとに9時に付けるため、その後に取り消された馬に
        /// 印が残る（2026-11-27: 園田8Rの◎、名古屋1Rの○。オッズの人気順の印は、発売後に取り消された馬に残る）。
        /// 取消は単勝オッズ欄の "----" / "****" で分かるので、発走前にそれを見て、
        /// 発売前スコアのレースはスコアの次点の馬、オッズのレースは今の人気順で付け直す。
        /// 発走後に付け直すと結果を見てからの後出しになるため、発走前のレースだけを対象にする。
        /// </summary>
        private async Task<int> ReplaceScratchedMarksAsync(DateTime targetDate, CancellationToken ct)
        {
            List<WordPressClient.DayRace> day;
            try
            {
                day = await _wp.GetDayRacesAsync(targetDate);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_source.SourceName}] 取消の確認でサイトの一覧を取得できませんでした（次の回に確認）: {ex.Message}");
                return 0;
            }

            var marked = day.Where(r => Owns(r.Key) && !r.Cancelled && !r.HasResult && r.Predictions.Count > 0).ToList();
            PostTimeStore.Apply(targetDate, marked.Select(r => r.Key));
            var now = DateTime.Now;
            var due = marked.Where(r => r.Key.PostDateTime.HasValue && now < r.Key.PostDateTime.Value
                                        && r.Key.PostDateTime.Value - now <= ScratchCheckWindow).ToList();
            if (due.Count == 0) return 0;
            EnsureInitialized();

            var replaced = 0;
            foreach (var race in due)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var odds = FetchOddsAndScratches(race.Key);
                    if (odds == null) continue; // オッズがまだ無い・取れない。次の回に確かめる。
                    var scratchedMarks = race.Predictions.Keys.Where(odds.Value.Scratched.Contains).ToList();
                    if (scratchedMarks.Count == 0) continue;

                    var marks = RebuildMarks(race.Key, odds.Value.ByUmaban, odds.Value.Scratched);
                    if (marks.Count < Marks.Length)
                    {
                        Console.WriteLine($"[{_source.SourceName}] {race.Key.AsSlug()} 取消（{string.Join(",", scratchedMarks)}番）があったが、" +
                                          "付け直す印が揃わないため元のまま");
                        continue;
                    }

                    await _wp.UpsertPredictionsAsync(race.Key, marks, replace: true);
                    replaced++;
                    Console.WriteLine($"[{_source.SourceName}] {race.Key.AsSlug()} 取消（{string.Join(",", scratchedMarks)}番）のため印を付け直し" +
                                      $"（{FormatMarks(race.Predictions)} → {FormatMarks(marks)}）");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{_source.SourceName}] {race.Key.AsSlug()} 取消の確認に失敗（次の回に確認）: {ex.Message}");
                }
            }
            return replaced;
        }

        /// <summary>取消の馬を除いて印を付け直す。発売前スコアのレースはスコア順、それ以外は今のオッズの人気順。</summary>
        private Dictionary<int, string> RebuildMarks(RaceKey race, Dictionary<int, JvRecordParser.TanshoOdds> byUmaban,
            HashSet<int> scratched)
        {
            var marks = new Dictionary<int, string>();
            if (PublishesPreSale(race) && _preSale != null && _preSale.TryGetValue(race.AsSlug(), out var scored)
                && scored.Skipped == null)
            {
                var ranked = scored.Runners.Where(r => !scratched.Contains(r.Umaban))
                    .OrderByDescending(r => r.Score).ThenBy(r => r.Umaban).Select(r => r.Umaban).ToList();
                for (int i = 0; i < Marks.Length && i < ranked.Count; i++) marks[ranked[i]] = Marks[i];
                return marks;
            }
            return MarksFromOdds(byUmaban);
        }

        /// <summary>単勝オッズ（取消の馬は含まない）と、取消の馬番。オッズが取れなければ null。</summary>
        private (Dictionary<int, JvRecordParser.TanshoOdds> ByUmaban, HashSet<int> Scratched)? FetchOddsAndScratches(RaceKey race)
        {
            int rc = _source.OpenRealtime(RealtimeOddsDataSpec, race.AsJvRealtimeKey());
            if (rc != 0)
            {
                _source.Close();
                return null;
            }

            Dictionary<int, JvRecordParser.TanshoOdds> byUmaban = null;
            HashSet<int> scratched = null;
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

                    // 後から届いたものほど新しい。
                    byUmaban = JvRecordParser.ParseTanshoOdds(buffer).ByUmaban;
                    scratched = JvRecordParser.ParseScratchedUmaban(buffer);
                }
            }
            finally
            {
                _source.Close();
            }

            if (byUmaban == null) return null;
            return (byUmaban, scratched);
        }

        /// <summary>この時刻を過ぎても予想が1件も無ければ異常とみなす。</summary>
        private static readonly TimeSpan NoPredictionIsAbnormalAfter = TimeSpan.FromHours(11);

        /// <summary>
        /// 1レース分の印を作る。戻り値は 馬番 => 印。
        /// 人気順が取得できない馬（発売前取消・無投票など）は対象外になる。
        /// </summary>
        private Dictionary<int, string> BuildMarks(RaceKey race) => MarksFromOdds(FetchTanshoOdds(race));

        /// <summary>単勝人気順の上位から印を付ける。人気順が印の数だけ揃っていなければ空。</summary>
        private static Dictionary<int, string> MarksFromOdds(Dictionary<int, JvRecordParser.TanshoOdds> byUmaban)
        {
            var marks = new Dictionary<int, string>();
            if (byUmaban == null) return marks;

            // 人気順が有効（1以上）の馬だけを対象に、人気の若い順へ印を割り当てる。
            // 同一人気が複数返ることは通常ないが、返ってきた場合は馬番順で安定させる。
            var ranked = byUmaban
                .Where(kv => kv.Value.Ninki > 0)
                .OrderBy(kv => kv.Value.Ninki)
                .ThenBy(kv => kv.Key)
                .ToList();

            // 印の数だけ人気順が揃うまで待つ。
            // オッズ配信が始まった直後は一部の馬しか値が入っておらず、
            // そこで確定させると印の欠けた予想が残ってしまう
            // （実測 2026-08-12: 大井2Rが5頭立てにもかかわらず◎1頭だけで固定された）。
            // 揃っていなければ空を返し、次の回で取り直す。
            if (ranked.Count < Marks.Length) return marks;

            for (int i = 0; i < Marks.Length; i++)
                marks[ranked[i].Key] = Marks[i];

            return marks;
        }

        /// <summary>
        /// オッズを取得できなかった理由。「まだ発売前」と「本当のエラー」を区別する。
        ///
        /// 以前は戻り値が0以外なら理由を問わず「オッズ未提供」として黙って進めていた。
        /// そのため、COMの競合(-202)や認証エラー(-301)が起きていても
        /// 「まだ発売前」と同じ見え方になり、切り分けができなかった。
        /// </summary>
        private int _lastOpenReturnCode;

        private Dictionary<int, JvRecordParser.TanshoOdds> FetchTanshoOdds(RaceKey race)
        {
            Dictionary<int, JvRecordParser.TanshoOdds> byUmaban = null;

            int rc = _source.OpenRealtime(RealtimeOddsDataSpec, race.AsJvRealtimeKey());
            _lastOpenReturnCode = rc;
            if (rc != 0)
            {
                // Openに対するCloseを必ず呼ぶ。呼ばずに抜けると以降のOpenが -202 で失敗し続ける。
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
