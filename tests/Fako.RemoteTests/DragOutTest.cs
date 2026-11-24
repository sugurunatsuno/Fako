using System.IO;
using System.Runtime.InteropServices;
using Fako.Remote;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

/// <summary>
/// SSH フェンスからエクスプローラーへのドラッグ（仮想ファイル）を、マウスを使わずに確かめる。
///
/// 実際の使い方と同じく 2 つのプロセスに分ける:
///   A（serve）: ドラッグのデータ（RemoteDataObject）を持ち、メッセージを処理し続ける（＝ドラッグ中の Fako）
///   B（drop） : フォルダーの「本物の」ドロップ先（シェルの IDropTarget＝エクスプローラーと同じもの）を取り出し、
///               A のデータを DragEnter → Drop する（＝エクスプローラー）
/// COM オブジェクトは OBJREF モニカー（文字列）でプロセス間に渡す（クリップボードは使わない）。
/// </summary>
internal static class DragOutTest
{
    /// <summary>A: データを用意し、B が終わるまでメッセージを処理する。</summary>
    public static void Serve(RemoteSession session, string home, string objrefFile, string doneFile, string logFile)
    {
        void Trace(string s) => File.AppendAllText(logFile, s + Environment.NewLine);
        // テストの exe は低整合性ラベル付きの作業フォルダーから動くことがあり、%TEMP% に書けないので作業フォルダーの中へ
        var temp = Path.Combine(Path.GetDirectoryName(objrefFile)!, "dl");
        var top = session.ListAsync(home).GetAwaiter().GetResult()
            .Where(e => e.Name is "README.md" or "projects" or "data.bin").ToList();

        List<RemoteDataObject.VirtualFile> Expand()
        {
            Trace($"expand thread={Environment.CurrentManagedThreadId}");
            var result = new List<RemoteDataObject.VirtualFile>();
            void Add(RemoteEntry e, string rel)
            {
                if (e.IsDirectory)
                {
                    result.Add(new(rel, e, true, 0, e.LastWriteTime));
                    foreach (var c in session.ListAsync(e.FullName).GetAwaiter().GetResult()) Add(c, rel + "\\" + c.Name);
                }
                else result.Add(new(rel, e, false, e.Length, e.LastWriteTime));
            }
            foreach (var e in top) Add(e, e.Name);
            return result;
        }

        string Download(RemoteEntry e)
        {
            Trace($"download {e.Name} thread={Environment.CurrentManagedThreadId}");
            var local = Path.Combine(temp, Guid.NewGuid().ToString("N"), e.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            session.DownloadToAsync(e, local).GetAwaiter().GetResult();
            return local;
        }

        var data = new RemoteDataObject(top.Select(e => e.FullName).ToArray(), "fence-x", Expand, Download);
        Marshal.ThrowExceptionForHR(CreateObjrefMoniker(data, out var moniker));
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var ctx));
        moniker.GetDisplayName(ctx, null, out var name);
        File.WriteAllText(objrefFile, name);
        Trace("served");

        // Fako の UI スレッドがドラッグ中にしているのと同じく、メッセージを処理し続ける
        var until = DateTime.Now.AddSeconds(600);
        while (!File.Exists(doneFile) && DateTime.Now < until)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(20);
        }
        GC.KeepAlive(data);
    }

    /// <summary>B: A のデータを、フォルダーのドロップ先にドロップする。</summary>
    public static int Drop(string objrefFile, string targetDir, string logFile)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}"); if (!ok) failures++; }
        string Log() => File.Exists(logFile) ? File.ReadAllText(logFile) : "";

        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        Directory.CreateDirectory(targetDir);

        var data = (ComIDataObject)Marshal.BindToMoniker(File.ReadAllText(objrefFile));
        var target = GetDropTarget(targetDir);
        int effect = 1 | 2; // COPY | MOVE
        var pt = new POINTL();
        int hr = target.DragEnter(data, MK_LBUTTON, pt, ref effect);
        Check(hr == 0 && effect != 0, $"エクスプローラーのドロップ先が受け付ける (effect={effect})");
        Check(!Log().Contains("download"), "ドラッグ中（ドロップ前）はダウンロードしない");
        effect = 1 | 2;
        target.DragOver(MK_LBUTTON, pt, ref effect);
        hr = target.Drop(data, 0, pt, ref effect);
        Check(hr == 0, $"ドロップ (hr=0x{hr:X8}, effect={effect})");

        // シェルのコピーは別スレッドで進むことがあるので待つ
        var readme = Path.Combine(targetDir, "README.md");
        var nested = Path.Combine(targetDir, "projects", "fako", "run.sh");
        var bin = Path.Combine(targetDir, "data.bin");
        for (int i = 0; i < 300 && !(File.Exists(readme) && File.Exists(nested) && File.Exists(bin) && new FileInfo(bin).Length == 300000); i++)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(100);
        }
        Check(File.Exists(readme) && File.ReadAllText(readme).Contains("SSH フェンス"), "ファイルが書き出され、内容が一致（README.md）");
        Check(File.Exists(nested), "フォルダーは中身ごと（projects\\fako\\run.sh）");
        Check(File.Exists(bin) && new FileInfo(bin).Length == 300000, "大きめのファイルもサイズが一致（data.bin 300KB）");
        int downloads = Log().Split('\n').Count(l => l.StartsWith("download"));
        Check(downloads == 3, $"ダウンロードはドロップ時にファイル数分だけ（{downloads} 回）");
        return failures;
    }

    // ---- COM ----

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter(ComIDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig] int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(ComIDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL { public int x, y; }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attrs);
        void EnumObjects(IntPtr hwnd, uint flags, out IntPtr e);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr l, IntPtr p1, IntPtr p2);
        void CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgf);
        void GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr r, out IntPtr ppv);
    }

    private static IDropTarget GetDropTarget(string folder)
    {
        Marshal.ThrowExceptionForHR(SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _));
        var iidFolder = typeof(IShellFolder).GUID;
        Marshal.ThrowExceptionForHR(SHBindToParent(pidl, ref iidFolder, out var parentPtr, out var child));
        var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
        var iid = typeof(IDropTarget).GUID;
        parent.GetUIObjectOf(IntPtr.Zero, 1, new[] { child }, ref iid, IntPtr.Zero, out var dt);
        return (IDropTarget)Marshal.GetObjectForIUnknown(dt);
    }

    private const int MK_LBUTTON = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("ole32.dll")]
    private static extern int CreateObjrefMoniker([MarshalAs(UnmanagedType.IUnknown)] object punk, out System.Runtime.InteropServices.ComTypes.IMoniker ppmk);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx ppbc);
}


