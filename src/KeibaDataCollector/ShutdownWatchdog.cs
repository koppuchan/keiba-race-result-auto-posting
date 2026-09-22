using System;
using System.Diagnostics;
using System.Threading;

namespace KeibaDataCollector
{
    /// <summary>
    /// 作業が終わったあと、プロセスが確実に終了することを保証する。
    ///
    /// 実際に発生した障害（2026-08-12）:
    ///   predictモードは9:00に全レースを処理し「予想の反映完了」まで出力したのに、
    ///   プロセスが終了しなかった。バッチのログに "predict batch end" が出ていない。
    ///   多重起動を禁止（IgnoreNew）しているため、以降30分ごとの実行がすべて
    ///   0x800710E0（要求が拒否されました）で弾かれ、その日は9:00の1回しか動かなかった。
    ///   9:00は発売前でオッズが無いため、結果として終日predictが0件になった。
    ///
    /// 原因はCOMの後片付け。READMEに「UmaConn終了時のメモリリークダイアログ」を
    /// 未解決として記録していたとおり、解放時にモーダルダイアログが出ると
    /// 誰も操作できないサーバー上では永久に待ち続ける。
    ///
    /// 作業自体は完了しているので、後片付けが固まってもプロセスは終わらせてよい。
    /// 終わらせないほうが害が大きい（翌回以降が全部動かなくなる）。
    /// </summary>
    internal static class ShutdownWatchdog
    {
        /// <summary>後片付けにこれだけ待ってやり、過ぎたら強制的に終了させる。</summary>
        private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30);

        /// <summary>Environment.Exit 自体も固まる場合に備えた二段目の猶予。</summary>
        private static readonly TimeSpan HardKillAfter = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 起動直後に呼ぶ。作業そのものが固まった場合の最後の歯止め。
        ///
        /// 実際に発生した障害（2026-09-22）:
        ///   Arm()は作業が終わったあとにしか動かない。COMの呼び出しが途中で
        ///   返ってこないと、そこまで到達しないのでプロセスが永久に残る。
        ///   VPSでKeibaDataCollector.exeが5個（PID 2596/3920/5300/8084/9032）まで溜まり、
        ///   exeがロックされて deploy.ps1 がビルド前に中断した。
        ///   Stop-Process -Force でも落ちず、手作業での復旧が必要になった。
        ///
        /// 上限は「その用途ならどれだけ遅くてもこれ以内には終わる」値にする。
        /// 短すぎると正常な実行を切ってしまい、長すぎると歯止めにならない。
        /// setupは利用キー入力のダイアログを待つ用途なので、呼び出し側で除外する。
        /// </summary>
        public static void ArmDeadline(TimeSpan limit, string mode)
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(limit);

                Console.WriteLine(
                    $"[watchdog] {mode}モードが{limit.TotalHours:0.#}時間を超えました。" +
                    "処理が進んでいないためプロセスを終了します" +
                    "（残り続けるとexeがロックされ、次回以降の更新ができなくなるため）。");
                Console.Out.Flush();

                // 作業が途中なので成功扱いにはしない。
                new Thread(() => Environment.Exit(1)) { IsBackground = true }.Start();
                Thread.Sleep(HardKillAfter);
                Process.GetCurrentProcess().Kill();
            })
            {
                IsBackground = true,
                Name = "shutdown-deadline",
            };
            thread.Start();
        }

        /// <summary>
        /// 作業完了後に呼ぶ。正常に終了できればこの監視は何もしない
        /// （バックグラウンドスレッドなのでプロセス終了を妨げない）。
        /// </summary>
        public static void Arm(int exitCode)
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(GracePeriod);

                // ここに到達した＝猶予を過ぎても終了していない。
                Console.WriteLine(
                    $"[watchdog] 後片付けが{GracePeriod.TotalSeconds:0}秒で終わらないため、プロセスを終了します。" +
                    "（COM解放時のダイアログ等が原因。作業自体は完了しています）");
                Console.Out.Flush();

                // まずは終了コードを保ったまま終わらせる。
                new Thread(() => Environment.Exit(exitCode)) { IsBackground = true }.Start();

                // Environment.Exit も終了ハンドラで固まりうるので、最後は問答無用で落とす。
                Thread.Sleep(HardKillAfter);
                Process.GetCurrentProcess().Kill();
            })
            {
                IsBackground = true,
                Name = "shutdown-watchdog",
            };
            thread.Start();
        }
    }
}
