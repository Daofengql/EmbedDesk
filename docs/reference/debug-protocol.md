# PID / 摄像头公共帧

本文记录 **EmbedDesk 当前编码器与解析器实际实现**，供固件联调用。使用步骤见[PID 与摄像头指南](../guide/pid-camera.md)，具体命令见 [PID 协议](pid-protocol.md)与[摄像头协议](camera-protocol.md)。

## 传输与分帧

同一种工具协议可运行在串口、TCP 客户端 / 服务端、UDP 客户端 / 服务端上，网络角色不改变帧结构。TCP 和串口是连续字节流，接收器允许一帧分多次到达，也允许一次读取包含多帧，并扫描帧头恢复同步。

UDP 工具客户端只接收指定目标，服务端锁定首个发送端。数据按到达顺序进入同一解析器，**没有图像 UDP 分片序号、乱序恢复或重传协议**。大图优先使用 TCP 或串口；不要把 TCP 的读取块大小当作帧长度。UDP 下自行拆成多报文不能保证跨丢包、乱序恢复。

JPEG 连续流是摄像头的另一种模式，不使用本页帧头或 CRC，见[连续流说明](camera-protocol.md#jpeg-连续流)。

## 字节布局

所有偏移从 `0` 开始，`N` 是负载字节数，`LE` 表示低字节在前。整包长度包含帧头、长度字段、命令、负载和尾部校验。

| 偏移 | 长度 | 字段 | 编码 |
| --- | --- | --- | --- |
| 0 | 4 | 固定帧头 | `53 5A 48 59`，ASCII `SZHY` |
| 4 | 1 | 地址 | PID 为 `01`–`05`；摄像头为 `00` / `01` |
| 5 | 4 | 整包长度 L | UInt32 LE |
| 9 | 1 | 命令 | 见各工具命令表，文中命令码均为十六进制 |
| 10 | N | 负载 | 无负载命令时 N=0 |
| 10+N | PID 1 / 摄像头 2 | 校验 | PID 为 Sum8；摄像头为 CRC-16 LE |

PID：`L = 11 + N`，解析器接受总长 11–256 字节；具体命令仍严格检查负载长度。摄像头：`L = 12 + N`，总长不超过 8 MiB（8,388,608 字节），因此单包图像负载最多为 8 MiB−12。

## PID 累加校验

从帧头第一个字节开始，累加到负载末尾，取低八位，**不包含校验字节本身**。空负载也需要校验字节。

```c
#include <stddef.h>
#include <stdint.h>

uint8_t pid_sum8(const uint8_t *data, size_t count) {
    uint8_t sum = 0;
    for (size_t i = 0; i < count; ++i) sum = (uint8_t)(sum + data[i]);
    return sum;
}
/* frame[L - 1] = pid_sum8(frame, L - 1); */
```

例如 CH1 上报运行状态：

```text
53 5A 48 59 01 0B 00 00 00 04 5E
```

`0B 00 00 00` 是总长 11，命令 `04` 无负载，前 10 字节之和的低八位是 `5E`。

## 摄像头 CRC-16

当前实现为初值 `FFFF`、反射多项式 `A001`、最终异或 `0000` 的 CRC-16（常称 CRC-16/MODBUS）。计算范围从帧头到负载末尾，排除 CRC 自身；线上先发 CRC 低字节。

```c
uint16_t camera_crc16(const uint8_t *data, size_t count) {
    uint16_t crc = 0xFFFF;
    for (size_t i = 0; i < count; ++i) {
        crc ^= data[i];
        for (unsigned bit = 0; bit < 8; ++bit)
            crc = (uint16_t)((crc >> 1) ^ ((crc & 1) ? 0xA001 : 0));
    }
    return crc;
}
/* uint16_t crc = camera_crc16(frame, L - 2);
   frame[L - 2] = (uint8_t)crc;
   frame[L - 1] = (uint8_t)(crc >> 8); */
```

核对向量：ASCII `123456789` → `0x4B37`；作为线上的校验值应写 `37 4B`。这只是 CRC 算法测试向量，不是一条完整摄像头报文。

## 错误与兼容范围

长度越界、校验不符、未知地址或命令、负载长度错误都会被拒绝或忽略，不会自动推断另一种固件协议。摄像头关闭 CRC 检查后仍要求保留两字节帧尾。缓冲和队列有上限，不支持无限排队。

实现依据公开[图像传输文档](https://www.firebbs.cn/forum.php?mod=viewthread&tid=29788)（2020-06-08，v0.0.1）与 [PID 通信文档](https://www.firebbs.cn/forum.php?mod=viewthread&tid=29923)（2020-06-30，v0.0.1）独立编写；没有移植其工具源码或界面资源。图像原文的多项式 / 字节序存在歧义，本页明确记录当前选择，不代表已经和所有同名工具或固件互通。

源码入口：[DebugToolProtocol.cs](../../src/EmbedDesk.Core/Models/DebugToolProtocol.cs)。已有自动检查覆盖分包 / 合包、非法长度、校验与 JPEG 边界，尚不能替代真实开发板互通验证。
