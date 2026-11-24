using Xunit;

namespace Fako.Tests;

/// <summary>GitHub Releases のタグ（v1.2.3 など）からバージョンを取り出す処理。</summary>
public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.10.0", 2, 10, 0)]
    [InlineData("  v0.1.0  ", 0, 1, 0)]
    [InlineData("v1.2.3-beta", 1, 2, 3)]
    [InlineData("v1.2.3-beta.1", 1, 2, 3)]
    [InlineData("1.2.3-rc.1", 1, 2, 3)]
    [InlineData("v1.2.3+build.7", 1, 2, 3)]
    [InlineData("v1.2.3-rc.1+build.7", 1, 2, 3)]
    public void TryParseVersion_AcceptsReleaseTag(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void TryParseVersion_IgnoresPreReleaseAndBuildMetadata()
    {
        Assert.True(UpdateService.TryParseVersion("v3.4.5-anything+else", out var version));
        Assert.Equal(new Version(3, 4, 5), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("V")]
    [InlineData("latest")]
    [InlineData("v1.2.x")]
    [InlineData("v1.2.3.4.5")]
    [InlineData("v-1.0.0")]
    [InlineData("release 1.2.3")]
    public void TryParseVersion_RejectsInvalidTags(string tag)
    {
        Assert.False(UpdateService.TryParseVersion(tag, out _));
    }
}
