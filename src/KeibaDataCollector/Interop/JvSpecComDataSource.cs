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

        private const int ReadBufferSize = 110000;

        public int Read(out string buffer, out string fileName)
        {
            // JVGets/NVGets(out byte[] buff, in long size, out string filename) を使う。
            // JVRead/NVRead（string版）は、実機で検証した結果、後期バインドのByRef文字列
            // マーシャリングでShift-JISバイト列が壊れる問題が確認された（BSTR化の過程で
            // 元のバイトが失われ、事後的な文字コード変換では復元できないケースがあった）。
            // JV-Linkインターフェース仕様書に「JVGetsはSJISをSJISのまま渡すことにより、
            // JV-Link内部での変換およびアプリケーション側でのUNICODE→SJIS変換が不要になり
            // コード変換におけるオーバーヘッドがなくなりました」と明記されている通り、
            // バイト配列で直接受け取ることで文字コード変換の問題自体を回避する。
            var args = new object[] { new byte[ReadBufferSize], ReadBufferSize, string.Empty };
            int rc = (int)InvokeByRef("Gets", args, isByRef: new[] { true, false, true });

            // ここでは Shift_JIS ではなく Latin-1 でデコードする。
            // JVData_Struct.cs の SetDataB は、渡された文字列を Str2Byte で再びバイト列へ戻し、
            // 仕様書どおりのバイト位置で各フィールドを切り出す作りになっている。
            // Shift_JIS でデコードすると バイト→文字列→バイト の往復が可逆にならず
            // （未定義バイト列が '?' に潰れて1バイト減る）、以降のフィールドが全てズレる。
            // Latin-1 は 0x00〜0xFF を U+0000〜U+00FF に一対一対応させるため往復が完全に可逆で、
            // SetDataB 側が元のバイト列をそのまま復元できる。
            // 日本語への変換は SetDataB 内の MidB2S が切り出し後に Shift_JIS で行う。
            var rawBytes = args[0] as byte[];
            buffer = (rawBytes != null && rc > 0)
                ? Encoding.GetEncoding(28591).GetString(rawBytes, 0, rc)
                : string.Empty;
            fileName = args[2] as string ?? string.Empty;
            return rc;
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
