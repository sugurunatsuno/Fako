using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fako.Models;

namespace Fako;

/// <summary>設定画面で編集中の振り分けルール。</summary>
public sealed class RuleViewModel : INotifyPropertyChanged
{
    private readonly Func<string, string?> _fenceTitle;
    private bool _enabled;
    private string _name;
    private RuleKind _kind;
    private string _pattern;
    private string _targetFenceId;
    private readonly string _id;

    public RuleViewModel(SortRule rule, Func<string, string?> fenceTitle)
    {
        _fenceTitle = fenceTitle;
        _id = rule.Id;
        _enabled = rule.Enabled;
        _name = rule.Name;
        _kind = rule.Kind;
        _pattern = rule.Pattern;
        _targetFenceId = rule.TargetFenceId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Name { get => _name; set => Set(ref _name, value); }

    public RuleKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            bool wasCategory = _kind == RuleKind.Category;
            _kind = value;
            // 「種類」とそれ以外では条件の書き方が違うので、切り替えたら条件を初期化する
            if (value == RuleKind.Category) _pattern = FileCategory.Image.ToString();
            else if (wasCategory) _pattern = "";
            Raise(nameof(Kind));
            Raise(nameof(Pattern));
            Raise(nameof(Category));
        }
    }

    public string Pattern { get => _pattern; set => Set(ref _pattern, value); }

    public FileCategory Category
    {
        get => Enum.TryParse<FileCategory>(_pattern, out var c) ? c : FileCategory.Image;
        set => Pattern = value.ToString();
    }

    public string TargetFenceId { get => _targetFenceId; set => Set(ref _targetFenceId, value ?? ""); }

    public bool IsCategory => _kind == RuleKind.Category;
    public bool IsTextPattern => !IsCategory;

    public string PatternHint => _kind switch
    {
        RuleKind.Extension => "例: pdf, docx, xlsx（カンマ・スペース区切りで複数）",
        RuleKind.NamePattern => "例: *見積*; 請求書_*（* は任意の文字列、? は任意の 1 文字。; 区切りで複数）",
        RuleKind.Regex => @"例: ^\d{8}_.*\.pdf$（名前に対する .NET の正規表現。大文字小文字は区別しません）",
        _ => "",
    };

    public string Summary
    {
        get
        {
            var cond = _kind == RuleKind.Category ? RuleEngine.CategoryLabel(Category) : (_pattern.Length > 0 ? _pattern : "（未入力）");
            var target = _fenceTitle(_targetFenceId) ?? "（未選択）";
            return $"{RuleEngine.KindLabel(_kind)}: {cond} → {target}";
        }
    }

    public string? Error
    {
        get
        {
            var e = RuleEngine.Validate(ToRule());
            if (e != null) return e;
            if (_fenceTitle(_targetFenceId) == null) return "振り分け先のフェンスを選んでください";
            return null;
        }
    }

    public bool HasError => Error != null;

    public SortRule ToRule() => new()
    {
        Id = _id,
        Enabled = _enabled,
        Name = _name,
        Kind = _kind,
        Pattern = _pattern,
        TargetFenceId = _targetFenceId,
    };

    /// <summary>フェンス名が変わったときなどに表示を更新する。</summary>
    public void Refresh()
    {
        Raise(nameof(Summary));
        Raise(nameof(Error));
        Raise(nameof(HasError));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name!);
    }

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(Summary) or nameof(Error) or nameof(HasError)) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCategory)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTextPattern)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PatternHint)));
    }
}
