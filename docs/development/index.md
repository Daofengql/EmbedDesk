# 构建与验证

桌面应用使用 .NET 8、WPF 和 MVVM。在 Windows 10 1809 或更新的 x64 系统安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。应用要求管理员权限，开发运行应从已提权的终端启动。

文档站使用独立的 Node.js 开发工具链，见[文档维护](docs-site.md)；运行或构建桌面应用不需要 Node.js。

## 代码入口

| 目录 | 内容 |
| --- | --- |
| `src/EmbedDesk.Core/` | 模型与接口 |
| `src/EmbedDesk.Protocols/` | 桥接帧与安全通道原语 |
| `src/EmbedDesk.Infrastructure/` | 串口、网络、桥接、远程、烧录与录制实现 |
| `src/EmbedDesk.App/` | WPF、MVVM、终端与嵌入资源 |
| `server/relay/` | 独立 Go 中继及第三方许可 |
| `tests/EmbedDesk.Core.Tests/` | 核心测试 |
| `tests/EmbedDesk.App.SmokeTests/` | 桌面与协议回归 |

进一步查阅[应用架构](../Architecture.md)与[协作约定](../../AGENTS.md)。

## 基础构建

从仓库根目录执行：

```powershell
dotnet build EmbedDesk.sln
dotnet test tests/EmbedDesk.Core.Tests/EmbedDesk.Core.Tests.csproj
dotnet build tests/EmbedDesk.App.SmokeTests/EmbedDesk.App.SmokeTests.csproj
dotnet run --project tests/EmbedDesk.App.SmokeTests -- --debug-connections-check
```

Smoke 项目没有包含在解决方案中。源码修改后先单独构建，再运行对应检查，避免使用旧程序集。桌面检查需要可交互的 Windows 桌面，会打开测试窗口；不要为测试结束正在使用的程序或连接。

## 按范围选择回归

每次只选择一个参数，不传参数为终端综合回归。完整入口见 [Program.cs](../../tests/EmbedDesk.App.SmokeTests/Program.cs)。

| 范围 | 常用参数 |
| --- | --- |
| 调试空状态、内部表单、网络角色、UDP 与接收上限 | `--debug-connections-check` |
| PID、摄像头、二进制与混合调试标签 | `--debug-tools-check` |
| 串口标签与连接布局 | `--serial-tabs-check`、`--connection-layout-check` |
| 远程标签、本地 Shell 与目录 | `--remote-tabs-check`、`--remote-empty-check`、`--shell-directory-check` |
| RDP / VNC / 网络 | `--desktop-network-check` |
| 设备连接与共享 | `--device-connections-check`、`--device-security-check`、`--virtual-serial-check` |
| 连接中心、设置与托盘截图 | `--connection-center-window-check` |
| 局域网文件与设置 | `--lan-files-check`、`--app-settings-check` |
| 质量与网络工具 | `--network-quality-check`、`--network-tools-check`、`--dns-query-check`、`--utility-tools-check` |
| Hosts 与工具窗口 | `--hosts-check`、`--hosts-window-check` |
| iperf3 | `--iperf3-check` |
| 固件协议模拟 | `--firmware-check`、`--esp-check`、`--ch347-check` |
| 录制与镜像 | `--recording-media-check` |
| 资源策略与旧桥接兼容 | `--performance-check`、`--serial-bridge-check` |
| 发布后的单 EXE | `--packaging-check`，执行前看[发布验证](releasing.md#验证单文件) |

## 中继构建与验证

设备连接和 TLS 检查前先构建中继；Go 版本以 [go.mod](../../server/relay/go.mod) 为准。

```powershell
Push-Location server/relay
try {
    go test ./...
    go vet ./...
    go build -o ../../artifacts/relay-test.exe .
} finally {
    Pop-Location
}
```

检查使用本机临时数据库、TLS 夹具和模拟 COM。部署步骤与真实边界见[中继 README](../../server/relay/README.md)。

## 记录验证结果

界面修改需要检查默认及最小尺寸的实际截图。单文件必须发布后在隔离目录检查，普通 build 不能代替。报告区分编译、自动回归、截图、真实对端互通和硬件实测；既有分析器警告存在时不要声称零警告。

`--iperf3-peer-check HOST PORT`、`--live-doh-check`、`--live-dns-check`、`--live-nat-check` 会产生外部流量，不属于默认本机回归。模拟与回环不能代替开发板、Flash、物理磁盘或公网部署验收。待验收事项见[产品方向](../ProductRoadmap.md)。
