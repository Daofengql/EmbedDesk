using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using EmbedDesk.Protocols;

namespace EmbedDesk.App.SmokeTests;

internal static class CollaborationProtocolChecks
{
    public static async Task RunAsync()
    {
        using var owner = CollaborationKeyAgreement.CreateKeyPair();
        using var visitor = CollaborationKeyAgreement.CreateKeyPair();
        var pairingSecret = RandomNumberGenerator.GetBytes(32);
        var transcript = CollaborationHandshake.CreateTranscript(RandomNumberGenerator.GetBytes(16), owner.PublicKey,
            visitor.PublicKey, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var ownerProof = CollaborationHandshake.CreateProof(pairingSecret, transcript, CollaborationPeerRole.Owner);
        var visitorProof = CollaborationHandshake.CreateProof(pairingSecret, transcript, CollaborationPeerRole.Visitor);
        Require(CollaborationHandshake.VerifyProof(pairingSecret, transcript, CollaborationPeerRole.Owner, ownerProof)
            && CollaborationHandshake.VerifyProof(pairingSecret, transcript, CollaborationPeerRole.Visitor, visitorProof),
            "both participants authenticate the complete handshake transcript");
        Require(!CollaborationHandshake.VerifyProof(pairingSecret, transcript, CollaborationPeerRole.Visitor, ownerProof),
            "handshake proofs are bound to the participant role");
        var changedTranscript = transcript.ToArray(); changedTranscript[^1] ^= 1;
        Require(!CollaborationHandshake.VerifyProof(pairingSecret, changedTranscript, CollaborationPeerRole.Owner, ownerProof),
            "handshake proof rejects a changed transcript");
        Require(CollaborationHandshake.TryDecodePairingSecret(Convert.ToBase64String(pairingSecret).TrimEnd('=').Replace('+', '-').Replace('/', '_'), out var decodedSecret)
            && decodedSecret.SequenceEqual(pairingSecret), "pairing credentials decode to the 256-bit secret");

        using var ownerKeys = CollaborationKeyAgreement.DeriveSessionKeys(owner.PrivateKey, visitor.PublicKey, pairingSecret, transcript);
        using var visitorKeys = CollaborationKeyAgreement.DeriveSessionKeys(visitor.PrivateKey, owner.PublicKey, pairingSecret, transcript);
        Require(ownerKeys.OwnerToVisitor.SequenceEqual(visitorKeys.OwnerToVisitor)
            && ownerKeys.VisitorToOwner.SequenceEqual(visitorKeys.VisitorToOwner)
            && !ownerKeys.OwnerToVisitor.SequenceEqual(ownerKeys.VisitorToOwner), "ECDH and pairing secret derive distinct directional keys");
        using var sender = ownerKeys.CreateChannel(CollaborationPeerRole.Owner);
        using var receiver = visitorKeys.CreateChannel(CollaborationPeerRole.Visitor);
        var encoded = sender.Encrypt(CollaborationMessageType.Data, Encoding.UTF8.GetBytes("serial bytes"));
        var decoded = receiver.Decrypt(encoded);
        Require(decoded.Type == CollaborationMessageType.Data && Encoding.UTF8.GetString(decoded.Plaintext) == "serial bytes" && decoded.Sequence == 1,
            "AES-GCM collaboration frame round-trips authenticated payloads");
        var tampered = encoded.ToArray(); tampered[^1] ^= 1;
        using var tamperReceiver = visitorKeys.CreateChannel(CollaborationPeerRole.Visitor);
        RequireThrows<CryptographicException>(() => tamperReceiver.Decrypt(tampered), "tampered collaboration frames are rejected");
        var nonceTampered = encoded.ToArray(); nonceTampered[27] ^= 1;
        using var nonceReceiver = visitorKeys.CreateChannel(CollaborationPeerRole.Visitor);
        RequireThrows<InvalidDataException>(() => nonceReceiver.Decrypt(nonceTampered), "collaboration nonces must encode the authenticated frame sequence");
        RequireThrows<InvalidDataException>(() => receiver.Decrypt(encoded), "replayed collaboration frame is rejected");

        using var gapSender = ownerKeys.CreateChannel(CollaborationPeerRole.Owner);
        using var gapReceiver = visitorKeys.CreateChannel(CollaborationPeerRole.Visitor);
        _ = gapSender.Encrypt(CollaborationMessageType.Heartbeat, []);
        var secondFrame = gapSender.Encrypt(CollaborationMessageType.Heartbeat, []);
        RequireThrows<InvalidDataException>(() => gapReceiver.Decrypt(secondFrame), "skipped collaboration frame sequence is rejected");
        RequireThrows<ArgumentOutOfRangeException>(() => sender.Encrypt(CollaborationMessageType.Data, new byte[CollaborationSecureChannel.MaximumPayloadSize + 1]),
            "oversized collaboration frames are rejected before encoding");

        var wrongPairingSecret = RandomNumberGenerator.GetBytes(32);
        Require(!CollaborationHandshake.VerifyProof(wrongPairingSecret, transcript, CollaborationPeerRole.Owner, ownerProof),
            "a different pairing secret cannot authenticate the handshake");
        using var wrongKeys = CollaborationKeyAgreement.DeriveSessionKeys(visitor.PrivateKey, owner.PublicKey, wrongPairingSecret, transcript);
        using var wrongReceiver = wrongKeys.CreateChannel(CollaborationPeerRole.Visitor);
        RequireThrows<CryptographicException>(() => wrongReceiver.Decrypt(encoded), "a different pairing secret cannot decrypt collaboration frames");

        var authorization = CollaborationAuthorization.Create(TimeSpan.FromMinutes(5));
        Require(!authorization.TryAcquireLease("owner", TimeSpan.FromMinutes(2)), "collaboration lease cannot be acquired before one-time pairing authorization is redeemed");
        Require(authorization.TryRedeem(authorization.Token), "one-time collaboration token can be redeemed once");
        Require(!authorization.TryRedeem(authorization.Token), "collaboration token cannot be replayed");
        Require(authorization.TryAcquireLease("owner", TimeSpan.FromMinutes(2)), "authorized owner acquires the serial lease");
        Require(!authorization.TryAcquireLease("visitor", TimeSpan.FromMinutes(2)), "second participant cannot steal an active serial lease");
        Require(authorization.ReleaseLease("owner") && authorization.TryAcquireLease("visitor", TimeSpan.FromMinutes(2)), "lease release allows a new participant");
        authorization.Revoke();
        Require(!authorization.IsLeaseValid("visitor") && !authorization.TryAcquireLease("owner", TimeSpan.FromMinutes(1)), "revocation releases and blocks future leases");

        var definition = new StcpTunnelDefinition { Name = "lab-ssh", Role = StcpTunnelRole.Visitor, ServerAddress = "HOST", ServerPort = 7000, Secret = new string('x', 32), ServiceName = "ssh", TargetHost = "127.0.0.1", TargetPort = 22, LocalPort = 22022, MaxBytesPerSecond = 128 * 1024 };
        var json = StcpProtocol.Serialize(definition);
        var restored = StcpProtocol.Deserialize(json);
        Require(restored == definition && !json.Contains("password", StringComparison.OrdinalIgnoreCase), "STCP configuration round-trips with explicit target and limit");
        RequireThrows<ArgumentException>(() => StcpProtocol.Validate(definition with { Secret = "short" }), "STCP rejects weak tunnel secrets");
        RequireThrows<ArgumentException>(() => StcpProtocol.ValidateProfile(definition with { ServiceName = string.Empty }), "STCP visitor requires a service name");

        using var streamSender = ownerKeys.CreateChannel(CollaborationPeerRole.Owner);
        using var streamReceiver = visitorKeys.CreateChannel(CollaborationPeerRole.Visitor);
        using var wire = new MemoryStream();
        await streamSender.SendAsync(wire, CollaborationMessageType.Data, Encoding.UTF8.GetBytes("fragmented serial stream"));
        using var fragmented = new ChunkedReadStream(wire.ToArray(), 3);
        var streamed = await streamReceiver.ReceiveAsync(fragmented);
        Require(streamed is { Type: CollaborationMessageType.Data, Sequence: 1 }
            && Encoding.UTF8.GetString(streamed.Value.Plaintext) == "fragmented serial stream",
            "collaboration wire reader handles fragmented stream reads and authenticates the complete frame");
        Require(await streamReceiver.ReceiveAsync(fragmented) is null, "collaboration wire reader distinguishes clean EOF before a frame");

        using var truncatedReceiver = visitorKeys.CreateChannel(CollaborationPeerRole.Visitor);
        using var truncated = new ChunkedReadStream(wire.ToArray()[..20], 2);
        await RequireThrowsAsync<EndOfStreamException>(() => truncatedReceiver.ReceiveAsync(truncated),
            "truncated collaboration headers fail instead of being accepted as partial frames");

        var oversizedHeader = wire.ToArray()[..32];
        BinaryPrimitives.WriteInt32BigEndian(oversizedHeader.AsSpan(28, 4), CollaborationSecureChannel.MaximumPayloadSize + 1);
        using var oversizedReceiver = visitorKeys.CreateChannel(CollaborationPeerRole.Visitor);
        using var oversized = new ChunkedReadStream(oversizedHeader, 32);
        await RequireThrowsAsync<InvalidDataException>(() => oversizedReceiver.ReceiveAsync(oversized),
            "collaboration wire reader rejects oversized lengths before allocating a payload");
        CryptographicOperations.ZeroMemory(pairingSecret);
        CryptographicOperations.ZeroMemory(wrongPairingSecret);
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
        catch (T) { Console.WriteLine($"PASS {message}"); }
    }
    private static async Task RequireThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
        catch (T) { Console.WriteLine($"PASS {message}"); }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private sealed class ChunkedReadStream(byte[] data, int maximumRead) : MemoryStream(data, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
    }
}
