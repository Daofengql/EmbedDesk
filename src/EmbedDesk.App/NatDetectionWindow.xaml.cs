using System.Collections.ObjectModel;
using System.Windows;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App;

public partial class NatDetectionWindow : Window, IDisposable
{
    private CancellationTokenSource? _lifetime;
    private bool _closed;

    public NatDetectionWindow()
    {
        InitializeComponent();
        DataContext = this;
        ServersBox.Text = string.Join(Environment.NewLine, NatDetector.DefaultServers);
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => Dispose();
    }

    public ObservableCollection<NatObservation> Results { get; } = [];
    public ObservableCollection<NatExitReport> Exits { get; } = [];

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_lifetime is not null || _closed) return;
        var servers = ServersBox.Text.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (servers.Length is < 1 or > 16) { StatusText.Text = "请填写 1–16 个 STUN 节点，每行一个或用分号分隔。"; return; }
        Results.Clear();
        Exits.Clear();
        ServerSettings.IsExpanded = false;
        ReportScroll.ScrollToTop();
        NatTypeText.Text = "检测中…";
        SummaryText.Text = "正在采集节点映射；完成后按公网 IP 分组，分别复测并判断。";
        StatusText.Text = "IPv4 / UDP · 国内节点优先";
        using var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ServersBox.IsEnabled = false;
        var progress = new Progress<NatObservation>(observation =>
        {
            if (_closed || !ReferenceEquals(_lifetime, lifetime) || lifetime.IsCancellationRequested) return;
            Results.Add(observation);
            StatusText.Text = observation.Server + " · " + observation.Detail;
        });
        try
        {
            var report = await new NatDetector().DetectAsync(servers, progress, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            NatTypeText.Text = report.Label;
            SummaryText.Text = report.Evidence;
            foreach (var exit in report.Exits) Exits.Add(exit);
            StatusText.Text = "检测完成 · IPv4 / UDP";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            NatTypeText.Text = "已停止 / Canceled";
            SummaryText.Text = "检测未完成，请重新检测后查看综合结论。";
            StatusText.Text = "已停止";
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            NatTypeText.Text = "检测失败 / Failed";
            SummaryText.Text = ex.Message;
        }
        finally
        {
            _lifetime = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            ServersBox.IsEnabled = true;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _lifetime?.Cancel();

    public void Dispose()
    {
        _closed = true;
        _lifetime?.Cancel(); // The owning async operation disposes its source after it exits.
        GC.SuppressFinalize(this);
    }
}
