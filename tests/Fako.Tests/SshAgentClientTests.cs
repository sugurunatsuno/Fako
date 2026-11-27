using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Fako.Remote;
using Xunit;

namespace Fako.Tests;

/// <summary>
/// SSH エージェントとの通信（名前付きパイプ・プロトコル・パイプ名解決）。
/// 実エージェントやネットワークには触れず、インプロセスのモックサーバーだけを使う。
/// </summary>
public class SshAgentClientTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(2);

    private static string UniquePipeName() => $"fako-test-agent-{Guid.NewGuid():N}";

    private static NamedPipeServerStream CreateServer(string pipeName) =>
        new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task ServeOnceAsync(string pipeName, Func<NamedPipeServerStream, Task> handler)
    {
        using var server = CreateServer(pipeName);
        await server.WaitForConnectionAsync();
        await handler(server);
    }

    // ------------------------------------------------------------------
    // テスト用の SSH wire format
    // ------------------------------------------------------------------

    private static byte[] MakeKeyBlob(string keyType)
    {
        var type = Encoding.ASCII.GetBytes(keyType);
        var blob = new byte[4 + type.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)type.Length);
        type.CopyTo(blob, 4);
        for (int i = 0; i < 8; i++) blob[4 + type.Length + i] = (byte)(i + 1);
        return blob;
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    private static byte[] IdentitiesAnswer(params (string KeyType, string Comment)[] keys)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(OpenSshAgentClient.SSH_AGENT_IDENTITIES_ANSWER);
        WriteUInt32(ms, (uint)keys.Length);
        foreach (var (keyType, comment) in keys)
        {
            WriteString(ms, MakeKeyBlob(keyType));
            WriteString(ms, Encoding.UTF8.GetBytes(comment));
        }
        return ms.ToArray();
    }

    private static byte[] SignResponse(byte[] signature)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(OpenSshAgentClient.SSH_AGENT_SIGN_RESPONSE);
        WriteString(ms, signature);
        return ms.ToArray();
    }

    private static async Task WriteMessageAsync(Stream stream, ReadOnlyMemory<byte> payload)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task<byte[]> ReadMessageAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload);
        return payload;
    }

    private static byte[] ReadString(byte[] data, ref int offset)
    {
        uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        var value = data.AsSpan(offset, checked((int)length)).ToArray();
        offset += value.Length;
        return value;
    }

    // ------------------------------------------------------------------
    // RequestIdentities
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestIdentities_ParsesMultipleKeyTypes()
    {
        var pipeName = UniquePipeName();
        var keys = new[]
        {
            ("ssh-ed25519", "ed key"),
            ("ssh-rsa", "rsa key"),
            ("ecdsa-sha2-nistp256", ""),
            ("sk-ssh-ed25519@openssh.com", "security key"),
        };

        var serverTask = ServeOnceAsync(pipeName, async server =>
        {
            var request = await ReadMessageAsync(server);
            Assert.Equal(OpenSshAgentClient.SSH_AGENTC_REQUEST_IDENTITIES, request[0]);
            await WriteMessageAsync(server, IdentitiesAnswer(keys));
        });

        var client = new OpenSshAgentClient(pipeName, Fast, Fast, Fast);
        var identities = client.RequestIdentities();
        await serverTask;

        Assert.Equal(keys.Length, identities.Count);
        for (int i = 0; i < keys.Length; i++)
        {
            var (keyType, comment) = keys[i];
            var identity = identities[i];
            var fingerprint = OpenSshAgentClient.ComputeFingerprintSha256(MakeKeyBlob(keyType));
            var shortFingerprint = fingerprint.Length > 20 ? fingerprint[..20] + "…" : fingerprint;

            Assert.Equal(keyType, identity.KeyType);
            Assert.Equal(comment, identity.Comment);
            Assert.Equal(fingerprint, identity.FingerprintSha256);
            Assert.Equal(MakeKeyBlob(keyType), identity.KeyBlob);
            Assert.Equal(
                string.IsNullOrWhiteSpace(comment)
                    ? $"{keyType} {shortFingerprint}"
                    : $"{comment}（{keyType} {shortFingerprint}）",
                identity.DisplayLabel);
        }
    }

    [Fact]
    public async Task RequestIdentities_ReturnsEmptyListWhenAgentHasNoKeys()
    {
        var pipeName = UniquePipeName();
        var serverTask = ServeOnceAsync(pipeName, async server =>
        {
            await ReadMessageAsync(server);
            await WriteMessageAsync(server, IdentitiesAnswer());
        });

        var client = new OpenSshAgentClient(pipeName, Fast, Fast, Fast);
        var identities = client.RequestIdentities();
        await serverTask;

        Assert.Empty(identities);
    }

    [Fact]
    public async Task RequestIdentities_ThrowsOperationRefusedOnFailure()
    {
        var pipeName = UniquePipeName();
        var serverTask = ServeOnceAsync(pipeName, async server =>
        {
            await ReadMessageAsync(server);
            await WriteMessageAsync(server, new[] { OpenSshAgentClient.SSH_AGENT_FAILURE });
        });

        var client = new OpenSshAgentClient(pipeName, Fast, Fast, Fast);
        Assert.Throws<SshAgentOperationRefusedException>(() => { client.RequestIdentities(); });
        await serverTask;
    }

    // ------------------------------------------------------------------
    // Sign
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0u)]
    [InlineData(OpenSshAgentClient.SSH_AGENT_RSA_SHA2_256)]
    [InlineData(OpenSshAgentClient.SSH_AGENT_RSA_SHA2_512)]
    public async Task Sign_SendsProtocolFieldsAndReturnsSignature(uint flags)
    {
        var pipeName = UniquePipeName();
        var keyBlob = MakeKeyBlob("ssh-rsa");
        var data = Encoding.UTF8.GetBytes("data to sign");
        var signature = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };

        byte[]? receivedKeyBlob = null;
        byte[]? receivedData = null;
        uint receivedFlags = uint.MaxValue;

        var serverTask = ServeOnceAsync(pipeName, async server =>
        {
            var request = await ReadMessageAsync(server);
            Assert.Equal(OpenSshAgentClient.SSH_AGENTC_SIGN_REQUEST, request[0]);
            int offset = 1;
            receivedKeyBlob = ReadString(request, ref offset);
            receivedData = ReadString(request, ref offset);
            receivedFlags = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(offset, 4));
            await WriteMessageAsync(server, SignResponse(signature));
        });

        var client = new OpenSshAgentClient(pipeName, Fast, Fast, Fast);
        var result = client.Sign(keyBlob, data, flags);
        await serverTask;

        Assert.Equal(keyBlob, receivedKeyBlob);
        Assert.Equal(data, receivedData);
        Assert.Equal(flags, receivedFlags);
        Assert.Equal(signature, result);
    }

    [Fact]
    public async Task Sign_ThrowsOperationRefusedOnFailure()
    {
        var pipeName = UniquePipeName();
        var serverTask = ServeOnceAsync(pipeName, async server =>
        {
            await ReadMessageAsync(server);
            await WriteMessageAsync(server, new[] { OpenSshAgentClient.SSH_AGENT_FAILURE });
        });

        var client = new OpenSshAgentClient(pipeName, Fast, Fast, Fast);
        Assert.Throws<SshAgentOperationRefusedException>(() =>
        {
            client.Sign(MakeKeyBlob("ssh-ed25519"), new byte[] { 1, 2, 3 }, 0);
        });
        await serverTask;
    }

    // ------------------------------------------------------------------
    // 異常系・タイムアウト
    // ------------------------------------------------------------------

    [Fact]
    public void RequestIdentities_ThrowsUnavailableForMissingPipe()
    {
        var client = new OpenSshAgentClient(
            UniquePipeName(),
            connectTimeout: TimeSpan.FromMilliseconds(200),
            identityTimeout: Fast,
            signTimeout: Fast);

        Assert.Throws<SshAgentUnavailableException>(() => { client.RequestIdentities(); });
    }

    [Theory]
    [InlineData(0u)]
    [InlineData((uint)OpenSshAgentClient.MaxMessageLength + 1)]
    public async Task RequestIdentities_RejectsInvalidMessageLength(uint length)
    {
        var pipeName = UniquePipeName();
        var serverTask = ServeOnceAsync(pipeName, async server =>
        {
            await ReadMessageAsync(server);
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, length);
            await server.WriteAsync(header);
            await server.FlushAsync();
        });

        var client = new OpenSshAgentClient(pipeName, Fast, Fast, Fast);
        var ex = Assert.Throws<SshAgentException>(() => { client.RequestIdentities(); });
        await serverTask;

        Assert.IsNotType<SshAgentUnavailableException>(ex);
        Assert.IsNotType<SshAgentOperationRefusedException>(ex);
    }

    [Fact]
    public async Task RequestIdentities_ThrowsOnTruncatedResponse()
    {
        var pipeName = UniquePipeName();
        var serverTask = ServeOnceAsync(pipeName, async server =>
        {
            await ReadMessageAsync(server);
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, 16);
            await server.WriteAsync(header);
            await server.WriteAsync(new byte[] { 1, 2, 3, 4 });
            await server.FlushAsync();
            // ここでサーバーが切断する（残り 12 バイトが来ない）
        });

        var client = new OpenSshAgentClient(pipeName, Fast, Fast, Fast);
        Assert.Throws<SshAgentException>(() => { client.RequestIdentities(); });
        await serverTask;
    }

    [Fact]
    public async Task RequestIdentities_TimesOutWhenServerDoesNotRespond()
    {
        var pipeName = UniquePipeName();
        using var server = CreateServer(pipeName);
        var accepted = server.WaitForConnectionAsync();

        var client = new OpenSshAgentClient(
            pipeName,
            connectTimeout: Fast,
            identityTimeout: TimeSpan.FromMilliseconds(200),
            signTimeout: Fast);

        Assert.Throws<SshAgentException>(() => { client.RequestIdentities(); });
        await accepted;
    }

    // ------------------------------------------------------------------
    // パイプ名の解決・正規化
    // ------------------------------------------------------------------

    [Fact]
    public void ResolvePipePath_PrefersExplicitValue()
    {
        Assert.Equal(@"\\.\pipe\explicit",
            OpenSshAgentClient.ResolvePipePath(@"\\.\pipe\explicit", @"\\.\pipe\env"));
        Assert.Equal(@"\\.\pipe\explicit",
            OpenSshAgentClient.ResolvePipePath("  \"\\\\.\\pipe\\explicit\"  ", @"\\.\pipe\env"));
    }

    [Fact]
    public void ResolvePipePath_UsesEnvironmentWhenExplicitIsMissingOrSocket()
    {
        Assert.Equal(@"\\.\pipe\env", OpenSshAgentClient.ResolvePipePath(null, @"\\.\pipe\env"));
        Assert.Equal(@"\\.\pipe\env", OpenSshAgentClient.ResolvePipePath("   ", @"\\.\pipe\env"));
        Assert.Equal(@"\\.\pipe\env", OpenSshAgentClient.ResolvePipePath("SSH_AUTH_SOCK", @"\\.\pipe\env"));
        Assert.Equal(@"\\.\pipe\env", OpenSshAgentClient.ResolvePipePath("ssh_auth_sock", @"\\.\pipe\env"));
    }

    [Fact]
    public void ResolvePipePath_FallsBackToDefault()
    {
        Assert.Equal(OpenSshAgentClient.DefaultPipePath, OpenSshAgentClient.ResolvePipePath(null, null));
        Assert.Equal(OpenSshAgentClient.DefaultPipePath, OpenSshAgentClient.ResolvePipePath("  ", "  "));
    }

    [Fact]
    public void ResolvePipePath_NoneThrows()
    {
        Assert.Throws<SshAgentUnavailableException>(() => OpenSshAgentClient.ResolvePipePath("none", null));
        Assert.Throws<SshAgentUnavailableException>(() => OpenSshAgentClient.ResolvePipePath(" NONE ", null));
    }

    [Theory]
    [InlineData(@"\\.\pipe\openssh-ssh-agent", "openssh-ssh-agent")]
    [InlineData("//./pipe/openssh-ssh-agent", "openssh-ssh-agent")]
    [InlineData("openssh-ssh-agent", "openssh-ssh-agent")]
    [InlineData("  \"openssh-ssh-agent\"  ", "openssh-ssh-agent")]
    [InlineData("\"\\\\.\\pipe\\openssh-ssh-agent\"", "openssh-ssh-agent")]
    [InlineData(@"\\.\pipe\custom-agent", "custom-agent")]
    public void NormalizePipeName_AcceptsValidPaths(string path, string expected)
    {
        Assert.Equal(expected, OpenSshAgentClient.NormalizePipeName(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\.\pipe\")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"\\.\pipe\..")]
    [InlineData("sub..name")]
    [InlineData(@"C:\foo")]
    [InlineData(@"\\otherhost\pipe\x")]
    [InlineData(@"\\.\pipe\sub\name")]
    public void NormalizePipeName_RejectsInvalidPaths(string path)
    {
        Assert.Throws<SshAgentException>(() => OpenSshAgentClient.NormalizePipeName(path));
    }

    // ------------------------------------------------------------------
    // 公開鍵ファイルの指紋
    // ------------------------------------------------------------------

    [Fact]
    public void TryGetPublicKeyFingerprint_ReadsFirstKeyLine()
    {
        var dir = TestTemp.CreateDirectory();
        try
        {
            var keyBlob = MakeKeyBlob("ssh-ed25519");
            var path = Path.Combine(dir, "id_ed25519.pub");
            File.WriteAllText(path,
                "# コメント行はスキップ\n" +
                "\n" +
                $"ssh-ed25519 {Convert.ToBase64String(keyBlob)} me@example\n");

            Assert.Equal(
                OpenSshAgentClient.ComputeFingerprintSha256(keyBlob),
                OpenSshAgentClient.TryGetPublicKeyFingerprint(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryGetPublicKeyFingerprint_ReturnsNullForInvalidOrMissingFiles()
    {
        var dir = TestTemp.CreateDirectory();
        try
        {
            var commentOnly = Path.Combine(dir, "comment.pub");
            File.WriteAllText(commentOnly, "# ssh-ed25519 AAAA\n");

            var missingParts = Path.Combine(dir, "missing-parts.pub");
            File.WriteAllText(missingParts, "ssh-ed25519\n");

            var invalidBase64 = Path.Combine(dir, "invalid.pub");
            File.WriteAllText(invalidBase64, "ssh-ed25519 !!!not-base64!!!\n");

            Assert.Null(OpenSshAgentClient.TryGetPublicKeyFingerprint(commentOnly));
            Assert.Null(OpenSshAgentClient.TryGetPublicKeyFingerprint(missingParts));
            Assert.Null(OpenSshAgentClient.TryGetPublicKeyFingerprint(invalidBase64));
            Assert.Null(OpenSshAgentClient.TryGetPublicKeyFingerprint(Path.Combine(dir, "does-not-exist.pub")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // 利用者向けの文言
    // ------------------------------------------------------------------

    [Fact]
    public void Describe_ExplainsAgentFailuresInJapanese()
    {
        Assert.Contains("SSH エージェントに接続できません",
            RemoteUi.Describe(new SshAgentUnavailableException("詳細")));
        Assert.Contains("署名が拒否",
            RemoteUi.Describe(new SshAgentOperationRefusedException("詳細")));
        Assert.Equal("固有のメッセージ",
            RemoteUi.Describe(new SshAgentException("固有のメッセージ")));
    }
}
