# Embedded Debugger

面向嵌入式联调的 Windows 桌面工作台，集成串口调试、SSH / RDP / VNC 远程管理和网络调试。使用 .NET 8、WPF、WebView2 与 xterm.js，提供统一的暗色界面。

## 功能

- **串口调试**：设备扫描与描述、波特率、数据位 / 校验位 / 停止位和流控设置；分开的收发区与 Shell 模式；HEX 收发、循环发送和队列发送。支持多标签同时连接不同串口，各标签独立保留配置、记录、Shell 和发送任务；切换不停止后台收发，可拖动排序、双击名称重命名，关闭标签需确认。
- **网络串口服务**：在独立窗口配置客户端 / 服务端和多路串口映射；服务端只发布可映射的 COM，不提前打开设备，客户端打开远程串口标签时才下发波特率、数据位、校验、停止位和流控，服务端按客户端参数打开；远端串口可直接出现在调试列表中。数据使用有界分帧传输，避免长报文撑爆缓冲区。创建系统级虚拟 COM 口需另行配置外部驱动及映射工具。
- **本地与 SSH 终端**：本地 PowerShell / CMD、SSH 密码或私钥登录、密钥管理、多标签会话、连接握手日志和主机系统图标。终端支持彩色输出、TUI、复制粘贴、字号调整和独立全屏，窗口及字号变化会同步到 PTY。
- **RDP / VNC 桌面**：在远程会话列表中新增连接，双击后直接嵌入现有标签页，支持切换、排序、重连、关闭确认和全屏。RDP 使用 Windows 原生 MSTSC ActiveX 控件，支持 NLA、系统证书提示、剪贴板重定向和窗口尺寸同步；旧版服务端回退为画面缩放。VNC 支持标准密码认证、桌面显示、键鼠输入和发送 Latin-1 剪贴板文本。
- **文件工作区**：底部本地 / 远程目录、上传下载和右键操作；面板默认收起，可拖出为独立窗口并合并回来。小文件可双击下载到缓存，在标签页预览图片或编辑文本并保存到远端；当前默认预览上限为 5 MiB。
- **远端状态**：Linux CPU、内存、负载、网络计数和运行时间；CPU、内存和负载以进度条显示。
- **网络质量**：SSH / Telnet、RDP / VNC 和 TCP 客户端在连接成功后，每 10 秒通过 TCPing 测量目标服务端口的握手耗时；状态栏显示延迟和信号条，悬停查看最近 20 次探测的平均值、峰值、抖动和未成功比例。右键可切换 ICMP Ping 或关闭探测。UDP 使用 ICMP；TCP 服务端仅在有一个对端时探测该主机，UDP 服务端跟随最近对端。探测失败不改变会话连接状态。
- **网络调试**：统一的暗色工作区，可收起和调整宽度的会话列表、独立收发区、常驻发送侧栏和流量计数。支持 TCP / UDP / HTTP 客户端和服务端、MQTT 客户端、HEX、循环发送、发送队列、自动重连及 HTTP 接收片段诊断；发送内容和任务按会话独立保存于内存。服务器会话可选择全部客户端或指定对端发送。
- **嵌入式工具**：按芯片烧录、存储介质、数据与调试辅助集中分类。ESP、STM32、STC89 与 CH347T 均已连接实际协议实现，日志入口支持原始字节录制与定时回放；支持型号和边界见下表。

网络工具中的 **DNS 查询**使用内置 UDP / TCP / DoH 客户端，不依赖 `nslookup` 或其他外部程序。可指定 DNS 服务器（IPv4 / IPv6）、端口、记录类型、IN / CH / HS 查询类、递归、超时及重试；支持 A、AAAA、CNAME、MX、TXT、NS、SOA、PTR、SRV、CAA、DNAME、NAPTR、ANY 和 `TYPE数字`（不支持 AXFR / IXFR 区域传送）。UDP 截断时自动转 TCP；结果保留 TTL、应答 / 权威 / 附加区段和响应码，可复制完整响应。自动模式对域名查询 A，对 IP 地址查询 PTR。 DoH 使用 RFC 8484 HTTPS POST，支持阿里、腾讯、Cloudflare、Google 预设和自定义 HTTPS URL，正常验证 HTTPS 证书；查询内容通过加密连接发送，DoH 服务域名仍由系统解析。

网络工具按「地址与证书」「连通与性能」「发现与 NAT」分类，网络工具页不混入固件和介质操作；新增工具均为 .NET 内部实现，不调用外部可执行文件：

- **CRC / 校验和**：CRC-16/MODBUS、CRC-16/CCITT-FALSE、CRC-32/ISO-HDLC、SUM-8、XOR-8、SHA-256、MD5；显示算法参数与 CRC 低字节在前的发送顺序。支持 HEX、文本、Base64 或原始文件（最多 16 MiB）。
- **HEX / 文本编解码**：HEX、Base64、UTF-8、ASCII、UTF-16 LE 互转，严格报告无效输入；可读取最多 1 MiB 的文件，不自动添加 BOM 或换行。
- **端口扫描**：扫描单个主机的 TCP 端口列表 / 范围，IPv4 / IPv6、超时、并发与停止控制；列表和复制结果只包含开放端口及连接耗时，摘要保留扫描总数与开放数量，每次最多 1024 个端口。
- **TLS 证书检查**：连接地址与 SNI 可分别设置，查看有效期、SAN、证书链、名称匹配、指纹、协议和 PEM；可选联网吊销检查。检查模式展示无效证书并标明原因，不发送业务数据；支持直接 TLS，不支持 STARTTLS。
- **iperf3 吞吐测试**：按 ESnet iperf 3.17.1 的 BSD 许可移植 TCP 协议、独立定时区间统计和测试结束逻辑，使用 .NET 内置实现。支持客户端 / 持续监听服务端、正向 / 反向、IPv4 / IPv6、即时速率、两端统计和取消；数据停滞时仍显示速率，反向统计排除结束后的数据和控制消息等待时间。无需外部 EXE。范围为单流 `-P 1`、无预热 `-O 0`、1–3600 秒按时长不限速测试；不支持 UDP / SCTP、多流、认证、限速、CPU 或 TCP 重传统计。已通过独立报文 fixture 和本机正反向测试，标准外部服务端互通仍待验证，见 [协议说明](docs/third-party/iperf3-protocol.md)。

## 烧录与录制能力

| 工具 | 已实现 | 兼容范围与限制 |
| --- | --- | --- |
| ESP | 自动 BOOT/RESET、型号与 JEDEC 容量检测、多 BIN 地址烧录、MD5 / 回读校验、范围擦除、Flash 备份、ESP-IDF 分区表与 CSV、分区备份、串口监视器 | ESP8266、ESP32、S2/S3/C2/C3/C6/H2，标准 UART、256 KiB–16 MiB SPI Flash；纯 ROM 读取较慢；安全启动或 Flash 加密时禁止普通擦写；分区是原始备份，未实现文件系统浏览 / NVS 键值编辑 |
| STM32 | AN3155 识别、BIN / HEX / S-Record 分析、全片擦除、写入后逐块回读、独立校验、备份、GO 执行 | 8E1 系统 USART Bootloader、主 Flash 地址 0x08000000–0x081FFFFF；容量由用户按芯片填写，使用目标上报的命令能力；不改读保护与选项字节 |
| C51 / STC89 | 上电握手、MCU ID 识别、动态波特率、代码区擦除、分块写入、设备校验和确认、原选项保持、运行固件 | 仅原始 STC89 白名单型号；不通用于 STC8 / STC15 / STC12 / STC89A 或其他厂商 C51；旧协议无通用 Flash 回读，设备块校验不等同于回读 |
| CH347T | Windows 原生 HID 通信、JEDEC 识别、NOR / NAND 备份、范围擦除、写入及回读；NAND 原始页+OOB、ECC 与坏块预检 | HID 1A86:55DC / MI_01；W25Q16/32/64/128（EF40xx）和 W25N01GV；保持物理块地址，含坏块的写入范围被拒绝，不做通用 UBI / 坏块重映射。详见 [协议说明](docs/ch347-spi-support.md) |
| SD / USB | ISO / IMG / BIN 原始写入、SHA-256 回读；FAT32、磁盘身份与卷锁检查、确认 / 取消 / 关闭清理、阶段进度 | 原始镜像写盘，不转换 Windows 安装 ISO；FAT32 支持 256 MiB–2 TiB、512 B 逻辑扇区；取消不能回滚已写数据 |
| 日志与回放 | 串口 / 网络 RX+TX 原始录制、历史导出、完整性校验、0.25–8× 定时回放；确认后可向选定串口重发 TX | `.edbgtrace`，64 MiB / 100,000 条上限，磁盘跟不上时显式停止；RX 不重发；历史导出受会话 4 MiB / 1,000 条缓存限制；网络录制可预览回放，重发目标为串口 |

烧录工具支持取消和擦写二次确认，未知型号或越界范围不会开始擦写。协议测试使用模拟设备、固定报文向量与文件数据流，本轮没有对实际开发板、CH347T 或物理磁盘执行擦写；硬件互操作仍需实机验收。系统级虚拟 COM 仍需独立驱动，应用内远程串口不受此限制。

工具回归测试（不会擦写物理硬件）：

```powershell
dotnet run --project tests/EmbeddedDebugger.App.SmokeTests -- --firmware-check
dotnet run --project tests/EmbeddedDebugger.App.SmokeTests -- --recording-media-check
dotnet run --project tests/EmbeddedDebugger.App.SmokeTests -- --iperf3-check
```

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

Release 将完整的 BouncyCastle 程序集压缩存放，首次使用 SSH / VNC 时在内存中解压加载，不删减任何算法。精简 EXE 大小随功能版本变化；压缩只减少分发体积。依赖版本不变时，增量构建会复用压缩结果。排查依赖加载时可追加 `-p:PackManagedDependencies=false` 使用常规程序集。SDK 的 `EnableCompressionInSingleFile` 在精简版中仍保持关闭。

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

DNS 本机回环检查覆盖记录解码、指定服务器与端口、UDP 转 TCP、IPv6、超时取消、损坏报文和查询窗口；不依赖公共 DNS 节点：

```powershell
dotnet run --project .\tests\EmbeddedDebugger.App.SmokeTests\EmbeddedDebugger.App.SmokeTests.csproj -- --dns-query-check
```

## 性能与资源限制

接收区按批次增量更新，隐藏时暂停文本格式化；本地和远程终端合并输出后交给 WebView2。每个会话最多保留 1000 条、合计 4 MiB 原始历史，累计 RX / TX 不受截断影响；文本接收区最多保留约 100 万字符。VNC 在后台解码并复用帧缓冲，只向界面更新变化的区域。性能对比和复现方法见 [性能优化记录](docs/performance.md)。

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

DoH 传输及新增工具检查（包含本机 TCP / TLS 服务、CRC 标准向量和窗口截图）：

```powershell
dotnet run --project tests/EmbeddedDebugger.App.SmokeTests -- --dns-query-check
dotnet run --project tests/EmbeddedDebugger.App.SmokeTests -- --utility-tools-check
# 可选：实际查询四个公共 DoH 服务
dotnet run --project tests/EmbeddedDebugger.App.SmokeTests -- --live-doh-check
```
