# 安装与快速开始

这一页从下载开始，用本机 TCP 客户端和服务端完成一次收发，不需要开发板或外部服务器。

## 准备运行环境

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809 或更新版本，x64 |
| .NET | [.NET 8 Desktop Runtime，选择 Windows x64](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 终端组件 | [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) |
| 程序 | 从 [GitHub Releases](https://github.com/Daofengql/EmbedDesk/releases) 下载 `EmbedDesk.App.exe` |

默认发行版不包含 .NET 运行时；安装 SDK 不是运行程序的必要步骤。自带运行时的版本仍需要 WebView2。下载后可用 PowerShell 核对 Release 提供的 SHA-256：

```powershell
Get-FileHash ./EmbedDesk.App.exe -Algorithm SHA256
```

运行 EXE 并确认 Windows 管理员权限提示。终端、图标和许可证已嵌入，不需要额外的 `Assets` 文件夹。

## 发送第一条报文

### 1. 创建服务端

1. 打开「调试助手」，点击「开始第一个调试会话」或标签栏的 `+`。
2. 在「基础通信」中选择「TCP/UDP 服务端」。
3. 协议选择 `TCP`，监听地址填写 `127.0.0.1`，端口填写 `9000`。
4. 点击「创建并启动」，确认会话进入监听状态。

![新建调试会话的分类入口](/images/new-session.png)

图中为测试环境的新建弹窗局部。创建过程在主界面内完成，先选角色，再填协议和地址。

### 2. 创建客户端

1. 再次点击 `+`，选择「TCP/UDP 客户端」。
2. 协议选择 `TCP`，远程地址填写 `127.0.0.1`，端口填写 `9000`；本地绑定保留默认值。
3. 点击「创建并启动」，确认连接成功。
4. 在底部发送区输入 `hello`，以文本方式发送；切换到服务端标签查看接收内容。

![TCP 客户端连接本机 127.0.0.1:9000 的表单](/images/tcp-client.png)

客户端的「远程地址」填服务端地址。本地端口保留 `0`，由系统分配，不需要也填写 `9000`。

### 3. 从服务端回复

在服务端选择已连接的客户端作为发送目标，输入 `ok` 并发送。回到客户端后应能看到回复。两个标签独立运行，切换不影响连接。

如果 `9000` 已占用，换一个未占用端口，并同时修改客户端。编辑连接前先停止该会话。`127.0.0.1` 只供本机测试；跨机连接应填写服务端实际可达地址，并检查监听地址和防火墙。

## 下一步

根据设备接口选择[串口调试](serial.md)或 [TCP / UDP](network.md)；需要解析控制器数据时使用 [PID 与摄像头](pid-camera.md)。仅做本地调试不需要配置连接中心的中继服务器。

离开时可以断开会话、关闭标签，或收起到托盘保留后台连接。点击主窗口关闭按钮时，按提示选择收起、退出或取消。
