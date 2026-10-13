using System.Net;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App;

public partial class Iperf3Window : Window, IDisposable
{
    private CancellationTokenSource? _operation;
    private bool _closed;
    private bool _fillingNode;
    private readonly Action<Iperf3ToolPreferences>? _savePreferences;
    public Iperf3Window(Iperf3ToolPreferences? preferences = null, Action<Iperf3ToolPreferences>? savePreferences = null)
    {
        _savePreferences = savePreferences;
        InitializeComponent();
        NodeBox.ItemsSource = new[] { new PublicIperf3Server("自定义 iperf3 服务端", "", 5201, 5201, "") }.Concat(PublicIperf3Servers.All).ToArray();
        NodeBox.SelectedIndex = 0; ProtocolBox.SelectedIndex = 0; ModeBox.SelectedIndex = 0;
        if (preferences is not null) ApplyPreferences(preferences);
        WindowTheme.ApplyDarkTitleBar(this); Closed += (_, _) => Dispose();
        Closed += (_, _) => _savePreferences?.Invoke(CapturePreferences());
    }

    private void ApplyPreferences(Iperf3ToolPreferences preferences)
    {
        ModeBox.SelectedIndex = preferences.ModeIndex;
        HostBox.Text = preferences.Host;
        PortBox.Text = preferences.Port.ToString(CultureInfo.InvariantCulture);
        ProtocolBox.SelectedIndex = preferences.ProtocolIndex;
        BlockSizeBox.Text = preferences.BlockSizeBytes.ToString(CultureInfo.InvariantCulture);
        DirectionBox.SelectedIndex = preferences.DirectionIndex;
        DurationBox.Text = preferences.DurationSeconds.ToString(CultureInfo.InvariantCulture);
        RateBox.Text = preferences.Rate;
        RateUnitBox.SelectedIndex = preferences.RateUnitIndex;
    }

    private Iperf3ToolPreferences CapturePreferences() => new()
    {
        ModeIndex = ModeBox.SelectedIndex,
        Host = HostBox.Text,
        Port = int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 5201,
        ProtocolIndex = ProtocolBox.SelectedIndex,
        DirectionIndex = DirectionBox.SelectedIndex,
        DurationSeconds = int.TryParse(DurationBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) ? duration : 10,
        BlockSizeBytes = int.TryParse(BlockSizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockSize) ? blockSize : 131072,
        Rate = RateBox.Text,
        RateUnitIndex = RateUnitBox.SelectedIndex
    };
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StatusText is null) return;
        var server = ModeBox.SelectedIndex == 1;
        HostLabel.Text = server ? "监听地址（IPv4 / IPv6）" : "远程地址";
        HostBox.Text = server ? "0.0.0.0" : "127.0.0.1";
        ProtocolHint.Text = server ? "监听同一端口的 TCP 控制连接和 UDP 数据流，协议、方向、大小、限速由客户端指定。" : ProtocolDescription;
        SetRunning(false);
    }
    private string ProtocolDescription => ProtocolBox.SelectedIndex == 1
        ? "UDP 报文大小包含 iperf3 的 16 字节头；超过路径 MTU 可能分片。包/s 会按报文大小换算为 bit/s。"
        : "TCP 的大小表示每次写入的数据块；实际网卡分包由系统决定。";
    private void Protocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BlockSizeBox is null) return;
        var udp = ProtocolBox.SelectedIndex == 1;
        BlockLabel.Text = udp ? "UDP 报文大小（字节）" : "写入块大小（字节）";
        if (BlockSizeBox.Text is "131072" or "1200") BlockSizeBox.Text = udp ? "1200" : "131072";
        PacketRateItem.IsEnabled = udp;
        if (!udp && RateUnitBox.SelectedIndex == 4) RateUnitBox.SelectedIndex = 0;
        ProtocolHint.Text = ProtocolDescription;
    }
    private void Node_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HostBox is null || NodeBox.SelectedItem is not PublicIperf3Server node) return;
        if (node.Host.Length == 0) { NodeHint.Text = "填写自己的 iperf3 服务端，或从公共节点填入地址。"; return; }
        _fillingNode = true;
        try
        {
            HostBox.Text = node.Host;
            PortBox.Text = node.DefaultPort.ToString(CultureInfo.InvariantCulture);
            NodeHint.Text = node.Description;
        }
        finally { _fillingNode = false; }
    }
    private void Endpoint_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_fillingNode && NodeBox is not null) NodeBox.SelectedIndex = 0;
        if (CommandHint is not null) CommandHint.Text = ModeBox.SelectedIndex == 1
            ? $"对端示例：iperf3 -c 本机地址 -p {PortBox.Text} -u -b 20M -l 1200 -t 10"
            : $"对端启动：iperf3 -s -p {PortBox.Text}";
    }
    private void Source_Click(object sender, RoutedEventArgs e)
    {
        var source = (NodeBox.SelectedItem as PublicIperf3Server)?.Source;
        try { Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(source) ? "https://iperf.fr/iperf-servers.php" : source) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "无法打开节点来源：" + ex.Message; }
    }
    private Iperf3Options ReadOptions()
    {
        if (!int.TryParse(DurationBox.Text, out var seconds)) throw new FormatException("请输入整数测试时长。");
        if (!int.TryParse(BlockSizeBox.Text, out var block)) throw new FormatException("请输入整数报文 / 写入块大小。");
        if (!decimal.TryParse(RateBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || rate < 0)
            throw new FormatException("请输入有效的发送速率（0 表示不限速）。");
        var udp = ProtocolBox.SelectedIndex == 1;
        if (!udp && RateUnitBox.SelectedIndex == 4) throw new FormatException("包/s 仅适用于 UDP。");
        var multiplier = RateUnitBox.SelectedIndex switch { 0 => 1_000_000m, 1 => 1000m, 2 => 1_000_000_000m, 3 => 1m, _ => block * 8m };
        var bits = decimal.Truncate(rate * multiplier);
        if (bits > 100_000_000_000m || (rate > 0 && bits < 1)) throw new FormatException("发送带宽应为 1 bit/s–100 Gbit/s，或填 0 不限速。");
        var options = new Iperf3Options(seconds, DirectionBox.SelectedIndex == 1, udp, block, (long)bits);
        options.Validate(); return options;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) { StatusText.Text = "端口应为 1–65535。"; return; }
        var server = ModeBox.SelectedIndex == 1; var host = HostBox.Text.Trim();
        if (string.IsNullOrEmpty(host)) { StatusText.Text = server ? "请输入监听 IP 地址。" : "请输入远程地址。"; return; }
        IPAddress? address = null;
        if (server && !IPAddress.TryParse(host, out address)) { StatusText.Text = "监听地址必须为 IPv4 或 IPv6 地址，0.0.0.0 / :: 表示所有接口。"; return; }
        Iperf3Options? options = null;
        try { if (!server) options = ReadOptions(); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { StatusText.Text = ex.Message; return; }
        using var operation = new CancellationTokenSource(); _operation = operation; SetRunning(true); OutputBox.Clear();
        SpeedText.Text = TransferredText.Text = UdpSummaryText.Text = "—";
        var reverse = options?.Reverse == true;
        StatusText.Text = server ? $"正在监听 {host}:{port}" : $"正在连接 {host}:{port}（{(reverse ? "反向 / 下载" : "正向 / 上传")}）";
        if (options is not null)
            Append($"{(options.Udp ? "UDP" : "TCP")} · {options.Seconds}s · {options.EffectiveBlockBytes} 字节 · 发送限速 {(options.BitsPerSecond == 0 ? "不限速" : Rate(options.BitsPerSecond))}");
        try
        {
            var progress = new Progress<Iperf3Progress>(p =>
            {
                if (!ReferenceEquals(_operation, operation) || _closed) return;
                SpeedCaption.Text = "当前速率"; SpeedText.Text = Rate(p.BitsPerSecond); TransferredText.Text = Size(p.Bytes);
                if (p.Udp is not null) UdpSummaryText.Text = p.Sender ? "接收质量将在测试结束后返回" : Quality(p.Udp);
                if (p.IntervalSeconds < .005 && p.IntervalBytes == 0) return;
                Append($"{p.Seconds - p.IntervalSeconds,6:F2}–{p.Seconds:F2}s  {(p.Sender ? "发送" : "接收")}  {p.IntervalBytes / 1048576d,10:F2} MiB  {Rate(p.BitsPerSecond)}" +
                    (p.Udp is not null && !p.Sender ? "  " + Quality(p.Udp) : ""));
            });
            if (server) await Iperf3Client.RunServerAsync(address!, port, progress,
                new Progress<string>(message => { if (ReferenceEquals(_operation, operation)) Append(message); }),
                new Progress<Iperf3Result>(result => { if (ReferenceEquals(_operation, operation)) Report(result); }), operation.Token);
            else { Report(await Iperf3Client.RunAsync(host, port, options!, progress, operation.Token)); if (!_closed) StatusText.Text = "测试完成。"; }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { if (!_closed) StatusText.Text = "已停止。"; }
        catch (Exception ex) { if (!_closed) { StatusText.Text = "测试失败：" + ex.Message; Append(StatusText.Text); } }
        finally { _operation = null; if (!_closed) SetRunning(false); }
    }
    private void Report(Iperf3Result result)
    {
        if (_closed) return;
        SpeedCaption.Text = "接收端平均速率";
        SpeedText.Text = Rate(result.LocalSender ? result.PeerBitsPerSecond : result.LocalBitsPerSecond); TransferredText.Text = Size(result.LocalBytes);
        Append($"本地{(result.LocalSender ? "发送" : "接收")}：{result.LocalSeconds:F2}s · {result.LocalBytes:N0} bytes · {Rate(result.LocalBitsPerSecond)}");
        Append($"对端{(result.LocalSender ? "接收" : "发送")}：{result.PeerSeconds:F2}s · {result.PeerBytes:N0} bytes · {Rate(result.PeerBitsPerSecond)}");
        if (result.ReceiverUdp is { } udp)
        {
            UdpSummaryText.Text = Quality(udp);
            Append($"UDP 接收端：{Quality(udp)} · 乱序 {(udp.OutOfOrderPackets is { } count ? count.ToString("N0", CultureInfo.CurrentCulture) : "对端未提供")}");
        }
        else UdpSummaryText.Text = "—";
    }
    private static string Quality(Iperf3UdpStatistics udp) => $"丢包 {udp.LostPackets:N0}/{udp.Packets:N0} ({udp.LossPercent:F2}%) · 抖动 {udp.JitterMilliseconds:F3} ms";
    private void Append(string message)
    {
        if (_closed) return;
        if (OutputBox.Text.Length > 120000) OutputBox.Text = OutputBox.Text[^60000..];
        OutputBox.AppendText(message + Environment.NewLine); OutputBox.ScrollToEnd();
    }
    private static string Rate(double bits) => bits >= 1e9 ? $"{bits / 1e9:F2} Gbit/s" : bits >= 1e6 ? $"{bits / 1e6:F2} Mbit/s" : $"{bits / 1e3:F2} Kbit/s";
    private static string Size(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576d:F2} MiB" : $"{bytes / 1024d:F2} KiB";
    private void Stop_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); StopButton.IsEnabled = false; }
    private void SetRunning(bool running)
    {
        if (StartButton is null) return;
        var client = ModeBox.SelectedIndex != 1;
        StartButton.IsEnabled = ParametersPanel.IsEnabled = !running;
        StopButton.IsEnabled = running;
        ClientOptionsPanel.IsEnabled = ProtocolBox.IsEnabled = DirectionBox.IsEnabled = client;
        NodePanel.IsEnabled = !running && client;
        StartButton.Content = client ? "开始测速" : "开始监听";
    }
    public void Dispose() { _closed = true; _operation?.Cancel(); GC.SuppressFinalize(this); }
}
