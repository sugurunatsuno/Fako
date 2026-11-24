using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Fako.Interop;

internal static class ShellActions
{
    private const int ErrorCancelled = 1223;

    public static void Open(string path)
    {
        try
        {
            if (Models.FolderListing.IsShellItem(path))
            {
                // ごみ箱などは "shell:::{GUID}" でエクスプローラーに開かせる
                Process.Start("explorer.exe", "shell:" + path);
                return;
            }
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            if (File.Exists(path) && Path.GetDirectoryName(path) is { } dir)
                psi.WorkingDirectory = dir;
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // UAC やダイアログでキャンセルされた
        }
        catch (Exception ex)
        {
            MessageBox.Show($"開けませんでした。\n{path}\n\n{ex.Message}", "Fako",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void OpenLocation(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Fako", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>エクスプローラーと同じ確認・進捗・上書き確認のダイアログで、ファイルを移動／コピーする。</summary>
    public static void MoveOrCopyInto(IEnumerable<string> sources, string destDir, bool copy)
    {
        foreach (var src in sources)
        {
            try
            {
                var name = Path.GetFileName(src.TrimEnd('\\'));
                var dest = Path.Combine(destDir, name);
                if (string.Equals(Path.GetDirectoryName(src.TrimEnd('\\')), destDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    continue; // すでにそこにある
                var ui = Microsoft.VisualBasic.FileIO.UIOption.AllDialogs;
                var cancel = Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing;
                if (Directory.Exists(src))
                {
                    if (copy) Microsoft.VisualBasic.FileIO.FileSystem.CopyDirectory(src, dest, ui, cancel);
                    else Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(src, dest, ui, cancel);
                }
                else if (File.Exists(src))
                {
                    if (copy) Microsoft.VisualBasic.FileIO.FileSystem.CopyFile(src, dest, ui, cancel);
                    else Microsoft.VisualBasic.FileIO.FileSystem.MoveFile(src, dest, ui, cancel);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{(copy ? "コピー" : "移動")}できませんでした。\n{src}\n\n{ex.Message}", "Fako",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    public static void ShowProperties(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            lpVerb = "properties",
            lpFile = path,
            nShow = 1,
        };
        ShellExecuteEx(ref info);
    }

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
}
