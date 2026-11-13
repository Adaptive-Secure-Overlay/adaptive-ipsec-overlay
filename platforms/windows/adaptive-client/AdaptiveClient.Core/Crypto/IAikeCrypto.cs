using AdaptiveClient.Core.Models;

namespace AdaptiveClient.Core.Crypto;

public interface IAikeCrypto
{
    /// <summary>
    /// Generates a new opaque session identifier used only inside the AIKE control plane.
    /// </summary>
    string CreateSessionId();

    /// <summary>
    /// Creates the local ephemeral X25519 public key for the current session.
    /// The matching secret/private key must never be exported.
    /// </summary>
    string CreateEphemeralPublicKey(string sessionId);

    /// <summary>
    /// Signs the full envelope (including protected metadata hashes and transcript bindings).
    /// In production this must be Ed25519 over a canonicalized payload.
    /// </summary>
    AikeMessage SignEnvelope(PeerDescriptor localPeer, AikeMessage envelope, string previousTranscriptHash);

    /// <summary>
    /// Verifies the envelope signature and the expected transcript position.
    /// </summary>
    bool VerifySignature(AikeMessage message, string expectedPreviousTranscriptHash, PeerDescriptor expectedSender);

    /// <summary>
    /// Produces updated transcript hash TH' from TH + accepted message.
    /// </summary>
    string UpdateTranscript(string previousTranscriptHash, AikeMessage acceptedMessage);

    /// <summary>
    /// Checks final transcript commitment at the end of the key exchange.
    /// </summary>
    bool VerifyTranscript(string expectedTranscriptHash, AikeMessage finalMessage, PeerDescriptor expectedSender);

    /// <summary>
    /// Derives directional ESP keys only from session-established secrets.
    /// X1/X2 must never participate in this step.
    /// </summary>
    EspKeyMaterial DeriveEspKeys(SessionState session);
}
