using System.IO;
using Fako.Models;
using Xunit;

namespace Fako.Tests;

/// <summary>自動振り分けルールの検証とマッチング。</summary>
public class RuleEngineTests : IDisposable
{
    private readonly string _tempDir;

    public RuleEngineTests()
    {
        _tempDir = TestTemp.CreateDirectory();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static SortRule Rule(RuleKind kind, string pattern) =>
        new() { Kind = kind, Pattern = pattern, Name = "test" };

    private string TempFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Validate_AcceptsWellFormedRules()
    {
        Assert.Null(RuleEngine.Validate(Rule(RuleKind.Extension, "pdf, docx")));
        Assert.Null(RuleEngine.Validate(Rule(RuleKind.Extension, "*.pdf;*.docx")));
        Assert.Null(RuleEngine.Validate(Rule(RuleKind.NamePattern, "*見積*; 請求書*")));
        Assert.Null(RuleEngine.Validate(Rule(RuleKind.Category, "Image")));
        Assert.Null(RuleEngine.Validate(Rule(RuleKind.Category, "Folder")));
        Assert.Null(RuleEngine.Validate(Rule(RuleKind.Category, "Shortcut")));
        Assert.Null(RuleEngine.Validate(Rule(RuleKind.Regex, @"^IMG_\d{4}")));
    }

    [Theory]
    [InlineData(RuleKind.Extension, "")]
    [InlineData(RuleKind.Extension, "  , ;、　")]
    [InlineData(RuleKind.NamePattern, "")]
    [InlineData(RuleKind.NamePattern, " , ; ")]
    [InlineData(RuleKind.Regex, "")]
    [InlineData(RuleKind.Regex, "   ")]
    public void Validate_RejectsEmptyPattern(RuleKind kind, string pattern)
    {
        Assert.Equal("条件が空です", RuleEngine.Validate(Rule(kind, pattern)));
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("")]
    [InlineData("Images")]
    public void Validate_RejectsUnknownCategory(string pattern)
    {
        Assert.Equal("種類を選んでください", RuleEngine.Validate(Rule(RuleKind.Category, pattern)));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("(abc")]
    [InlineData("*")]
    public void Validate_RejectsInvalidRegex(string pattern)
    {
        var error = RuleEngine.Validate(Rule(RuleKind.Regex, pattern));
        Assert.NotNull(error);
        Assert.Contains("正規表現が正しくありません", error);
    }

    [Theory]
    [InlineData(@"C:\x\report.pdf", true)]
    [InlineData(@"C:\x\REPORT.PDF", true)]
    [InlineData(@"C:\x\letter.docx", true)]
    [InlineData(@"C:\x\.docx", true)]
    [InlineData(@"C:\x\report.txt", false)]
    [InlineData(@"C:\x\pdf", false)]
    [InlineData(@"C:\x\report.pdf.txt", false)]
    public void Matches_Extension(string path, bool expected)
    {
        var rule = Rule(RuleKind.Extension, "pdf, docx");
        Assert.Equal(expected, RuleEngine.Matches(rule, path));
    }

    [Theory]
    [InlineData("*.pdf", @"C:\x\a.pdf", true)]
    [InlineData("*.pdf", @"C:\x\a.docx", false)]
    [InlineData("pdf", @"C:\x\a.pdf", true)]
    [InlineData(" .PDF ", @"C:\x\a.pdf", true)]
    [InlineData("pdf;docx", @"C:\x\a.docx", true)]
    public void Matches_ExtensionNormalizesWildcardAndDot(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, RuleEngine.Matches(Rule(RuleKind.Extension, pattern), path));
    }

    [Theory]
    [InlineData(@"C:\x\photo.png", true)]
    [InlineData(@"C:\x\photo.JPEG", true)]
    [InlineData(@"C:\x\photo.svg", true)]
    [InlineData(@"C:\x\photo.txt", false)]
    [InlineData(@"C:\x\photo", false)]
    public void Matches_CategoryImage(string path, bool expected)
    {
        Assert.Equal(expected, RuleEngine.Matches(Rule(RuleKind.Category, "Image"), path));
    }

    [Theory]
    [InlineData(@"C:\x\app.lnk", true)]
    [InlineData(@"C:\x\site.URL", true)]
    [InlineData(@"C:\x\app.exe", false)]
    [InlineData(@"C:\x\app.lnk.txt", false)]
    public void Matches_CategoryShortcut(string path, bool expected)
    {
        Assert.Equal(expected, RuleEngine.Matches(Rule(RuleKind.Category, "Shortcut"), path));
    }

    [Fact]
    public void Matches_CategoryFolder_RequiresExistingDirectory()
    {
        var folder = Path.Combine(_tempDir, "資料");
        Directory.CreateDirectory(folder);
        var missing = Path.Combine(_tempDir, "does-not-exist");

        Assert.True(RuleEngine.Matches(Rule(RuleKind.Category, "Folder"), folder));
        Assert.True(RuleEngine.Matches(Rule(RuleKind.Category, "Folder"), folder + "\\"));
        Assert.False(RuleEngine.Matches(Rule(RuleKind.Category, "Folder"), missing));

        // ファイル名が拡張子風でも、実体がフォルダーなら Extension には当たらない
        var tricky = Path.Combine(_tempDir, "folder.pdf");
        Directory.CreateDirectory(tricky);
        Assert.False(RuleEngine.Matches(Rule(RuleKind.Extension, "pdf"), tricky));
    }

    [Fact]
    public void Matches_CategoryFolder_IsNotMatchedByFileCategory()
    {
        var folder = Path.Combine(_tempDir, "folder");
        Directory.CreateDirectory(folder);

        Assert.False(RuleEngine.Matches(Rule(RuleKind.Category, "Image"), folder));
        Assert.False(RuleEngine.Matches(Rule(RuleKind.Category, "Document"), folder));
    }

    [Theory]
    [InlineData("*見積*", @"C:\x\2026年見積書.xlsx", true)]
    [InlineData("*見積*", @"C:\x\請求書.xlsx", false)]
    [InlineData("請求書*", @"C:\x\請求書_01.pdf", true)]
    [InlineData("*.pdf", @"C:\x\a.pdf", true)]
    [InlineData("*.PDF", @"C:\x\a.pdf", true)]
    [InlineData("report?.docx", @"C:\x\report1.docx", true)]
    [InlineData("report?.docx", @"C:\x\report12.docx", false)]
    [InlineData("a*;b*", @"C:\x\b-file.txt", true)]
    [InlineData("a*;b*", @"C:\x\c-file.txt", false)]
    public void Matches_NamePattern(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, RuleEngine.Matches(Rule(RuleKind.NamePattern, pattern), path));
    }

    [Theory]
    [InlineData(@"^IMG_\d{4}", @"C:\x\IMG_1234.png", true)]
    [InlineData(@"^IMG_\d{4}", @"C:\x\img_1234.png", true)]   // 大文字小文字は無視
    [InlineData(@"^IMG_\d{4}", @"C:\x\IMG_123.png", false)]
    [InlineData(@"\.pdf$", @"C:\x\a.pdf", true)]
    [InlineData(@"\.pdf$", @"C:\x\a.pdf.txt", false)]
    [InlineData("^請求書", @"C:\x\請求書_2026.pdf", true)]
    public void Matches_Regex(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, RuleEngine.Matches(Rule(RuleKind.Regex, pattern), path));
    }

    [Fact]
    public void Matches_InvalidRegex_ReturnsFalseInsteadOfThrowing()
    {
        Assert.False(RuleEngine.Matches(Rule(RuleKind.Regex, "["), @"C:\x\a.pdf"));
    }

    [Fact]
    public void Matches_UsesFileNameOnly_NotDirectory()
    {
        var path = Path.Combine(_tempDir, "pdf", "note.txt");
        Assert.True(RuleEngine.Matches(Rule(RuleKind.Extension, "txt"), path));
        Assert.False(RuleEngine.Matches(Rule(RuleKind.Extension, "pdf"), path));
    }

    [Fact]
    public void Matches_RealFileOnDisk()
    {
        var pdf = TempFile("a.pdf");
        var docx = TempFile("b.docx");

        Assert.True(RuleEngine.Matches(Rule(RuleKind.Extension, "pdf, docx"), pdf));
        Assert.True(RuleEngine.Matches(Rule(RuleKind.Extension, "pdf, docx"), docx));
        Assert.True(RuleEngine.Matches(Rule(RuleKind.Category, "Document"), pdf));
    }
}
