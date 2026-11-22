using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace SlotGuard;

internal static class AuthenticatorCodes
{
    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string FormatKey(ReadOnlySpan<byte> secret)
    {
        Span<char> result = stackalloc char[32];
        var bits = 0;
        var value = 0;
        var position = 0;
        foreach (var part in secret)
        {
            value = (value << 8) | part;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result[position++] = Base32[(value >> bits) & 31];
            }
        }
        return new string(result[..position]);
    }

    public static long? Match(ReadOnlySpan<byte> secret, string? input, DateTimeOffset now, long? lastUsed = null)
    {
        if (input is not { Length: > 0 and <= 16 }) return null;
        var code = input.Replace(" ", "").Trim();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return null;

        var current = now.ToUnixTimeSeconds() / 30;
        Span<byte> supplied = stackalloc byte[6];
        for (var index = 0; index < supplied.Length; index++) supplied[index] = (byte)code[index];
        Span<byte> message = stackalloc byte[8];
        Span<byte> digest = stackalloc byte[20];
        Span<char> expected = stackalloc char[6];
        Span<byte> expectedBytes = stackalloc byte[6];
        for (var step = current + 1; step >= current - 1; step--)
        {
            if (step <= lastUsed) break;
            BinaryPrimitives.WriteInt64BigEndian(message, step);
            HMACSHA1.HashData(secret, message, digest);
            var offset = digest[^1] & 15;
            var number = (BinaryPrimitives.ReadInt32BigEndian(digest[offset..]) & int.MaxValue) % 1_000_000;
            number.TryFormat(expected, out _, "D6", CultureInfo.InvariantCulture);
            for (var index = 0; index < expected.Length; index++) expectedBytes[index] = (byte)expected[index];
            if (CryptographicOperations.FixedTimeEquals(supplied, expectedBytes)) return step;
        }
        return null;
    }
}
