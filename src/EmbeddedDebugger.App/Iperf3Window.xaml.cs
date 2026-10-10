using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;

namespace EmbeddedDebugger.App;

public partial class Iperf3Window : Window, IDisposable
{
    private Process? _process;
    private CancellationTokenSource? _builtinCts;
    private TcpListener? _builtinListener;

    public Iperf3Window()
    {
        InitializeComponent();
        ModeBox.SelectedIndex = 0;
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => Dispose();
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HostBox is null) return;
        var server = ModeBox.SelectedIndex == 1;
        HostBox.IsEnabled = !server;
        DurationBox.IsEnabled = !server;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StopProcess();
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) { StatusText.Text = "端口应为 1–65535。"; return; }
        var server = ModeBox.SelectedIndex == 1;
        if (!server && string.IsNullOrWhiteSpace(HostBox.Text)) { StatusText.Text = "请输入客户端目标地址。"; return; }
        var duration = 0;
        if (!server && (!int.TryParse(DurationBox.Text, out duration) || duration is < 1 or > 86400)) { StatusText.Text = "测试时长应为 1–86400 秒。"; return; }
        var startInfo = new ProcessStartInfo { FileName = "iperf3", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (server) { startInfo.ArgumentList.Add("-s"); startInfo.ArgumentList.Add("-p"); startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture)); }
        else { startInfo.ArgumentList.Add("-c"); startInfo.ArgumentList.Add(HostBox.Text.Trim()); startInfo.ArgumentList.Add("-p"); startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture)); startInfo.ArgumentList.Add("-t"); startInfo.ArgumentList.Add(DurationBox.Text); }
        try
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += ProcessOutput;
            _process.ErrorDataReceived += ProcessOutput;
            _process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() => SetRunning(false)));
            if (!_process.Start()) throw new InvalidOperationException("无法启动 iperf3。");
            _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
            SetRunning(true);
            StatusText.Text = server ? $"iperf3 服务端监听 {port}" : $"正在测试 {HostBox.Text.Trim()}:{port}";
            await _process.WaitForExitAsync();
            SetRunning(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            OutputBox.AppendText($"{Environment.NewLine}系统 iperf3 不可用：{ex.Message}{Environment.NewLine}切换到内置吞吐测试。{Environment.NewLine}");
            await StartBuiltInAsync(server, HostBox.Text.Trim(), port, duration);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopProcess();

    private void ProcessOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        Dispatcher.BeginInvoke(new Action(() => { OutputBox.AppendText(e.Data + Environment.NewLine); OutputBox.ScrollToEnd(); }));
    }

    private void StopProcess()
    {
        var builtin = _builtinCts;
        _builtinCts = null;
        builtin?.Cancel();
        try { _builtinListener?.Stop(); } catch (ObjectDisposedException) { }
        _builtinListener = null;
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch (InvalidOperationException) { }
        _process?.Dispose(); _process = null;
        if (IsLoaded) SetRunning(false);
    }

    private async Task StartBuiltInAsync(bool server, string host, int port, int duration)
    {
        var lifetime = new CancellationTokenSource();
        _builtinCts = lifetime;
        SetRunning(true);
        StatusText.Text = server ? $"内置服务端监听 {port}" : $"内置吞吐测试：{host}:{port}";
        try
        {
            if (server) await RunBuiltInServerAsync(port, lifetime.Token);
            else await RunBuiltInClientAsync(host, port, duration, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { AppendOutput($"内置测试失败：{ex.Message}"); }
        finally
        {
            if (ReferenceEquals(_builtinCts, lifetime))
            {
                _builtinCts = null;
            }
            lifetime.Dispose();
            if (IsLoaded) SetRunning(false);
        }
    }

    private async Task RunBuiltInClientAsync(string host, int port, int duration, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        using var stream = client.GetStream();
        var buffer = new byte[64 * 1024];
        var bytes = 0L;
        var started = Stopwatch.GetTimestamp();
        var nextReport = TimeSpan.FromSeconds(1);
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(duration))
        {
            await stream.WriteAsync(buffer, cancellationToken);
            bytes += buffer.Length;
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= nextReport)
            {
                AppendOutput($"[内置] {elapsed.TotalSeconds:0.0}s · {FormatRate(bytes, elapsed)}");
                nextReport += TimeSpan.FromSeconds(1);
            }
        }
        var total = Stopwatch.GetElapsedTime(started);
        AppendOutput($"[内置] 完成 · {bytes:N0} bytes · {FormatRate(bytes, total)}");
        StatusText.Text = "内置吞吐测试完成";
    }

    private async Task RunBuiltInServerAsync(int port, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        _builtinListener = listener;
        listener.Start();
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var buffer = new byte[64 * 1024];
        var bytes = 0L;
        var started = Stopwatch.GetTimestamp();
        var nextReport = TimeSpan.FromSeconds(1);
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            bytes += read;
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= nextReport)
            {
                AppendOutput($"[内置] {elapsed.TotalSeconds:0.0}s · {bytes:N0} bytes · {FormatRate(bytes, elapsed)}");
                nextReport += TimeSpan.FromSeconds(1);
            }
        }
        var total = Stopwatch.GetElapsedTime(started);
        AppendOutput($"[内置] 客户端断开 · {bytes:N0} bytes · {FormatRate(bytes, total)}");
        StatusText.Text = "内置服务端测试完成";
        listener.Stop();
        _builtinListener = null;
    }

    private void AppendOutput(string text)
    {
        OutputBox.AppendText(text + Environment.NewLine);
        OutputBox.ScrollToEnd();
    }

    private static string FormatRate(long bytes, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var bits = bytes * 8d / seconds;
        return bits switch
        {
            >= 1_000_000_000 => $"{bits / 1_000_000_000:0.00} Gbit/s",
            >= 1_000_000 => $"{bits / 1_000_000:0.00} Mbit/s",
            _ => $"{bits / 1_000:0.00} Kbit/s"
        };
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running; StopButton.IsEnabled = running; ModeBox.IsEnabled = !running; PortBox.IsEnabled = !running; HostBox.IsEnabled = !running && ModeBox.SelectedIndex == 0; DurationBox.IsEnabled = !running && ModeBox.SelectedIndex == 0;
    }

    public void Dispose()
    {
        StopProcess();
        GC.SuppressFinalize(this);
    }
}
