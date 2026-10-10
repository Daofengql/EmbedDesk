# Embedded Debugger

面向嵌入式联调的 Windows 桌面工作台，集成串口调试、SSH 远程管理和网络调试。使用 .NET 8、WPF、WebView2 与 xterm.js，提供统一的暗色界面。

## 功能

- **串口调试**：设备扫描与描述、波特率、数据位 / 校验位 / 停止位和流控设置；分开的收发区与 Shell 模式；HEX 收发、循环发送和队列发送。
- **网络串口服务**：在独立窗口配置客户端 / 服务端和多路串口映射；远端串口可直接出现在调试列表中。创建系统级虚拟 COM 口需另行配置外部驱动及映射工具。
- **本地与 SSH 终端**：本地 PowerShell / CMD、SSH 密码或私钥登录、密钥管理、多标签会话、连接握手日志和主机系统图标。终端支持彩色输出、TUI、复制粘贴、字号调整和独立全屏，窗口及字号变化会同步到 PTY。
- **文件工作区**：底部本地 / 远程目录、上传下载和右键操作；面板默认收起，可拖出为独立窗口并合并回来。小文件可双击下载到缓存，在标签页预览图片或编辑文本并保存到远端；当前默认预览上限为 5 MiB。
- **远端状态**：Linux CPU、内存、负载、网络计数和运行时间；CPU、内存和负载以进度条显示。
- **网络调试**：TCP / UDP 客户端和服务端、多会话、自动重连、定时发送及 HTTP 报文诊断。TCP 服务端支持多个客户端，UDP 服务端可向最近的远端回发数据。

## 开发运行

需要 Windows 10 1809 或更新版本、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。本地终端使用 Windows ConPTY；SSH 文件管理需要远端启用 SFTP。

```powershell
dotnet restore .\EmbeddedDebugger.sln
dotnet build .\EmbeddedDebugger.sln
dotnet run --project .\src\EmbeddedDebugger.App\EmbeddedDebugger.App.csproj
```

会话配置和密钥路径索引保存在 `%LOCALAPPDATA%\EmbeddedDebugger`；会话密码不写入保存的配置文件。WebView2 和终端资源缓存也保存在用户目录中。

## 发布单 EXE

默认发布为 Windows x64 单 EXE，自带 .NET 8 Desktop Runtime；目标系统仍需安装 WebView2 Runtime：

```powershell
dotnet publish .\src\EmbeddedDebugger.App\EmbeddedDebugger.App.csproj -c Release
```

成品为 `artifacts\release\win-x64\EmbeddedDebugger.App.exe`。终端 HTML、JavaScript、样式及其许可文件已嵌入程序，运行时会解包到用户缓存，无需在 EXE 旁放置 `Assets` 目录。发布配置关闭裁剪以兼容 WPF、WebView2 和 SSH。

目标机器已安装 .NET 8 Desktop Runtime 时，也可生成体积更小的单 EXE：

```powershell
dotnet publish .\src\EmbeddedDebugger.App\EmbeddedDebugger.App.csproj -c Release -p:PublishProfile=Compact
```

精简版输出至 `artifacts\publish\win-x64\EmbeddedDebugger.App.exe`。开发编译使用 `dotnet build`，正式单 EXE 交付使用 `dotnet publish`。

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
