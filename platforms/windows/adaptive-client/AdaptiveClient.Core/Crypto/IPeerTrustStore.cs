namespace AdaptiveClient.Core.Crypto;

public interface IPeerTrustStore
{
    bool IsTrusted(string peerId, string keyId);
}
