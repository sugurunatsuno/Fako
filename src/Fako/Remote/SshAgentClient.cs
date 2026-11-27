using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Fako.Remote;

/// <summary>SSH エージェントとの通信に失敗した。</summary>
public class SshAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>SSH エージェントに接続できない（未起動・パイプ名違いなど）。</summary>
public sealed class SshAgentUnavailableException(string message, Exception? inner = null) : SshAgentException(message, inner);

/// <summary>SSH エージェントが操作を拒否した（鍵が無い・アプリがロック中など）。</summary>
public sealed class SshAgentOperationRefusedException(string message) : SshAgentException(message);

/// <summary>SSH エージェントが公開している 1 つの鍵。</summary>
public sealed record SshAgentIdentity(byte[] KeyBlob, string KeyType, string FingerprintSha256, string Comment)
{
    /// <summary>一覧表示用の短い指紋。</summary>
    public string ShortFingerprint =>
        FingerprintSha256.Length > 20 ? FingerprintSha256[..20] + "…" : FingerprintSha256;

    /// <summary>「コメント（ssh-ed25519 SHA256:…）」形式の表示名。</summary>
    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Comment) ? $"{KeyType} {ShortFingerprint}" : $"{Comment}（{KeyType} {ShortFingerprint}）";
}

/// <summary>SSH エージェントへの問い合わせ（テストで差し替えられるようにインターフェースにする）。</summary>
internal interface ISshAgentClient
{
    /// <summary>接続先の表示用パス。</summary>
    string EndpointDisplay { get; }

    /// <summary>エージェントに登録されている鍵の一覧を取得する。</summary>
    IReadOnlyList<SshAgentIdentity> RequestIdentities(CancellationToken ct = default);

    /// <summary>指定した鍵でデータに署名する。戻り値は SSH の署名 blob。</summary>
    byte[] Sign(byte[] keyBlob, byte[] data, uint flags, CancellationToken ct = default);
}

/// <summary>
/// Windows の名前付きパイプ経由で OpenSSH エージェント（RFC 9987 / draft-miller-ssh-agent）と話す。
/// 1 回の要求ごとに接続し、応答を受け取ったら閉じる。
/// </summary>
internal sealed class OpenSshAgentClient : ISshAgentClient
{
    public const string DefaultPipePath = @"\\.\pipe\openssh-ssh-agent";

    public const byte SSH_AGENT_FAILURE = 5;
    public const byte SSH_AGENTC_REQUEST_IDENTITIES = 11;
    public const byte SSH_AGENT_IDENTITIES_ANSWER = 12;
    public const byte SSH_AGENTC_SIGN_REQUEST = 13;
    public const byte SSH_AGENT_SIGN_RESPONSE = 14;

    public const uint SSH_AGENT_RSA_SHA2_256 = 0x02u;
    public const uint SSH_AGENT_RSA_SHA2_512 = 0x04u;

    /// <summary>1 メッセージの上限（壊れた／悪意ある応答でメモリを食い尽くさない）。</summary>
    public const int MaxMessageLength = 1024 * 1024;

    /// <summary>SSH_AGENTC_REQUEST_IDENTITIES (length=1, type=11) の固定フレーム。</summary>
    private static readonly byte[] RequestIdentitiesFrame = [0, 0, 0, 1, SSH_AGENTC_REQUEST_IDENTITIES];

    private readonly string _pipePath;
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _identityTimeout;
    private readonly TimeSpan _signTimeout;

    public string EndpointDisplay => _pipePath;

    public OpenSshAgentClient(string pipePath, TimeSpan? connectTimeout = null,
        TimeSpan? identityTimeout = null, TimeSpan? signTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(pipePath))
            throw new SshAgentUnavailableException("SSH エージェントのパイプが指定されていません。");

        _pipePath = pipePath.Trim();
        _pipeName = NormalizePipeName(pipePath);
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _identityTimeout = identityTimeout ?? TimeSpan.FromSeconds(10);
        _signTimeout = signTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>設定値からクライアントを作る（null なら環境変数と既定のパイプを使う）。</summary>
    public static OpenSshAgentClient Create(string? explicitPipe = null, string? envSshAuthSock = null) =>
        new(ResolvePipePath(explicitPipe, envSshAuthSock));

    /// <summary>
    /// 使うパイプのパスを決める。
    /// 明示指定（"none" と "SSH_AUTH_SOCK" を除く）→ 環境変数 SSH_AUTH_SOCK → Windows の既定パイプ。
    /// </summary>
    public static string ResolvePipePath(string? explicitPipe, string? envSshAuthSock = null)
    {
        var explicitValue = explicitPipe?.Trim().Trim('"', '\'').Trim();
        if (!string.IsNullOrEmpty(explicitValue))
        {
            if (explicitValue.Equals("none", StringComparison.OrdinalIgnoreCase))
                throw new SshAgentUnavailableException("SSH エージェントの使用が無効に設定されています（none）。");
            if (!explicitValue.Equals("SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase))
                return explicitValue;
        }

        envSshAuthSock ??= Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(envSshAuthSock)) return envSshAuthSock.Trim();
        return DefaultPipePath;
    }

    /// <summary>
    /// パイプのパスから名前付きパイプ名（\\.\pipe\ より後ろ）を取り出す。
    /// 単純な名前もそのまま受け付ける。不正なパスは <see cref="SshAgentException"/>。
    /// </summary>
    public static string NormalizePipeName(string pipePath)
    {
        if (string.IsNullOrWhiteSpace(pipePath))
            throw new SshAgentException("SSH エージェントのパイプ名が空です。");

        var name = pipePath.Trim().Trim('"', '\'').Trim();
        const string ntPrefix = @"\\.\pipe\";
        const string slashPrefix = "//./pipe/";
        if (name.StartsWith(ntPrefix, StringComparison.OrdinalIgnoreCase))
            name = name[ntPrefix.Length..];
        else if (name.StartsWith(slashPrefix, StringComparison.OrdinalIgnoreCase))
            name = name[slashPrefix.Length..];

        if (name.Length == 0)
            throw new SshAgentException("SSH エージェントのパイプ名が空です。");
        if (name is "." or ".." || name.Contains("..", StringComparison.Ordinal))
            throw new SshAgentException($"SSH エージェントのパイプ名が不正です（{pipePath}）。");
        if (name.IndexOfAny(['/', '\\']) >= 0)
            throw new SshAgentException($"SSH エージェントのパイプ名が不正です（{pipePath}）。");

        return name;
    }

    /// <summary>鍵 blob の SHA256 指紋（OpenSSH と同じ "SHA256:..." 形式）。</summary>
    public static string ComputeFingerprintSha256(byte[] keyBlob)
    {
        ArgumentNullException.ThrowIfNull(keyBlob);
        return ComputeFingerprintSha256(keyBlob.AsSpan());
    }

    /// <summary>鍵 blob の SHA256 指紋をスタックバッファで計算する（中間配列・中間文字列を省く）。</summary>
    public static string ComputeFingerprintSha256(ReadOnlySpan<byte> keyBlob)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(keyBlob, hash);
        Span<char> base64 = stackalloc char[44];
        Convert.TryToBase64Chars(hash, base64, out int charsWritten);
        while (charsWritten > 0 && base64[charsWritten - 1] == '=') charsWritten--;
        return string.Concat("SHA256:", base64[..charsWritten]);
    }

    /// <summary>鍵 blob 先頭の SSH wire format 文字列（ssh-ed25519 など）を読む。</summary>
    public static string ReadKeyType(byte[] keyBlob)
    {
        if (keyBlob is null)
            throw new SshAgentException("SSH エージェントから受け取った鍵データが不正です。");
        return ReadKeyType(keyBlob.AsSpan());
    }

    /// <summary>鍵 blob 先頭の SSH wire format 文字列（ssh-ed25519 など）を読む。</summary>
    public static string ReadKeyType(ReadOnlySpan<byte> keyBlob)
    {
        if (keyBlob.Length < 4)
            throw new SshAgentException("SSH エージェントから受け取った鍵データが不正です。");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(keyBlob);
        if (length == 0 || length > (uint)(keyBlob.Length - 4))
            throw new SshAgentException("SSH エージェントから受け取った鍵データが不正です。");
        return Encoding.ASCII.GetString(keyBlob.Slice(4, (int)length));
    }

    /// <summary>
    /// 公開鍵ファイル（.pub）の 1 行目から指紋を計算する。読めない／形式が違う場合は null。
    /// Phase 2 の ~/.ssh/config 連携で IdentityFile とエージェント内の鍵を照合するために使う。
    /// </summary>
    public static string? TryGetPublicKeyFingerprint(string pubKeyPath)
    {
        try
        {
            if (!File.Exists(pubKeyPath)) return null;
            var line = File.ReadLines(pubKeyPath)
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));
            if (line == null) return null;

            var parts = line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            return ComputeFingerprintSha256(Convert.FromBase64String(parts[1]));
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 要求
    // ------------------------------------------------------------------

    public IReadOnlyList<SshAgentIdentity> RequestIdentities(CancellationToken ct = default) =>
        RequestIdentitiesAsync(ct).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<SshAgentIdentity>> RequestIdentitiesAsync(CancellationToken ct)
    {
        using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
        byte[]? responseBuffer = null;
        try
        {
            await WriteAllAsync(pipe, RequestIdentitiesFrame, _identityTimeout, ct).ConfigureAwait(false);
            (responseBuffer, int responseLength) = await ReadMessagePooledAsync(pipe, _identityTimeout, ct).ConfigureAwait(false);
            var response = responseBuffer.AsSpan(0, responseLength);

            return response[0] switch
            {
                SSH_AGENT_IDENTITIES_ANSWER => ParseIdentities(response),
                SSH_AGENT_FAILURE => throw new SshAgentOperationRefusedException(
                    "SSH エージェントが鍵一覧の取得を拒否しました。"),
                _ => throw new SshAgentException(
                    $"SSH エージェントから予期しない応答を受信しました（type={response[0]}）。"),
            };
        }
        catch (SshAgentException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
        {
            throw new SshAgentException("SSH エージェントとの通信に失敗しました。", ex);
        }
        finally
        {
            if (responseBuffer != null) ArrayPool<byte>.Shared.Return(responseBuffer);
        }
    }

    public byte[] Sign(byte[] keyBlob, byte[] data, uint flags, CancellationToken ct = default) =>
        SignAsync(keyBlob, data, flags, ct).GetAwaiter().GetResult();

    private async Task<byte[]> SignAsync(byte[] keyBlob, byte[] data, uint flags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyBlob);
        ArgumentNullException.ThrowIfNull(data);

        long total = 1L + 4 + keyBlob.Length + 4 + data.Length + 4;
        if (keyBlob.Length > MaxMessageLength || data.Length > MaxMessageLength || total > MaxMessageLength)
            throw new SshAgentException("署名するデータが大きすぎます。");

        int frameLength = 4 + (int)total;
        var frameBuffer = ArrayPool<byte>.Shared.Rent(frameLength);
        byte[]? responseBuffer = null;
        try
        {
            int offset = 0;
            BinaryPrimitives.WriteUInt32BigEndian(frameBuffer.AsSpan(offset, 4), (uint)total);
            offset += 4;
            frameBuffer[offset++] = SSH_AGENTC_SIGN_REQUEST;
            WriteString(frameBuffer, ref offset, keyBlob);
            WriteString(frameBuffer, ref offset, data);
            BinaryPrimitives.WriteUInt32BigEndian(frameBuffer.AsSpan(offset, 4), flags);

            using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
            try
            {
                await WriteAllAsync(pipe, frameBuffer.AsMemory(0, frameLength), _signTimeout, ct).ConfigureAwait(false);
                (responseBuffer, int responseLength) = await ReadMessagePooledAsync(pipe, _signTimeout, ct).ConfigureAwait(false);
                var response = responseBuffer.AsSpan(0, responseLength);

                switch (response[0])
                {
                    case SSH_AGENT_SIGN_RESPONSE:
                        offset = 1;
                        return ReadString(response, ref offset);
                    case SSH_AGENT_FAILURE:
                        throw new SshAgentOperationRefusedException("SSH エージェントによって署名が拒否されました。");
                    default:
                        throw new SshAgentException(
                            $"SSH エージェントから予期しない応答を受信しました（type={response[0]}）。");
                }
            }
            catch (SshAgentException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
            {
                throw new SshAgentException("SSH エージェントとの通信に失敗しました。", ex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frameBuffer);
            if (responseBuffer != null) ArrayPool<byte>.Shared.Return(responseBuffer);
        }
    }

    // ------------------------------------------------------------------
    // 通信
    // ------------------------------------------------------------------

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            var timeoutMs = Math.Max(1, (int)_connectTimeout.TotalMilliseconds);
            await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            return pipe;
        }
        catch (TimeoutException ex)
        {
            pipe.Dispose();
            throw new SshAgentUnavailableException(
                $"SSH エージェント（{EndpointDisplay}）への接続がタイムアウトしました。", ex);
        }
        catch (FileNotFoundException ex)
        {
            pipe.Dispose();
            throw new SshAgentUnavailableException(
                $"SSH エージェント（{EndpointDisplay}）が見つかりません。", ex);
        }
        catch (IOException ex)
        {
            pipe.Dispose();
            throw new SshAgentUnavailableException(
                $"SSH エージェント（{EndpointDisplay}）に接続できません。", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            pipe.Dispose();
            throw new SshAgentUnavailableException(
                $"SSH エージェント（{EndpointDisplay}）への接続が許可されませんでした。", ex);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static async Task WriteAllAsync(Stream stream, ReadOnlyMemory<byte> buffer,
        TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await stream.WriteAsync(buffer, ct).AsTask().WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new SshAgentException("SSH エージェントへの送信がタイムアウトしました。", ex);
        }
    }

    private static async Task<(byte[] Buffer, int Length)> ReadMessagePooledAsync(Stream pipe, TimeSpan timeout, CancellationToken ct)
    {
        var header = ArrayPool<byte>.Shared.Rent(4);
        uint length;
        try
        {
            await ReadExactlyAsync(pipe, header.AsMemory(0, 4), timeout, ct).ConfigureAwait(false);
            length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        if (length == 0 || length > MaxMessageLength)
            throw new SshAgentException($"SSH エージェントからの応答の長さが不正です（{length} バイト）。");

        var payload = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            await ReadExactlyAsync(pipe, payload.AsMemory(0, (int)length), timeout, ct).ConfigureAwait(false);
            return (payload, (int)length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(payload);
            throw;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer,
        TimeSpan timeout, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buffer[read..], ct).AsTask().WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new SshAgentException(
                    "SSH エージェントからの応答がタイムアウトしました。Bitwarden などのアプリが応答しているか確認してください。",
                    ex);
            }

            if (n == 0)
                throw new SshAgentException("SSH エージェントからの応答が途中で途切れました。");
            read += n;
        }
    }

    // ------------------------------------------------------------------
    // SSH wire format
    // ------------------------------------------------------------------

    private static List<SshAgentIdentity> ParseIdentities(ReadOnlySpan<byte> response)
    {
        int offset = 1;
        uint count = ReadUInt32(response, ref offset);
        if (count > MaxMessageLength / 8 || count > (uint)((response.Length - offset) / 8))
            throw new SshAgentException($"SSH エージェントから受け取った鍵の数が不正です（{count} 件）。");

        var identities = new List<SshAgentIdentity>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var keyBlob = ReadString(response, ref offset);
            var comment = ReadUtf8String(response, ref offset);
            var keyType = ReadKeyType(keyBlob.AsSpan());
            identities.Add(new SshAgentIdentity(keyBlob, keyType, ComputeFingerprintSha256(keyBlob.AsSpan()), comment));
        }
        return identities;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static byte[] ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var slice = ReadStringSpan(data, ref offset);
        return slice.ToArray();
    }

    private static string ReadUtf8String(ReadOnlySpan<byte> data, ref int offset)
    {
        var slice = ReadStringSpan(data, ref offset);
        return slice.IsEmpty ? "" : Encoding.UTF8.GetString(slice);
    }

    private static ReadOnlySpan<byte> ReadStringSpan(ReadOnlySpan<byte> data, ref int offset)
    {
        uint length = ReadUInt32(data, ref offset);
        if (length > MaxMessageLength || length > (uint)(data.Length - offset))
            throw new SshAgentException("SSH エージェントから受け取ったデータが不正です。");
        var slice = data.Slice(offset, (int)length);
        offset += (int)length;
        return slice;
    }

    private static void WriteString(byte[] buffer, ref int offset, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), (uint)value.Length);
        offset += 4;
        value.CopyTo(buffer.AsSpan(offset));
        offset += value.Length;
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count)
            throw new SshAgentException("SSH エージェントから受け取ったデータが不正です。");
    }
}
