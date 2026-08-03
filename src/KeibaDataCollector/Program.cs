using System;
using System.Threading;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Services;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector
{
    internal static class Program
    {
        // COM(ActiveX)相手はSTAスレッドが前提のため必須。
        [STAThread]
        private static void Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "help";

            // WordPressClient はここでは作らない: setup モードはWordPressに一切繋がないため、
            // WordPressUser/WordPressAppPassword 未設定でも setup だけは実行できるようにする。
            using (var jvLink = new JvSpecComDataSource(AppConfig.JvLinkProgId, "JV", "JV-Link(中央競馬)"))
            using (var umaConn = new JvSpecComDataSource(AppConfig.UmaConnProgId, "NV", "UmaConn(地方競馬)"))
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
                            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

                            // ソースごとに独立してtry/catchし、片方の失敗がもう片方の監視を止めない
                            // ようにする。
                            var jvResultTask = RunWatchFor(jvLink, wp, cts.Token);
                            var umaResultTask = RunWatchFor(umaConn, wp, cts.Token);

                            System.Threading.Tasks.Task.WaitAll(jvResultTask, umaResultTask);
                        }
                        break;
                    }

                    case "probe":
                        // 調査用。どのデータ種別で何が取得できるかを実際に叩いて確認する
                        // （地方競馬でオッズ・人気が別種別で提供されていないかの確認用）。
                        // WordPressには一切書き込まない。
                        RunProbeFor(jvLink);
                        RunProbeFor(umaConn);
                        break;

                    case "setup":
                        // 初回1回だけ手動実行: 利用キー入力ダイアログを開いて設定を保存する。
                        // 片方のProgIDが未確認/未登録でもう片方の結果が分からなくなるのを避けるため、
                        // 個別にtry/catchして両方の結果を必ず表示する。
                        RunSetupFor(jvLink);
                        RunSetupFor(umaConn);
                        break;

                    default:
                        Console.WriteLine("使い方: KeibaDataCollector.exe [setup|morning|watch|probe]");
                        Console.WriteLine("  setup   : 初回のみ。利用キー等をGUIダイアログで設定する。");
                        Console.WriteLine("  morning : 朝一バッチ。当日の出走表を取得しWordPressへ反映する。");
                        Console.WriteLine("  watch   : レース確定を監視し、結果・払戻を随時WordPressへ反映する。");
                        Console.WriteLine("  probe   : 調査用。どのデータ種別で何が取得できるか確認する（WordPressへは書き込まない）。");
                        break;
                }
            }
        }

        private static void RunProbeFor(JvSpecComDataSource source)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new DataSpecProbeService(source).Run(DateTime.Today);
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
                Console.WriteLine($"[{source.SourceName}] 朝一バッチ失敗（このソースのみスキップして続行）: {ex.Message}");
            }
        }

        private static async System.Threading.Tasks.Task RunWatchFor(
            JvSpecComDataSource source, WordPress.WordPressClient wp, CancellationToken ct)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                await new RaceResultService(source, wp, AppConfig.RealtimePollInterval)
                    .RunWatchLoopAsync(DateTime.Today, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Console.WriteLine($"[{source.SourceName}] 監視失敗（このソースのみスキップ）: {ex.Message}");
            }
        }
    }
}
