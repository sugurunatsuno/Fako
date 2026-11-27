using Renci.SshNet;
using Renci.SshNet.Security;

namespace Fako.Remote;

/// <summary>
/// SSH エージェント内の 1 つの鍵を、SSH.NET の公開鍵認証で使えるようにする。
/// RSA は SHA-2 の署名アルゴリズムを優先し、最後に従来の ssh-rsa も試す。
/// </summary>
internal sealed class SshAgentPrivateKeySource : IPrivateKeySource
{
    private readonly HostAlgorithm[] _algorithms;

    public SshAgentIdentity Identity { get; }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => _algorithms;

    public SshAgentPrivateKeySource(ISshAgentClient client, SshAgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(identity);

        Identity = identity;
        _algorithms = identity.KeyType == "ssh-rsa"
            // RFC 8332 / OpenSSH: サーバーが対応している新しい署名方式から順に試す。
            ? [
                new SshAgentHostAlgorithm("rsa-sha2-512", identity, client, OpenSshAgentClient.SSH_AGENT_RSA_SHA2_512),
                new SshAgentHostAlgorithm("rsa-sha2-256", identity, client, OpenSshAgentClient.SSH_AGENT_RSA_SHA2_256),
                new SshAgentHostAlgorithm("ssh-rsa", identity, client, 0),
              ]
            : [new SshAgentHostAlgorithm(identity.KeyType, identity, client, 0)];
    }
}

/// <summary>SSH エージェントに署名を依頼する <see cref="HostAlgorithm"/>。</summary>
internal sealed class SshAgentHostAlgorithm : HostAlgorithm
{
    private readonly SshAgentIdentity _identity;
    private readonly ISshAgentClient _client;
    private readonly uint _signFlags;

    public SshAgentHostAlgorithm(string name, SshAgentIdentity identity, ISshAgentClient client, uint signFlags)
        : base(name)
    {
        _identity = identity;
        _client = client;
        _signFlags = signFlags;
    }

    public override byte[] Data => _identity.KeyBlob;

    public override byte[] Sign(byte[] data) => _client.Sign(_identity.KeyBlob, data, _signFlags);

    public override bool VerifySignature(byte[] data, byte[] signature) =>
        throw new NotSupportedException();
}
