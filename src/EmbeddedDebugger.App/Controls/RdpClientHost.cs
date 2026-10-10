using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace EmbeddedDebugger.App.Controls;

// The registered Windows MSTSC ActiveX control is loaded at runtime. No generated
// COM interop DLL or separately installed RDP package is required for publishing.
public sealed class RdpClientHost : Forms.AxHost
{
    private ConnectionPointCookie? _cookie;
    private RdpEventSink? _sink;
    private bool _loggedIn;
    // IMsRdpClient9 supports dynamic desktop resizing and works on Windows 10+.
    // Newer control registrations can fail before transport negotiation on some
    // Windows builds, so use this broadly deployed interface deliberately.
    public RdpClientHost() : base("8B918B82-7985-4C24-89DF-C33AD2BBFBCD") { }
    public dynamic Client => GetOcx();
    public event Action<string, string>? StateChanged;
    protected override void CreateSink()
    {
        base.CreateSink();
        _sink = new RdpEventSink(this);
        _cookie = new ConnectionPointCookie(GetOcx(), _sink, typeof(IRdpEvents));
    }
    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
        _sink = null;
        base.DetachSink();
    }
    internal void Report(string state, string text)
    {
        if (state == "已连接") _loggedIn = true;
        StateChanged?.Invoke(state, text);
    }
    internal void Disconnected(int reason)
    {
        string description;
        try { description = Client.GetErrorDescription((uint)reason, (uint)Client.ExtendedDisconnectReason); }
        catch (COMException) { description = $"断开代码：{reason}"; }
        Report(_loggedIn ? "已断开" : "连接失败", $"{description.Trim()}（RDP {reason}）");
    }
}

[ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IRdpEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int reason);
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warning);
    [DispId(18)] void OnAuthenticationWarningDisplayed();
    [DispId(19)] void OnAuthenticationWarningDismissed();
    [DispId(22)] void OnLogonError(int errorCode);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEventSink(RdpClientHost owner) : StandardOleMarshalObject, IRdpEvents
{
    public void OnConnecting() => owner.Report("连接中", "正在建立 RDP 连接…");
    public void OnConnected() => owner.Report("连接中", "安全通道已建立，正在登录 Windows…");
    public void OnLoginComplete() => owner.Report("已连接", "Windows 远程桌面已连接");
    public void OnDisconnected(int reason) => owner.Disconnected(reason);
    public void OnFatalError(int errorCode) => owner.Report("连接失败", $"远程桌面错误：{errorCode}");
    public void OnWarning(int warning) { }
    public void OnAuthenticationWarningDisplayed() => owner.Report("连接中", "请在 Windows 远程桌面提示中确认主机身份。");
    public void OnAuthenticationWarningDismissed() => owner.Report("连接中", "正在完成身份验证…");
    public void OnLogonError(int errorCode) => owner.Report("连接中", $"Windows 登录提示：{errorCode}");
}
