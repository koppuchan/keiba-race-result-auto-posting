using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// JV-Link / UmaConn 共通の後期バインド(late-bound)COMラッパー。
    /// 両者は同一のインターフェース仕様に準拠しているが、メソッド名のプレフィックスが異なる
    /// （JV-Link: JV*, UmaConn: NV*）。実機のCOM型情報を確認して判明した対応表:
    ///   JVInit  ⇔ NVInit,  JVOpen ⇔ NVOpen,  JVRead ⇔ NVRead,  JVClose ⇔ NVClose,
    ///   JVRTOpen ⇔ NVRTOpen,  JVSetUIProperties ⇔ NVSetUIProperties,
    ///   JVSetServiceKey ⇔ NVSetServiceKey 等（PowerShellから New-Object -ComObject で
    ///   Get-Member して両COMオブジェクトのメソッド一覧を比較し確認済み）。
    /// そのためProgIDに加えてmethodPrefix（"JV"/"NV"）を渡す。
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
        private readonly string _methodPrefix;
        private object _com;
        private Type _type;

        public string SourceName { get; }

        public JvSpecComDataSource(string progId, string methodPrefix, string sourceName)
        {
            _progId = progId;
            _methodPrefix = methodPrefix;
            SourceName = sourceName;
        }

        public void Initialize(string softwareId)
        {
            EnsureComObject();

            // JVInit/NVInit(string sid) -- sidはJRA-VANソフトウェアID相当。
            int rc = (int)Invoke("Init", softwareId);
            if (rc < 0)
                throw new InvalidOperationException($"{SourceName} {_methodPrefix}Init failed: {rc}");
        }

        public void RunInteractiveSetup()
        {
            // JVSetUIProperties/NVSetUIProperties() -- 利用キー入力ダイアログを表示し、設定を保存する。
            // 初回のみ手動実行想定（自動実行フローには組み込まない）。
            // Initより前に呼べる想定のため、Initialize()を経由せずCOMオブジェクトだけ用意する。
            EnsureComObject();
            Invoke("SetUIProperties");
        }

        private void EnsureComObject()
        {
            if (_com != null) return;

            _type = Type.GetTypeFromProgID(_progId);
            if (_type == null)
            {
                throw new InvalidOperationException(
                    $"COMオブジェクト '{_progId}' が見つかりません。" +
                    $"{SourceName} がこのPCにインストール・登録されているか確認してください。");
            }
            _com = Activator.CreateInstance(_type);
        }

        public OpenResult Open(string dataSpec, string fromTime, DataOption option)
        {
            // JVOpen/NVOpen(string dataspec, string fromtime, int option,
            //        out int readcount, out int downloadcount, out string lastfiletimestamp)
            // readcount/downloadcount/lastfiletimestampはout引数のため、ByRefでのInvokeが必要
            // （実機で確認: ByRef指定なしだと書き戻しが行われず常に呼び出し前の値のまま）。
            var args = new object[] { dataSpec, fromTime, (int)option, 0, 0, "" };
            int rc = (int)InvokeByRef("Open", args, isByRef: new[] { false, false, false, true, true, true });

            return new OpenResult
            {
                ReturnCode = rc,
                ReadCount = SafeInt(args[3]),
                DownloadCount = SafeInt(args[4]),
                LastFileTimestamp = args[5] as string ?? string.Empty,
            };
        }

        // ★診断用: 呼び出し1回目だけ、out引数の書き戻しが実際に効いているかログに出す。
        private bool _readDebugLogged;

        public int Read(out string buffer, out string fileName)
        {
            // JVRead/NVRead(out string buff, in long size, out string filename)
            // buff/filenameはout引数のためByRefでのInvokeが必要（Open同様、実機で確認済みの問題）。
            var args = new object[] { string.Empty, 110000, string.Empty };
            int rc = (int)InvokeByRef("Read", args, isByRef: new[] { true, false, true });

            if (!_readDebugLogged)
            {
                _readDebugLogged = true;
                var arg0Str = args[0] as string;
                var preview = arg0Str == null ? "(not a string)" : arg0Str.Substring(0, Math.Min(50, arg0Str.Length));
                Console.WriteLine(
                    $"[{SourceName}] Read診断: rc={rc}, " +
                    $"args[0]の型={args[0]?.GetType()?.FullName ?? "null"}, args[0]の長さ={arg0Str?.Length ?? -1}, " +
                    $"args[0]の内容(先頭50文字)=[{preview}], " +
                    $"args[1]の型={args[1]?.GetType()?.FullName ?? "null"}, args[1]の値={args[1]}, " +
                    $"args[2]の型={args[2]?.GetType()?.FullName ?? "null"}, args[2]の値={args[2]}");
            }

            // COMから返るbuffは「各バイト値をそのまま1文字として詰めた生バイト列」であり、
            // JVData_Struct.cs（SetDataB内でShift_JISとしてGetBytes()し直す前提）はこの生バイト列を
            // 一度Shift_JISとして正しくデコードした文字列を渡される想定になっている。
            // 実機で確認: このデコードをせずSetDataBに渡すと全角文字を含むフィールド以降が
            // バイト位置ズレを起こし文字化けする。
            buffer = DecodeRawComString(args[0] as string);
            fileName = args[2] as string ?? string.Empty;
            return rc;
        }

        private static string DecodeRawComString(string rawByteString)
        {
            if (string.IsNullOrEmpty(rawByteString)) return string.Empty;

            var bytes = new byte[rawByteString.Length];
            for (int i = 0; i < rawByteString.Length; i++)
                bytes[i] = unchecked((byte)rawByteString[i]);

            return Encoding.GetEncoding("Shift_JIS").GetString(bytes);
        }

        public int OpenRealtime(string dataSpec, string key)
        {
            // JVRTOpen/NVRTOpen(string dataspec, string key) -- リアルタイム系データ種別コードは仕様書で要確認
            // （速報オッズ/中間成績/確定成績/払戻 等でコードが分かれている）。
            return (int)Invoke("RTOpen", dataSpec, key);
        }

        public void Close()
        {
            if (_com == null) return;
            try
            {
                Invoke("Close");
            }
            catch (Exception ex)
            {
                // Dispose経路から呼ばれるため、後片付けの失敗でアプリ全体を落とさない。
                Console.WriteLine($"[{SourceName}] Close失敗（無視して続行）: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Close();
            if (_com != null && Marshal.IsComObject(_com))
                Marshal.FinalReleaseComObject(_com);
            _com = null;
        }

        private object Invoke(string methodSuffix, params object[] args)
        {
            var method = _methodPrefix + methodSuffix;
            return _type.InvokeMember(
                method,
                BindingFlags.InvokeMethod,
                null,
                _com,
                args);
        }

        /// <summary>
        /// out引数を含むCOMメソッド用。Type.InvokeMemberは既定ではByRef引数の書き戻しを保証しないため
        /// （実機で確認: JVOpen/JVReadのout引数が常に空/初期値のまま返ってきた）、
        /// ParameterModifierで明示的にByRefを指定して呼び出す。呼び出し後、argsの該当要素が更新される。
        /// </summary>
        private object InvokeByRef(string methodSuffix, object[] args, bool[] isByRef)
        {
            var method = _methodPrefix + methodSuffix;
            var modifier = new ParameterModifier(args.Length);
            for (int i = 0; i < args.Length; i++)
                modifier[i] = isByRef[i];

            return _type.InvokeMember(
                method,
                BindingFlags.InvokeMethod,
                Type.DefaultBinder,
                _com,
                args,
                new[] { modifier },
                null,
                null);
        }

        private static int SafeInt(object value) => value is int i ? i : 0;
    }
}
