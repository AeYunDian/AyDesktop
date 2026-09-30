using System.Text;

namespace AyDesktop.Services;

/// <summary>
/// RFC 4648 Base32 解码器。
/// 注意：这是编码，不是加密，仅用于避免源码/二进制里出现明文敏感串。
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] Decode(string input)
    {
        if (string.IsNullOrEmpty(input))
            return Array.Empty<byte>();

        // 忽略 padding、空白、换行；大小写不敏感
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c == '=') continue;
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(char.ToUpperInvariant(c));
        }

        string s = sb.ToString();
        int byteCount = s.Length * 5 / 8;
        var result = new byte[byteCount];

        int buffer = 0, bitsLeft = 0, index = 0;
        foreach (var c in s)
        {
            int val = Alphabet.IndexOf(c);
            if (val < 0)
                throw new FormatException($"Invalid Base32 character: '{c}'");

            buffer = (buffer << 5) | val;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                result[index++] = (byte)((buffer >> (bitsLeft - 8)) & 0xFF);
                bitsLeft -= 8;
            }
        }

        return result;
    }

    public static string DecodeToString(string input) =>
        Encoding.UTF8.GetString(Decode(input));

    public static string Encode(byte[] data)
    {
        if (data == null || data.Length == 0) return "";

        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bitsLeft = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0)
            sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }

    public static string EncodeString(string input) =>
        Encode(Encoding.UTF8.GetBytes(input ?? string.Empty));
}