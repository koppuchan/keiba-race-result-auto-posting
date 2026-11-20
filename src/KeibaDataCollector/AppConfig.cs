using System;
using System.Configuration;

namespace KeibaDataCollector
{
    // 機密情報は環境変数を優先。App.configの値は非機密の既定値/開発時フォールバックとしてのみ使う。
    internal static class AppConfig
    {
        public static string JvLinkProgId => Get("JvLinkProgId");
        public static string UmaConnProgId => Get("UmaConnProgId");
        public static string JvLinkSoftwareId => Get("JvLinkSoftwareId");

        public static string WordPressBaseUrl => Get("WordPressBaseUrl");
        public static string WordPressUser => Get("WordPressUser");
        public static string WordPressAppPassword => Get("WordPressAppPassword");

        public static TimeSpan RealtimePollInterval =>
            TimeSpan.FromSeconds(int.Parse(Get("RealtimePollIntervalSeconds")));

        /// <summary>keiba-ai-data-system の過去データ（SQLite）。発売前スコアの計算で読み取り専用で開く。</summary>
        public static string KeibaAiHistoryDbPath => Get("KeibaAiHistoryDbPath");

        /// <summary>
        /// 予想印の付け方。
        ///   odds   : 朝一オッズの人気順（従来どおり）
        ///   shadow : 公開はオッズの人気順のまま、発売前スコアの印も計算して記録だけする（並行稼働・比較用）
        ///   score  : 発売前スコアで印を付けて公開する（計算できなかったレースはオッズの人気順）
        /// </summary>
        public static PredictionMethod PredictionMethod
        {
            get
            {
                var value = GetOptional("PredictionMethod", "odds").Trim().ToLowerInvariant();
                switch (value)
                {
                    case "shadow": return PredictionMethod.Shadow;
                    case "score": return PredictionMethod.Score;
                    case "odds": return PredictionMethod.Odds;
                    default:
                        Console.WriteLine($"設定値 PredictionMethod の値 '{value}' が不明です（odds として扱います）。");
                        return PredictionMethod.Odds;
                }
            }
        }

        private static string GetOptional(string key, string fallback)
        {
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
            var fromConfig = ConfigurationManager.AppSettings[key];
            return string.IsNullOrEmpty(fromConfig) ? fallback : fromConfig;
        }

        private static string Get(string key)
        {
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

            var fromConfig = ConfigurationManager.AppSettings[key];
            if (!string.IsNullOrEmpty(fromConfig)) return fromConfig;

            throw new InvalidOperationException(
                $"設定値 '{key}' が未設定です。環境変数か App.config に設定してください。");
        }
    }

    internal enum PredictionMethod
    {
        Odds,
        Shadow,
        Score,
    }
}
