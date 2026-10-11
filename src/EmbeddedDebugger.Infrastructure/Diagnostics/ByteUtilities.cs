using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EmbeddedDebugger.Infrastructure.Diagnostics;

public enum ByteFormat { Hex, Utf8, Ascii, Utf16Le, Base64 }
public sealed record ChecksumValue(string Algorithm, string Value, string LittleEndian, string Parameters);

public static class ByteUtilities
{
    public static byte[] Decode(string input, ByteFormat format) => format switch
    {
        ByteFormat.Hex => DecodeHex(input),
        ByteFormat.Base64 => Convert.FromBase64String(input),
        _ => GetEncoding(format).GetBytes(input)
    };

    public static string Encode(byte[] bytes, ByteFormat format) => format switch
    {
        ByteFormat.Hex => string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))),
        ByteFormat.Base64 => Convert.ToBase64String(bytes),
        _ => GetEncoding(format).GetString(bytes)
    };

    private static Encoding GetEncoding(ByteFormat format) => format switch
    {
        ByteFormat.Utf8 => new UTF8Encoding(false, true),
        ByteFormat.Utf16Le => new UnicodeEncoding(false, false, true),
        ByteFormat.Ascii => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static byte[] DecodeHex(string input)
    {
        var hex = new StringBuilder(input.Length);
        foreach (var part in input.Split([' ', '\t', '\r', '\n', ',', ':', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (digits.Length == 0 || digits.Length % 2 != 0 || digits.Any(c => !Uri.IsHexDigit(c)))
                throw new FormatException("HEX 需要完整的两位字节，例如 01 03 FF 或 0x01, 0x03, 0xFF。");
            hex.Append(digits);
        }
        return Convert.FromHexString(hex.ToString());
    }

    public static IReadOnlyList<ChecksumValue> Checksums(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ushort modbus = 0xFFFF, ccitt = 0xFFFF;
        uint crc32 = 0xFFFFFFFF;
        byte sum = 0, xor = 0;
        foreach (var value in data)
        {
            sum = unchecked((byte)(sum + value)); xor ^= value;
            modbus ^= value; ccitt ^= (ushort)(value << 8); crc32 ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                modbus = (ushort)((modbus >> 1) ^ ((modbus & 1) != 0 ? 0xA001 : 0));
                ccitt = (ushort)((ccitt << 1) ^ ((ccitt & 0x8000) != 0 ? 0x1021 : 0));
                crc32 = (crc32 >> 1) ^ ((crc32 & 1) != 0 ? 0xEDB88320u : 0u);
            }
        }
        crc32 ^= 0xFFFFFFFF;
        return [
            Checksum("CRC-16 / MODBUS", modbus, 2, "poly=8005  init=FFFF  refin/out=true  xorout=0000"),
            Checksum("CRC-16 / CCITT-FALSE", ccitt, 2, "poly=1021  init=FFFF  refin/out=false  xorout=0000"),
            Checksum("CRC-32 / ISO-HDLC", crc32, 4, "poly=04C11DB7  init=FFFFFFFF  refin/out=true  xorout=FFFFFFFF"),
            Checksum("SUM-8", sum, 1, "所有字节之和，取低 8 位"),
            Checksum("XOR-8", xor, 1, "所有字节逐个异或"),
            new("SHA-256", Convert.ToHexString(SHA256.HashData(data)), "—", "文件 / 数据指纹"),
            new("MD5", Convert.ToHexString(MD5.HashData(data)), "—", "仅用于兼容性校验，不用于安全验证")
        ];
    }

    private static ChecksumValue Checksum(string name, uint value, int bytes, string parameters)
    {
        var hex = value.ToString("X" + bytes * 2, CultureInfo.InvariantCulture);
        var little = string.Join(' ', Enumerable.Range(0, bytes).Select(i => ((value >> (8 * i)) & 255).ToString("X2", CultureInfo.InvariantCulture)));
        return new(name, hex, little, parameters);
    }
}
