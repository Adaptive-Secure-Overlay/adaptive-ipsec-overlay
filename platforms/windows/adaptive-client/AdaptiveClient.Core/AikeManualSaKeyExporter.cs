using AdaptiveClient.Core.Crypto;
using AdaptiveClient.Core.Models;

namespace AdaptiveClient.Core;

public static class AikeManualSaKeyExporter
{
    public static AikeManualSaKeyExportResult Export(AikeManualSaKeyExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        return Export(
            request,
            BuildPeer(request.HostPeerId, request.HostIp, now),
            BuildPeer(request.PeerPeerId, request.PeerIp, now),
            identityKeys: null);
    }

    public static AikeManualSaKeyExportResult Export(
        AikeManualSaKeyExportRequest request,
        PeerDescriptor hostPeer,
        PeerDescriptor peerPeer,
        IAikeIdentityKeyProvider? identityKeys)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var notAfter = now.AddMinutes(5);
            var crypto = new X25519HkdfLabAikeCrypto(identityKeys);
            var trustStore = new ExportTrustStore();
            var initiator = new AikeSessionManager(crypto, trustStore);
            var responder = new AikeSessionManager(crypto, trustStore);
            hostPeer = EnsureReceiverHint(hostPeer, request.HostIp, now);
            peerPeer = EnsureReceiverHint(peerPeer, request.PeerIp, now);
            var routeId = string.IsNullOrWhiteSpace(request.RouteId)
                ? $"live-aike:{request.HostPeerId}->{request.PeerPeerId}"
                : request.RouteId.Trim();
            var profile = EspProfiles.Resolve(request.EspProfile);

            var start = initiator.CreateSession(
                localPeer: hostPeer,
                remotePeer: peerPeer,
                options: new AikeSessionOptions(
                    RouteId: routeId,
                    EspProfile: profile.Name,
                    Now: now,
                    NotAfter: notAfter));
            var ke1SignatureAlgorithm = start.FirstMessage.Signature?.Algorithm;

            var ke1 = AikeMessageCodec.Decode(AikeMessageCodec.Encode(start.FirstMessage));
            var responderInitial = SessionState.NewResponder(
                sessionId: ke1.SessionId,
                routeId: ke1.RouteId,
                localPeer: peerPeer,
                remotePeer: hostPeer,
                createdAt: now,
                notAfter: notAfter);

            var responderAfterKe1 = responder.ProcessMessage(responderInitial, ke1);
            if (!responderAfterKe1.Success || responderAfterKe1.Session is null || responderAfterKe1.OutboundMessage is null)
            {
                return AikeManualSaKeyExportResult.Fail(responderAfterKe1.Error ?? "responder failed to process KE1");
            }
            var ke2SignatureAlgorithm = responderAfterKe1.OutboundMessage.Signature?.Algorithm;

            var ke2 = AikeMessageCodec.Decode(AikeMessageCodec.EncodeToBytes(responderAfterKe1.OutboundMessage));
            var initiatorAfterKe2 = initiator.ProcessMessage(start.Session, ke2);
            if (!initiatorAfterKe2.Success || initiatorAfterKe2.Session is null)
            {
                return AikeManualSaKeyExportResult.Fail(initiatorAfterKe2.Error ?? "initiator failed to process KE2");
            }

            var responderEstablished = responderAfterKe1.Session.WithPhase(AikeSessionPhase.Established);
            var finalPayload = new AikePlainPayload(
                EphemeralPublicKey: responderEstablished.LocalEphemeralPublicKey,
                Metadata: new Dictionary<string, string>
                {
                    ["aike_stage"] = "manual_sa_key_export",
                    ["final"] = "install_ack"
                });

            var finalUnsigned = AikeMessage.CreateUnsigned(
                AikeMessageType.InstallAck,
                responderEstablished.WithPhase(AikeSessionPhase.Ke2Sent),
                finalPayload,
                now,
                notAfter);
            var finalSigned = crypto.SignEnvelope(responderEstablished.LocalPeer, finalUnsigned, responderEstablished.TranscriptHash);
            var finalSignatureAlgorithm = finalSigned.Signature?.Algorithm;
            var initiatorEstablished = initiator.ProcessMessage(
                initiatorAfterKe2.Session,
                AikeMessageCodec.Decode(AikeMessageCodec.EncodeToBytes(finalSigned)));
            if (!initiatorEstablished.Success || initiatorEstablished.Session is null)
            {
                return AikeManualSaKeyExportResult.Fail(initiatorEstablished.Error ?? "initiator failed to establish");
            }

            var hostInstall = BuildInstall(
                localIp: request.HostIp,
                remoteIp: request.PeerIp,
                profile: profile,
                inboundSpi: request.HostInboundSpi,
                outboundSpi: request.PeerInboundSpi);
            var peerInstall = BuildInstall(
                localIp: request.PeerIp,
                remoteIp: request.HostIp,
                profile: profile,
                inboundSpi: request.PeerInboundSpi,
                outboundSpi: request.HostInboundSpi);

            var hostMaterial = initiator.DeriveEspMaterial(initiatorEstablished.Session, hostInstall);
            var peerMaterial = responder.DeriveEspMaterial(responderEstablished, peerInstall);
            var validationErrors = ValidateDirectionalPair(hostMaterial, peerMaterial);
            if (validationErrors.Length > 0)
            {
                return AikeManualSaKeyExportResult.Fail(string.Join("; ", validationErrors));
            }

            return AikeManualSaKeyExportResult.Ok(
                SessionId: initiatorEstablished.Session.SessionId,
                RouteId: routeId,
                EspProfile: profile.Name,
                HostPeerId: hostPeer.PeerId,
                PeerPeerId: peerPeer.PeerId,
                HostIp: request.HostIp,
                PeerIp: request.PeerIp,
                Host: DirectionalKeys.FromMaterial(hostMaterial),
                Peer: DirectionalKeys.FromMaterial(peerMaterial),
                TranscriptHash: initiatorEstablished.Session.TranscriptHash,
                IdentitySignatureMode: BuildSignatureMode(ke1SignatureAlgorithm, ke2SignatureAlgorithm, finalSignatureAlgorithm));
        }
        catch (Exception ex)
        {
            return AikeManualSaKeyExportResult.Fail(ex.Message);
        }
    }

    private static PeerDescriptor BuildPeer(string peerId, string ip, DateTimeOffset now)
    {
        var normalizedPeerId = string.IsNullOrWhiteSpace(peerId) ? ip : peerId.Trim();
        var keyId = $"{normalizedPeerId}-lab-key";
        return new PeerDescriptor(
            PeerId: normalizedPeerId,
            KeyId: keyId,
            PublicIdentityKey: keyId,
            ReceiverHint: ip,
            TrustSource: PeerTrustSource.TofuForLabOnly,
            ValidFrom: now.AddMinutes(-1),
            ValidTo: now.AddDays(1));
    }

    private static PeerDescriptor EnsureReceiverHint(PeerDescriptor peer, string fallbackReceiverHint, DateTimeOffset now)
    {
        var validFrom = peer.ValidFrom == default ? now.AddMinutes(-1) : peer.ValidFrom;
        var validTo = peer.ValidTo == default ? now.AddDays(1) : peer.ValidTo;
        return peer with
        {
            ReceiverHint = string.IsNullOrWhiteSpace(peer.ReceiverHint) ? fallbackReceiverHint : peer.ReceiverHint,
            TrustSource = peer.TrustSource,
            ValidFrom = validFrom,
            ValidTo = validTo
        };
    }

    private static string BuildSignatureMode(params string?[] algorithms)
    {
        var normalized = algorithms
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return normalized.Length == 0 ? "unknown" : string.Join("+", normalized);
    }

    private static EspInstallParameters BuildInstall(
        string localIp,
        string remoteIp,
        EspProfile profile,
        uint inboundSpi,
        uint outboundSpi)
    {
        return new EspInstallParameters(
            LocalIp: localIp,
            RemoteIp: remoteIp,
            Protocol: ManualSaInstallPlan.IpsecProtocolEspNumeric,
            EspProfile: profile.Name,
            InboundSpi: inboundSpi,
            OutboundSpi: outboundSpi,
            InboundCipherAlgorithm: "AES-128-CBC",
            InboundAuthAlgorithm: "HMAC-SHA256-128",
            OutboundCipherAlgorithm: "AES-128-CBC",
            OutboundAuthAlgorithm: "HMAC-SHA256-128",
            LifetimeSeconds: 3600,
            MaxBytes: 0);
    }

    private static string[] ValidateDirectionalPair(EspMaterial host, EspMaterial peer)
    {
        var errors = new List<string>();
        if (!host.OutboundCipherKey.SequenceEqual(peer.InboundCipherKey))
        {
            errors.Add("host outbound cipher key does not match peer inbound cipher key");
        }

        if (!host.OutboundAuthKey.SequenceEqual(peer.InboundAuthKey))
        {
            errors.Add("host outbound auth key does not match peer inbound auth key");
        }

        if (!host.InboundCipherKey.SequenceEqual(peer.OutboundCipherKey))
        {
            errors.Add("host inbound cipher key does not match peer outbound cipher key");
        }

        if (!host.InboundAuthKey.SequenceEqual(peer.OutboundAuthKey))
        {
            errors.Add("host inbound auth key does not match peer outbound auth key");
        }

        return errors.ToArray();
    }

    private sealed class ExportTrustStore : IPeerTrustStore
    {
        public bool IsTrusted(string peerId, string keyId) =>
            !string.IsNullOrWhiteSpace(peerId) && !string.IsNullOrWhiteSpace(keyId);
    }
}

public sealed record AikeManualSaKeyExportRequest(
    string HostIp,
    string PeerIp,
    string HostPeerId,
    string PeerPeerId,
    string RouteId,
    string EspProfile,
    uint HostInboundSpi = 0xA100,
    uint PeerInboundSpi = 0xB200);

public sealed record AikeManualSaKeyExportResult(
    bool Success,
    string? Error,
    string? SessionId,
    string? RouteId,
    string? EspProfile,
    string? HostPeerId,
    string? PeerPeerId,
    string? HostIp,
    string? PeerIp,
    DirectionalKeys? Host,
    DirectionalKeys? Peer,
    string? TranscriptHash,
    string? IdentitySignatureMode = null)
{
    public static AikeManualSaKeyExportResult Ok(
        string SessionId,
        string RouteId,
        string EspProfile,
        string HostPeerId,
        string PeerPeerId,
        string HostIp,
        string PeerIp,
        DirectionalKeys Host,
        DirectionalKeys Peer,
        string TranscriptHash,
        string? IdentitySignatureMode = null) =>
        new(
            Success: true,
            Error: null,
            SessionId,
            RouteId,
            EspProfile,
            HostPeerId,
            PeerPeerId,
            HostIp,
            PeerIp,
            Host,
            Peer,
            TranscriptHash,
            IdentitySignatureMode);

    public static AikeManualSaKeyExportResult Fail(string error) =>
        new(
            Success: false,
            Error: error,
            SessionId: null,
            RouteId: null,
            EspProfile: null,
            HostPeerId: null,
            PeerPeerId: null,
            HostIp: null,
            PeerIp: null,
            Host: null,
            Peer: null,
            TranscriptHash: null,
            IdentitySignatureMode: null);
}

public sealed record DirectionalKeys(
    string InCipherKey,
    string InAuthKey,
    string OutCipherKey,
    string OutAuthKey)
{
    public static DirectionalKeys FromMaterial(EspMaterial material) =>
        new(
            InCipherKey: Convert.ToHexString(material.InboundCipherKey),
            InAuthKey: Convert.ToHexString(material.InboundAuthKey),
            OutCipherKey: Convert.ToHexString(material.OutboundCipherKey),
            OutAuthKey: Convert.ToHexString(material.OutboundAuthKey));

    public object ToLogSafe() => new
    {
        in_cipher_key_length = InCipherKey.Length / 2,
        in_auth_key_length = InAuthKey.Length / 2,
        out_cipher_key_length = OutCipherKey.Length / 2,
        out_auth_key_length = OutAuthKey.Length / 2
    };
}
