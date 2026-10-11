using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace EmbeddedDebugger.Infrastructure.Networking;

internal static class DnsMessageCodec
{
    internal sealed record Query(byte[] Packet, string Name, ushort Type, ushort Class);
    internal sealed record Response(int Code, string Flags, IReadOnlyList<DnsResourceRecord> Records);

    internal static Query CreateRequest(DnsLookupOptions options)
    {
        var name = options.Query.Trim();
        var isAddress = IPAddress.TryParse(name, out var address);
        var type = options.RecordType == 0 ? (ushort)(isAddress ? 12 : 1) : options.RecordType;
        if (type == 12 && isAddress)
        {
            var bytes = address!.GetAddressBytes();
            name = address.AddressFamily == AddressFamily.InterNetwork
                ? string.Join(".", bytes.Reverse()) + ".in-addr.arpa"
                : string.Join(".", Convert.ToHexString(bytes).ToLowerInvariant().Reverse()) + ".ip6.arpa";
        }
        else if (name != ".")
        {
            if (name.EndsWith('.')) name = name[..^1];
            if (name.Length == 0 || name.Split('.').Any(label => label.Length == 0)) throw new FormatException("请输入有效域名或 IP 地址。");
            var idn = new IdnMapping();
            try { name = string.Join(".", name.Split('.').Select(label => idn.GetAscii(label))).ToLowerInvariant(); }
            catch (ArgumentException ex) { throw new FormatException("域名标签格式无效，或编码后超过 63 字节。", ex); }
        }
        using var stream = new MemoryStream();
        var header = new byte[12];
        RandomNumberGenerator.Fill(header.AsSpan(0, 2));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), options.RecursionDesired ? (ushort)0x0100 : (ushort)0);
        header[5] = 1;
        stream.Write(header);
        if (name != ".")
        {
            foreach (var label in name.Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length is < 1 or > 63 || label.Any(character => character <= ' ' || character >= 127 || character == '\\'))
                    throw new FormatException("DNS 标签应为 1–63 字节，且不能包含空白或转义字符。");
                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes);
            }
        }
        stream.WriteByte(0);
        if (stream.Length - 12 > 255) throw new FormatException("DNS 名称编码后不能超过 255 字节。");
        var question = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question, type);
        BinaryPrimitives.WriteUInt16BigEndian(question.AsSpan(2), options.QueryClass);
        stream.Write(question);
        return new(stream.ToArray(), name, type, options.QueryClass);
    }

    internal static bool MatchesQuestion(byte[] packet, Query query)
    {
        if (packet.Length < 12 || !packet.AsSpan(0, 2).SequenceEqual(query.Packet.AsSpan(0, 2))) return false;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        if ((flags & 0xF800) != 0x8000 || BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)) != 1) return false;
        try
        {
            var offset = 12;
            var name = ReadName(packet, ref offset);
            return string.Equals(name, query.Name, StringComparison.OrdinalIgnoreCase) &&
                Read16(packet, ref offset) == query.Type && Read16(packet, ref offset) == query.Class;
        }
        catch (InvalidDataException) { return false; }
    }

    internal static Response ParseResponse(byte[] packet)
    {
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        var counts = new[] { BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8)), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10)) };
        if (counts.Sum(count => (int)count) > 4096) throw BadPacket();
        var offset = 12;
        ReadName(packet, ref offset);
        Read16(packet, ref offset); Read16(packet, ref offset);
        var records = new List<DnsResourceRecord>();
        var sections = new[] { "应答", "权威", "附加" };
        var code = flags & 15;
        for (var section = 0; section < counts.Length; section++)
        {
            for (var i = 0; i < counts[section]; i++)
            {
                var name = ReadName(packet, ref offset);
                var type = Read16(packet, ref offset);
                var recordClass = Read16(packet, ref offset);
                var ttl = Read32(packet, ref offset);
                var length = Read16(packet, ref offset);
                Require(packet, offset, length);
                var end = offset + length;
                // OPT's class/TTL describe EDNS metadata, not a cache lifetime.
                if (type == 41)
                {
                    code |= (int)(ttl >> 24) << 4;
                    records.Add(new(sections[section], name, "OPT", "—", 0, $"UDP payload={recordClass}; EDNS={(ttl >> 16) & 255}; flags=0x{ttl & 65535:X4}"));
                    offset = end;
                    continue;
                }
                var value = ReadValue(packet, type, ref offset, end);
                if (offset != end) throw BadPacket();
                records.Add(new(sections[section], name, DnsLookupClient.TypeName(type), DnsLookupClient.ClassName(recordClass), ttl, value));
            }
        }
        if (offset != packet.Length) throw BadPacket();
        var bits = new List<string>();
        if ((flags & 0x0400) != 0) bits.Add("AA · 权威应答");
        if ((flags & 0x0200) != 0) bits.Add("TC · 响应截断");
        if ((flags & 0x0100) != 0) bits.Add("RD · 请求递归");
        if ((flags & 0x0080) != 0) bits.Add("RA · 支持递归");
        if ((flags & 0x0020) != 0) bits.Add("AD · 服务器报告已验证");
        if ((flags & 0x0010) != 0) bits.Add("CD · 禁用校验");
        return new(code, bits.Count == 0 ? "无" : string.Join(" / ", bits), records);
    }

    private static string ReadValue(byte[] packet, ushort type, ref int offset, int end)
    {
        var length = end - offset;
        switch (type)
        {
            case 1 when length == 4:
            case 28 when length == 16:
                var ip = new IPAddress(packet.AsSpan(offset, length)); offset = end; return ip.ToString();
            case 1 or 28: throw BadPacket();
            case 2 or 5 or 12 or 39: return ReadName(packet, ref offset);
            case 15: var priority = Read16(packet, ref offset); return $"{priority} {ReadName(packet, ref offset)}";
            case 6:
                var primary = ReadName(packet, ref offset); var mailbox = ReadName(packet, ref offset);
                var serial = Read32(packet, ref offset); var refresh = Read32(packet, ref offset); var retry = Read32(packet, ref offset);
                var expire = Read32(packet, ref offset); var minimum = Read32(packet, ref offset);
                return $"{primary} {mailbox} (serial={serial}, refresh={refresh}, retry={retry}, expire={expire}, minimum={minimum})";
            case 16 or 99:
                var strings = new List<string>();
                while (offset < end) strings.Add(ReadString(packet, ref offset, end));
                return string.Join(" ", strings);
            case 33:
                var preference = Read16(packet, ref offset); var weight = Read16(packet, ref offset); var port = Read16(packet, ref offset);
                return $"priority={preference} weight={weight} port={port} target={ReadName(packet, ref offset)}";
            case 35:
                var order = Read16(packet, ref offset); var pref = Read16(packet, ref offset);
                var flags = ReadString(packet, ref offset, end); var services = ReadString(packet, ref offset, end); var regexp = ReadString(packet, ref offset, end);
                return $"{order} {pref} {flags} {services} {regexp} {ReadName(packet, ref offset)}";
            case 257:
                if (length < 2) throw BadPacket();
                var caaFlags = packet[offset++]; var tagLength = packet[offset++];
                if (offset + tagLength > end) throw BadPacket();
                var tag = Encoding.ASCII.GetString(packet, offset, tagLength); offset += tagLength;
                var authority = Quote(Encoding.UTF8.GetString(packet, offset, end - offset)); offset = end;
                return $"{caaFlags} {tag} {authority}";
            default:
                var raw = Convert.ToHexString(packet.AsSpan(offset, length)); offset = end;
                return $"\\# {length} {raw}";
        }
    }

    private static string ReadString(byte[] packet, ref int offset, int end)
    {
        if (offset >= end) throw BadPacket();
        var length = packet[offset++];
        if (offset + length > end) throw BadPacket();
        var value = Encoding.UTF8.GetString(packet, offset, length); offset += length;
        return Quote(value);
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    private static string ReadName(byte[] packet, ref int offset)
    {
        var cursor = offset; var jumped = false; var expandedLength = 1;
        var visited = new HashSet<int>(); var labels = new List<string>();
        while (true)
        {
            Require(packet, cursor, 1);
            if (!visited.Add(cursor) || visited.Count > 128) throw BadPacket();
            var position = cursor;
            var length = packet[cursor++];
            if ((length & 0xC0) == 0xC0)
            {
                Require(packet, cursor, 1);
                var target = ((length & 0x3F) << 8) | packet[cursor++];
                if (target >= position) throw BadPacket();
                if (!jumped) offset = cursor;
                jumped = true; cursor = target;
                continue;
            }
            if (length > 63) throw BadPacket();
            if (length == 0) { if (!jumped) offset = cursor; break; }
            Require(packet, cursor, length);
            expandedLength += length + 1;
            if (expandedLength > 255) throw BadPacket();
            var label = new StringBuilder();
            foreach (var value in packet.AsSpan(cursor, length))
                if (value is > 32 and < 127 && value is not (46 or 92)) label.Append((char)value);
                else label.Append('\\').Append(value.ToString("D3", CultureInfo.InvariantCulture));
            labels.Add(label.ToString()); cursor += length;
        }
        return labels.Count == 0 ? "." : string.Join(".", labels);
    }

    private static ushort Read16(byte[] data, ref int offset)
    { Require(data, offset, 2); var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)); offset += 2; return value; }
    private static uint Read32(byte[] data, ref int offset)
    { Require(data, offset, 4); var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)); offset += 4; return value; }
    private static void Require(byte[] data, int offset, int length)
    { if (offset < 0 || length < 0 || offset > data.Length - length) throw BadPacket(); }
    private static InvalidDataException BadPacket() => new("DNS 响应报文损坏或长度无效。");
}
