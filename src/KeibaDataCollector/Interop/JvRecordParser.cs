using System;
using System.Collections.Generic;
using KeibaDataCollector.Models;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// JVRead/UCRead で得られる固定長レコード文字列を、レコード種別ID（先頭2バイト。
    /// "RA"=レース詳細, "SE"=馬毎レース情報, "HR"=払戻 等）で判別してパースする層。
    ///
    /// バイト位置の裏付けは JRA-VAN公式SDK付属の JVData_Struct.cs
    /// （Interop/JvDataSdk/JVData_Struct.cs、実機のSDKダウンロードから取得・配置済み）の
    /// SetDataB() 実装をそのまま利用する。したがって着順・払戻金額などのオフセットを
    /// 独自に推測している箇所はない。
    /// </summary>
    public static class JvRecordParser
    {
        public static string GetRecordTypeId(string rawRecord)
        {
            if (string.IsNullOrEmpty(rawRecord) || rawRecord.Length < 2)
                return string.Empty;
            return rawRecord.Substring(0, 2);
        }

        // ★診断用: 最初の1件だけ、SEレコードの生の中身(先頭76文字=RECORD_ID+RACE_ID+Wakuban+
        // Umaban+KettoNum+Bameiの範囲)をHEXダンプする。バイト位置ズレの切り分け用。
        private static bool _seDebugLogged;

        /// <summary>"SE"レコード（馬毎レース情報）を朝一の出走表項目としてパースする。</summary>
        public static (RaceKey Key, RaceCardEntry Entry) ParseRaceCard(string rawRecord)
        {
            if (!_seDebugLogged)
            {
                _seDebugLogged = true;
                DumpSeRecordDebug(rawRecord);
            }

            var se = new JV_SE_RACE_UMA();
            se.SetDataB(ref rawRecord);

            var entry = new RaceCardEntry
            {
                Waku = SafeInt(se.Wakuban),
                Umaban = SafeInt(se.Umaban),
                HorseName = Trim(se.Bamei),
                SexAge = FormatSexAge(se.SexCD, se.Barei),
                Kinryo = SafeTenths(se.Futan),
                JockeyName = Trim(se.KisyuRyakusyo),
                TrainerName = Trim(se.ChokyosiRyakusyo),
            };

            return (ExtractRaceKey(se.id), entry);
        }

        /// <summary>"SE"レコード（馬毎レース情報、確定後）を着順結果としてパースする。</summary>
        public static (RaceKey Key, RaceResultEntry Entry) ParseRaceResult(string rawRecord)
        {
            var se = new JV_SE_RACE_UMA();
            se.SetDataB(ref rawRecord);

            var entry = new RaceResultEntry
            {
                Chakujun = SafeInt(se.KakuteiJyuni),
                Waku = SafeInt(se.Wakuban),
                Umaban = SafeInt(se.Umaban),
                HorseName = Trim(se.Bamei),
                SexAge = FormatSexAge(se.SexCD, se.Barei),
                Kinryo = SafeTenths(se.Futan),
                JockeyName = Trim(se.KisyuRyakusyo),
                Time = FormatTime(se.Time),
                // TODO: ChakusaCDは数値の着差コード。ハナ/クビ/アタマ等の表示文字列への変換表は
                // JV-Data仕様書のコード表を要確認（今回入手したSDK同梱ドキュメントには見当たらず）。
                // 誤った着差表示を出すリスクを避けるため、現状は生コードをそのまま出す。
                ChakusaText = Trim(se.ChakusaCD),
                Ninki = SafeInt(se.Ninki),
                TanshoOdds = SafeTenths(se.Odds),
                Ushi3F = SafeTenths(se.HaronTimeL3),
                TrainerName = Trim(se.ChokyosiRyakusyo),
                BataijuuZengo = SafeInt(se.BaTaijyu),
                BataijuuZogen = FormatZogen(se.ZogenFugo, se.ZogenSa),
            };

            return (ExtractRaceKey(se.id), entry);
        }

        /// <summary>"HR"レコード（払戻）をパースする。1レコードに全券種分がまとまっているため
        /// 複数件の<see cref="PayoutEntry"/>を返す。</summary>
        public static (RaceKey Key, List<PayoutEntry> Payouts) ParsePayouts(string rawRecord)
        {
            var hr = new JV_HR_PAY();
            hr.SetDataB(ref rawRecord);

            var payouts = new List<PayoutEntry>();
            AddPayInfo1(payouts, "単勝", hr.PayTansyo);
            AddPayInfo1(payouts, "複勝", hr.PayFukusyo);
            AddPayInfo1(payouts, "枠連", hr.PayWakuren);
            AddPayInfo2(payouts, "馬連", hr.PayUmaren);
            AddPayInfo2(payouts, "ワイド", hr.PayWide);
            AddPayInfo2(payouts, "馬単", hr.PayUmatan);
            AddPayInfo3(payouts, "3連複", hr.PaySanrenpuku);
            AddPayInfo4(payouts, "3連単", hr.PaySanrentan);

            return (ExtractRaceKey(hr.id), payouts);
        }

        /// <summary>"RA"レコード（レース詳細）からコーナー通過順位を取り出す。</summary>
        public static (RaceKey Key, List<string> CornerPassage) ParseCornerPassage(string rawRecord)
        {
            var ra = new JV_RA_RACE();
            ra.SetDataB(ref rawRecord);

            var passage = new List<string>();
            foreach (var corner in ra.CornerInfo)
            {
                var jyuni = Trim(corner.Jyuni);
                if (!string.IsNullOrEmpty(jyuni))
                    passage.Add(jyuni);
            }

            return (ExtractRaceKey(ra.id), passage);
        }

        private static void DumpSeRecordDebug(string rawRecord)
        {
            Console.WriteLine($"SE診断: 文字列長={rawRecord.Length}（期待値555）");

            var len = Math.Min(80, rawRecord.Length);
            var hexBuilder = new System.Text.StringBuilder();
            for (int i = 0; i < len; i++)
                hexBuilder.Append(((int)rawRecord[i]).ToString("X2")).Append(' ');

            Console.WriteLine($"SE診断: 先頭{len}文字のHEX=[{hexBuilder}]");
            Console.WriteLine($"SE診断: 先頭{len}文字のそのまま表示=[{rawRecord.Substring(0, len)}]");
        }

        private static RaceKey ExtractRaceKey(RACE_ID id)
        {
            var monthDay = Trim(id.MonthDay);
            var month = monthDay.Length >= 2 ? SafeInt(monthDay.Substring(0, 2)) : 0;
            var day = monthDay.Length >= 4 ? SafeInt(monthDay.Substring(2, 2)) : 0;
            var year = SafeInt(id.Year);

            DateTime date;
            try
            {
                date = year > 0 && month > 0 && day > 0 ? new DateTime(year, month, day) : DateTime.MinValue;
            }
            catch (ArgumentOutOfRangeException)
            {
                date = DateTime.MinValue;
            }

            return new RaceKey
            {
                TrackCode = Trim(id.JyoCD),
                RaceDate = date,
                RaceNumber = SafeInt(id.RaceNum),
            };
        }

        private static void AddPayInfo1(List<PayoutEntry> list, string ticketType, PAY_INFO1[] entries)
        {
            foreach (var e in entries)
            {
                if (IsEmptyPay(e.Pay)) continue;
                list.Add(new PayoutEntry
                {
                    TicketType = ticketType,
                    Combination = Trim(e.Umaban),
                    Amount = SafeInt(e.Pay),
                    Ninki = SafeInt(e.Ninki),
                });
            }
        }

        private static void AddPayInfo2(List<PayoutEntry> list, string ticketType, PAY_INFO2[] entries)
        {
            foreach (var e in entries)
            {
                if (IsEmptyPay(e.Pay)) continue;
                list.Add(new PayoutEntry
                {
                    TicketType = ticketType,
                    Combination = FormatKumi(e.Kumi),
                    Amount = SafeInt(e.Pay),
                    Ninki = SafeInt(e.Ninki),
                });
            }
        }

        private static void AddPayInfo3(List<PayoutEntry> list, string ticketType, PAY_INFO3[] entries)
        {
            foreach (var e in entries)
            {
                if (IsEmptyPay(e.Pay)) continue;
                list.Add(new PayoutEntry
                {
                    TicketType = ticketType,
                    Combination = FormatKumi(e.Kumi),
                    Amount = SafeInt(e.Pay),
                    Ninki = SafeInt(e.Ninki),
                });
            }
        }

        private static void AddPayInfo4(List<PayoutEntry> list, string ticketType, PAY_INFO4[] entries)
        {
            foreach (var e in entries)
            {
                if (IsEmptyPay(e.Pay)) continue;
                list.Add(new PayoutEntry
                {
                    TicketType = ticketType,
                    Combination = FormatKumi(e.Kumi),
                    Amount = SafeInt(e.Pay),
                    Ninki = SafeInt(e.Ninki),
                });
            }
        }

        private static bool IsEmptyPay(string pay) => SafeInt(pay) <= 0;

        /// <summary>2桁ずつの馬番連結（例:"0609"）を"6-9"のようなハイフン区切りに整形する。</summary>
        private static string FormatKumi(string kumi)
        {
            var digits = Trim(kumi);
            if (string.IsNullOrEmpty(digits) || digits.Length % 2 != 0)
                return digits;

            var parts = new List<string>();
            for (int i = 0; i < digits.Length; i += 2)
                parts.Add(SafeInt(digits.Substring(i, 2)).ToString());

            return string.Join("-", parts);
        }

        /// <summary>走破タイム4桁("1325")を"1:32.5"形式に整形する。</summary>
        private static string FormatTime(string raw)
        {
            var t = Trim(raw);
            if (t.Length < 4) return string.Empty;

            var minutes = t.Substring(0, 1);
            var seconds = t.Substring(1, 2);
            var tenths = t.Substring(3, 1);
            return $"{minutes}:{seconds}.{tenths}";
        }

        private static string FormatSexAge(string sexCd, string barei)
        {
            string sex;
            switch (Trim(sexCd))
            {
                case "1": sex = "牡"; break;
                case "2": sex = "牝"; break;
                case "3": sex = "セ"; break;
                default: sex = string.Empty; break;
            }

            var age = SafeInt(barei);
            return age > 0 ? $"{sex}{age}" : sex;
        }

        /// <summary>増減符号("+"/"-"/空白)と増減差(3桁)から符号付きの馬体重増減を返す。
        /// "999"（体重計不能）は0として扱う。</summary>
        private static int FormatZogen(string zogenFugo, string zogenSa)
        {
            var sa = SafeInt(zogenSa);
            if (sa == 999) return 0;
            return Trim(zogenFugo) == "-" ? -sa : sa;
        }

        /// <summary>末尾1桁を小数点として扱う数値文字列(例:"540"->54.0)を実数に変換する。</summary>
        private static double SafeTenths(string s) => SafeInt(s) / 10.0;

        private static int SafeInt(string s)
        {
            var t = Trim(s);
            return int.TryParse(t, out var v) ? v : 0;
        }

        private static string Trim(string s) => (s ?? string.Empty).Trim();
    }
}
