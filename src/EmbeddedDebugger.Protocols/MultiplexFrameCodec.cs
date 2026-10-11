using System.Buffers;
using System.Buffers.Binary;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.Protocols;

[Flags]
public enum MultiplexFrameFlags : byte
{
    Data = 0,
    Open = 1,
    Close = 2,
    Heartbeat = 4,
    Error = 8,
    OpenAcknowledged = 16
}

public sealed record MultiplexFrame(
    ushort ChannelId,
    MultiplexFrameFlags Flags,
    ReadOnlyMemory<byte> Payload);

public static class MultiplexFrameCodec
{
    private const uint Magic = 0x45444247;
    private const byte Version = 1;
    private const int HeaderSize = 16;
    private const int MaxPayloadSize = 4 * 1024 * 1024;
    public const int DataChunkSize = 32 * 1024;

    // Exact reads avoid repeatedly copying a partially received large frame.
    public static async Task<MultiplexFrame?> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[HeaderSize];
        if (await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != Magic || header[4] != Version)
            throw new InvalidDataException("Invalid serial bridge protocol header.");
        var flags = (MultiplexFrameFlags)header[5];
        if (!Enum.IsDefined(flags)) throw new InvalidDataException("Unknown serial bridge frame type.");
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
        var limit = flags == MultiplexFrameFlags.Data ? MaxPayloadSize : 4096;
        if (length < 0 || length > limit) throw new InvalidDataException("Invalid serial bridge payload length.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4)) != ComputeChecksum(payload))
            throw new InvalidDataException("Serial bridge checksum mismatch.");
        return new(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2)), flags, payload);
    }

    public static byte[] Encode(MultiplexFrame frame)
    {
        if (frame.Payload.Length > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "Payload is too large.");
        }

        var buffer = new byte[HeaderSize + frame.Payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), Magic);
        buffer[4] = Version;
        buffer[5] = (byte)frame.Flags;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6, 2), frame.ChannelId);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(8, 4), frame.Payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12, 4), ComputeChecksum(frame.Payload.Span));
        frame.Payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static bool TryDecode(ref ArrayBufferWriter<byte> buffer, out MultiplexFrame? frame)
    {
        frame = null;
        if (buffer.WrittenCount < HeaderSize)
        {
            return false;
        }

        var span = buffer.WrittenSpan;
        if (BinaryPrimitives.ReadUInt32BigEndian(span[..4]) != Magic)
        {
            buffer = new ArrayBufferWriter<byte>();
            return false;
        }

        if (span[4] != Version)
        {
            throw new InvalidDataException($"Unsupported multiplex protocol version {span[4]}.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(span.Slice(8, 4));
        if (payloadLength < 0 || payloadLength > MaxPayloadSize)
        {
            throw new InvalidDataException("Invalid multiplex payload length.");
        }

        if (buffer.WrittenCount < HeaderSize + payloadLength)
        {
            return false;
        }

        var checksum = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(12, 4));
        var payload = span.Slice(HeaderSize, payloadLength).ToArray();
        if (checksum != ComputeChecksum(payload))
        {
            throw new InvalidDataException("Multiplex frame checksum mismatch.");
        }

        frame = new MultiplexFrame(
            BinaryPrimitives.ReadUInt16BigEndian(span.Slice(6, 2)),
            (MultiplexFrameFlags)span[5],
            payload);

        var remaining = span[(HeaderSize + payloadLength)..].ToArray();
        buffer = new ArrayBufferWriter<byte>(Math.Max(remaining.Length, HeaderSize));
        var destination = buffer.GetSpan(remaining.Length);
        remaining.AsSpan().CopyTo(destination);
        buffer.Advance(remaining.Length);
        return true;
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var value in data)
            {
                hash ^= value;
                hash *= 16777619;
            }
            return hash;
        }
    }
}


