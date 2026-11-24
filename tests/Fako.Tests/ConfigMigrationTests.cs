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
}
