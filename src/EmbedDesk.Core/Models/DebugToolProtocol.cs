using System.Buffers.Binary;

namespace EmbedDesk.Core.Models;

public sealed record DebugToolPacket(byte Address, byte Command, byte[] Payload, bool ChecksumValid);

/// <summary>Independent implementation of compatible camera/PID wire formats.</summary>
public sealed class DebugToolPacketReader(bool camera, bool verifyChecksum = true)
{
    public const int MaximumPacketLength = 8 * 1024 * 1024;
    private byte[] _buffer = new byte[4096];
    private int _count;
    public long RejectedPackets { get; private set; }

    public void Feed(ReadOnlySpan<byte> data, Action<DebugToolPacket> receive)
    {
        while (!data.IsEmpty)
        {
            var take = Math.Min(data.Length, MaximumPacketLength - _count);
            if (_buffer.Length < _count + take) Array.Resize(ref _buffer, Math.Min(MaximumPacketLength, Math.Max(_count + take, _buffer.Length * 2)));
            data[..take].CopyTo(_buffer.AsSpan(_count)); _count += take; data = data[take..];
            var offset = 0;
            while (_count - offset >= 4)
            {
                var span = _buffer.AsSpan(offset, _count - offset);
                if (BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x59485A53) { offset++; continue; }
                if (span.Length < 10) break;
                var length = BinaryPrimitives.ReadUInt32LittleEndian(span[5..]);
                if (length < (camera ? 12 : 11) || length > (camera ? MaximumPacketLength : 256)) { offset++; RejectedPackets++; continue; }
                if (span.Length < length) break;
                var packet = span[..(int)length];
                var valid = camera
                    ? Crc16(packet[..^2]) == BinaryPrimitives.ReadUInt16LittleEndian(packet[^2..])
                    : Sum8(packet[..^1]) == packet[^1];
                if (!valid && verifyChecksum) { offset++; RejectedPackets++; continue; }
                // Copy before callbacks: callbacks may retain a frame after the input buffer is reused.
                var parsed = new DebugToolPacket(packet[4], packet[9], packet[10..^(camera ? 2 : 1)].ToArray(), valid);
                offset += (int)length;
                receive(parsed);
            }
            if (offset != 0) { _buffer.AsSpan(offset, _count - offset).CopyTo(_buffer); _count -= offset; }
        }
    }

    public static byte[] Encode(bool camera, byte address, byte command, ReadOnlySpan<byte> payload)
    {
        var length = checked(payload.Length + (camera ? 12 : 11));
        if (length > (camera ? MaximumPacketLength : 256)) throw new ArgumentOutOfRangeException(nameof(payload));
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 0x59485A53);
        packet[4] = address; BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(5), length); packet[9] = command;
        payload.CopyTo(packet.AsSpan(10));
        if (camera) BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(length - 2), Crc16(packet.AsSpan(0, length - 2)));
        else packet[^1] = Sum8(packet.AsSpan(0, length - 1));
        return packet;
    }

    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (var value in bytes) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xA001)); }
        return crc;
    }

    private static byte Sum8(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0; foreach (var value in bytes) sum = unchecked((byte)(sum + value)); return sum;
    }
}

/// <summary>JPEG marker parser that skips metadata segments, stuffed bytes and restart markers.</summary>
public sealed class JpegStreamReader : IDisposable
{
    private readonly MemoryStream _frame = new();
    private int _state, _remaining, _lengthHigh;
    private bool _scan;
    public long RejectedFrames { get; private set; }

    public void Feed(ReadOnlySpan<byte> bytes, Action<byte[]> receive)
    {
        foreach (var value in bytes)
        {
            if (_state < 2)
            {
                if (_state == 1 && value == 0xD8) { _frame.SetLength(0); _frame.WriteByte(0xFF); _frame.WriteByte(0xD8); _state = 2; }
                else _state = value == 0xFF ? 1 : 0;
                continue;
            }
            _frame.WriteByte(value);
            if (_frame.Length > DebugToolPacketReader.MaximumPacketLength) { _state = 0; _frame.SetLength(0); RejectedFrames++; continue; }
            switch (_state)
            {
                case 2: if (value == 0xFF) _state = 3; else Reject(); break;
                case 3:
                    if (value == 0xFF) break;
                    if (value == 0xD9) { receive(_frame.ToArray()); _state = 0; _frame.SetLength(0); }
                    else if (value == 0xD8) { Reject(); _frame.WriteByte(0xFF); _frame.WriteByte(0xD8); _state = 2; }
                    else if (value == 1 || value is >= 0xD0 and <= 0xD7) _state = 2;
                    else { _scan = value == 0xDA; _state = 4; }
                    break;
                case 4: _lengthHigh = value << 8; _state = 5; break;
                case 5:
                    _remaining = (_lengthHigh | value) - 2;
                    if (_remaining < 0) Reject(); else _state = _remaining == 0 ? (_scan ? 7 : 2) : 6;
                    break;
                case 6: if (--_remaining == 0) _state = _scan ? 7 : 2; break;
                case 7: if (value == 0xFF) _state = 8; break;
                case 8:
                    if (value == 0xFF) break;
                    if (value == 0 || value is >= 0xD0 and <= 0xD7) _state = 7;
                    else if (value == 0xD9) { receive(_frame.ToArray()); _state = 0; _frame.SetLength(0); }
                    else { _scan = value == 0xDA; _state = 4; }
                    break;
            }
        }
    }

    private void Reject() { RejectedFrames++; _state = 0; _frame.SetLength(0); }
    public void Dispose() => _frame.Dispose();
}
