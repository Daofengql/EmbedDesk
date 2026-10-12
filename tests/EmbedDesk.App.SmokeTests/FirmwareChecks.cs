using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Windows;
using EmbedDesk.App;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Infrastructure.Firmware;

namespace EmbedDesk.App.SmokeTests;

internal static class FirmwareChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "EmbedDesk-Firmware", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binary = Path.Combine(root, "image.bin");
            await File.WriteAllBytesAsync(binary, [1, 2, 3, 4]);
            var summary = await FirmwareImageAnalyzer.AnalyzeAsync(binary);
            Require(summary.Format == FirmwareImageFormat.Binary && summary.DataBytes == 4 && summary.Sha256 == Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])), "Binary image analysis");
            var loaded = await FirmwareImageLoader.LoadAsync(binary, 0x08000000);
            Require(loaded.Count == 1 && loaded[0].Address == 0x08000000 && loaded[0].Data.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Binary load address");

            var hex = Path.Combine(root, "image.hex");
            await File.WriteAllTextAsync(hex, string.Join(Environment.NewLine, HexRecord(0x0000, 0x04, [0x08, 0x00]), HexRecord(0x0000, 0x00, [1, 2]), HexRecord(0x0002, 0x00, [3, 4]), HexRecord(0, 0x01, [])));
            var hexSummary = await FirmwareImageAnalyzer.AnalyzeAsync(hex);
            var hexSegments = await FirmwareImageLoader.LoadAsync(hex);
            Require(hexSummary.Format == FirmwareImageFormat.IntelHex && hexSummary.DataBytes == 4 && hexSegments.Count == 1 && hexSegments[0].Address == 0x08000000 && hexSegments[0].Data.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Intel HEX validation and merge");

            var srec = Path.Combine(root, "image.s19");
            await File.WriteAllTextAsync(srec, string.Join(Environment.NewLine, SRecord('1', 0x0800, [1, 2, 3]), SRecord('9', 0, [])));
            var srecSegments = await FirmwareImageLoader.LoadAsync(srec);
            Require(srecSegments.Count == 1 && srecSegments[0].Address == 0x0800 && srecSegments[0].Data.SequenceEqual(new byte[] { 1, 2, 3 }), "Motorola S-Record validation");

            await using var input = new MemoryStream([1, 2, 3, 4, 5]);
            await using var output = new MemoryStream(); output.SetLength(512);
            var expectedHash = await MediaImageOperations.WriteAndVerifyAsync(input, output, 512, 512);
            Require(expectedHash == Convert.ToHexString(SHA256.HashData([1, 2, 3, 4, 5])) && output.ToArray()[5..512].All(value => value == 0), "Image write padding and readback verification");
            var probeRoot = Path.Combine(root, "probe"); Directory.CreateDirectory(probeRoot);
            await Stm32ProbeFlashService.CopyFirmwareAsync(probeRoot, binary);
            Require(File.ReadAllBytes(Path.Combine(probeRoot, "image.bin")).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "ST-LINK / DAPLink drag-drop copy uses the original firmware bytes");
            var firmwareWindow = new FirmwareToolWindow(new FakeSerialService()); firmwareWindow.Show(); await Task.Delay(40); firmwareWindow.Close();
            var imageWindow = new ImageWriterWindow(); imageWindow.Show(); await Task.Delay(40); imageWindow.Close();
            Require(true, "Embedded firmware and image tool windows initialize");
            Console.WriteLine("PASS firmware image analyzers, STM32 image preparation and media verification");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static string HexRecord(int address, int type, byte[] data)
    {
        var bytes = new byte[5 + data.Length]; bytes[0] = (byte)data.Length; bytes[1] = (byte)(address >> 8); bytes[2] = (byte)address; bytes[3] = (byte)type; data.CopyTo(bytes, 4); bytes[^1] = (byte)(-bytes[..^1].Sum(value => value));
        return ":" + Convert.ToHexString(bytes);
    }
    private static string SRecord(char type, int address, byte[] data)
    {
        var bytes = new byte[1 + 2 + data.Length + 1]; bytes[0] = (byte)(bytes.Length - 1); bytes[1] = (byte)(address >> 8); bytes[2] = (byte)address; data.CopyTo(bytes, 3); bytes[^1] = (byte)~bytes[..^1].Sum(value => value); return $"S{type}" + Convert.ToHexString(bytes);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL " + message); Console.WriteLine("PASS " + message); }
}
