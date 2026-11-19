using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 調査用モード。あるデータ源が、どのデータ種別で何を返してくるかを実際に叩いて確認する。
    ///
    /// 経緯: 地方競馬（UmaConn）の結果で単勝オッズ・単勝人気順・後3ハロンが常に初期値
    /// （0000 / 00 / 000）だった。バイト位置のズレではないこと（同レコードの着差コード=343が
    /// 正しく読める）は確認済みなので、残る可能性は「その項目をSEレコードに載せていない」。
    /// 一方、地方競馬DATAの公式案内には「オッズデータと票数データは2010年2月以降」提供と
    /// あるため、オッズは別のデータ種別（速報オッズ）で配信されている可能性が高い。
    /// 推測で実装せず、実際に取得できるかをここで確かめる。
    /// </summary>
    public class DataSpecProbeService
    {
        private const string EarlyAnchorFromTime = "19860101000000";

        // JV-Data仕様書「（２）速報系データ」より。
        private static readonly (string Spec, string Name)[] RealtimeSpecsToProbe =
        {
            ("0B31", "速報オッズ（単複枠）"),
            ("0B30", "速報オッズ（全賭式）"),
            ("0B12", "速報レース情報（成績確定後）"),
            // 発売前に提供される予想データ。予想印を「発売前の評価順」で付けられるかの確認用
            // （お客様からのご相談 2026-11: 発売直後のオッズは最終オッズと乖離が大きいため）。
            ("0B13", "速報タイム型データマイニング予想"),
            ("0B17", "速報対戦型データマイニング予想"),
        };

        private static bool IsMiningSpec(string spec) => spec == "0B13" || spec == "0B17";

        private readonly IRaceDataSource _source;

        public DataSpecProbeService(IRaceDataSource source)
        {
            _source = source;
        }

        /// <param name="raceKeySlug">
        /// "20260811-46-1R" 形式。指定するとそのレースだけを調べる。
        /// 省略すると当日の最初のレースを使う。
        ///
        /// 名指しできるようにした理由: 競馬場によってオッズの配信時刻が違い、
        /// 「取得できていない競馬場のレース」を狙って調べる必要があるため。
        /// 最初のレースだけでは、既にオッズが出ている競馬場を引いてしまい何も分からない。
        /// </param>
        public void Run(DateTime targetDate, string raceKeySlug = null)
        {
            var raceKey = string.IsNullOrWhiteSpace(raceKeySlug)
                ? FindFirstRaceOfDay(targetDate)
                : ParseSlug(raceKeySlug);

            if (raceKey == null)
            {
                Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} の対象レースが見つかりませんでした。");
                return;
            }

            // 予想データは「何時に提供されるか」も確かめたいので、調査した時刻を残す。
            Console.WriteLine(
                $"[{_source.SourceName}] 調査対象レース: {raceKey.AsSlug()} (key={raceKey.AsJvRealtimeKey()}) " +
                $"調査時刻 {DateTime.Now:yyyy/MM/dd HH:mm}");

            foreach (var (spec, name) in RealtimeSpecsToProbe)
            {
                var found = ProbeRealtimeSpec(raceKey, spec, name, raceKey.AsJvRealtimeKey());
                // データマイニング予想はレース単位で取れない場合に備え、開催日単位のキーでも試す。
                if (!found && IsMiningSpec(spec))
                    ProbeRealtimeSpec(raceKey, spec, name + "・開催日単位", raceKey.RaceDate.ToString("yyyyMMdd"));
            }

            ProbeAccumulatedMining(raceKey);
        }

        /// <summary>レコードが1件以上取れたら true。</summary>
        private bool ProbeRealtimeSpec(RaceKey raceKey, string dataSpec, string specName, string key)
        {
            int rc = _source.OpenRealtime(dataSpec, key);
            if (rc != 0)
            {
                _source.Close();
                // -1は「該当データ無し」。それ以外はエラーコード（仕様書のコード表参照）。
                // -111/-114 はデータ種別・キーが不正（＝このデータ源ではその種別を扱っていない可能性）。
                Console.WriteLine(
                    $"[{_source.SourceName}] {dataSpec}({specName}) key={key}: 取得不可 rc={rc}" +
                    (rc == -1 ? "（該当データ無し）" :
                     rc == -111 ? "（データ種別が不正＝この種別は扱っていない可能性）" :
                     rc == -114 ? "（キーが不正）" : ""));
                return false;
            }

            var typeCounts = new Dictionary<string, int>();
            var oddsSamples = new List<string>();
            var miningLines = new List<string>();

            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3) { Thread.Sleep(500); continue; }
                    if (size < 0)
                    {
                        Console.WriteLine($"[{_source.SourceName}] {dataSpec}: Read失敗 {size}");
                        break;
                    }

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);
                    typeCounts[typeId] = typeCounts.TryGetValue(typeId, out var c) ? c + 1 : 1;

                    // O1(単複枠オッズ)なら、実際に単勝オッズ・人気順が入っているかを見る。
                    if (typeId == "O1" && oddsSamples.Count == 0)
                        oddsSamples.AddRange(DescribeTansyoOdds(buffer));

                    // DM/TM（データマイニング予想）なら、対象レースの中身と作成時刻を見る。
                    if (typeId == "DM" || typeId == "TM")
                        miningLines.AddRange(DescribeMining(buffer, raceKey));
                }
            }
            finally
            {
                _source.Close();
            }

            var breakdown = typeCounts.Count > 0
                ? string.Join(", ", typeCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))
                : "（レコードなし）";
            Console.WriteLine($"[{_source.SourceName}] {dataSpec}({specName}) key={key}: rc=0 レコード種別=[{breakdown}]");

            foreach (var line in oddsSamples.Concat(miningLines))
                Console.WriteLine($"    {line}");
            return typeCounts.Count > 0;
        }

        /// <summary>
        /// 蓄積系のデータマイニング予想（"MING"）に、対象レースの予想があるかを見る。
        /// 対象日の7日前以降のデータを読む（通常データ。ダイアログは出ない）。
        /// </summary>
        private void ProbeAccumulatedMining(RaceKey raceKey)
        {
            var fromTime = raceKey.RaceDate.AddDays(-7).ToString("yyyyMMdd") + "000000";
            var open = _source.Open("MING", fromTime, DataOption.Normal);
            if (open.ReturnCode < 0)
            {
                _source.Close();
                Console.WriteLine(
                    $"[{_source.SourceName}] MING(蓄積系データマイニング予想): 取得不可 rc={open.ReturnCode}" +
                    (open.ReturnCode == -1 ? "（該当データ無し）" :
                     open.ReturnCode == -111 ? "（データ種別が不正＝この種別は扱っていない可能性）" : ""));
                return;
            }

            var typeCounts = new Dictionary<string, int>();
            var lines = new List<string>();
            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3) { Thread.Sleep(500); continue; }
                    if (size < 0)
                    {
                        Console.WriteLine($"[{_source.SourceName}] MING: Read失敗 {size}");
                        break;
                    }

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);
                    typeCounts[typeId] = typeCounts.TryGetValue(typeId, out var c) ? c + 1 : 1;
                    if (typeId == "DM" || typeId == "TM")
                        lines.AddRange(DescribeMining(buffer, raceKey));
                }
            }
            finally
            {
                _source.Close();
            }

            var breakdown = typeCounts.Count > 0
                ? string.Join(", ", typeCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))
                : "（レコードなし）";
            Console.WriteLine($"[{_source.SourceName}] MING(蓄積系データマイニング予想) from={fromTime}: rc={open.ReturnCode} レコード種別=[{breakdown}]");
            if (lines.Count == 0 && typeCounts.Count > 0)
                Console.WriteLine($"    対象レース {raceKey.AsSlug()} の予想は含まれていません");
            foreach (var line in lines)
                Console.WriteLine($"    {line}");
        }

        /// <summary>
        /// データマイニング予想レコードのうち、対象レースのものを文字列化する。
        /// 作成時刻（朝9時に間に合うか）と、評価順に◎○▲△を付けた場合の結果を出す。
        /// </summary>
        private static List<string> DescribeMining(string rawRecord, RaceKey raceKey)
        {
            var lines = new List<string>();
            JvRecordParser.MiningRecord m;
            try
            {
                m = JvRecordParser.ParseMining(rawRecord);
            }
            catch (Exception ex)
            {
                lines.Add($"マイニング予想のパース失敗: {ex.Message}");
                return lines;
            }
            if (m == null || m.Key == null || m.Key.AsSlug() != raceKey.AsSlug()) return lines;

            var kind = m.Kind == "DM" ? "タイム型（予想走破タイム）" : "対戦型（予測スコア）";
            var ranked = m.RankedUmaban();
            string[] marks = { "◎", "○", "▲", "△" };
            var marked = string.Join(" ", ranked.Take(marks.Length).Select((u, i) => $"{marks[i]}{u}"));

            lines.Add($"{m.Kind} {kind} 作成 {m.MadeAt} データ区分={m.DataKubun} 頭数={m.Horses.Count}");
            lines.Add("  値（馬番=値）: " + string.Join(" ", m.Horses.Select(h => $"{h.Umaban}={h.Value.Trim()}")));
            lines.Add("  評価順で印を付けると: " + (marked.Length > 0 ? marked : "（有効な値なし）"));
            return lines;
        }

        /// <summary>O1レコードから単勝オッズ・人気順を先頭数頭ぶん取り出して文字列化する。</summary>
        private static List<string> DescribeTansyoOdds(string rawRecord)
        {
            var lines = new List<string>();

            var o1 = new JV_O1_ODDS_TANFUKUWAKU();
            try
            {
                o1.SetDataB(ref rawRecord);
            }
            catch (Exception ex)
            {
                lines.Add($"O1パース失敗: {ex.Message}");
                return lines;
            }

            foreach (var t in o1.OddsTansyoInfo)
            {
                if (string.IsNullOrWhiteSpace(t.Umaban) || t.Umaban.Trim() == "00") continue;
                lines.Add($"単勝オッズ: 馬番=[{t.Umaban}] オッズ=[{t.Odds}] 人気順=[{t.Ninki}]");
                if (lines.Count >= 5) return lines;
            }

            if (lines.Count == 0)
                lines.Add("単勝オッズ: 有効な馬番が1件も入っていません（未提供の可能性）");

            return lines;
        }

        /// <summary>当日のレースを1件だけ見つける（RAレコードから）。</summary>
        /// <summary>"20260811-46-1R" を RaceKey に戻す。形式が違えば null。</summary>
        private static RaceKey ParseSlug(string slug)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                slug.Trim(), @"^(\d{8})-(\w+)-(\d+)R$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                Console.WriteLine($"レースキーの形式が不正です: {slug}（例: 20260811-46-1R）");
                return null;
            }
            return new RaceKey
            {
                RaceDate = DateTime.ParseExact(m.Groups[1].Value, "yyyyMMdd", null),
                TrackCode = m.Groups[2].Value,
                RaceNumber = int.Parse(m.Groups[3].Value),
            };
        }

        private RaceKey FindFirstRaceOfDay(DateTime targetDate)
        {
            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode < 0)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] レース一覧の取得に失敗: {open.ReturnCode}");
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

                    if (JvRecordParser.GetRecordTypeId(buffer) != "RA") continue;

                    try
                    {
                        var (raceKey, _) = JvRecordParser.ParseCornerPassage(buffer);
                        if (raceKey.RaceDate.Date == targetDate.Date)
                            return raceKey;
                    }
                    catch
                    {
                        // 調査用途なので、壊れたレコードは黙って読み飛ばす。
                    }
                }
            }
            finally
            {
                _source.Close();
            }

            return null;
        }
    }
}
