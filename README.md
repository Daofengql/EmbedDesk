# EmbedDesk

面向嵌入式联调的 Windows 桌面工作台，使用 .NET 8、WPF、MVVM、WebView2 与 xterm.js，集成串口、远程终端 / 桌面、网络诊断和嵌入式工具。

本文集中说明功能、使用边界、开发与发布；实现细节见 [架构](docs/Architecture.md)，后续工作见 [产品方向](docs/ProductRoadmap.md)。

## 主要功能

| 工作区 | 当前能力 |
| --- | --- |
| 连接中心 | 设备 ID、动态 / 静态密码或人工授权、自动局域网直连 / 中继回退、COM 共享、TCP / UDP 端口映射 |
| 串口调试 | 设备描述与插拔刷新、通信参数、文本 / HEX、Shell、多标签、循环 / 队列发送及录制 |
| 远程调试 | 多开本地 PowerShell / CMD、SSH / Telnet、RDP / VNC、SFTP 文件工作区与文本 / 图片预览、S3 对象浏览器 |
| 网络调试 | TCP / UDP / HTTP 客户端和服务端、MQTT 客户端、指定网卡 / 对端、独立收发与发送任务 |
| 网络工具 | DNS / DoH、CIDR、TLS / 证书 / 密码、Hosts、本机信息、Ping / 路由、端口扫描、内网发现、NAT / UPnP、iperf3、局域网文件 |
| 嵌入式工具 | ESP UART ROM、STM32 USART / 探针拖拽盘、原始 STC89 白名单、CH347T HID、SD / USB 原始镜像、数据与调试辅助、校验 / 编解码、录制回放 |

会话独立运行，切换标签不停止通信，支持拖动排序和关闭确认。收起到托盘保留连接，真正退出等待资源清理。本机配置与受保护凭据的保存范围见 [架构说明](docs/Architecture.md#配置与生命周期)。

## 使用边界

- 连接中心需[自部署 Go 中继](server/relay/README.md)并在设置填写地址，当前没有内置公共节点。中继只监听 HTTP，公网 HTTPS 由反向代理提供；设备 ID 查询与授权仍依赖中继。
- 应用内远程串口无需驱动；[系统虚拟 COM](docs/VirtualSerialPorts.md)是需要预装 com0com 的实验性功能。共享只做应用内软件预留，不预打开硬件，也不阻止其他程序占用物理 COM。
- [端口映射](docs/DeviceConnections.md)限定为连接方本机回环端口到对端已发布服务；设备授权、实际 COM 和映射监听不会随重启恢复。
- [局域网文件](docs/LanFiles.md)以 TCP HTTP 传输正文、UDP 发现，支持子目录浏览、剪贴板连接信息、多文件 / 分块并行上传、右下角悬浮传输队列、实时速率 / 剩余时间和后台保留。使用持久化断点及逐块 SHA-256，无整文件扫描或固定大小上限。仅用于可信 IPv4 局域网，文件与访问码不加密，不建立系统目录映射。
- 本地和 SSH 终端支持交互式 TUI、复制粘贴、字号与全屏，并同步实际 PTY 行列数。SSH 底部文件面板默认折叠，可拖出再合并；文本 / 图片预览默认上限 5 MiB。
- RDP 使用系统 MSTSC ActiveX；VNC 支持 RFB 3.3 / 3.7 / 3.8、标准密码或无认证及 Raw / CopyRect / DesktopSize，无 TLS / VeNCrypt，密码只使用前 8 个 Latin-1 字符。S3 支持 HTTPS 端点、浏览、上传下载、删除及分段上传，尚无真实服务验收，不含 ACL、预签名分享或断点续传。
- 网络调试的 HTTP 为 TCP 报文诊断。连接质量探测失败不改变会话状态；默认 TCPing 会额外建立短连接，可切换 ICMP 或关闭。

| 工具 | 支持范围与限制 |
| --- | --- |
| DNS / DoH、TLS | 内置 UDP / TCP / DoH，UDP 截断转 TCP，正常验证 DoH TLS；不依赖 nslookup，不支持 AXFR / IXFR。TLS 检查仅支持直接 TLS；证书与算法见 [专项说明](docs/security-crypto.md) |
| Hosts、扫描与 NAT | Hosts 只匹配完整域名，应用前备份并检测并发修改，不提供 DNS 代理；端口扫描最多 1,024 个端口、只列开放项；NAT 判定依赖多项观测证据 |
| iperf3 | 标准 TCP / UDP 单流、正反向、按时长测试（1–3,600 秒），范围为 `-P 1`、`-O 0`；UDP 仍需 TCP 控制连接。无多流、双向同时、SCTP、认证或 CPU / 重传统计，详见 [协议与历史实测](docs/third-party/iperf3-protocol.md) |
| ESP / STM32 / STC | ESP8266、ESP32 / S2 / S3 / C2 / C3 / C6 / H2 UART ROM；STM32 AN3155 USART；仅原始 STC89 白名单，块校验不等于 Flash 回读。探针入口仅为 USB 拖拽盘；ESP 分区备份不是文件系统 / NVS 编辑 |
| CH347T | Windows 原生 HID，明确支持的 NOR / NAND 芯片见 [芯片表](docs/ch347-spi-support.md)；NAND 保留物理布局，不自动重映射坏块 |
| SD / USB 镜像 | 原始写盘、回读校验与 FAT32；FAT32 范围为 256 MiB–2 TiB、512 B 扇区，不转换 Windows 安装 ISO |
| 录制回放 | `.edbgtrace` 在显示截断前记录原始 RX / TX，上限 64 MiB / 100,000 条；0.25–8× 回放，实际串口重发只发送 TX，并与其他发送任务互斥 |
| 数据与调试辅助 | 摄像头兼容协议 / JPEG 接收、五通道 PID 曲线与可选平滑滤波、连续 HEX / ASCII 二进制编辑；详见 [专项说明](docs/DebugTools.md) |

烧录前检查型号、容量、镜像范围与命令能力，未知型号拒绝破坏性写入；取消不能回滚已擦写数据。协议模拟和文件流测试不能替代开发板、Flash 或物理磁盘验收。iperf3 公共节点保留公开来源及历史验证日期，不保证当前可用；内置服务端尚需标准 CLI 互通验收。

## 开发与验证

在 Windows 10 1809 或更新版本上安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 与 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。命令从仓库根目录执行；应用 EXE 使用 `requireAdministrator`，开发运行应从已提权终端启动。

```powershell
dotnet build EmbedDesk.sln
dotnet test tests/EmbedDesk.Core.Tests/EmbedDesk.Core.Tests.csproj
dotnet build tests/EmbedDesk.App.SmokeTests/EmbedDesk.App.SmokeTests.csproj
dotnet run --project tests/EmbedDesk.App.SmokeTests -- --iperf3-check
```

烟雾项目不在解决方案内，修改后须单独构建；桌面检查需要可交互的 Windows 桌面。每次只选一个参数，不传参数为终端综合回归。完整入口见 [Program.cs](tests/EmbedDesk.App.SmokeTests/Program.cs)，选择原则见 [AGENTS.md](AGENTS.md)。

常用专项为 `--device-connections-check`、`--device-security-check`、`--virtual-serial-check`、`--lan-files-check`、`--app-settings-check`、`--connection-center-window-check`、`--firmware-check`、`--recording-media-check` 和 `--performance-check`。设备连接 / TLS 检查前先构建中继：

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

Go 版本以 [go.mod](server/relay/go.mod) 为准；部署见 [服务端说明](server/relay/README.md)。`--iperf3-peer-check HOST PORT`、`--live-doh-check`、`--live-dns-check`、`--live-nat-check` 会产生外部流量，仅在需要真实对端验证时主动执行。报告应区分编译、自动回归、截图检查、真实互通与硬件实测，不把测试入口或历史记录当作本次通过。

## 发布

默认 `Compact` 为不携带 .NET 运行时的 Windows x64 单 EXE，目标系统需 **.NET 8 Desktop Runtime（x64）** 和 WebView2 Runtime：

```powershell
dotnet publish src/EmbedDesk.App/EmbedDesk.App.csproj -c Release
```

成品为 `artifacts/release/win-x64/EmbedDesk.App.exe`，终端资源、图标及许可已嵌入，无需旁置 `Assets`。若成品正在运行，使用新的输出目录。明确需要自带运行时才追加 `-p:PublishProfile=SingleFile`，输出至 `artifacts/release-self-contained/win-x64/`，仍需 WebView2。

两种配置均不裁剪；SDK 单文件压缩仅用于自包含版本。Release 另通过 [PackedDependencies.targets](build/PackedDependencies.targets) 压缩嵌入完整 BouncyCastle、按需加载并复用版本缓存；排查加载问题可追加 `-p:PackManagedDependencies=false`。

单文件检查须发布烟雾程序，再将其 EXE 单独复制到新建隔离目录执行：

```powershell
dotnet publish tests/EmbedDesk.App.SmokeTests/EmbedDesk.App.SmokeTests.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o artifacts/smoke-publish
# 在只含烟雾 EXE 的隔离目录中运行：
./EmbedDesk.App.SmokeTests.exe --packaging-check
```

同时检查实际产品 EXE 启动与终端初始化；普通 build 不能代替发布验收。[GitHub Release 工作流](.github/workflows/release.yml) 由 `vMAJOR.MINOR.PATCH` 标签（可带预发布后缀）触发，发布精简 EXE、SHA-256 和许可；不会创建或推送标签，也不包含桌面、Go 或硬件验收。

## 许可证

原创代码采用 **GPL-3.0-only**，允许商业使用和收费分发。对外分发修改版需遵守 GPL、保留许可并提供对应源码，见 [LICENSE](LICENSE)。第三方依赖、移植实现及资源保留各自的许可与来源，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
