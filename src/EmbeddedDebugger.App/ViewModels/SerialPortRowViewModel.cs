using CommunityToolkit.Mvvm.ComponentModel;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.App.ViewModels;

public sealed class SerialPortRowViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isOpen;
    private bool _isAvailable = true;
    private int _baudRate = 115200;
    private int _customBaudRate;
    private int _dataBits = 8;
    private System.IO.Ports.Parity _parity;
    private System.IO.Ports.StopBits _stopBits = System.IO.Ports.StopBits.One;
    private System.IO.Ports.Handshake _handshake;

    public SerialPortRowViewModel(SerialPortDescriptor descriptor)
    {
        PortName = descriptor.PortName;
        DisplayName = descriptor.DisplayName;
        Description = descriptor.Description ?? "未提供描述";
        Manufacturer = descriptor.Manufacturer ?? "-";
    }

    public string PortName { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string Manufacturer { get; }
    public bool IsRemote { get; init; }
    public bool IsAvailable { get => _isAvailable; set { if (SetProperty(ref _isAvailable, value)) OnPropertyChanged(nameof(SelectionLabel)); } }
    public string SelectionLabel => $"{PortName}  ·  {DisplayName}{(IsAvailable ? string.Empty : " · 离线")}";
    public int DataBits { get => _dataBits; set => SetProperty(ref _dataBits, value); }
    public System.IO.Ports.Parity Parity { get => _parity; set => SetProperty(ref _parity, value); }
    public System.IO.Ports.StopBits StopBits { get => _stopBits; set => SetProperty(ref _stopBits, value); }
    public System.IO.Ports.Handshake Handshake { get => _handshake; set => SetProperty(ref _handshake, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public bool IsOpen
    {
        get => _isOpen;
        set { if (SetProperty(ref _isOpen, value)) { OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(CanConfigure)); } }
    }
    public bool CanConfigure => !IsOpen;
    public string Status => IsOpen ? "已连接" : "未连接";
    public int BaudRate
    {
        get => _baudRate;
        set { if (SetProperty(ref _baudRate, value)) OnPropertyChanged(nameof(EffectiveBaudRate)); }
    }
    public int CustomBaudRate
    {
        get => _customBaudRate;
        set { if (SetProperty(ref _customBaudRate, value)) { OnPropertyChanged(nameof(EffectiveBaudRate)); OnPropertyChanged(nameof(CustomBaudRateText)); } }
    }
    public string CustomBaudRateText
    {
        get => CustomBaudRate > 0 ? CustomBaudRate.ToString() : string.Empty;
        set => CustomBaudRate = string.IsNullOrWhiteSpace(value) ? 0 : int.Parse(value);
    }
    public string EffectiveBaudRate => CustomBaudRate > 0 ? CustomBaudRate.ToString() : BaudRate.ToString();

    public SerialPortSettings ToSettings() => new()
    {
        PortName = PortName,
        BaudRate = CustomBaudRate > 0 ? CustomBaudRate : BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBits,
        Handshake = Handshake
    };
}
