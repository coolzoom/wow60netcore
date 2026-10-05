using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Net;

/// <summary>
/// Client side of the SRP6 variant used by the 1.x logon protocol (k = 3, SHA-1, little-endian numbers,
/// 40-byte session key built from the interleaved hashes of the even and odd bytes of S).
/// </summary>
public sealed class Srp6
{
    public const int KeySize = 32;

    private Srp6(byte[] a, byte[] m1, byte[] m2, byte[] sessionKey)
    {
        PublicA = a;
        ClientProof = m1;
        ExpectedServerProof = m2;
        SessionKey = sessionKey;
    }

    public byte[] PublicA { get; }
    public byte[] ClientProof { get; }
    public byte[] ExpectedServerProof { get; }
    /// <summary>K: 40 bytes shared with the server; also the world-socket header key.</summary>
    public byte[] SessionKey { get; }

    public static Srp6 Compute(string account, string password, ReadOnlySpan<byte> serverB, ReadOnlySpan<byte> generator,
        ReadOnlySpan<byte> modulus, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> privateA = default)
    {
        var n = ToInt(modulus);
        var g = ToInt(generator);
        var b = ToInt(serverB);
        if (n.IsZero || (b % n).IsZero)
            throw new InvalidDataException("Server sent an invalid SRP6 public key.");

        var bBytes = ToBytes(b, KeySize);
        BigInteger a, publicA;
        do
        {
            a = ToInt(privateA.IsEmpty ? RandomNumberGenerator.GetBytes(19) : privateA);
            publicA = BigInteger.ModPow(g, a, n);
        } while ((publicA % n).IsZero && privateA.IsEmpty);
        var aBytes = ToBytes(publicA, KeySize);

        var u = ToInt(SHA1.HashData([.. Shortest(aBytes), .. Shortest(bBytes)]));
        var x = PrivateKey(account, password, salt);
        var gx = BigInteger.ModPow(g, x, n);
        var baseValue = ((b - 3 * gx) % n + n) % n;
        var s = BigInteger.ModPow(baseValue, a + u * x, n);
        var sessionKey = InterleavedHash(ToBytes(s, KeySize));

        var hN = SHA1.HashData(modulus);
        var hG = SHA1.HashData(generator);
        for (var i = 0; i < hN.Length; i++)
            hN[i] ^= hG[i];
        var hUser = SHA1.HashData(Encoding.UTF8.GetBytes(account.ToUpperInvariant()));
        var m1 = SHA1.HashData([.. Shortest(hN), .. hUser, .. Shortest(salt), .. Shortest(aBytes), .. Shortest(bBytes), .. Shortest(sessionKey)]);
        var m2 = SHA1.HashData([.. Shortest(aBytes), .. Shortest(m1), .. Shortest(sessionKey)]);
        return new Srp6(aBytes, m1, m2, sessionKey);
    }

    /// <summary>x = SHA1(salt | SHA1(ACCOUNT:PASSWORD)).</summary>
    public static BigInteger PrivateKey(string account, string password, ReadOnlySpan<byte> salt)
    {
        var inner = SHA1.HashData(Encoding.UTF8.GetBytes($"{account}:{password}".ToUpperInvariant()));
        return ToInt(SHA1.HashData([.. salt, .. inner]));
    }

    public static byte[] InterleavedHash(byte[] s)
    {
        var even = new byte[s.Length / 2];
        var odd = new byte[s.Length / 2];
        for (var i = 0; i < even.Length; i++)
        {
            even[i] = s[2 * i];
            odd[i] = s[2 * i + 1];
        }
        var h1 = SHA1.HashData(even);
        var h2 = SHA1.HashData(odd);
        var key = new byte[40];
        for (var i = 0; i < 20; i++)
        {
            key[2 * i] = h1[i];
            key[2 * i + 1] = h2[i];
        }
        return key;
    }

    /// <summary>
    /// The logon servers hash these values as big numbers in their shortest form (no high zero bytes), so a padded
    /// value whose top byte happens to be zero would fail about one login in a hundred.
    /// </summary>
    private static byte[] Shortest(ReadOnlySpan<byte> littleEndian)
    {
        var length = littleEndian.Length;
        while (length > 1 && littleEndian[length - 1] == 0)
            length--;
        return littleEndian[..length].ToArray();
    }

    public static BigInteger ToInt(ReadOnlySpan<byte> littleEndian) => new(littleEndian, isUnsigned: true, isBigEndian: false);

    public static byte[] ToBytes(BigInteger value, int size)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (bytes.Length >= size)
            return bytes;
        Array.Resize(ref bytes, size);
        return bytes;
    }
}
