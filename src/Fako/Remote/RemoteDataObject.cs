using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Fako.Remote;

/// <summary>
/// SSH フェンスからドラッグするときのデータ（Windows の「仮想ファイル」）。
///
/// エクスプローラーには FileGroupDescriptorW（ファイル名・サイズ・更新日時の一覧）と
/// FileContents（中身のストリーム）を渡す。中身は**ドロップされて要求されたときに初めて**ダウンロードするので、
/// ドラッグを始めただけでは転送しない。フォルダーは中身を相対パスで展開して渡す。
/// Fako のフェンス同士の移動用に、リモートのパスと元のフェンスもテキストで載せる。
/// </summary>
[ComVisible(true)]
internal sealed class RemoteDataObject : ComIDataObject
{
    public sealed record VirtualFile(string RelativePath, RemoteEntry? Entry, bool IsDirectory, long Size, DateTime Modified);

    private static readonly short CfDescriptor = Format("FileGroupDescriptorW");
    private static readonly short CfContents = Format("FileContents");
    private static readonly short CfPreferredEffect = Format("Preferred DropEffect");
    public static readonly short CfRemotePaths = Format(FenceWindow.RemoteFormat);
    public static readonly short CfSourceFence = Format(FenceWindow.SourceFenceFormat);

    private readonly Func<List<VirtualFile>> _expand;
    private readonly Func<RemoteEntry, string> _download;
    private readonly string[] _remotePaths;
    private readonly string _sourceFence;
    private List<VirtualFile>? _files;

    /// <param name="expand">ドラッグする項目を、フォルダーの中身まで展開した一覧を作る（最初に必要になったときに 1 回だけ）。</param>
    /// <param name="download">1 ファイルを一時フォルダーにダウンロードして、そのパスを返す（ドロップ時に呼ばれる）。</param>
    public RemoteDataObject(string[] remotePaths, string sourceFence, Func<List<VirtualFile>> expand, Func<RemoteEntry, string> download)
    {
        _remotePaths = remotePaths;
        _sourceFence = sourceFence;
        _expand = expand;
        _download = download;
    }

    private List<VirtualFile> Files => _files ??= _expand();

    private IEnumerable<FORMATETC> Formats()
    {
        yield return Fmt(CfDescriptor, TYMED.TYMED_HGLOBAL);
        yield return Fmt(CfContents, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL);
        yield return Fmt(CfPreferredEffect, TYMED.TYMED_HGLOBAL);
        yield return Fmt(CfRemotePaths, TYMED.TYMED_HGLOBAL);
        yield return Fmt(CfSourceFence, TYMED.TYMED_HGLOBAL);
    }

    // ------------------------------------------------------------------
    // IDataObject
    // ------------------------------------------------------------------

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == CfDescriptor && Has(format, TYMED.TYMED_HGLOBAL))
        {
            medium = HGlobal(BuildDescriptor());
        }
        else if (format.cfFormat == CfContents && format.lindex >= 0 && format.lindex < Files.Count)
        {
            var file = Files[format.lindex];
            if (file.IsDirectory || file.Entry == null) throw new COMException("フォルダー", DV_E_FORMATETC);
            var local = _download(file.Entry);
            if (Has(format, TYMED.TYMED_ISTREAM))
            {
                var stream = new ComFileStream(local);
                medium.tymed = TYMED.TYMED_ISTREAM;
                medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
            }
            else
            {
                medium = HGlobal(File.ReadAllBytes(local));
            }
        }
        else if (format.cfFormat == CfPreferredEffect && Has(format, TYMED.TYMED_HGLOBAL))
        {
            medium = HGlobal(BitConverter.GetBytes(1)); // DROPEFFECT_COPY
        }
        else if (format.cfFormat == CfRemotePaths && Has(format, TYMED.TYMED_HGLOBAL))
        {
            medium = HGlobal(Encoding.Unicode.GetBytes(string.Join("\n", _remotePaths) + "\0"));
        }
        else if (format.cfFormat == CfSourceFence && Has(format, TYMED.TYMED_HGLOBAL))
        {
            medium = HGlobal(Encoding.Unicode.GetBytes(_sourceFence + "\0"));
        }
        else
        {
            throw new COMException("未対応の形式", DV_E_FORMATETC);
        }
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();

    public int QueryGetData(ref FORMATETC format)
    {
        foreach (var f in Formats())
        {
            if (f.cfFormat == format.cfFormat && (f.tymed & format.tymed) != 0) return S_OK;
        }
        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DATA_S_SAMEFORMATETC;
    }

    /// <summary>エクスプローラーは「Performed DropEffect」などを書き込んでくる。受け取るだけで使わない。</summary>
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        if (release) ReleaseStgMedium(ref medium);
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new NotSupportedException();
        return new FormatEnumerator(Formats().ToArray());
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new NotSupportedException();

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    // ------------------------------------------------------------------

    /// <summary>FILEGROUPDESCRIPTORW: 件数＋FILEDESCRIPTORW（592 バイト）×件数。</summary>
    private byte[] BuildDescriptor()
    {
        const int size = 592;
        var files = Files;
        var buf = new byte[4 + size * files.Count];
        BitConverter.GetBytes(files.Count).CopyTo(buf, 0);
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            int o = 4 + size * i;
            uint flags = FD_ATTRIBUTES | FD_WRITESTIME | FD_PROGRESSUI | FD_UNICODE | (f.IsDirectory ? 0 : FD_FILESIZE);
            BitConverter.GetBytes(flags).CopyTo(buf, o);
            // clsid(16) sizel(8) pointl(8) は 0 のまま
            BitConverter.GetBytes(f.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL).CopyTo(buf, o + 36);
            long ft = f.Modified > DateTime.MinValue ? f.Modified.ToFileTimeUtc() : 0;
            BitConverter.GetBytes(ft).CopyTo(buf, o + 56); // ftLastWriteTime
            BitConverter.GetBytes((uint)(f.Size >> 32)).CopyTo(buf, o + 64);
            BitConverter.GetBytes((uint)(f.Size & 0xFFFFFFFF)).CopyTo(buf, o + 68);
            var name = f.RelativePath.Length > 259 ? f.RelativePath[..259] : f.RelativePath;
            Encoding.Unicode.GetBytes(name).CopyTo(buf, o + 72);
        }
        return buf;
    }

    private static STGMEDIUM HGlobal(byte[] data)
    {
        var h = GlobalAlloc(0 /* GMEM_FIXED: ポインターがそのまま HGLOBAL */, (UIntPtr)Math.Max(1, data.Length));
        if (h == IntPtr.Zero) throw new OutOfMemoryException();
        Marshal.Copy(data, 0, h, data.Length);
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = h };
    }

    private static bool Has(FORMATETC f, TYMED t) => (f.tymed & t) != 0;

    private static FORMATETC Fmt(short cf, TYMED tymed) => new()
    {
        cfFormat = cf,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = tymed,
    };

    private static short Format(string name) => unchecked((short)RegisterClipboardFormat(name));

    /// <summary>テキスト形式で載せたデータ（リモートのパス・元のフェンス）を、受け取った側で読む。</summary>
    public static string? ReadText(System.Windows.IDataObject data, string format)
    {
        if (!data.GetDataPresent(format)) return null;
        var o = data.GetData(format);
        switch (o)
        {
            case string s: return s;
            case string[] a: return string.Join("\n", a);
            case MemoryStream ms: return Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0');
            default: return null;
        }
    }

    private const uint FD_ATTRIBUTES = 0x4, FD_WRITESTIME = 0x20, FD_FILESIZE = 0x40, FD_PROGRESSUI = 0x4000, FD_UNICODE = 0x80000000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int S_OK = 0;
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    // ------------------------------------------------------------------

    private sealed class FormatEnumerator(FORMATETC[] formats) : IEnumFORMATETC
    {
        private int _index;

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            int n = 0;
            while (n < celt && _index < formats.Length) rgelt[n++] = formats[_index++];
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = n;
            return n == celt ? 0 : 1; // S_OK / S_FALSE
        }

        public int Skip(int celt)
        {
            _index += celt;
            return _index <= formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            _index = 0;
            return 0;
        }

        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(formats) { _index = _index };
    }

    /// <summary>ダウンロード済みの一時ファイルを、エクスプローラーに IStream として読ませる。</summary>
    private sealed class ComFileStream : IStream
    {
        private readonly FileStream _fs;
        private readonly string _name;

        public ComFileStream(string path)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _name = Path.GetFileName(path);
        }

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int n = _fs.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, n);
            if (n == 0) _fs.Dispose(); // 読み終わったら閉じる（一時ファイルを掴みっぱなしにしない）
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long pos = _fs.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
        }

        public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG
            {
                cbSize = _fs.CanRead ? _fs.Length : 0,
                type = 2, // STGTY_STREAM
                pwcsName = (grfStatFlag & 1) != 0 ? null! : _name, // STATFLAG_NONAME
            };
        }

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            var buf = new byte[81920];
            long total = 0;
            while (total < cb)
            {
                int n = _fs.Read(buf, 0, (int)Math.Min(buf.Length, cb - total));
                if (n == 0) break;
                pstm.Write(buf.Length == n ? buf : buf[..n], n, IntPtr.Zero);
                total += n;
            }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, total);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, total);
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("読み取り専用", unchecked((int)0x80030005));
        public void SetSize(long libNewSize) => throw new COMException("読み取り専用", unchecked((int)0x80030005));
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("未対応", unchecked((int)0x80030001));
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("未対応", unchecked((int)0x80030001));
        public void Clone(out IStream ppstm) => throw new COMException("未対応", unchecked((int)0x80030001));
    }
}

/// <summary>OLE の DoDragDrop を直接呼ぶ（WPF の DragDrop は独自 IDataObject の仮想ファイルをうまく渡せないため）。</summary>
internal static class OleDrag
{
    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int fEscapePressed, uint grfKeyState);
        [PreserveSig] int GiveFeedback(uint dwEffect);
    }

    [ComVisible(true)]
    private sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(int fEscapePressed, uint grfKeyState)
        {
            if (fEscapePressed != 0) return DRAGDROP_S_CANCEL;
            return (grfKeyState & MK_LBUTTON) == 0 ? DRAGDROP_S_DROP : 0;
        }

        public int GiveFeedback(uint dwEffect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    public const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2;
    private const int DRAGDROP_S_DROP = 0x00040100, DRAGDROP_S_CANCEL = 0x00040101, DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    private const uint MK_LBUTTON = 0x1;

    /// <returns>実際に行われた効果（DROPEFFECT_*）。</returns>
    public static int Run(ComIDataObject data, int allowedEffects)
    {
        DoDragDrop(data, new DropSource(), allowedEffects, out int effect);
        return effect;
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(ComIDataObject pDataObj, IDropSource pDropSource, int dwOKEffects, out int pdwEffect);
}
