using System.Runtime.InteropServices;
using System.Text;

namespace Fako.Interop;

/// <summary>
/// Windows の DPAPI（CryptProtectData）で文字列を暗号化する。
/// 同じ Windows ユーザーでなければ復号できないので、設定ファイルにパスワードを平文で残さずに済む。
/// </summary>
internal static class Dpapi
{
    // 旧名のまま（この文字列を変えると、保存済みの SSH パスワードを復号できなくなる）
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kakoi.Remote.v1");

    public static string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(Transform(data, protect: true));
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedBase64), protect: false));
        }
        catch (Exception ex)
        {
            Log.Error("保存した資格情報を復号できません", ex);
            return null;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = ToBlob(input);
        var entropy = ToBlob(Entropy);
        try
        {
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Fako", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entropy.pbData);
        }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var p = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        Marshal.Copy(data, 0, p, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = p };
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
