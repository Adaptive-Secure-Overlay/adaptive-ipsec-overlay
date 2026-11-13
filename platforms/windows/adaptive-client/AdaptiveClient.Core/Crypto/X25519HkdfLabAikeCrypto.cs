using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AdaptiveClient.Core.Models;

namespace AdaptiveClient.Core.Crypto;

/// <summary>
/// Crypto provider for the Windows EXE path.
///
/// This provider uses real ephemeral X25519 and HKDF-SHA256 for ESP key material.
/// When an identity key provider is supplied, envelopes are signed and verified with
/// Ed25519. Otherwise it keeps the old lab transcript signature for dry-run helpers.
/// </summary>
public sealed class X25519HkdfLabAikeCrypto : IAikeCrypto
{
    public const string LabSignatureAlgorithm = "LAB-X25519-HKDF-SHA256-SIG";
    public const string PublicKeyPrefix = "x25519:";

    private static readonly Encoding Utf8 = Encoding.UTF8;
    private readonly IAikeIdentityKeyProvider? identityKeys;
    private readonly ConcurrentDictionary<string, byte[]> privateKeysByPublicKey = new(StringComparer.Ordinal);

    public X25519HkdfLabAikeCrypto(IAikeIdentityKeyProvider? identityKeys = null)
    {
        this.identityKeys = identityKeys;
    }

    public string CreateSessionId() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public string CreateEphemeralPublicKey(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("session id is required before generating X25519 keypair.");
        }

        var privateKey = ManagedX25519.GeneratePrivateKey();
        var publicKey = ManagedX25519.PublicFromPrivate(privateKey);
        var encodedPublic = PublicKeyPrefix + Base64Url(publicKey);
        privateKeysByPublicKey[PrivateKeyMapKey(sessionId, encodedPublic)] = privateKey;
        return encodedPublic;
    }

    public AikeMessage SignEnvelope(PeerDescriptor localPeer, AikeMessage envelope, string previousTranscriptHash)
    {
        if (!string.Equals(envelope.PreviousTranscriptHash, previousTranscriptHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Envelope previous transcript hash does not match signing context.");
        }

        var payloadHash = ComputePayloadHash(envelope);
        var aadHash = ComputeAadHash(envelope with { PayloadHash = payloadHash }, payloadHash);
        var unsigned = envelope with
        {
            PayloadHash = payloadHash,
            AadHash = aadHash
        };

        var signatureAlgorithm = LabSignatureAlgorithm;
        var signature = ComputeLabSignature(unsigned, payloadHash, aadHash, previousTranscriptHash, localPeer);
        if (identityKeys is not null
            && Ed25519Identity.IsEd25519PublicIdentityKey(localPeer.PublicIdentityKey)
            && identityKeys.TryExportPrivateSeed(localPeer.PeerId, localPeer.KeyId, out var privateSeed))
        {
            try
            {
                signatureAlgorithm = Ed25519Identity.SignatureAlgorithm;
                signature = Ed25519Identity.Sign(privateSeed, BuildSignedInput(unsigned, payloadHash, aadHash, previousTranscriptHash));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateSeed);
            }
        }

        return unsigned with
        {
            Signature = new AikeSignature(signatureAlgorithm, localPeer.KeyId, signature)
        };
    }

    public bool VerifySignature(AikeMessage message, string expectedPreviousTranscriptHash, PeerDescriptor expectedSender)
    {
        if (message.Signature is null)
        {
            return false;
        }

        if (!string.Equals(message.PreviousTranscriptHash, expectedPreviousTranscriptHash, StringComparison.Ordinal))
        {
            return false;
        }

        var payloadHash = ComputePayloadHash(message);
        if (!CryptographicOperations.FixedTimeEquals(HexToBytes(payloadHash), HexToBytes(message.PayloadHash)))
        {
            return false;
        }

        var aadHash = ComputeAadHash(message with { PayloadHash = payloadHash }, payloadHash);
        if (!CryptographicOperations.FixedTimeEquals(HexToBytes(aadHash), HexToBytes(message.AadHash)))
        {
            return false;
        }

        if (string.Equals(message.Signature.Algorithm, Ed25519Identity.SignatureAlgorithm, StringComparison.Ordinal))
        {
            if (!string.Equals(message.SenderId, expectedSender.PeerId, StringComparison.Ordinal)
                || !string.Equals(message.SenderKeyId, expectedSender.KeyId, StringComparison.Ordinal)
                || !Ed25519Identity.IsEd25519PublicIdentityKey(expectedSender.PublicIdentityKey))
            {
                return false;
            }

            return Ed25519Identity.Verify(
                expectedSender.PublicIdentityKey,
                BuildSignedInput(message, payloadHash, aadHash, expectedPreviousTranscriptHash),
                message.Signature.Value);
        }

        if (!string.Equals(message.Signature.Algorithm, LabSignatureAlgorithm, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = ComputeLabSignature(
            message,
            payloadHash,
            aadHash,
            expectedPreviousTranscriptHash,
            new PeerDescriptor(
                PeerId: message.SenderId,
                KeyId: message.Signature.KeyId,
                PublicIdentityKey: message.Signature.KeyId,
                ReceiverHint: message.ReceiverHint,
                TrustSource: PeerTrustSource.TofuForLabOnly,
                ValidFrom: DateTimeOffset.UnixEpoch,
                ValidTo: DateTimeOffset.MaxValue));

        return CryptographicOperations.FixedTimeEquals(
            Utf8.GetBytes(expected),
            Utf8.GetBytes(message.Signature.Value));
    }

    public string UpdateTranscript(string previousTranscriptHash, AikeMessage acceptedMessage)
    {
        return Hex(SHA256.HashData(Utf8.GetBytes(
            string.Join(
                '|',
                "AIKE-v1-TH",
                previousTranscriptHash,
                acceptedMessage.Version,
                acceptedMessage.MessageType,
                acceptedMessage.SessionId,
                acceptedMessage.RouteId,
                acceptedMessage.SenderId,
                acceptedMessage.PeerId,
                acceptedMessage.Sequence,
                acceptedMessage.PayloadHash,
                acceptedMessage.AadHash,
                acceptedMessage.Signature?.Value ?? string.Empty))));
    }

    public bool VerifyTranscript(string expectedTranscriptHash, AikeMessage finalMessage, PeerDescriptor expectedSender)
    {
        return string.Equals(finalMessage.PreviousTranscriptHash, expectedTranscriptHash, StringComparison.Ordinal)
               && VerifySignature(finalMessage, expectedTranscriptHash, expectedSender);
    }

    public EspKeyMaterial DeriveEspKeys(SessionState session)
    {
        if (!session.HasEstablishedKeyExchange)
        {
            throw new InvalidOperationException("X25519/HKDF ESP keys require completed KE1/KE2 exchange.");
        }

        if (string.IsNullOrWhiteSpace(session.LocalEphemeralPublicKey)
            || string.IsNullOrWhiteSpace(session.RemoteEphemeralPublicKey))
        {
            throw new InvalidOperationException("local and remote ephemeral public keys are required.");
        }

        if (!privateKeysByPublicKey.TryGetValue(
                PrivateKeyMapKey(session.SessionId, session.LocalEphemeralPublicKey),
                out var localPrivateKey))
        {
            throw new InvalidOperationException("local X25519 private key is not available for this session.");
        }

        var remotePublic = DecodePublicKey(session.RemoteEphemeralPublicKey);
        var sharedSecret = ManagedX25519.SharedSecret(localPrivateKey, remotePublic);
        var initiatorPeer = session.Role == AikeRole.Initiator ? session.LocalPeer : session.RemotePeer;
        var responderPeer = session.Role == AikeRole.Initiator ? session.RemotePeer : session.LocalPeer;
        var initiatorEphemeralPublicKey = session.Role == AikeRole.Initiator
            ? session.LocalEphemeralPublicKey
            : session.RemoteEphemeralPublicKey;
        var responderEphemeralPublicKey = session.Role == AikeRole.Initiator
            ? session.RemoteEphemeralPublicKey
            : session.LocalEphemeralPublicKey;

        var salt = SHA256.HashData(Utf8.GetBytes(string.Join(
            '|',
            "AIKE-v1-X25519-HKDF-salt",
            session.SessionId,
            session.RouteId,
            initiatorPeer.PeerId,
            responderPeer.PeerId,
            NormalizePublicKey(initiatorEphemeralPublicKey),
            NormalizePublicKey(responderEphemeralPublicKey))));

        var aToBCipher = Hkdf(sharedSecret, salt, $"AIKE-v1 ESP {initiatorPeer.PeerId}->{responderPeer.PeerId} cipher", 32);
        var aToBAuth = Hkdf(sharedSecret, salt, $"AIKE-v1 ESP {initiatorPeer.PeerId}->{responderPeer.PeerId} auth", 32);
        var bToACipher = Hkdf(sharedSecret, salt, $"AIKE-v1 ESP {responderPeer.PeerId}->{initiatorPeer.PeerId} cipher", 32);
        var bToAAuth = Hkdf(sharedSecret, salt, $"AIKE-v1 ESP {responderPeer.PeerId}->{initiatorPeer.PeerId} auth", 32);

        return session.Role == AikeRole.Initiator
            ? new EspKeyMaterial(
                InboundCipherKey: bToACipher,
                InboundAuthKey: bToAAuth,
                OutboundCipherKey: aToBCipher,
                OutboundAuthKey: aToBAuth)
            : new EspKeyMaterial(
                InboundCipherKey: aToBCipher,
                InboundAuthKey: aToBAuth,
                OutboundCipherKey: bToACipher,
                OutboundAuthKey: bToAAuth);
    }

    private static byte[] Hkdf(byte[] sharedSecret, byte[] salt, string info, int length) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, length, salt, Utf8.GetBytes(info));

    private static string ComputePayloadHash(AikeMessage message)
    {
        var payload = message.Payload.DebugPlaintext;
        var metadata = payload?.Metadata is null
            ? string.Empty
            : string.Join("|", payload.Metadata.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));

        return Hex(SHA256.HashData(Utf8.GetBytes(string.Join(
            '|',
            "AIKE-v1-payload",
            payload?.EphemeralPublicKey ?? string.Empty,
            metadata,
            message.Payload.Encryption,
            message.Payload.KeyId ?? string.Empty,
            message.Payload.Iv ?? string.Empty,
            message.Payload.Ciphertext ?? string.Empty,
            message.Payload.Tag ?? string.Empty))));
    }

    private static string ComputeAadHash(AikeMessage message, string payloadHash)
    {
        return Hex(SHA256.HashData(Utf8.GetBytes(string.Join(
            '|',
            "AIKE-v1-aad",
            message.Version,
            message.MessageType,
            message.SessionId,
            message.RouteId,
            message.SenderId,
            message.SenderKeyId,
            message.ReceiverHint,
            message.Role,
            message.PeerId,
            message.Sequence,
            message.Nonce,
            message.Lifetime.CreatedAt.ToUnixTimeSeconds(),
            message.Lifetime.NotAfter.ToUnixTimeSeconds(),
            message.Lifetime.SaSeconds,
            message.PreviousTranscriptHash,
            message.Algorithms.IdentitySignature,
            message.Algorithms.KeyExchange,
            message.Algorithms.Kdf,
            message.Algorithms.MetadataAead,
            message.Algorithms.EspProfile,
            payloadHash))));
    }

    private static string ComputeLabSignature(
        AikeMessage message,
        string payloadHash,
        string aadHash,
        string previousTranscriptHash,
        PeerDescriptor signer)
    {
        return Hex(SHA256.HashData(Utf8.GetBytes(string.Join(
            '|',
            "AIKE-v1-lab-signature",
            signer.PeerId,
            signer.KeyId,
            message.MessageType,
            message.SessionId,
            message.RouteId,
            message.Role,
            message.PeerId,
            message.Sequence,
            previousTranscriptHash,
            payloadHash,
                aadHash))));
    }

    private static byte[] BuildSignedInput(
        AikeMessage message,
        string payloadHash,
        string aadHash,
        string previousTranscriptHash)
    {
        var payload = message.Payload.DebugPlaintext;
        var metadata = payload?.Metadata is null
            ? string.Empty
            : string.Join("|", payload.Metadata.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));

        return Utf8.GetBytes(string.Join(
            '|',
            "AIKE-SIG-v1",
            message.Version,
            message.MessageType,
            message.SessionId,
            message.RouteId,
            message.SenderId,
            message.SenderKeyId,
            message.ReceiverHint,
            message.Role,
            message.PeerId,
            message.Sequence,
            message.Nonce,
            message.Lifetime.CreatedAt.ToUnixTimeSeconds(),
            message.Lifetime.NotAfter.ToUnixTimeSeconds(),
            message.Lifetime.SaSeconds,
            previousTranscriptHash,
            message.Algorithms.IdentitySignature,
            message.Algorithms.KeyExchange,
            message.Algorithms.Kdf,
            message.Algorithms.MetadataAead,
            message.Algorithms.EspProfile,
            payload?.EphemeralPublicKey ?? string.Empty,
            metadata,
            message.Payload.Encryption,
            message.Payload.KeyId ?? string.Empty,
            message.Payload.Iv ?? string.Empty,
            message.Payload.Ciphertext ?? string.Empty,
            message.Payload.Tag ?? string.Empty,
            payloadHash,
            aadHash));
    }

    private static string PrivateKeyMapKey(string sessionId, string publicKey) =>
        sessionId + "|" + NormalizePublicKey(publicKey);

    private static byte[] DecodePublicKey(string encoded)
    {
        var normalized = NormalizePublicKey(encoded);
        var bytes = Base64UrlDecode(normalized);
        if (bytes.Length != ManagedX25519.KeySize)
        {
            throw new InvalidOperationException("X25519 public key must be 32 bytes.");
        }

        return bytes;
    }

    private static string NormalizePublicKey(string encoded) =>
        encoded.StartsWith(PublicKeyPrefix, StringComparison.Ordinal)
            ? encoded[PublicKeyPrefix.Length..]
            : encoded;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return [];
        }

        return Convert.FromHexString(hex);
    }
}
