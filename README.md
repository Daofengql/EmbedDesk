# EmbedDesk

面向嵌入式联调的 Windows 桌面工作台，使用 .NET 8、WPF、MVVM、WebView2 与 xterm.js，集成设备连接、通信调试、远程终端、网络诊断和嵌入式工具。

**[中文文档站](https://daofengql.github.io/EmbedDesk/) · [下载 EXE](https://github.com/Daofengql/EmbedDesk/releases) · [安装与快速开始](docs/guide/getting-started.md) · [常见问题](docs/guide/faq.md)**

## 开始使用

默认发行版为 Windows x64 精简单 EXE，需要 Windows 10 1809 或更新版本、**.NET 8 Desktop Runtime x64** 和 **WebView2 Runtime**。启动时会申请管理员权限，终端、图标和许可已嵌入，无需旁置 Assets 文件夹。

首次使用可以跟随[本机 TCP 收发示例](docs/guide/getting-started.md#发送第一条报文)，不接开发板即可完成第一条报文收发。全部 Markdown 也可以直接在仓库阅读。

| 工作区 | 当前能力与指南 |
| --- | --- |
| 连接中心 | [设备 ID 授权、串口共享、TCP/UDP 映射、自动局域网直连与中继回退](docs/guide/connection-center.md) |
| 调试助手 | [一排独立会话标签](docs/guide/debug-assistant.md)，包含[串口](docs/guide/serial.md)、[TCP/UDP 与 MQTT](docs/guide/network.md)、[PID 与摄像头](docs/guide/pid-camera.md) |
| 远程调试 | [本地 Shell、SSH / Telnet、RDP / VNC、SFTP 文件与 S3 对象浏览](docs/guide/remote.md) |
| 网络工具 | [DNS / DoH、CIDR、TLS、Hosts、Ping / 路由、端口扫描、NAT / UPnP 与 iperf3](docs/guide/network-tools.md)，以及[局域网文件](docs/guide/lan-files.md) |
| 嵌入式工具 | [ESP / STM32 / STC89 / CH347T、原始镜像、二进制编辑、校验 / 编解码与录制回放](docs/guide/embedded-tools.md) |

连接中心内置「免费中继 1」，新安装默认使用，也可[自行部署或配置可信中继](server/README.md)。内置节点使用 HTTPS / WSS 地址，由反向代理提供 TLS。每个节点统一维护公共准入 Token，仅用于简单过滤扫描，可通过连接代码导入。Go 服务内嵌 React / Ant Design 控制台，Linux 单二进制部署，支持 MySQL / Redis 和可选 SQLite、三级用户权限及图片验证码注册。用户输入设备 ID 绑定无主设备后，同账号设备共享总带宽；匿名设备每台使用节点默认值。

应用内远程串口无需驱动，系统虚拟 COM 是需预装 com0com 的实验性功能。局域网文件只用于可信 IPv4 局域网，文件与访问码不加密。硬件、协议和真实互通边界见[技术参考](docs/reference/index.md)；候选工作见[产品方向](docs/ProductRoadmap.md)，不代表当前支持。

## 开发与验证

Windows 下安装 .NET 8 SDK 与 WebView2，从仓库根目录执行：

```powershell
dotnet build EmbedDesk.sln
dotnet test tests/EmbedDesk.Core.Tests/EmbedDesk.Core.Tests.csproj
dotnet build tests/EmbedDesk.App.SmokeTests/EmbedDesk.App.SmokeTests.csproj
dotnet run --project tests/EmbedDesk.App.SmokeTests -- --debug-connections-check
```

Smoke 项目需单独构建，桌面检查需要交互式 Windows 桌面。按改动范围选择入口，详见[构建与验证](docs/development/index.md)和[协作约定](AGENTS.md)。[架构与代码导航](docs/Architecture.md)说明分层与生命周期；固件联调可直接查阅[公共帧](docs/reference/debug-protocol.md)、[PID 命令](docs/reference/pid-protocol.md)和[摄像头协议](docs/reference/camera-protocol.md)。

## 发布

```powershell
dotnet publish src/EmbedDesk.App/EmbedDesk.App.csproj -c Release
```

默认成品为 `artifacts/release/win-x64/EmbedDesk.App.exe`，不带 .NET 运行时；自包含选项、依赖嵌入与隔离单文件验证见[发布说明](docs/development/releasing.md)。[Release 工作流](.github/workflows/release.yml)在推送版本标签后生成 EXE、SHA-256 和许可，不自动创建标签。

## 文档站

图文使用指南、协议与开发说明使用 VitePress，带中文本地搜索、深浅主题、手机目录和 Mermaid 图表。文档开发依赖不加入桌面 EXE：

```powershell
npm ci
npm run docs:dev
npm run docs:build
```

[Pages 工作流](.github/workflows/docs.yml)在 main 文档更新后部署，PR 只构建。首次启用与维护方法见[文档维护](docs/development/docs-site.md)。

## 许可证

原创代码采用 **GPL-3.0-only**，允许商业使用和收费分发。分发修改版须遵守 GPL、保留许可并提供对应源码，见 [LICENSE](LICENSE)。第三方依赖、移植实现及资源保留各自的许可、版本和来源，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
