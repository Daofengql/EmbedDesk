using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EmbedDesk.Protocols;

public enum CollaborationMessageType : byte
{
    PairRequest = 1,
    PairAccepted = 2,
    Authorize = 3,
    Revoke = 4,
    LeaseAcquire = 5,
    LeaseGranted = 6,
    LeaseRelease = 7,
    Heartbeat = 8,
    Data = 9,
    Close = 10
}

public enum CollaborationPeerRole : byte
{
    Owner = 1,
    Visitor = 2
}

public sealed record CollaborationFrame(CollaborationMessageType Type, ulong Sequence, ReadOnlyMemory<byte> Ciphertext, ReadOnlyMemory<byte> Tag, ReadOnlyMemory<byte> Nonce);

public sealed record CollaborationKeyPair(byte[] PrivateKey, byte[] PublicKey) : IDisposable
{
    public void Dispose() => CryptographicOperations.ZeroMemory(PrivateKey);
}

public sealed record CollaborationSessionKeys(byte[] OwnerToVisitor, byte[] VisitorToOwner) : IDisposable
{
    public CollaborationSecureChannel CreateChannel(CollaborationPeerRole role) => role switch
    {
        CollaborationPeerRole.Owner => new CollaborationSecureChannel(OwnerToVisitor, VisitorToOwner),
        CollaborationPeerRole.Visitor => new CollaborationSecureChannel(VisitorToOwner, OwnerToVisitor),
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(OwnerToVisitor);
        CryptographicOperations.ZeroMemory(VisitorToOwner);
    }
}

/// <summary>Protocol-only authorization state. A server can persist the opaque token hash and enforce these transitions.</summary>
public sealed class CollaborationAuthorization
{
    private readonly byte[] _tokenHash;
    private readonly object _gate = new();
    private DateTimeOffset _expiresAt;
    private bool _redeemed, _revoked;
    private string? _leaseOwner;
    private DateTimeOffset _leaseExpiresAt;

    private CollaborationAuthorization(byte[] token, DateTimeOffset expiresAt)
    {
        try
        {
            Token = Base64Url(token);
            _tokenHash = SHA256.HashData(token);
            _expiresAt = expiresAt;
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    public string Token { get; }
    public DateTimeOffset ExpiresAt { get { lock (_gate) return _expiresAt; } }
    public bool IsRevoked { get { lock (_gate) return _revoked; } }

    public static CollaborationAuthorization Create(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        return new CollaborationAuthorization(RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.Add(lifetime));
    }

    public bool TryRedeem(string token)
    {
        if (!TryDecodeBase64Url(token, out var bytes)) return false;
        var hash = SHA256.HashData(bytes);
        try
        {
            lock (_gate)
            {
                if (_redeemed || _revoked || DateTimeOffset.UtcNow >= _expiresAt || !CryptographicOperations.FixedTimeEquals(hash, _tokenHash)) return false;
                _redeemed = true;
                return true;
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(hash); }
    }

    public void Revoke()
    {
        lock (_gate) { _revoked = true; _leaseOwner = null; }
    }

    public bool TryAcquireLease(string owner, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(owner) || duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30)) return false;
        lock (_gate)
        {
            if (!_redeemed || _revoked || DateTimeOffset.UtcNow >= _expiresAt) return false;
            if (_leaseOwner is not null && DateTimeOffset.UtcNow < _leaseExpiresAt && !string.Equals(_leaseOwner, owner, StringComparison.Ordinal)) return false;
            _leaseOwner = owner; _leaseExpiresAt = DateTimeOffset.UtcNow.Add(duration); return true;
        }
    }

    public bool ReleaseLease(string owner)
    {
        lock (_gate)
        {
            if (!string.Equals(_leaseOwner, owner, StringComparison.Ordinal)) return false;
            _leaseOwner = null; return true;
        }
    }

    public bool IsLeaseValid(string owner)
    {
        lock (_gate) return !_revoked && DateTimeOffset.UtcNow < _expiresAt && string.Equals(_leaseOwner, owner, StringComparison.Ordinal) && DateTimeOffset.UtcNow < _leaseExpiresAt;
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        try
        {
            var text = value.Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + ((4 - text.Length % 4) % 4), '=');
            bytes = Convert.FromBase64String(text);
            if (bytes.Length == 32) return true;
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            return false;
        }
        catch (FormatException) { bytes = []; return false; }
    }
}

/// <summary>Transcript-bound proof of possession for the one-time pairing secret.</summary>
public static class CollaborationHandshake
{
    private static readonly byte[] TranscriptLabel = Encoding.UTF8.GetBytes("EmbedDesk serial collaboration transcript v2");
    private static readonly byte[] AuthenticationLabel = Encoding.UTF8.GetBytes("EmbedDesk serial collaboration handshake authentication v2");

    public static byte[] CreateTranscript(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> ownerPublicKey,
        ReadOnlySpan<byte> visitorPublicKey, ReadOnlySpan<byte> ownerNonce, ReadOnlySpan<byte> visitorNonce)
    {
        if (sessionId.Length != 16) throw new ArgumentException("协作会话 ID 必须为 16 字节。", nameof(sessionId));
        if (ownerPublicKey.IsEmpty || ownerPublicKey.Length > 1024) throw new ArgumentException("所有者公钥长度无效。", nameof(ownerPublicKey));
        if (visitorPublicKey.IsEmpty || visitorPublicKey.Length > 1024) throw new ArgumentException("访问者公钥长度无效。", nameof(visitorPublicKey));
        if (ownerNonce.Length != 32) throw new ArgumentException("所有者随机数必须为 32 字节。", nameof(ownerNonce));
        if (visitorNonce.Length != 32) throw new ArgumentException("访问者随机数必须为 32 字节。", nameof(visitorNonce));

        var transcript = new byte[TranscriptLabel.Length + 16 + 2 + ownerPublicKey.Length + 2 + visitorPublicKey.Length + 64];
        var offset = 0;
        TranscriptLabel.CopyTo(transcript, offset); offset += TranscriptLabel.Length;
        sessionId.CopyTo(transcript.AsSpan(offset)); offset += sessionId.Length;
        BinaryPrimitives.WriteUInt16BigEndian(transcript.AsSpan(offset, 2), (ushort)ownerPublicKey.Length); offset += 2;
        ownerPublicKey.CopyTo(transcript.AsSpan(offset)); offset += ownerPublicKey.Length;
        BinaryPrimitives.WriteUInt16BigEndian(transcript.AsSpan(offset, 2), (ushort)visitorPublicKey.Length); offset += 2;
        visitorPublicKey.CopyTo(transcript.AsSpan(offset)); offset += visitorPublicKey.Length;
        ownerNonce.CopyTo(transcript.AsSpan(offset)); offset += ownerNonce.Length;
        visitorNonce.CopyTo(transcript.AsSpan(offset));
        return transcript;
    }

    public static byte[] CreateProof(ReadOnlySpan<byte> pairingSecret, ReadOnlySpan<byte> transcript, CollaborationPeerRole role)
    {
        Validate(pairingSecret, transcript, role);
        var transcriptHash = SHA256.HashData(transcript);
        var pairingKeyMaterial = pairingSecret.ToArray();
        var authenticationKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, pairingKeyMaterial, 32, transcriptHash, AuthenticationLabel);
        try
        {
            Span<byte> authenticatedData = stackalloc byte[1 + 32];
            authenticatedData[0] = (byte)role;
            transcriptHash.CopyTo(authenticatedData[1..]);
            return HMACSHA256.HashData(authenticationKey, authenticatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pairingKeyMaterial);
            CryptographicOperations.ZeroMemory(authenticationKey);
            CryptographicOperations.ZeroMemory(transcriptHash);
        }
    }

    public static bool VerifyProof(ReadOnlySpan<byte> pairingSecret, ReadOnlySpan<byte> transcript,
        CollaborationPeerRole role, ReadOnlySpan<byte> proof)
    {
        if (proof.Length != 32) return false;
        var expected = CreateProof(pairingSecret, transcript, role);
        try { return CryptographicOperations.FixedTimeEquals(expected, proof); }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    public static bool TryDecodePairingSecret(string credential, out byte[] secret)
    {
        try
        {
            var value = credential.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            secret = Convert.FromBase64String(value);
            if (secret.Length == 32) return true;
            CryptographicOperations.ZeroMemory(secret);
        }
        catch (FormatException) { }
        secret = [];
        return false;
    }

    private static void Validate(ReadOnlySpan<byte> pairingSecret, ReadOnlySpan<byte> transcript, CollaborationPeerRole role)
    {
        if (pairingSecret.Length != 32) throw new ArgumentException("配对密钥必须为 256 位。", nameof(pairingSecret));
        if (transcript.IsEmpty || transcript.Length > 4096) throw new ArgumentException("握手 transcript 长度无效。", nameof(transcript));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
    }
}

public static class CollaborationKeyAgreement
{
    public static CollaborationKeyPair CreateKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new CollaborationKeyPair(ecdh.ExportPkcs8PrivateKey(), ecdh.ExportSubjectPublicKeyInfo());
    }

    public static CollaborationSessionKeys DeriveSessionKeys(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey,
        ReadOnlySpan<byte> pairingSecret, ReadOnlySpan<byte> transcript)
    {
        if (pairingSecret.Length != 32) throw new ArgumentException("配对密钥必须为 256 位。", nameof(pairingSecret));
        if (transcript.IsEmpty || transcript.Length > 4096) throw new ArgumentException("握手 transcript 长度无效。", nameof(transcript));
        using var local = ECDiffieHellman.Create();
        local.ImportPkcs8PrivateKey(privateKey, out _);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
        var shared = local.DeriveKeyMaterial(peer.PublicKey);
        var transcriptHash = SHA256.HashData(transcript);
        var salt = HMACSHA256.HashData(pairingSecret, transcriptHash);
        try
        {
            var master = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt,
                Encoding.UTF8.GetBytes("EmbedDesk serial collaboration session v2").Concat(transcriptHash).ToArray());
            try
            {
                var ownerToVisitor = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, transcriptHash, Encoding.UTF8.GetBytes("owner to visitor"));
                var visitorToOwner = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, transcriptHash, Encoding.UTF8.GetBytes("visitor to owner"));
                return new CollaborationSessionKeys(ownerToVisitor, visitorToOwner);
            }
            finally { CryptographicOperations.ZeroMemory(master); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(transcriptHash);
        }
    }
}

public sealed class CollaborationSecureChannel : IDisposable
{
    private const uint Magic = 0x45444353;
    private const byte Version = 1;
    private const int HeaderSize = 32;
    public const int MaximumPayloadSize = 1024 * 1024;
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly byte[] _sendNoncePrefix;
    private byte[]? _receiveNoncePrefix;
    private readonly object _sendSync = new();
    private readonly object _receiveSync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private ulong _sendSequence;
    private ulong _lastReceivedSequence;

    public CollaborationSecureChannel(ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey)
    {
        if (sendKey.Length != 32) throw new ArgumentException("协作发送密钥必须为 256 位。", nameof(sendKey));
        if (receiveKey.Length != 32) throw new ArgumentException("协作接收密钥必须为 256 位。", nameof(receiveKey));
        _sendKey = sendKey.ToArray();
        _receiveKey = receiveKey.ToArray();
        _sendNoncePrefix = RandomNumberGenerator.GetBytes(4);
    }

    public byte[] Encrypt(CollaborationMessageType type, ReadOnlySpan<byte> plaintext)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (plaintext.Length > MaximumPayloadSize) throw new ArgumentOutOfRangeException(nameof(plaintext), "协作帧超过最大负载长度。");
        lock (_sendSync)
        {
            var sequence = checked(_sendSequence + 1);
            Span<byte> nonce = stackalloc byte[12];
            _sendNoncePrefix.CopyTo(nonce);
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], sequence);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            var result = new byte[HeaderSize + ciphertext.Length + tag.Length];
            WriteHeader(result, type, sequence, nonce, ciphertext.Length);
            using var aes = new AesGcm(_sendKey, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, result.AsSpan(0, HeaderSize));
            ciphertext.CopyTo(result, HeaderSize);
            tag.CopyTo(result, HeaderSize + ciphertext.Length);
            _sendSequence = sequence;
            return result;
        }
    }

    /// <summary>Encrypts and writes one complete frame. Concurrent sends are serialized to preserve frame order.</summary>
    public async Task SendAsync(Stream stream, CollaborationMessageType type, ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var encoded = Encrypt(type, plaintext.Span);
            await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    /// <summary>Reads one bounded frame from a byte stream; returns null only for a clean EOF before a frame starts.</summary>
    public async Task<(CollaborationMessageType Type, ulong Sequence, byte[] Plaintext)?> ReceiveAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var header = new byte[HeaderSize];
            var firstByte = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (firstByte == 0) return null;
            await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            var payloadLength = ValidateHeader(header);
            var encoded = new byte[HeaderSize + payloadLength + 16];
            header.CopyTo(encoded, 0);
            await stream.ReadExactlyAsync(encoded.AsMemory(HeaderSize), cancellationToken).ConfigureAwait(false);
            return Decrypt(encoded);
        }
        finally { _receiveGate.Release(); }
    }

    public (CollaborationMessageType Type, ulong Sequence, byte[] Plaintext) Decrypt(ReadOnlySpan<byte> encoded)
    {
        lock (_receiveSync)
        {
            var length = ValidateHeader(encoded);
            if (encoded.Length != HeaderSize + length + 16) throw new InvalidDataException("协作帧长度无效。");
            var type = (CollaborationMessageType)encoded[5];
            var sequence = BinaryPrimitives.ReadUInt64BigEndian(encoded[8..16]);
            if (sequence != checked(_lastReceivedSequence + 1)) throw new InvalidDataException("协作帧序号不连续或已重放。");
            var nonce = encoded.Slice(16, 12);
            if (BinaryPrimitives.ReadUInt64BigEndian(nonce[4..]) != sequence
                || (_receiveNoncePrefix is not null && !CryptographicOperations.FixedTimeEquals(_receiveNoncePrefix, nonce[..4])))
                throw new InvalidDataException("协作帧 nonce 与连续序号不匹配。");
            var ciphertext = encoded.Slice(HeaderSize, length);
            var tag = encoded.Slice(HeaderSize + length, 16);
            var plaintext = new byte[length];
            using var aes = new AesGcm(_receiveKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, encoded[..HeaderSize]);
            _receiveNoncePrefix ??= nonce[..4].ToArray();
            _lastReceivedSequence = sequence;
            return (type, sequence, plaintext);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_sendKey);
        CryptographicOperations.ZeroMemory(_receiveKey);
        CryptographicOperations.ZeroMemory(_sendNoncePrefix);
        if (_receiveNoncePrefix is not null) CryptographicOperations.ZeroMemory(_receiveNoncePrefix);
        _sendGate.Dispose();
        _receiveGate.Dispose();
    }
    private static int ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || BinaryPrimitives.ReadUInt32BigEndian(header) != Magic
            || header[4] != Version || header[6] != 0 || header[7] != 0)
            throw new InvalidDataException("协作帧头无效。");
        if (!Enum.IsDefined((CollaborationMessageType)header[5])) throw new InvalidDataException("协作帧类型无效。");
        var length = BinaryPrimitives.ReadInt32BigEndian(header[28..32]);
        if (length < 0 || length > MaximumPayloadSize) throw new InvalidDataException("协作帧负载长度超出上限。");
        return length;
    }
    private static void WriteHeader(Span<byte> destination, CollaborationMessageType type, ulong sequence, ReadOnlySpan<byte> nonce, int length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, Magic); destination[4] = Version; destination[5] = (byte)type;
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..16], sequence); nonce.CopyTo(destination[16..28]); BinaryPrimitives.WriteInt32BigEndian(destination[28..32], length);
    }
}
