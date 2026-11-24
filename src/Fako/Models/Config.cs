using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Fako.Models;

public enum FenceMode
{
    /// <summary>ドロップしたファイル／フォルダーへの参照を並べる（実体は移動しない）。</summary>
    Shortcuts,
    /// <summary>指定フォルダーの中身をそのまま表示する。</summary>
    Portal,
    /// <summary>デスクトップ上で、どのフェンスにも入っていないもの。</summary>
    Unsorted,
    /// <summary>SSH（SFTP）でつないだ先のフォルダー。</summary>
    Remote,
    /// <summary>最近更新されたファイル（仮想。実体を登録しない）。</summary>
    Recent,
}

public enum RemoteAuth
{
    /// <summary>秘密鍵ファイル（パスフレーズは任意）。</summary>
    KeyFile,
    Password,
}

/// <summary>SSH フェンスの接続先。</summary>
public sealed class RemoteConfig
{
    /// <summary>~/.ssh/config から選んだときの Host 名（表示用）。</summary>
    public string? SshConfigAlias { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public RemoteAuth Auth { get; set; } = RemoteAuth.KeyFile;
    public string? KeyFile { get; set; }
    /// <summary>パスワード／パスフレーズを DPAPI（このユーザーだけが復号できる）で暗号化したもの。保存しないなら null。</summary>
    public string? ProtectedSecret { get; set; }
    /// <summary>今表示しているフォルダー（空ならホームディレクトリ）。</summary>
    public string Path { get; set; } = "";
    /// <summary>「.」で始まるファイルも表示する。</summary>
    public bool ShowHidden { get; set; }

    [JsonIgnore]
    public string Display => $"{(string.IsNullOrEmpty(User) ? "" : User + "@")}{Host}{(Port == 22 ? "" : ":" + Port)}";
    [JsonIgnore]
    public string HostKeyId => $"{Host}:{Port}";

    public RemoteConfig Clone() => (RemoteConfig)MemberwiseClone();
}

/// <summary>エクスプローラーの「表示」と同じ並び。Ctrl+ホイールでこの順に切り替わる。</summary>
public enum FenceView
{
    ExtraLarge,
    Large,
    Medium,
    Small,
    List,
    Details,
    Tiles,
}

public enum SortKey
{
    /// <summary>ドラッグで並べた順（ショートカット型のみ）。</summary>
    Manual,
    Name,
    Modified,
    Type,
    Size,
}

public sealed class FenceConfig
{
    public FenceView View { get; set; } = FenceView.Medium;
    public SortKey SortKey { get; set; } = SortKey.Manual;
    public bool SortDescending { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新しいフェンス";
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 340;
    /// <summary>展開時の高さ（折りたたみ中も保持）。</summary>
    public double Height { get; set; } = 220;
    public bool RolledUp { get; set; }

    /// <summary>v0.2 までの背景色。読み込み時に <see cref="Style"/> へ移して消す。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    public FenceStyle Style { get; set; } = new();
    public FenceMode Mode { get; set; } = FenceMode.Shortcuts;
    public string? PortalPath { get; set; }
    /// <summary>SSH フェンスの接続先（Mode = Remote のとき）。</summary>
    public RemoteConfig? Remote { get; set; }

    /// <summary>「最近のファイル」フェンスで何時間前までを出すか。</summary>
    public int RecentHours { get; set; } = 24;

    /// <summary>
    /// タブ: このフェンス自身が 1 枚目のページで、ここに 2 枚目以降のページを持つ。
    /// ページは自分の中身（種類・アイテム・表示方法・並べ替え・タイトル）だけを使い、
    /// 位置・大きさ・外観・折りたたみはこのフェンス（1 枚目）のものを使う。
    /// </summary>
    public List<FenceConfig> Tabs { get; set; } = new();

    /// <summary>表示中のページ（0 = このフェンス自身）。</summary>
    public int ActiveTab { get; set; }

    /// <summary>このフェンスのページ（自分＋タブ）。</summary>
    [JsonIgnore]
    public IEnumerable<FenceConfig> Pages => new[] { this }.Concat(Tabs);
    public List<string> Items { get; set; } = new();
}

/// <summary>フェンスの外観。</summary>
public sealed class FenceStyle
{
    public string Background { get; set; } = "#A0202428";
    /// <summary>null = 自動（背景に白を薄く重ねる）。</summary>
    public string? TitleBackground { get; set; }
    public string TitleForeground { get; set; } = "#FFFFFFFF";
    public double TitleFontSize { get; set; } = 13;
    public bool TitleBold { get; set; } = true;
    public TitleAlign TitleAlignment { get; set; } = TitleAlign.Center;
    public string BorderColor { get; set; } = "#40FFFFFF";
    public double BorderThickness { get; set; } = 1;
    public double CornerRadius { get; set; } = 8;
    public string TextColor { get; set; } = "#FFFFFFFF";
    public double ItemFontSize { get; set; } = 12;
    public bool TextShadow { get; set; } = true;

    public FenceStyle Clone() => (FenceStyle)MemberwiseClone();
}

public enum TitleAlign
{
    Left,
    Center,
    Right,
}

// ------------------------------------------------------------------
// 自動振り分け
// ------------------------------------------------------------------

public enum RuleKind
{
    /// <summary>拡張子（「pdf, docx」のように区切って複数指定）。</summary>
    Extension,
    /// <summary>名前のワイルドカード（「*見積*; 請求書*」）。</summary>
    NamePattern,
    /// <summary>種類（フォルダー／ショートカット／画像…）。</summary>
    Category,
    /// <summary>名前の正規表現。</summary>
    Regex,
}

public enum FileCategory
{
    Folder,
    Shortcut,
    Image,
    Document,
    Archive,
    Program,
    Video,
    Audio,
}

public sealed class SortRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "新しいルール";
    public RuleKind Kind { get; set; } = RuleKind.Extension;
    /// <summary>Kind に応じた条件（Category のときは FileCategory の名前）。</summary>
    public string Pattern { get; set; } = "";
    public string TargetFenceId { get; set; } = "";

    public SortRule Clone() => (SortRule)MemberwiseClone();
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public bool DoubleClickToHide { get; set; } = true;
    /// <summary>アイコンをシングルクリックで開く（ドラッグはそのまま使える）。</summary>
    public bool SingleClickOpen { get; set; }
    /// <summary>画面共有向け。マウスを乗せるまで中身と名前を隠す。</summary>
    public bool PrivacyMode { get; set; }
    /// <summary>起動時に新しいバージョンを確認する。</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>「後で」を選んだバージョン（そのバージョンの自動通知を抑止）。</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>デスクトップのアイコンをフェンスで管理するか。null = 旧設定から移行する。</summary>
    public DesktopIconMode? IconMode { get; set; }

    /// <summary>v0.3 までの設定（ファイル属性で個別に隠す）。読み込み時に IconMode へ移して消す。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HideFencedDesktopIcons { get; set; }

    /// <summary>
    /// v0.3 までに Fako が付けた属性の記録。属性方式はやめたので、起動時にこの記録に従って元に戻す。
    /// JSON のキーは旧名のまま（既存の config.json をそのまま読めるように）。
    /// </summary>
    [JsonPropertyName("HiddenByKakoi")]
    public List<HiddenRecord> HiddenByFako { get; set; } = new();

    // スナップ
    public bool SnapEnabled { get; set; } = true;
    public bool SnapToScreenEdges { get; set; } = true;
    /// <summary>吸着する距離（論理ピクセル）。</summary>
    public double SnapDistance { get; set; } = 12;
    /// <summary>フェンス同士を並べたときの間隔（論理ピクセル）。</summary>
    public double SnapGap { get; set; } = 8;

    // 自動振り分け
    public bool AutoSortEnabled { get; set; } = true;
    public List<SortRule> Rules { get; set; } = new();
    /// <summary>手でフェンスから外した項目。ルールで再び取り込まない。</summary>
    public List<string> RuleExclusions { get; set; } = new();

    /// <summary>新しいフェンスの外観。</summary>
    public FenceStyle DefaultStyle { get; set; } = new();

    /// <summary>SSH の信頼済みホスト鍵（"host:port" → "ssh-ed25519 SHA256:..."）。初回接続時に確認して登録する。</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new();

    public List<FenceConfig> Fences { get; set; } = new();

    /// <summary>すべてのフェンスのすべてのページ（タブを含む）。</summary>
    public IEnumerable<FenceConfig> AllPages() => Fences.SelectMany(f => f.Pages);

    public FenceConfig? FindPage(string id) => AllPages().FirstOrDefault(p => p.Id == id);

    /// <summary>古い形式の設定を今の形式に直す。</summary>
    public void Migrate()
    {
        foreach (var f in Fences)
        {
            f.Tabs ??= new();
            foreach (var t in f.Tabs) t.Tabs = new(); // タブの入れ子は作らない
            f.ActiveTab = Math.Clamp(f.ActiveTab, 0, f.Tabs.Count);
        }
        foreach (var f in AllPages())
        {
            f.Style ??= new FenceStyle();
            if (!string.IsNullOrEmpty(f.Color))
            {
                f.Style.Background = f.Color;
                f.Color = null;
            }
        }
        DefaultStyle ??= new FenceStyle();
        Rules ??= new();
        RuleExclusions ??= new();
        HiddenByFako ??= new();
        KnownHosts ??= new();

        // v0.3 の「属性で隠す」→ v0.4 の「フェンスで管理する」
        IconMode ??= HideFencedDesktopIcons == false ? DesktopIconMode.NotManaged : DesktopIconMode.FenceManaged;
        HideFencedDesktopIcons = null;
    }
}

public enum DesktopIconMode
{
    /// <summary>
    /// Windows 標準のデスクトップアイコンを非表示にし、すべてフェンスで表示する
    /// （どのフェンスにも入っていない項目は「未整理」フェンス）。ファイルには触らない。
    /// </summary>
    FenceManaged,
    /// <summary>Windows 標準のアイコンもそのまま（フェンスに入れた項目は両方に表示される）。</summary>
    NotManaged,
}

/// <summary>設定の書き出し／読み込みに使うファイル（フェンスの配置と外観だけを運ぶ）。</summary>
public sealed class LayoutDocument
{
    public const string FormatName = "fako-layout";
    public const int CurrentVersion = 1;

    public string? Format { get; set; }
    public int FormatVersion { get; set; }
    public string? AppVersion { get; set; }
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public AppConfig? Config { get; set; }
}

public sealed class HiddenRecord
{
    public string Path { get; set; } = "";
    public FileAttributes Added { get; set; }
}

/// <summary>%APPDATA%\Fako\config.json への保存・読み込み。</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま書く
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>環境変数 FAKO_HOME で差し替え可能（テスト用）。旧名の KAKOI_HOME も受け付ける。</summary>
    public static string Directory { get; } = ResolveDirectory();

    private static string ResolveDirectory()
    {
        if (Environment.GetEnvironmentVariable("FAKO_HOME") is { Length: > 0 } home) return home;
        if (Environment.GetEnvironmentVariable("KAKOI_HOME") is { Length: > 0 } legacy) return legacy;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fako");
    }

    public string FilePath { get; } = Path.Combine(Directory, "config.json");

    /// <summary>フェンスの配置・外観・設定を JSON にする。SSH の保存済みパスワードは含めない。</summary>
    public static string ExportLayout(AppConfig config)
    {
        // 参照を切るために、いったん JSON にしてから複製する
        var copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, Options), Options)
                   ?? new AppConfig();
        foreach (var page in copy.AllPages())
            if (page.Remote is { } remote) remote.ProtectedSecret = null;
        copy.HiddenByFako.Clear();

        var doc = new LayoutDocument
        {
            Format = LayoutDocument.FormatName,
            FormatVersion = LayoutDocument.CurrentVersion,
            AppVersion = typeof(ConfigStore).Assembly.GetName().Version?.ToString(3),
            Config = copy,
        };
        return JsonSerializer.Serialize(doc, Options);
    }

    /// <summary>書き出した設定を読み込む。形式が違う／新しすぎる場合は null とエラーを返す。</summary>
    public static AppConfig? ImportLayout(string json, out string? error)
    {
        error = null;
        try
        {
            var doc = JsonSerializer.Deserialize<LayoutDocument>(json, Options);
            if (doc is null || !string.Equals(doc.Format, LayoutDocument.FormatName, StringComparison.Ordinal))
            {
                error = "Fako の設定ファイルではありません。";
                return null;
            }
            if (doc.FormatVersion < 1 || doc.FormatVersion > LayoutDocument.CurrentVersion)
            {
                error = $"このファイルは新しい形式（v{doc.FormatVersion}）のため読み込めません。Fako を更新してください。";
                return null;
            }
            var config = doc.Config ?? new AppConfig();
            foreach (var page in config.AllPages())
                if (page.Remote is { } remote) remote.ProtectedSecret = null;
            config.HiddenByFako ??= new();
            config.HiddenByFako.Clear();
            config.Migrate();
            return config;
        }
        catch (JsonException ex)
        {
            error = $"設定ファイルを読み取れません。\n{ex.Message}";
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool _legacyChecked;

    /// <summary>旧名の %APPDATA%\Kakoi\config.json があれば、初回だけ引き継ぐ。</summary>
    private static void MigrateLegacy()
    {
        if (_legacyChecked) return;
        _legacyChecked = true;

        // 環境変数で場所を指定しているときは触らない（テストの隔離を壊さない）
        if (Environment.GetEnvironmentVariable("FAKO_HOME") is { Length: > 0 } ||
            Environment.GetEnvironmentVariable("KAKOI_HOME") is { Length: > 0 }) return;
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var legacy = Path.Combine(appData, "Kakoi", "config.json");
            var target = Path.Combine(Directory, "config.json");
            if (File.Exists(target) || !File.Exists(legacy)) return;
            System.IO.Directory.CreateDirectory(Directory);
            File.Copy(legacy, target);
            Log.Info($"settings migrated: {legacy} -> {target}");
        }
        catch (Exception ex)
        {
            Log.Error("設定の引き継ぎに失敗", ex);
        }
    }

    public AppConfig Load()
    {
        MigrateLegacy();
        if (!File.Exists(FilePath))
        {
            var fresh = new AppConfig();
            fresh.Migrate();
            return fresh;
        }
        try
        {
            var json = File.ReadAllText(FilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
            config.Migrate();
            return config;
        }
        catch (Exception)
        {
            // 壊れていたら退避して新規に始める（黙って上書きしない）
            var bad = Path.Combine(Directory, $"config.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(FilePath, bad, overwrite: true); } catch { }
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public sealed class FenceItem
{
    public FenceItem(string path, string name, bool exists, bool isFolder, string typeName,
        long? size, DateTime? modified, ImageSource? icon)
    {
        Path = path;
        Name = name;
        Exists = exists;
        IsFolder = isFolder;
        TypeName = typeName;
        Size = size;
        Modified = modified;
        Icon = icon;
    }

    public string Path { get; }
    public string Name { get; }
    public bool Exists { get; }
    public bool IsFolder { get; }
    public string TypeName { get; }
    public long? Size { get; }
    public DateTime? Modified { get; }
    public ImageSource? Icon { get; }

    public double Opacity => Exists ? 1.0 : 0.45;

    /// <summary>エクスプローラーと同じ「12,345 KB」形式（1 バイト以上は切り上げ）。</summary>
    public string SizeText => Size is long s ? $"{(long)Math.Ceiling(s / 1024.0):N0} KB" : "";

    public string ModifiedText => Modified?.ToString("yyyy/MM/dd HH:mm") ?? "";

    public string Tooltip
    {
        get
        {
            if (!Exists) return $"{Path}\n（見つかりません）";
            var lines = new List<string> { Path, $"種類: {TypeName}" };
            if (Size != null) lines.Add($"サイズ: {SizeText}");
            if (Modified != null) lines.Add($"更新日時: {ModifiedText}");
            return string.Join("\n", lines);
        }
    }
}
