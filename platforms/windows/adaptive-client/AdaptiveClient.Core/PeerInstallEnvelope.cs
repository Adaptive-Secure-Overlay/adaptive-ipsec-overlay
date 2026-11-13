using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdaptiveClient.Core.Crypto;

namespace AdaptiveClient.Core;

public sealed record PeerInstallEncryptedEnvelope(
    int EnvelopeVersion,
    string EnvelopeMode,
    string EnvelopeEncryption,
    string EnvelopeKdf,
    string EnvelopeSenderId,
    string EnvelopeRecipientId,
    string SessionId,
    string RouteId,
    string? EnvelopeEphemeralPublicKey,
    string EnvelopeNonce,
    string EnvelopeCiphertext,
    string EnvelopeTag);

public static class PeerInstallEnvelope
{
    public const int Version = 1;
    public const string ModePsk = "psk";
    public const string ModeX25519 = "x25519";
    public const string Encryption = "AES-256-GCM";
    public const string Kdf = "HKDF-SHA256";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public static PeerInstallEncryptedEnvelope Encrypt(
        IReadOnlyDictionary<string, string> payload,
        string envelopeSecret,
        string senderId,
        string recipientId,
        string sessionId,
        string routeId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateSecret(envelopeSecret);
        ValidateRequired(senderId, nameof(senderId));
        ValidateRequired(recipientId, nameof(recipientId));
        ValidateRequired(sessionId, nameof(sessionId));
        ValidateRequired(routeId, nameof(routeId));

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new SortedDictionary<string, string>(
                payload.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            JsonOptions));
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var key = DeriveKey(envelopeSecret, senderId, recipientId, sessionId, routeId);
        var aad = BuildAad(ModePsk, senderId, recipientId, sessionId, routeId, ephemeralPublicKey: null);

        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return new PeerInstallEncryptedEnvelope(
                EnvelopeVersion: Version,
                EnvelopeMode: ModePsk,
                EnvelopeEncryption: Encryption,
                EnvelopeKdf: Kdf,
                EnvelopeSenderId: senderId,
                EnvelopeRecipientId: recipientId,
                SessionId: sessionId,
                RouteId: routeId,
                EnvelopeEphemeralPublicKey: null,
                EnvelopeNonce: Convert.ToBase64String(nonce),
                EnvelopeCiphertext: Convert.ToBase64String(ciphertext),
                EnvelopeTag: Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static PeerInstallEncryptedEnvelope EncryptForRecipient(
        IReadOnlyDictionary<string, string> payload,
        string recipientPublicKey,
        string senderId,
        string recipientId,
        string sessionId,
        string routeId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateRequired(recipientPublicKey, nameof(recipientPublicKey));
        ValidateRequired(senderId, nameof(senderId));
        ValidateRequired(recipientId, nameof(recipientId));
        ValidateRequired(sessionId, nameof(sessionId));
        ValidateRequired(routeId, nameof(routeId));

        var recipientPublic = X25519EnvelopeIdentity.DecodePublicKey(recipientPublicKey);
        var ephemeralPrivate = X25519EnvelopeIdentity.CreatePrivateKey();
        var ephemeralPublic = X25519EnvelopeIdentity.GetPublicIdentityKey(ephemeralPrivate);
        var sharedSecret = ManagedX25519.SharedSecret(ephemeralPrivate, recipientPublic);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new SortedDictionary<string, string>(
                payload.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            JsonOptions));
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var key = DeriveX25519Key(sharedSecret, senderId, recipientId, sessionId, routeId, ephemeralPublic, recipientPublicKey);
        var aad = BuildAad(ModeX25519, senderId, recipientId, sessionId, routeId, ephemeralPublic);

        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return new PeerInstallEncryptedEnvelope(
                EnvelopeVersion: Version,
                EnvelopeMode: ModeX25519,
                EnvelopeEncryption: Encryption,
                EnvelopeKdf: Kdf,
                EnvelopeSenderId: senderId,
                EnvelopeRecipientId: recipientId,
                SessionId: sessionId,
                RouteId: routeId,
                EnvelopeEphemeralPublicKey: ephemeralPublic,
                EnvelopeNonce: Convert.ToBase64String(nonce),
                EnvelopeCiphertext: Convert.ToBase64String(ciphertext),
                EnvelopeTag: Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recipientPublic);
            CryptographicOperations.ZeroMemory(ephemeralPrivate);
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static IReadOnlyDictionary<string, string> Decrypt(
        PeerInstallEncryptedEnvelope envelope,
        string envelopeSecret)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateSecret(envelopeSecret);
        if (envelope.EnvelopeVersion != Version)
        {
            throw new InvalidOperationException($"Unsupported peer install envelope version: {envelope.EnvelopeVersion}.");
        }

        if (!string.Equals(NormalizeMode(envelope.EnvelopeMode), ModePsk, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Peer install envelope is not in PSK mode.");
        }

        if (!string.Equals(envelope.EnvelopeEncryption, Encryption, StringComparison.Ordinal)
            || !string.Equals(envelope.EnvelopeKdf, Kdf, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsupported peer install envelope crypto profile.");
        }

        var nonce = Convert.FromBase64String(envelope.EnvelopeNonce);
        var ciphertext = Convert.FromBase64String(envelope.EnvelopeCiphertext);
        var tag = Convert.FromBase64String(envelope.EnvelopeTag);
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(
            envelopeSecret,
            envelope.EnvelopeSenderId,
            envelope.EnvelopeRecipientId,
            envelope.SessionId,
            envelope.RouteId);
        var aad = BuildAad(
            ModePsk,
            envelope.EnvelopeSenderId,
            envelope.EnvelopeRecipientId,
            envelope.SessionId,
            envelope.RouteId,
            envelope.EnvelopeEphemeralPublicKey);

        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext, JsonOptions);
            return values is null
                ? throw new InvalidOperationException("Peer install envelope plaintext was empty.")
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static IReadOnlyDictionary<string, string> DecryptForRecipient(
        PeerInstallEncryptedEnvelope envelope,
        ReadOnlySpan<byte> recipientPrivateKey,
        string recipientPublicKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EnvelopeVersion != Version)
        {
            throw new InvalidOperationException($"Unsupported peer install envelope version: {envelope.EnvelopeVersion}.");
        }

        if (!string.Equals(NormalizeMode(envelope.EnvelopeMode), ModeX25519, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Peer install envelope is not in X25519 mode.");
        }

        if (!string.Equals(envelope.EnvelopeEncryption, Encryption, StringComparison.Ordinal)
            || !string.Equals(envelope.EnvelopeKdf, Kdf, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsupported peer install envelope crypto profile.");
        }

        if (string.IsNullOrWhiteSpace(envelope.EnvelopeEphemeralPublicKey))
        {
            throw new InvalidOperationException("X25519 peer install envelope is missing ephemeral public key.");
        }

        var ephemeralPublic = X25519EnvelopeIdentity.DecodePublicKey(envelope.EnvelopeEphemeralPublicKey);
        var sharedSecret = ManagedX25519.SharedSecret(recipientPrivateKey, ephemeralPublic);
        var nonce = Convert.FromBase64String(envelope.EnvelopeNonce);
        var ciphertext = Convert.FromBase64String(envelope.EnvelopeCiphertext);
        var tag = Convert.FromBase64String(envelope.EnvelopeTag);
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveX25519Key(
            sharedSecret,
            envelope.EnvelopeSenderId,
            envelope.EnvelopeRecipientId,
            envelope.SessionId,
            envelope.RouteId,
            envelope.EnvelopeEphemeralPublicKey,
            recipientPublicKey);
        var aad = BuildAad(
            ModeX25519,
            envelope.EnvelopeSenderId,
            envelope.EnvelopeRecipientId,
            envelope.SessionId,
            envelope.RouteId,
            envelope.EnvelopeEphemeralPublicKey);

        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext, JsonOptions);
            return values is null
                ? throw new InvalidOperationException("Peer install envelope plaintext was empty.")
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeralPublic);
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(
        string envelopeSecret,
        string senderId,
        string recipientId,
        string sessionId,
        string routeId)
    {
        var ikm = Encoding.UTF8.GetBytes(envelopeSecret);
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"AIKE-peer-install-envelope-v1|{senderId}|{recipientId}|{sessionId}|{routeId}"));
        var prk = Hmac(salt, ikm);
        try
        {
            return HkdfExpand(prk, "AIKE peer-install envelope AES-256-GCM", 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(prk);
        }
    }

    private static byte[] DeriveX25519Key(
        byte[] sharedSecret,
        string senderId,
        string recipientId,
        string sessionId,
        string routeId,
        string ephemeralPublicKey,
        string recipientPublicKey)
    {
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"AIKE-peer-install-envelope-x25519-v1|{senderId}|{recipientId}|{sessionId}|{routeId}|{ephemeralPublicKey}|{recipientPublicKey}"));
        var prk = Hmac(salt, sharedSecret);
        try
        {
            return HkdfExpand(prk, "AIKE peer-install X25519 envelope AES-256-GCM", 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(prk);
        }
    }

    private static byte[] HkdfExpand(byte[] prk, string info, int length)
    {
        var infoBytes = Encoding.UTF8.GetBytes(info);
        var okm = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;

        while (offset < length)
        {
            var input = new byte[previous.Length + infoBytes.Length + 1];
            Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
            Buffer.BlockCopy(infoBytes, 0, input, previous.Length, infoBytes.Length);
            input[^1] = counter++;
            previous = Hmac(prk, input);
            var take = Math.Min(previous.Length, length - offset);
            Buffer.BlockCopy(previous, 0, okm, offset, take);
            offset += take;
            CryptographicOperations.ZeroMemory(input);
        }

        CryptographicOperations.ZeroMemory(previous);
        return okm;
    }

    private static byte[] Hmac(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private static byte[] BuildAad(
        string mode,
        string senderId,
        string recipientId,
        string sessionId,
        string routeId,
        string? ephemeralPublicKey) =>
        Encoding.UTF8.GetBytes(
            $"version={Version}|mode={NormalizeMode(mode)}|enc={Encryption}|kdf={Kdf}|sender={senderId}|recipient={recipientId}|session={sessionId}|route={routeId}|eph={ephemeralPublicKey ?? string.Empty}");

    private static void ValidateSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 16)
        {
            throw new InvalidOperationException("Peer install envelope secret must be at least 16 characters.");
        }
    }

    private static void ValidateRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }
    }

    private static string NormalizeMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? ModePsk : mode.Trim().ToLowerInvariant();
}

public static class X25519EnvelopeIdentity
{
    public const string PublicKeyPrefix = "x25519:";
    public const int PrivateKeySize = ManagedX25519.KeySize;
    public const int PublicKeySize = ManagedX25519.KeySize;

    public static byte[] CreatePrivateKey() => ManagedX25519.GeneratePrivateKey();

    public static byte[] DerivePrivateKey(ReadOnlySpan<byte> identitySeed)
    {
        if (identitySeed.Length == 0)
        {
            throw new ArgumentException("identity seed is required.", nameof(identitySeed));
        }

        var seed = identitySeed.ToArray();
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("AIKE-peer-install-envelope-x25519-identity-v1"));
        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                seed,
                PrivateKeySize,
                salt,
                Encoding.UTF8.GetBytes("peer-install-envelope-x25519-private-key"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static string GetPublicIdentityKey(ReadOnlySpan<byte> privateKey) =>
        PublicKeyPrefix + Base64Url(ManagedX25519.PublicFromPrivate(privateKey));

    public static bool IsX25519PublicIdentityKey(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(PublicKeyPrefix, StringComparison.Ordinal);

    public static byte[] DecodePublicKey(string publicIdentityKey)
    {
        if (!TryDecodePublicKey(publicIdentityKey, out var publicKey))
        {
            throw new InvalidOperationException("Invalid X25519 public identity key.");
        }

        return publicKey;
    }

    public static bool TryDecodePublicKey(string publicIdentityKey, out byte[] publicKey)
    {
        publicKey = [];
        if (!IsX25519PublicIdentityKey(publicIdentityKey))
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
