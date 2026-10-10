# Embedded Debugger

面向嵌入式联调的 Windows 桌面工作台，集成串口调试、SSH / RDP / VNC 远程管理和网络调试。使用 .NET 8、WPF、WebView2 与 xterm.js，提供统一的暗色界面。

## 功能

- **串口调试**：设备扫描与描述、波特率、数据位 / 校验位 / 停止位和流控设置；分开的收发区与 Shell 模式；HEX 收发、循环发送和队列发送。
- **网络串口服务**：在独立窗口配置客户端 / 服务端和多路串口映射；远端串口可直接出现在调试列表中。创建系统级虚拟 COM 口需另行配置外部驱动及映射工具。
- **本地与 SSH 终端**：本地 PowerShell / CMD、SSH 密码或私钥登录、密钥管理、多标签会话、连接握手日志和主机系统图标。终端支持彩色输出、TUI、复制粘贴、字号调整和独立全屏，窗口及字号变化会同步到 PTY。
- **RDP / VNC 桌面**：在远程会话列表中新增连接，双击后直接嵌入现有标签页，支持切换、排序、重连、关闭确认和全屏。RDP 使用 Windows 原生 MSTSC ActiveX 控件，支持 NLA、系统证书提示、剪贴板重定向和窗口尺寸同步；旧版服务端回退为画面缩放。VNC 支持标准密码认证、桌面显示、键鼠输入和发送 Latin-1 剪贴板文本。
- **文件工作区**：底部本地 / 远程目录、上传下载和右键操作；面板默认收起，可拖出为独立窗口并合并回来。小文件可双击下载到缓存，在标签页预览图片或编辑文本并保存到远端；当前默认预览上限为 5 MiB。
- **远端状态**：Linux CPU、内存、负载、网络计数和运行时间；CPU、内存和负载以进度条显示。
- **网络质量**：SSH / Telnet、RDP / VNC 和 TCP 客户端在连接成功后，每 10 秒通过 TCPing 测量目标服务端口的握手耗时；状态栏显示延迟和信号条，悬停查看最近 20 次探测的平均值、峰值、抖动和未成功比例。右键可切换 ICMP Ping 或关闭探测。UDP 使用 ICMP；TCP 服务端仅在有一个对端时探测该主机，UDP 服务端跟随最近对端。探测失败不改变会话连接状态。
- **网络调试**：统一的暗色工作区，可收起和调整宽度的会话列表、独立收发区、常驻发送侧栏和流量计数。支持 TCP / UDP 客户端和服务端、HEX、循环发送、发送队列、自动重连及 HTTP 接收片段诊断；发送内容和任务按会话独立保存于内存。TCP 服务端向所有客户端广播，UDP 服务端向最近的对端回发数据。

## 开发运行

需要 Windows 10 1809 或更新版本、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。本地终端使用 Windows ConPTY；SSH 文件管理需要远端启用 SFTP。

```powershell
dotnet restore .\EmbeddedDebugger.sln
dotnet build .\EmbeddedDebugger.sln
dotnet run --project .\src\EmbeddedDebugger.App\EmbeddedDebugger.App.csproj
```

会话配置和密钥路径索引保存在 `%LOCALAPPDATA%\EmbeddedDebugger`；会话密码不写入保存的配置文件。WebView2 和终端资源缓存也保存在用户目录中。

RDP 需要系统提供 MSTSC ActiveX 控件，服务端启用远程桌面。VNC 首版实现 RFB 3.3 / 3.7 / 3.8、无认证或标准 VNC 密码认证，以及 Raw / CopyRect / DesktopSize；暂不支持 VeNCrypt、TLS 和厂商专用认证。标准 VNC 密码仅使用前 8 个 Latin-1 字符，图像流没有传输加密，适合在可信网络或 VPN 内使用。

TCPing 会额外建立并立即关闭一个 TCP 连接，不发送业务数据、不登录；服务端可能记录这些连接。对只允许单连接或对连接次数敏感的设备，可在延迟指示器右键菜单中改用 ICMP 或关闭探测。TCP 握手耗时、ICMP 往返耗时均不代表当前会话的应用响应时间；探测未成功比例也不等同于业务数据包丢包率。

## 发布单 EXE

默认发布为精简的 Windows x64 单 EXE，不携带 .NET 运行时。假定目标系统已安装 **.NET 8 Desktop Runtime（x64）** 和 WebView2 Runtime：

```powershell
dotnet publish .\src\EmbeddedDebugger.App\EmbeddedDebugger.App.csproj -c Release
```

成品为 `artifacts\release\win-x64\EmbeddedDebugger.App.exe`。终端 HTML、JavaScript、样式及其许可文件已嵌入程序，运行时会解包到用户缓存，无需在 EXE 旁放置 `Assets` 目录。发布配置关闭裁剪以兼容 WPF、WebView2 和 SSH。

如果需要向尚未安装 .NET 运行时的机器交付，可显式生成自带运行时的较大版本：

```powershell
dotnet publish .\src\EmbeddedDebugger.App\EmbeddedDebugger.App.csproj -c Release -p:PublishProfile=SingleFile
```

自带运行时版本输出至 `artifacts\release-self-contained\win-x64\EmbeddedDebugger.App.exe`，仍需系统安装 WebView2 Runtime。开发编译使用 `dotnet build`，正式单 EXE 交付使用 `dotnet publish`。

## 测试

核心协议测试：

```powershell
dotnet test .\tests\EmbeddedDebugger.Core.Tests\EmbeddedDebugger.Core.Tests.csproj
```

Windows 桌面烟雾测试覆盖终端、侧栏、文件工作区、全屏及会话行为；运行过程中会打开测试窗口并模拟输入，需要可交互的桌面：

```powershell
dotnet run --project .\tests\EmbeddedDebugger.App.SmokeTests\EmbeddedDebugger.App.SmokeTests.csproj
```

真实 SSH 回环测试入口为 `tests/EmbeddedDebugger.App.SmokeTests/loopback_ssh.py`，需要 Python、Paramiko 和 Git Bash。它使用临时主机密钥及固定的本机测试账号，不读取实际会话凭据。

新增桌面与网络测试通过本机回环验证 TCP / UDP、HEX 和发送任务、原生 RDP 握手与断开，以及 VNC 认证、图像、尺寸变化和输入，不需要外部服务器：

```powershell
dotnet run --project .\tests\EmbeddedDebugger.App.SmokeTests\EmbeddedDebugger.App.SmokeTests.csproj -- --desktop-network-check
```

RDP 完整 Windows 登录、证书交互和不同 VNC 服务端的兼容性仍需使用实际目标机器验证。

网络质量测试覆盖本机 TCP / ICMP 实测、关闭端口、取消与过期结果、滚动统计、探测方式切换，以及各协议的目标选择和生命周期：

```powershell
dotnet run --project .\tests\EmbeddedDebugger.App.SmokeTests\EmbeddedDebugger.App.SmokeTests.csproj -- --network-quality-check
```

## 项目结构

```text
src/
  EmbeddedDebugger.Core/            模型与接口
  EmbeddedDebugger.Protocols/       EDBG 多路复用帧编解码
  EmbeddedDebugger.Infrastructure/  网络、串口、桥接、SSH 与 SFTP
  EmbeddedDebugger.App/             WPF 界面、本地终端与 MVVM
tests/
  EmbeddedDebugger.Core.Tests/      核心协议测试
  EmbeddedDebugger.App.SmokeTests/   桌面、终端与 SSH 回归测试
docs/                              架构与串口桥接协议
scripts/                           图标资源生成脚本
```

协议说明见 [SerialBridgeProtocol.md](docs/SerialBridgeProtocol.md)，架构说明见 [Architecture.md](docs/Architecture.md)。

## 许可证

本项目原创代码采用 **GNU GPL v3.0 only（SPDX: `GPL-3.0-only`）**，完整条款见 [LICENSE](LICENSE)。

允许商业使用和收费分发。对外分发修改版时，必须遵守 GPL-3.0，保留相同许可并提供对应源码；不能把修改后的本体作为闭源软件分发。仅通过网络提供服务不会自动触发 GPL 的源码提供义务；独立在线服务可以闭源并收费。

第三方组件及系统图标保留各自许可证，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
