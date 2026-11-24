using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Fako;

/// <summary>名前を入力する小さなダイアログ（名前の変更・新しいフォルダー）。</summary>
public partial class RenameWindow : Window
{
    private readonly string _original;
    private readonly Func<string, string?> _validate;

    public string NewName => NameBox.Text.Trim();

    /// <summary>手元のファイル／フォルダーの名前の変更。</summary>
    public RenameWindow(string path) : this(
        Path.GetFileName(path.TrimEnd('\\')),
        Directory.Exists(path),
        name => ValidateLocal(name, Path.GetFileName(path.TrimEnd('\\')), Path.GetDirectoryName(path.TrimEnd('\\')) ?? ""))
    {
    }

    /// <param name="original">今の名前（新規作成なら初期値）。</param>
    /// <param name="validate">入力のチェック。問題があれば理由、無ければ null。</param>
    /// <param name="title">ウィンドウのタイトル（既定は「名前の変更」）。</param>
    /// <param name="prompt">説明文（既定は「「元の名前」の新しい名前:」）。</param>
    public RenameWindow(string original, bool isFolder, Func<string, string?> validate, string? title = null, string? prompt = null)
    {
        InitializeComponent();
        _original = original;
        _validate = validate;
        if (title != null) Title = title;
        Prompt.Text = prompt ?? $"「{original}」の新しい名前:";
        NameBox.Text = original;
        IsNew = title != null;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            // エクスプローラーと同じく、拡張子を除いた部分を選んだ状態にする
            int dot = isFolder ? -1 : original.LastIndexOf('.');
            if (dot > 0) NameBox.Select(0, dot);
            else NameBox.SelectAll();
        };
    }

    /// <summary>新規作成（同じ名前のままでも OK にする）。</summary>
    private bool IsNew { get; }

    public static string? ValidateLocal(string name, string original, string directory)
    {
        if (name.Length == 0) return "名前を入力してください";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "次の文字は使えません: \\ / : * ? \" < > |";
        if (name.EndsWith('.')) return "名前の最後に「.」は使えません";
        if (!string.Equals(name, original, StringComparison.OrdinalIgnoreCase)
            && (File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name))))
            return "同じ名前のファイルまたはフォルダーがすでにあります";
        return null;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var error = NewName.Length == 0 ? "名前を入力してください" : _validate(NewName);
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = error == null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (NewName.Length == 0 || _validate(NewName) != null) return;
        DialogResult = IsNew || !string.Equals(NewName, _original, StringComparison.Ordinal);
    }
}
