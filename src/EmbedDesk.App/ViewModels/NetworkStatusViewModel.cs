using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App.ViewModels;

public enum NetworkProbeMode { Automatic, Icmp, Disabled }

/// <summary>Called on the UI thread. Every target or mode change invalidates queued probe results.</summary>
public sealed class NetworkStatusViewModel : ObservableObject, IDisposable
{
    private readonly INetworkProbe _probe;
    private readonly TimeSpan _interval;
    private readonly Queue<NetworkProbeResult> _samples = new();
    private CancellationTokenSource? _lifetime;
    private NetworkProbeTarget? _target;
    private string _context = "等待连接", _label = "等待连接", _tooltip = "连接后自动探测网络", _tone = "#9AA5AF";
    private int _quality;
    private bool _disposed;
    private NetworkProbeMode _mode;

    public NetworkStatusViewModel(INetworkProbe? probe = null, TimeSpan? interval = null)
    {
        _probe = probe ?? new NetworkProbe();
        _interval = interval ?? TimeSpan.FromSeconds(10);
        UseAutomaticCommand = new RelayCommand(() => SetMode(NetworkProbeMode.Automatic));
        UseIcmpCommand = new RelayCommand(() => SetMode(NetworkProbeMode.Icmp));
        DisableCommand = new RelayCommand(() => SetMode(NetworkProbeMode.Disabled));
    }

    public string Label => _label;
    public string Tooltip => _tooltip;
    public string Tone => _tone;
    public double Bar1Opacity => _quality >= 1 ? 1 : 0.2;
    public double Bar2Opacity => _quality >= 2 ? 1 : 0.2;
    public double Bar3Opacity => _quality >= 3 ? 1 : 0.2;
    public double Bar4Opacity => _quality >= 4 ? 1 : 0.2;
    public double? Milliseconds { get; private set; }
    public int SampleCount => _samples.Count;
    public bool IsMonitoring => _lifetime is not null;
    public bool IsAutomatic => _mode == NetworkProbeMode.Automatic;
    public bool IsIcmp => _mode == NetworkProbeMode.Icmp;
    public bool IsDisabled => _mode == NetworkProbeMode.Disabled;
    public NetworkProbeTarget? Target => _target;
    public IRelayCommand UseAutomaticCommand { get; }
    public IRelayCommand UseIcmpCommand { get; }
    public IRelayCommand DisableCommand { get; }

    public void Track(NetworkProbeTarget target, string context)
    {
        if (_disposed || _target == target && _context == context) return;
        _target = target;
        _context = context;
        Restart();
    }

    public void Stop(string reason = "等待连接")
    {
        if (_disposed || _target is null && _context == reason) return;
        _target = null;
        _context = reason;
        Restart();
    }

    private void SetMode(NetworkProbeMode mode)
    {
        if (_disposed || _mode == mode) return;
        _mode = mode;
        Restart();
    }

    private void Restart()
    {
        Cancel();
        _samples.Clear();
        Milliseconds = null;
        _quality = 0;
        _tone = "#9AA5AF";
        _label = IsDisabled ? "探测已关闭" : _target is null ? _context : "正在测量…";
        _tooltip = _target is null ? _context : $"{_context} · {_target.Host}\n右键可选择 TCPing / ICMP 或关闭探测。";
        if (!_disposed && !IsDisabled && _target is not null)
        {
            var lifetime = _lifetime = new CancellationTokenSource();
            var target = IsIcmp ? _target with { TcpPort = null } : _target;
            _ = Task.Run(() => MonitorAsync(target, lifetime));
        }
        OnPropertyChanged(string.Empty);
    }

    private async Task MonitorAsync(NetworkProbeTarget target, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var result = await _probe.ProbeAsync(target, lifetime.Token).ConfigureAwait(false);
                void Apply()
                {
                    if (!_disposed && ReferenceEquals(_lifetime, lifetime) && !lifetime.IsCancellationRequested) ApplyResult(target, result);
                }
                if (Application.Current?.Dispatcher is { } dispatcher) await dispatcher.InvokeAsync(Apply);
                else Apply();
                await Task.Delay(_interval, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { lifetime.Dispose(); }
    }

    private void ApplyResult(NetworkProbeTarget target, NetworkProbeResult result)
    {
        _samples.Enqueue(result);
        while (_samples.Count > 20) _samples.Dequeue();
        var replies = _samples.Where(s => s.Outcome == NetworkProbeOutcome.Success && s.Milliseconds.HasValue).Select(s => s.Milliseconds!.Value).ToArray();
        var missed = _samples.Count(s => s.Outcome != NetworkProbeOutcome.Success);
        Milliseconds = result.Outcome == NetworkProbeOutcome.Success ? result.Milliseconds : null;
        var method = target.TcpPort.HasValue ? "TCP" : "ICMP";
        if (Milliseconds is double ms)
        {
            _label = $"{method} {(ms < 1 ? "<1" : ms.ToString("0", System.Globalization.CultureInfo.CurrentCulture))} ms";
            _quality = ms <= 60 ? 4 : ms <= 150 ? 3 : ms <= 300 ? 2 : 1;
            if (missed > 0) _quality = Math.Min(_quality, 2);
            _tone = _quality >= 3 ? "#4CC9B0" : _quality == 2 ? "#E6BC68" : "#EF936C";
        }
        else
        {
            _label = $"{method} 不可测";
            _quality = 0;
            _tone = result.Outcome == NetworkProbeOutcome.NoReply ? "#E6BC68" : "#9AA5AF";
        }
        var statistics = $"最近 {_samples.Count} 次探测 · 未成功 {missed}/{_samples.Count}（{100d * missed / _samples.Count:0}%）";
        if (replies.Length > 0) statistics += $"\n平均 {replies.Average():0.0} ms · 最高 {replies.Max():0.0} ms";
        // Only adjacent successful samples contribute to jitter; never bridge a failed probe.
        var jitter = _samples.Zip(_samples.Skip(1)).Where(p => p.First.Milliseconds.HasValue && p.Second.Milliseconds.HasValue)
            .Select(p => Math.Abs(p.First.Milliseconds!.Value - p.Second.Milliseconds!.Value)).ToArray();
        if (jitter.Length > 0) statistics += $" · 抖动 {jitter.Average():0.0} ms";
        _tooltip = $"{_context}\n{result.Detail}\n{statistics}\n" + (target.TcpPort.HasValue
            ? "TCPing 测量新建 TCP 连接的握手耗时，不代表当前会话的操作延迟。\n每 10 秒建立并立即关闭一个连接，不发送业务数据；服务端可能记录这些连接。"
            : "ICMP 往返耗时，不代表服务端口或当前会话状态。每 10 秒探测一次。")
            + "\n探测未成功不等于会话已断开。右键可切换方式或关闭探测。";
        OnPropertyChanged(string.Empty);
    }

    private void Cancel()
    {
        var lifetime = _lifetime;
        _lifetime = null;
        lifetime?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _target = null;
        _context = "探测已停止";
        Restart();
    }
}
