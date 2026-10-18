using System.IO;
using System.Security;
using System.Windows;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Connections;
using EmbedDesk.Infrastructure.Serial;

namespace EmbedDesk.App;

public partial class RemoteSerialUseWindow : Window
{
    private readonly Func<DeviceRemoteService, VirtualSerialPair?, Task<bool>>? _submit;
    private readonly Func<string>? _submissionError;
    private int _scanVersion;
    private bool _initialized, _scanning, _closed, _saving;
    private string _scanError = string.Empty, _operationError = string.Empty;
    public bool UseVirtualPort => UsageMode.SelectedIndex == 1;
    public DeviceRemoteService? SelectedService => ServiceBox.SelectedItem as DeviceRemoteService;
    public VirtualSerialPair? SelectedPair => PairBox.SelectedItem as VirtualSerialPair;
    internal Task ScanTask { get; private set; } = Task.CompletedTask;
    internal Task SubmissionTask { get; private set; } = Task.CompletedTask;

    public RemoteSerialUseWindow(DeviceRemoteService service) : this(service, null) { }
    internal RemoteSerialUseWindow(DeviceRemoteService service, IReadOnlyList<VirtualSerialPair>? pairs) : this([service], pairs) { }
    public RemoteSerialUseWindow(IEnumerable<DeviceRemoteService> services, ConnectionCenterViewModel model, Action<DeviceRemoteService> openInApp)
        : this(services, null, async (service, pair) =>
        {
            if (pair is not null) return await model.MapVirtualSerialAsync(service, pair);
            openInApp(model.ResolveRemoteService(service));
            return true;
        }, () => model.Error) { }
    internal RemoteSerialUseWindow(IEnumerable<DeviceRemoteService> services, IReadOnlyList<VirtualSerialPair>? pairs,
        Func<DeviceRemoteService, VirtualSerialPair?, Task<bool>>? submit = null, Func<string>? submissionError = null)
    {
        InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this);
        _submit = submit; _submissionError = submissionError;
        var available = services.Where(service => service.Service.Kind == SharedServiceKind.Serial && service.Service.Enabled).ToArray();
        ServiceBox.ItemsSource = available; ServiceBox.SelectedItem = available.FirstOrDefault();
        _initialized = true;
        Closed += (_, _) => { _closed = true; _scanVersion++; };
        Closing += (_, args) => { if (_saving) args.Cancel = true; };
        if (pairs is null) ScanTask = RefreshPairsAsync();
        else { PairBox.ItemsSource = pairs; PairBox.SelectedItem = pairs.FirstOrDefault(p => p.Problem.Length == 0) ?? pairs.FirstOrDefault(); UpdateMode(); }
    }
    private void Service_Changed(object sender, RoutedEventArgs e) { if (_initialized) { _operationError = string.Empty; UpdateMode(); } }
    private void Mode_Changed(object sender, RoutedEventArgs e) { if (_initialized) { _operationError = string.Empty; UpdateMode(); } }
    private void Pair_Changed(object sender, RoutedEventArgs e) { if (_initialized) { _operationError = string.Empty; UpdateMode(); } }
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
        if (!_initialized) return;
        var service = SelectedService;
        VirtualFields.Visibility = UseVirtualPort ? Visibility.Visible : Visibility.Collapsed;
        AppHint.Visibility = UseVirtualPort ? Visibility.Collapsed : Visibility.Visible;
        EmptyServicesHint.Visibility = service is null ? Visibility.Visible : Visibility.Collapsed;
        ServiceBox.IsEnabled = !_saving && ServiceBox.HasItems;
        UsageMode.IsEnabled = !_saving;
        RefreshPairsButton.IsEnabled = !_scanning && !_saving;
        PairBox.IsEnabled = !_scanning && !_saving;
        CancelButton.IsEnabled = !_saving;
        DriverStatus.Text = _scanning ? "正在检测 com0com 串口对…" : _scanError.Length > 0 ? _scanError : SelectedPair is { } pair ? pair.Problem : "未检测到可用的 com0com 串口对。可安装并配置驱动后刷新，或选择在本软件内使用。";
        UseSerialButton.Content = _saving ? "正在启用…" : UseVirtualPort ? "保存并启用映射" : "进入串口调试";
        UseSerialButton.IsEnabled = !_saving && service is not null && (!UseVirtualPort || !_scanning && _scanError.Length == 0 && SelectedPair is { Problem.Length: 0 });
        MappingPreview.Text = service is null ? "请选择对端发布的串口。" : UseVirtualPort
            ? $"{(SelectedPair is { } selected ? selected.DisplayName : "待选择本机串口对")} → {service.DeviceId} · {service.TargetDescription}"
            : $"串口调试 → {service.DeviceId} · {service.TargetDescription}";
        ErrorText.Text = _operationError;
    }
    private async void Use_Click(object sender, RoutedEventArgs e) => await (SubmissionTask = SubmitAsync());
    private async Task SubmitAsync()
    {
        if (!UseSerialButton.IsEnabled || SelectedService is not { } service) return;
        var pair = UseVirtualPort ? SelectedPair : null;
        _saving = true; _operationError = string.Empty; UpdateMode();
        try
        {
            if (_submit is null || await _submit(service, pair))
            {
                _saving = false;
                DialogResult = true;
            }
            else _operationError = _submissionError?.Invoke() is { Length: > 0 } error ? error : "未能启用串口，请确认设备连接状态后重试。";
        }
        catch (Exception ex) { _operationError = ex.Message; }
        finally { _saving = false; if (!_closed) UpdateMode(); }
    }
}
