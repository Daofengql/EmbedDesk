using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EmbedDesk.Core.Models;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace EmbedDesk.Infrastructure.Serial;

public sealed record VirtualSerialPair(string PublicPort, string BridgePort, string PhysicalBridgePort, string Problem)
{
    public string DisplayName => $"{PublicPort}  ↔  {BridgePort}";
}

public sealed record VirtualSerialPeerState(bool IsOpen, int BaudRate, int DataBits, Parity Parity, StopBits StopBits, bool Dtr, bool Rts)
{
    public SerialPortSettings ToSettings() => new()
    {
        BaudRate = BaudRate, DataBits = DataBits, Parity = Parity, StopBits = StopBits,
        DtrEnable = Dtr, RtsEnable = Rts, Handshake = Handshake.None
    };
}

/// <summary>A driver-backed pair endpoint. Only the bridge owns this side of the pair.</summary>
public interface IVirtualSerialEndpoint : IAsyncDisposable
{
    VirtualSerialPeerState GetPeerState();
    int Read(byte[] buffer);
    void Write(byte[] buffer);
    void SetReady(bool ready);
    void Discard();
}

/// <summary>Uses an existing com0com driver; never installs, reconfigures or removes devices.</summary>
public sealed class Com0ComEndpoint : IVirtualSerialEndpoint
{
    private readonly SafeFileHandle _handle;
    private const string ParametersPath = @"SYSTEM\CurrentControlSet\Services\com0com\Parameters";
    // Extended IOCTL layout/constants adapted from com0com cncext.h, GPL-2.0-or-later.
    // Copyright (c) 2008 Vyacheslav Frolov. Source revision and license: docs/third-party/com0com.md.
    private const uint InsertIoctl = 0x1b007c;
    private const uint GetSnapshot = 0x01000302; // GET | MST | paired baud rate | paired line control

    private Com0ComEndpoint(SafeFileHandle handle) => _handle = handle;

    public static IReadOnlyList<VirtualSerialPair> ScanPairs()
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var map = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (map is null) return [];
        var ports = new Dictionary<(string Pair, string Side), string>();
        foreach (var name in map.GetValueNames())
        {
            var match = Regex.Match(name, @"^\\Device\\com0com([12])(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && map.GetValue(name) is string port)
                ports[(match.Groups[2].Value, match.Groups[1].Value)] = port;
        }
        var result = new List<VirtualSerialPair>();
        foreach (var entry in ports)
        {
            if (!IsComName(entry.Value)) continue;
            var otherSide = entry.Key.Side == "1" ? "2" : "1";
            if (!ports.TryGetValue((entry.Key.Pair, otherSide), out var bridge)) continue;
            var physical = (otherSide == "1" ? "CNCA" : "CNCB") + entry.Key.Pair;
            using var config = Registry.LocalMachine.OpenSubKey(ParametersPath + "\\" + physical);
            var problem = ReadPin(config, "dcd", 2) != 0x80 || ReadPin(config, "cts", 1) != 1 || ReadPin(config, "dsr", 2) != 2
                ? $"请在 com0com 中为 {physical} 设置 dcd=ropen,cts=rrts,dsr=rdtr。" : string.Empty;
            result.Add(new(entry.Value, bridge, physical, problem));
        }
        return result.OrderBy(p => p.PublicPort, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static uint ReadPin(RegistryKey? key, string name, uint fallback) => key?.GetValue(name) switch
    { null => fallback, int value => unchecked((uint)value), _ => uint.MaxValue };

    public static bool IsComName(string? name) => name is { Length: > 3 and <= 12 }
        && name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && name[3..].All(char.IsAsciiDigit)
        && int.TryParse(name[3..], out var number) && number > 0;

    public static Com0ComEndpoint Open(VirtualSerialPair pair)
    {
        var current = ScanPairs().FirstOrDefault(p => string.Equals(p.PublicPort, pair.PublicPort, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.BridgePort, pair.BridgePort, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("未发现此 com0com 串口对。请先安装可正常加载的驱动并配置串口对。");
        if (current.Problem.Length > 0) throw new IOException(current.Problem);
        var handle = Native.CreateFile(@"\\.\" + current.BridgePort, 0xc0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "无法打开虚拟串口桥接端；请检查是否已被其他程序占用。"); }
        var endpoint = new Com0ComEndpoint(handle);
        try
        {
            var caps = endpoint.Query(0xffffffff);
            if (caps.Length != 8 || !caps.AsSpan(0, 4).SequenceEqual("c0c\0"u8)
                || (BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(4)) & GetSnapshot) != GetSnapshot)
                throw new IOException("驱动不支持所需的 com0com 扩展接口。");
            var dcb = new Native.Dcb { Length = (uint)Marshal.SizeOf<Native.Dcb>() };
            Check(Native.GetCommState(handle, ref dcb));
            dcb.Flags = 1; // binary, no automatic flow control; readiness sets DTR/RTS separately
            dcb.BaudRate = 115200; dcb.ByteSize = 8; dcb.Parity = 0; dcb.StopBits = 0;
            Check(Native.SetCommState(handle, ref dcb));
            var timeouts = new Native.Timeouts { ReadInterval = uint.MaxValue, ReadConstant = 20, WriteConstant = 500 };
            Check(Native.SetCommTimeouts(handle, ref timeouts));
            endpoint.GetPeerState(); endpoint.Discard();
            return endpoint;
        }
        catch { handle.Dispose(); throw; }
    }

    private byte[] Query(uint flags)
    {
        // Escape=0 disables in-band insertions. Snapshot metadata is returned only by the IOCTL,
        // keeping every value (including NUL and 0xff) in the data stream unchanged.
        var input = new byte[9]; "c0c\0"u8.CopyTo(input.AsSpan(1));
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(5), flags);
        var output = new byte[32];
        Check(Native.DeviceIoControl(_handle, InsertIoctl, input, input.Length, output, output.Length, out var size, IntPtr.Zero));
        return output.AsSpan(0, size).ToArray();
    }

    public VirtualSerialPeerState GetPeerState() => DecodeSnapshot(Query(GetSnapshot));

    public static VirtualSerialPeerState DecodeSnapshot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 14 || bytes[0] != 0 || bytes[1] != 3 || bytes[3] != 0 || bytes[4] != 16 || bytes[9] != 0 || bytes[10] != 17)
            throw new InvalidDataException("com0com 参数快照格式无效。");
        var baud = BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
        if (baud is < 300 or > 4_000_000 || bytes[11] is < 5 or > 8 || bytes[12] > 4 || bytes[13] > 2)
            throw new InvalidDataException("外部软件设置了不支持的串口参数。");
        var stop = bytes[13] switch { 0 => StopBits.One, 1 => StopBits.OnePointFive, _ => StopBits.Two };
        return new((bytes[2] & 0x80) != 0, (int)baud, bytes[11], (Parity)bytes[12], stop, (bytes[2] & 0x20) != 0, (bytes[2] & 0x10) != 0);
    }

    public int Read(byte[] buffer) { Check(Native.ReadFile(_handle, buffer, buffer.Length, out var count, IntPtr.Zero)); return count; }
    public void Write(byte[] buffer)
    {
        Check(Native.WriteFile(_handle, buffer, buffer.Length, out var count, IntPtr.Zero));
        if (count != buffer.Length) throw new IOException("虚拟串口写入超时或未完整发送。");
    }
    public void Discard() => Check(Native.PurgeComm(_handle, 0x0c));
    public void SetReady(bool ready)
    {
        Check(Native.EscapeCommFunction(_handle, ready ? 5u : 6u)); // SETDTR / CLRDTR
        Check(Native.EscapeCommFunction(_handle, ready ? 3u : 4u)); // SETRTS / CLRRTS
    }
    public ValueTask DisposeAsync() { _handle.Dispose(); return ValueTask.CompletedTask; }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Dcb
        {
            internal uint Length, BaudRate, Flags;
            internal ushort Reserved, XonLimit, XoffLimit;
            internal byte ByteSize, Parity, StopBits, XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
            internal ushort Reserved1;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Timeouts { internal uint ReadInterval, ReadMultiplier, ReadConstant, WriteMultiplier, WriteConstant; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputSize, [Out] byte[] output, int outputSize, out int size, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCommState(SafeFileHandle file, ref Dcb state);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCommState(SafeFileHandle file, ref Dcb state);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCommTimeouts(SafeFileHandle file, ref Timeouts timeouts);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReadFile(SafeFileHandle file, [Out] byte[] data, int size, out int read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WriteFile(SafeFileHandle file, byte[] data, int size, out int written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PurgeComm(SafeFileHandle file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EscapeCommFunction(SafeFileHandle file, uint function);
    }
}
