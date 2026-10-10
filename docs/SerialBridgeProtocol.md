# EDBG Serial Bridge Protocol

默认桥接连接为 TCP 字节流。每个帧采用大端序：

| 偏移 | 长度 | 字段 |
|---:|---:|---|
| 0 | 4 | Magic `0x45444247`（ASCII `EDBG`） |
| 4 | 1 | Version，目前为 `1` |
| 5 | 1 | Flags：`Data=0`、`Open=1`、`Close=2`、`Heartbeat=4`、`Error=8` |
| 6 | 2 | ChannelId |
| 8 | 4 | PayloadLength |
| 12 | 4 | FNV-1a 32 位校验 |
| 16 | N | Payload |

同一 TCP 链路可以承载多个串口。双方先发送 `Open` 帧声明通道，数据使用 `Data` 帧传输；断开时可发送 `Close` 帧。接收方应支持粘包、拆包和连续多帧解析，并在长度超过 4 MiB 或校验失败时丢弃连接。
