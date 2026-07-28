using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// JV-Link / UmaConn 共通の後期バインド(late-bound)COMラッパー。
    /// 両者は同一のJV-Link Interface Specificationに準拠しているため、ProgIDを差し替えるだけで
    /// どちらのデータソースにも使える。
    ///
    /// 後期バインド（Type.GetTypeFromProgID + InvokeMember）を採用している理由:
    /// Visual StudioでCOM参照を追加すると型名は実行環境ごとに生成されるインタロップアセンブリに依存するため、
    /// この環境（Windows/SDK未導入）では正確な生成型名を検証できない。ProgIDは比較的安定した文字列のため、
    /// こちらを採用して環境非依存にしている。
    /// 実行時にCOMイベント（JVWatchEvent等）を使う場合は後期バインドでは購読できないため、
    /// このクラスではポーリング方式（Read()を一定間隔で呼ぶ）を前提にしている。
    /// </summary>
    public class JvSpecComDataSource : IRaceDataSource
    {
        private readonly string _progId;
        private object _com;
        private Type _type;

        public string SourceName { get; }

        public JvSpecComDataSource(string progId, string sourceName)
        {
            _progId = progId;
            SourceName = sourceName;
        }

        public void Initialize(string softwareId)
        {
            EnsureComObject();

            // JVInit(string sid) -- sidはJRA-VANソフトウェアID相当。UmaConn側で同名メソッドか要確認。
            int rc = (int)Invoke("JVInit", softwareId);
            if (rc < 0)
                throw new InvalidOperationException($"{SourceName} JVInit failed: {rc}");
        }

        public void RunInteractiveSetup()
        {
            // JVSetUIProperties() -- 利用キー入力ダイアログを表示し、設定を保存する。
            // 初回のみ手動実行想定（自動実行フローには組み込まない）。
            // JVInitより前に呼べる想定のため、Initialize()を経由せずCOMオブジェクトだけ用意する。
            EnsureComObject();
            Invoke("JVSetUIProperties");
        }

        private void EnsureComObject()
        {
            if (_com != null) return;

            _type = Type.GetTypeFromProgID(_progId);
            if (_type == null)
            {
                throw new InvalidOperationException(
                    $"COMオブジェクト '{_progId}' が見つかりません。" +
                    $"{SourceName} がこのPCにインストール・登録されているか確認してください。" +
                    "（UmaConnの場合、ProgIDが 'NVDTLab.NVLink' で正しいか未確認 -- " +
                    "レジストリのHKEY_CLASSES_ROOTでNVDTLab関連のキーを確認するか、" +
                    "UmaConn付属のサンプルコードを確認してください）");
            }
            _com = Activator.CreateInstance(_type);
        }

        public OpenResult Open(string dataSpec, string fromTime, DataOption option)
        {
            // JVOpen(string dataspec, string fromtime, int option,
            //        out int readcount, out int downloadcount, out string lastfiletimestamp)
            // 引数の正確な型・並び順はJV-Link/UmaConn仕様書で要確認。
            var args = new object[] { dataSpec, fromTime, (int)option, 0, 0, "" };
            int rc = (int)Invoke("JVOpen", args);

            return new OpenResult
            {
                ReturnCode = rc,
                ReadCount = SafeInt(args[3]),
                DownloadCount = SafeInt(args[4]),
                LastFileTimestamp = args[5] as string ?? string.Empty,
            };
        }

        public int Read(out string buffer, out string fileName)
        {
            // JVRead(out string buff, out int size, out string filename)
            var args = new object[] { string.Empty, 110000, string.Empty };
            int rc = (int)Invoke("JVRead", args);
            buffer = args[0] as string ?? string.Empty;
            fileName = args[2] as string ?? string.Empty;
            return rc;
        }

        public int OpenRealtime(string dataSpec, string key)
        {
            // JVRTOpen(string dataspec, string key) -- リアルタイム系データ種別コードは仕様書で要確認
            // （速報オッズ/中間成績/確定成績/払戻 等でコードが分かれている）。
            return (int)Invoke("JVRTOpen", dataSpec, key);
        }

        public void Close()
        {
            if (_com != null)
                Invoke("JVClose");
        }

        public void Dispose()
        {
            Close();
            if (_com != null && Marshal.IsComObject(_com))
                Marshal.FinalReleaseComObject(_com);
            _com = null;
        }

        private object Invoke(string method, params object[] args)
        {
            return _type.InvokeMember(
                method,
                BindingFlags.InvokeMethod,
                null,
                _com,
                args);
        }

        private static int SafeInt(object value) => value is int i ? i : 0;
    }
}
