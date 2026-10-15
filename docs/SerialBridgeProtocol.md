# 旧 EDBG 串口桥接协议

本文描述保留用于兼容和回归的旧手动 TCP 桥接 EDBG v1。当前正常产品入口改为连接中心统一设备授权、自动直连 / 中继回退；它使用 P-256 / AES-GCM 安全帧承载 JSON 服务 RPC，8 KiB 数据块，并传递 DTR / RTS。新旧协议不能直接互通，不能把 EDBG 的 FNV 校验视为身份认证或加密。新协议、资源预留和边界见 [设备连接说明](DeviceConnections.md)。

## 帧格式

旧桥接连接为 TCP 字节流。同一链路按 `ushort` 通道号复用串口，每个帧采用大端序：

| 偏移 | 长度 | 字段 |
|---:|---:|---|
| 0 | 4 | Magic `0x45444247`（ASCII `EDBG`） |
| 4 | 1 | Version，目前为 `1` |
| 5 | 1 | 帧类型：`Data=0`、`Open=1`、`Close=2`、`Heartbeat=4`、`Error=8`、`OpenAcknowledged=16` |
| 6 | 2 | ChannelId |
| 8 | 4 | PayloadLength |
| 12 | 4 | Payload 的 FNV-1a 32 位校验 |
| 16 | N | Payload |

FNV-1a 初值为 `2166136261`，逐字节异或后乘 `16777619`，保留低 32 位。它只检测载荷损坏，不提供身份、保密性或抗篡改保证。运行时读取器只接受表中单个已定义类型，不能组合多个标志位。

## 打开与关闭

1. 服务端用 `Open` 帧发布各通道允许映射的 UTF-8 COM 名称，此时不打开设备。
2. 客户端请求打开时，向该通道发送 JSON `Open`，包含 `PortName`、`BaudRate`、`DataBits`、`Parity`、`StopBits`、`Handshake`、`DtrEnable` 和 `RtsEnable`；枚举使用数值编码，新增控制线字段在缺省时为 `false`。
3. 服务端检查通道和 COM 匹配、参数有效且未重复打开，再调用串口服务；成功才返回空载荷 `OpenAcknowledged`，失败返回 UTF-8 `Error` 并释放本次通道资源。
4. 实际数据使用 `Data`。未打开通道收到数据返回错误；旧协议修改参数需先关闭，再重新打开。
5. 客户端 `Close` 或连接断开后，服务端取消该通道回调和发送并释放实际 COM。

旧服务端每次接受新客户端前先等待旧连接清理完成，不承诺多客户端同时使用同一映射目录。

## 分块与资源边界

正常发送将长数据切成**最多 32 KiB** 的 `Data` 帧。运行时 `MultiplexFrameCodec.ReadAsync` 先读取 16 字节头并验证 magic、版本、类型和长度，再按长度精确读取载荷，因此支持 TCP 拆包 / 粘包，不累计无限字节流。

接收数据帧上限 4 MiB，非数据控制帧上限 4 KiB，负长度、超限、非法头或校验失败终止连接。旧服务端发送队列最多 128 项，数据按 32 KiB 分块时最多约 4 MiB 排队，并通过可取消的等待施加背压。通用 `TryDecode` 增量接口的输入缓冲由调用方持有；当前桥接收发循环使用 `ReadAsync` 精确读取。

## 实现与检查

- [MultiplexFrameCodec.cs](../src/EmbedDesk.Protocols/MultiplexFrameCodec.cs)：帧读写和校验。
- [SerialBridgeControlCodec.cs](../src/EmbedDesk.Protocols/SerialBridgeControlCodec.cs)：参数 JSON。
- [SerialBridgeConnection.cs](../src/EmbedDesk.Infrastructure/Bridging/SerialBridgeConnection.cs)、[RemoteSerialClient.cs](../src/EmbedDesk.Infrastructure/Bridging/RemoteSerialClient.cs)：旧桥接生命周期。

核心编解码测试及 `--serial-bridge-check` 验证兼容行为，调用方式见[构建与验证](development/index.md)。当前连接中心的授权、加密、软件预留和系统虚拟串口应以对应专题为准。
