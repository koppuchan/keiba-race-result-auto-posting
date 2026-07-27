using System;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// JVRead/UCRead で得られる固定長レコード文字列を、レコード種別ID（先頭2バイト。
    /// 例: "RA"=レース詳細, "SE"=馬毎レース情報, "HR"=払戻 等）で判別してパースする層。
    ///
    /// 意図的に未実装にしている: 各レコードのバイトオフセットは
    /// JRA-VAN公式SDK付属の JVData_Struct.cs（フィールド定義済み構造体）で確定させる必要があり、
    /// ここを勘で実装すると着順・払戻金額を誤って本番サイトに公開するリスクがある。
    /// SDKからJVData_Struct.cs（またはJV-Data仕様書の該当レコード定義部）を共有してもらえれば、
    /// このクラスに実際のマッピングを実装する。
    /// </summary>
    public static class JvRecordParser
    {
        public static string GetRecordTypeId(string rawRecord)
        {
            if (string.IsNullOrEmpty(rawRecord) || rawRecord.Length < 2)
                return string.Empty;
            return rawRecord.Substring(0, 2);
        }

        public static RaceCardEntry ParseRaceCardEntry(string rawRecord)
        {
            // TODO: レコード種別 "SE"（馬毎出走情報）を JVData_Struct.cs の該当構造体でマッピングする。
            throw new NotImplementedException(
                "JVData_Struct.cs のフィールド定義を組み込むまで未実装。SourceDetectはGetRecordTypeIdで可能。");
        }

        public static RaceResultEntry ParseRaceResultEntry(string rawRecord)
        {
            // TODO: レコード種別 "SE"（確定後は着順・タイム等が埋まる）をマッピングする。
            throw new NotImplementedException(
                "JVData_Struct.cs のフィールド定義を組み込むまで未実装。");
        }

        public static PayoutEntry ParsePayoutEntry(string rawRecord)
        {
            // TODO: レコード種別 "HR"（払戻）をマッピングする。
            throw new NotImplementedException(
                "JVData_Struct.cs のフィールド定義を組み込むまで未実装。");
        }
    }
}
