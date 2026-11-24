using System.Runtime.InteropServices;

namespace Fako.Interop;

/// <summary>
/// 自プロセスの整合性レベル。
/// exe ファイルに「低」の必須ラベルが付いていると（ラベル付きフォルダーに置かれた場合など）、
/// プロセスは低整合性で起動する。その場合
///   - エクスプローラーへの Shell_NotifyIcon が ERROR_ACCESS_DENIED（トレイに出ない）
///   - %APPDATA% に書けない（設定が保存されない）
///   - 他プロセスのウィンドウとの所有関係を張れない
/// といった不具合になるので、起動時に検出して知らせる。
/// </summary>
internal static class ProcessIntegrity
{
    public const int Low = 0x1000;
    public const int Medium = 0x2000;

    public static int Current()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token)) return -1;
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
                var buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out _)) return -1;
                    var sid = Marshal.ReadIntPtr(buf);
                    int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(token); }
        }
        catch
        {
            return -1;
        }
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int returned);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
}
