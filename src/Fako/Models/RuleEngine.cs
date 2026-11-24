using System.IO;
using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace Fako.Models;

/// <summary>デスクトップの項目を、ルールに従ってショートカット型フェンスへ振り分ける。</summary>
internal static class RuleEngine
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    private static readonly Dictionary<FileCategory, string[]> CategoryExtensions = new()
    {
        [FileCategory.Image] = new[] { "jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "heif", "tif", "tiff", "svg", "ico", "avif" },
        [FileCategory.Document] = new[] { "pdf", "doc", "docx", "xls", "xlsx", "xlsm", "ppt", "pptx", "txt", "csv", "md", "rtf", "odt", "ods", "odp", "one", "accdb", "mdb" },
        [FileCategory.Archive] = new[] { "zip", "7z", "rar", "lzh", "tar", "gz", "tgz", "bz2", "xz", "cab", "iso" },
        [FileCategory.Program] = new[] { "exe", "msi", "msix", "appx", "bat", "cmd", "ps1", "vbs", "jar", "com" },
        [FileCategory.Video] = new[] { "mp4", "mov", "avi", "mkv", "wmv", "webm", "m4v", "mpg", "mpeg" },
        [FileCategory.Audio] = new[] { "mp3", "wav", "m4a", "aac", "flac", "ogg", "wma" },
    };

    public static string CategoryLabel(FileCategory c) => c switch
    {
        FileCategory.Folder => "フォルダー",
        FileCategory.Shortcut => "ショートカット",
        FileCategory.Image => "画像",
        FileCategory.Document => "ドキュメント（PDF・Office・テキスト）",
        FileCategory.Archive => "圧縮ファイル",
        FileCategory.Program => "プログラム・スクリプト",
        FileCategory.Video => "動画",
        FileCategory.Audio => "音声",
        _ => c.ToString(),
    };

    public static string KindLabel(RuleKind k) => k switch
    {
        RuleKind.Extension => "拡張子",
        RuleKind.NamePattern => "名前（ワイルドカード）",
        RuleKind.Category => "種類",
        RuleKind.Regex => "名前（正規表現）",
        _ => k.ToString(),
    };

    /// <summary>条件の書き方が正しいか。正しくなければ理由を返す。</summary>
    public static string? Validate(SortRule rule)
    {
        switch (rule.Kind)
        {
            case RuleKind.Extension or RuleKind.NamePattern when SplitPatterns(rule.Pattern).Length == 0:
                return "条件が空です";
            case RuleKind.Category when !Enum.TryParse<FileCategory>(rule.Pattern, out _):
                return "種類を選んでください";
            case RuleKind.Regex:
                if (string.IsNullOrWhiteSpace(rule.Pattern)) return "条件が空です";
                try { _ = new Regex(rule.Pattern); }
                catch (ArgumentException ex) { return "正規表現が正しくありません: " + ex.Message; }
                break;
        }
        return null;
    }

    public static bool Matches(SortRule rule, string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        bool isFolder = Directory.Exists(path);
        var ext = isFolder ? "" : Path.GetExtension(name).TrimStart('.');

        switch (rule.Kind)
        {
            case RuleKind.Extension:
                return !isFolder && SplitPatterns(rule.Pattern)
                    .Any(p => Cmp.Equals(p.TrimStart('*').TrimStart('.'), ext));

            case RuleKind.NamePattern:
                return SplitPatterns(rule.Pattern)
                    .Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));

            case RuleKind.Category:
                if (!Enum.TryParse<FileCategory>(rule.Pattern, out var cat)) return false;
                return cat switch
                {
                    FileCategory.Folder => isFolder,
                    FileCategory.Shortcut => !isFolder && (Cmp.Equals(ext, "lnk") || Cmp.Equals(ext, "url")),
                    _ => !isFolder && CategoryExtensions.TryGetValue(cat, out var exts) && exts.Contains(ext, Cmp),
                };

            case RuleKind.Regex:
                try { return Regex.IsMatch(name, rule.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
                catch (Exception) { return false; }

            default:
                return false;
        }
    }

    private static string[] SplitPatterns(string pattern) =>
        pattern.Split(new[] { ',', ';', ' ', '、', '　' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// どのフェンスにも入っていないデスクトップ項目にルールを当て、該当フェンスへ追加する。
    /// </summary>
    /// <returns>(振り分けたパス, 振り分け先フェンス Id) の一覧。</returns>
    public static List<(string Path, string FenceId)> Apply(AppConfig config)
    {
        var moved = new List<(string, string)>();
        if (!config.AutoSortEnabled) return moved;

        var rules = config.Rules
            .Where(r => r.Enabled && Validate(r) == null)
            .Select(r => (Rule: r, Fence: config.AllPages().FirstOrDefault(f => f.Id == r.TargetFenceId && f.Mode == FenceMode.Shortcuts)))
            .Where(x => x.Fence != null)
            .ToList();
        if (rules.Count == 0) return moved;

        var fenced = config.AllPages().Where(f => f.Mode == FenceMode.Shortcuts).SelectMany(f => f.Items).ToHashSet(Cmp);
        var excluded = config.RuleExclusions.ToHashSet(Cmp);

        foreach (var path in FolderListing.List(FolderListing.UserDesktop, FolderListing.CommonDesktop))
        {
            if (fenced.Contains(path) || excluded.Contains(path)) continue;
            foreach (var (rule, fence) in rules)
            {
                if (!Matches(rule, path)) continue;
                fence!.Items.Add(path);
                fenced.Add(path);
                moved.Add((path, fence.Id));
                break; // 最初に当てはまったルールだけ
            }
        }

        // 消えたファイルの除外記録は掃除する
        config.RuleExclusions.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        return moved;
    }
}
