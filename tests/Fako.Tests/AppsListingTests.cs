using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fako.Interop;
using Fako.Models;
using Xunit;

namespace Fako.Tests;

/// <summary>アプリ一覧（shell:AppsFolder）仮想フェンスの列挙・ノイズ判定・設定永続化のテスト。</summary>
public class AppsListingTests
{
    // ------------------------------------------------------------------
    // FolderListing ヘルパー
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData(@"SHELL:APPSFOLDER\SomeApp", true)]
    [InlineData(@"shell:appsfolder\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe", true)]
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}", false)]
    [InlineData(@"C:\Program Files\Fako\Fako.exe", false)]
    [InlineData("shell:AppsFolder", false)]
    [InlineData("", false)]
    public void IsAppItem_RecognizesAppPrefixCaseInsensitively(string path, bool expected)
    {
        Assert.Equal(expected, FolderListing.IsAppItem(path));
    }

    [Fact]
    public void ExtractAppId_StripsPrefixWhenPresent()
    {
        const string appId = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";
        Assert.Equal(appId, FolderListing.ExtractAppId(FolderListing.AppPrefix + appId));
        Assert.Equal(appId, FolderListing.ExtractAppId(@"SHELL:APPSFOLDER\" + appId));
        Assert.Equal(@"C:\foo.exe", FolderListing.ExtractAppId(@"C:\foo.exe"));
        Assert.Equal("", FolderListing.ExtractAppId(""));
    }

    [Fact]
    public void StartMenuProgramRoots_ReturnsRootedNonEmptyDirectories()
    {
        var roots = FolderListing.StartMenuProgramRoots().ToList();

        Assert.NotEmpty(roots);
        Assert.All(roots, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r));
            Assert.True(Path.IsPathRooted(r));
        });
    }

    // ------------------------------------------------------------------
    // AppsListing.IsNoiseApp
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("", "SomeApp")]
    [InlineData("   ", "SomeApp")]
    [InlineData("SomeApp", "")]
    [InlineData("SomeApp", "   ")]
    [InlineData("Project Support", "http://example.com/support")]
    [InlineData("Vendor Page", "https://example.com")]
    [InlineData("User Guide", @"C:\Apps\Foo\guide.pdf")]
    [InlineData("Quick Start", @"C:\Apps\Foo\index.html")]
    [InlineData("Index", @"C:\Apps\Foo\index.HTM")]
    [InlineData("Help", @"C:\Apps\Foo\help.chm")]
    [InlineData("Notes", @"C:\Apps\Foo\notes.txt")]
    [InlineData("License Info", @"C:\Apps\Foo\license.rtf")]
    [InlineData("Readme Doc", @"C:\Apps\Foo\README.md")]
    [InlineData("Shortcut Link", @"C:\Apps\Foo\site.url")]
    [InlineData("Foo", @"C:\Program Files\Foo\unins000.exe")]
    [InlineData("Foo", @"C:\Program Files\Foo\Uninstall.exe")]
    [InlineData("Foo", @"C:\Program Files\Foo\uninstall_foo.exe")]
    [InlineData("Foo", @"C:\Program Files\Foo\uninst.exe")]
    [InlineData("Foo", @"C:\Program Files\Foo\uninst_bar.exe")]
    [InlineData("Uninstall Foo", @"C:\Program Files\Foo\setup.exe")]
    [InlineData("Foo のアンインストール", @"C:\Program Files\Foo\setup.exe")]
    [InlineData("Foo アンインストーラー", @"C:\Program Files\Foo\setup.exe")]
    [InlineData("Foo アンインストーラ", @"C:\Program Files\Foo\setup.exe")]
    [InlineData("Foo の削除", @"C:\Program Files\Foo\setup.exe")]
    [InlineData("Foo 削除ツール", @"C:\Program Files\Foo\tool.exe")]
    [InlineData("ReadMe", @"C:\Apps\Foo\open.exe")]
    [InlineData("Read Me", @"C:\Apps\Foo\open.exe")]
    [InlineData("User Manual", @"C:\Apps\Foo\open.exe")]
    [InlineData("Python 3.13 Manuals (64-bit)", @"C:\Python313\python.exe")]
    [InlineData("Python 3.13 Module Docs (64-bit)", @"C:\Python313\python.exe")]
    [InlineData("Foo Documentation", @"C:\Apps\Foo\open.exe")]
    [InlineData("Release Notes", @"C:\Apps\Foo\open.exe")]
    [InlineData("Changelog", @"C:\Apps\Foo\open.exe")]
    [InlineData("License", @"C:\Apps\Foo\open.exe")]
    [InlineData("License Agreement", @"C:\Apps\Foo\open.exe")]
    [InlineData("Check for Updates", @"C:\Apps\Foo\updater.exe")]
    [InlineData("EULA", @"C:\Apps\Foo\open.exe")]
    [InlineData("RivaTuner Statistics Server localization reference", @"C:\RTSS\ref.exe")]
    [InlineData("Skin Format Reference", @"C:\RTSS\ref.exe")]
    [InlineData("Visit Web Site", @"C:\Apps\Foo\open.exe")]
    [InlineData("Project Website", @"C:\Apps\Foo\open.exe")]
    [InlineData("操作マニュアル", @"C:\Apps\Foo\open.exe")]
    [InlineData("ヘルプを表示", @"C:\Apps\Foo\open.exe")]
    [InlineData("はじめにお読みください", @"C:\Apps\Foo\open.exe")]
    [InlineData("最初にお読みください", @"C:\Apps\Foo\open.exe")]
    [InlineData("バージョン情報", @"C:\Apps\Foo\about.exe")]
    [InlineData("更新の確認", @"C:\Apps\Foo\updater.exe")]
    [InlineData("Javaについて", @"C:\Program Files\Java\javacpl.exe")]
    public void IsNoiseApp_ReturnsTrueForNoiseItems(string displayName, string appId)
    {
        Assert.True(AppsListing.IsNoiseApp(displayName, appId));
    }

    [Theory]
    [InlineData("Bitwarden", "com.squirrel.Bitwarden.Bitwarden")]
    [InlineData("Discord", "com.squirrel.Discord.Discord")]
    [InlineData("Fako", @"C:\Program Files\Fako\Fako.exe")]
    [InlineData("Microsoft Edge", "MSEdge")]
    [InlineData("Microsoft Store", "Microsoft.WindowsStore_8wekyb3d8bbwe!App")]
    [InlineData("OpenCode", @"C:\Users\alice\AppData\Local\OpenCode\OpenCode.exe")]
    [InlineData("PowerShell", @"{6D809377-6AF0-444B-8957-A3773F02200E}\PowerShell\7\pwsh.exe")]
    [InlineData("Rider", @"C:\Users\alice\AppData\Local\Programs\Rider\bin\rider64.exe")]
    [InlineData("ターミナル", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App")]
    [InlineData("電卓", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("設定", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel")]
    [InlineData("メモ帳", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App")]
    [InlineData("Noita", "steam://rungameid/881100")]
    [InlineData("Microsoft To Do", "Microsoft.Todos_8wekyb3d8bbwe!App")]
    [InlineData("Python 3.13 (64-bit)", @"C:\Python313\python.exe")]
    [InlineData("RivaTuner Statistics Server", @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe")]
    [InlineData("iLok License Manager", @"C:\Program Files\PACE\iLokLicenseManager.exe")]
    public void IsNoiseApp_ReturnsFalseForRegularApps(string displayName, string appId)
    {
        Assert.False(AppsListing.IsNoiseApp(displayName, appId));
    }

    // ------------------------------------------------------------------
    // AppsListing.Filter
    // ------------------------------------------------------------------

    [Fact]
    public void Filter_HideNoiseControlsNoiseRemoval()
    {
        var entries = new[]
        {
            MakeEntry("Bitwarden", "Bitwarden"),
            MakeEntry("Uninstall Foo", @"C:\Foo\unins000.exe"),
            MakeEntry("Javaについて", @"C:\Java\about.exe"),
            MakeEntry("Noita", "steam://rungameid/881100"),
        };

        var filtered = AppsListing.Filter(entries, hideNoise: true);
        var unfiltered = AppsListing.Filter(entries, hideNoise: false);

        Assert.Equal(new[] { "Bitwarden", "Noita" }, filtered.Select(e => e.DisplayName));
        Assert.Equal(4, unfiltered.Count);
    }

    [Fact]
    public void Filter_DeduplicatesAppIdCaseInsensitively()
    {
        var entries = new[]
        {
            MakeEntry("Calculator", "Microsoft.WindowsCalculator!App"),
            MakeEntry("Calculator Duplicate", "microsoft.windowscalculator!app"),
            MakeEntry("Notepad", "Microsoft.WindowsNotepad!App"),
        };

        var filtered = AppsListing.Filter(entries, hideNoise: true);

        Assert.Equal(2, filtered.Count);
        Assert.Equal("Calculator", filtered[0].DisplayName);
        Assert.Equal("Notepad", filtered[1].DisplayName);
    }

    [Fact]
    public void Filter_ExcludesBlankNameOrBlankAppId()
    {
        var entries = new[]
        {
            MakeEntry("", "ValidId1"),
            MakeEntry("   ", "ValidId2"),
            MakeEntry("ValidName1", ""),
            MakeEntry("ValidName2", "   "),
            MakeEntry("Fako", "Fako.App"),
        };

        var filtered = AppsListing.Filter(entries, hideNoise: false);

        var only = Assert.Single(filtered);
        Assert.Equal("Fako", only.DisplayName);
    }

    [Fact]
    public void Filter_SortsDisplayNameNaturally()
    {
        var entries = new[]
        {
            MakeEntry("App 10", "app10"),
            MakeEntry("App 2", "app2"),
            MakeEntry("App 1", "app1"),
        };

        var filtered = AppsListing.Filter(entries, hideNoise: true);

        Assert.Equal(new[] { "App 1", "App 2", "App 10" }, filtered.Select(e => e.DisplayName));
    }

    // ------------------------------------------------------------------
    // Config / ConfigStore
    // ------------------------------------------------------------------

    [Fact]
    public void FenceConfig_AppsDefaultsAreTrue()
    {
        var fence = new FenceConfig();

        Assert.True(fence.AppsHideNoise);
        Assert.True(fence.AppsSingleClick);
        Assert.True(fence.AppsAlwaysShowSearch);
    }

    [Fact]
    public void SaveLoad_AndExportImport_RoundTripAppsFenceSettings()
    {
        var dir = TestTemp.CreateDirectory();
        try
        {
            var store = new ConfigStore(dir);
            var config = new AppConfig();
            config.Fences.Add(new FenceConfig
            {
                Title = "アプリ",
                Mode = FenceMode.Apps,
                AppsHideNoise = false,
                AppsSingleClick = false,
                AppsAlwaysShowSearch = false,
            });

            store.Save(config);
            var loaded = store.Load();

            var savedFence = Assert.Single(loaded.Fences);
            Assert.Equal(FenceMode.Apps, savedFence.Mode);
            Assert.False(savedFence.AppsHideNoise);
            Assert.False(savedFence.AppsSingleClick);
            Assert.False(savedFence.AppsAlwaysShowSearch);

            var layoutJson = ConfigStore.ExportLayout(loaded);
            var imported = ConfigStore.ImportLayout(layoutJson, out var error);

            Assert.Null(error);
            Assert.NotNull(imported);
            var importedFence = Assert.Single(imported!.Fences);
            Assert.Equal(FenceMode.Apps, importedFence.Mode);
            Assert.False(importedFence.AppsHideNoise);
            Assert.False(importedFence.AppsSingleClick);
            Assert.False(importedFence.AppsAlwaysShowSearch);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_LegacyConfigWithoutAppsProperties_DefaultsToTrue()
    {
        var dir = TestTemp.CreateDirectory();
        try
        {
            var store = new ConfigStore(dir);
            File.WriteAllText(store.FilePath, """
                {
                  "Version": 1,
                  "Fences": [
                    {
                      "Title": "既存フェンス",
                      "Mode": "Shortcuts"
                    }
                  ]
                }
                """);

            var loaded = store.Load();

            var fence = Assert.Single(loaded.Fences);
            Assert.Equal(FenceMode.Shortcuts, fence.Mode);
            Assert.True(fence.AppsHideNoise);
            Assert.True(fence.AppsSingleClick);
            Assert.True(fence.AppsAlwaysShowSearch);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // FenceItem Tooltip / Null ガード / ShellApps 実環境スモークテスト
    // ------------------------------------------------------------------

    [Fact]
    public void Filter_NullEntries_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AppsListing.Filter(null!, hideNoise: true));
    }

    [Fact]
    public void FenceItem_Tooltip_UsesDisplayNameForAppItems()
    {
        var item = new FenceItem(
            @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            "電卓",
            exists: true,
            isFolder: false,
            typeName: "アプリケーション",
            size: null,
            modified: null,
            icon: null);

        Assert.Equal("電卓\n種類: アプリケーション", item.Tooltip);
    }

    [Fact]
    public void ShellApps_Enumerate_ReturnsInstalledAppsWithAppPrefix()
    {
        IReadOnlyList<AppEntry>? apps = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { apps = ShellApps.Enumerate(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.NotNull(apps);
        Assert.NotEmpty(apps!);
        Assert.All(apps!, app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.AppId));
            Assert.False(string.IsNullOrWhiteSpace(app.DisplayName));
            Assert.StartsWith(FolderListing.AppPrefix, app.ParsingPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(FolderListing.AppPrefix + app.AppId, app.ParsingPath);
        });
    }

    [Fact]
    public void ShellApps_Enumerate_CachesUntilInvalidated()
    {
        RunOnSta(() =>
        {
            ShellApps.InvalidateCache();
            var first = ShellApps.Enumerate();
            var second = ShellApps.Enumerate();

            Assert.Same(first, second);
            Assert.NotEmpty(first);

            ShellApps.InvalidateCache();
            var third = ShellApps.Enumerate();

            Assert.NotSame(first, third);
            Assert.Equal(first.Select(e => e.AppId), third.Select(e => e.AppId));
        });
    }

    [Fact]
    public void ShellIcons_CreateAppItems_MatchesListInstalledOrderAndPopulatesIcons()
    {
        RunOnSta(() =>
        {
            ShellApps.InvalidateCache();
            ShellIcons.InvalidateApps();

            var items = ShellIcons.CreateAppItems(hideNoise: true, iconPx: 64);
            var expected = AppsListing.ListInstalled(hideNoise: true);

            Assert.Equal(expected.Select(e => e.ParsingPath), items.Select(i => i.Path));
            Assert.Equal(expected.Select(e => e.DisplayName), items.Select(i => i.Name));
            Assert.Contains(items, i => i.Icon is not null);

            // ウォームキャッシュ経由の 2 回目も同順序・同結果
            var cachedItems = ShellIcons.CreateAppItems(hideNoise: true, iconPx: 64);
            Assert.Equal(items.Select(i => i.Path), cachedItems.Select(i => i.Path));

            // 異なるサイズでの一括取得パス（BulkFetch）も動作する
            var smallItems = ShellIcons.CreateAppItems(hideNoise: true, iconPx: 32);
            Assert.Equal(expected.Select(e => e.ParsingPath), smallItems.Select(i => i.Path));
            Assert.Contains(smallItems, i => i.Icon is not null);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShellIcons_HBitmapToBitmapSource_PreservesOrientationAndAlphaFormat(bool hasAlpha)
    {
        RunOnSta(() =>
        {
            const int w = 2, h = 2;
            IntPtr hbmp = CreateTestDib32(w, h, bottomUp: true, hasAlpha, out byte[] expectedTopDownPixels);
            try
            {
                var bmp = ShellIcons.HBitmapToBitmapSource(hbmp);
                Assert.NotNull(bmp);
                Assert.Equal(w, bmp!.PixelWidth);
                Assert.Equal(h, bmp.PixelHeight);
                Assert.Equal(hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, bmp.Format);

                var actual = new byte[w * h * 4];
                bmp.CopyPixels(actual, w * 4, 0);
                Assert.Equal(expectedTopDownPixels, actual);
            }
            finally
            {
                DeleteTestObject(hbmp);
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new AggregateException(failure);
    }

    private static IntPtr CreateTestDib32(int w, int h, bool bottomUp, bool hasAlpha, out byte[] expectedTopDown)
    {
        // 上行: 青 (B=0xFF, G=0, R=0)、下行: 赤 (B=0, G=0, R=0xFF)
        byte a = hasAlpha ? (byte)0x80 : (byte)0x00;
        expectedTopDown =
        [
            0xFF, 0x00, 0x00, a,    0xFF, 0x00, 0x00, a,
            0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00,
        ];

        var bmi = new TestBitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<TestBitmapInfoHeader>(),
            biWidth = w,
            biHeight = bottomUp ? h : -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        IntPtr hbmp = CreateTestDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        Assert.NotEqual(IntPtr.Zero, hbmp);
        Assert.NotEqual(IntPtr.Zero, bits);

        int stride = w * 4;
        for (int y = 0; y < h; y++)
        {
            int srcRow = bottomUp ? (h - 1 - y) : y;
            Marshal.Copy(expectedTopDown, srcRow * stride, IntPtr.Add(bits, y * stride), stride);
        }
        return hbmp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestBitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    private static extern IntPtr CreateTestDIBSection(IntPtr hdc, ref TestBitmapInfoHeader pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static extern bool DeleteTestObject(IntPtr hObject);

    private static AppEntry MakeEntry(string displayName, string appId) =>
        new(appId, displayName, FolderListing.AppPrefix + appId);
}
