using AdaptiveClient.Core.Crypto;
using AdaptiveClient.Core.Models;
using System.Collections.Generic;

namespace AdaptiveClient.Core;

/// <summary>
/// Small in-process smoke helper for JSON codec + KE1/KE2 lab flow.
/// This helper is intentionally minimal and contains no console I/O.
/// </summary>
public static class AikeMessageCodecSmoke
{
    public sealed record SmokeResult(
        bool Success,
        AikeSessionPhase FinalState,
        string SessionId,
        string? Error)
    {
        public static SmokeResult Ok(string sessionId, AikeSessionPhase finalState)
            => new(Success: true, FinalState: finalState, SessionId: sessionId, Error: null);

        public static SmokeResult Fail(string sessionId, AikeSessionPhase finalState, string error)
            => new(Success: false, FinalState: finalState, SessionId: sessionId, Error: error);
    }

    public sealed record EspMaterialSmokeResult(
        bool Success,
        AikeSessionPhase FinalState,
        string SessionId,
        string? Protocol,
        uint InboundSpi,
        uint OutboundSpi,
        string? Error)
    {
        public static EspMaterialSmokeResult Ok(string sessionId, SessionState session)
            => new(
                Success: true,
                FinalState: session.Phase,
                SessionId: session.SessionId,
                Protocol: session.HasEstablishedKeyExchange ? "50" : null,
                InboundSpi: 0,
                OutboundSpi: 0,
                Error: null);

        public static EspMaterialSmokeResult Fail(string sessionId, AikeSessionPhase finalState, string error)
            => new(
                Success: false,
                FinalState: finalState,
                SessionId: sessionId,
                Protocol: null,
                InboundSpi: 0,
                OutboundSpi: 0,
                Error: error);
    }

    public sealed record IdentitySignedExportSmokeResult(
        bool Success,
        string? IdentitySignatureMode,
        string? SessionId,
        string? Error)
    {
        public static IdentitySignedExportSmokeResult Ok(string? identitySignatureMode, string? sessionId)
            => new(Success: true, IdentitySignatureMode: identitySignatureMode, SessionId: sessionId, Error: null);

        public static IdentitySignedExportSmokeResult Fail(string? sessionId, string error)
            => new(Success: false, IdentitySignatureMode: null, SessionId: sessionId, Error: error);
    }

    public sealed record ManualSaInstallPlanResult(
        bool Success,
        AikeSessionPhase FinalState,
        string SessionId,
        ManualSaInstallPlan? Plan,
        ManualSaInstallPlanValidationResult Validation,
        string? Error)
    {
        public static ManualSaInstallPlanResult Ok(
            string sessionId,
            AikeSessionPhase finalState,
            ManualSaInstallPlan plan)
            => new(
                Success: true,
                FinalState: finalState,
                SessionId: sessionId,
                Plan: plan,
                Validation: ManualSaInstallPlanValidationResult.Valid(),
                Error: null);

        public static ManualSaInstallPlanResult Fail(
            string sessionId,
            AikeSessionPhase finalState,
            string error)
            => new(
                Success: false,
                FinalState: finalState,
                SessionId: sessionId,
                Plan: null,
                Validation: ManualSaInstallPlanValidationResult.Invalid(error),
                Error: error);

        public static ManualSaInstallPlanResult Invalid(
            string sessionId,
            AikeSessionPhase finalState,
            ManualSaInstallPlanValidationResult validation)
            => new(
                Success: false,
                FinalState: finalState,
                SessionId: sessionId,
                Plan: null,
                Validation: validation,
                Error: validation.Error ?? "manual SA install plan is invalid");
    }

    public sealed record ManualSaPlanFileRoundTripResult(
        bool Success,
        AikeSessionPhase FinalState,
        string SessionId,
        string? SerializedPlan,
        string? RedactedPlan,
        ManualSaPlanFileSerializer.ManualSaPlanFileValidationResult Validation,
        string? Error)
    {
        public static ManualSaPlanFileRoundTripResult Ok(
            string sessionId,
            AikeSessionPhase finalState,
            string serializedPlan,
            string redactedPlan,
            ManualSaPlanFileSerializer.ManualSaPlanFileValidationResult validation)
            => new(
                Success: true,
                FinalState: finalState,
                SessionId: sessionId,
                SerializedPlan: serializedPlan,
                RedactedPlan: redactedPlan,
                Validation: validation,
                Error: null);

        public static ManualSaPlanFileRoundTripResult Fail(
            string sessionId,
            AikeSessionPhase finalState,
            string error,
            ManualSaPlanFileSerializer.ManualSaPlanFileValidationResult? validation = null)
            => new(
                Success: false,
                FinalState: finalState,
                SessionId: sessionId,
                SerializedPlan: null,
                RedactedPlan: null,
                Validation: validation ?? ManualSaPlanFileSerializer.ManualSaPlanFileValidationResult.Invalid(error),
                Error: error);

        public static ManualSaPlanFileRoundTripResult Invalid(
            string sessionId,
            AikeSessionPhase finalState,
            ManualSaPlanFileSerializer.ManualSaPlanFileValidationResult validation)
            => new(
                Success: false,
                FinalState: finalState,
                SessionId: sessionId,
                SerializedPlan: null,
                RedactedPlan: null,
                Validation: validation,
                Error: validation.Error ?? "manual SA plan-file is invalid");
    }

    public sealed record DynamicEndpointPlanFileRoundTripResult(
        bool Success,
        AikeSessionPhase FinalState,
        string SessionId,
        EndpointRecordLog? LocalEndpoint,
        EndpointRecordLog? RemoteEndpoint,
        string? RedactedPlan,
        int SerializedPlanLength,
        ManualSaPlanFileSerializer.ManualSaPlanFileValidationResult PlanFileValidation,
        string? Error)
    {
        public static DynamicEndpointPlanFileRoundTripResult Ok(
            string sessionId,
            AikeSessionPhase finalState,
            EndpointRecord localEndpoint,
            EndpointRecord remoteEndpoint,
            string redactedPlan,
            int serializedPlanLength,
            ManualSaPlanFileSerializer.ManualSaPlanFileValidationResult validation)
            => new(
                Success: true,
                FinalState: finalState,
                SessionId: sessionId,
                LocalEndpoint: localEndpoint.ToLogSafe(),
                RemoteEndpoint: remoteEndpoint.ToLogSafe(),
                RedactedPlan: redactedPlan,
                SerializedPlanLength: serializedPlanLength,
                PlanFileValidation: validation,
                Error: null);

        public static DynamicEndpointPlanFileRoundTripResult Fail(
            string sessionId,
            AikeSessionPhase finalState,
            string error)
            => new(
                Success: false,
                FinalState: finalState,
                SessionId: sessionId,
                LocalEndpoint: null,
                RemoteEndpoint: null,
                RedactedPlan: null,
                SerializedPlanLength: 0,
                PlanFileValidation: ManualSaPlanFileSerializer.ManualSaPlanFileValidationResult.Invalid(error),
                Error: error);
    }

    /// <summary>
    /// Runs a deterministic lab path: initiator creates KE1, responder decodes it and
    /// produces KE2, initiator decodes KE2 and validates state transitions.
    /// </summary>
    [Obsolete("Prefer RunKe1Ke2EstablishedRoundTrip for full FSM smoke coverage.")]
    public static bool RunKe1Ke2RoundTrip()
    {
        #pragma warning disable CS0618
        var crypto = new DeterministicLabAikeCrypto();
        #pragma warning restore CS0618
        var trustStore = new TrustedForSmokeTrustStore();
        var initiator = new AikeSessionManager(crypto, trustStore);
        var responder = new AikeSessionManager(crypto, trustStore);

        var now = DateTimeOffset.UtcNow;
        var notAfter = now.AddMinutes(5);

        var initiatorPeer = new PeerDescriptor(
            PeerId: "initiator-poc",
            KeyId: "init-key-lab",
            PublicIdentityKey: "lab-public-init",
            ReceiverHint: "127.0.0.1:0",
            TrustSource: PeerTrustSource.TofuForLabOnly,
            ValidFrom: now.AddMinutes(-10),
            ValidTo: now.AddDays(1));

        var responderPeer = new PeerDescriptor(
            PeerId: "responder-poc",
            KeyId: "resp-key-lab",
            PublicIdentityKey: "lab-public-resp",
            ReceiverHint: "127.0.0.1:0",
            TrustSource: PeerTrustSource.TofuForLabOnly,
            ValidFrom: now.AddMinutes(-10),
            ValidTo: now.AddDays(1));

        var options = new AikeSessionOptions(
            RouteId: "lab-route",
            EspProfile: EspProfiles.ManualSa.Name,
            Now: now,
            NotAfter: notAfter);

        var initiatorStart = initiator.CreateSession(
            localPeer: initiatorPeer,
            remotePeer: responderPeer,
            options: options);

        var encodedKe1 = AikeMessageCodec.Encode(initiatorStart.FirstMessage);
        var decodedKe1 = AikeMessageCodec.Decode(encodedKe1);

        var responderInitial = SessionState.NewResponder(
            sessionId: decodedKe1.SessionId,
            routeId: decodedKe1.RouteId,
            localPeer: responderPeer,
            remotePeer: initiatorPeer,
            createdAt: now,
            notAfter: notAfter);

        var fromResponder = responder.ProcessMessage(responderInitial, decodedKe1);
        if (!fromResponder.Success || fromResponder.OutboundMessage is null)
        {
            return false;
        }

        var encodedKe2 = AikeMessageCodec.EncodeToBytes(fromResponder.OutboundMessage);
        var decodedKe2 = AikeMessageCodec.Decode(encodedKe2);

        var fromInitiator = initiator.ProcessMessage(initiatorStart.Session, decodedKe2);
        return fromInitiator.Success
               && fromInitiator.Session is not null
               && fromInitiator.Session.Phase == AikeSessionPhase.Ke2Sent
               && fromInitiator.Session.HasEstablishedKeyExchange;
    }

    /// <summary>
    /// Runs a deterministic lab smoke path through the complete FSM:
    /// CreateSession -> KE1 -> KE2 -> Established.
    /// </summary>
    public static SmokeResult RunKe1Ke2EstablishedRoundTrip()
    {
        var established = TryRunEstablishedRoundTrip();
        if (!established.Success || established.Session is null)
        {
            return SmokeResult.Fail(
                established.SessionId,
                established.FinalState,
                established.Error ?? "establishment failed");
        }

        return SmokeResult.Ok(established.Session.SessionId, established.Session.Phase);
    }

    /// <summary>
    /// Runs the same FSM with real Ed25519 signatures over the AIKE signed input.
    /// </summary>
    public static SmokeResult RunEd25519SignedEstablishedRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var initiatorSeed = Ed25519Identity.CreateSeed();
        var responderSeed = Ed25519Identity.CreateSeed();
        try
        {
            var initiatorPeer = new PeerDescriptor(
                PeerId: "initiator-ed25519",
                KeyId: "init-ed25519",
                PublicIdentityKey: Ed25519Identity.GetPublicIdentityKey(initiatorSeed),
                ReceiverHint: "127.0.0.1:0",
                TrustSource: PeerTrustSource.Pinned,
                ValidFrom: now.AddMinutes(-10),
                ValidTo: now.AddDays(1));

            var responderPeer = new PeerDescriptor(
                PeerId: "responder-ed25519",
                KeyId: "resp-ed25519",
                PublicIdentityKey: Ed25519Identity.GetPublicIdentityKey(responderSeed),
                ReceiverHint: "127.0.0.1:0",
                TrustSource: PeerTrustSource.Pinned,
                ValidFrom: now.AddMinutes(-10),
                ValidTo: now.AddDays(1));

            var identityKeys = new InMemoryIdentityKeyProvider();
            identityKeys.Add(initiatorPeer.PeerId, initiatorPeer.KeyId, initiatorSeed);
            identityKeys.Add(responderPeer.PeerId, responderPeer.KeyId, responderSeed);

            var established = TryRunEstablishedRoundTrip(
                new X25519HkdfLabAikeCrypto(identityKeys),
                (initiatorPeer, responderPeer));
            if (!established.Success || established.Session is null)
            {
                return SmokeResult.Fail(
                    established.SessionId,
                    established.FinalState,
                    established.Error ?? "ed25519 establishment failed");
            }

            return SmokeResult.Ok(established.Session.SessionId, established.Session.Phase);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(initiatorSeed);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(responderSeed);
        }
    }

    /// <summary>
    /// Runs the manual-SA export path with Ed25519 identities on both endpoints.
    /// </summary>
    public static IdentitySignedExportSmokeResult RunDualEd25519ManualSaKeyExport()
    {
        var now = DateTimeOffset.UtcNow;
        var hostSeed = Ed25519Identity.CreateSeed();
        var peerSeed = Ed25519Identity.CreateSeed();
        try
        {
            var hostPeer = new PeerDescriptor(
                PeerId: "host-ed25519",
                KeyId: "host-ed25519-key",
                PublicIdentityKey: Ed25519Identity.GetPublicIdentityKey(hostSeed),
                ReceiverHint: "192.168.95.1",
                TrustSource: PeerTrustSource.Pinned,
                ValidFrom: now.AddMinutes(-10),
                ValidTo: now.AddDays(1));

            var peerPeer = new PeerDescriptor(
                PeerId: "peer-ed25519",
                KeyId: "peer-ed25519-key",
                PublicIdentityKey: Ed25519Identity.GetPublicIdentityKey(peerSeed),
                ReceiverHint: "192.168.113.199",
                TrustSource: PeerTrustSource.Pinned,
                ValidFrom: now.AddMinutes(-10),
                ValidTo: now.AddDays(1));

            var identityKeys = new InMemoryIdentityKeyProvider();
            identityKeys.Add(hostPeer.PeerId, hostPeer.KeyId, hostSeed);
            identityKeys.Add(peerPeer.PeerId, peerPeer.KeyId, peerSeed);

            var export = AikeManualSaKeyExporter.Export(
                new AikeManualSaKeyExportRequest(
                    HostIp: "192.168.95.1",
                    PeerIp: "192.168.113.199",
                    HostPeerId: hostPeer.PeerId,
                    PeerPeerId: peerPeer.PeerId,
                    RouteId: "dual-ed25519-manual-sa-smoke",
                    EspProfile: EspProfiles.ManualSa.Name,
                    HostInboundSpi: 0xA100,
                    PeerInboundSpi: 0xB200),
                hostPeer,
                peerPeer,
                identityKeys);

            if (!export.Success)
            {
                return IdentitySignedExportSmokeResult.Fail(export.SessionId, export.Error ?? "dual ed25519 export failed");
            }

            if (!string.Equals(export.IdentitySignatureMode, Ed25519Identity.SignatureAlgorithm, StringComparison.Ordinal))
            {
                return IdentitySignedExportSmokeResult.Fail(
                    export.SessionId,
                    $"expected {Ed25519Identity.SignatureAlgorithm} identity signature mode, got {export.IdentitySignatureMode ?? "null"}");
            }

            return IdentitySignedExportSmokeResult.Ok(export.IdentitySignatureMode, export.SessionId);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(hostSeed);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(peerSeed);
        }
    }

    /// <summary>
    /// Runs established smoke path and derives ESP material with dummy IP/SPI values.
    /// </summary>
    public static EspMaterialSmokeResult RunEstablishedAndDeriveEspMaterial()
    {
        var crypto = new X25519HkdfLabAikeCrypto();
        var established = TryRunEstablishedRoundTrip(crypto);
        if (!established.Success || established.Session is null)
        {
            return EspMaterialSmokeResult.Fail(
                established.SessionId,
                established.FinalState,
                established.Error ?? "establishment failed");
        }

        var session = established.Session;
        var manager = new AikeSessionManager(
            crypto,
            new TrustedForSmokeTrustStore());

        try
        {
            var install = new EspInstallParameters(
                LocalIp: "192.168.95.1",
                RemoteIp: "192.168.113.199",
                Protocol: "50",
                EspProfile: EspProfiles.ManualSa.Name,
                InboundSpi: 0xA100,
                OutboundSpi: 0xB200,
                InboundCipherAlgorithm: "AES-128-CBC",
                InboundAuthAlgorithm: "HMAC-SHA256-128",
                OutboundCipherAlgorithm: "AES-128-CBC",
                OutboundAuthAlgorithm: "HMAC-SHA256-128",
                LifetimeSeconds: 3600,
                MaxBytes: 0);

            var material = manager.DeriveEspMaterial(session, install);
            return new EspMaterialSmokeResult(
                Success: true,
                FinalState: material.Protocol == "50" ? session.Phase : AikeSessionPhase.Failed,
                SessionId: session.SessionId,
                Protocol: material.Protocol,
                InboundSpi: material.InboundSpi,
                OutboundSpi: material.OutboundSpi,
                Error: null);
        }
        catch (Exception ex)
        {
            return EspMaterialSmokeResult.Fail(
                session.SessionId,
                session.Phase,
                ex.Message);
        }
    }

    /// <summary>
    /// Runs established path and returns a log-safe Manual-SA install plan boundary contract.
    /// </summary>
    public static ManualSaInstallPlanResult RunEstablishedAndBuildManualSaInstallPlan()
    {
        var crypto = new X25519HkdfLabAikeCrypto();
        var established = TryRunEstablishedRoundTrip(crypto);
        if (!established.Success || established.Session is null)
        {
            return ManualSaInstallPlanResult.Fail(
                established.SessionId,
                established.FinalState,
                established.Error ?? "establishment failed");
        }

        var session = established.Session;
        var manager = new AikeSessionManager(
            crypto,
            new TrustedForSmokeTrustStore());

        try
        {
            var install = new EspInstallParameters(
                LocalIp: "192.168.95.1",
                RemoteIp: "192.168.113.199",
                Protocol: "50",
                EspProfile: EspProfiles.ManualSa.Name,
                InboundSpi: 0xA100,
                OutboundSpi: 0xB200,
                InboundCipherAlgorithm: "AES-128-CBC",
                InboundAuthAlgorithm: "HMAC-SHA256-128",
                OutboundCipherAlgorithm: "AES-128-CBC",
                OutboundAuthAlgorithm: "HMAC-SHA256-128",
                LifetimeSeconds: 3600,
                MaxBytes: 0);

            var material = manager.DeriveEspMaterial(session, install);
            var plan = material.ToManualSaInstallPlan();
            var validation = ManualSaInstallPlanValidator.Validate(plan);
            if (!validation.IsValid)
            {
                return ManualSaInstallPlanResult.Invalid(session.SessionId, session.Phase, validation);
            }

            return ManualSaInstallPlanResult.Ok(session.SessionId, session.Phase, plan);
        }
        catch (Exception ex)
        {
            return ManualSaInstallPlanResult.Fail(
                session.SessionId,
                session.Phase,
                ex.Message);
        }
    }

    /// <summary>
    /// Runs plan contract boundary and validates it via manual-sa plan-file v1 serializer.
    /// </summary>
    public static ManualSaPlanFileRoundTripResult RunPlanFileBoundaryRoundTrip()
    {
        var planResult = RunEstablishedAndBuildManualSaInstallPlan();
        if (!planResult.Success || planResult.Plan is null)
        {
            return ManualSaPlanFileRoundTripResult.Fail(
                planResult.SessionId,
                planResult.FinalState,
                planResult.Error ?? "manual SA plan not available");
        }

        try
        {
            var serialized = ManualSaPlanFileSerializer.ToKeyValuePlan(planResult.Plan);
            var redacted = ManualSaPlanFileSerializer.ToRedactedKeyValuePlan(planResult.Plan);
            var validation = ManualSaPlanFileSerializer.ValidateRoundTrip(planResult.Plan, serialized);
            if (!validation.IsValid)
            {
                return ManualSaPlanFileRoundTripResult.Invalid(
                    planResult.SessionId,
                    planResult.FinalState,
                    validation);
            }

            return ManualSaPlanFileRoundTripResult.Ok(
                planResult.SessionId,
                planResult.FinalState,
                serialized,
                redacted,
                validation);
        }
        catch (Exception ex)
        {
            return ManualSaPlanFileRoundTripResult.Fail(
                planResult.SessionId,
                planResult.FinalState,
                ex.Message);
        }
    }

    /// <summary>
    /// Runs established path, resolves current dynamic endpoints, builds Manual-SA plan
    /// from those endpoint records, then validates plan-file v1 roundtrip.
    /// </summary>
    public static DynamicEndpointPlanFileRoundTripResult RunDynamicEndpointPlanFileBoundaryRoundTrip()
    {
        var established = TryRunEstablishedRoundTrip();
        if (!established.Success || established.Session is null)
        {
            return DynamicEndpointPlanFileRoundTripResult.Fail(
                established.SessionId,
                established.FinalState,
                established.Error ?? "establishment failed");
        }

        var session = established.Session;
        var now = DateTimeOffset.UtcNow;
        var directory = CreateSmokeEndpointDirectory(session, now);
        var localResolution = directory.ResolveLatest(session.LocalPeer.PeerId, now);
        if (!localResolution.Success || localResolution.Endpoint is null)
        {
            return DynamicEndpointPlanFileRoundTripResult.Fail(
                session.SessionId,
                session.Phase,
                localResolution.Error ?? "local endpoint resolution failed");
        }

        var remoteResolution = directory.ResolveLatest(session.RemotePeer.PeerId, now);
        if (!remoteResolution.Success || remoteResolution.Endpoint is null)
        {
            return DynamicEndpointPlanFileRoundTripResult.Fail(
                session.SessionId,
                session.Phase,
                remoteResolution.Error ?? "remote endpoint resolution failed");
        }

        var localSeqValidation = EndpointRecordValidator.Validate(localResolution.Endpoint, now, previousSeq: 1);
        if (!localSeqValidation.IsValid)
        {
            return DynamicEndpointPlanFileRoundTripResult.Fail(
                session.SessionId,
                session.Phase,
                localSeqValidation.Error ?? "local endpoint sequence validation failed");
        }

        var remoteSeqValidation = EndpointRecordValidator.Validate(remoteResolution.Endpoint, now, previousSeq: 5);
        if (!remoteSeqValidation.IsValid)
        {
            return DynamicEndpointPlanFileRoundTripResult.Fail(
                session.SessionId,
                session.Phase,
                remoteSeqValidation.Error ?? "remote endpoint sequence validation failed");
        }

        var manager = new AikeSessionManager(
            #pragma warning disable CS0618
            new DeterministicLabAikeCrypto(),
            #pragma warning restore CS0618
            new TrustedForSmokeTrustStore());

        try
        {
            var install = new EspInstallParameters(
                LocalIp: localResolution.Endpoint.Ip,
                RemoteIp: remoteResolution.Endpoint.Ip,
                Protocol: "50",
                EspProfile: EspProfiles.ManualSa.Name,
                InboundSpi: 0xA100,
                OutboundSpi: 0xB200,
                InboundCipherAlgorithm: "AES-128-CBC",
                InboundAuthAlgorithm: "HMAC-SHA256-128",
                OutboundCipherAlgorithm: "AES-128-CBC",
                OutboundAuthAlgorithm: "HMAC-SHA256-128",
                LifetimeSeconds: 3600,
                MaxBytes: 0);

            var material = manager.DeriveEspMaterial(session, install);
            var plan = material.ToManualSaInstallPlan(localResolution.Endpoint, remoteResolution.Endpoint);
            var planValidation = ManualSaInstallPlanValidator.Validate(plan);
            if (!planValidation.IsValid)
            {
                return DynamicEndpointPlanFileRoundTripResult.Fail(
                    session.SessionId,
                    session.Phase,
                    planValidation.Error ?? "manual SA plan validation failed");
            }

            var serialized = ManualSaPlanFileSerializer.ToKeyValuePlan(plan);
            var redacted = ManualSaPlanFileSerializer.ToRedactedKeyValuePlan(plan);
            var roundTripValidation = ManualSaPlanFileSerializer.ValidateRoundTrip(plan, serialized);
            if (!roundTripValidation.IsValid)
            {
                return DynamicEndpointPlanFileRoundTripResult.Fail(
                    session.SessionId,
                    session.Phase,
                    roundTripValidation.Error ?? "manual SA plan-file roundtrip failed");
            }

            return DynamicEndpointPlanFileRoundTripResult.Ok(
                session.SessionId,
                session.Phase,
                localResolution.Endpoint,
                remoteResolution.Endpoint,
                redacted,
                serialized.Length,
                roundTripValidation);
        }
        catch (Exception ex)
        {
            return DynamicEndpointPlanFileRoundTripResult.Fail(
                session.SessionId,
                session.Phase,
                ex.Message);
        }
    }

    private sealed class TrustedForSmokeTrustStore : IPeerTrustStore
    {
        public bool IsTrusted(string peerId, string keyId)
        {
            return true;
        }
    }

    private sealed record EstablishedRoundTripResult(
        bool Success,
        SessionState? Session,
        AikeSessionPhase FinalState,
        string SessionId,
        string? Error);

    private static EstablishedRoundTripResult TryRunEstablishedRoundTrip(
        IAikeCrypto? cryptoOverride = null,
        (PeerDescriptor initiator, PeerDescriptor responder)? peerOverride = null)
    {
        IAikeCrypto crypto;
        if (cryptoOverride is not null)
        {
            crypto = cryptoOverride;
        }
        else
        {
            #pragma warning disable CS0618
            crypto = new DeterministicLabAikeCrypto();
            #pragma warning restore CS0618
        }

        var trustStore = new TrustedForSmokeTrustStore();
        var initiator = new AikeSessionManager(crypto, trustStore);
        var responder = new AikeSessionManager(crypto, trustStore);

        var now = DateTimeOffset.UtcNow;
        var notAfter = now.AddMinutes(5);

        var peers = peerOverride ?? CreateSmokePeers(now);
        var options = new AikeSessionOptions(
            RouteId: "lab-route",
            EspProfile: EspProfiles.ManualSa.Name,
            Now: now,
            NotAfter: notAfter);

        var initiatorStart = initiator.CreateSession(
            localPeer: peers.initiator,
            remotePeer: peers.responder,
            options: options);

        var ke1Payload = AikeMessageCodec.Encode(initiatorStart.FirstMessage);
        var decodedKe1 = AikeMessageCodec.Decode(ke1Payload);
        var responderInitial = SessionState.NewResponder(
            sessionId: decodedKe1.SessionId,
            routeId: decodedKe1.RouteId,
            localPeer: peers.responder,
            remotePeer: peers.initiator,
            createdAt: now,
            notAfter: notAfter);

        var responderAfterKe1 = responder.ProcessMessage(responderInitial, decodedKe1);
        if (!responderAfterKe1.Success || responderAfterKe1.OutboundMessage is null || responderAfterKe1.Session is null)
        {
            return new EstablishedRoundTripResult(
                Success: false,
                Session: responderAfterKe1.Session,
                FinalState: AikeSessionPhase.Ke1Sent,
                SessionId: initiatorStart.Session.SessionId,
                Error: responderAfterKe1.Error ?? "responder failed to process KE1");
        }

        var ke2Bytes = AikeMessageCodec.EncodeToBytes(responderAfterKe1.OutboundMessage);
        var decodedKe2 = AikeMessageCodec.Decode(ke2Bytes);
        var initiatorAfterKe2 = initiator.ProcessMessage(initiatorStart.Session, decodedKe2);
        if (!initiatorAfterKe2.Success || initiatorAfterKe2.Session is null)
        {
            return new EstablishedRoundTripResult(
                Success: false,
                Session: initiatorAfterKe2.Session,
                FinalState: AikeSessionPhase.Ke1Sent,
                SessionId: initiatorStart.Session.SessionId,
                Error: initiatorAfterKe2.Error ?? "initiator failed to process KE2");
        }

        var responderAfterKe2 = responderAfterKe1.Session.WithPhase(AikeSessionPhase.Ke2Sent);
        if (!responderAfterKe2.HasEstablishedKeyExchange)
        {
            return new EstablishedRoundTripResult(
                Success: false,
                Session: responderAfterKe2,
                FinalState: initiatorAfterKe2.Session.Phase,
                SessionId: initiatorStart.Session.SessionId,
                Error: "responder state missing established key exchange");
        }

        var finalPayload = new AikePlainPayload(
            EphemeralPublicKey: responderAfterKe2.LocalEphemeralPublicKey,
            Metadata: new Dictionary<string, string>
            {
                ["aike_stage"] = "establish",
                ["final"] = "install_ack"
            });

        var finalUnsigned = AikeMessage.CreateUnsigned(
            AikeMessageType.InstallAck,
            responderAfterKe2,
            finalPayload,
            now,
            notAfter);

        var finalSigned = crypto.SignEnvelope(responderAfterKe2.LocalPeer, finalUnsigned, responderAfterKe2.TranscriptHash);
        var finalWire = AikeMessageCodec.Decode(AikeMessageCodec.EncodeToBytes(finalSigned));
        var initiatorAfterEstablished = initiator.ProcessMessage(initiatorAfterKe2.Session, finalWire);
        if (!initiatorAfterEstablished.Success || initiatorAfterEstablished.Session is null)
        {
            return new EstablishedRoundTripResult(
                Success: false,
                Session: initiatorAfterEstablished.Session,
                FinalState: initiatorAfterKe2.Session.Phase,
                SessionId: initiatorStart.Session.SessionId,
                Error: initiatorAfterEstablished.Error ?? "initiator failed to establish");
        }

        if (!initiatorAfterEstablished.Session.HasEstablishedKeyExchange)
        {
            return new EstablishedRoundTripResult(
                Success: false,
                Session: initiatorAfterEstablished.Session,
                FinalState: initiatorAfterEstablished.Session.Phase,
                SessionId: initiatorAfterEstablished.Session.SessionId,
                Error: "initiator did not establish key exchange");
        }

        if (initiatorAfterEstablished.Session.Phase != AikeSessionPhase.Established)
        {
            return new EstablishedRoundTripResult(
                Success: false,
                Session: initiatorAfterEstablished.Session,
                FinalState: initiatorAfterEstablished.Session.Phase,
                SessionId: initiatorAfterEstablished.Session.SessionId,
                Error: $"unexpected final phase: {initiatorAfterEstablished.Session.Phase}");
        }

        return new EstablishedRoundTripResult(
            Success: true,
            Session: initiatorAfterEstablished.Session,
            FinalState: initiatorAfterEstablished.Session.Phase,
            SessionId: initiatorAfterEstablished.Session.SessionId,
            Error: null);
    }

    private static EndpointDirectorySnapshot CreateSmokeEndpointDirectory(
        SessionState session,
        DateTimeOffset now)
    {
        return new EndpointDirectorySnapshot(
        [
            new EndpointRecord(
                NodeId: session.LocalPeer.PeerId,
                Ip: "192.168.95.250",
                Port: null,
                SelectorProtocol: "icmp",
                Source: "local-interface-snapshot",
                Sequence: 1,
                ValidFrom: now.AddMinutes(-10),
                ExpiresAt: now.AddMinutes(10),
                Signature: "lab-signature-local-1"),

            new EndpointRecord(
                NodeId: session.LocalPeer.PeerId,
                Ip: "192.168.95.1",
                Port: null,
                SelectorProtocol: "icmp",
                Source: "local-interface-snapshot",
                Sequence: 2,
                ValidFrom: now.AddMinutes(-1),
                ExpiresAt: now.AddMinutes(10),
                Signature: "lab-signature-local-2"),

            new EndpointRecord(
                NodeId: session.RemotePeer.PeerId,
                Ip: "192.168.113.198",
                Port: null,
                SelectorProtocol: "icmp",
                Source: "aike-endpoint-update",
                Sequence: 5,
                ValidFrom: now.AddMinutes(-10),
                ExpiresAt: now.AddMinutes(10),
                Signature: "lab-signature-remote-5"),

            new EndpointRecord(
                NodeId: session.RemotePeer.PeerId,
                Ip: "192.168.113.199",
                Port: null,
                SelectorProtocol: "icmp",
                Source: "aike-endpoint-update",
                Sequence: 6,
                ValidFrom: now.AddMinutes(-1),
                ExpiresAt: now.AddMinutes(10),
                Signature: "lab-signature-remote-6")
        ]);
    }

    private static (PeerDescriptor initiator, PeerDescriptor responder) CreateSmokePeers(DateTimeOffset now)
    {
        var initiatorPeer = new PeerDescriptor(
            PeerId: "initiator-poc",
            KeyId: "init-key-lab",
            PublicIdentityKey: "lab-public-init",
            ReceiverHint: "127.0.0.1:0",
            TrustSource: PeerTrustSource.TofuForLabOnly,
            ValidFrom: now.AddMinutes(-10),
            ValidTo: now.AddDays(1));

        var responderPeer = new PeerDescriptor(
            PeerId: "responder-poc",
            KeyId: "resp-key-lab",
            PublicIdentityKey: "lab-public-resp",
            ReceiverHint: "127.0.0.1:0",
            TrustSource: PeerTrustSource.TofuForLabOnly,
            ValidFrom: now.AddMinutes(-10),
            ValidTo: now.AddDays(1));

        return (initiatorPeer, responderPeer);
    }
}
