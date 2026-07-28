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
            using (var jvLink = new JvSpecComDataSource(AppConfig.JvLinkProgId, "JV-Link(中央競馬)"))
            using (var umaConn = new JvSpecComDataSource(AppConfig.UmaConnProgId, "UmaConn(地方競馬)"))
            {
                switch (mode)
                {
                    case "morning":
                    {
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        // 初回のみ RunInteractiveSetup() を手動実行して利用キーをGUIで登録しておくこと。
                        jvLink.Initialize(AppConfig.JvLinkSoftwareId);
                        umaConn.Initialize(AppConfig.JvLinkSoftwareId);

                        new RaceCardService(jvLink, wp).RunMorningBatch(DateTime.Today, trackCode: "");
                        new RaceCardService(umaConn, wp).RunMorningBatch(DateTime.Today, trackCode: "");
                        break;
                    }

                    case "watch":
                    {
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        jvLink.Initialize(AppConfig.JvLinkSoftwareId);
                        umaConn.Initialize(AppConfig.JvLinkSoftwareId);

                        using (var cts = new CancellationTokenSource())
                        {
                            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

                            var jvResultTask = new RaceResultService(jvLink, wp, AppConfig.RealtimePollInterval)
                                .RunWatchLoopAsync(realtimeDataSpec: "0B12", key: "", cts.Token); // データ種別コード要確認
                            var umaResultTask = new RaceResultService(umaConn, wp, AppConfig.RealtimePollInterval)
                                .RunWatchLoopAsync(realtimeDataSpec: "0B12", key: "", cts.Token); // データ種別コード要確認

                            System.Threading.Tasks.Task.WaitAll(jvResultTask, umaResultTask);
                        }
                        break;
                    }

                    case "setup":
                        // 初回1回だけ手動実行: 利用キー入力ダイアログを開いて設定を保存する。
                        jvLink.RunInteractiveSetup();
                        umaConn.RunInteractiveSetup();
                        break;

                    default:
                        Console.WriteLine("使い方: KeibaDataCollector.exe [setup|morning|watch]");
                        Console.WriteLine("  setup   : 初回のみ。利用キー等をGUIダイアログで設定する。");
                        Console.WriteLine("  morning : 朝一バッチ。当日の出走表を取得しWordPressへ反映する。");
                        Console.WriteLine("  watch   : レース確定を監視し、結果・払戻を随時WordPressへ反映する。");
                        break;
                }
            }
        }
    }
}
