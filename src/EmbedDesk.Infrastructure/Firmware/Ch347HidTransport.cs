using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmbedDesk.Infrastructure.Firmware;

public interface ISpiFlashTransport : IAsyncDisposable
{
    Task<byte[]> ExchangeAsync(byte[] command, int readLength, CancellationToken cancellationToken = default);
}

public sealed record Ch347Device(string Path, string DisplayName);

/// <summary>CH347T standard HID SPI interface. Uses only Windows HID/SetupAPI, without vendor DLLs.</summary>
public sealed class Ch347HidTransport : ISpiFlashTransport
{
    private readonly FileStream _stream;
    private readonly int _inputLength;
    private readonly int _outputLength;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _chipSelect;
    private bool _disposed;

    private Ch347HidTransport(FileStream stream, int inputLength, int outputLength, int chipSelect)
    { _stream = stream; _inputLength = inputLength; _outputLength = outputLength; _chipSelect = chipSelect; }

    public static IReadOnlyList<Ch347Device> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CH347 HID requires Windows.");
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        var devices = new List<Ch347Device>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new Win32Exception();
                }
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero)) throw new Win32Exception();
                    var path = Marshal.PtrToStringUni(detail + 4) ?? string.Empty;
                    if (path.Contains("vid_1a86&pid_55dc", StringComparison.OrdinalIgnoreCase) && path.Contains("mi_01", StringComparison.OrdinalIgnoreCase))
                        devices.Add(new(path, $"CH347T HID SPI · {devices.Count + 1}"));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    public static async Task<Ch347HidTransport> OpenAsync(Ch347Device device, int clockDivider = 5, int chipSelect = 0, CancellationToken cancellationToken = default)
    {
        if (clockDivider is < 0 or > 7 || chipSelect is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(clockDivider));
        if (!device.Path.Contains("vid_1a86&pid_55dc", StringComparison.OrdinalIgnoreCase) || !device.Path.Contains("mi_01", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("仅支持 CH347T 的 HID SPI 接口。", nameof(device));
        // Exclusive ownership prevents two programmer windows from interleaving CS transactions.
        var handle = Native.CreateFile(device.Path, 0xc0000000, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
        FileStream? stream = null;
        try
        {
            if (!Native.HidD_GetPreparsedData(handle, out var preparsed)) throw new Win32Exception();
            int inputLength, outputLength;
            var caps = Marshal.AllocHGlobal(64);
            try
            {
                if (Native.HidP_GetCaps(preparsed, caps) != 0x110000) throw new IOException("无法读取 HID 报告描述符。");
                inputLength = (ushort)Marshal.ReadInt16(caps, 4);
                outputLength = (ushort)Marshal.ReadInt16(caps, 6);
            }
            finally { Marshal.FreeHGlobal(caps); Native.HidD_FreePreparsedData(preparsed); }
            if (inputLength < 513 || outputLength < 513 || inputLength > 4096 || outputLength > 4096)
                throw new IOException($"CH347 HID 报告长度不匹配：输入 {inputLength} / 输出 {outputLength}。");
            Native.HidD_FlushQueue(handle);
            // HID writes are one report each; FileStream buffering must never coalesce reports.
            stream = new FileStream(handle, FileAccess.ReadWrite, 1, true);
            var transport = new Ch347HidTransport(stream, inputLength, outputLength, chipSelect);
            var config = new byte[26];
            config[2] = 4; config[3] = 1; // master
            config[11] = 2; // software CS
            config[12] = (byte)(clockDivider << 3);
            config[16] = 7; config[20] = 0xff;
            await transport.WritePacketAsync(0xc0, config, cancellationToken).ConfigureAwait(false);
            var ack = await transport.ReadPacketAsync(0xc0, cancellationToken).ConfigureAwait(false);
            if (ack.Length < 1 || ack[0] != 0) throw new IOException("CH347 SPI 初始化未确认。");
            await transport.SetSelectAsync(false, cancellationToken).ConfigureAwait(false);
            return transport;
        }
        catch { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); else handle.Dispose(); throw; }
    }

    public async Task<byte[]> ExchangeAsync(byte[] command, int readLength, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0 || readLength < 0 || command.Length + (long)readLength > 65536) throw new ArgumentOutOfRangeException(nameof(readLength));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await SetSelectAsync(true, cancellationToken).ConfigureAwait(false);
            try
            {
                var tx = new byte[command.Length + readLength];
                Array.Fill(tx, (byte)0xff); command.CopyTo(tx, 0);
                var rx = new byte[tx.Length];
                for (var offset = 0; offset < tx.Length; offset += 507)
                {
                    var count = Math.Min(507, tx.Length - offset);
                    await WritePacketAsync(0xc2, tx.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                    var reply = await ReadPacketAsync(0xc2, cancellationToken).ConfigureAwait(false);
                    if (reply.Length != count) throw new IOException($"SPI 接收长度错误：{reply.Length} / {count}。");
                    reply.CopyTo(rx, offset);
                }
                return rx[command.Length..];
            }
            finally { await SetSelectAsync(false, CancellationToken.None).ConfigureAwait(false); }
        }
        finally { _gate.Release(); }
    }

    private Task SetSelectAsync(bool asserted, CancellationToken token)
    {
        var payload = new byte[10]; payload[_chipSelect * 5] = asserted ? (byte)0x80 : (byte)0xc0;
        return WritePacketAsync(0xc1, payload, token);
    }

    private async Task WritePacketAsync(byte opcode, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var packet = new byte[_outputLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), checked((ushort)(payload.Length + 3)));
        packet[3] = opcode;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), checked((ushort)payload.Length));
        payload.CopyTo(packet.AsMemory(6));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(3000);
        try { await _stream.WriteAsync(packet, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("CH347 HID 写入超时。"); }
    }

    private async Task<byte[]> ReadPacketAsync(byte expectedOpcode, CancellationToken token)
    {
        var packet = new byte[_inputLength];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(3000);
        int count;
        try { count = await _stream.ReadAsync(packet, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("CH347 HID 响应超时。"); }
        if (count < 6 || packet[0] != 0 || packet[3] != expectedOpcode) throw new IOException("CH347 HID 返回了无效响应。");
        var frameLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1));
        var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4));
        if (frameLength != dataLength + 3 || dataLength > count - 6) throw new IOException("CH347 HID 响应被截断。");
        return packet.AsSpan(6, dataLength).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; await _stream.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { internal int Size; internal Guid Guid; internal int Flags; internal IntPtr Reserved; }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FlushQueue(SafeFileHandle handle);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, IntPtr caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
