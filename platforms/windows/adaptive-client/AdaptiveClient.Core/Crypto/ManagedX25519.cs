using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AdaptiveClient.Core.Crypto;

internal static class ManagedX25519
{
    public const int KeySize = 32;

    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger A24 = 121665;

    public static byte[] GeneratePrivateKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] PublicFromPrivate(ReadOnlySpan<byte> privateKey)
    {
        Span<byte> basePoint = stackalloc byte[KeySize];
        basePoint[0] = 9;
        return ScalarMult(privateKey, basePoint);
    }

    public static byte[] SharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        var secret = ScalarMult(privateKey, publicKey);
        if (IsAllZero(secret))
        {
            throw new CryptographicException("X25519 shared secret is all-zero.");
        }

        return secret;
    }

    public static byte[] PrivateKeyFromDeterministicSeed(string seed)
    {
        var privateKey = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        privateKey[0] &= 248;
        privateKey[31] &= 127;
        privateKey[31] |= 64;
        return privateKey;
    }

    public static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uCoordinate)
    {
        if (scalar.Length != KeySize)
        {
            throw new ArgumentException("X25519 scalar must be 32 bytes.", nameof(scalar));
        }

        if (uCoordinate.Length != KeySize)
        {
            throw new ArgumentException("X25519 u-coordinate must be 32 bytes.", nameof(uCoordinate));
        }

        var k = scalar.ToArray();
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;

        var u = uCoordinate.ToArray();
        u[31] &= 127;

        var x1 = DecodeLittleEndian(u);
        var x2 = BigInteger.One;
        var z2 = BigInteger.Zero;
        var x3 = x1;
        var z3 = BigInteger.One;
        var swap = 0;

        for (var t = 254; t >= 0; t--)
        {
            var kt = (k[t >> 3] >> (t & 7)) & 1;
            swap ^= kt;
            if (swap != 0)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }

            swap = kt;

            var a = Mod(x2 + z2);
            var aa = Mod(a * a);
            var b = Mod(x2 - z2);
            var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3);
            var d = Mod(x3 - z3);
            var da = Mod(d * a);
            var cb = Mod(c * b);
            x3 = Mod((da + cb) * (da + cb));
            z3 = Mod(x1 * Mod((da - cb) * (da - cb)));
            x2 = Mod(aa * bb);
            z2 = Mod(e * Mod(aa + (A24 * e)));
        }

        if (swap != 0)
        {
            (x2, x3) = (x3, x2);
            (z2, z3) = (z3, z2);
        }

        var result = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
        return EncodeLittleEndian(result);
    }

    private static BigInteger DecodeLittleEndian(ReadOnlySpan<byte> value) =>
        new(value, isUnsigned: true, isBigEndian: false);

    private static byte[] EncodeLittleEndian(BigInteger value)
    {
        var output = new byte[KeySize];
        var encoded = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        encoded.AsSpan(0, Math.Min(encoded.Length, output.Length)).CopyTo(output);
        return output;
    }

    private static BigInteger Mod(BigInteger value)
    {
        value %= P;
        return value.Sign < 0 ? value + P : value;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        var aggregate = 0;
        foreach (var item in value)
        {
            aggregate |= item;
        }

        return aggregate == 0;
    }
}
