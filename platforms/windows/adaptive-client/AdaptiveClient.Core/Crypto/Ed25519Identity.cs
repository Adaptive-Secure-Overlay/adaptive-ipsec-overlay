using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace AdaptiveClient.Core.Crypto;

public interface IAikeIdentityKeyProvider
{
    bool TryExportPrivateSeed(string peerId, string keyId, out byte[] privateSeed);
}

public sealed class InMemoryIdentityKeyProvider : IAikeIdentityKeyProvider
{
    private readonly Dictionary<string, byte[]> seeds = new(StringComparer.Ordinal);

    public void Add(string peerId, string keyId, byte[] privateSeed)
    {
        seeds[MapKey(peerId, keyId)] = privateSeed.ToArray();
    }

    public bool TryExportPrivateSeed(string peerId, string keyId, out byte[] privateSeed)
    {
        if (seeds.TryGetValue(MapKey(peerId, keyId), out var seed))
        {
            privateSeed = seed.ToArray();
            return true;
        }

        privateSeed = [];
        return false;
    }

    private static string MapKey(string peerId, string keyId) => $"{peerId}\n{keyId}";
}

public static class Ed25519Identity
{
    public const string PublicKeyPrefix = "ed25519:";
    public const string SignatureAlgorithm = "Ed25519";
    public const int SeedSize = 32;
    public const int PublicKeySize = 32;

    public static byte[] CreateSeed() => RandomNumberGenerator.GetBytes(SeedSize);

    public static string GetPublicIdentityKey(ReadOnlySpan<byte> privateSeed)
    {
        var publicKey = GetPublicKey(privateSeed);
        return PublicKeyPrefix + Base64Url(publicKey);
    }

    public static byte[] GetPublicKey(ReadOnlySpan<byte> privateSeed)
    {
        ValidateSeed(privateSeed);
        var seed = privateSeed.ToArray();
        try
        {
            return new Ed25519PrivateKeyParameters(seed, 0)
                .GeneratePublicKey()
                .GetEncoded();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public static string Sign(ReadOnlySpan<byte> privateSeed, ReadOnlySpan<byte> message)
    {
        ValidateSeed(privateSeed);
        var seed = privateSeed.ToArray();
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(forSigning: true, new Ed25519PrivateKeyParameters(seed, 0));
            signer.BlockUpdate(message.ToArray(), 0, message.Length);
            return Base64Url(signer.GenerateSignature());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public static bool Verify(string publicIdentityKey, ReadOnlySpan<byte> message, string signature)
    {
        if (!TryDecodePublicKey(publicIdentityKey, out var publicKey)
            || !TryBase64UrlDecode(signature, out var signatureBytes))
        {
            return false;
        }

        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message.ToArray(), 0, message.Length);
        return verifier.VerifySignature(signatureBytes);
    }

    public static bool IsEd25519PublicIdentityKey(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(PublicKeyPrefix, StringComparison.Ordinal);

    public static bool TryDecodePublicKey(string publicIdentityKey, out byte[] publicKey)
    {
        publicKey = [];
        if (!IsEd25519PublicIdentityKey(publicIdentityKey))
        {
            return false;
        }

        if (!TryBase64UrlDecode(publicIdentityKey[PublicKeyPrefix.Length..], out var decoded)
            || decoded.Length != PublicKeySize)
        {
            return false;
        }

        publicKey = decoded;
        return true;
    }

    private static void ValidateSeed(ReadOnlySpan<byte> privateSeed)
    {
        if (privateSeed.Length != SeedSize)
        {
            throw new ArgumentException($"Ed25519 private seed must be {SeedSize} bytes.", nameof(privateSeed));
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
