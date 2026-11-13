namespace AdaptiveClient.Core.Models;

public sealed record SessionState(
    string SessionId,
    string RouteId,
    AikeSessionPhase Phase,
    AikeRole Role,
    PeerDescriptor LocalPeer,
    PeerDescriptor RemotePeer,
    ulong LocalSequence,
    ulong RemoteSequence,
    string LocalNonce,
    string? RemoteNonce,
    string? LocalEphemeralPublicKey,
    string? RemoteEphemeralPublicKey,
    string TranscriptHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset NotAfter,
    int SaLifetimeSeconds)
{
    /// <summary>
    /// Lightweight phase tracking used by the skeleton state machine.
    /// </summary>
    public bool HasEstablishedKeyExchange =>
        !string.IsNullOrWhiteSpace(LocalEphemeralPublicKey) &&
        !string.IsNullOrWhiteSpace(RemoteEphemeralPublicKey);

    public bool IsExpired(DateTimeOffset now) => now >= NotAfter;

    public static SessionState NewInitiator(
        string sessionId,
        string routeId,
        PeerDescriptor localPeer,
        PeerDescriptor remotePeer,
        DateTimeOffset createdAt,
        DateTimeOffset notAfter)
    {
        return new SessionState(
            SessionId: sessionId,
            RouteId: routeId,
            Phase: AikeSessionPhase.Created,
            Role: AikeRole.Initiator,
            LocalPeer: localPeer,
            RemotePeer: remotePeer,
            LocalSequence: 1,
            RemoteSequence: 0,
            LocalNonce: Guid.NewGuid().ToString("N"),
            RemoteNonce: null,
            LocalEphemeralPublicKey: null,
            RemoteEphemeralPublicKey: null,
            TranscriptHash: $"AIKE-v1:{sessionId}",
            CreatedAt: createdAt,
            NotAfter: notAfter,
            SaLifetimeSeconds: 3600);
    }

    public static SessionState NewResponder(
        string sessionId,
        string routeId,
        PeerDescriptor localPeer,
        PeerDescriptor remotePeer,
        DateTimeOffset createdAt,
        DateTimeOffset notAfter)
    {
        return new SessionState(
            SessionId: sessionId,
            RouteId: routeId,
            Phase: AikeSessionPhase.Created,
            Role: AikeRole.Responder,
            LocalPeer: localPeer,
            RemotePeer: remotePeer,
            LocalSequence: 1,
            RemoteSequence: 0,
            LocalNonce: Guid.NewGuid().ToString("N"),
            RemoteNonce: null,
            LocalEphemeralPublicKey: null,
            RemoteEphemeralPublicKey: null,
            TranscriptHash: $"AIKE-v1:{sessionId}",
            CreatedAt: createdAt,
            NotAfter: notAfter,
            SaLifetimeSeconds: 3600);
    }

    public SessionState WithPhase(AikeSessionPhase phase) => this with { Phase = phase };

    public SessionState WithNextLocalSequence() => this with { LocalSequence = LocalSequence + 1 };

    public SessionState WithRemoteSequence(ulong sequence) => this with { RemoteSequence = sequence };

    public SessionState WithTranscript(string transcriptHash) => this with { TranscriptHash = transcriptHash };

    public SessionState WithEstablishedKeyExchange(string localEphemeralPublicKey, string remoteEphemeralPublicKey) =>
        this with
        {
            LocalEphemeralPublicKey = localEphemeralPublicKey,
            RemoteEphemeralPublicKey = remoteEphemeralPublicKey
        };

    public SessionState WithRemoteEphemeralPublicKey(string remoteEphemeralPublicKey) =>
        this with { RemoteEphemeralPublicKey = remoteEphemeralPublicKey };

    public SessionState WithLocalEphemeralPublicKey(string localEphemeralPublicKey) =>
        this with { LocalEphemeralPublicKey = localEphemeralPublicKey };

    public SessionState Fail(string reason)
    {
        return this with { Phase = AikeSessionPhase.Failed };
    }
}

public enum AikeSessionPhase
{
    Created,
    Ke1Sent,
    Ke1Received,
    Ke2Sent,
    Established,
    Failed
}

public enum AikeRole
{
    Initiator,
    Responder,
    Relay
}

public sealed record AikeSessionOptions(
    string RouteId,
    string EspProfile,
    DateTimeOffset Now,
    DateTimeOffset NotAfter);

public sealed record AikeSessionStart(
    SessionState Session,
    AikeMessage FirstMessage);
