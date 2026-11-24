using System.IO;

namespace Fako.Remote;

/// <summary>~/.ssh/config の Host エントリ（接続設定の入力補助に使う）。</summary>
public sealed record SshHostEntry(string Alias, string HostName, string? User, int Port, string? IdentityFile)
{
    public override string ToString() =>
        Alias == HostName ? Alias : $"{Alias}（{(User != null ? User + "@" : "")}{HostName}{(Port == 22 ? "" : ":" + Port)}）";
}

internal static class SshConfigReader
{
    public static string UserSshDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>
    /// ~/.ssh/config の Host ブロックを読む（ワイルドカードの Host は除外。Include や Match は扱わない簡易版）。
    /// 「Host *」の設定は既定値として各エントリに反映する。
    /// </summary>
    public static List<SshHostEntry> Read()
    {
        var path = Path.Combine(UserSshDir, "config");
        var result = new List<SshHostEntry>();
        if (!File.Exists(path)) return result;

        var blocks = new List<(List<string> Patterns, Dictionary<string, string> Options)>();
        (List<string> Patterns, Dictionary<string, string> Options)? current = null;
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var (key, value) = SplitOption(line);
                if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    current = (value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    blocks.Add(current.Value);
                }
                else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
                {
                    current = null; // Match ブロックは扱わない
                }
                else if (current is { } c && !c.Options.ContainsKey(key))
                {
                    c.Options[key] = value; // ssh と同じく最初の値が優先
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("~/.ssh/config を読めません", ex);
            return result;
        }

        var defaults = blocks.Where(b => b.Patterns.Contains("*")).SelectMany(b => b.Options)
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

        foreach (var (patterns, options) in blocks)
        {
            foreach (var alias in patterns.Where(p => !p.Contains('*') && !p.Contains('?') && !p.StartsWith('!')))
            {
                string? Get(string k) => options.TryGetValue(k, out var v) ? v : defaults.TryGetValue(k, out var d) ? d : null;
                var host = Get("HostName") ?? alias;
                int port = int.TryParse(Get("Port"), out var p) ? p : 22;
                var identity = Get("IdentityFile");
                if (identity != null) identity = ExpandPath(identity, alias, host);
                result.Add(new SshHostEntry(alias, host.Replace("%h", alias), Get("User"), port, identity));
            }
        }
        return result;
    }

    /// <summary>よく使われる既定の鍵ファイル（~/.ssh/id_ed25519 など）のうち、存在するもの。</summary>
    public static string? DefaultKeyFile()
    {
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
        {
            var p = Path.Combine(UserSshDir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static (string Key, string Value) SplitOption(string line)
    {
        int i = line.IndexOfAny(new[] { ' ', '\t', '=' });
        if (i < 0) return (line, "");
        var key = line[..i];
        var value = line[(i + 1)..].TrimStart(' ', '\t', '=').Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        return (key, value);
    }

    private static string ExpandPath(string p, string alias, string host)
    {
        p = p.Replace("%h", host).Replace("%n", alias).Replace("%%", "%");
        if (p.StartsWith("~/") || p.StartsWith("~\\"))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]);
        return Environment.ExpandEnvironmentVariables(p.Replace('/', Path.DirectorySeparatorChar));
    }
}
