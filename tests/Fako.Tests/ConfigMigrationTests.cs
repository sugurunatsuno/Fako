using System.Text.Json;
using Fako.Models;
using Xunit;

namespace Fako.Tests;

/// <summary>壊れた・古い config.json を読み込んだときの正規化。</summary>
public class ConfigMigrationTests
{
    [Fact]
    public void Migrate_NormalizesNullCollections()
    {
        var page = new FenceConfig
        {
            Tabs = null!,
            Items = null!,
            Style = null!,
        };
        var config = new AppConfig
        {
            Fences = new List<FenceConfig> { page },
            Rules = null!,
            RuleExclusions = null!,
            KnownHosts = null!,
            HiddenByFako = null!,
            DefaultStyle = null!,
        };

        config.Migrate();

        Assert.NotNull(config.Fences);
        Assert.NotNull(config.Rules);
        Assert.NotNull(config.RuleExclusions);
        Assert.NotNull(config.KnownHosts);
        Assert.NotNull(config.HiddenByFako);
        Assert.NotNull(config.DefaultStyle);
        Assert.NotNull(page.Tabs);
        Assert.NotNull(page.Items);
        Assert.NotNull(page.Style);
    }

    [Fact]
    public void Migrate_ReplacesNullFencesAndDropsNullEntries()
    {
        var config = new AppConfig { Fences = null! };
        config.Migrate();
        Assert.NotNull(config.Fences);

        var withNull = new AppConfig { Fences = new List<FenceConfig> { null!, new FenceConfig() } };
        withNull.Migrate();
        Assert.Single(withNull.Fences);
    }

    [Fact]
    public void Migrate_NormalizesInvalidBackgroundImageSettings()
    {
        var fence = new FenceConfig
        {
            Style = new FenceStyle
            {
                BackgroundImagePath = "   ",
                BackgroundImageOpacity = -0.5,
                BackgroundImageStretch = "UnknownMode",
            },
        };
        var config = new AppConfig
        {
            Fences = new List<FenceConfig> { fence },
            DefaultStyle = new FenceStyle
            {
                BackgroundImagePath = "",
                BackgroundImageOpacity = double.NaN,
                BackgroundImageStretch = null!,
            },
        };

        config.Migrate();

        Assert.Null(fence.Style.BackgroundImagePath);
        Assert.Equal(0d, fence.Style.BackgroundImageOpacity);
        Assert.Equal("UniformToFill", fence.Style.BackgroundImageStretch);

        Assert.Null(config.DefaultStyle.BackgroundImagePath);
        Assert.Equal(1d, config.DefaultStyle.BackgroundImageOpacity);
        Assert.Equal("UniformToFill", config.DefaultStyle.BackgroundImageStretch);
    }

    [Fact]
    public void Migrate_LegacyJsonWithoutBackgroundImageFields_KeepsDefaults()
    {
        const string legacyJson = """
            {
              "Version": 1,
              "DefaultStyle": { "Background": "#B0112233", "CornerRadius": 10 },
              "Fences": [
                {
                  "Title": "既存フェンス",
                  "Style": { "Background": "#C0445566" }
                }
              ]
            }
            """;

        var config = JsonSerializer.Deserialize<AppConfig>(legacyJson)!;
        config.Migrate();

        Assert.Equal("#B0112233", config.DefaultStyle.Background);
        Assert.Null(config.DefaultStyle.BackgroundImagePath);
        Assert.Equal(1d, config.DefaultStyle.BackgroundImageOpacity);
        Assert.Equal("UniformToFill", config.DefaultStyle.BackgroundImageStretch);

        var fence = Assert.Single(config.Fences);
        Assert.Equal("#C0445566", fence.Style.Background);
        Assert.Null(fence.Style.BackgroundImagePath);
        Assert.Equal(1d, fence.Style.BackgroundImageOpacity);
        Assert.Equal("UniformToFill", fence.Style.BackgroundImageStretch);
    }
}
