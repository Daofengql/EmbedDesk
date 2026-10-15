using System.IO;
using System.Security;
using System.Windows;
using EmbedDesk.Infrastructure.Connections;
using EmbedDesk.Infrastructure.Serial;

namespace EmbedDesk.App;

public partial class RemoteSerialUseWindow : Window
{
    private int _scanVersion;
    private bool _scanning, _closed;
    private string _scanError = string.Empty;
    public bool UseVirtualPort => UsageMode.SelectedIndex == 1;
    public VirtualSerialPair? SelectedPair => PairBox.SelectedItem as VirtualSerialPair;
    internal Task ScanTask { get; private set; } = Task.CompletedTask;

    public RemoteSerialUseWindow(DeviceRemoteService service) : this(service, null) { }
    internal RemoteSerialUseWindow(DeviceRemoteService service, IReadOnlyList<VirtualSerialPair>? pairs)
    {
        InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this);
        ServiceLabel.Text = $"{service.DeviceId} · {service.Service.Name}";
        Closed += (_, _) => { _closed = true; _scanVersion++; };
        if (pairs is null) ScanTask = RefreshPairsAsync();
        else { PairBox.ItemsSource = pairs; PairBox.SelectedItem = pairs.FirstOrDefault(p => p.Problem.Length == 0) ?? pairs.FirstOrDefault(); UpdateMode(); }
    }
    private void Mode_Changed(object sender, RoutedEventArgs e) { if (VirtualFields is not null) UpdateMode(); }
    private void Pair_Changed(object sender, RoutedEventArgs e) { if (DriverStatus is not null) UpdateMode(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await (ScanTask = RefreshPairsAsync());
    private async Task RefreshPairsAsync()
    {
        var version = ++_scanVersion; _scanning = true; _scanError = string.Empty; UpdateMode();
        try
        {
            var pairs = await Task.Run(Com0ComEndpoint.ScanPairs);
            if (_closed || version != _scanVersion) return;
            PairBox.ItemsSource = pairs; PairBox.SelectedItem = pairs.FirstOrDefault(p => p.Problem.Length == 0) ?? pairs.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { if (!_closed && version == _scanVersion) _scanError = ex.Message; }
        finally { if (!_closed && version == _scanVersion) { _scanning = false; UpdateMode(); } }
    }
    private void UpdateMode()
    {
        VirtualFields.Visibility = UseVirtualPort ? Visibility.Visible : Visibility.Collapsed;
        AppHint.Visibility = UseVirtualPort ? Visibility.Collapsed : Visibility.Visible;
        RefreshPairsButton.IsEnabled = !_scanning;
        PairBox.IsEnabled = !_scanning;
        DriverStatus.Text = _scanning ? "正在检测 com0com 串口对…" : _scanError.Length > 0 ? _scanError : SelectedPair is { } pair ? pair.Problem : "未检测到可用的 com0com 串口对。可安装并配置驱动后刷新，或选择在本软件内使用。";
        UseSerialButton.Content = UseVirtualPort ? "保存并启用映射" : "进入串口调试";
        UseSerialButton.IsEnabled = !UseVirtualPort || !_scanning && _scanError.Length == 0 && SelectedPair is { Problem.Length: 0 };
    }
    private void Use_Click(object sender, RoutedEventArgs e)
    { if (!UseVirtualPort || !_scanning && _scanError.Length == 0 && SelectedPair is { Problem.Length: 0 }) DialogResult = true; }
}
