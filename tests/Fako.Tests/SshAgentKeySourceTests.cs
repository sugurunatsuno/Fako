using Fako.Models;
using Fako.Remote;
using Renci.SshNet.Security;
using Xunit;

namespace Fako.Tests;

/// <summary>
/// エージェント内の鍵を SSH.NET の公開鍵認証へ橋渡しする部分と、
/// RemoteSession がサーバーへ接続する前に行う検証。
/// </summary>
public class SshAgentKeySourceTests
{
    private sealed class FakeAgentClient : ISshAgentClient
    {
        public IReadOnlyList<SshAgentIdentity> Identities { get; init; } = Array.Empty<SshAgentIdentity>();

        public List<(byte[] KeyBlob, byte[] Data, uint Flags)> Calls { get; } = new();

        public string EndpointDisplay => "fake-agent";

        public IReadOnlyList<SshAgentIdentity> RequestIdentities(CancellationToken ct = default) => Identities;

        public byte[] Sign(byte[] keyBlob, byte[] data, uint flags, CancellationToken ct = default)
        {
            Calls.Add((keyBlob, data, flags));
            return new byte[] { 0x53, 0x49, 0x47 };
        }
    }

    private static byte[] MakeKeyBlob(string keyType)
    {
        var type = System.Text.Encoding.ASCII.GetBytes(keyType);
        var blob = new byte[4 + type.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)type.Length);
        type.CopyTo(blob, 4);
        return blob;
    }

    private static SshAgentIdentity Identity(string keyType, string fingerprint = "SHA256:test") =>
        new(MakeKeyBlob(keyType), keyType, fingerprint, keyType + " comment");

    [Fact]
    public void RsaIdentity_OffersSha2ThenLegacyAndDelegatesSigning()
    {
        var client = new FakeAgentClient();
        var identity = Identity("ssh-rsa");
        var source = new SshAgentPrivateKeySource(client, identity);

        var algorithms = source.HostKeyAlgorithms.ToArray();

        Assert.Equal(
            new[] { "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa" },
            algorithms.Select(a => a.Name));

        var data = new byte[] { 1, 2, 3 };
        foreach (var algorithm in algorithms) algorithm.Sign(data);

        Assert.Equal(new uint[] { 0x04, 0x02, 0x00 }, client.Calls.Select(c => c.Flags));
        Assert.All(client.Calls, call =>
        {
            Assert.Equal(identity.KeyBlob, call.KeyBlob);
            Assert.Equal(data, call.Data);
        });
    }

    [Theory]
    [InlineData("ssh-ed25519")]
    [InlineData("ecdsa-sha2-nistp256")]
    [InlineData("sk-ssh-ed25519@openssh.com")]
    public void NonRsaIdentity_OffersSingleAlgorithmWithoutFlags(string keyType)
    {
        var client = new FakeAgentClient();
        var identity = Identity(keyType);
        var source = new SshAgentPrivateKeySource(client, identity);

        var algorithm = Assert.Single(source.HostKeyAlgorithms);
        Assert.Equal(keyType, algorithm.Name);

        var data = new byte[] { 9, 8, 7 };
        algorithm.Sign(data);

        var call = Assert.Single(client.Calls);
        Assert.Equal(0u, call.Flags);
        Assert.Equal(identity.KeyBlob, call.KeyBlob);
        Assert.Equal(data, call.Data);
    }

    [Fact]
    public void Algorithm_ExposesKeyBlobAndDoesNotVerifyLocally()
    {
        var client = new FakeAgentClient();
        var identity = Identity("ssh-ed25519");
        var algorithm = Assert.Single(new SshAgentPrivateKeySource(client, identity).HostKeyAlgorithms);

        Assert.Same(identity.KeyBlob, algorithm.Data);
        Assert.Throws<NotSupportedException>(() =>
        {
            algorithm.VerifySignature(new byte[] { 1 }, new byte[] { 2 });
        });
    }

    [Fact]
    public async Task RemoteSession_ThrowsWhenAgentHasNoKeys()
    {
        var client = new FakeAgentClient();
        var cfg = new RemoteConfig
        {
            Host = "example.invalid",
            Port = 22,
            User = "tester",
            Auth = RemoteAuth.Agent,
        };

        using var session = new RemoteSession(cfg, new Dictionary<string, string>());
        session.AgentClientFactory = _ => client;

        var ex = await Assert.ThrowsAsync<SshAgentException>(() => session.ConnectAsync(null));

        Assert.Contains("鍵が登録されていません", ex.Message);
    }

    [Fact]
    public async Task RemoteSession_ThrowsWhenFingerprintDoesNotMatch()
    {
        var client = new FakeAgentClient
        {
            Identities = new[] { Identity("ssh-ed25519", "SHA256:actual") },
        };
        var cfg = new RemoteConfig
        {
            Host = "example.invalid",
            Port = 22,
            User = "tester",
            Auth = RemoteAuth.Agent,
            AgentKeyFingerprint = "SHA256:different",
        };

        using var session = new RemoteSession(cfg, new Dictionary<string, string>());
        session.AgentClientFactory = _ => client;

        var ex = await Assert.ThrowsAsync<SshAgentException>(() => session.ConnectAsync(null));

        Assert.Contains("見つかりません", ex.Message);
        Assert.Contains("SHA256:different", ex.Message);
    }
}
