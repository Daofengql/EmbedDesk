using System.Buffers.Binary;
using System.Text;
using EmbedDesk.Core.Models;
using Xunit;

namespace EmbedDesk.Core.Tests;

public sealed class DebugToolProtocolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FragmentedAndCoalescedPacketsAreIndependent(bool camera)
    {
        var first = DebugToolPacketReader.Encode(camera, 1, 2, [0, 255, 0x53, 0x5A, 0x48, 0x59]);
        var second = DebugToolPacketReader.Encode(camera, 0, 1, [1, 2, 3]);
        var reader = new DebugToolPacketReader(camera); var received = new List<DebugToolPacket>();
        foreach (var value in first) reader.Feed([value], received.Add);
        reader.Feed(second.Concat(first).ToArray(), received.Add);
        Assert.Equal(3, received.Count); Assert.Equal(first[10..^(camera ? 2 : 1)], received[0].Payload);
        Assert.All(received, packet => Assert.True(packet.ChecksumValid));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidLengthAndChecksumRecoverAtNextHeader(bool camera)
    {
        var valid = DebugToolPacketReader.Encode(camera, 1, 2, [7]);
        var invalidLength = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(invalidLength.AsSpan(5), uint.MaxValue);
        var invalidChecksum = (byte[])valid.Clone(); invalidChecksum[^1] ^= 0x55;
        var reader = new DebugToolPacketReader(camera); var received = new List<DebugToolPacket>();
        reader.Feed(new byte[] { 1, 2, 0x53 }.Concat(invalidLength).Concat(invalidChecksum).Concat(valid).ToArray(), received.Add);
        Assert.Single(received); Assert.Equal(new byte[] { 7 }, received[0].Payload); Assert.Equal(2, reader.RejectedPackets);
    }

    [Fact]
    public void CameraCanSkipVerificationButKeepsTwoCrcBytes()
    {
        var bytes = DebugToolPacketReader.Encode(true, 0, 2, [10, 20]); bytes[^1] ^= 0x44;
        var packets = new List<DebugToolPacket>(); new DebugToolPacketReader(true, false).Feed(bytes, packets.Add);
        Assert.Single(packets); Assert.False(packets[0].ChecksumValid); Assert.Equal(new byte[] { 10, 20 }, packets[0].Payload);
        Assert.Equal((ushort)0x4B37, DebugToolPacketReader.Crc16(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void PublishedPidExamplesHaveCorrectLengthsAndChecksum()
    {
        var sample = Convert.FromHexString("535A48590117000000030000803F0000004000004040E8");
        var packets = new List<DebugToolPacket>(); new DebugToolPacketReader(false).Feed(sample, packets.Add);
        Assert.Single(packets); Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(packets[0].Payload));
        Assert.Equal(Convert.FromHexString("535A4859010B000000045E"), DebugToolPacketReader.Encode(false, 1, 4, []));
    }

    [Fact]
    public void JpegStreamSkipsThumbnailMarkersAndEntropyEscapes()
    {
        // APP metadata contains an embedded SOI/EOI; neither terminates the outer image.
        var image = Convert.FromHexString("FFD8FFE00006FFD8FFD9FFDA000200FF00FFD000FFD9");
        using var reader = new JpegStreamReader(); var frames = new List<byte[]>();
        foreach (var value in image.Concat(image)) reader.Feed([value], frames.Add);
        Assert.Equal(2, frames.Count); Assert.All(frames, frame => Assert.Equal(image, frame));
    }
}
