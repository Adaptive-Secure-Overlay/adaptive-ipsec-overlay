namespace AdaptiveClient.Core.Models;

/// <summary>
/// Signed control-plane envelope used by AIKE messages.
/// </summary>
public sealed record AikeMessage(
    int Version,
    AikeMessageType MessageType,
    string SessionId,
    string RouteId,
    string SenderId,
    string SenderKeyId,
    string ReceiverHint,
    AikeRole Role,
    string PeerId,
    ulong Sequence,
    string Nonce,
    AikeLifetime Lifetime,
    string PreviousTranscriptHash,
    AikeAlgorithmSuite Algorithms,
    AikeEncryptedPayload Payload,
    string PayloadHash,
    string AadHash,
    AikeSignature? Signature)
{
    public static AikeMessage CreateUnsigned(
        AikeMessageType messageType,
        SessionState session,
        AikePlainPayload payload,
        DateTimeOffset createdAt,
        DateTimeOffset notAfter)
    {
        return new AikeMessage(
            Version: 1,
            MessageType: messageType,
            SessionId: session.SessionId,
            RouteId: session.RouteId,
            SenderId: session.LocalPeer.PeerId,
            SenderKeyId: session.LocalPeer.KeyId,
            ReceiverHint: session.RemotePeer.ReceiverHint,
            Role: session.Role,
            PeerId: session.RemotePeer.PeerId,
            Sequence: session.LocalSequence,
            Nonce: Guid.NewGuid().ToString("N"),
            Lifetime: new AikeLifetime(createdAt, notAfter, session.SaLifetimeSeconds),
            PreviousTranscriptHash: session.TranscriptHash,
            Algorithms: AikeAlgorithmSuite.DefaultEspTransport(),
            Payload: AikeEncryptedPayload.Plain(payload),
            PayloadHash: string.Empty,
            AadHash: string.Empty,
            Signature: null);
    }
}

public sealed record AikePlainPayload(
    string? EphemeralPublicKey,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record AikeEncryptedPayload(
    string Encryption,
    string? KeyId,
    string? Iv,
    string? Ciphertext,
    string? Tag,
    AikePlainPayload? DebugPlaintext)
{
    public static AikeEncryptedPayload Plain(AikePlainPayload payload)
    {
        return new AikeEncryptedPayload(
            Encryption: "none",
            KeyId: null,
            Iv: null,
            Ciphertext: null,
            Tag: null,
            DebugPlaintext: payload);
    }
}

public sealed record AikeSignature(
    string Algorithm,
    string KeyId,
    string Value);

public sealed record AikeLifetime(
    DateTimeOffset CreatedAt,
    DateTimeOffset NotAfter,
    int SaSeconds);

public sealed record AikeAlgorithmSuite(
    string IdentitySignature,
    string KeyExchange,
    string Kdf,
    string MetadataAead,
    string EspProfile)
{
    public static AikeAlgorithmSuite DefaultEspTransport()
    {
        return new AikeAlgorithmSuite(
            IdentitySignature: "Ed25519",
            KeyExchange: "X25519",
            Kdf: "HKDF-SHA256",
            MetadataAead: "AES-256-GCM",
            EspProfile: "ESP_AES_128_CBC_HMAC_SHA256_128");
    }
}

public enum AikeMessageType
{
    SessionInit,
    Ke1,
    Ke2,
    ProtectedMetadata,
    AddrToResponder,
    SpiOffer,
    InstallAck,
    Error
}
