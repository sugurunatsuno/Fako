using Microsoft.Win32;

namespace Fako.Interop;

/// <summary>HKCU\...\Run による「Windows の起動時に開始」。</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Fako";
    private const string LegacyValueName = "Kakoi";

    /// <summary>旧名の自動起動を Fako に引き継ぐ（設定を保ったままレジストリの値を移す）。</summary>
    public static void Migrate()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        bool hasNew = key.GetValue(ValueName) is string;
        bool hasOld = key.GetValue(LegacyValueName) is string;
        if (hasOld && !hasNew && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\"");
        if (hasOld)
        {
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            Log.Info("autostart migrated from legacy value");
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>登録済みなのに exe の場所が変わっていたら、今の exe に書き換える。</summary>
    public static void RefreshPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string current || Environment.ProcessPath is not { } exe) return;
        var expected = $"\"{exe}\"";
        if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(ValueName, expected);
            Log.Info($"autostart path updated: {current} -> {expected}");
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
