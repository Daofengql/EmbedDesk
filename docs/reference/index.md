# 技术参考

按任务操作请先看[使用指南](../guide/index.md)。这里记录当前实现的结构、协议、限制与来源，保留既有文档路径，方便源码和历史链接继续访问。

## 设备与通信协议

| 文档 | 内容 |
| --- | --- |
| [PID / 摄像头公共帧](debug-protocol.md) | 帧头、整包长度、字节序、校验算法与分帧 |
| [PID 协议](pid-protocol.md) | 五通道、上报 / 下发命令、数值编码与完整报文 |
| [摄像头协议](camera-protocol.md) | 格式与图像、确认、寄存器读写、JPEG 连续流 |
| [设备连接与共享](../DeviceConnections.md) | 身份、授权、数据路径、限流与安全边界 |
| [局域网文件](../LanFiles.md) | 发现、分块传输、断点与路径限制 |
| [系统虚拟串口](../VirtualSerialPorts.md) | com0com 前提与实验性范围 |
| [iperf3](../third-party/iperf3-protocol.md) | 标准协议子集、来源与历史对端验证 |

## 实现与支持范围

| 文档 | 内容 |
| --- | --- |
| [应用架构](../Architecture.md) | 分层、资源生命周期、配置与代码入口 |
| [调试助手设计](../DebugAssistantDesign.md) | 一级标签、内部表单、适配器与接收上限 |
| [数据与调试辅助](../DebugTools.md) | PID 滤波、图像解码、二进制编辑边界 |
| [CH347T](../ch347-spi-support.md) | HID 实现和明确支持的芯片表 |
| [证书与密码](../security-crypto.md) | TLS、证书与算法支持范围 |
| [性能与资源](../performance.md) | 当前缓存策略及历史局部测量 |

## 兼容、来源与待验收事项

[旧 EDBG 帧协议](../SerialBridgeProtocol.md)只用于旧实现兼容回归，与当前连接中心协议不互通。[com0com 来源](../third-party/com0com.md)和[许可证索引](licenses.md)说明第三方边界。

[产品方向](../ProductRoadmap.md)只列候选工作和未完成验收。历史验证不是永久兼容承诺，本机回环、协议模拟和截图不能代替真实设备或公网实测。
