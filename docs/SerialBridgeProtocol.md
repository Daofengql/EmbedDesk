# EDBG Serial Bridge Protocol

默认桥接连接为 TCP 字节流。每个帧采用大端序：

| 偏移 | 长度 | 字段 |
|---:|---:|---|
| 0 | 4 | Magic `0x45444247`（ASCII `EDBG`） |
| 4 | 1 | Version，目前为 `1` |
| 5 | 1 | Flags：`Data=0`、`Open=1`、`Close=2`、`Heartbeat=4`、`Error=8`、`OpenAcknowledged=16` |
| 6 | 2 | ChannelId |
| 8 | 4 | PayloadLength |
| 12 | 4 | FNV-1a 32 位校验 |
| 16 | N | Payload |

同一 TCP 链路可以承载多个串口。服务端启动时只用 `Open` 帧发布允许映射的 COM 名称，不打开设备；客户端打开远程串口时发送 JSON `Open` 帧，内容为 `PortName`、`BaudRate`、`DataBits`、`Parity`、`StopBits` 和 `Handshake`。服务端校验该 COM 确实已映射后，按客户端参数打开设备并返回 `OpenAcknowledged`；失败时返回 `Error`。客户端关闭远程串口发送 `Close`，服务端随即关闭对应 COM。数据使用 `Data` 帧传输，应用层将长报文切成 32 KiB 以下的帧；接收方应支持粘包、拆包和连续多帧解析，并在单帧长度超过 4 MiB、累计缓冲超过 8 MiB 或校验失败时拒绝连接。
