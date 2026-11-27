using System.IO;
using Fako.Remote;
using Xunit;

namespace Fako.Tests;

/// <summary>~/.ssh/config の読み取り補助（IdentityAgent・IdentitiesOnly・既定値の継承）。</summary>
public class SshConfigReaderTests
{
    private static List<SshHostEntry> Read(params string[] lines) =>
        SshConfigReader.ReadFromLines(lines);

    [Theory]
    [InlineData("none", "none")]
    [InlineData("SSH_AUTH_SOCK", "SSH_AUTH_SOCK")]
    [InlineData(@"\\.\pipe\openssh-ssh-agent", @"\\.\pipe\openssh-ssh-agent")]
    [InlineData("//./pipe/custom-agent", "//./pipe/custom-agent")]
    public void IdentityAgent_IsPreserved(string value, string expected)
    {
        var entry = Assert.Single(Read("Host sample", "  IdentityAgent " + value));

        Assert.Equal(expected, entry.IdentityAgent);
    }

    [Fact]
    public void IdentityAgent_TildePathIsExpanded()
    {
        var entry = Assert.Single(Read("Host sample", "  IdentityAgent ~/agent.sock"));

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "agent.sock"),
            entry.IdentityAgent);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    [InlineData(null, false)]
    public void IdentitiesOnly_IsRead(string? value, bool expected)
    {
        var lines = value == null
            ? new[] { "Host sample", "  HostName sample.example.com" }
            : new[] { "Host sample", "  HostName sample.example.com", "  IdentitiesOnly " + value };

        var entry = Assert.Single(Read(lines));

        Assert.Equal(expected, entry.IdentitiesOnly);
    }

    [Fact]
    public void HostStar_ProvidesDefaultsForLaterHosts()
    {
        var entry = Assert.Single(Read(
            "Host *",
            "  User default-user",
            "  Port 2222",
            "  IdentityAgent none",
            "Host alpha",
            "  HostName alpha.example.com"));

        Assert.Equal("alpha", entry.Alias);
        Assert.Equal("alpha.example.com", entry.HostName);
        Assert.Equal("default-user", entry.User);
        Assert.Equal(2222, entry.Port);
        Assert.Equal("none", entry.IdentityAgent);
    }

    [Fact]
    public void HostStar_DoesNotOverrideExplicitValues()
    {
        var entry = Assert.Single(Read(
            "Host *",
            "  User default-user",
            "  Port 2222",
            "Host beta",
            "  User beta-user",
            "  Port 2200"));

        Assert.Equal("beta-user", entry.User);
        Assert.Equal(2200, entry.Port);
    }

    [Fact]
    public void DuplicateOption_FirstValueWins()
    {
        var entry = Assert.Single(Read(
            "Host gamma",
            "  User first-user",
            "  User second-user",
            "  Port 2200",
            "  Port 2201"));

        Assert.Equal("first-user", entry.User);
        Assert.Equal(2200, entry.Port);
    }

    [Fact]
    public void WildcardHostsAreExcludedAndMatchBlocksAreIgnored()
    {
        var entries = Read(
            "Host *.example.com",
            "  User wildcard-user",
            "Host real",
            "  HostName real.example.com",
            "Match host real",
            "  User matched-user",
            "  Port 9999");

        var entry = Assert.Single(entries);
        Assert.Equal("real", entry.Alias);
        Assert.Equal("real.example.com", entry.HostName);
        Assert.Null(entry.User);
        Assert.Equal(22, entry.Port);
    }
}
