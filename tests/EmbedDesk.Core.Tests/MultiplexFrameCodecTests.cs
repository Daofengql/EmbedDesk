using System.Buffers;
using EmbedDesk.Protocols;
using Xunit;

namespace EmbedDesk.Core.Tests;

public sealed class MultiplexFrameCodecTests
{
    [Fact]
    public void EncodeAndDecode_RoundTripsChannelAndPayload()
    {
        var original = new MultiplexFrame(7, MultiplexFrameFlags.Data, new byte[] { 1, 2, 3, 4 });
        var bytes = MultiplexFrameCodec.Encode(original);
        var buffer = new ArrayBufferWriter<byte>();
        bytes.AsSpan().CopyTo(buffer.GetSpan(bytes.Length));
        buffer.Advance(bytes.Length);

        var decoded = MultiplexFrameCodec.TryDecode(ref buffer, out var frame);

        Assert.True(decoded);
        Assert.NotNull(frame);
        Assert.Equal(original.ChannelId, frame!.ChannelId);
        Assert.Equal(original.Flags, frame.Flags);
        Assert.Equal(original.Payload.ToArray(), frame.Payload.ToArray());
        Assert.Equal(0, buffer.WrittenCount);
    }
}

