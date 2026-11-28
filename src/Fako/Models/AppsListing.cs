using System.Text.RegularExpressions;
using Fako.Interop;

namespace Fako.Models;

/// <summary>shell:AppsFolder 内の 1 アプリ項目。</summary>
internal sealed record AppEntry(string AppId, string DisplayName, string ParsingPath);

/// <summary>インストール済みアプリの列挙・ノイズ除去・整列。</summary>
internal static class AppsListing
{
    private static readonly string[] NoiseExtensions =
    {
        ".pdf", ".html", ".htm", ".chm", ".txt", ".rtf", ".md", ".url",
    };

    private static readonly Regex UninstallerFileNameRegex = new(
        @"^(?:unins\d*|uninst.*|.*[-_.]uninst(?:all(?:er)?)?)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NoiseDisplayNameWordRegex = new(
        @"(?:^license$|\b(?:uninstall(?:er)?|readme|read\s+me|manuals?|documentation|module\s+docs|release\s+notes|change\s*log|license\s+(?:agreement|terms|info)|eula|localization\s+reference|skin\s+format\s+reference|web\s*site|check\s+for\s+updates)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] NoiseJapaneseKeywords =
    {
        "アンインストール",
        "アンインストーラ",
        "の削除",
        "を削除",
        "削除ツール",
        "マニュアル",
        "ヘルプを表示",
        "お読みください",
        "更新の確認",
        "バージョン情報",
    };

    /// <summary>アンインストーラー・マニュアル・Web リンク等の補助項目であれば true。</summary>
    public static bool IsNoiseApp(string displayName, string appId)
    {
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(appId))
            return true;

        var trimmedName = displayName.Trim();
        var trimmedId = appId.Trim();

        // 1. Web サイト／サポートリンク
        if (trimmedId.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmedId.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. ドキュメント／ヘルプ拡張子
        foreach (var ext in NoiseExtensions)
        {
            if (trimmedId.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 3. ファイル名部分がアンインストーラー系
        int lastSep = trimmedId.LastIndexOfAny(['\\', '/']);
        var fileName = lastSep >= 0 ? trimmedId[(lastSep + 1)..] : trimmedId;
        if (UninstallerFileNameRegex.IsMatch(fileName))
            return true;

        // 4 & 5. 表示名がアンインストール／マニュアル／ヘルプ／ドキュメント系
        if (NoiseDisplayNameWordRegex.IsMatch(trimmedName))
            return true;

        foreach (var keyword in NoiseJapaneseKeywords)
        {
            if (trimmedName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (trimmedName.EndsWith("について", StringComparison.Ordinal))
            return true;

        return false;
    }

    public static IReadOnlyList<AppEntry> Filter(IEnumerable<AppEntry> entries, bool hideNoise)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AppEntry>();

        foreach (var entry in entries)
        {
            if (entry is null) continue;
            if (string.IsNullOrWhiteSpace(entry.AppId) || string.IsNullOrWhiteSpace(entry.DisplayName))
                continue;
            if (!seen.Add(entry.AppId))
                continue;
            if (hideNoise && IsNoiseApp(entry.DisplayName, entry.AppId))
                continue;
            list.Add(entry);
        }

        list.Sort((a, b) => FolderListing.LogicalComparer.Instance.Compare(a.DisplayName, b.DisplayName));
        return list;
    }

    public static IReadOnlyList<AppEntry> ListInstalled(bool hideNoise) =>
        Filter(ShellApps.Enumerate(), hideNoise);
}
