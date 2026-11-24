using System.IO;
using Fako.Models;
using Xunit;

namespace Fako.Tests;

/// <summary>フォルダー列挙と「最近のファイル」の境界条件（UI・ネットワークには触れない）。</summary>
public class FolderListingTests : IDisposable
{
    private readonly string _tempDir;

    public FolderListingTests()
    {
        _tempDir = TestTemp.CreateDirectory();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void List_NonexistentOrEmptyDirectories_ReturnsEmpty()
    {
        var missing = Path.Combine(_tempDir, "missing");
        var missingNested = Path.Combine(missing, "deeper");

        Assert.Empty(FolderListing.List(missing));
        Assert.Empty(FolderListing.List(null, "", missing, missingNested));
        Assert.Empty(FolderListing.List());
    }

    [Fact]
    public void List_ReturnsEntriesWithFoldersFirst()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "zeta"));
        File.WriteAllText(Path.Combine(_tempDir, "alpha.txt"), "");
        File.WriteAllText(Path.Combine(_tempDir, "beta.txt"), "");

        var entries = FolderListing.List(_tempDir);

        Assert.Equal(3, entries.Count);
        Assert.Equal(Path.Combine(_tempDir, "zeta"), entries[0]);
        Assert.Contains(Path.Combine(_tempDir, "alpha.txt"), entries);
        Assert.Contains(Path.Combine(_tempDir, "beta.txt"), entries);
    }

    [Fact]
    public void List_DuplicateDirectories_DoNotThrow()
    {
        File.WriteAllText(Path.Combine(_tempDir, "only.txt"), "");

        var entries = FolderListing.List(_tempDir, _tempDir);

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void IsShellItem_RecognizesPrefix()
    {
        Assert.True(FolderListing.IsShellItem("::{645FF040-5081-101B-9F08-00AA002F954E}"));
        Assert.False(FolderListing.IsShellItem(@"C:\Users\Public\Desktop"));
        Assert.False(FolderListing.IsShellItem(""));
    }

    [Fact]
    public void RecentFiles_DoesNotThrowForExtremeArguments()
    {
        var recent = FolderListing.RecentFiles(hours: 0, max: 0);

        // max は 1〜1000 に丸められる
        Assert.NotNull(recent);
        Assert.InRange(recent.Count, 0, 1);
    }

    [Fact]
    public void RecentFiles_ClampsMaximumCount()
    {
        var recent = FolderListing.RecentFiles(hours: 24, max: 5);

        Assert.NotNull(recent);
        Assert.InRange(recent.Count, 0, 5);
        Assert.All(recent, path =>
        {
            Assert.True(File.Exists(path));
            Assert.True(File.GetLastWriteTime(path) >= DateTime.Now.AddHours(-25));
        });
    }

    [Fact]
    public void RecentFiles_HugeRange_DoesNotThrow()
    {
        var recent = FolderListing.RecentFiles(int.MaxValue, int.MaxValue);

        Assert.NotNull(recent);
        Assert.InRange(recent.Count, 0, 1000);
    }

    [Fact]
    public void RecentFiles_IsOrderedNewestFirst()
    {
        var recent = FolderListing.RecentFiles(hours: 24 * 30, max: 50);
        var times = recent.Select(File.GetLastWriteTime).ToList();

        for (int i = 1; i < times.Count; i++)
            Assert.True(times[i - 1] >= times[i], "最近のファイルは新しい順に並ぶ");
    }

    [Fact]
    public void RecentRoots_ContainsNoNullOrEmptyEntries()
    {
        var roots = FolderListing.RecentRoots().ToList();

        Assert.NotEmpty(roots);
        Assert.All(roots, r => Assert.False(string.IsNullOrWhiteSpace(r)));
        Assert.All(roots, r => Assert.True(Path.IsPathRooted(r)));
    }

    [Fact]
    public void LogicalComparer_SortsNumbersNaturally()
    {
        var names = new List<string> { "file10.txt", "file2.txt", "file1.txt" };
        names.Sort(FolderListing.LogicalComparer.Instance);

        Assert.Equal(new[] { "file1.txt", "file2.txt", "file10.txt" }, names);
    }
}
