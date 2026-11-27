using System.IO;
using Fako.Models;
using Xunit;

namespace Fako.Tests;

/// <summary>レイアウト（フェンスの配置・外観）の書き出し／読み込み。</summary>
public class ConfigStoreLayoutTests
{
    private const string RootSecret = "ROOT-TOP-SECRET";
    private const string TabSecret = "TAB-TOP-SECRET";

    private static AppConfig CreateSampleConfig()
    {
        var tab = new FenceConfig
        {
            Title = "2ページ目",
            Mode = FenceMode.Portal,
            PortalPath = @"C:\Portal",
            Items = { @"C:\tab\item.docx" },
            Remote = new RemoteConfig
            {
                Host = "tab.example.com",
                Port = 2200,
                User = "bob",
                ProtectedSecret = TabSecret,
                Path = "/home/bob",
                ShowHidden = true,
            },
        };

        var fence = new FenceConfig
        {
            Title = "仕事",
            Left = 123.5,
            Top = 45.25,
            Width = 400,
            Height = 310,
            Mode = FenceMode.Shortcuts,
            RecentHours = 48,
            ActiveTab = 1,
            Items = { @"C:\docs\a.pdf", @"C:\docs\b.docx" },
            Remote = new RemoteConfig
            {
                Host = "example.com",
                Port = 2222,
                User = "alice",
                Auth = RemoteAuth.Password,
                KeyFile = @"C:\keys\id_ed25519",
                ProtectedSecret = RootSecret,
                Path = "/srv/share",
                ShowHidden = true,
            },
        };
        fence.Tabs.Add(tab);

        var config = new AppConfig
        {
            Fences = { fence },
            RuleExclusions = { @"C:\docs\excluded.txt" },
            HiddenByFako = { new HiddenRecord { Path = @"C:\old\x.txt", Added = FileAttributes.Hidden } },
            KnownHosts = { ["example.com:2222"] = "ssh-ed25519 SHA256:test" },
        };
        return config;
    }

    [Fact]
    public void ExportImport_RoundTripsFencePositionAndItems()
    {
        var original = CreateSampleConfig();

        var json = ConfigStore.ExportLayout(original);
        var imported = ConfigStore.ImportLayout(json, out var error);

        Assert.Null(error);
        Assert.NotNull(imported);
        var fence = Assert.Single(imported!.Fences);
        Assert.Equal("仕事", fence.Title);
        Assert.Equal(123.5, fence.Left);
        Assert.Equal(45.25, fence.Top);
        Assert.Equal(400, fence.Width);
        Assert.Equal(310, fence.Height);
        Assert.Equal(FenceMode.Shortcuts, fence.Mode);
        Assert.Equal(48, fence.RecentHours);
        Assert.Equal(1, fence.ActiveTab);
        Assert.Equal(new[] { @"C:\docs\a.pdf", @"C:\docs\b.docx" }, fence.Items);
        Assert.Equal(@"C:\docs\excluded.txt", Assert.Single(imported.RuleExclusions));
        Assert.Equal("ssh-ed25519 SHA256:test", imported.KnownHosts["example.com:2222"]);
    }

    [Fact]
    public void ExportImport_RoundTripsTabs()
    {
        var json = ConfigStore.ExportLayout(CreateSampleConfig());
        var imported = ConfigStore.ImportLayout(json, out var error);

        Assert.Null(error);
        var fence = Assert.Single(imported!.Fences);
        var tab = Assert.Single(fence.Tabs);
        Assert.Equal("2ページ目", tab.Title);
        Assert.Equal(FenceMode.Portal, tab.Mode);
        Assert.Equal(@"C:\Portal", tab.PortalPath);
        Assert.Equal(new[] { @"C:\tab\item.docx" }, tab.Items);
        Assert.Equal("tab.example.com", tab.Remote?.Host);
        Assert.Equal(2200, tab.Remote?.Port);
        Assert.True(tab.Remote?.ShowHidden);
    }

    [Fact]
    public void Export_StripsProtectedSecrets()
    {
        var json = ConfigStore.ExportLayout(CreateSampleConfig());

        Assert.DoesNotContain(RootSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(TabSecret, json, StringComparison.Ordinal);

        var imported = ConfigStore.ImportLayout(json, out var error);
        Assert.Null(error);
        var fence = Assert.Single(imported!.Fences);
        Assert.Null(fence.Remote!.ProtectedSecret);
        Assert.Null(Assert.Single(fence.Tabs).Remote!.ProtectedSecret);
        // 秘密以外の接続情報は残る
        Assert.Equal("example.com", fence.Remote.Host);
        Assert.Equal(2222, fence.Remote.Port);
        Assert.Equal("alice", fence.Remote.User);
        Assert.Equal(@"C:\keys\id_ed25519", fence.Remote.KeyFile);
    }

    [Fact]
    public void Export_ClearsHiddenFileRecords()
    {
        var json = ConfigStore.ExportLayout(CreateSampleConfig());
        var imported = ConfigStore.ImportLayout(json, out var error);

        Assert.Null(error);
        Assert.Empty(imported!.HiddenByFako);
    }

    [Fact]
    public void Import_MigratesLegacyColorIntoStyle()
    {
        var config = CreateSampleConfig();
        config.Fences[0].Color = "#FF112233";
        config.Fences[0].Style.Background = "#FF445566";

        var json = ConfigStore.ExportLayout(config);
        var imported = ConfigStore.ImportLayout(json, out var error);

        Assert.Null(error);
        var fence = Assert.Single(imported!.Fences);
        Assert.Null(fence.Color);
        Assert.Equal("#FF112233", fence.Style.Background);
    }

    [Fact]
    public void Import_ClampsOutOfRangeActiveTab()
    {
        var config = CreateSampleConfig();
        config.Fences[0].ActiveTab = 99;

        var json = ConfigStore.ExportLayout(config);
        var imported = ConfigStore.ImportLayout(json, out var error);

        Assert.Null(error);
        Assert.Equal(1, Assert.Single(imported!.Fences).ActiveTab);
    }

    [Fact]
    public void ExportImport_PreservesStyleAndSettings()
    {
        var config = CreateSampleConfig();
        config.DefaultStyle.TitleFontSize = 21;
        config.DefaultStyle.CornerRadius = 14;
        config.DefaultStyle.TitleAlignment = TitleAlign.Right;
        config.DoubleClickToHide = false;
        config.SingleClickOpen = true;
        config.PrivacyMode = true;
        config.SnapDistance = 30;

        var json = ConfigStore.ExportLayout(config);
        var imported = ConfigStore.ImportLayout(json, out var error);

        Assert.Null(error);
        Assert.Equal(21, imported!.DefaultStyle.TitleFontSize);
        Assert.Equal(14, imported.DefaultStyle.CornerRadius);
        Assert.Equal(TitleAlign.Right, imported.DefaultStyle.TitleAlignment);
        Assert.False(imported.DoubleClickToHide);
        Assert.True(imported.SingleClickOpen);
        Assert.True(imported.PrivacyMode);
        Assert.Equal(30, imported.SnapDistance);
    }

    [Fact]
    public void Import_RejectsEmptyObject()
    {
        var config = ConfigStore.ImportLayout("{}", out var error);

        Assert.Null(config);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Import_RejectsForeignFormatName()
    {
        var config = ConfigStore.ImportLayout(
            """{"Format":"some-other-app","FormatVersion":1,"Config":{}}""", out var error);

        Assert.Null(config);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"format\": \"fako-layout\", ")]
    [InlineData("[1, 2, 3]")]
    public void Import_RejectsBrokenJson(string json)
    {
        var config = ConfigStore.ImportLayout(json, out var error);

        Assert.Null(config);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(999)]
    public void Import_RejectsUnsupportedFormatVersion(int formatVersion)
    {
        var json = """{"Format":"fako-layout","FormatVersion":__VERSION__,"Config":{}}"""
            .Replace("__VERSION__", formatVersion.ToString());

        var config = ConfigStore.ImportLayout(json, out var error);

        Assert.Null(config);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Import_AcceptsDocumentWithoutConfig()
    {
        // Config が無くても形式さえ正しければ空の設定として読む
        var config = ConfigStore.ImportLayout(
            """{"Format":"fako-layout","FormatVersion":1}""", out var error);

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Empty(config!.Fences);
        Assert.NotNull(config.DefaultStyle);
    }

    [Fact]
    public void Import_RejectsFutureVersionWithHelpfulMessage()
    {
        var config = ConfigStore.ImportLayout(
            """{"Format":"fako-layout","FormatVersion":2,"Config":{}}""", out var error);

        Assert.Null(config);
        Assert.Contains("新しい形式", error);
    }

    [Fact]
    public void ExportImport_RoundTripsBackgroundImageStyle()
    {
        var config = CreateSampleConfig();
        config.DefaultStyle.BackgroundImagePath = @"C:\Users\alice\AppData\Roaming\Fako\backgrounds\default.png";
        config.DefaultStyle.BackgroundImageOpacity = 0.75;
        config.DefaultStyle.BackgroundImageStretch = "Uniform";
        config.Fences[0].Style.BackgroundImagePath = @"C:\Users\alice\AppData\Roaming\Fako\backgrounds\fence.jpg";
        config.Fences[0].Style.BackgroundImageOpacity = 0.4;
        config.Fences[0].Style.BackgroundImageStretch = "Fill";

        var json = ConfigStore.ExportLayout(config);
        var imported = ConfigStore.ImportLayout(json, out var error);

        Assert.Null(error);
        Assert.NotNull(imported);
        Assert.Equal(@"C:\Users\alice\AppData\Roaming\Fako\backgrounds\default.png", imported!.DefaultStyle.BackgroundImagePath);
        Assert.Equal(0.75, imported.DefaultStyle.BackgroundImageOpacity);
        Assert.Equal("Uniform", imported.DefaultStyle.BackgroundImageStretch);

        var fence = Assert.Single(imported.Fences);
        Assert.Equal(@"C:\Users\alice\AppData\Roaming\Fako\backgrounds\fence.jpg", fence.Style.BackgroundImagePath);
        Assert.Equal(0.4, fence.Style.BackgroundImageOpacity);
        Assert.Equal("Fill", fence.Style.BackgroundImageStretch);
    }

    [Fact]
    public void Import_LegacyLayoutWithoutBackgroundImageFields_UsesSafeDefaults()
    {
        const string legacyLayout = """
            {
              "Format": "fako-layout",
              "FormatVersion": 1,
              "Config": {
                "DefaultStyle": { "Background": "#A0202428" },
                "Fences": [
                  {
                    "Title": "旧レイアウト",
                    "Style": { "Background": "#A0182640" }
                  }
                ]
              }
            }
            """;

        var imported = ConfigStore.ImportLayout(legacyLayout, out var error);

        Assert.Null(error);
        Assert.NotNull(imported);
        Assert.Null(imported!.DefaultStyle.BackgroundImagePath);
        Assert.Equal(1d, imported.DefaultStyle.BackgroundImageOpacity);
        Assert.Equal("UniformToFill", imported.DefaultStyle.BackgroundImageStretch);

        var fence = Assert.Single(imported.Fences);
        Assert.Equal("#A0182640", fence.Style.Background);
        Assert.Null(fence.Style.BackgroundImagePath);
        Assert.Equal(1d, fence.Style.BackgroundImageOpacity);
        Assert.Equal("UniformToFill", fence.Style.BackgroundImageStretch);
    }

    [Fact]
    public void ExportImport_RoundTripsAgentSettings()
    {
        var config = CreateSampleConfig();
        config.Fences[0].Remote!.Auth = RemoteAuth.Agent;
        config.Fences[0].Remote!.AgentKeyFingerprint = "SHA256:agent-key";
        config.Fences[0].Remote!.AgentPipe = @"\\.\pipe\custom-agent";
        config.Fences[0].Remote!.KeyFile = null;
        config.Fences[0].Remote!.ProtectedSecret = null;

        var imported = ConfigStore.ImportLayout(ConfigStore.ExportLayout(config), out var error);

        Assert.Null(error);
        var remote = Assert.Single(imported!.Fences).Remote!;
        Assert.Equal(RemoteAuth.Agent, remote.Auth);
        Assert.Equal("SHA256:agent-key", remote.AgentKeyFingerprint);
        Assert.Equal(@"\\.\pipe\custom-agent", remote.AgentPipe);
    }

    [Fact]
    public void SaveLoad_RoundTripsAgentSettings()
    {
        var dir = TestTemp.CreateDirectory();
        try
        {
            var store = new ConfigStore(dir);
            var config = new AppConfig();
            config.Fences.Add(new FenceConfig
            {
                Title = "エージェント",
                Remote = new RemoteConfig
                {
                    Host = "agent.example.com",
                    Port = 2222,
                    User = "alice",
                    Auth = RemoteAuth.Agent,
                    AgentKeyFingerprint = "SHA256:saved-key",
                    AgentPipe = @"\\.\pipe\saved-agent",
                    Path = "/srv/share",
                },
            });

            store.Save(config);
            var loaded = store.Load();

            var remote = Assert.Single(loaded.Fences).Remote!;
            Assert.Equal("agent.example.com", remote.Host);
            Assert.Equal(2222, remote.Port);
            Assert.Equal("alice", remote.User);
            Assert.Equal(RemoteAuth.Agent, remote.Auth);
            Assert.Equal("SHA256:saved-key", remote.AgentKeyFingerprint);
            Assert.Equal(@"\\.\pipe\saved-agent", remote.AgentPipe);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_OldJsonWithoutAgentPropertiesUsesNulls()
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
                      "Title": "旧設定",
                      "Remote": {
                        "Host": "old.example.com",
                        "Port": 22,
                        "User": "bob",
                        "Auth": "KeyFile",
                        "KeyFile": "C:\\keys\\id_rsa"
                      }
                    }
                  ]
                }
                """);

            var loaded = store.Load();

            var remote = Assert.Single(loaded.Fences).Remote!;
            Assert.Equal(RemoteAuth.KeyFile, remote.Auth);
            Assert.Equal(@"C:\keys\id_rsa", remote.KeyFile);
            Assert.Null(remote.AgentKeyFingerprint);
            Assert.Null(remote.AgentPipe);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
