using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbedDesk.Infrastructure.Connections;

namespace EmbedDesk.App.ViewModels;

internal sealed class RelayLatencyStatus : ObservableObject, IAsyncDisposable
{
    private readonly Func<DeviceNodeOptions, CancellationToken, Task<RelayHealthResult>> _probe;
    private readonly TimeSpan _interval;
    private readonly object _sync = new();
    private CancellationTokenSource? _life, _probeLife;
    private Task _runner = Task.CompletedTask;
    private bool _online, _hasResult;
    private string _text = "未连接", _color = "#9AA5AF", _hint = "上线后自动探测当前中继延迟。";
    public string Text { get => _text; private set => SetProperty(ref _text, value); }
    public string Color { get => _color; private set => SetProperty(ref _color, value); }
    public string Hint { get => _hint; private set => SetProperty(ref _hint, value); }

    internal RelayLatencyStatus(Func<DeviceNodeOptions, CancellationToken, Task<RelayHealthResult>>? probe = null, TimeSpan? interval = null)
    { _probe = probe ?? RelayHealthProbe.CheckAsync; _interval = interval ?? TimeSpan.FromSeconds(5); }

    internal async Task StartAsync(DeviceNodeOptions options, bool online)
    {
        await StopAsync();
        _life = new();
        SetOnline(online);
        _runner = RunAsync(options, _life.Token);
    }
    internal void SetOnline(bool online)
    {
        Console.WriteLine($"DEBUG SetOnline({online}) online={_online}");
        CancellationTokenSource? probeToCancel = null;
        lock (_sync)
        {
            if (_online == online) return;
            _online = online;
            if (!online)
            {
                // Take the probe reference under the same lock used by RunAsync
                // when it checks _online and creates a request.  Without this
                // gate, a runner that had just passed the check could create a
                // fresh HTTP request after the connection went offline.
                probeToCancel = _probeLife;
                _probeLife = null;
            }
        }
        if (!online)
        {
            probeToCancel?.Cancel(); _hasResult = false;
            Text = "未连接"; Color = "#9AA5AF"; Hint = "上线后自动探测当前中继延迟。";
        }
        else if (!_hasResult)
        { Text = "探测中…"; Color = "#9AA5AF"; Hint = "正在探测当前中继延迟。"; }
    }
    private async Task RunAsync(DeviceNodeOptions options, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_online)
                {
                    using var probeLife = CancellationTokenSource.CreateLinkedTokenSource(token);
                    lock (_sync)
                    {
                        if (!_online || token.IsCancellationRequested) continue;
                        _probeLife = probeLife;
                        Console.WriteLine($"DEBUG probe begin online={_online}");
                    }
                    try
                    {
                        var result = await _probe(options, probeLife.Token);
                        lock (_sync)
                        {
                            if (!probeLife.IsCancellationRequested && _online) Apply(result);
                            Console.WriteLine($"DEBUG probe finish online={_online} canceled={probeLife.IsCancellationRequested}");
                        }
                    }
                    catch (OperationCanceledException) when (probeLife.IsCancellationRequested) { }
                    finally
                    {
                        lock (_sync)
                        {
                            if (ReferenceEquals(_probeLife, probeLife)) _probeLife = null;
                        }
                    }
                }
                await Task.Delay(_interval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    internal void Apply(RelayHealthResult result)
    {
        _hasResult = true;
        if (!result.Available || result.Milliseconds < 0)
        { Text = "不可用"; Color = "#9AA5AF"; Hint = "中继延迟探测失败：" + result.Error + "；将自动重试，不改变设备连接状态。"; return; }
        Text = result.Milliseconds == 0 ? "<1 ms" : result.Milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
        Color = result.Milliseconds <= 100 ? "#4CC9B0" : result.Milliseconds <= 200 ? "#F0C36D" : "#FF8585";
        Hint = "当前中继 HTTP 延迟（含连接及 TLS 握手），约每 5 秒刷新。\n绿色 ≤100 ms，黄色 101–200 ms，红色 >200 ms。\n最近采样：" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    }
    internal async Task StopAsync()
    {
        _life?.Cancel();
        await _runner;
        _life?.Dispose(); _life = null;
        _hasResult = false; _online = false;
        Text = "未连接"; Color = "#9AA5AF"; Hint = "上线后自动探测当前中继延迟。";
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
