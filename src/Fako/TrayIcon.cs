using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Fako.Interop;
using Fako.Models;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Fako;

/// <summary>
/// タスクトレイ常駐アイコンとメニュー。
/// WinForms の NotifyIcon は登録失敗を黙って握りつぶすため、Shell_NotifyIcon を直接呼んで結果をログに残す。
/// エクスプローラーの再起動（TaskbarCreated）時には登録し直す。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAY = WM_APP + 1;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_NULL = 0x0000;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const uint IconId = 1;

    private readonly App _app;
    private readonly HwndSource _window;
    private readonly uint _taskbarCreatedMsg;
    private readonly IntPtr _hIcon;
    private readonly Drawing.Icon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly DispatcherTimer _retryTimer;
    private bool _added;
    private int _retries;

    public TrayIcon(App app)
    {
        _app = app;
        _menu = BuildMenu();
        _icon = LoadIcon();
        _hIcon = _icon.Handle;
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        // メッセージ受信用の不可視ウィンドウ（メッセージ専用ウィンドウだと SetForegroundWindow できないので通常の popup）
        var p = new HwndSourceParameters("FakoTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x00000080,          // WS_EX_TOOLWINDOW
        };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);

        // ログオン直後などでタスクバーがまだ無いと失敗するので、しばらく再試行する
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _retryTimer.Tick += (_, _) =>
        {
            if (_added || ++_retries > 30) { _retryTimer.Stop(); return; }
            Add();
        };

        Add();
        if (!_added) _retryTimer.Start();
    }

    private void Add()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = _hIcon;
        data.szTip = "Fako — デスクトップ整理";

        // 前回のプロセスが異常終了して残っていることがあるので、念のため消してから追加する
        var del = NewData(0);
        Shell_NotifyIcon(NIM_DELETE, ref del);

        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        int err = Marshal.GetLastWin32Error();
        if (_added)
        {
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
            Log.Info("tray icon added");
        }
        else
        {
            Log.Info($"tray icon NIM_ADD failed (error={err}, retry={_retries})");
        }
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..252] + "…" : text;
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            // NOTIFYICON_VERSION_4: LOWORD(lParam) がイベント
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            switch (ev)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                case WM_LBUTTONUP:
                    _app.ToggleAllHidden();
                    break;
                case WM_CONTEXTMENU:
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            Log.Info("TaskbarCreated: re-adding tray icon");
            _added = false;
            Add();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // 前面にしておかないと、メニュー外をクリックしても閉じない
        NativeMethods.SetForegroundWindow(_window.Handle);
        _menu.Show(WinForms.Cursor.Position);
        NativeMethods.PostMessage(_window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var app = _app;
        var menu = new WinForms.ContextMenuStrip();
        var settings = new WinForms.ToolStripMenuItem("設定…", null, (_, _) => app.OpenSettings());
        settings.Font = new System.Drawing.Font(settings.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(settings);
        menu.Items.Add("アップデートを確認…", null, (_, _) => app.CheckForUpdatesNow());
        menu.Items.Add("自動振り分けのルール…", null, (_, _) => app.OpenSettings(2));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("新しいフェンス", null, (_, _) => app.NewFence());
        menu.Items.Add("フォルダーポータルを追加…", null, (_, _) => app.NewPortalFence());
        menu.Items.Add("未整理フェンスを追加", null, (_, _) => app.NewFence(FenceMode.Unsorted));
        menu.Items.Add("最近のファイル（仮想）を追加", null, (_, _) => app.NewFence(FenceMode.Recent));
        menu.Items.Add("SSH フェンスを追加…", null, (_, _) => app.NewRemoteFence());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var toggle = new WinForms.ToolStripMenuItem("フェンスを隠す", null, (_, _) => app.ToggleAllHidden());
        var peek = new WinForms.ToolStripMenuItem("Peek（フェンスを前面に）", null, (_, _) => app.TogglePeek());
        var palette = new WinForms.ToolStripMenuItem("検索…（Ctrl+Alt+F）", null, (_, _) => app.TogglePalette());
        var undo = new WinForms.ToolStripMenuItem("元に戻す（Ctrl+Z）", null, (_, _) => app.UndoLast());
        var dblClick = new WinForms.ToolStripMenuItem("デスクトップのダブルクリックで隠す／表示", null,
            (_, _) => app.SetDoubleClickToHide(!app.Config.DoubleClickToHide));
        var privacy = new WinForms.ToolStripMenuItem("プライバシーモード", null,
            (_, _) => app.SetPrivacyMode(!app.Config.PrivacyMode));
        var managed = new WinForms.ToolStripMenuItem("デスクトップのアイコンをフェンスで管理する", null,
            (_, _) => app.SetIconMode(app.IsFenceManaged ? DesktopIconMode.NotManaged : DesktopIconMode.FenceManaged));
        var startup = new WinForms.ToolStripMenuItem("Windows の起動時に開始", null,
            (_, _) => AutoStart.Set(!AutoStart.IsEnabled()));
        menu.Items.AddRange(new WinForms.ToolStripItem[] { peek, palette, undo, toggle, dblClick, privacy, managed, startup });

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("設定フォルダーを開く", null, (_, _) =>
        {
            System.IO.Directory.CreateDirectory(ConfigStore.Directory);
            Process.Start("explorer.exe", $"\"{ConfigStore.Directory}\"");
        });
        menu.Items.Add("終了", null, (_, _) => app.Shutdown());

        menu.Opening += (_, _) =>
        {
            toggle.Text = app.AllHidden ? "フェンスを表示" : "フェンスを隠す";
            peek.Text = app.IsPeeking ? "Peek を終了" : "Peek（フェンスを前面に）";
            undo.Enabled = app.CanUndo;
            dblClick.Checked = app.Config.DoubleClickToHide;
            privacy.Checked = app.Config.PrivacyMode;
            managed.Checked = app.IsFenceManaged;
            startup.Checked = AutoStart.IsEnabled();
        };
        return menu;
    }

    /// <summary>埋め込んだ fako.ico から、トレイに合うサイズのアイコンを作る。</summary>
    private static Drawing.Icon LoadIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Fako.fako.ico")
            ?? throw new InvalidOperationException("アイコン リソース (Fako.fako.ico) が見つかりません。");
        return new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _retryTimer.Stop();
        var data = NewData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _menu.Dispose();
        _window.Dispose();
        _icon.Dispose();
    }

    // ---- Shell_NotifyIcon ----

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_INFO = 0x01;
    private const uint NOTIFYICON_VERSION_4 = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion; // uTimeout と共用
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);
}
