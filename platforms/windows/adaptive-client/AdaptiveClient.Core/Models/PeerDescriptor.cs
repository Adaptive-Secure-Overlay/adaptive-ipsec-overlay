namespace AdaptiveClient.Core.Models;

public sealed record PeerDescriptor(
    string PeerId,
    string KeyId,
    /// <summary>
    /// Public Ed25519 identity key of the peer, pinned or obtained from trusted enrollment authority.
    /// </summary>
    string PublicIdentityKey,
    string ReceiverHint,
    PeerTrustSource TrustSource,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo)
{
    public bool IsPinnedOrTrusted =>
        TrustSource is PeerTrustSource.Pinned or PeerTrustSource.EnrollmentAuthority;
}

public enum PeerTrustSource
{
    Pinned,
    EnrollmentAuthority,
    TofuForLabOnly
}
