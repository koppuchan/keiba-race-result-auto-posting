using System;
using System.Threading;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Services;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector
{
    internal static class Program
    {
        // 異常があったかどうか。タスクスケジューラの「前回の実行結果」に反映させる。
        // これが常に0だと、1日分まるごと反映されていなくても「成功」に見えてしまい、
        // お客様からの指摘で初めて気づくことになる（実際に発生した）。
        private static bool _hadFailure;

        // COM(ActiveX)相手はSTAスレッドが前提のため必須。
        [STAThread]
        private static int Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "help";

            // 作業中に固まってもプロセスが残らないようにする。
            // setupは利用キー入力のダイアログを人が操作する用途なので対象外。
            // probeは調査用の手動実行なので、勝手に切らない。
            var deadline = DeadlineFor(mode);
            if (deadline.HasValue) ShutdownWatchdog.ArmDeadline(deadline.Value, mode);

            // watch は1日中動くため、上の上限だけでは途中で止まったことに翌日まで気付けない。
            // 進まなくなったら終了させ、タスクスケジューラの繰り返しで起動し直させる。
            if (mode == "watch") ShutdownWatchdog.ArmStallDetector(WatchStallLimit, mode);

            try
            {
                Run(mode, args);
            }
            catch (Exception ex)
            {
                // ここまで漏れてくるのは設定不備など、処理を始める前の失敗。
                // 未処理例外のまま落とすと、サーバーではWindowsのエラー報告ダイアログが
                // 出てタスクが終了しなくなる恐れがあるため、必ず捕まえて終了コードで返す。
                LogFailure("起動", "処理を開始できませんでした", ex);
            }

            if (_hadFailure)
            {
                Console.WriteLine("異常終了: 上記のエラーを確認してください。");
                return 1;
            }
            return 0;
        }

        /// <summary>
        /// モードごとの実行時間の上限。これを超えたら固まったものとして打ち切る。
        ///
        /// morning/predict は実測で数分から十数分
        /// （UmaConnの朝一は約130万件を読むため5分前後かかる）。
        /// 1時間あれば遅い日でも足りるので、その3倍を上限にする。
        ///
        /// watch は RaceResultService.DailyCutoff（当日23:30）まで動くのが正常。
        /// 起動が0:00でも23.5時間なので、余裕を見て25時間とする。
        /// </summary>
        private static TimeSpan? DeadlineFor(string mode)
        {
            switch (mode)
            {
                case "morning":
                case "predict":
                    return TimeSpan.FromHours(3);
                case "watch":
                    return TimeSpan.FromHours(25);
                case "catchup":
                    // 1レースあたり数秒。数日分でも数分で終わるので、1時間あれば十分。
                    return TimeSpan.FromHours(1);
                default:
                    // setup（ダイアログ待ち）・probe（手動調査）・help は打ち切らない。
                    return null;
            }
        }

        private static void Run(string mode, string[] args)
        {
            // probe は第2引数でレースキーを受け取る（例: probe 20260811-46-1R）。
            var arg = args.Length > 1 ? args[1] : null;

            // WordPressClient はここでは作らない: setup モードはWordPressに一切繋がないため、
            // WordPressUser/WordPressAppPassword 未設定でも setup だけは実行できるようにする。
            using (var jvLink = new JvSpecComDataSource(AppConfig.JvLinkProgId, "JV", "JV-Link(中央競馬)"))
            using (var umaConn = new JvSpecComDataSource(AppConfig.UmaConnProgId, "NV", "UmaConn(地方競馬)"))
            try
            {
                switch (mode)
                {
                    case "morning":
                    {
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        // 片方のソース（例: UmaConn未設置）が失敗しても、もう片方は必ず動くように
                        // ソースごとに独立してtry/catchする。
                        RunMorningFor(jvLink, wp);
                        RunMorningFor(umaConn, wp);
                        break;
                    }

                    case "watch":
                    {
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        using (var cts = new CancellationTokenSource())
                        {
                            Console.CancelKeyPress += (_, e) =>
                            {
                                e.Cancel = true;

                                // タスクスケジューラからの実行（出力をログへ流している）では無視する。
                                //
                                // 実際に発生（2026-10-03）: ログが14:18に "^C" で途切れ、そこで監視が止まった。
                                // タスクは「ログオン時のみ実行」のため、VPSの画面に監視の黒いウィンドウが
                                // 出ており、そこでのCtrl+C（文字のコピーのつもり等）で止まりうる。
                                // 中央は9R以降、高知・ばんえいは全レースの結果が出ないまま終わった。
                                // 手元で動かす run-watch.bat は出力を流していないので、従来どおり止められる。
                                if (Console.IsOutputRedirected)
                                {
                                    Console.WriteLine(
                                        "[watch] Ctrl+C を受け取りましたが、タスクからの実行のため無視して監視を続けます。" +
                                        "止める場合は deploy.ps1 を使ってください。");
                                    return;
                                }
                                cts.Cancel();
                            };

                            // ソースごとに独立してtry/catchし、片方の失敗がもう片方の監視を止めない
                            // ようにする。
                            var jvResultTask = RunWatchFor(jvLink, wp, cts.Token);
                            var umaResultTask = RunWatchFor(umaConn, wp, cts.Token);

                            try
                            {
                                System.Threading.Tasks.Task.WaitAll(jvResultTask, umaResultTask);
                            }
                            catch (AggregateException ex)
                            {
                                // Ctrl+C 時の Task.Delay 由来のキャンセルは正常系。
                                // それ以外は握りつぶさず記録する。
                                foreach (var inner in ex.Flatten().InnerExceptions)
                                {
                                    if (inner is OperationCanceledException) continue;
                                    LogFailure("watch", "監視タスクが異常終了しました", inner);
                                }
                            }
                        }
                        break;
                    }

                    case "predict":
                    {
                        // 朝一オッズの人気順から予想印（◎○▲△）を生成して反映する。
                        // 結果監視とは独立して動くため、片方が失敗しても他方に影響しない。
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        RunPredictFor(jvLink, wp);
                        RunPredictFor(umaConn, wp);
                        break;
                    }

                    case "catchup":
                    {
                        // 過去の日付で、結果か払戻が欠けたまま残ったレースを取り直す。
                        // watch は当日しか見ないため、日付が変わると取りこぼしを拾う手段が無かった。
                        var dates = ParseCatchUpDates(args);
                        if (dates == null) break;

                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        foreach (var date in dates)
                            RunCatchUpForDate(jvLink, umaConn, wp, date);
                        break;
                    }

                    case "probe":
                        // 調査用。どのデータ種別で何が取得できるかを実際に叩いて確認する
                        // （地方競馬でオッズ・人気が別種別で提供されていないかの確認用）。
                        // WordPressには一切書き込まない。
                        RunProbeFor(jvLink, arg);
                        RunProbeFor(umaConn, arg);
                        break;

                    case "setup":
                        // 初回1回だけ手動実行: 利用キー入力ダイアログを開いて設定を保存する。
                        // 片方のProgIDが未確認/未登録でもう片方の結果が分からなくなるのを避けるため、
                        // 個別にtry/catchして両方の結果を必ず表示する。
                        RunSetupFor(jvLink);
                        RunSetupFor(umaConn);
                        break;

                    default:
                        Console.WriteLine("使い方: KeibaDataCollector.exe [setup|morning|predict|watch|catchup|probe]");
                        Console.WriteLine("  setup   : 初回のみ。利用キー等をGUIダイアログで設定する。");
                        Console.WriteLine("  morning : 朝一バッチ。当日の出走表を取得しWordPressへ反映する。");
                        Console.WriteLine("  predict : 朝一オッズの人気順から予想印を生成しWordPressへ反映する。");
                        Console.WriteLine("  watch   : レース確定を監視し、結果・払戻を随時WordPressへ反映する。");
                        Console.WriteLine("  catchup : 指定日の、結果か払戻が欠けているレースを取り直して反映する。");
                        Console.WriteLine("            例: catchup 2026-10-05 2026-10-06 / catchup today / catchup recent（当日から7日分）");
                        Console.WriteLine("  probe   : 調査用。どのデータ種別で何が取得できるか確認する（WordPressへは書き込まない）。");
                        Console.WriteLine("            レースを指定する場合: probe 20260811-46-1R");
                        break;
                }
            }
            finally
            {
                // ここまで来れば作業は終わっている。この先はCOMの後片付けだけで、
                // そこが固まってもプロセスは終了させてよい（終了しないほうが害が大きい）。
                ShutdownWatchdog.Arm(_hadFailure ? 1 : 0);
            }
        }

        /// <summary>例外の内容をログに残す。原因調査には型と発生箇所が要るため、
        /// Messageだけでなく例外の全文（スタックトレース含む）を出す。</summary>
        private static void LogFailure(string sourceName, string what, Exception ex)
        {
            _hadFailure = true;
            Console.WriteLine($"[{sourceName}] {what}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.ToString());
        }

        private static void RunProbeFor(JvSpecComDataSource source, string raceKeySlug = null)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new DataSpecProbeService(source).Run(DateTime.Today, raceKeySlug);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{source.SourceName}] 調査失敗（このソースのみスキップ）: {ex.Message}");
            }
        }

        private static void RunSetupFor(JvSpecComDataSource source)
        {
            Console.WriteLine($"[{source.SourceName}] セットアップダイアログを開きます...");
            try
            {
                source.RunInteractiveSetup();
                Console.WriteLine($"[{source.SourceName}] セットアップ完了。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{source.SourceName}] セットアップ失敗: {ex.Message}");
            }
        }

        private static void RunMorningFor(JvSpecComDataSource source, WordPress.WordPressClient wp)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new RaceCardService(source, wp).RunMorningBatch(DateTime.Today, trackCode: "");
            }
            catch (Exception ex)
            {
                // 片方のソースが失敗しても、もう片方は動かす。ただし失敗は終了コードに残す。
                LogFailure(source.SourceName, "朝一バッチ失敗（このソースのみスキップして続行）", ex);
            }
        }

        private static void RunPredictFor(JvSpecComDataSource source, WordPress.WordPressClient wp)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new PredictionService(source, wp)
                    .RunAsync(DateTime.Today, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // 片方のソースが失敗しても、もう片方は動かす。ただし失敗は終了コードに残す。
                // 予想が出ないことに気付けないと、お客様からの指摘で初めて分かることになる。
                LogFailure(source.SourceName, "予想の生成に失敗（このソースのみスキップして続行）", ex);
            }
        }

        /// <summary>catchup の引数（yyyy-MM-dd を1つ以上）を読む。不正なら使い方を出して null。</summary>
        private static System.Collections.Generic.List<DateTime> ParseCatchUpDates(string[] args)
        {
            var dates = new System.Collections.Generic.List<DateTime>();
            for (int i = 1; i < args.Length; i++)
            {
                // タスクスケジューラからは日付を渡しにくいので、当日は "today" で指定できる。
                if (string.Equals(args[i], "today", StringComparison.OrdinalIgnoreCase))
                {
                    dates.Add(DateTime.Today);
                    continue;
                }
                // "recent" = 当日から遡って7日分（0B12 の提供期間）。定期実行で使う。
                // 提供元の配信が日付をまたいで遅れても、手作業なしで拾えるようにするため
                // （2026-10-05 の欠けは、当日は提供されず、10-08 になって取得できた）。
                // 欠けが無い日はサイトへの照会1回で終わるので、毎回7日分見ても負荷は小さい。
                if (string.Equals(args[i], "recent", StringComparison.OrdinalIgnoreCase))
                {
                    for (int back = 0; back < 7; back++)
                        dates.Add(DateTime.Today.AddDays(-back));
                    continue;
                }
                if (!DateTime.TryParseExact(args[i], "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date))
                {
                    _hadFailure = true;
                    Console.WriteLine($"日付の形式が不正です: {args[i]}（例: catchup 2026-10-05）");
                    return null;
                }
                dates.Add(date);
            }

            if (dates.Count == 0)
            {
                _hadFailure = true;
                Console.WriteLine("日付を指定してください（例: catchup 2026-10-05 2026-10-06）");
                return null;
            }
            return dates;
        }

        /// <summary>
        /// 指定日の、結果か払戻が欠けているレースを取り直す。
        ///
        /// 対象はサイトに公開済みの投稿から選ぶ。欠けているものだけを取りに行くので、
        /// 揃っているレースを書き換えることはない。
        ///
        /// データ源はレースごとに分ける。地方だけの日に JV-Link を開かないためで、
        /// JRA-VAN のメンテナンス中にダイアログで止まり、地方の取り直しまで
        /// 巻き込まれることがない（2026-10-06 に watch がこれで止まった）。
        /// </summary>
        private static void RunCatchUpForDate(
            JvSpecComDataSource jvLink, JvSpecComDataSource umaConn, WordPress.WordPressClient wp, DateTime date)
        {
            System.Collections.Generic.List<Models.RaceKey> races;
            try
            {
                races = wp.FindRacesMissingResultsAsync(date).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogFailure("catchup", $"{date:yyyy-MM-dd} の対象レースを取得できませんでした", ex);
                return;
            }

            Console.WriteLine($"{date:yyyy-MM-dd}: 結果か払戻が欠けているレース {races.Count}件");
            if (races.Count == 0) return;

            var jra = races.FindAll(IsJraTrack);
            var nar = races.FindAll(k => !IsJraTrack(k));

            if (jra.Count > 0) RunCatchUpFor(jvLink, wp, jra);
            if (nar.Count > 0) RunCatchUpFor(umaConn, wp, nar);
        }

        /// <summary>中央競馬の競馬場コード（01 札幌 〜 10 小倉）。それ以外は地方競馬（UmaConn）。</summary>
        private static bool IsJraTrack(Models.RaceKey key) =>
            int.TryParse(key.TrackCode, out var code) && code >= 1 && code <= 10;

        private static void RunCatchUpFor(
            JvSpecComDataSource source, WordPress.WordPressClient wp, System.Collections.Generic.List<Models.RaceKey> races)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                var summary = new RaceResultService(source, wp, AppConfig.RealtimePollInterval)
                    .CatchUpAsync(races)
                    .GetAwaiter().GetResult();

                // 提供元にデータが無いのはこちらの異常ではないが、取得に失敗したものは残す。
                if (summary.Failed > 0) _hadFailure = true;
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "取り直しに失敗（このソースのみスキップして続行）", ex);
            }
        }

        // 監視が例外で落ちたときの再開待ち時間。
        private static readonly TimeSpan WatchRetryDelay = TimeSpan.FromMinutes(3);

        // watch がこれだけ進まなければ、止まったものとして終了させる。
        //
        // 固まっていなくても進捗が途切れる最長は、1レースの反映中にWordPressが
        // 一時的な不調（503等）を返し続ける場合。照会1回（HttpClientの既定タイムアウト100秒）と
        // 送信5回（各100秒）＋再試行の待ち（計52秒）で約11分になる。
        // レース一覧の取得は数分かかるが、読めている間は進捗を記録している。
        // 止まっていない実行を切ると、起動し直すたびに同じことが起きて
        // かえって反映が遅れるため、その倍近い余裕を取る。
        private static readonly TimeSpan WatchStallLimit = TimeSpan.FromMinutes(20);

        /// <summary>
        /// 1つのデータ源の監視を、その日の打ち切り時刻まで動かし続ける。
        ///
        /// 以前は例外を1回捕まえたら、そのソースの監視をその日ずっと諦めていた。
        /// COMや通信の一時的な失敗でも「その日は一切反映されない」ことになり、
        /// しかも終了コードは正常のままだったため気づけなかった。
        /// 落ちても間隔をあけて再開し、最後まで粘る。
        /// </summary>
        private static async System.Threading.Tasks.Task RunWatchFor(
            JvSpecComDataSource source, WordPress.WordPressClient wp, CancellationToken ct)
        {
            try
            {
                await RunWatchUntilCutoff(source, wp, ct);
            }
            finally
            {
                // 監視を終えたあとに「止まった」と判定されないようにする。
                ShutdownWatchdog.StopTracking(source.SourceName);
            }
        }

        private static async System.Threading.Tasks.Task RunWatchUntilCutoff(
            JvSpecComDataSource source, WordPress.WordPressClient wp, CancellationToken ct)
        {
            var attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                attempt++;
                // 失敗して再開を待っている間も「止まっている」わけではない。
                // ここで記録しないと、片方のデータ源が認証エラー等で失敗し続けているだけで
                // プロセスごと終了させてしまい、正常なもう片方の監視まで巻き込む。
                ShutdownWatchdog.ReportProgress(source.SourceName);
                try
                {
                    source.Initialize(AppConfig.JvLinkSoftwareId);
                    await new RaceResultService(source, wp, AppConfig.RealtimePollInterval)
                        .RunWatchLoopAsync(DateTime.Today, ct);

                    // 打ち切り時刻まで動ききった＝その日の監視は完了。
                    return;
                }
                catch (OperationCanceledException)
                {
                    return; // Ctrl+C / 停止要求。異常ではない。
                }
                catch (Exception ex)
                {
                    LogFailure(source.SourceName, $"監視が中断しました（{attempt}回目）", ex);
                }

                // 打ち切り時刻を過ぎていれば再開しない（翌日のタスクを妨げないため）。
                if (DateTime.Now >= DateTime.Today.Add(RaceResultService.DailyCutoff))
                {
                    Console.WriteLine($"[{source.SourceName}] 本日の監視時間を過ぎたため再開しません。");
                    return;
                }

                Console.WriteLine(
                    $"[{source.SourceName}] {WatchRetryDelay.TotalMinutes:0}分後に監視を再開します。");
                try
                {
                    await System.Threading.Tasks.Task.Delay(WatchRetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
