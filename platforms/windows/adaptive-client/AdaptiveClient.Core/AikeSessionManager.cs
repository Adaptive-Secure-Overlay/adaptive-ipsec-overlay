using AdaptiveClient.Core.Crypto;
using AdaptiveClient.Core.Models;

namespace AdaptiveClient.Core;

public sealed class AikeSessionManager
{
    private readonly IAikeCrypto crypto;
    private readonly IPeerTrustStore trustStore;

    public AikeSessionManager(IAikeCrypto crypto, IPeerTrustStore trustStore)
    {
        this.crypto = crypto;
        this.trustStore = trustStore;
    }

    public AikeSessionStart CreateSession(
        PeerDescriptor localPeer,
        PeerDescriptor remotePeer,
        AikeSessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RouteId))
        {
            throw new InvalidOperationException("route id is required.");
        }

        EnsurePeerDescriptor(localPeer);
        EnsurePeerDescriptor(remotePeer);
        EnsurePeerTrusted(localPeer.PeerId, localPeer.KeyId);
        EnsurePeerTrusted(remotePeer.PeerId, remotePeer.KeyId);
        EnsureTimeWindow(options.Now, options.NotAfter, "session window");

        var session = SessionState.NewInitiator(
                sessionId: crypto.CreateSessionId(),
                routeId: options.RouteId,
                localPeer: localPeer,
                remotePeer: remotePeer,
                createdAt: options.Now,
                notAfter: options.NotAfter)
            .WithPhase(AikeSessionPhase.Created);

        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            throw new InvalidOperationException("crypto backend returned empty session id.");
        }

        var ephemeralPublicKey = crypto.CreateEphemeralPublicKey(session.SessionId);
        if (string.IsNullOrWhiteSpace(ephemeralPublicKey))
        {
            throw new InvalidOperationException("crypto backend returned empty ephemeral public key");
        }

        var ke1Payload = new AikePlainPayload(
            EphemeralPublicKey: ephemeralPublicKey,
            Metadata: new Dictionary<string, string>
            {
                ["esp_profile"] = options.EspProfile,
                ["privacy_model"] = "pinned-identity-x1-x2-untrusted"
            });

        var ke1 = AikeMessage.CreateUnsigned(
            AikeMessageType.Ke1,
            session,
            ke1Payload,
            options.Now,
            options.NotAfter);

        var signedKe1 = crypto.SignEnvelope(localPeer, ke1, session.TranscriptHash);
        var next = session
            .WithLocalEphemeralPublicKey(ephemeralPublicKey)
            .WithNextLocalSequence()
            .WithTranscript(crypto.UpdateTranscript(session.TranscriptHash, signedKe1))
            .WithPhase(AikeSessionPhase.Ke1Sent);

        return new AikeSessionStart(next, signedKe1);
    }

    public ProcessResult ProcessMessage(SessionState session, AikeMessage message)
    {
        if (session.Phase == AikeSessionPhase.Failed)
        {
            return ProcessResult.Rejected(
                $"session {session.SessionId} is failed and cannot process messages",
                session);
        }

        if (session.IsExpired(DateTimeOffset.UtcNow))
        {
            return ProcessResult.Rejected(
                $"session {session.SessionId} is expired ({session.NotAfter:o})",
                session.WithPhase(AikeSessionPhase.Failed));
        }

        if (string.IsNullOrWhiteSpace(message.SessionId))
        {
            return ProcessResult.Rejected("message has empty session_id", session);
        }

        if (!string.Equals(session.SessionId, message.SessionId, StringComparison.Ordinal))
        {
            return ProcessResult.Rejected(
                $"session id mismatch. expected={session.SessionId}, actual={message.SessionId}",
                session);
        }

        if (!trustStore.IsTrusted(message.SenderId, message.SenderKeyId))
        {
            return ProcessResult.Rejected(
                $"sender '{message.SenderId}' is not trusted",
                session.WithPhase(AikeSessionPhase.Failed));
        }

        if (message.Lifetime.NotAfter < DateTimeOffset.UtcNow)
        {
            return ProcessResult.Rejected(
                $"incoming message is expired (not_after={message.Lifetime.NotAfter:o})",
                session);
        }

        if (message.Sequence != session.RemoteSequence + 1)
        {
            return ProcessResult.Rejected(
                $"message sequence must be sequential. expected={session.RemoteSequence + 1}, actual={message.Sequence}",
                session.WithPhase(AikeSessionPhase.Failed));
        }

        if (!string.Equals(message.PreviousTranscriptHash, session.TranscriptHash, StringComparison.Ordinal))
        {
            return ProcessResult.Rejected(
                "previous_transcript_hash does not match current session transcript",
                session.WithPhase(AikeSessionPhase.Failed));
        }

        if (!string.Equals(message.SenderId, session.RemotePeer.PeerId, StringComparison.Ordinal)
            || !string.Equals(message.SenderKeyId, session.RemotePeer.KeyId, StringComparison.Ordinal)
            || !string.Equals(message.PeerId, session.LocalPeer.PeerId, StringComparison.Ordinal))
        {
            return ProcessResult.Rejected(
                "message sender/peer binding does not match session endpoints",
                session.WithPhase(AikeSessionPhase.Failed));
        }

        if (!crypto.VerifySignature(message, session.TranscriptHash, session.RemotePeer))
        {
            return ProcessResult.Rejected(
                "signature verification failed",
                session.WithPhase(AikeSessionPhase.Failed));
        }

        var withRemote = session
            .WithRemoteSequence(message.Sequence)
            .WithTranscript(crypto.UpdateTranscript(session.TranscriptHash, message));

        return message.MessageType switch
        {
            AikeMessageType.Ke1 => ProcessKe1(message, withRemote),
            AikeMessageType.Ke2 => ProcessKe2(message, withRemote),
            AikeMessageType.SpiOffer or AikeMessageType.InstallAck => ProcessEstablish(message, withRemote),
            AikeMessageType.ProtectedMetadata or AikeMessageType.AddrToResponder => ProcessMetadata(message, withRemote),
            AikeMessageType.SessionInit => ProcessResult.Rejected(
                "SESSION_INIT is not part of this skeleton; use KE1/KE2 flow.",
                session),
            AikeMessageType.Error =>
                ProcessResult.Rejected("remote reported protocol error", session.WithPhase(AikeSessionPhase.Failed)),
            _ => ProcessResult.Rejected($"unsupported message type '{message.MessageType}'", session.WithPhase(AikeSessionPhase.Failed))
        };
    }

    public EspMaterial DeriveEspMaterial(SessionState session, EspInstallParameters install)
    {
        if (session.Phase != AikeSessionPhase.Established)
        {
            throw new InvalidOperationException(
                $"ESP material can be derived only after Established phase. Current phase is '{session.Phase}'.");
        }

        if (!session.HasEstablishedKeyExchange)
        {
            throw new InvalidOperationException("AIKE key exchange is not established yet.");
        }

        if (string.IsNullOrWhiteSpace(install.LocalIp) || string.IsNullOrWhiteSpace(install.RemoteIp))
        {
            throw new InvalidOperationException("ESP install parameters must include local_ip and remote_ip.");
        }

        if (session.IsExpired(DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException($"session {session.SessionId} has expired.");
        }

        var profile = EspProfiles.Resolve(install.EspProfile);
        var keys = crypto.DeriveEspKeys(session);
        if (keys.InboundCipherKey.Length == 0
            || keys.OutboundCipherKey.Length == 0
            || keys.InboundAuthKey.Length == 0
            || keys.OutboundAuthKey.Length == 0)
        {
            throw new InvalidOperationException("crypto backend returned empty ESP key material.");
        }

        return new EspMaterial(
            LocalIp: install.LocalIp,
            RemoteIp: install.RemoteIp,
            Protocol: "50",
            Profile: profile,
            Mode: EspMode.Transport,
            InboundSpi: install.InboundSpi,
            OutboundSpi: install.OutboundSpi,
            InboundCipherAlgorithm: install.InboundCipherAlgorithm,
            InboundCipherKey: ToProfileLength(keys.InboundCipherKey, profile.KeyLengths.InboundCipherKeyLength, "inbound-cipher"),
            InboundAuthAlgorithm: install.InboundAuthAlgorithm,
            InboundAuthKey: ToProfileLength(keys.InboundAuthKey, profile.KeyLengths.InboundAuthKeyLength, "inbound-auth"),
            OutboundCipherAlgorithm: install.OutboundCipherAlgorithm,
            OutboundCipherKey: ToProfileLength(keys.OutboundCipherKey, profile.KeyLengths.OutboundCipherKeyLength, "outbound-cipher"),
            OutboundAuthAlgorithm: install.OutboundAuthAlgorithm,
            OutboundAuthKey: ToProfileLength(keys.OutboundAuthKey, profile.KeyLengths.OutboundAuthKeyLength, "outbound-auth"),
            LifetimeSeconds: install.LifetimeSeconds,
            MaxBytes: install.MaxBytes);
    }

    private static byte[] ToProfileLength(byte[] keyMaterial, int targetLength, string label)
    {
        if (keyMaterial.Length == targetLength)
        {
            return keyMaterial;
        }

        if (targetLength <= 0)
        {
            throw new InvalidOperationException($"derived {label} key length must be > 0 for profile.");
        }

        if (targetLength > keyMaterial.Length)
        {
            var expanded = new byte[targetLength];
            var filled = 0;
            while (filled < targetLength)
            {
                var chunk = Math.Min(keyMaterial.Length, targetLength - filled);
                Buffer.BlockCopy(keyMaterial, 0, expanded, filled, chunk);
                filled += chunk;
            }

            return expanded;
        }

        var truncated = new byte[targetLength];
        Array.Copy(keyMaterial, truncated, targetLength);
        return truncated;
    }

    public bool VerifyTranscript(SessionState session, AikeMessage finalMessage)
    {
        if (session.Phase != AikeSessionPhase.Established)
        {
            return false;
        }

        if (session.IsExpired(DateTimeOffset.UtcNow))
        {
            return false;
        }

        if (!session.HasEstablishedKeyExchange)
        {
            return false;
        }

        return crypto.VerifyTranscript(session.TranscriptHash, finalMessage, session.RemotePeer);
    }

    private ProcessResult ProcessKe1(AikeMessage message, SessionState withRemote)
    {
        if (withRemote.Role != AikeRole.Responder)
        {
            return ProcessResult.Rejected(
                "incoming KE1 is expected only on responder side.",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        if (withRemote.Phase != AikeSessionPhase.Created)
        {
            return ProcessResult.Rejected(
                $"unexpected state for KE1: {withRemote.Phase}. expected {AikeSessionPhase.Created}",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        var remoteEphemeral = ExtractEphemeral(message);
        if (string.IsNullOrWhiteSpace(remoteEphemeral))
        {
            return ProcessResult.Rejected(
                "KE1 payload must contain sender ephemeral public key.",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        var localEphemeral = withRemote.LocalEphemeralPublicKey ?? crypto.CreateEphemeralPublicKey(withRemote.SessionId);
        if (string.IsNullOrWhiteSpace(localEphemeral))
        {
            return ProcessResult.Rejected(
                "crypto backend returned empty responder ephemeral public key.",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        var beforeKe2 = withRemote
            .WithRemoteEphemeralPublicKey(remoteEphemeral)
            .WithLocalEphemeralPublicKey(localEphemeral)
            .WithPhase(AikeSessionPhase.Ke1Received);

        var ke2Payload = new AikePlainPayload(
            EphemeralPublicKey: localEphemeral,
            Metadata: new Dictionary<string, string>
            {
                ["ke2"] = "ack",
                ["privacy_model"] = "direct-esp-lab"
            });

        var ke2 = AikeMessage.CreateUnsigned(
            AikeMessageType.Ke2,
            beforeKe2,
            ke2Payload,
            DateTimeOffset.UtcNow,
            withRemote.NotAfter);

        var signedKe2 = crypto.SignEnvelope(beforeKe2.LocalPeer, ke2, beforeKe2.TranscriptHash);
        var afterKe2 = beforeKe2
            .WithNextLocalSequence()
            .WithTranscript(crypto.UpdateTranscript(beforeKe2.TranscriptHash, signedKe2))
            .WithPhase(AikeSessionPhase.Ke2Sent);

        return ProcessResult.AcceptedWithResponse(afterKe2, signedKe2);
    }

    private ProcessResult ProcessKe2(AikeMessage message, SessionState withRemote)
    {
        if (withRemote.Role != AikeRole.Initiator)
        {
            return ProcessResult.Rejected(
                "incoming KE2 is expected only on initiator side.",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        if (withRemote.Phase != AikeSessionPhase.Ke1Sent)
        {
            return ProcessResult.Rejected(
                $"unexpected state for KE2: {withRemote.Phase}. expected {AikeSessionPhase.Ke1Sent}",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        var remoteEphemeral = ExtractEphemeral(message);
        if (string.IsNullOrWhiteSpace(remoteEphemeral))
        {
            return ProcessResult.Rejected(
                "KE2 payload must contain responder ephemeral public key.",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        if (string.IsNullOrWhiteSpace(withRemote.LocalEphemeralPublicKey))
        {
            return ProcessResult.Rejected(
                "local ephemeral key is missing; create session first.",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        var afterKe2 = withRemote
            .WithRemoteEphemeralPublicKey(remoteEphemeral)
            .WithPhase(AikeSessionPhase.Ke2Sent);

        return ProcessResult.Accepted(afterKe2);
    }

    private ProcessResult ProcessEstablish(AikeMessage message, SessionState withRemote)
    {
        if (!withRemote.HasEstablishedKeyExchange)
        {
            return ProcessResult.Rejected(
                "establishment message received before KE1/KE2 key exchange.",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        if (withRemote.Phase == AikeSessionPhase.Established)
        {
            return ProcessResult.Accepted(withRemote);
        }

        if (withRemote.Phase != AikeSessionPhase.Ke2Sent)
        {
            return ProcessResult.Rejected(
                $"unexpected state for establishment: {withRemote.Phase}. expected {AikeSessionPhase.Ke2Sent}",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        return ProcessResult.Accepted(withRemote.WithPhase(AikeSessionPhase.Established));
    }

    private ProcessResult ProcessMetadata(AikeMessage message, SessionState withRemote)
    {
        if (withRemote.Role == AikeRole.Responder)
        {
            // Metadata relay is accepted as a hint in lab mode; it cannot move responder
            // directly into Established without explicit SpiOffer/InstallAck.
            if (withRemote.Phase is not AikeSessionPhase.Ke2Sent and not AikeSessionPhase.Established)
            {
                return ProcessResult.Rejected(
                    $"metadata received in unexpected state: {withRemote.Phase}",
                    withRemote.WithPhase(AikeSessionPhase.Failed));
            }

            if (string.IsNullOrWhiteSpace(message.PayloadHash) || string.IsNullOrWhiteSpace(message.AadHash))
            {
                return ProcessResult.Rejected(
                    "metadata message is missing payload/hash fields.",
                    withRemote.WithPhase(AikeSessionPhase.Failed));
            }

            return ProcessResult.Accepted(withRemote);
        }

        if (withRemote.Phase != AikeSessionPhase.Ke2Sent)
        {
            return ProcessResult.Rejected(
                $"metadata received in unexpected state: {withRemote.Phase}",
                withRemote.WithPhase(AikeSessionPhase.Failed));
        }

        return ProcessResult.Accepted(withRemote);
    }

    private static string ExtractEphemeral(AikeMessage message)
    {
        return message.Payload.DebugPlaintext?.EphemeralPublicKey
               ?? string.Empty;
    }

    private static void EnsurePeerDescriptor(PeerDescriptor peer)
    {
        if (string.IsNullOrWhiteSpace(peer.PeerId) || string.IsNullOrWhiteSpace(peer.KeyId))
        {
            throw new InvalidOperationException("peer descriptor must contain peer_id and key_id.");
        }
    }

    private static void EnsureTimeWindow(DateTimeOffset start, DateTimeOffset end, string label)
    {
        if (end <= start)
        {
            throw new InvalidOperationException($"{label} must be in the future.");
        }
    }

    private void EnsurePeerTrusted(string peerId, string keyId)
    {
        if (!trustStore.IsTrusted(peerId, keyId))
        {
            throw new InvalidOperationException($"peer '{peerId}' with key_id '{keyId}' is not trusted.");
        }
    }
}
