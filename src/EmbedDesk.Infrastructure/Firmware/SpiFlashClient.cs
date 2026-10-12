using System.Diagnostics;

namespace EmbedDesk.Infrastructure.Firmware;

public enum SpiFlashKind { Nor, Nand }
public sealed record SpiFlashInfo(string Name, string JedecId, SpiFlashKind Kind, int Capacity, int PageSize, int EraseSize, int SpareSize)
{
    public string Description => $"{Name} · {Kind.ToString().ToUpperInvariant()} · {Capacity / 1048576} MiB · JEDEC {JedecId}";
}
public sealed record SpiFlashProgress(string Stage, long Completed, long Total, string Detail = "")
{
    public double Percent => Total == 0 ? 0 : Math.Clamp(Completed * 100.0 / Total, 0, 100);
}

/// <summary>Conservative single-I/O Winbond NOR / W25N01GV NAND programmer; physical addresses never shift.</summary>
public sealed class SpiFlashClient : IAsyncDisposable
{
    private readonly ISpiFlashTransport _transport;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private byte _originalNandConfig;
    private byte _nandConfig;
    private bool _disposed;
    public SpiFlashInfo Info { get; }
    public int CorrectedPages { get; private set; }
    public static IReadOnlyList<SpiFlashInfo> SupportedChips { get; } = Array.AsReadOnly(new[]
    {
        new SpiFlashInfo("W25Q16JV/BV", "EF4015", SpiFlashKind.Nor, 2 * 1048576, 256, 4096, 0),
        new SpiFlashInfo("W25Q32JV/BV", "EF4016", SpiFlashKind.Nor, 4 * 1048576, 256, 4096, 0),
        new SpiFlashInfo("W25Q64JV/BV", "EF4017", SpiFlashKind.Nor, 8 * 1048576, 256, 4096, 0),
        new SpiFlashInfo("W25Q128JV/BV", "EF4018", SpiFlashKind.Nor, 16 * 1048576, 256, 4096, 0),
        new SpiFlashInfo("W25N01GV", "EFAA21", SpiFlashKind.Nand, 128 * 1048576, 2048, 131072, 64),
    });

    private SpiFlashClient(ISpiFlashTransport transport, SpiFlashInfo info) { _transport = transport; Info = info; }

    public static async Task<SpiFlashClient> IdentifyAsync(ISpiFlashTransport transport, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var id = Convert.ToHexString(await transport.ExchangeAsync([0x9f], 3, token).ConfigureAwait(false));
        var info = SupportedChips.FirstOrDefault(chip => chip.Kind == SpiFlashKind.Nor && chip.JedecId == id);
        if (info is null)
        {
            var nandId = Convert.ToHexString(await transport.ExchangeAsync([0x9f, 0], 3, token).ConfigureAwait(false));
            info = SupportedChips.FirstOrDefault(chip => chip.Kind == SpiFlashKind.Nand && chip.JedecId == nandId);
            if (info is null) throw new NotSupportedException($"未支持的 JEDEC ID：NOR {id} / NAND {nandId}。已禁止擦写，请核对供电、接线与芯片型号。");
        }
        var client = new SpiFlashClient(transport, info);
        if (info.Kind == SpiFlashKind.Nand)
        {
            await client.WaitReadyAsync(token).ConfigureAwait(false);
            client._originalNandConfig = await client.GetFeatureAsync(0xb0, token).ConfigureAwait(false);
            if ((client._originalNandConfig & 0xc0) != 0)
                throw new IOException("NAND 正处于 OTP 模式或 OTP 锁定状态；为避免访问 OTP 区域，未启用操作。请退出 OTP 模式后重试。");
            client._nandConfig = (byte)(client._originalNandConfig | 0x18); // ECC enabled, buffer read mode
            try { await client.SetFeatureAsync(0xb0, client._nandConfig, token).ConfigureAwait(false); }
            catch { try { await client.SetFeatureAsync(0xb0, client._originalNandConfig, CancellationToken.None).ConfigureAwait(false); } catch { } throw; }
        }
        return client;
    }

    public async Task<IReadOnlyList<int>> ScanBadBlocksAsync(IProgress<SpiFlashProgress>? progress = null, CancellationToken token = default)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckDisposed();
            if (Info.Kind != SpiFlashKind.Nand) return Array.Empty<int>();
            return await FindBadBlocksAsync(0, Info.Capacity, progress, token).ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }

    public async Task BackupAsync(Stream destination, long offset, long length, bool includeSpare = false, IProgress<SpiFlashProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("备份目标不可写。", nameof(destination));
        ValidateRange(offset, length, Info.Kind == SpiFlashKind.Nand ? Info.PageSize : 1);
        if (includeSpare && Info.Kind != SpiFlashKind.Nand) throw new ArgumentException("NOR 没有 OOB 区域。", nameof(includeSpare));
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckDisposed(); CorrectedPages = 0;
            if (includeSpare) await SetFeatureAsync(0xb0, (byte)(_nandConfig & ~0x10), token).ConfigureAwait(false);
            try
            {
                for (long done = 0; done < length;)
                {
                    token.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(Info.Kind == SpiFlashKind.Nand ? Info.PageSize : 4096, length - done);
                    var data = Info.Kind == SpiFlashKind.Nand
                        ? await ReadNandPageAsync((int)((offset + done) / Info.PageSize), includeSpare, !includeSpare, token).ConfigureAwait(false)
                        : await ReadNorAsync(offset + done, count, token).ConfigureAwait(false);
                    await destination.WriteAsync(data, token).ConfigureAwait(false);
                    done += count;
                    progress?.Report(new("读取备份", done, length, includeSpare ? "物理页 + OOB，ECC 关闭" : $"已纠正页：{CorrectedPages}"));
                }
                await destination.FlushAsync(token).ConfigureAwait(false);
            }
            finally { if (includeSpare) await SetFeatureAsync(0xb0, _nandConfig, CancellationToken.None).ConfigureAwait(false); }
        }
        finally { _operation.Release(); }
    }

    public async Task ProgramAndVerifyAsync(Stream image, long offset, IProgress<SpiFlashProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanRead || !image.CanSeek) throw new ArgumentException("固件必须是可定位的 BIN 数据流。", nameof(image));
        var length = image.Length - image.Position;
        var erasedLength = checked((length + Info.EraseSize - 1) / Info.EraseSize * Info.EraseSize);
        ValidateRange(offset, erasedLength, Info.EraseSize);
        if (length <= 0) throw new ArgumentException("固件为空。", nameof(image));
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckDisposed();
            await EnsureWritableAsync(offset, erasedLength, progress, token).ConfigureAwait(false);
            var protection = Info.Kind == SpiFlashKind.Nand ? await UnlockNandAsync(token).ConfigureAwait(false) : (byte?)null;
            try
            {
                long done = 0;
                var page = new byte[Info.PageSize];
                for (long block = offset; block < offset + erasedLength; block += Info.EraseSize)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new("擦除并写入", done, length, $"擦除 0x{block:X8}"));
                    await EraseBlockAsync(block, token).ConfigureAwait(false);
                    for (var position = 0; position < Info.EraseSize && done < length; position += Info.PageSize)
                    {
                        token.ThrowIfCancellationRequested();
                        Array.Fill(page, (byte)0xff);
                        var count = (int)Math.Min(page.Length, length - done);
                        await image.ReadExactlyAsync(page.AsMemory(0, count), token).ConfigureAwait(false);
                        await ProgramPageAsync(block + position, page, token).ConfigureAwait(false);
                        var actual = Info.Kind == SpiFlashKind.Nand
                            ? await ReadNandPageAsync((int)((block + position) / Info.PageSize), false, true, token).ConfigureAwait(false)
                            : await ReadNorAsync(block + position, page.Length, token).ConfigureAwait(false);
                        if (!actual.AsSpan().SequenceEqual(page))
                        {
                            var difference = Enumerable.Range(0, page.Length).First(i => actual[i] != page[i]);
                            throw new IOException($"回读校验失败：物理地址 0x{block + position + difference:X8}。");
                        }
                        done += count;
                        progress?.Report(new("写入并回读校验", done, length, $"0x{block + position:X8}"));
                    }
                }
            }
            finally { await FinishWriteAsync(protection).ConfigureAwait(false); }
        }
        finally { _operation.Release(); }
    }

    /// <summary>Erase exact aligned physical range. Bad blocks cause refusal before any erase.</summary>
    public async Task EraseAsync(long offset, long length, IProgress<SpiFlashProgress>? progress = null, CancellationToken token = default)
    {
        ValidateRange(offset, length, Info.EraseSize);
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckDisposed();
            await EnsureWritableAsync(offset, length, progress, token).ConfigureAwait(false);
            var protection = Info.Kind == SpiFlashKind.Nand ? await UnlockNandAsync(token).ConfigureAwait(false) : (byte?)null;
            try
            {
                for (long done = 0; done < length; done += Info.EraseSize)
                {
                    await EraseBlockAsync(offset + done, token).ConfigureAwait(false);
                    // Check every page: a successful status bit alone is not an erase verification.
                    for (var page = 0; page < Info.EraseSize; page += Info.PageSize)
                    {
                        var data = Info.Kind == SpiFlashKind.Nand
                            ? await ReadNandPageAsync((int)((offset + done + page) / Info.PageSize), false, true, token).ConfigureAwait(false)
                            : await ReadNorAsync(offset + done + page, Info.PageSize, token).ConfigureAwait(false);
                        if (data.Any(value => value != 0xff)) throw new IOException($"擦除回读校验失败：0x{offset + done + page:X8}。");
                    }
                    progress?.Report(new("擦除并回读校验", done + Info.EraseSize, length));
                }
            }
            finally { await FinishWriteAsync(protection).ConfigureAwait(false); }
        }
        finally { _operation.Release(); }
    }

    private async Task EnsureWritableAsync(long offset, long length, IProgress<SpiFlashProgress>? progress, CancellationToken token)
    {
        if (Info.Kind == SpiFlashKind.Nand)
        {
            var bad = await FindBadBlocksAsync(offset, length, progress, token).ConfigureAwait(false);
            if (bad.Count > 0) throw new IOException($"目标范围包含坏块：{string.Join(", ", bad)}。已在擦除前停止；不会跳块改变固件物理地址。请使用不含坏块的范围或专用文件系统刷写流程。");
        }
        else
        {
            await WaitReadyAsync(token).ConfigureAwait(false);
            var status1 = (await _transport.ExchangeAsync([0x05], 1, token).ConfigureAwait(false))[0];
            var status2 = (await _transport.ExchangeAsync([0x35], 1, token).ConfigureAwait(false))[0];
            if ((status1 & 0x7c) != 0 || (status2 & 0x40) != 0)
                throw new IOException("NOR 启用了区域写保护（BP / CMP）。未改动状态寄存器，请解除目标芯片保护后重试。");
        }
    }

    private async Task<List<int>> FindBadBlocksAsync(long offset, long length, IProgress<SpiFlashProgress>? progress, CancellationToken token)
    {
        var bad = new List<int>();
        try
        {
            await SetFeatureAsync(0xb0, (byte)(_nandConfig & ~0x10), token).ConfigureAwait(false);
            for (long address = offset; address < offset + length; address += Info.EraseSize)
            {
                var block = (int)(address / Info.EraseSize);
                var marked = false;
                // Factory bad-block markers are in spare byte zero; inspect the first two pages conservatively.
                for (var page = 0; page < 2; page++)
                {
                    await _transport.ExchangeAsync(AddressCommand(0x13, block * 64 + page), 0, token).ConfigureAwait(false);
                    await WaitReadyAsync(token).ConfigureAwait(false);
                    var marker = await _transport.ExchangeAsync([0x03, 0x08, 0, 0], 1, token).ConfigureAwait(false);
                    marked |= marker[0] != 0xff;
                }
                if (marked) bad.Add(block);
                progress?.Report(new("检查坏块", address - offset + Info.EraseSize, length, $"块 {block} · 已发现 {bad.Count} 个坏块"));
            }
        }
        finally { await SetFeatureAsync(0xb0, _nandConfig, CancellationToken.None).ConfigureAwait(false); }
        return bad;
    }

    private async Task<byte> UnlockNandAsync(CancellationToken token)
    {
        var original = await GetFeatureAsync(0xa0, token).ConfigureAwait(false);
        try { await SetFeatureAsync(0xa0, (byte)(original & 0x83), token).ConfigureAwait(false); }
        catch { try { await SetFeatureAsync(0xa0, original, CancellationToken.None).ConfigureAwait(false); } catch { } throw; }
        return original;
    }

    private async Task FinishWriteAsync(byte? protection)
    {
        try
        {
            await WaitReadyAsync(CancellationToken.None).ConfigureAwait(false);
            await _transport.ExchangeAsync([0x04], 0, CancellationToken.None).ConfigureAwait(false);
        }
        finally { if (protection is not null) await SetFeatureAsync(0xa0, protection.Value, CancellationToken.None).ConfigureAwait(false); }
    }

    private async Task EraseBlockAsync(long address, CancellationToken token)
    {
        await WriteEnableAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await _transport.ExchangeAsync(AddressCommand(Info.Kind == SpiFlashKind.Nand ? (byte)0xd8 : (byte)0x20,
            Info.Kind == SpiFlashKind.Nand ? address / Info.PageSize : address), 0, CancellationToken.None).ConfigureAwait(false);
        // Once a destructive command is sent, wait for it to finish even when cancellation was requested.
        var status = await WaitReadyAsync(CancellationToken.None).ConfigureAwait(false);
        if (Info.Kind == SpiFlashKind.Nand && (status & 4) != 0) throw new IOException($"NAND 擦除失败：块 {address / Info.EraseSize}；已停止，不修改坏块标记。");
        token.ThrowIfCancellationRequested();
    }

    private async Task ProgramPageAsync(long address, byte[] data, CancellationToken token)
    {
        await WriteEnableAsync(token).ConfigureAwait(false);
        var header = Info.Kind == SpiFlashKind.Nand ? new byte[] { 0x02, 0, 0 } : AddressCommand(0x02, address);
        var command = new byte[header.Length + data.Length]; header.CopyTo(command, 0); data.CopyTo(command, header.Length);
        token.ThrowIfCancellationRequested();
        await _transport.ExchangeAsync(command, 0, CancellationToken.None).ConfigureAwait(false);
        if (Info.Kind == SpiFlashKind.Nand) await _transport.ExchangeAsync(AddressCommand(0x10, address / Info.PageSize), 0, CancellationToken.None).ConfigureAwait(false);
        var status = await WaitReadyAsync(CancellationToken.None).ConfigureAwait(false);
        if (Info.Kind == SpiFlashKind.Nand && (status & 8) != 0) throw new IOException($"NAND 编程失败：页 {address / Info.PageSize}；已停止，不修改坏块标记。");
        token.ThrowIfCancellationRequested();
    }

    private async Task WriteEnableAsync(CancellationToken token)
    {
        await WaitReadyAsync(token).ConfigureAwait(false);
        await _transport.ExchangeAsync([0x06], 0, token).ConfigureAwait(false);
        var status = await ReadStatusAsync(token).ConfigureAwait(false);
        if ((status & 2) == 0) throw new IOException("芯片未确认写使能（WEL=0），请检查 WP 引脚和写保护。");
    }

    private async Task<byte[]> ReadNandPageAsync(int row, bool includeSpare, bool checkEcc, CancellationToken token)
    {
        await _transport.ExchangeAsync(AddressCommand(0x13, row), 0, token).ConfigureAwait(false);
        var status = await WaitReadyAsync(token).ConfigureAwait(false);
        if (checkEcc)
        {
            var ecc = (status >> 4) & 3;
            if (ecc >= 2) throw new IOException($"NAND 第 {row} 页出现不可纠正 ECC 错误，已停止。原始备份模式可以保留该页及 OOB。");
            if (ecc == 1) CorrectedPages++;
        }
        return await _transport.ExchangeAsync([0x03, 0, 0, 0], Info.PageSize + (includeSpare ? Info.SpareSize : 0), token).ConfigureAwait(false);
    }

    private Task<byte[]> ReadNorAsync(long address, int count, CancellationToken token) => _transport.ExchangeAsync(AddressCommand(0x03, address), count, token);
    private Task<byte[]> ReadFeatureBytesAsync(byte address, CancellationToken token) => _transport.ExchangeAsync([0x0f, address], 1, token);
    private async Task<byte> GetFeatureAsync(byte address, CancellationToken token) => (await ReadFeatureBytesAsync(address, token).ConfigureAwait(false))[0];
    private async Task SetFeatureAsync(byte address, byte value, CancellationToken token)
    {
        await _transport.ExchangeAsync([0x1f, address, value], 0, token).ConfigureAwait(false);
        await WaitReadyAsync(token).ConfigureAwait(false);
        if (await GetFeatureAsync(address, token).ConfigureAwait(false) != value) throw new IOException($"NAND 寄存器 0x{address:X2} 设置未生效；请检查写保护和 OTP 锁定位。");
    }

    private async Task<byte> ReadStatusAsync(CancellationToken token) => Info.Kind == SpiFlashKind.Nand
        ? await GetFeatureAsync(0xc0, token).ConfigureAwait(false)
        : (await _transport.ExchangeAsync([0x05], 1, token).ConfigureAwait(false))[0];

    private async Task<byte> WaitReadyAsync(CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var status = await ReadStatusAsync(token).ConfigureAwait(false);
            if ((status & 1) == 0) return status;
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Flash 忙状态超时，未继续后续操作。");
            await Task.Delay(2, token).ConfigureAwait(false);
        }
    }

    private void ValidateRange(long offset, long length, int alignment)
    {
        if (offset < 0 || length <= 0 || offset > Info.Capacity || length > Info.Capacity - offset)
            throw new ArgumentOutOfRangeException(nameof(length), $"范围必须落在 0 – 0x{Info.Capacity:X} 内，且长度不能为零。");
        if (offset % alignment != 0 || (alignment > 1 && length % alignment != 0))
            throw new ArgumentException($"起始地址和操作长度必须按 {alignment:N0} 字节对齐。");
    }

    private static byte[] AddressCommand(byte opcode, long address) => [opcode, (byte)(address >> 16), (byte)(address >> 8), (byte)address];
    private void CheckDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public async ValueTask DisposeAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return; _disposed = true;
            try { if (Info.Kind == SpiFlashKind.Nand) await SetFeatureAsync(0xb0, _originalNandConfig, CancellationToken.None).ConfigureAwait(false); }
            finally { await _transport.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _operation.Release(); }
    }
}
