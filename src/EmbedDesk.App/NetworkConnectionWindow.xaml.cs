using System.Net;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.Core.Models;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App;

public partial class NetworkConnectionWindow : Window
{
    private readonly MainWindowViewModel? _workspace;
    private bool _applyingPreset;

    public NetworkConnectionWindow(TransportProtocol protocol = TransportProtocol.Tcp, MainWindowViewModel? workspace = null)
    {
        _workspace = workspace;
        InitializeComponent();
        if (_workspace is null) PresetPanel.Visibility = PresetSavePanel.Visibility = Visibility.Collapsed;
        var addressOptions = NetworkListenAddressOption.GetOptions();
        LocalAddressBox.ItemsSource = addressOptions;
        LocalAddressBox.SelectedItem = LocalAddressBox.Items.Cast<NetworkListenAddressOption>().First(option => option.Address == IPAddress.Any.ToString());
        ClientSourceAddressBox.ItemsSource = GetClientSourceOptions(addressOptions);
        ClientSourceAddressBox.SelectedIndex = 0;
        ProtocolBox.SelectedIndex = protocol switch { TransportProtocol.Udp => 1, TransportProtocol.Mqtt => 2, _ => 0 };
        if (_workspace is not null) PresetBox.ItemsSource = _workspace.GetNetworkPresets();
        OptionsChanged(this, null!);
        WindowTheme.ApplyDarkTitleBar(this);
    }
    public TransportOptions? Options { get; private set; }
    private void ListenAddressChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomLocalAddressBox is null) return;
        CustomLocalAddressBox.Visibility = (LocalAddressBox.SelectedItem as NetworkListenAddressOption)?.IsCustom == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerFields is null || ClientFields is null || HttpBox is null || MqttFields is null) return;
        var tcp = ProtocolBox.SelectedIndex == 0;
        var mqtt = ProtocolBox.SelectedIndex == 2;
        var server = ModeBox.SelectedIndex == 1;
        if (mqtt && server) ModeBox.SelectedIndex = 0;
        server = ModeBox.SelectedIndex == 1;
        ModeBox.IsEnabled = !mqtt;
        ServerFields.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        ClientFields.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
        ClientSourcePanel.Visibility = mqtt ? Visibility.Collapsed : Visibility.Visible;
        ReconnectBox.Visibility = mqtt || tcp && !server ? Visibility.Visible : Visibility.Collapsed;
        HttpBox.Visibility = HttpHint.Visibility = tcp ? Visibility.Visible : Visibility.Collapsed;
        MqttFields.Visibility = mqtt ? Visibility.Visible : Visibility.Collapsed;
        if (mqtt && RemotePortBox.Text == "9000") RemotePortBox.Text = "1883";
        else if (!mqtt && RemotePortBox.Text == "1883") RemotePortBox.Text = "9000";
        if (server && LocalPortBox.Text == "0") LocalPortBox.Text = "9000";
        else if (!server && LocalPortBox.Text == "9000") LocalPortBox.Text = "0";
    }
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (TryBuildOptions(out var options)) { Options = options; DialogResult = true; }
    }

    private bool TryBuildOptions(out TransportOptions options)
    {
        var mqtt = ProtocolBox.SelectedIndex == 2;
        var server = !mqtt && ModeBox.SelectedIndex == 1;
        var listenOption = LocalAddressBox.SelectedItem as NetworkListenAddressOption;
        var localHost = server ? listenOption?.IsCustom == true ? CustomLocalAddressBox.Text.Trim() : listenOption?.Address : IPAddress.Any.ToString();
        if (localHost is null || !IPAddress.TryParse(localHost, out _)) { ErrorText.Text = "请选择监听地址，或输入有效的本机 IP 地址。"; options = new(); return false; }
        if (!int.TryParse(LocalPortBox.Text, out var localPort) || localPort is < 0 or > 65535) { ErrorText.Text = "本地端口范围为 0–65535。"; options = new(); return false; }
        var needsRemote = mqtt || ProtocolBox.SelectedIndex == 1 || ModeBox.SelectedIndex == 0;
        if (needsRemote && string.IsNullOrWhiteSpace(RemoteHostBox.Text)) { ErrorText.Text = "请填写目标地址。"; options = new(); return false; }
        if (!int.TryParse(RemotePortBox.Text, out var remotePort) || remotePort is < 1 or > 65535) { ErrorText.Text = "目标端口范围为 1–65535。"; options = new(); return false; }
        if (mqtt && string.IsNullOrWhiteSpace(MqttTopicBox.Text)) { ErrorText.Text = "请填写 MQTT 主题。"; options = new(); return false; }
        options = new TransportOptions
        {
            Protocol = mqtt ? TransportProtocol.Mqtt : ProtocolBox.SelectedIndex == 0 ? TransportProtocol.Tcp : TransportProtocol.Udp,
            Mode = server ? TransportMode.Server : TransportMode.Client,
            LocalHost = localHost, LocalPort = localPort, RemoteHost = RemoteHostBox.Text.Trim(), RemotePort = remotePort,
            ClientLocalAddress = !server && !mqtt ? (ClientSourceAddressBox.SelectedItem as NetworkListenAddressOption)?.Address : null,
            AutoReconnect = mqtt || ProtocolBox.SelectedIndex == 0 && ModeBox.SelectedIndex == 0 && ReconnectBox.IsChecked == true,
            TreatAsHttp = ProtocolBox.SelectedIndex == 0 && HttpBox.IsChecked == true,
            MqttTopic = MqttTopicBox.Text.Trim(),
            MqttUsername = string.IsNullOrWhiteSpace(MqttUsernameBox.Text) ? null : MqttUsernameBox.Text.Trim(),
            MqttPassword = string.IsNullOrEmpty(MqttPasswordBox.Password) ? null : MqttPasswordBox.Password,
            MqttUseTls = MqttTlsBox.IsChecked == true
        };
        return true;
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingPreset || PresetBox.SelectedItem is not NetworkConnectionPreset preset) return;
        PresetNameBox.Text = preset.Name;
        LoadPreset(preset);
    }

    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not NetworkConnectionPreset preset) return;
        PresetNameBox.Text = preset.Name;
        LoadPreset(preset);
    }

    private void LoadPreset(NetworkConnectionPreset preset)
    {
        _applyingPreset = true;
        try
        {
            ProtocolBox.SelectedIndex = preset.Protocol switch { TransportProtocol.Udp => 1, TransportProtocol.Mqtt => 2, _ => 0 };
            ModeBox.SelectedIndex = preset.Mode == TransportMode.Server ? 1 : 0;
            LocalPortBox.Text = preset.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RemoteHostBox.Text = preset.RemoteHost;
            RemotePortBox.Text = preset.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ReconnectBox.IsChecked = preset.AutoReconnect;
            HttpBox.IsChecked = preset.TreatAsHttp;
            MqttTopicBox.Text = preset.MqttTopic;
            MqttUsernameBox.Text = preset.MqttUsername;
            MqttPasswordBox.Clear();
            MqttTlsBox.IsChecked = preset.MqttUseTls;
            ClientSourceAddressBox.SelectedItem = ClientSourceAddressBox.Items.Cast<NetworkListenAddressOption>()
                .FirstOrDefault(item => item.Address == preset.ClientLocalAddress) ?? ClientSourceAddressBox.Items[0];
            var options = LocalAddressBox.Items.Cast<NetworkListenAddressOption>().FirstOrDefault(item => item.Address == preset.LocalHost);
            if (options is not null) LocalAddressBox.SelectedItem = options;
            else
            {
                LocalAddressBox.SelectedItem = LocalAddressBox.Items.Cast<NetworkListenAddressOption>().FirstOrDefault(item => item.IsCustom);
                CustomLocalAddressBox.Text = preset.LocalHost;
            }
            OptionsChanged(this, null!);
        }
        finally { _applyingPreset = false; }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { ErrorText.Text = "当前窗口没有可用的设置存储。"; return; }
        if (!TryBuildOptions(out var options)) return;
        SavePreset(options, Guid.NewGuid());
    }

    private void UpdatePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { ErrorText.Text = "当前窗口没有可用的设置存储。"; return; }
        if (PresetBox.SelectedItem is not NetworkConnectionPreset selected)
        { ErrorText.Text = "请先选择要更新的预设，或使用“另存为新预设”。"; return; }
        if (!TryBuildOptions(out var options)) return;
        SavePreset(options, selected.Id);
    }

    private void SavePreset(TransportOptions options, Guid id)
    {
        if (_workspace is not { } workspace) return;
        try
        {
            workspace.SaveNetworkPreset(new NetworkConnectionPreset
            {
                Id = id,
                Name = PresetNameBox.Text,
                Protocol = options.Protocol,
                Mode = options.Mode,
                LocalHost = options.LocalHost,
                LocalPort = options.LocalPort,
                ClientLocalAddress = options.ClientLocalAddress ?? string.Empty,
                RemoteHost = options.RemoteHost,
                RemotePort = options.RemotePort,
                AutoReconnect = options.AutoReconnect,
                TreatAsHttp = options.TreatAsHttp,
                MqttTopic = options.MqttTopic,
                MqttUsername = options.MqttUsername ?? string.Empty,
                MqttUseTls = options.MqttUseTls
            });
            RefreshPresets(id);
            ErrorText.Text = "预设已保存；密码和发送内容不会保存。";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { ErrorText.Text = exception.Message; }
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || PresetBox.SelectedItem is not NetworkConnectionPreset selected) return;
        if (MessageBox.Show(this, $"确定删除网络预设“{selected.Name}”吗？", "删除预设", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _workspace.DeleteNetworkPreset(selected.Id);
        RefreshPresets(null);
        ErrorText.Text = "预设已删除。";
    }

    private void RefreshPresets(Guid? selectedId)
    {
        if (_workspace is null) return;
        _applyingPreset = true;
        try
        {
            PresetBox.ItemsSource = _workspace.GetNetworkPresets();
            PresetBox.SelectedItem = selectedId is { } id
                ? PresetBox.Items.Cast<NetworkConnectionPreset>().FirstOrDefault(preset => preset.Id == id)
                : null;
        }
        finally { _applyingPreset = false; }
    }

    private static IReadOnlyList<NetworkListenAddressOption> GetClientSourceOptions(IReadOnlyList<NetworkListenAddressOption> addresses) =>
    [
        new("自动选择（系统路由）", string.Empty),
        .. addresses.Where(option => !option.IsCustom && option.Address is not ("0.0.0.0" or "::"))
    ];
}
