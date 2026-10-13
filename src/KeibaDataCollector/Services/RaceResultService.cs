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
    /// 確定後リアルタイム: レース確定検知〜結果・払戻の取得〜WordPress即時反映をポーリングで回す。
    ///
    /// JVRTOpenの"0B12"（払戻確定）は、公式にはJVWatchEvent（COMイベント）が返すレース単位の
    /// キー（"YYYYMMDDJJRR"）で呼ぶ設計だが、後期バインドではCOMイベントを購読できないため、
    /// 朝一相当のクエリで当日のレース一覧を先に取得し、レースごとに個別ポーリングする方式にしている。
    /// </summary>
    public class RaceResultService
    {
        // 速報オッズ（単複枠）。JV-Data仕様書「（２）速報系データ」より、O1レコード
        // （単勝オッズ・単勝人気順を含む）が返る種別。
        private const string RealtimeOddsDataSpec = "0B31";

        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;
        private readonly TimeSpan _pollInterval;

        // レースキーごとに結果を積み上げるバッファ。SE/HR/RAレコードが個別に届くたび、
        // このレースの最新状態でWordPressへ再送する（Eventually Consistentな即時反映）。
        private readonly Dictionary<string, RaceResult> _buffers = new Dictionary<string, RaceResult>();

        // 前回の「監視中...」以降に 0B12 を照会した結果の内訳。
        // 未確定のレースはログに何も出さないため、これが無いと
        // 「まだ結果が無い」のか「照会がずっとデータ無しを返している」のか区別できない
        // （2026-10-05: プロセスは動いていたが、地方39件が14時以降1件も確定しなかった）。
        private int _realtimeNoData;
        private int _realtimeEmpty;
        private int _realtimeWithData;

        // 中止を反映済みのレース。一覧は一定間隔で取り直すため、
        // これが無いと同じ中止レースに毎回WordPressへ書きに行ってしまう。
        private readonly HashSet<string> _cancelledNotified = new HashSet<string>();

        // このデータ源が受け持つレース（中央 = JV-Link、地方 = UmaConn）。null なら全レース。
        private readonly Func<RaceKey, bool> _ownsRace;

        // レースごとの、最後に結果を問い合わせた時刻。問い合わせの間隔を空けるために使う。
        private readonly Dictionary<string, DateTime> _lastChecked = new Dictionary<string, DateTime>();

        // サイトの投稿から分かった、当日のレースの有無。null = まだ分からない（サイトに聞けていない）。
        private bool? _siteHasAnyRaces;
        private bool? _siteHasOwnRaces;

        public RaceResultService(IRaceDataSource source, WordPressClient wp, TimeSpan pollInterval,
            Func<RaceKey, bool> ownsRace = null)
        {
            _source = source;
            _wp = wp;
            _pollInterval = pollInterval;
            _ownsRace = ownsRace;
        }

        private bool Owns(RaceKey key) => _ownsRace == null || _ownsRace(key);

        // ポーリングが生きているのに未確定レコードは何もログを出さないため、外から見ると
        // 「待機中」と「詰まっている」の区別がつかない。一定周期ごとに状況を出力する。
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

        // 監視するレースの一覧は、サイトの投稿（朝一バッチが全レースを投稿している）から取る。
        // サイトへの問い合わせは軽いので、10分ごとに取り直す。
        private static readonly TimeSpan SiteListInterval = TimeSpan.FromMinutes(10);

        // 提供元のデータを全件読み込んでのレース一覧の取得は、起動時の1回だけにする。
        // サイトのレース一覧が使えないとき（サイトに聞けない・その日の投稿が無い）に限り、
        // 従来どおり取り直すが、その間隔も1時間にする。
        //
        // 以前は10分ごとに全件を読み込んでいた。地方（UmaConn）は1回に5〜15分かかり、
        // 2024年からの全履歴を毎回読み直す、提供元への大きな負荷だった。提供元はアクセスが
        // 過剰なIPからの取得を制限することがあり、2026-10-05 以降に続いた「昼ごろから結果が
        // 『データ無し』になる」症状の有力な原因と考えている。読み込み中は結果の確認も止まる。
        // 起動時の1回は、サイトに載っていないレースや前日以前の中止を拾うための保険として残す。
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromHours(1);

        // 1レースの結果を問い合わせる間隔。発走時刻を過ぎてから問い合わせを始める。
        //
        // 以前は未確定の全レースを20秒ごとに問い合わせており、1日あたり約13万回になっていた
        // （発走前で結果があるはずのないレースにも聞いていた）。結果は発走の数分後にしか
        // 出ないので、1分ごとでも反映の遅れは最大1分程度にとどまる。
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

        // 発走から1時間たっても結果が揃わないレースは、提供元の配信が止まっていることが多い。
        // 問い合わせを5分ごとに落とす（取りこぼしは catchup も20分ごとに拾う）。
        private static readonly TimeSpan LateAfter = TimeSpan.FromHours(1);
        private static readonly TimeSpan LateCheckInterval = TimeSpan.FromMinutes(5);

        // その日の監視を打ち切る時刻。地方競馬のナイター（概ね21時台まで）を
        // 見終えてから終了し、翌日のトリガーを妨げないようにする。
        // 中断からの再開判定にも使うため公開する。
        public static readonly TimeSpan DailyCutoff = TimeSpan.FromHours(23.5);

        public class CatchUpSummary
        {
            public int Confirmed;
            public int Partial;
            public int NoData;
            public int Failed;
        }

        /// <summary>
        /// 過去の日付で、結果か払戻が欠けたまま残ったレースを1回ずつ取り直す（catchup モード）。
        ///
        /// watch は当日のレースしか見ないため、提供元の配信が遅れたり監視が止まったりして
        /// 日付が変わると、取りこぼしを拾う手段が無かった
        /// （2026-10-05: 地方39件、10-06: 15件、10-07: 2件が欠けたまま残った）。
        /// 0B12 の提供期間は1週間なので、その間なら取り直せる。
        /// </summary>
        public async Task<CatchUpSummary> CatchUpAsync(IReadOnlyList<RaceKey> races)
        {
            var summary = new CatchUpSummary();
            var notStarted = 0;

            foreach (var raceKey in races)
            {
                // 発走前のレースには結果があるはずがないので、問い合わせない。
                if (raceKey.PostDateTime.HasValue && DateTime.Now < raceKey.PostDateTime.Value)
                {
                    notStarted++;
                    continue;
                }

                var withDataBefore = _realtimeWithData;
                try
                {
                    if (await CheckAndPublishRaceAsync(raceKey))
                    {
                        summary.Confirmed++;
                        Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 確定（着順・払戻とも反映）");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    summary.Failed++;
                    Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 取得に失敗: {ex.Message}");
                    continue;
                }

                if (_realtimeWithData > withDataBefore)
                {
                    // 届いた分は反映済み。全着順か払戻のどちらかがまだ提供されていない。
                    summary.Partial++;
                    Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 一部のみ（全着順か払戻が未提供）");
                }
                else
                {
                    summary.NoData++;
                    Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 提供元にデータ無し");
                }
            }

            Console.WriteLine(
                $"[{_source.SourceName}] 取り直し完了: 対象{races.Count}件 / 確定{summary.Confirmed}件 / " +
                $"一部のみ{summary.Partial}件 / データ無し{summary.NoData}件 / 失敗{summary.Failed}件 / " +
                $"発走前{notStarted}件");
            return summary;
        }

        public async Task RunWatchLoopAsync(DateTime targetDate, CancellationToken ct)
        {
            // 確定済みのレースキー。取り直しのたびに再登録されるのを防ぐ。
            var completed = new HashSet<string>();
            var pending = new List<RaceKey>();

            var lastHeartbeat = DateTime.Now;
            var lastSiteList = DateTime.MinValue;
            var lastDiscovery = DateTime.MinValue;
            var cutoff = targetDate.Date.Add(DailyCutoff);

            while (!ct.IsCancellationRequested && DateTime.Now < cutoff)
            {
                // このループが回らなくなったら、プロセスごと起動し直させる（ShutdownWatchdog）。
                ShutdownWatchdog.ReportProgress(_source.SourceName);

                // 監視するレースは、まずサイトの投稿から取る（軽い）。
                if (DateTime.Now - lastSiteList >= SiteListInterval)
                {
                    await MergeSiteRacesAsync(targetDate, pending, completed);
                    lastSiteList = DateTime.Now;
                }

                // 提供元の全件読み込みは、起動時の1回だけ（DiscoveryInterval 参照）。
                // サイトに当日のレースがあり、その中にこのデータ源の受け持ちが1件も無い日
                // （平日の中央など）は、起動時も読まない。JRA-VAN のメンテナンス中に JV-Link を開いて
                // ダイアログで止まることも避けられる（2026-10-06 に発生）。
                // サイトに聞けなかった・サイトにその日のレースが無い（朝一バッチの失敗など）ときは、
                // 従来どおり提供元から一覧を取り、1時間ごとに取り直す。
                var noOwnRacesToday = _siteHasAnyRaces == true && _siteHasOwnRaces == false;
                var siteListUsable = _siteHasAnyRaces == true;
                var firstDiscovery = lastDiscovery == DateTime.MinValue;
                if (!noOwnRacesToday
                    && (firstDiscovery || !siteListUsable)
                    && DateTime.Now - lastDiscovery >= DiscoveryInterval)
                {
                    MergeDiscoveredRaces(targetDate, pending, completed);
                    // 間隔は取得が「終わってから」数える。取得に時間がかかっても、
                    // 次の取得までの間に各レースの確認ができるようにするため。
                    lastDiscovery = DateTime.Now;
                }

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (ct.IsCancellationRequested) break;

                    var raceKey = pending[i];
                    if (!IsDue(raceKey)) continue;
                    _lastChecked[raceKey.AsSlug()] = DateTime.Now;

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
                    // 監視対象が多い日は1周に時間がかかるため、1レースごとに記録する。
                    ShutdownWatchdog.ReportProgress(_source.SourceName);

                    if (confirmed)
                    {
                        Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 払戻確定・反映完了。監視対象から除外。");
                        completed.Add(raceKey.AsSlug());
                        pending.RemoveAt(i);
                    }
                }

                // ここで「未確定が0になったら終了」としてはいけない。
                // レース一覧(RAレコード)は当日ぶんが一度に揃うとは限らず、開催の進行に
                // 合わせて順次配信される。実機で確認: 起動時に園田1Rしか一覧に無く、
                // それを反映した時点で未確定0・確定1となり監視を終了してしまい、
                // 以降のレースが1件も反映されないまま1日が終わった。
                // 未確定が0でも打ち切り時刻まで待機し、取り直しで現れたレースを拾う。

                if (DateTime.Now - lastHeartbeat >= HeartbeatInterval)
                {
                    // メモリ使用量も出す。JVGets/NVGetsへ渡すバッファの扱いを誤ると
                    // 1レコードごとに解放されない領域が積み上がり、数時間後に
                    // ヒープ破損でプロセスごと落ちる（実機で発生）。
                    // 値が単調に増え続けていないかを、この行だけで追えるようにしておく。
                    var workingSetMb = Environment.WorkingSet / 1024d / 1024d;
                    var started = pending.Count(k => !k.PostDateTime.HasValue || DateTime.Now >= k.PostDateTime.Value);
                    Console.WriteLine(
                        $"[{_source.SourceName}] 監視中... 未確定{pending.Count}件（うち発走済み{started}件） / 確定済み{completed.Count}件 " +
                        $"（{DateTime.Now:HH:mm:ss}時点、メモリ{workingSetMb:0}MB）。" +
                        $" 0B12照会: データ無し{_realtimeNoData} / 空{_realtimeEmpty} / あり{_realtimeWithData}");
                    _realtimeNoData = _realtimeEmpty = _realtimeWithData = 0;
                    lastHeartbeat = DateTime.Now;
                }

                await Task.Delay(_pollInterval, ct);
            }

            // 1日の締めとして結果を1行にまとめる。ログ末尾を見るだけで、
            // その日きちんと反映できたのか／取りこぼしたのかが判断できるようにする。
            if (completed.Count == 0 && pending.Count > 0)
            {
                Console.WriteLine(
                    $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 監視終了: " +
                    $"[要確認] 監視対象{pending.Count}件に対し確定は0件でした。");
            }
            else
            {
                Console.WriteLine(
                    $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 監視終了: " +
                    $"確定{completed.Count}件 / 未確定のまま{pending.Count}件。");
            }
        }

        /// <summary>
        /// このレースの結果を今問い合わせるべきか。
        /// 発走前は問い合わせない。発走後は1分ごと、発走から1時間を過ぎたら5分ごと。
        /// 発走時刻が分からないレースは1分ごと（取りこぼすより、多めに聞くほうに倒す）。
        /// </summary>
        private bool IsDue(RaceKey raceKey)
        {
            var now = DateTime.Now;
            var post = raceKey.PostDateTime;
            if (post.HasValue && now < post.Value) return false;

            var interval = post.HasValue && now - post.Value > LateAfter ? LateCheckInterval : CheckInterval;
            return !_lastChecked.TryGetValue(raceKey.AsSlug(), out var last) || now - last >= interval;
        }

        /// <summary>
        /// サイトに公開済みの当日のレースのうち、このデータ源の受け持ちで結果か払戻が欠けているものを
        /// 監視対象に足す。あわせて、監視中のレースに発走時刻を当てはめる。
        /// </summary>
        private async Task MergeSiteRacesAsync(DateTime targetDate, List<RaceKey> pending, HashSet<string> completed)
        {
            List<WordPressClient.DayRace> day;
            try
            {
                day = await _wp.GetDayRacesAsync(targetDate);
            }
            catch (Exception ex)
            {
                // サイトに聞けなくても、提供元からの一覧で監視は続けられる。
                Console.WriteLine($"[{_source.SourceName}] サイトのレース一覧を取得できませんでした（監視は継続）: {ex.Message}");
                _siteHasAnyRaces = _siteHasOwnRaces = null;
                return;
            }

            var own = day.Where(r => Owns(r.Key)).ToList();
            _siteHasAnyRaces = day.Count > 0;
            _siteHasOwnRaces = own.Count > 0;

            var missing = own.Where(r => !r.Cancelled && !(r.HasResult && r.HasPayouts)).Select(r => r.Key).ToList();
            PostTimeStore.Apply(targetDate, missing);
            // 起動後に朝一バッチが発走時刻を保存した場合にも効くよう、監視中のレースにも当てはめる。
            PostTimeStore.Apply(targetDate, pending);

            var known = new HashSet<string>(pending.Select(k => k.AsSlug()));
            var added = 0;
            foreach (var raceKey in missing)
            {
                var slug = raceKey.AsSlug();
                if (completed.Contains(slug) || known.Contains(slug)) continue;
                pending.Add(raceKey);
                known.Add(slug);
                added++;
            }

            if (added > 0)
            {
                Console.WriteLine(
                    $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} サイトのレース一覧から{added}件を監視対象に追加" +
                    $"（受け持ち{own.Count}件） 未確定{pending.Count}件 / 確定済み{completed.Count}件");
            }
        }

        /// <summary>当日のレース一覧を取り直し、未確定かつ未登録のものを監視対象に足す。
        /// 取得に失敗しても、既に監視中のレースは止めない。</summary>
        private void MergeDiscoveredRaces(DateTime targetDate, List<RaceKey> pending, HashSet<string> completed)
        {
            var started = DateTime.Now;
            RaceDiscovery.RaceListing listing;
            try
            {
                listing = RaceDiscovery.Scan(_source, targetDate);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_source.SourceName}] レース一覧の取り直しに失敗（監視は継続）: {ex.Message}");
                return;
            }

            var discovered = listing.Active;

            // RAレコードには発走時刻がある。朝一バッチが保存できなかった場合に備えて、ここでも保存する。
            PostTimeStore.Merge(targetDate, discovered);
            PostTimeStore.Apply(targetDate, discovered);

            // 中止が蓄積系に載るのは開催の翌日以降なので、ここで拾えるのは主に前日以前のぶん。
            // 当日ぶんは CheckAndPublishRaceAsync が速報系の区分9で先に片付けている。
            // 公開済みの投稿が無ければ MarkRaceCancelledAsync は何もしない。
            foreach (var raceKey in listing.Cancelled)
            {
                var slug = raceKey.AsSlug();
                if (!_cancelledNotified.Add(slug)) continue;

                try
                {
                    if (_wp.MarkRaceCancelledAsync(raceKey).GetAwaiter().GetResult())
                        Console.WriteLine($"[{_source.SourceName}] {slug} 中止のため非表示にしました");
                }
                catch (Exception ex)
                {
                    // 次回の取り直しでやり直せるよう、記録を取り消しておく。
                    _cancelledNotified.Remove(slug);
                    Console.WriteLine($"[{_source.SourceName}] {slug} 中止の反映に失敗（次回リトライ）: {ex.Message}");
                }

                // 中止のレースが監視対象に残っていたら外す。
                pending.RemoveAll(k => k.AsSlug() == slug);

                // 開催ごと中止の日は件数が多く、WordPressの応答が遅いと時間がかかるため。
                ShutdownWatchdog.ReportProgress(_source.SourceName);
            }

            var known = new HashSet<string>(pending.Select(k => k.AsSlug()));
            var added = 0;

            foreach (var raceKey in discovered)
            {
                var slug = raceKey.AsSlug();
                if (completed.Contains(slug) || known.Contains(slug)) continue;

                pending.Add(raceKey);
                known.Add(slug);
                added++;
            }

            // レース一覧は開催の進行に合わせて増えていくため、毎回の取得総数を残す。
            // 「一覧に何件見えているのか」が分からないと、反映漏れの切り分けができない。
            Console.WriteLine(
                $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} レース一覧を取得: {discovered.Count}件" +
                (added > 0 ? $"（うち{added}件を監視対象に追加）" : "（追加なし）") +
                $" 未確定{pending.Count}件 / 確定済み{completed.Count}件" +
                // 所要時間も出す。取得の間は各レースの確認が止まるため、
                // ここが長いと結果の反映がその分だけ遅れる（2026-10-05 は1回18分）。
                $"（所要 {(DateTime.Now - started).TotalMinutes:0.0}分）");
        }

        /// <summary>
        /// 0B12（速報レース情報・成績確定後）は、1レースにつき段階的に4回配信される
        /// （JV-Data仕様書 データ提供タイミングより）:
        ///   払戻･3着まで確定 → 払戻･5着まで確定 → 払戻･全馬着順確定 → 払戻･全馬着順+コーナ通過順
        /// このときSE(馬毎レース情報)のデータ区分は 3→4→5→6 と進み、仕様書の特記事項に
        /// 「区分が3(3着まで確定)・4(5着まで確定)の場合、該当馬のみの情報を返す
        /// （順位が確定していない馬の情報は返さない）」と明記されている。
        ///
        /// 実機で確認: 払戻(HR)を受け取った時点で監視終了としていたため、最初の
        /// 「3着まで確定」で打ち切ってしまい、12頭立てのレースが着順3件・コーナー0件で
        /// 確定扱いになっていた。全馬着順+コーナ通過順（区分6）以降まで待つ必要がある。
        /// </summary>
        private const string DataKubunAllFinishersWithCorner = "6";
        private const string DataKubunFinalResult = "7";

        private static bool IsCompleteResult(string dataKubun) =>
            dataKubun == DataKubunAllFinishersWithCorner || dataKubun == DataKubunFinalResult;

        /// <summary>指定レースの確定状況をJVRTOpen("0B12", レースキー)で確認し、
        /// データがあれば取得・WordPress反映する（段階的に届くため、その時点の最新内容で都度反映）。
        /// 全馬着順+コーナ通過順まで確定したらtrueを返す（＝このレースはもう監視不要）。</summary>
        private async Task<bool> CheckAndPublishRaceAsync(RaceKey raceKey)
        {
            int rc = _source.OpenRealtime("0B12", raceKey.AsJvRealtimeKey());
            if (rc == -1)
            {
                _realtimeNoData++;
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
            bool gotAnyRecord = false;
            bool isComplete = false;
            bool isCancelled = false;
            var seenDataKubun = new SortedSet<string>();

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

                    var dataKubun = JvRecordParser.GetDataKubun(buffer);

                    // 中止のレコードは、中身が初期値（着順0・タイム0:00.0）なので
                    // 取り込んではいけない。読み捨てて、あとで中止として処理する。
                    if (JvRecordParser.IsRaceCancelled(dataKubun))
                    {
                        isCancelled = true;
                        seenDataKubun.Add(dataKubun);
                        continue;
                    }

                    switch (JvRecordParser.GetRecordTypeId(buffer))
                    {
                        case "SE":
                        {
                            var (_, entry) = JvRecordParser.ParseRaceResult(buffer);
                            // 段階配信のたびに同じ馬が再送されるため、馬番で上書きマージする。
                            result.Entries.RemoveAll(e => e.Umaban == entry.Umaban);
                            result.Entries.Add(entry);
                            gotAnyRecord = true;
                            seenDataKubun.Add(dataKubun);
                            if (IsCompleteResult(dataKubun)) isComplete = true;
                            break;
                        }
                        case "HR":
                        {
                            var (_, payouts) = JvRecordParser.ParsePayouts(buffer);
                            result.Payouts = payouts;
                            gotAnyRecord = true;
                            break;
                        }
                        case "RA":
                        {
                            var (_, cornerPassage) = JvRecordParser.ParseCornerPassage(buffer);
                            // コーナー通過順位は区分6以降でのみ設定される。空で上書きしない。
                            if (cornerPassage.Count > 0)
                                result.CornerPassage = cornerPassage;
                            gotAnyRecord = true;
                            seenDataKubun.Add(dataKubun);
                            if (IsCompleteResult(dataKubun)) isComplete = true;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _source.Close();
            }

            if (gotAnyRecord || isCancelled) _realtimeWithData++;
            else _realtimeEmpty++;

            // 中止が分かった時点で、そのレースは二度と結果が来ない（仕様書 p.38「４．払戻 提供なし」）。
            // 公開済みの出走表・結果を中止として伏せ、監視も終える。
            // ここでtrueを返さないと、来ない払戻を打ち切り時刻まで待ち続けることになる。
            if (isCancelled)
            {
                if (await _wp.MarkRaceCancelledAsync(raceKey))
                    Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 中止（データ区分9）のため非表示にしました");
                return true;
            }

            // rc=0でもレコードが1件も無い場合は反映不要（無駄なWordPress更新を避ける）。
            if (!gotAnyRecord) return false;

            // 地方競馬（UmaConn）はSEレコードの単勝オッズ・単勝人気順を初期値のまま返すため、
            // 速報オッズ("0B31")から補完する（実機で0B31には実値が入っていることを確認済み）。
            // 中央競馬はSEに実値が入るので、その場合はここを通らない＝挙動を変えない。
            if (result.Entries.Any(e => e.TanshoOdds <= 0 && e.Ninki <= 0))
                MergeTanshoOddsFromRealtime(raceKey, result);

            result.Entries.Sort((a, b) => a.Umaban.CompareTo(b.Umaban));

            // 内容が前回から変わっていなければWordPressへは送られない。その場合ログも出さない
            // （速報段階で止まっているレースが毎回同じ行を出力し続けるのを防ぐ）。
            var published = await _wp.PublishRaceResultAsync(result);
            // 払戻が取れていないうちは監視を終えない。
            //
            // データ区分6/7（全馬着順確定）はRA/SEレコードだけで判定されるため、
            // 払戻(HR)がまだ届いていない状態でも「確定」になりうる。
            // そこで監視対象から外すと、あとからHRが届いても二度と取りに行かず、
            // 着順はあるのに払戻が空のまま残る。
            // 実際に発生した（2026-08-24: 結果29レースに対し払戻5レース。
            // 盛岡は1〜4Rに払戻があり5R以降が空という形で表面化した）。
            var confirmed = isComplete && result.Payouts.Count > 0;

            if (published)
            {
                var state = confirmed ? " 確定"
                          : isComplete ? " 着順確定・払戻待ち"
                          : " 速報・続報待ち";
                Console.WriteLine(
                    $"[{_source.SourceName}] {raceKey.AsSlug()} 反映（着順{result.Entries.Count}件, " +
                    $"払戻{result.Payouts.Count}件, コーナー{result.CornerPassage.Count}件, " +
                    $"データ区分[{string.Join(",", seenDataKubun)}]{state}）");
            }

            return confirmed;
        }

        /// <summary>速報オッズ("0B31" 単複枠)から単勝オッズ・単勝人気順を取得し、
        /// 値が入っていない出走馬に補完する。取得できなくても結果本体の反映は続行する。</summary>
        private void MergeTanshoOddsFromRealtime(RaceKey raceKey, RaceResult result)
        {
            Dictionary<int, JvRecordParser.TanshoOdds> byUmaban = null;

            try
            {
                int rc = _source.OpenRealtime(RealtimeOddsDataSpec, raceKey.AsJvRealtimeKey());
                if (rc != 0)
                {
                    _source.Close();
                    return; // -1（該当データ無し）等。オッズ無しのまま反映する。
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
                        if (parsed.Count > 0) byUmaban = parsed;
                    }
                }
                finally
                {
                    _source.Close();
                }
            }
            catch (Exception ex)
            {
                // オッズは補助情報。取得に失敗しても着順・払戻の反映は止めない。
                Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} オッズ取得失敗（オッズ無しで反映）: {ex.Message}");
                return;
            }

            if (byUmaban == null) return;

            foreach (var entry in result.Entries)
            {
                if (!byUmaban.TryGetValue(entry.Umaban, out var odds)) continue;
                // SE側に実値があればそちらを尊重し、無い項目だけ埋める。
                if (entry.TanshoOdds <= 0) entry.TanshoOdds = odds.Odds;
                if (entry.Ninki <= 0) entry.Ninki = odds.Ninki;
            }
        }

        /// <summary>当日のレース一覧（キーのみ）を取得する。
        /// 予想生成でも同じ列挙が必要なため、実装は RaceDiscovery に共通化している。</summary>
        private List<RaceKey> DiscoverTodaysRaceKeys(DateTime targetDate)
            => RaceDiscovery.ForDate(_source, targetDate);

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
