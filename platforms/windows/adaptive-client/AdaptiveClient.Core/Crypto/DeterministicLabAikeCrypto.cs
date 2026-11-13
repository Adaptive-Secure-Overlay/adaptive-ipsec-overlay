using System.Security.Cryptography;
using System.Text;
using AdaptiveClient.Core.Models;

namespace AdaptiveClient.Core.Crypto;

/// <summary>
/// Lab-only deterministic crypto stub for smoke-paths.
///
/// NOT FOR PRODUCTION:
/// - deterministic keys/nonces
/// - no cryptographic security guarantees
/// - signatures/tokens are HMAC-like hashes for protocol-shape testing only
/// </summary>
[Obsolete("DeterministicLabAikeCrypto is for lab smoke and MUST NOT be used in production.")]
public sealed class DeterministicLabAikeCrypto : IAikeCrypto
{
    private const string Algo = "DETERMINISTIC-LAB-1";
    private const string NotForProductionLabel = "NOT_FOR_PRODUCTION_LAB";
    private static readonly Encoding Utf8 = Encoding.UTF8;

    private static long sessionCounter;

    public string CreateSessionId()
    {
        var index = Interlocked.Increment(ref sessionCounter);
        return MakeHexDigest($"session:{index:D6}");
    }

    public string CreateEphemeralPublicKey(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return string.Empty;
        }

        return $"ephemeral-{MakeHexDigest($"ephemeral:{sessionId}")}";
    }

    public AikeMessage SignEnvelope(PeerDescriptor localPeer, AikeMessage envelope, string previousTranscriptHash)
    {
        if (string.IsNullOrWhiteSpace(localPeer.PeerId) || string.IsNullOrWhiteSpace(localPeer.KeyId))
        {
            throw new InvalidOperationException("Peer descriptor missing peer_id/key_id.");
        }

        if (!string.Equals(envelope.PreviousTranscriptHash, previousTranscriptHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Envelope previous transcript hash does not match signing context.");
        }

        var payloadHash = ComputePayloadHash(envelope);
        var aadHash = ComputeAadHash(envelope with { PayloadHash = payloadHash }, payloadHash);
        var signature = ComputeSignatureValue(envelope, payloadHash, aadHash, previousTranscriptHash, localPeer.KeyId);

        return envelope with
        {
            PayloadHash = payloadHash,
            AadHash = aadHash,
            Signature = new AikeSignature(Algo, localPeer.KeyId, signature)
        };
    }

    public bool VerifySignature(AikeMessage message, string expectedPreviousTranscriptHash, PeerDescriptor expectedSender)
    {
        if (message.Signature is null)
        {
            return false;
        }

        if (!string.Equals(message.Signature.Algorithm, Algo, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(message.PreviousTranscriptHash, expectedPreviousTranscriptHash, StringComparison.Ordinal))
        {
            return false;
        }

        var payloadHash = ComputePayloadHash(message);
        if (!string.Equals(message.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return false;
        }

        var aadHash = ComputeAadHash(message with { PayloadHash = payloadHash }, payloadHash);
        if (!string.Equals(message.AadHash, aadHash, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedSignature = ComputeSignatureValue(
            message,
            payloadHash,
            aadHash,
            message.PreviousTranscriptHash,
            message.Signature.KeyId);

        return string.Equals(message.Signature.Value, expectedSignature, StringComparison.Ordinal);
    }

    public string UpdateTranscript(string previousTranscriptHash, AikeMessage acceptedMessage)
    {
        var material = $"{previousTranscriptHash}|{acceptedMessage.MessageType}|{acceptedMessage.SenderId}|{acceptedMessage.Sequence}|{acceptedMessage.PayloadHash}|{acceptedMessage.AadHash}|{acceptedMessage.Signature?.Value}";
        return MakeHexDigest(material);
    }

    public bool VerifyTranscript(string expectedTranscriptHash, AikeMessage finalMessage, PeerDescriptor expectedSender)
    {
        if (!VerifySignature(finalMessage, expectedTranscriptHash, expectedSender))
        {
            return false;
        }

        // Minimal deterministic binding check for the final handshake step:
        // the final message must reference the expected transcript state.
        if (!string.Equals(finalMessage.PreviousTranscriptHash, expectedTranscriptHash, StringComparison.Ordinal))
        {
            return false;
        }

        var updated = UpdateTranscript(expectedTranscriptHash, finalMessage);
        return string.Equals(finalMessage.AadHash, updated, StringComparison.Ordinal);
    }

    public EspKeyMaterial DeriveEspKeys(SessionState session)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            throw new InvalidOperationException("SessionId is required to derive deterministic ESP keys.");
        }

        var baseMaterial = string.Concat(
            session.SessionId,
            "|",
            session.LocalEphemeralPublicKey ?? string.Empty,
            "|",
            session.RemoteEphemeralPublicKey ?? string.Empty,
            "|",
            session.Role);

        return new EspKeyMaterial(
            InboundCipherKey: DeriveFixedLengthBytes(baseMaterial + "|inbound-cipher", 32),
            InboundAuthKey: DeriveFixedLengthBytes(baseMaterial + "|inbound-auth", 32),
            OutboundCipherKey: DeriveFixedLengthBytes(baseMaterial + "|outbound-cipher", 32),
            OutboundAuthKey: DeriveFixedLengthBytes(baseMaterial + "|outbound-auth", 32));
    }

    private static string ComputePayloadHash(AikeMessage message)
    {
        var payload = message.Payload.DebugPlaintext;
        var metadata = payload?.Metadata is null
            ? "<none>"
            : string.Join("|", payload.Metadata.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));

        var plaintext = $"ephemeral={payload?.EphemeralPublicKey ?? "<none>"}|metadata={metadata}";
        var head = $"{message.MessageType}|{message.SessionId}|{message.RouteId}|{message.Sequence}|{message.SenderId}|{message.ReceiverHint}|{plaintext}";
        return MakeHexDigest(head);
    }

    private static string ComputeAadHash(AikeMessage message, string payloadHash)
    {
        var lifetime = $"{message.Lifetime.CreatedAt:o}|{message.Lifetime.NotAfter:o}|{message.Lifetime.SaSeconds}";
        var algorithms = $"{message.Algorithms.IdentitySignature}|{message.Algorithms.KeyExchange}|{message.Algorithms.Kdf}|{message.Algorithms.MetadataAead}|{message.Algorithms.EspProfile}";
        var aad = $"{message.Version}|{message.MessageType}|{message.Role}|{message.PeerId}|{message.Sequence}|{message.Nonce}|{lifetime}|{message.PreviousTranscriptHash}|{algorithms}|{payloadHash}|{NotForProductionLabel}";
        return MakeHexDigest(aad);
    }

    private static string ComputeSignatureValue(
        AikeMessage message,
        string payloadHash,
        string aadHash,
        string previousTranscriptHash,
        string keyId)
    {
        var material = $"{keyId}|{message.SenderId}|{message.SessionId}|{message.Sequence}|{previousTranscriptHash}|{payloadHash}|{aadHash}";
        return MakeHexDigest(material);
    }

    private static byte[] DeriveFixedLengthBytes(string seed, int length)
    {
        var hash = SHA256.HashData(Utf8.GetBytes(seed));
        var result = new byte[length];
        Buffer.BlockCopy(hash, 0, result, 0, Math.Min(length, hash.Length));
        if (length > hash.Length)
        {
            var remaining = length - hash.Length;
            Buffer.BlockCopy(SHA256.HashData(hash), 0, result, hash.Length, remaining);
        }

        return result;
    }

    private static string MakeHexDigest(string input)
    {
        var bytes = SHA256.HashData(Utf8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
