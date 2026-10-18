using System.Globalization;
using System.Windows;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Connections;

namespace EmbedDesk.App;

public partial class RemotePortMappingWindow : Window
{
    private readonly Func<DeviceRemoteService, int, Task<bool>> _submit;
    private readonly Func<string> _submissionError;
    private bool _initialized, _saving, _closed;
    private string _operationError = string.Empty;
    public DeviceRemoteService? SelectedService => ServiceBox.SelectedItem as DeviceRemoteService;
    internal Task SubmissionTask { get; private set; } = Task.CompletedTask;

    public RemotePortMappingWindow(IEnumerable<DeviceRemoteService> services, ConnectionCenterViewModel model)
        : this(services, model.MapAsync, () => model.Error) { }
    internal RemotePortMappingWindow(IEnumerable<DeviceRemoteService> services, Func<DeviceRemoteService, int, Task<bool>> submit, Func<string> submissionError)
    {
        InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this);
        _submit = submit; _submissionError = submissionError;
        var available = services.Where(service => service.Service.Kind is (SharedServiceKind.Tcp or SharedServiceKind.Udp) && service.Service.Enabled).ToArray();
        ServiceBox.ItemsSource = available; ServiceBox.SelectedItem = available.FirstOrDefault();
        _initialized = true;
        Closing += (_, args) => { if (_saving) args.Cancel = true; };
        Closed += (_, _) => _closed = true;
        UpdateForm();
    }
    private bool TryGetPort(out int port) => int.TryParse(LocalPort.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
    private void Service_Changed(object sender, RoutedEventArgs e) { if (_initialized) { _operationError = string.Empty; UpdateForm(); } }
    private void Port_Changed(object sender, RoutedEventArgs e) { if (_initialized) { _operationError = string.Empty; UpdateForm(); } }
    private void UpdateForm()
    {
        var service = SelectedService;
        var valid = TryGetPort(out var port);
        EmptyServicesHint.Visibility = service is null ? Visibility.Visible : Visibility.Collapsed;
        ProtocolLabel.Text = service is null ? "传输协议：随目标确定" : $"传输协议：{service.Service.ProtocolLabel}（随目标固定）";
        ValidationText.Text = valid ? string.Empty : "请输入 1 至 65535 的本机监听端口。";
        MappingPreview.Text = service is null ? "请选择对端发布的目标。" : $"{service.Service.ProtocolLabel} · 127.0.0.1:{(valid ? port.ToString(CultureInfo.InvariantCulture) : "待填端口")} → {service.DeviceId} · {service.TargetDescription}";
        ServiceBox.IsEnabled = !_saving && ServiceBox.HasItems;
        LocalPort.IsEnabled = !_saving && service is not null;
        CancelButton.IsEnabled = !_saving;
        SavePortButton.IsEnabled = !_saving && service is not null && valid;
        SavePortButton.Content = _saving ? "正在启用…" : "保存并启用映射";
        ErrorText.Text = _operationError;
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await (SubmissionTask = SubmitAsync());
    private async Task SubmitAsync()
    {
        if (!SavePortButton.IsEnabled || SelectedService is not { } service || !TryGetPort(out var port)) return;
        _saving = true; _operationError = string.Empty; UpdateForm();
        try
        {
            if (await _submit(service, port))
            {
                _saving = false;
                DialogResult = true;
            }
            else _operationError = _submissionError() is { Length: > 0 } error ? error : "未能启用映射，请确认设备连接状态后重试。";
        }
        catch (Exception ex) { _operationError = ex.Message; }
        finally { _saving = false; if (!_closed) UpdateForm(); }
    }
}
