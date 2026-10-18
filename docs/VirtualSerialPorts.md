# 可选的系统虚拟串口

远程串口有两种使用方式，在连接中心的串口映射列点击添加，先选择对端已发布串口，再选择：

- **在本软件内使用**：默认方式，进入串口调试标签，设置参数后连接；不需要虚拟串口驱动。
- **映射为系统虚拟 COM（实验性）**：桥接已安装的 com0com 串口对。其他 Windows 软件打开串口对的公共 COM，EmbedDesk 独占另一端，通过现有设备会话访问远端串口。不会额外建立公网连接。

同一远端硬件同时只能由一个使用方打开。应用内使用不创建系统设备；选择虚拟 COM 也不会自动安装驱动、修改签名策略或删除现有系统设备。

## 准备驱动与串口对

先从 [com0com 项目](https://sourceforge.net/projects/com0com/) 取得适合当前系统、能正常加载的驱动。旧版本的签名可能不被新版 Windows 接受；本功能不提供绕过系统签名或安全启动的操作。安装是独立的系统维护步骤，不随单 EXE 分发驱动。

在驱动提供的 Setup Command Prompt 中创建一个专用串口对，例如：

```text
setupc.exe install PortName=COM# PortName=COM#,dcd=ropen,cts=rrts,dsr=rdtr
setupc.exe list
```

`COM#` 由驱动选择空闲编号。上例第一端供其他软件使用，第二端由 EmbedDesk 打开；第二端的 `dcd=ropen` 表示对端打开状态，`cts=rrts` / `dsr=rdtr` 分别表示对端 RTS / DTR。不要修改正在使用的串口对。对已有专用对配置时，使用 `list` 输出中的实际 CNCA / CNCB 名称执行 `change`。

程序通过 Windows 注册表中的活动设备映射检测 com0com 对，检查桥接端引脚配置，并在打开时核对驱动扩展能力。不会根据用户输入对任意物理 COM 执行驱动探测。端口被占用或配置不完整会显示真实错误；没有驱动时仍可选择应用内使用。

## 使用与生命周期

1. 在连接中心连接对方设备，在串口映射列点击添加。
2. 选择对端已发布的串口，再选择系统虚拟 COM 模式和本机专用串口对。
3. 保存并启用映射。此时只打开本机桥接端，不打开远端硬件。
4. 用其他软件打开列表左侧的公共 COM。映射读取其波特率、数据位、校验、停止位和 DTR / RTS，远端成功打开后开始转发。
5. 外部软件关闭 COM 后释放远端硬件，映射继续等待下一次打开。停用、删除、设备断线或真正退出应用会关闭桥接端；托盘隐藏继续维持连接。

规则保存公共 COM、桥接端和目标服务标识，重启后保持未启用，需要重新连接设备并手动启用。原有 TCP / UDP 规则保持原格式语义。

## 当前实现边界

- 使用原生 Win32 串口 API，无需运行 hub4com、com2tcp 或 setupc。系统仍必须安装可正常加载的 com0com 驱动并预先创建串口对。
- 波特率、格式和 DTR / RTS 变化通过原有加密设备通道确认后应用，不为参数变化关闭并重开远端硬件；两端需要使用支持 `serial-configure` 的新版 EmbedDesk。
- 首版远端流控为关闭，尚不转发硬件/软件流控、Break、远端 CTS / DSR / DCD / RI、串口错误计数、清空缓冲和全部 Windows 串口 IOCTL。不能据此承诺烧录器或所有串口软件完全兼容。
- 控制状态通过约 20 ms 的轮询快照读取，受驱动和网络等待影响；快速开关或瞬时控制线脉冲可能无法重现，也不保证 Windows IOCTL 与数据的精确时序及 Flush / drain 语义，不适合依赖严格复位时序的操作。
- 外部软件的 `CreateFile` 由本机驱动处理，不能把远端占用错误同步变成本机打开调用失败。远端打开或配置失败会在连接方的映射状态中显示，拉低本机桥接端 DTR / RTS，释放映射；需处理原因后手动重新启用并重新打开外部 COM。
- 每次外部 COM 打开独立建立有界收发泵，接收队列最多 8 × 8 KiB；队列满时等待接收泵消费，不因短时突发停止映射。关闭或断线先取消生产者与发送额度等待，再等待在途驱动写入完成；重新打开使用新队列，旧回调不能混入。真实驱动写入错误仍会停止映射并显示原因。最多同时启用 16 条虚拟串口映射。现有远端共享资源管理器继续保证实际 COM 的独占。

## 验证

`--device-connections-check` 需先构建 `artifacts/relay-test.exe`，命令见[构建与验证](development/index.md)。以下参数每次单独调用，结果应按实际执行记录；模拟驱动和界面检查不需要安装 com0com。

```powershell
dotnet build tests/EmbedDesk.App.SmokeTests/EmbedDesk.App.SmokeTests.csproj
dotnet run --no-build --project tests/EmbedDesk.App.SmokeTests -- --virtual-serial-check
dotnet run --no-build --project tests/EmbedDesk.App.SmokeTests -- --device-connections-check
dotnet run --no-build --project tests/EmbedDesk.App.SmokeTests -- --connection-center-window-check
```

`--virtual-serial-check` 验证原生快照解码、模拟驱动按需打开、全部 256 字节值收发、在线配置、超过八帧的突发、慢端背压、双向收发、关闭取消与重开隔离。`--serial-flow-check` 使用环境变量 `EMBEDDESK_TEST_RELAY_URL` 指定独立的测试中继，可选 `EMBEDDESK_TEST_RELAY_ADMISSION_KEY`，验证真实设备协议上的持续小包、大突发、双向碎片回显及断线取消；中继可运行于 Linux 虚拟机，无需启动 Windows 中继进程。界面检查覆盖应用内、缺少驱动及已配置串口对的截图。这些检查不替代真实 com0com 驱动和第三方软件互通，更不替代物理串口或开发板测试。
