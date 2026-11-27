using System.Buffers;
using System.IO;

namespace Fako.Remote;

/// <summary>~/.ssh/config の Host エントリ（接続設定の入力補助に使う）。</summary>
public sealed record SshHostEntry(
    string Alias,
    string HostName,
    string? User,
    int Port,
    string? IdentityFile,
    string? IdentityAgent = null,
    bool IdentitiesOnly = false)
{
    public override string ToString() =>
        Alias == HostName ? Alias : $"{Alias}（{(User != null ? User + "@" : "")}{HostName}{(Port == 22 ? "" : ":" + Port)}）";
}

internal static class SshConfigReader
{
    private static readonly SearchValues<char> OptionDelimiters = SearchValues.Create(" \t=");
    private static readonly string[] DefaultKeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    public static string UserSshDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>
    /// ~/.ssh/config の Host ブロックを読む（ワイルドカードの Host は除外。Include や Match は扱わない簡易版）。
    /// 「Host *」の設定は既定値として各エントリに反映する。
    /// </summary>
    public static List<SshHostEntry> Read()
    {
        var path = Path.Combine(UserSshDir, "config");
        if (!File.Exists(path)) return new List<SshHostEntry>();

        try
        {
            return ReadFromLines(File.ReadAllLines(path));
        }
        catch (Exception ex)
        {
            Log.Error("~/.ssh/config を読めません", ex);
            return new List<SshHostEntry>();
        }
    }

    /// <summary>ssh config の行を解析する（テストや将来の Include 対応で使う）。</summary>
    internal static List<SshHostEntry> ReadFromLines(IEnumerable<string> lines)
    {
        var result = new List<SshHostEntry>();
        var blocks = new List<(string[] Patterns, Dictionary<string, string> Options)>();
        (string[] Patterns, Dictionary<string, string> Options)? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var (key, value) = SplitOption(line);
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                current = (value.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                blocks.Add(current.Value);
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                current = null; // Match ブロックは扱わない
            }
            else if (current is { } c)
            {
                c.Options.TryAdd(key, value); // ssh と同じく最初の値が優先
            }
        }

        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (patterns, options) in blocks)
        {
            if (Array.IndexOf(patterns, "*") < 0) continue;
            foreach (var kv in options)
                defaults.TryAdd(kv.Key, kv.Value);
        }

        string? userProfile = null;
        foreach (var (patterns, options) in blocks)
        {
            foreach (var alias in patterns)
            {
                if (alias.Contains('*') || alias.Contains('?') || alias.StartsWith('!')) continue;
                string? Get(string k) => options.TryGetValue(k, out var v) ? v : defaults.TryGetValue(k, out var d) ? d : null;
                var host = Get("HostName") ?? alias;
                int port = int.TryParse(Get("Port"), out var p) ? p : 22;
                var identity = Get("IdentityFile");
                if (identity != null)
                {
                    userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    identity = ExpandPath(identity, alias, host, userProfile);
                }
                var identityAgent = Get("IdentityAgent");
                if (identityAgent != null)
                {
                    identityAgent = NormalizeIdentityAgent(identityAgent, alias, host, ref userProfile);
                }
                var identitiesOnly = Get("IdentitiesOnly")?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
                var resolvedHost = host.Contains("%h", StringComparison.Ordinal) ? host.Replace("%h", alias) : host;
                result.Add(new SshHostEntry(alias, resolvedHost, Get("User"), port, identity,
                    identityAgent, identitiesOnly));
            }
        }
        return result;
    }

    /// <summary>よく使われる既定の鍵ファイル（~/.ssh/id_ed25519 など）のうち、存在するもの。</summary>
    public static string? DefaultKeyFile()
    {
        var sshDir = UserSshDir;
        foreach (var name in DefaultKeyNames)
        {
            var p = Path.Combine(sshDir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static (string Key, string Value) SplitOption(string line)
    {
        int i = line.AsSpan().IndexOfAny(OptionDelimiters);
        if (i < 0) return (line, "");
        var key = line[..i];
        var value = line.AsSpan(i + 1).TrimStart(" \t=").Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        return (key, value.ToString());
    }

    /// <summary>
    /// IdentityAgent はパイプ名や予約語をそのまま保つ（~ 展開や / → \ の置換をしない）。
    /// </summary>
    private static string NormalizeIdentityAgent(string value, string alias, string host, ref string? userProfile)
    {
        var p = value.Trim();
        if (p.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase))
            return p;
        if (p.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("//./pipe/", StringComparison.OrdinalIgnoreCase))
            return p;
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ExpandPath(p, alias, host, userProfile);
    }

    private static string ExpandPath(string p, string alias, string host, string userProfile)
    {
        if (p.Contains('%'))
            p = p.Replace("%h", host).Replace("%n", alias).Replace("%%", "%");
        if (p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
            p = Path.Combine(userProfile, p[2..]);
        if (p.Contains('/'))
            p = p.Replace('/', Path.DirectorySeparatorChar);
        return p.Contains('%') ? Environment.ExpandEnvironmentVariables(p) : p;
    }
}
